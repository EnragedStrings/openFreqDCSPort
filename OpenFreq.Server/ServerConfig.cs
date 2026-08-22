using System.Text.Json.Serialization;
using OpenFreq.Common.Satcom;

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

    /// <summary>Default catalog: two generic, clearly-fictional StaticGeo slots and one DAMA 5kHz
    /// net assigned to the first -- enough for SATCOM to work immediately with zero admin config,
    /// per docs/SATCOM_SIMULATION.md. Never claims a real military satellite currently carries a
    /// specific operational channel.</summary>
    public static SatcomServerConfig Default => new()
    {
        Satellites =
        [
            new SatcomSatelliteDefinition
            {
                Id = "satcom-alpha", DisplayName = "SATCOM-ALPHA (generic, mission-defined GEO slot)",
                EphemerisMode = SatcomEphemerisMode.StaticGeo, StaticLongitudeDeg = 100.0
            },
            new SatcomSatelliteDefinition
            {
                Id = "satcom-bravo", DisplayName = "SATCOM-BRAVO (generic, mission-defined GEO slot)",
                EphemerisMode = SatcomEphemerisMode.StaticGeo, StaticLongitudeDeg = -23.0
            }
        ],
        Nets =
        [
            new SatcomNetDefinition
            {
                NetId = "a10-arc210-satcom", DisplayName = "A-10C II ARC-210 UHF SATCOM (default net)",
                SelectionMode = SatelliteSelectionMode.Assigned, AssignedSatelliteId = "satcom-alpha",
                Waveform = SatcomWaveform.Dama5k, BandwidthHz = 5_000.0,
                UplinkHz = 300_000_000.0, DownlinkHz = 260_000_000.0
            }
        ]
    };
}
