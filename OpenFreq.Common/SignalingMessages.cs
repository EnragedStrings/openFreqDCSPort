using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFreq.Common;

/// <summary>
/// Base signaling message wrapper
/// </summary>
public class SignalingMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;

    [JsonPropertyName("payload")] public JsonElement? Payload { get; set; }
}

/// <summary>
/// Authentication request message
/// </summary>
public class AuthenticateMessage
{
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

/// <summary>
/// Join frequency channel request
/// </summary>
public class JoinChannelMessage
{
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }
}

/// <summary>
/// Leave frequency channel request
/// </summary>
public class LeaveChannelMessage
{
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }
}

/// <summary>
/// Audio transmission state change message
/// </summary>
public class AudioTransmissionMessage
{
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }

    [JsonPropertyName("transmitting")] public bool Transmitting { get; set; }

    [JsonPropertyName("3d")] public bool Is3d { get; set; }
}

/// <summary>
/// Error response message
/// </summary>
public class ErrorMessage
{
    [JsonPropertyName("error")] public string Error { get; set; } = string.Empty;
    [JsonPropertyName("serverVersion")] public string? ServerVersion { get; set; }
}

/// <summary>
/// Success response message
/// </summary>
public class SuccessMessage
{
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;

    [JsonPropertyName("peerId")] public string? PeerId { get; set; }

    [JsonPropertyName("audioPort")] public int? AudioPort { get; set; }

    [JsonPropertyName("opusCompression")] public bool OpusCompressionEnabled { get; set; }

    [JsonPropertyName("dcsLineOfSight")] public bool DcsLineOfSightEnabled { get; set; } = true;

    [JsonPropertyName("satcomEnabled")] public bool SatcomEnabled { get; set; } = true;

    [JsonPropertyName("frequencies")] public SortedDictionary<int, List<PeerData>> FrequenciesPeers { get; set; } = [];

}

/// <summary>
/// Peer joined channel notification
/// </summary>
public class PeerJoinedMessage
{
    [JsonPropertyName("peerId")] public string PeerId { get; set; } = string.Empty;
    [JsonPropertyName("peerDisplayName")] public string PeerDisplayName { get; set; } = string.Empty;

    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }
}

/// <summary>
/// Peer left channel notification
/// </summary>
public class PeerLeftMessage
{
    [JsonPropertyName("peerId")] public string PeerId { get; set; } = string.Empty;

    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }
}

/// <summary>
/// Transmission state change notification from peer
/// </summary>
public class TransmissionEventMessage
{
    [JsonPropertyName("peerId")] public string PeerId { get; set; } = string.Empty;
    [JsonPropertyName("peerDisplayName")] public string PeerDisplayName { get; set; } = string.Empty;
    [JsonPropertyName("transmitting")] public bool Transmitting { get; set; }
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }
    [JsonPropertyName("3d")] public bool Is3d { get; set; }
}

/// <summary>
/// Channel state with list of peers
/// </summary>
public class ChannelStateMessage
{
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }

    [JsonPropertyName("peers")] public List<Peer> Peers { get; set; } = [];

    public class Peer
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("displayname")] public string DisplayName { get; set; } = string.Empty;

        public Peer(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }
    }
}

/// <summary>
/// Update display name
/// </summary>
public class DisplayNameMessage
{
    [JsonPropertyName("displayname")] public string DisplayName { get; set; } = string.Empty;
}


/// <summary>
/// Peer list has changed
/// </summary>
public class AllPeersStatusMessage
{
    [JsonPropertyName("frequencies")] public SortedDictionary<int, List<PeerData>> FrequenciesPeers { get; set; } = [];
}

/// <summary>
/// Client notifies server of its current 3D mode
/// </summary>
public class ModeUpdateMessage
{
    [JsonPropertyName("3d")] public bool Is3d { get; set; }
}

/// <summary>
/// Server-wide debug/behavior settings pushed to authenticated clients.
/// </summary>
public class ServerSettingsMessage
{
    [JsonPropertyName("dcsLineOfSight")] public bool DcsLineOfSightEnabled { get; set; } = true;

    [JsonPropertyName("satcomEnabled")] public bool SatcomEnabled { get; set; } = true;
}

/// <summary>
/// Client -&gt; server, sent once per DCS export tick per active SATCOM-capable radio. The server is
/// authoritative for satellite selection/link-budget/DAMA; this is the raw geometry/state input it
/// needs. TerrainLosClear is computed client-side (only the client has DCS's land.isVisible) along
/// a LOCAL ray toward the last-known assigned satellite direction, extended until safely above all
/// possible theater terrain -- not an arbitrary short cutoff, and never cast the full ~35,786 km to
/// the satellite itself. The server independently sanity-checks this claim against the satellite it
/// actually assigned (see SatcomLinkEngine) rather than trusting it blindly.
/// </summary>
public class SatcomGeometryUpdateMessage
{
    [JsonPropertyName("channelKey")] public string ChannelKey { get; set; } = string.Empty;
    [JsonPropertyName("netId")] public string NetId { get; set; } = string.Empty;

    [JsonPropertyName("lat")] public double LatitudeDeg { get; set; }
    [JsonPropertyName("lon")] public double LongitudeDeg { get; set; }
    [JsonPropertyName("alt")] public double AltitudeMeters { get; set; }

    [JsonPropertyName("heading")] public double? HeadingRad { get; set; }
    [JsonPropertyName("pitch")] public double? PitchRad { get; set; }
    [JsonPropertyName("bank")] public double? BankRad { get; set; }

