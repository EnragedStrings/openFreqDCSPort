using OpenFreq.Common.Satcom;

namespace OpenFreqServer.Satcom;

/// <summary>
/// Server-authoritative per-transmission link evaluation. For a given net, evaluates the ONE
/// uplink leg (active transmitter -&gt; satellite) once, then the downlink leg (satellite -&gt;
/// receiver) independently for every recipient -- never a single "distance between the two
/// aircraft" shortcut, and receivers never need direct LOS to the transmitter, only each end needs
/// its own leg to the satellite (true bent-pipe relay modeling).
///
/// Sanity-checks each client's self-reported terrain-LOS claim against the satellite actually
/// assigned to it: a client cannot claim clear local terrain LOS toward a satellite that server-side
/// geometry says is below its horizon in the first place (impossible for a legitimate client to
/// have computed) -- a modest but real anti-cheat check given the server has no independent access
/// to DCS terrain itself.
/// </summary>
public sealed class SatcomLinkEngine
{
    public sealed class ClientNetState
    {
        public required string ClientId;
        public required string NetId;
        public SatcomTerminalState Terminal;
        public bool RadioPowered;
        public bool PttPressed;
        public bool TerrainLosClear;
        public long UpdatedAtMs;
    }

    private readonly IReadOnlyList<SatcomSatelliteDefinition> _catalog;
    private readonly SatcomEphemerisService _ephemeris;
    private readonly SatcomSatelliteSelector _selector;

    private readonly Dictionary<string, ClientNetState> _clients = new(); // key: SessionKey(clientId, netId)
    private readonly Dictionary<string, SatcomLinkQualityTracker> _trackers = new();

    public SatcomLinkEngine(IReadOnlyList<SatcomSatelliteDefinition> catalog, SatcomEphemerisService ephemeris,
        SatcomSatelliteSelector selector)
    {
        _catalog = catalog;
        _ephemeris = ephemeris;
        _selector = selector;
    }

    public static string SessionKey(string clientId, string netId) => $"{clientId}:{netId}";

    public void UpdateGeometry(string clientId, string netId, SatcomTerminalState terminal, bool radioPowered,
        bool pttPressed, bool terrainLosClear, long nowMs)
    {
        var key = SessionKey(clientId, netId);
        _clients[key] = new ClientNetState
        {
            ClientId = clientId, NetId = netId, Terminal = terminal, RadioPowered = radioPowered,
            PttPressed = pttPressed, TerrainLosClear = terrainLosClear, UpdatedAtMs = nowMs
        };
    }

    public void RemoveClient(string clientId, string netId)
    {
        var key = SessionKey(clientId, netId);
        _clients.Remove(key);
        _trackers.Remove(key);
        _selector.Reset(key);
    }

    /// <summary>Currently transmitting client on this net, if any and if their last geometry
    /// update is still fresh (avoids using a stale transmitter position after a disconnect).</summary>
    public string? FindActiveTransmitter(string netId, long nowMs, double maxAgeSeconds = 10.0) =>
        _clients.Values
            .Where(c => c.NetId == netId && c.PttPressed && (nowMs - c.UpdatedAtMs) / 1000.0 <= maxAgeSeconds)
            .Select(c => c.ClientId)
            .FirstOrDefault();

