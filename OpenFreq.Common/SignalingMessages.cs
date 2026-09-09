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

    /// <summary>Opt-in capability declaration: "relay me transcripts of transmissions I could
    /// plausibly hear" -- see TranscriptDeliveryMessage. Defaults to false/absent for every
    /// existing client (the GUI client, OpenFreq.Testclient, the SRS bridge); a bot client sets
    /// this true. Deliberately global to the connection, not per-frequency-join -- a bot exists to
    /// process every transmission it hears, not some of them.</summary>
    [JsonPropertyName("wantsTranscripts")] public bool WantsTranscripts { get; set; }
}

/// <summary>
/// Join frequency channel request
/// </summary>
public class JoinChannelMessage
{
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }

    /// <summary>Optional: "treat this specific channel-join as listening from this geodetic
    /// position." Only meaningful to clients that want position-gated transcript delivery (see
    /// AuthenticateMessage.WantsTranscripts) -- a bot operating multiple named positions (e.g. a
    /// "Nellis Tower" and a "Luke Tower") declares each one's position when joining that position's
    /// frequency. Null/absent for every other client -- their own client-side audibility model is
    /// unaffected either way.</summary>
    [JsonPropertyName("lat")] public double? LatitudeDeg { get; set; }
    [JsonPropertyName("lon")] public double? LongitudeDeg { get; set; }
    [JsonPropertyName("alt")] public double? AltitudeMeters { get; set; }

    /// <summary>Join silently: received for audio-routing purposes exactly like a normal join, but
    /// never broadcast as a PeerJoined to others already on the channel, never shown in anyone
    /// else's peer list/roster, and never counted against MaxClientsPerChannel. Used by a GCI
    /// "monitor all frequencies" scanner that sweeps across every active frequency on the server --
    /// without this, that sweep would spam every pilot with join/leave noise. Defaults false so
    /// every existing client/bot is unaffected.</summary>
    [JsonPropertyName("observer")] public bool IsObserver { get; set; }
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

    /// <summary>Identifies one PTT key-down-to-key-up session, minted client-side once per
    /// StartTransmissionAsync call and carried on both the start (transmitting:true) and stop
    /// (transmitting:false) messages for that session (and the 333ms heartbeat resends in between).
    /// Lets the server correlate a later TransmissionTranscriptMessage (which can arrive seconds
    /// after the PTT session itself, once local speech-to-text finishes) back to the transmission
    /// it belongs to -- see SignalingServer's pending-transmission registry. Null/absent for any
    /// client that predates this field; such transmissions simply can't carry a transcript.</summary>
    [JsonPropertyName("transmissionId")] public string? TransmissionId { get; set; }

    /// <summary>Optional: this transmitter's own geodetic position at the moment of this
    /// transmission, if the client mode knows one (DCS export position, or a GCI location's
    /// configured static position) -- the "from" side of a transcript-delivery LOS check. Null/
    /// absent means the transmission simply can't be LOS-gated (delivered to every capable
    /// listener) -- see TranscriptDeliveryMessage's own doc comment.</summary>
    [JsonPropertyName("lat")] public double? LatitudeDeg { get; set; }
    [JsonPropertyName("lon")] public double? LongitudeDeg { get; set; }
    [JsonPropertyName("alt")] public double? AltitudeMeters { get; set; }
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
/// Client -&gt; server, low-rate (every ~1-2s, not per DCS export tick -- see
/// OpenFreqService.SendDcsPresenceUpdatesAsync), sent by any DCS-mode OpenFreq client regardless of
/// SATCOM state. Unlike SatcomGeometryUpdateMessage (which only exists for active SATCOM radios),
/// this is the server's only source of "who's running DCS and roughly where" -- used to pick a
/// live DCS client as a remote terrain-LOS oracle for SRS-bridged legs the server has no terrain
/// data of its own to evaluate (see SrsLosOracleService, phase 4c/4d of the SRS bridge plan).
/// Theater is required for correctness: querying an oracle on the wrong map returns meaningless
/// terrain results, so callers must filter to matching theaters before picking one.
/// </summary>
public class DcsPresenceUpdateMessage
{
    [JsonPropertyName("lat")] public double LatitudeDeg { get; set; }
    [JsonPropertyName("lon")] public double LongitudeDeg { get; set; }
    [JsonPropertyName("alt")] public double AltitudeMeters { get; set; }
    [JsonPropertyName("theater")] public string Theater { get; set; } = string.Empty;
}

