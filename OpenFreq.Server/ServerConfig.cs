using System.Text.Json.Serialization;
using OpenFreq.Common.Satcom;
using OpenFreqServer.SrsBridge;

namespace OpenFreqServer;

public class ServerConfig
{
    [JsonPropertyName("serverPassword")]
    public string? ServerPassword { get; set; }

    [JsonPropertyName("websocketPort")]
    public int WebSocketPort { get; set; } = 9987;

    [JsonPropertyName("audioPort")]
    public int AudioPort { get; set; } = 9988;

    [JsonPropertyName("maxClientsPerChannel")]
    public int MaxClientsPerChannel { get; set; } = 50;

    [JsonPropertyName("maxChannelsPerClient")]
    public int MaxChannelsPerClient { get; set; } = 10;

    [JsonPropertyName("opusCompression")]
    public bool EnableOpusCompression { get; set; } = true;

    [JsonPropertyName("dcsLineOfSight")]
    public bool DcsLineOfSightEnabled { get; set; } = true;

    /// <summary>Global on/off gate for the ARC-210 SATCOM simulation, mirroring
    /// DcsLineOfSightEnabled above. Like that flag, this is the server's actual "authority" over
    /// SATCOM in this codebase's existing architecture -- it does not compute or verify link
    /// quality itself (that's client-side, same as terrestrial propagation; see
    /// docs/SATCOM_SIMULATION.md's "Server Authority" section for why).</summary>
    [JsonPropertyName("satcomEnabled")]
    public bool SatcomEnabled { get; set; } = true;

    /// <summary>Server-authoritative SATCOM catalog/nets/ephemeris settings, consulted only when
    /// <see cref="SatcomEnabled"/> is true. See docs/SATCOM_SIMULATION.md.</summary>
    [JsonPropertyName("satcom")]
    public SatcomServerConfig Satcom { get; set; } = SatcomServerConfig.Default;

    [JsonPropertyName("broadcastPeerUpdates")]
    public bool BroadcastPeerUpdates { get; set; } = true;

    /// <summary>Opt-in gate for the SRS (DCS-SimpleRadioStandalone) protocol bridge -- off by
    /// default so existing deployments aren't surprised by a newly-opened port. See
    /// docs/SRS_BRIDGE.md.</summary>
    [JsonPropertyName("srsBridgeEnabled")]
    public bool SrsBridgeEnabled { get; set; } = false;

    [JsonPropertyName("srsBridge")]
    public SrsBridgeConfig SrsBridge { get; set; } = SrsBridgeConfig.Default;

    /// <summary>Opt-in: silently download and stage new releases in the background, applying them
    /// (and restarting the server process) only once zero clients are connected -- never
    /// disconnects anyone unexpectedly. Off by default, same as SrsBridgeEnabled -- an existing
    /// deployment shouldn't restart itself without the operator asking for that. See
    /// ServerUpdateCoordinator.</summary>
    [JsonPropertyName("autoUpdateEnabled")]
    public bool AutoUpdateEnabled { get; set; } = false;

    /// <summary>How often ServerUpdateCoordinator re-checks GitHub for a newer release (an
    /// immediate check always happens once at startup regardless). Clamped to a 1-minute floor.
    /// </summary>
    [JsonPropertyName("updateCheckIntervalMinutes")]
    public int UpdateCheckIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// Admin-editable SATCOM catalog: which satellites exist (and how their position is determined),
/// which nets/services exist (waveform, uplink/downlink, RF parameters, satellite assignment), and
/// ephemeris/debug settings. Ships with a StaticGeo default (see <see cref="Default"/>) so SATCOM
/// works out of the box with zero admin setup; LiveTle satellites require an admin-supplied,
/// currently-valid NORAD id (verify against current CelesTrak data before use -- this project does
/// not ship one).
/// </summary>
public class SatcomServerConfig
{
    /// <summary>Directory (relative to the server executable, unless rooted) where fetched
    /// TLE/GP data is cached for LiveTle satellites, so a network outage or CelesTrak downtime
    /// degrades to "last known good" rather than losing ephemeris entirely.</summary>
    [JsonPropertyName("ephemerisCacheDirectory")]
    public string EphemerisCacheDirectory { get; set; } = "satcom_cache";

    /// <summary>How often LiveTle satellites re-fetch elements from CelesTrak. ~2 hours matches
    /// CelesTrak's own published GP/TLE update cadence -- SOURCE_DERIVED, not arbitrary.</summary>
    [JsonPropertyName("ephemerisFetchIntervalHours")]
    public double EphemerisFetchIntervalHours { get; set; } = 2.0;

    /// <summary>Local SGP4 propagation tick rate, Hz -- satellites don't need re-propagating every
    /// audio frame, just often enough that look angles stay current. GAMEPLAY_CONFIG.</summary>
    [JsonPropertyName("propagationHz")]
    public double PropagationHz { get; set; } = 1.0;