    [JsonPropertyName("radioPowered")] public bool RadioPowered { get; set; }

    /// <summary>The local ARC-210 cockpit login/acquisition animation (SatcomAcquisitionStateMachine,
    /// client-local, unchanged 5.0s timing) has reached Ready. DAMA network access is layered on
    /// top of this, not merged into it -- see docs/SATCOM_SIMULATION.md.</summary>
    [JsonPropertyName("loginReady")] public bool LoginReady { get; set; }

    [JsonPropertyName("ptt")] public bool PttPressed { get; set; }
    [JsonPropertyName("terrainLos")] public bool TerrainLosClear { get; set; }
    [JsonPropertyName("debugRequested")] public bool DebugRequested { get; set; }

    /// <summary>Only meaningful for a Dedicated-waveform net: the terminal's own currently-tuned
    /// carrier frequency, Hz -- see SatcomTerminalState.TunedFrequencyHz.</summary>
    [JsonPropertyName("tunedFrequencyHz")] public double? TunedFrequencyHz { get; set; }

    /// <summary>Generic, non-classified DAMA request priority (higher = served first when the
    /// net is at capacity). NOT a model of real (classified) precedence values -- a GAMEPLAY_CONFIG
    /// abstraction only. Defaults to 0 (normal).</summary>
    [JsonPropertyName("priority")] public int Priority { get; set; }
}

/// <summary>One catalog satellite's current resolved position, for the low-rate broadcast below.</summary>
public class SatcomSatelliteInfoDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("lat")] public double LatitudeDeg { get; set; }
    [JsonPropertyName("lon")] public double LongitudeDeg { get; set; }
    [JsonPropertyName("alt")] public double AltitudeMeters { get; set; }
    [JsonPropertyName("stale")] public bool IsStale { get; set; }
}

/// <summary>
/// Server -&gt; all clients, low rate (on change / a few times a minute). Lets clients compute their
/// own az/el (for UI and for the short-range terrain-LOS ray) without needing SGP4 client-side --
/// the server remains the sole ephemeris/propagation authority.
/// </summary>
public class SatelliteEphemerisUpdateMessage
{
    [JsonPropertyName("satellites")] public List<SatcomSatelliteInfoDto> Satellites { get; set; } = [];
}

/// <summary>One upcoming encoded SATCOM voice frame's server-decided disposition and scheduled
/// local playback arrival time (Unix ms, client clock domain -- see SatcomLinkStateMessage remarks).</summary>
public class SatcomFrameDispositionDto
{
    [JsonPropertyName("t")] public long ScheduledArrivalUnixMs { get; set; }
    [JsonPropertyName("d")] public string Disposition { get; set; } = "Clean"; // Clean/Corrected/Corrupted/Erased
}

/// <summary>
/// Server -&gt; one client, per-channel SATCOM link state. Carries the authoritative link metrics
/// (for UI/debug -- detailed RF metrics only populated when the server has granted this session
/// debug access, per DebugAuthorized) and a short lookahead batch of already-decided frame
/// dispositions with scheduled arrival timestamps, so the client's real-time audio thread only ever
/// dequeues precomputed results and never rolls its own corruption dice or does per-sample network
/// work. Timestamps are in the SERVER's Unix-ms clock; the client maps them to its own local
/// playback clock via the connection's already-established clock offset (see RTPJitterbuffer's
/// existing clock-sync handling) rather than assuming clocks are identical.
/// </summary>
public class SatcomLinkStateMessage
{
    [JsonPropertyName("channelKey")] public string ChannelKey { get; set; } = string.Empty;

    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("failureReason")] public string FailureReason { get; set; } = "None";

    [JsonPropertyName("satelliteId")] public string SatelliteId { get; set; } = string.Empty;
    [JsonPropertyName("satelliteName")] public string SatelliteName { get; set; } = string.Empty;

    [JsonPropertyName("qualityState")] public string QualityState { get; set; } = "Lost";
    [JsonPropertyName("damaState")] public string DamaState { get; set; } = "Offline";
    [JsonPropertyName("damaFrameIndex")] public long? DamaFrameIndex { get; set; }
    [JsonPropertyName("damaSlot")] public int? DamaSlot { get; set; }

    [JsonPropertyName("propagationSec")] public double PropagationLatencySeconds { get; set; }
    [JsonPropertyName("frameErrorRate")] public double FrameErrorRate { get; set; }

    [JsonPropertyName("debug")] public bool DebugAuthorized { get; set; }
    [JsonPropertyName("cn0DbHz")] public double? CombinedCn0DbHz { get; set; }
    [JsonPropertyName("ebN0Db")] public double? EbN0Db { get; set; }
    [JsonPropertyName("rawBer")] public double? RawBer { get; set; }
    [JsonPropertyName("postFecBer")] public double? PostFecBer { get; set; }
    [JsonPropertyName("upElevationDeg")] public double? UplinkElevationDeg { get; set; }
    [JsonPropertyName("upAzimuthDeg")] public double? UplinkAzimuthDeg { get; set; }
    [JsonPropertyName("upRangeM")] public double? UplinkRangeMeters { get; set; }
    [JsonPropertyName("downElevationDeg")] public double? DownlinkElevationDeg { get; set; }
    [JsonPropertyName("downAzimuthDeg")] public double? DownlinkAzimuthDeg { get; set; }
    [JsonPropertyName("downRangeM")] public double? DownlinkRangeMeters { get; set; }

    [JsonPropertyName("frames")] public List<SatcomFrameDispositionDto> FrameDispositions { get; set; } = [];
}
