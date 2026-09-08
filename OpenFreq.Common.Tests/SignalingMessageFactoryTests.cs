using OpenFreq.Common.Signaling;

namespace OpenFreq.Common.Tests;

public class SignalingMessageFactoryTests
{
    [Fact]
    public void CreateAuthenticate_SetsCorrectType()
    {
        var msg = SignalingMessageFactory.CreateAuthenticate("pass", "Player1");
        Assert.Equal(SignalingMessageTypes.Authenticate, msg.Type);
    }

    [Fact]
    public void CreateAuthenticate_PayloadRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateAuthenticate("secret", "Viper");

        var payload = SignalingMessageFactory.DeserializePayload<AuthenticateMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Equal("secret", payload.Password);
        Assert.Equal("Viper", payload.DisplayName);
    }

    [Fact]
    public void CreateAuthenticate_NullDisplayName_Preserved()
    {
        var msg = SignalingMessageFactory.CreateAuthenticate("pass", null);
        var payload = SignalingMessageFactory.DeserializePayload<AuthenticateMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Null(payload.DisplayName);
    }

    [Fact]
    public void CreateAuthenticate_VersionRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateAuthenticate("pass", "Viper", "1.2.3");
        var payload = SignalingMessageFactory.DeserializePayload<AuthenticateMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Equal("1.2.3", payload.Version);
    }

    [Fact]
    public void CreateError_ServerVersionRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateError("Version mismatch", "1.2.3");
        var payload = SignalingMessageFactory.DeserializePayload<ErrorMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Equal("Version mismatch", payload.Error);
        Assert.Equal("1.2.3", payload.ServerVersion);
    }

    [Fact]
    public void CreateError_NoServerVersion_Null()
    {
        var msg = SignalingMessageFactory.CreateError("boom");
        var payload = SignalingMessageFactory.DeserializePayload<ErrorMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Null(payload.ServerVersion);
    }

    [Fact]
    public void CreateJoin_SetsCorrectTypeAndFrequency()
    {
        var msg = SignalingMessageFactory.CreateJoin(251000);

        Assert.Equal(SignalingMessageTypes.Join, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<JoinChannelMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal(251000, payload.FrequencyKhz);
    }

    [Fact]
    public void CreateLeave_SetsCorrectTypeAndFrequency()
    {
        var msg = SignalingMessageFactory.CreateLeave(135100);

        Assert.Equal(SignalingMessageTypes.Leave, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<LeaveChannelMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal(135100, payload.FrequencyKhz);
    }

    [Fact]
    public void CreateTransmission_TransmittingTrue_PayloadRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateTransmission(251000, transmitting: true, is3d: false);

        Assert.Equal(SignalingMessageTypes.Transmission, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal(251000, payload.FrequencyKhz);
        Assert.True(payload.Transmitting);
        Assert.False(payload.Is3d);
    }

    [Fact]
    public void CreateTransmission_Is3dTrue_PayloadRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateTransmission(251000, transmitting: false, is3d: true);
        var payload = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.True(payload.Is3d);
    }

    [Fact]
    public void CreateError_SetsCorrectTypeAndMessage()
    {
        var msg = SignalingMessageFactory.CreateError("Wrong password");

        Assert.Equal(SignalingMessageTypes.Error, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<ErrorMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("Wrong password", payload.Error);
    }

    [Fact]
    public void CreatePeerJoined_AllFieldsPreserved()
    {
        var msg = SignalingMessageFactory.CreatePeerJoined("peer-42", "Maverick", 251000);

        Assert.Equal(SignalingMessageTypes.PeerJoined, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<PeerJoinedMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("peer-42", payload.PeerId);
        Assert.Equal("Maverick", payload.PeerDisplayName);
        Assert.Equal(251000, payload.FrequencyKhz);
    }

    [Fact]
    public void CreatePeerJoined_NullDisplayName_FallsBackToUnnamed()
    {
        var msg = SignalingMessageFactory.CreatePeerJoined("peer-1", null, 251000);
        var payload = SignalingMessageFactory.DeserializePayload<PeerJoinedMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Equal("Unnamed", payload.PeerDisplayName);
    }

    [Fact]
    public void CreatePeerLeft_AllFieldsPreserved()
    {
        var msg = SignalingMessageFactory.CreatePeerLeft("peer-99", 135100);

        Assert.Equal(SignalingMessageTypes.PeerLeft, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<PeerLeftMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("peer-99", payload.PeerId);
        Assert.Equal(135100, payload.FrequencyKhz);
    }

    [Fact]
    public void CreateSetDisplayName_PayloadRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateSetDisplayName("Goose");

        Assert.Equal(SignalingMessageTypes.SetDisplayName, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<DisplayNameMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("Goose", payload.DisplayName);
    }

    [Fact]
    public void CreateTransmissionEvent_AllFieldsPreserved()
    {
        var msg = SignalingMessageFactory.CreateTransmissionEvent("peer-7", 251000, transmitting: true, is3d: true);

        var payload = SignalingMessageFactory.DeserializePayload<TransmissionEventMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("peer-7", payload.PeerId);
        Assert.Equal(251000, payload.FrequencyKhz);
        Assert.True(payload.Transmitting);
        Assert.True(payload.Is3d);
    }

    [Fact]
    public void CreateModeUpdate_Is3dTrue_PayloadRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateModeUpdate(is3d: true);

        Assert.Equal(SignalingMessageTypes.ModeUpdate, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<ModeUpdateMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.True(payload.Is3d);
    }

    [Fact]
    public void CreateSuccess_AllFieldsPreserved()
    {
        var peers = new SortedDictionary<int, List<PeerData>>();
        var msg = SignalingMessageFactory.CreateSuccess(
            "Authenticated", peers, peerId: "me-123", audioPort: 9988, opusEnabled: true);

        Assert.Equal(SignalingMessageTypes.Success, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<SuccessMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("Authenticated", payload.Message);
        Assert.Equal("me-123", payload.PeerId);
        Assert.Equal(9988, payload.AudioPort);
        Assert.True(payload.OpusCompressionEnabled);
    }

    [Fact]
    public void CreateAllPeersStatus_EmptyDict_PayloadRoundTrips()
    {
        var allPeers = new SortedDictionary<int, List<PeerData>>();
        var msg = SignalingMessageFactory.CreateAllPeersStatusMessage(allPeers);

        Assert.Equal(SignalingMessageTypes.AllPeersStatus, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<AllPeersStatusMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Empty(payload.FrequenciesPeers);
    }

    [Fact]
    public void DeserializePayload_NullPayload_ReturnsNull()
    {
        var result = SignalingMessageFactory.DeserializePayload<AuthenticateMessage>(null);
        Assert.Null(result);
    }

    [Fact]
    public void CreateChannelState_AllFieldsPreserved()
    {
        var peers = new List<ChannelStateMessage.Peer>
        {
            new("peer-1", "Viper"),
            new("peer-2", "Maverick")
        };

        var msg = SignalingMessageFactory.CreateChannelState(251000, peers);

        Assert.Equal(SignalingMessageTypes.ChannelState, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<ChannelStateMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal(251000, payload.FrequencyKhz);
        Assert.Equal(2, payload.Peers.Count);
        Assert.Equal("peer-1", payload.Peers[0].Id);
        Assert.Equal("Viper", payload.Peers[0].DisplayName);
    }

    [Fact]
    public void CreateSuccess_NullablesOmitted_StillDeserializes()
    {
        var peers = new SortedDictionary<int, List<PeerData>>();
        var msg = SignalingMessageFactory.CreateSuccess("ok", peers);

        var payload = SignalingMessageFactory.DeserializePayload<SuccessMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("ok", payload.Message);
        Assert.Null(payload.PeerId);
        Assert.Null(payload.AudioPort);
    }

    [Fact]
    public void CreateAllPeersStatus_WithEntries_RoundTrips()
    {
        var allPeers = new SortedDictionary<int, List<PeerData>>
        {
            [251000] = [new PeerData("p1", "Viper", PeerData.PeerStatus.Transmitting)]
        };

        var msg = SignalingMessageFactory.CreateAllPeersStatusMessage(allPeers);
        var payload = SignalingMessageFactory.DeserializePayload<AllPeersStatusMessage>(msg.Payload);

        Assert.NotNull(payload);
        var entry = Assert.Single(payload.FrequenciesPeers);
        Assert.Equal(251000, entry.Key);
        Assert.Equal("Viper", entry.Value[0].Name);
        Assert.Equal(PeerData.PeerStatus.Transmitting, entry.Value[0].Status);
    }

    [Fact]
    public void CreateAuthenticate_WantsTranscriptsDefaultsFalse()
    {
        var msg = SignalingMessageFactory.CreateAuthenticate("pass", "Viper");
        var payload = SignalingMessageFactory.DeserializePayload<AuthenticateMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.False(payload.WantsTranscripts);
    }

    [Fact]
    public void CreateAuthenticate_WantsTranscriptsTrue_RoundTrips()
    {
        var msg = SignalingMessageFactory.CreateAuthenticate("pass", "BotClient", wantsTranscripts: true);
        var payload = SignalingMessageFactory.DeserializePayload<AuthenticateMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.True(payload.WantsTranscripts);
    }

    [Fact]
    public void CreateJoin_NoPosition_FieldsNull()
    {
        var msg = SignalingMessageFactory.CreateJoin(251000);
        var payload = SignalingMessageFactory.DeserializePayload<JoinChannelMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Null(payload.LatitudeDeg);
        Assert.Null(payload.LongitudeDeg);
        Assert.Null(payload.AltitudeMeters);
    }

    [Fact]
    public void CreateJoin_WithPosition_RoundTrips()
    {
        var msg = SignalingMessageFactory.CreateJoin(251000, lat: 36.2361, lon: -115.0342, alt: 580.0);
        var payload = SignalingMessageFactory.DeserializePayload<JoinChannelMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Equal(36.2361, payload.LatitudeDeg);
        Assert.Equal(-115.0342, payload.LongitudeDeg);
        Assert.Equal(580.0, payload.AltitudeMeters);
    }

    [Fact]
    public void CreateTransmission_NoTransmissionIdOrPosition_FieldsNull()
    {
        var msg = SignalingMessageFactory.CreateTransmission(251000, transmitting: true, is3d: true);
        var payload = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Null(payload.TransmissionId);
        Assert.Null(payload.LatitudeDeg);
    }

    [Fact]
    public void CreateTransmission_WithTransmissionIdAndPosition_RoundTrips()
    {
        var id = Guid.NewGuid();
        var msg = SignalingMessageFactory.CreateTransmission(251000, transmitting: true, is3d: true,
            transmissionId: id, lat: 34.6, lon: -115.85, alt: 300.0);
        var payload = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Equal(id.ToString("N"), payload.TransmissionId);
        Assert.Equal(34.6, payload.LatitudeDeg);
        Assert.Equal(-115.85, payload.LongitudeDeg);
        Assert.Equal(300.0, payload.AltitudeMeters);
    }

    [Fact]
    public void CreateTransmissionTranscript_AllFieldsPreserved()
    {
        var id = Guid.NewGuid();
        var words = new List<TranscriptWordDto>
        {
            new() { Text = "Nellis", StartSec = 0.0, EndSec = 0.3 },
            new() { Text = " Tower,", StartSec = 0.3, EndSec = 0.7 },
            new() { Text = " request", StartSec = 1.0, EndSec = 1.4 },
            new() { Text = " taxi", StartSec = 1.4, EndSec = 1.7 }
        };
        var msg = SignalingMessageFactory.CreateTransmissionTranscript(id, words, "en");

        Assert.Equal(SignalingMessageTypes.TransmissionTranscript, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<TransmissionTranscriptMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal(id.ToString("N"), payload.TransmissionId);
        Assert.Equal(4, payload.Words.Count);
        Assert.Equal("Nellis", payload.Words[0].Text);
        Assert.Equal(0.0, payload.Words[0].StartSec);
        Assert.Equal(1.7, payload.Words[3].EndSec);
        Assert.Equal("en", payload.Language);
    }

    [Fact]
    public void CreateTransmissionTranscript_NoLanguage_Null()
    {
        var msg = SignalingMessageFactory.CreateTransmissionTranscript(Guid.NewGuid(),
            [new TranscriptWordDto { Text = "checking in", StartSec = 0, EndSec = 0.5 }]);
        var payload = SignalingMessageFactory.DeserializePayload<TransmissionTranscriptMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Null(payload.Language);
    }

    [Fact]
    public void CreateTranscriptDelivery_AllFieldsPreserved()
    {
        var id = Guid.NewGuid();
        var inner = new TranscriptDeliveryMessage
        {
            TransmissionId = id.ToString("N"),
            FromPeerId = "peer-42",
            FromDisplayName = "Viper 1",
            FrequencyKhz = 251000,
            Text = "request taxi",
            Language = "en",
            FromLatitudeDeg = 36.2361,
            FromLongitudeDeg = -115.0342,
            FromAltitudeMeters = 580.0
        };

        var msg = SignalingMessageFactory.CreateTranscriptDelivery(inner);

        Assert.Equal(SignalingMessageTypes.TranscriptDelivery, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<TranscriptDeliveryMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal(id.ToString("N"), payload.TransmissionId);
        Assert.Equal("peer-42", payload.FromPeerId);
        Assert.Equal("Viper 1", payload.FromDisplayName);
        Assert.Equal(251000, payload.FrequencyKhz);
        Assert.Equal("request taxi", payload.Text);
        Assert.Equal("en", payload.Language);
        Assert.Equal(36.2361, payload.FromLatitudeDeg);
        Assert.Equal(-115.0342, payload.FromLongitudeDeg);
        Assert.Equal(580.0, payload.FromAltitudeMeters);
    }

    [Fact]
    public void CreateTranscriptDelivery_NoPosition_FieldsNull()
    {
        var msg = SignalingMessageFactory.CreateTranscriptDelivery(new TranscriptDeliveryMessage
        {
            TransmissionId = Guid.NewGuid().ToString("N"),
            FromPeerId = "peer-1",
            FromDisplayName = "Maverick",
            FrequencyKhz = 135100,
            Text = "wilco"
        });

        var payload = SignalingMessageFactory.DeserializePayload<TranscriptDeliveryMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Null(payload.FromLatitudeDeg);
        Assert.Null(payload.FromLongitudeDeg);
        Assert.Null(payload.FromAltitudeMeters);
    }
}
