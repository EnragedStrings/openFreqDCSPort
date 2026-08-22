using System;

namespace OpenFreq.Common.Satcom;

/// <summary>
/// UHF MILSATCOM DAMA (Demand Assigned Multiple Access) network-access states, per-terminal.
/// PUBLIC STANDARD concept (MIL-STD-188-185 defines the real DAMA control-system architecture --
/// channel controllers, demand assignment, network access), but this state machine is a SIMULATION
/// APPROXIMATION behavioral abstraction, not a reproduction of any classified network-control
/// signaling/procedure.
///
/// Owned and run SERVER-SIDE (one instance per client/net session) by DamaNetworkController, fed
/// the server's own authoritatively-computed linkAvailable/combinedMarginDb rather than a client's
/// self-only geometry -- the state machine itself is unchanged from the client-local version this
/// replaces (it was already correct), only who drives it and with what inputs changed. The client
/// only ever displays the DamaState a SatcomLinkStateMessage pushes down.
/// </summary>
public enum DamaState
{
    /// <summary>ARC-210 not logged into SATCOM (acquisition/login not Ready). No network access
    /// attempted.</summary>
    Offline,

    /// <summary>Login is Ready; looking for the assigned satellite/channel. Stays here (retrying)
    /// for as long as the satellite isn't actually visible/usable.</summary>
    Searching,

    /// <summary>Satellite found; establishing network timing/sync with the DAMA controller.</summary>
    Synchronizing,

    /// <summary>On the network, idle -- able to request a channel (TX) or receive one assigned to
    /// someone else (RX).</summary>
    Ready,

    /// <summary>PTT pressed; a channel/timeslot has been requested from the controller but not
    /// yet confirmed.</summary>
    Requesting,

    /// <summary>Channel granted; about to key up.</summary>
    Assigned,

    /// <summary>Actively transmitting on the assigned channel.</summary>
    Tx,

    /// <summary>Actively receiving someone else's transmission.</summary>
    Rx,

    /// <summary>The network denied the channel request (e.g. link margin too poor, or the network
    /// being at capacity). Returns to Ready to allow another attempt rather than getting stuck.</summary>
    ServiceDenied,

    /// <summary>Was on the network (Ready/Requesting/Assigned/Tx/Rx) but the satellite link
    /// dropped out from under it (e.g. severe airframe masking). Falls back to Searching once the
    /// link recovers, rather than requiring a full new login.</summary>
    LostSync
}

/// <summary>
/// Drives <see cref="DamaState"/> from three externally-observed signals: whether the ARC-210's
/// own login/acquisition is Ready, whether the satellite link is currently usable, and whether the
/// local operator is requesting to transmit (PTT). Timestamp-based -- no sleep/blocking,
/// deterministic, unit-testable with an injected "now".
/// </summary>
public sealed class SatcomDamaStateMachine
{
    public const double SearchingSeconds = 1.0;
    public const double SynchronizingSeconds = 1.5;

    /// <summary>SIMULATION APPROXIMATION: link margin below this (dB) causes a channel request to
    /// be denied rather than granted -- not a published network-access threshold.</summary>
    public const double MinCombinedMarginDbForAssignment = -3.0;

    public DamaState State { get; private set; } = DamaState.Offline;

    private long? _stateEnteredAtMs;
    private double? _lastCombinedMarginDb;