/// <summary>
/// Server -&gt; one client, asking it to referee terrain line-of-sight between two arbitrary
/// geodetic points via its own live DCS instance's terrain.isVisible -- neither point has to be
/// this client's own aircraft (see OpenFreqDCS.lua's buildRemoteLosResponse, which converts both
/// via coord.LLtoLO before raycasting). Used by SrsLosOracleService for legs involving an
/// SRS-bridged peer, which the server itself has no terrain data to evaluate. RequestId correlates
/// with the client's DcsLosOracleResponseMessage; the server times the request out (see
/// SignalingServer.RequestRemoteLineOfSightAsync) rather than waiting indefinitely.
/// </summary>
public class DcsLosOracleRequestMessage
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    [JsonPropertyName("fromLat")] public double FromLatitudeDeg { get; set; }
    [JsonPropertyName("fromLon")] public double FromLongitudeDeg { get; set; }
    [JsonPropertyName("fromAlt")] public double FromAltitudeMeters { get; set; }
    [JsonPropertyName("toLat")] public double ToLatitudeDeg { get; set; }
    [JsonPropertyName("toLon")] public double ToLongitudeDeg { get; set; }
    [JsonPropertyName("toAlt")] public double ToAltitudeMeters { get; set; }
}

/// <summary>Client -&gt; server, answering one DcsLosOracleRequestMessage.</summary>
public class DcsLosOracleResponseMessage
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    [JsonPropertyName("terrainAvailable")] public bool TerrainAvailable { get; set; }
    [JsonPropertyName("visible")] public bool Visible { get; set; }
    [JsonPropertyName("loss")] public double Loss { get; set; }
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

    /// <summary>Which physical SATCOM antenna is connected (see SatcomAntennaSelection), resolved
    /// client-side by SatcomAntennaSelectorStateMachine for diversity-capable airframes -- "Upper"
    /// or "Lower". Null/unparseable falls back to Upper (single-antenna behavior) server-side, so
    /// non-diversity aircraft that never set this are unaffected.</summary>
    [JsonPropertyName("antennaSelection")] public string? AntennaSelection { get; set; }
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

    // DEBUG-ONLY (candidate for removal/gating once the antenna model is trusted -- see
    // docs/SATCOM_SIMULATION.md): raw SatcomAntennaModel breakdown per leg, so a user tuned to a
    // SATCOM frequency can see exactly why their antenna gain is what it is -- effective tilt,
    // off-boresight angle, and the footprint/terminal gain components separately, not just the
    // combined C/N0 further up. Only populated when DebugAuthorized.
    [JsonPropertyName("upTiltDeg")] public double? UplinkTiltDeg { get; set; }
    [JsonPropertyName("upOffBoresightDeg")] public double? UplinkOffBoresightDeg { get; set; }
    [JsonPropertyName("upFootprintGainDb")] public double? UplinkFootprintGainDb { get; set; }
    [JsonPropertyName("upTerminalGainDb")] public double? UplinkTerminalGainDb { get; set; }
    [JsonPropertyName("downTiltDeg")] public double? DownlinkTiltDeg { get; set; }
    [JsonPropertyName("downOffBoresightDeg")] public double? DownlinkOffBoresightDeg { get; set; }
    [JsonPropertyName("downFootprintGainDb")] public double? DownlinkFootprintGainDb { get; set; }
    [JsonPropertyName("downTerminalGainDb")] public double? DownlinkTerminalGainDb { get; set; }

    [JsonPropertyName("frames")] public List<SatcomFrameDispositionDto> FrameDispositions { get; set; } = [];
}