    /// <summary>When true, clients that set SatcomGeometryUpdateMessage.DebugRequested receive the
    /// detailed RF/DAMA metrics bundle in SatcomLinkStateMessage. Admin-controlled -- normal
    /// end-user clients never get a client-side toggle for this (see docs/SATCOM_SIMULATION.md
    /// "no client quality cheating").</summary>
    [JsonPropertyName("debugTelemetryEnabled")]
    public bool DebugTelemetryEnabled { get; set; } = true;

    [JsonPropertyName("satellites")]
    public List<SatcomSatelliteDefinition> Satellites { get; set; } = [];

    [JsonPropertyName("nets")]
    public List<SatcomNetDefinition> Nets { get; set; } = [];

    /// <summary>Default catalog: the real 11-satellite UHF Follow-On (UFO) constellation, tracked
    /// live via SGP4/CelesTrak (LiveTle mode -- see SatcomEphemerisService), with the default net
    /// set to AutoBestVisible so each client is served whichever UFO bird is actually visible from
    /// its own position, per docs/SATCOM_SIMULATION.md. NORAD catalog numbers are PROJECT_OBSERVED
    /// (looked up against CelesTrak's own live GP element query, cross-checked against multiple
    /// sources, 2026-08-22) -- this only fixes WHICH real objects to track; their live position
    /// always comes from a fresh CelesTrak/SGP4 fetch at runtime, never a value baked in here. As
    /// with every LiveTle satellite, a network outage falls back to the last-cached position, and
    /// only after that to each entry's crude Static* fields (0 deg longitude here -- there is no
    /// verified current operational longitude to assert per bird, several are decades old and may
    /// have drifted or been retired from active service; this project still never claims one is
    /// presently carrying a specific real channel). If CelesTrak is unreachable and no cache exists
    /// (first run, offline), SATCOM will simply have no visible satellites until connectivity
    /// returns -- switch any entry back to StaticGeo for a guaranteed-available offline fallback.</summary>
    public static SatcomServerConfig Default => new()
    {
        Satellites =
        [
            new SatcomSatelliteDefinition { Id = "ufo-1", DisplayName = "UFO 1 (USA 98)", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 22563 },
            new SatcomSatelliteDefinition { Id = "ufo-2", DisplayName = "UFO 2 (USA 95)", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 22787 },
            new SatcomSatelliteDefinition { Id = "ufo-3", DisplayName = "UFO 3 (USA 104)", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 23132 },
            new SatcomSatelliteDefinition { Id = "ufo-4", DisplayName = "UFO 4 (USA 108)", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 23467 },
            new SatcomSatelliteDefinition { Id = "ufo-5", DisplayName = "UFO 5 (USA 111)", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 23589 },
            new SatcomSatelliteDefinition { Id = "ufo-6", DisplayName = "UFO 6 (USA 114)", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 23696 },
            new SatcomSatelliteDefinition { Id = "ufo-7", DisplayName = "UFO 7 (USA 127)", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 23967 },
            new SatcomSatelliteDefinition { Id = "ufo-8", DisplayName = "UFO 8 (USA 138)", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 25258 },
            new SatcomSatelliteDefinition { Id = "ufo-9", DisplayName = "UFO 9 (USA 140)", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 25501 },
            new SatcomSatelliteDefinition { Id = "ufo-10", DisplayName = "UFO 10 (USA 146)", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 25967 },
            new SatcomSatelliteDefinition { Id = "ufo-11", DisplayName = "UFO 11 (USA 174)", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 28117 }
        ],
        Nets =
        [
            new SatcomNetDefinition
            {
                NetId = "a10-arc210-satcom", DisplayName = "A-10C II ARC-210 UHF SATCOM (default net)",
                // Best-visible bird per client, from that client's own position -- see
                // SatcomSatelliteSelector/SatcomLinkEngine.Evaluate. Each receiver independently
                // picks its own best UFO satellite and the active transmitter's uplink is checked
                // against THAT satellite, so two stations can only hear each other when both can
                // actually reach a shared bird -- no separate "sync to the same satellite" step
                // needed, it falls out of the per-leg LOS evaluation already in place.
                SelectionMode = SatelliteSelectionMode.AutoBestVisible,
                Waveform = SatcomWaveform.Dama5k, BandwidthHz = 5_000.0,
                UplinkHz = 300_000_000.0, DownlinkHz = 260_000_000.0
            },
            new SatcomNetDefinition
            {
                NetId = "a10-arc210-satcom-dedicated",
                DisplayName = "A-10C II ARC-210 UHF SATCOM (dedicated/half-duplex)",
                // Point-to-point, no DAMA login -- the operator tunes the ARC-210 directly to an
                // assigned transponder frequency (channels 26-30 on the channel-select knob,
                // PROJECT_OBSERVED), so UplinkHz/DownlinkHz below are only a fallback for the rare
                // case a terminal hasn't reported its tuned frequency yet -- see
                // SatcomLinkEngine.Evaluate/SatcomSatelliteSelector, which both prefer each
                // terminal's own SatcomTerminalState.TunedFrequencyHz for this net.
                SelectionMode = SatelliteSelectionMode.AutoBestVisible,
                Waveform = SatcomWaveform.Dedicated5k, BandwidthHz = 5_000.0,
                UplinkHz = 300_000_000.0, DownlinkHz = 300_000_000.0
            }
        ]
    };
}
