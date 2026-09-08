using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OpenFreq.Common.Signaling;

public static class SignalingMessageFactory
{
    public static SignalingMessage CreateAuthenticate(string password, string? displayName, string? version = null,
        bool wantsTranscripts = false)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Authenticate,
            Payload = JsonSerializer.SerializeToElement(
                new AuthenticateMessage
                {
                    Password = password, DisplayName = displayName, Version = version,
                    WantsTranscripts = wantsTranscripts
                },
                OpenFreqJsonContext.Default.AuthenticateMessage)
        };
    }

    public static SignalingMessage CreateJoin(int frequencyKhz, double? lat = null, double? lon = null,
        double? alt = null)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Join,
            Payload = JsonSerializer.SerializeToElement(
                new JoinChannelMessage
                {
                    FrequencyKhz = frequencyKhz, LatitudeDeg = lat, LongitudeDeg = lon, AltitudeMeters = alt
                },
                OpenFreqJsonContext.Default.JoinChannelMessage)
        };
    }

    public static SignalingMessage CreateLeave(int frequencyKhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Leave,
            Payload = JsonSerializer.SerializeToElement(
                new LeaveChannelMessage { FrequencyKhz = frequencyKhz },
                OpenFreqJsonContext.Default.LeaveChannelMessage)
        };
    }

    public static SignalingMessage CreateTransmission(int frequencyKhz, bool transmitting, bool is3d,
        Guid? transmissionId = null, double? lat = null, double? lon = null, double? alt = null)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Transmission,
            Payload = JsonSerializer.SerializeToElement(
                new AudioTransmissionMessage
                {
                    FrequencyKhz = frequencyKhz,
                    Transmitting = transmitting,
                    Is3d = is3d,
                    TransmissionId = transmissionId?.ToString("N"),
                    LatitudeDeg = lat,
                    LongitudeDeg = lon,
                    AltitudeMeters = alt
                },
                OpenFreqJsonContext.Default.AudioTransmissionMessage)
        };
    }

    public static SignalingMessage CreateSuccess(string message, SortedDictionary<int, List<PeerData>> peers,
        string? peerId = null, int? audioPort = null, bool opusEnabled = true, bool dcsLineOfSightEnabled = true,
        bool satcomEnabled = true)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Success,
            Payload = JsonSerializer.SerializeToElement(
                new SuccessMessage
                {
                    Message = message,
                    PeerId = peerId,
                    AudioPort = audioPort,
                    OpusCompressionEnabled = opusEnabled,
                    DcsLineOfSightEnabled = dcsLineOfSightEnabled,
                    SatcomEnabled = satcomEnabled,
                    FrequenciesPeers = peers
                },
                OpenFreqJsonContext.Default.SuccessMessage)
        };
    }

    public static SignalingMessage CreateError(string error, string? serverVersion = null)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Error,
            Payload = JsonSerializer.SerializeToElement(
                new ErrorMessage { Error = error, ServerVersion = serverVersion },
                OpenFreqJsonContext.Default.ErrorMessage)
        };
    }

    public static SignalingMessage CreatePeerJoined(string peerId, string? peerDisplayName, int frequencyKhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.PeerJoined,
            Payload = JsonSerializer.SerializeToElement(
                new PeerJoinedMessage
                {
                    PeerId = peerId,
                    PeerDisplayName = peerDisplayName ?? "Unnamed",
                    FrequencyKhz = frequencyKhz
                },
                OpenFreqJsonContext.Default.PeerJoinedMessage)
        };
    }

    public static SignalingMessage CreatePeerLeft(string peerId, int frequencyKhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.PeerLeft,
            Payload = JsonSerializer.SerializeToElement(
                new PeerLeftMessage
                {
                    PeerId = peerId,
                    FrequencyKhz = frequencyKhz
                },
                OpenFreqJsonContext.Default.PeerLeftMessage)
        };
    }

    public static SignalingMessage CreateSetDisplayName(string displayName)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.SetDisplayName,
            Payload = JsonSerializer.SerializeToElement(
                new DisplayNameMessage { DisplayName = displayName },
                OpenFreqJsonContext.Default.DisplayNameMessage)
        };
    }

    public static SignalingMessage CreateTransmissionEvent(string peerId, int frequencyKhz, bool transmitting, bool is3d)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Transmission,
            Payload = JsonSerializer.SerializeToElement(
                new TransmissionEventMessage
                {
                    PeerId = peerId,
                    FrequencyKhz = frequencyKhz,
                    Transmitting = transmitting,
                    Is3d = is3d
                },
                OpenFreqJsonContext.Default.TransmissionEventMessage)
        };
    }

    public static SignalingMessage CreateChannelState(int frequencyKhz, List<ChannelStateMessage.Peer> peers)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.ChannelState,
            Payload = JsonSerializer.SerializeToElement(
                new ChannelStateMessage
                {
                    FrequencyKhz = frequencyKhz,
                    Peers = peers
                },
                OpenFreqJsonContext.Default.ChannelStateMessage)
        };
    }

    public static SignalingMessage CreateAllPeersStatusMessage(SortedDictionary<int, List<PeerData>> allPeersStatus)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.AllPeersStatus,
            Payload = JsonSerializer.SerializeToElement(
                new AllPeersStatusMessage()
                {
                    FrequenciesPeers = allPeersStatus
                },
                OpenFreqJsonContext.Default.AllPeersStatusMessage)
        };
    }

    public static SignalingMessage CreateModeUpdate(bool is3d)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.ModeUpdate,
            Payload = JsonSerializer.SerializeToElement(
                new ModeUpdateMessage { Is3d = is3d },
                OpenFreqJsonContext.Default.ModeUpdateMessage)
        };
    }

    public static SignalingMessage CreateServerSettings(bool dcsLineOfSightEnabled, bool satcomEnabled = true)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.ServerSettings,
            Payload = JsonSerializer.SerializeToElement(
                new ServerSettingsMessage
                {
                    DcsLineOfSightEnabled = dcsLineOfSightEnabled,
                    SatcomEnabled = satcomEnabled
                },
                OpenFreqJsonContext.Default.ServerSettingsMessage)
        };
    }

    public static SignalingMessage CreateSatcomGeometryUpdate(SatcomGeometryUpdateMessage message)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.SatcomGeometryUpdate,
            Payload = JsonSerializer.SerializeToElement(message, OpenFreqJsonContext.Default.SatcomGeometryUpdateMessage)
        };
    }

    public static SignalingMessage CreateDcsPresenceUpdate(DcsPresenceUpdateMessage message)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.DcsPresenceUpdate,
            Payload = JsonSerializer.SerializeToElement(message, OpenFreqJsonContext.Default.DcsPresenceUpdateMessage)
        };
    }

    public static SignalingMessage CreateDcsLosOracleRequest(DcsLosOracleRequestMessage message)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.DcsLosOracleRequest,
            Payload = JsonSerializer.SerializeToElement(message, OpenFreqJsonContext.Default.DcsLosOracleRequestMessage)
        };
    }

    public static SignalingMessage CreateDcsLosOracleResponse(DcsLosOracleResponseMessage message)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.DcsLosOracleResponse,
            Payload = JsonSerializer.SerializeToElement(message, OpenFreqJsonContext.Default.DcsLosOracleResponseMessage)
        };
    }

    public static SignalingMessage CreateSatelliteEphemerisUpdate(List<SatcomSatelliteInfoDto> satellites)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.SatelliteEphemerisUpdate,
            Payload = JsonSerializer.SerializeToElement(
                new SatelliteEphemerisUpdateMessage { Satellites = satellites },
                OpenFreqJsonContext.Default.SatelliteEphemerisUpdateMessage)
        };
    }

    public static SignalingMessage CreateSatcomLinkState(SatcomLinkStateMessage message)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.SatcomLinkState,
            Payload = JsonSerializer.SerializeToElement(message, OpenFreqJsonContext.Default.SatcomLinkStateMessage)
        };
    }

    public static SignalingMessage CreateTransmissionTranscript(Guid transmissionId, List<TranscriptWordDto> words,
        string? language = null)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.TransmissionTranscript,
            Payload = JsonSerializer.SerializeToElement(
                new TransmissionTranscriptMessage
                {
                    TransmissionId = transmissionId.ToString("N"),
                    Words = words,
                    Language = language
                },
                OpenFreqJsonContext.Default.TransmissionTranscriptMessage)
        };
    }

    public static SignalingMessage CreateTranscriptDelivery(TranscriptDeliveryMessage message)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.TranscriptDelivery,
            Payload = JsonSerializer.SerializeToElement(message, OpenFreqJsonContext.Default.TranscriptDeliveryMessage)
        };
    }

    public static T? DeserializePayload<T>(JsonElement? payload) where T : class
    {
        if (payload == null)
            return null;

        var typeInfo = (JsonTypeInfo<T>)OpenFreqJsonContext.Default.GetTypeInfo(typeof(T))!;
        return payload.Value.Deserialize(typeInfo);
    }
}