    /// <summary>
    /// Advance the state machine.
    /// </summary>
    /// <param name="loginReady">SatcomAcquisitionStateMachine.State == Ready for this ARC-210.</param>
    /// <param name="linkAvailable">Whether a satellite is currently visible/usable (e.g.
    /// SatcomLinkResult.LinkAvailable for the assigned/best candidate satellite).</param>
    /// <param name="combinedMarginDb">Current combined link margin (dB, e.g. EbN0 - threshold) --
    /// used only to decide REQUESTING -&gt; ASSIGNED vs SERVICE_DENIED. Ignored (treated as
    /// unavailable) when <paramref name="linkAvailable"/> is false.</param>
    /// <param name="pttPressed">Local operator is holding PTT for this radio.</param>
    /// <param name="receivingCarrier">A remote transmission is currently being received on this
    /// channel (independent of pttPressed -- both could be true only in a collision, which this
    /// state machine doesn't specially arbitrate; TX takes priority below).</param>
    /// <param name="nowMs">Monotonic time, milliseconds.</param>
    public DamaState Update(bool loginReady, bool linkAvailable, double combinedMarginDb, bool pttPressed,
        bool receivingCarrier, long nowMs)
    {
        _lastCombinedMarginDb = linkAvailable ? combinedMarginDb : null;

        if (!loginReady)
        {
            TransitionTo(DamaState.Offline, nowMs);
            return State;
        }

        // Losing the satellite drops any on-network state straight to LostSync, from anywhere
        // except Offline/Searching (already not relying on it) -- modeled as an interrupt, not
        // something only checked in specific states.
        if (!linkAvailable && State is DamaState.Synchronizing or DamaState.Ready or DamaState.Requesting
                or DamaState.Assigned or DamaState.Tx or DamaState.Rx)
        {
            TransitionTo(DamaState.LostSync, nowMs);
            return State;
        }

        switch (State)
        {
            case DamaState.Offline:
                TransitionTo(DamaState.Searching, nowMs);
                break;

            case DamaState.Searching:
                if (linkAvailable && ElapsedSeconds(nowMs) >= SearchingSeconds)
                    TransitionTo(DamaState.Synchronizing, nowMs);
                break;

            case DamaState.Synchronizing:
                if (ElapsedSeconds(nowMs) >= SynchronizingSeconds)
                    TransitionTo(DamaState.Ready, nowMs);
                break;

            case DamaState.Ready:
                if (pttPressed)
                    TransitionTo(DamaState.Requesting, nowMs);
                else if (receivingCarrier)
                    TransitionTo(DamaState.Rx, nowMs);
                break;

            case DamaState.Requesting:
                if (!pttPressed)
                {
                    TransitionTo(DamaState.Ready, nowMs); // PTT released before grant -- abandon the request
                }
                else if (combinedMarginDb >= MinCombinedMarginDbForAssignment)
                {
                    TransitionTo(DamaState.Assigned, nowMs);
                }
                else
                {
                    TransitionTo(DamaState.ServiceDenied, nowMs);
                }
                break;

            case DamaState.Assigned:
                TransitionTo(pttPressed ? DamaState.Tx : DamaState.Ready, nowMs);
                break;

            case DamaState.Tx:
                if (!pttPressed)
                    TransitionTo(DamaState.Ready, nowMs);
                break;

            case DamaState.Rx:
                if (pttPressed)
                    TransitionTo(DamaState.Requesting, nowMs);
                else if (!receivingCarrier)
                    TransitionTo(DamaState.Ready, nowMs);
                break;

            case DamaState.ServiceDenied:
                // One-cycle state: the caller observes it, then the next Update call (still
                // Ready-eligible) moves on. Auto-recovers to Ready immediately rather than
                // needing a separate acknowledgement step.
                TransitionTo(DamaState.Ready, nowMs);
                break;

            case DamaState.LostSync:
                if (linkAvailable)
                    TransitionTo(DamaState.Searching, nowMs);
                break;
        }

        return State;
    }

    private double ElapsedSeconds(long nowMs) =>
        Math.Max(0.0, (nowMs - (_stateEnteredAtMs ?? nowMs)) / 1000.0);

    private void TransitionTo(DamaState next, long nowMs)
    {
        if (next == State) return;
        State = next;
        _stateEnteredAtMs = nowMs;
    }

    public void Reset()
    {
        State = DamaState.Offline;
        _stateEnteredAtMs = null;
        _lastCombinedMarginDb = null;
    }
}
