using OpenFreq.Common.Satcom;

namespace OpenFreqServer.Satcom;

/// <summary>One terminal's DAMA session on one net -- wraps the existing, already-tested
/// <see cref="SatcomDamaStateMachine"/> unchanged; only who drives it (the server) and with what
/// inputs (real link-engine output) changed from the earlier client-local version.</summary>
public sealed class DamaTerminalSession
{
    public required string ClientId;
    public required string NetId;
    public readonly SatcomDamaStateMachine StateMachine = new();
    public bool HasSlot;

    /// <summary>When the control (orderwire) channel became jammed for this terminal, or null when it is clear.</summary>
    public long? OrderwireJammedSinceMs;
}

/// <summary>
/// Single authoritative owner of DAMA network access across all terminals on all nets: it is the
/// network controller, not a peer of the per-terminal state machines it drives. Dedicated
/// (non-DAMA) waveforms skip network access/slot contention entirely -- once the link is up they're
/// always available, matching real point-to-point assigned-channel behavior.
/// </summary>
public sealed class DamaNetworkController
{
    /// <summary>With the orderwire jammed, terminals keep frame sync this many frame periods before dropping to
    /// LostSync (research-02 §3: existing circuits drop at the next frame resync, within 1–2 frames). EST.</summary>
    public const double OrderwireLossFrames = 1.5;

    private readonly Dictionary<string, DamaTerminalSession> _sessions = new();
    private readonly Dictionary<string, IDamaFrameScheduler> _schedulersByNet = new();

    private IDamaFrameScheduler GetOrCreateScheduler(SatcomNetDefinition net)
    {
        if (_schedulersByNet.TryGetValue(net.NetId, out var existing))
            return existing;

        IDamaFrameScheduler scheduler = net.Waveform switch
        {
            SatcomWaveform.Dama5k => new Dama5kHzScheduler(),
            SatcomWaveform.Dama25k => new Dama25kHzScheduler(),
            // Dedicated waveforms don't contend for network capacity -- effectively unlimited.
            _ => new Dama5kHzScheduler(capacityPerFrame: int.MaxValue)
        };
        _schedulersByNet[net.NetId] = scheduler;
        return scheduler;
    }

    /// <summary>Advances one terminal's DAMA session for this tick. <paramref name="link"/> is the
    /// server's authoritative SatcomLinkEngine result for this client/net right now. While
    /// <paramref name="orderwireJammed"/> (DAMA nets only), no channel can be assigned, and after
    /// <see cref="OrderwireLossFrames"/> frames the terminal loses network sync.</summary>
    public DamaState Update(string clientId, SatcomNetDefinition net, bool loginReady, SatcomLinkResult link,
        bool pttPressed, bool receivingCarrier, int priority, long nowMs, bool orderwireJammed = false)
    {
        var key = SatcomLinkEngine.SessionKey(clientId, net.NetId);
        if (!_sessions.TryGetValue(key, out var session))
        {
            session = new DamaTerminalSession { ClientId = clientId, NetId = net.NetId };
            _sessions[key] = session;
        }

        var scheduler = GetOrCreateScheduler(net);
        var effectiveMarginDb = link.LinkAvailable ? link.EbN0Db : -999.0;
        var linkAvailable = link.LinkAvailable;

        orderwireJammed &= net.Waveform.IsDama();
        if (!orderwireJammed)
        {
            session.OrderwireJammedSinceMs = null;
        }
        else
        {
            session.OrderwireJammedSinceMs ??= nowMs;
            // No assignments while the controller can't be heard.
            effectiveMarginDb = Math.Min(effectiveMarginDb, SatcomDamaStateMachine.MinCombinedMarginDbForAssignment - 1.0);
            if (nowMs - session.OrderwireJammedSinceMs.Value >= OrderwireLossFrames * scheduler.FramePeriodSeconds * 1000.0)
                linkAvailable = false; // frame sync lost
        }

        // Capacity gate: only intervene at the moment a request would otherwise be granted purely
        // on link margin (Requesting + PTT held + margin sufficient) and no slot is held yet.
        if (session.StateMachine.State == DamaState.Requesting && pttPressed && !session.HasSlot && !orderwireJammed &&
            effectiveMarginDb >= SatcomDamaStateMachine.MinCombinedMarginDbForAssignment)
        {
            var slot = scheduler.TryAssignSlot(clientId, priority, nowMs, out var evictedClientId);
            if (slot == null)
            {
                // Network at capacity, nobody lower-priority to preempt -- deny regardless of RF margin.
                effectiveMarginDb = SatcomDamaStateMachine.MinCombinedMarginDbForAssignment - 1.0;
            }
            else
            {
                session.HasSlot = true;
                if (evictedClientId != null && _sessions.TryGetValue(
                        SatcomLinkEngine.SessionKey(evictedClientId, net.NetId), out var evictedSession))
                {
                    evictedSession.HasSlot = false;
                    evictedSession.StateMachine.Reset(); // preempted terminal must re-request from scratch
                }
            }
        }

        var prevState = session.StateMachine.State;
        var newState = session.StateMachine.Update(loginReady, linkAvailable, effectiveMarginDb, pttPressed,
            receivingCarrier, nowMs);

        var stillHoldsChannel = newState is DamaState.Assigned or DamaState.Tx;
        if (session.HasSlot && !stillHoldsChannel && prevState is DamaState.Assigned or DamaState.Tx)
        {
            scheduler.ReleaseSlot(clientId);
            session.HasSlot = false;
        }

        return newState;
    }

    public long? CurrentFrameIndex(SatcomNetDefinition net, long nowMs) =>
        _schedulersByNet.TryGetValue(net.NetId, out var scheduler) ? scheduler.CurrentFrameIndex(nowMs) : null;

    public int? CurrentSlot(string clientId, string netId) =>
        _schedulersByNet.TryGetValue(netId, out var scheduler)
            ? scheduler.ActiveHolders.FirstOrDefault(h => h.ClientId == clientId)?.SlotIndex
            : null;

    public void RemoveClient(string clientId, string netId)
    {
        var key = SatcomLinkEngine.SessionKey(clientId, netId);
        if (_sessions.Remove(key, out var session) && session.HasSlot && _schedulersByNet.TryGetValue(netId, out var scheduler))
            scheduler.ReleaseSlot(clientId);
    }
}