/// <summary>One transcribed word/token and its time span, seconds relative to the start of the PTT
/// session it belongs to (transmission t=0 is StartTransmissionAsync's key-down moment) -- word-
/// level rather than sentence-level specifically so the server can drop just the words that
/// actually fall inside a "stepped" (overlapping-transmission) window rather than a whole sentence,
/// when redacting a transcript for one bot at delivery time -- see TranscriptDeliveryMessage's own
/// doc comment. Text must carry its own leading whitespace exactly as it should appear once
/// concatenated with its neighbors (the same convention whisper.cpp itself uses when reconstructing
/// sentence text from tokens) -- the server rebuilds a trimmed transcript by plain concatenation of
/// the surviving words in order, not by joining them with an inserted separator.</summary>
public class TranscriptWordDto
{
    [JsonPropertyName("w")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("s")] public double StartSec { get; set; }
    [JsonPropertyName("e")] public double EndSec { get; set; }
}

/// <summary>
/// Client -&gt; server: the transmitting client's own local speech-to-text result for one completed
/// PTT session, run on the raw pre-effects mic buffer (see OpenFreqService.RecordProcedure) once
/// StopTransmissionAsync fires -- necessarily arrives some time (typically a second or more) after
/// the transmission itself ended, since transcription isn't real-time. TransmissionId correlates
/// this back to the AudioTransmissionMessage that started the session; the server drops this
/// silently if that transmission is no longer in its pending registry (expired, or never carried an
/// id at all -- see AudioTransmissionMessage.TransmissionId). Only sent when the local client has
/// opted into sharing transcripts -- see AuthenticateMessage.WantsTranscripts's own doc comment for
/// why sending this is the transmitter's choice, not the listener's. Word-level timing (not just a
/// flat string) is what lets the server redact a stepped-on portion per bot at delivery time instead
/// of either the transmitting client trying to guess which bots stepped it, or every bot receiving
/// an artificially clean transcript of audio that would have actually been unintelligible garble.
/// </summary>
public class TransmissionTranscriptMessage
{
    [JsonPropertyName("transmissionId")] public string TransmissionId { get; set; } = string.Empty;
    [JsonPropertyName("words")] public List<TranscriptWordDto> Words { get; set; } = [];
    [JsonPropertyName("language")] public string? Language { get; set; }
}

/// <summary>
/// Server -&gt; one client: relays a transcript this client is allowed to see, as plain already-
/// finalized text -- a bot developer never needs to deal with word timing or stepping mechanics,
/// only a plausible transcript of what that bot could actually have heard. Only sent to peers that
/// declared AuthenticateMessage.WantsTranscripts=true, are currently joined to FrequencyKhz, and --
/// when both this listener's declared position (JoinChannelMessage.Lat/Lon/Alt) and the
/// transmitter's position (AudioTransmissionMessage.Lat/Lon/Alt) are known -- pass a real terrain
/// line-of-sight check via the same oracle mechanism SRS-bridged legs already use (see
/// LosOracleService). When either position is unknown, gating can't be evaluated and the transcript
/// is delivered anyway; when both are known but LOS can't be confirmed within a bounded wait, it is
/// NOT delivered (fail-closed) -- "should not be sent if there is no LOS" is treated as the safer
/// default over risking a false positive.
///
/// Stepping: if another transmission was active on the same frequency during any part of this one,
/// AND this specific bot's own audibility (the same LOS check above) reaches that other transmitter
/// too, the words falling inside the time window both transmissions were active get dropped before
/// Text is built -- a bot that could only actually hear one of the two callers isn't handed a clean
/// transcript of both. A transcript reduced to nothing by this is not delivered at all, same as a
/// fully LOS-blocked one. This redaction is evaluated independently per bot (two bots with different
/// audibility to the same pair of transmitters can legitimately receive different Text for the same
/// TransmissionId) and never reaches the transmitting client -- it has no idea whether, or for whom,
/// any of this happened.
/// </summary>
public class TranscriptDeliveryMessage
{
    [JsonPropertyName("transmissionId")] public string TransmissionId { get; set; } = string.Empty;
    [JsonPropertyName("fromPeerId")] public string FromPeerId { get; set; } = string.Empty;
    [JsonPropertyName("fromDisplayName")] public string FromDisplayName { get; set; } = string.Empty;
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("language")] public string? Language { get; set; }

    [JsonPropertyName("fromLat")] public double? FromLatitudeDeg { get; set; }
    [JsonPropertyName("fromLon")] public double? FromLongitudeDeg { get; set; }
    [JsonPropertyName("fromAlt")] public double? FromAltitudeMeters { get; set; }
}