    /// <summary>Full authoritative evaluation of the link a given receiver would experience on
    /// this net right now, using the real geometry of whoever is actually transmitting (or the
    /// receiver's own geometry as an idle-state proxy when nobody is), per docs/SATCOM_SIMULATION.md.</summary>
    public SatcomLinkResult Evaluate(string receiverClientId, string netId, SatcomNetDefinition net, long nowMs)
    {
        var rxKey = SessionKey(receiverClientId, netId);
        if (!_clients.TryGetValue(rxKey, out var rxState) || !rxState.RadioPowered)
            return SatcomLinkResult.Unavailable(SatcomAcquisitionFailureReason.RadioNotReady, "radio not powered");

        var satelliteId = _selector.SelectSatellite(rxKey, net, _catalog, _ephemeris, rxState.Terminal, nowMs);
        if (satelliteId == null)
            return SatcomLinkResult.Unavailable(SatcomAcquisitionFailureReason.NoSatAssignment, "no satellite assigned for this net");

        var satellite = _catalog.FirstOrDefault(s => s.Id == satelliteId);
        if (satellite == null)
            return SatcomLinkResult.Unavailable(SatcomAcquisitionFailureReason.NoSatAssignment, $"unknown satellite '{satelliteId}'");

        var satPos = _ephemeris.GetPosition(satelliteId);
        if (satPos == null)
            return SatcomLinkResult.Unavailable(SatcomAcquisitionFailureReason.EphemerisStale, "no ephemeris available yet");

        var txClientId = FindActiveTransmitter(netId, nowMs);
        var txState = txClientId != null ? _clients.GetValueOrDefault(SessionKey(txClientId, netId)) : null;
        var txTerminal = txState?.Terminal ?? rxState.Terminal; // idle-state proxy: "what would my own uplink look like"
        var txTerrainLos = txState?.TerrainLosClear ?? rxState.TerrainLosClear;

        var uplink = SatcomLinkEngineCore.EvaluateLeg(net, satellite, satPos.Value, txTerminal, net.UplinkHz, isUplinkLeg: true);
        var downlink = SatcomLinkEngineCore.EvaluateLeg(net, satellite, satPos.Value, rxState.Terminal, net.DownlinkHz, isUplinkLeg: false);

        // Whether each leg was ALREADY unusable (below horizon/Earth-occluded/antenna-blocked)
        // before terrain-LOS was even applied -- needed below to report the real cause instead of
        // always blaming local terrain.
        var uplinkUnusableBeforeTerrain = !uplink.Usable;
        var downlinkUnusableBeforeTerrain = !downlink.Usable;

        uplink = ApplyTerrainLos(uplink, txTerrainLos);
        downlink = ApplyTerrainLos(downlink, rxState.TerrainLosClear);

        var ebN0Db = (uplink.Usable && downlink.Usable)
            ? SatcomLinkBudget.EbN0Db(SatcomLinkBudget.CombineCarrierToNoiseDensityDbHz(uplink.Cn0DbHz, downlink.Cn0DbHz), net.BitRateBps)
            : -999.0;

        if (!_trackers.TryGetValue(rxKey, out var tracker))
        {
            tracker = new SatcomLinkQualityTracker();
            _trackers[rxKey] = tracker;
        }
        var qualityState = tracker.Update(ebN0Db, net, nowMs);

        var result = SatcomLinkEngineCore.Combine(satellite.Id, satellite.DisplayName, uplink, downlink, net, qualityState);
        if (!uplink.Usable)
            return result with { FailureReason = ClassifyFailure(uplink, uplinkUnusableBeforeTerrain), UnavailableReason = uplink.UnavailableReason };
        if (!downlink.Usable)
            return result with { FailureReason = ClassifyFailure(downlink, downlinkUnusableBeforeTerrain), UnavailableReason = downlink.UnavailableReason };

        return result;
    }

    /// <summary>Reports the ACTUAL cause of an unusable leg instead of always blaming local
    /// terrain: below the geometric horizon (includes Earth-ellipsoid occlusion and antenna/
    /// footprint-out-of-range, which are also "can't reach it from here" rather than a local
    /// obstruction) vs. genuinely blocked by local DCS terrain (only possible when the leg was
    /// otherwise RF-usable and ApplyTerrainLos is what flipped it).</summary>
    private static SatcomAcquisitionFailureReason ClassifyFailure(SatcomLegResult leg, bool unusableBeforeTerrain) =>
        unusableBeforeTerrain ? SatcomAcquisitionFailureReason.BelowHorizon : SatcomAcquisitionFailureReason.TerrainBlocked;

    /// <summary>Applies the client's self-reported local DCS terrain-LOS result as an additional
    /// gate on top of the pure geometric/RF leg: a "clear" claim is only trusted when the satellite
    /// is geometrically above the horizon in the first place (anti-cheat -- a legitimate
    /// land.isVisible ray toward the actual assigned satellite could never produce "clear" for a
    /// below-horizon satellite); otherwise a "blocked" claim overrides an RF-usable leg, since a
    /// local mountain can block a satellite the RF math alone doesn't know about.</summary>
    private static SatcomLegResult ApplyTerrainLos(SatcomLegResult leg, bool clientClaimedClear)
    {
        var plausiblyClear = clientClaimedClear && leg.ElevationDeg > 0.0;
        if (plausiblyClear || !leg.Usable)
            return leg;

        return leg with { AntennaGainDb = -100.0, Cn0DbHz = -999.0, UnavailableReason = "local DCS terrain blocks satellite" };
    }
}
