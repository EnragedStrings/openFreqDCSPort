using OpenFreq.Common.Satcom;

namespace OpenFreqServer.Satcom;

/// <summary>
/// Chooses which satellite serves a given (client, net) session. <see cref="SatelliteSelectionMode.Assigned"/>
/// (the default) and <see cref="SatelliteSelectionMode.Manual"/> both just return the net's
/// configured satellite id -- never automatically re-picked by margin/distance, per the design
/// brief's explicit correction. Only <see cref="SatelliteSelectionMode.AutoBestVisible"/> ranks
/// candidates, and even then with hysteresis (a minimum margin improvement AND a minimum hold
/// duration before switching), never "closest/best satellite this exact tick".
/// </summary>
public sealed class SatcomSatelliteSelector
{
    private sealed class AutoSelectionState
    {
        public string? SatelliteId;
        public long SelectedAtMs;
    }

    private readonly Dictionary<string, AutoSelectionState> _autoState = new();

    public string? SelectSatellite(string sessionKey, SatcomNetDefinition net,
        IReadOnlyList<SatcomSatelliteDefinition> catalog, SatcomEphemerisService ephemeris,
        SatcomTerminalState terminal, long nowMs)
    {
        return net.SelectionMode switch
        {
            SatelliteSelectionMode.Assigned or SatelliteSelectionMode.Manual => net.AssignedSatelliteId,
            SatelliteSelectionMode.AutoBestVisible => SelectAutoBestVisible(sessionKey, net, catalog, ephemeris, terminal, nowMs),
            _ => null
        };
    }

    private string? SelectAutoBestVisible(string sessionKey, SatcomNetDefinition net,
        IReadOnlyList<SatcomSatelliteDefinition> catalog, SatcomEphemerisService ephemeris,
        SatcomTerminalState terminal, long nowMs)
    {
        // Ranking proxy: downlink-leg C/N0 from this terminal's own position. The true combined
        // (uplink+uplink) quality depends on who's transmitting, which isn't known at selection
        // time -- using the downlink leg alone is a reasonable, documented simplification for
        // ranking candidates (SatcomLinkEngine still does the full two-leg evaluation afterward for
        // whichever satellite is actually selected).
        string? bestId = null;
        var bestCn0 = double.NegativeInfinity;
        // Dedicated nets rank against the terminal's own tuned frequency (see
        // SatcomLinkEngine.Evaluate for why); DAMA nets always use the net's fixed downlink.
        var downlinkHz = !net.Waveform.IsDama() && terminal.TunedFrequencyHz is { } tuned ? tuned : net.DownlinkHz;

        foreach (var sat in catalog)
        {
            if (!sat.Enabled || sat.Health <= 0.0) continue;
            var pos = ephemeris.GetPosition(sat.Id);
            if (pos == null) continue;

            var leg = SatcomLinkEngineCore.EvaluateLeg(net, sat, pos.Value, terminal, downlinkHz, isUplinkLeg: false);
            if (!leg.Usable) continue;

            if (leg.Cn0DbHz > bestCn0)
            {
                bestCn0 = leg.Cn0DbHz;
                bestId = sat.Id;
            }
        }

        if (!_autoState.TryGetValue(sessionKey, out var state))
        {
            state = new AutoSelectionState();
            _autoState[sessionKey] = state;
        }

        if (state.SatelliteId == null)
        {
            state.SatelliteId = bestId;
            state.SelectedAtMs = nowMs;
            return bestId;
        }

        if (bestId == null)
            return state.SatelliteId; // nothing visible right now; don't drop a working assignment for a momentary gap

        if (bestId == state.SatelliteId)
            return state.SatelliteId;

        var heldSeconds = Math.Max(0.0, (nowMs - state.SelectedAtMs) / 1000.0);
        if (heldSeconds < net.AutoSelectMinHoldSeconds)
            return state.SatelliteId; // too soon to hand over again

        var currentSat = catalog.FirstOrDefault(s => s.Id == state.SatelliteId);
        var currentPos = currentSat != null ? ephemeris.GetPosition(currentSat.Id) : null;
        if (currentSat != null && currentPos != null)
        {
            var currentLeg = SatcomLinkEngineCore.EvaluateLeg(net, currentSat, currentPos.Value, terminal, net.DownlinkHz, false);
            if (currentLeg.Usable && bestCn0 - currentLeg.Cn0DbHz < net.AutoSelectMarginHysteresisDb)
                return state.SatelliteId; // candidate isn't enough better to justify a handover
        }

        state.SatelliteId = bestId;
        state.SelectedAtMs = nowMs;
        return bestId;
    }

    public void Reset(string sessionKey) => _autoState.Remove(sessionKey);
}
