namespace OpenFreq.Common.Signaling;

/// <summary>
/// Constants for signaling message types
/// </summary>
public static class SignalingMessageTypes
{
    public const string Authenticate = "authenticate";
    public const string Join = "join";
    public const string Leave = "leave";
    public const string Transmission = "transmission";
    public const string Success = "success";
    public const string Error = "error";
    public const string PeerJoined = "peer-joined";
    public const string PeerLeft = "peer-left";
    public const string ChannelState = "channel-state";
    public const string SetDisplayName = "set-display-name";
    public const string AllPeersStatus = "all-peers-status";
    public const string ModeUpdate = "mode-update";
    public const string ServerSettings = "server-settings";
    public const string SatcomGeometryUpdate = "satcom-geometry-update";
    public const string DcsPresenceUpdate = "dcs-presence-update";
    public const string DcsLosOracleRequest = "dcs-los-oracle-request";
    public const string DcsLosOracleResponse = "dcs-los-oracle-response";
    public const string SatelliteEphemerisUpdate = "satellite-ephemeris-update";
    public const string SatcomLinkState = "satcom-link-state";

    /// <summary>Client -&gt; server: the transmitting client's own local speech-to-text result for
    /// one completed PTT session -- see TransmissionTranscriptMessage.</summary>
    public const string TransmissionTranscript = "transmission-transcript";

    /// <summary>Server -&gt; one bot-capable client: relays a transcript it's allowed to see -- see
    /// TranscriptDeliveryMessage.</summary>
    public const string TranscriptDelivery = "transcript-delivery";
}
