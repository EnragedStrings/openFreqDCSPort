using System.Text.Json;
using OpenFreqServer.SrsBridge;

namespace OpenFreq.Server.Tests.SrsBridge;

/// <summary>
/// Wire-format fidelity tests for the SRS sync protocol DTOs. These exist because the DTOs must
/// produce/accept JSON byte-for-byte compatible with a real, unmodified SRS client -- a casing or
/// field-name mistake here would silently break interop rather than fail loudly, so we assert
/// against literal field names (not just "does it round-trip through our own serializer").
/// </summary>
public class SrsSyncProtocolTests
{
    private static SrsClient SampleClient()
    {
        var radios = SrsPlayerRadioInfo.CreateDefaultRadios();
        radios[1] = new SrsRadio
        {
            Freq = 251_000_000, Modulation = SrsModulation.AM, Enc = true, EncKey = 3,
            Name = "arc210", IntercomUnitId = 0
        };

        return new SrsClient
        {
            ClientGuid = "abc123",
            Name = "Pilot One",
            Coalition = 2,
            Seat = 0,
            LatLngPosition = new SrsLatLngPosition { Lat = 41.9274, Lng = 41.8604, Alt = 3000 },
            RadioInfo = new SrsPlayerRadioInfo { Unit = "A-10C_2", UnitId = 42, Radios = radios }
        };
    }

    [Fact]
    public void Serialize_UsesExactSrsWireFieldNames()
    {
        var message = new SrsNetworkMessage
        {
            MsgType = SrsNetworkMessage.MessageType.SYNC,
            Clients = [SampleClient()],
            Version = "2.3.8.2"
        };

        var json = JsonSerializer.Serialize(message, SrsJsonContext.Default.SrsNetworkMessage);

        // Top-level: PascalCase, matching NetworkMessage.cs exactly.
        Assert.Contains("\"MsgType\":2", json); // SYNC is ordinal 2
        Assert.Contains("\"Clients\":", json);
        Assert.Contains("\"Version\":\"2.3.8.2\"", json);

        // SRClientBase: PascalCase properties.
        Assert.Contains("\"ClientGuid\":\"abc123\"", json);
        Assert.Contains("\"Coalition\":2", json);

        // LatLngPosition: lowercase, matching the real class's lat/lng/alt member names.
        Assert.Contains("\"lat\":41.9274", json);
        Assert.Contains("\"lng\":41.8604", json);
        Assert.Contains("\"alt\":3000", json);

        // PlayerRadioInfoBase: lowercase field-style names.
        Assert.Contains("\"unit\":\"A-10C_2\"", json);
        Assert.Contains("\"unitId\":42", json);
        Assert.Contains("\"radios\":[", json);

        // RadioBase: mix of lowercase fields (enc/encKey/freq/modulation) and PascalCase
        // properties (Name/IntercomUnitId) -- matching the real mixed-style class exactly.
        Assert.Contains("\"enc\":true", json);
        Assert.Contains("\"encKey\":3", json);
        Assert.Contains("\"freq\":251000000", json);
        Assert.Contains("\"modulation\":0", json); // AM is ordinal 0
        Assert.Contains("\"Name\":\"arc210\"", json);
        Assert.Contains("\"IntercomUnitId\":0", json);
    }

    [Fact]
    public void Deserialize_RealSrsShapedSyncMessage_PopulatesAllFields()
    {
        // Modeled directly on what a real SRS client sends for MsgType.SYNC -- hand-written, not
        // round-tripped through our own serializer, so this catches a mismatch our own
        // serialize/deserialize pair could otherwise agree on while still diverging from SRS.
        const string json = """
            {"Client":{"ClientGuid":"real-guid-1234567890","Name":"Viper1","Coalition":2,"AllowRecord":false,"Seat":0,"RadioInfo":{"ambient":{"abType":"","vol":0.15},"iff":{"Mode1":-1,"Mode3":-1,"Mode4":false,"Status":0},"radios":[{"enc":false,"encKey":0,"freq":305000000.0,"modulation":0,"retransmit":false,"secFreq":1.0,"Name":"","Model":"","IntercomUnitId":0}],"unit":"F-16C_50","unitId":16777217},"LatLngPosition":{"lat":41.80139,"lng":41.75694,"alt":3048.0},"Gateway":false,"DISEntityId":-1,"GatewayClient":false},"MsgType":2,"Version":"2.3.8.2"}
            """;

        var message = JsonSerializer.Deserialize(json, SrsJsonContext.Default.SrsNetworkMessage);

        Assert.NotNull(message);
        Assert.Equal(SrsNetworkMessage.MessageType.SYNC, message!.MsgType);
        Assert.Equal("2.3.8.2", message.Version);

        var client = message.Client;
        Assert.NotNull(client);
        Assert.Equal("real-guid-1234567890", client!.ClientGuid);
        Assert.Equal("Viper1", client.Name);
        Assert.Equal(2, client.Coalition);

        Assert.NotNull(client.LatLngPosition);
        Assert.Equal(41.80139, client.LatLngPosition!.Lat, precision: 5);
        Assert.Equal(41.75694, client.LatLngPosition.Lng, precision: 5);
        Assert.Equal(3048.0, client.LatLngPosition.Alt, precision: 1);

        Assert.NotNull(client.RadioInfo);
        Assert.Equal("F-16C_50", client.RadioInfo!.Unit);
        Assert.Equal(16777217u, client.RadioInfo.UnitId);

        var radio = client.RadioInfo.Radios[0];
        Assert.Equal(305_000_000.0, radio.Freq, precision: 1);
        Assert.Equal(SrsModulation.AM, radio.Modulation);
        Assert.True(radio.IsTuned);
    }

    [Fact]
    public void ServerSettings_Defaults_KeepsDistanceEnabledTrue()
    {
        // Regression guard: the real SRS client (TCPClientHandler.cs) stops reporting its own
        // position at all when DISTANCE_ENABLED and LOS_ENABLED are both false, which puts it in
        // a "not really in game" state and (very plausibly) suppresses real audio processing --
        // see SrsServerSettings.cs's doc comment. This must stay true even if LOS_ENABLED stays
        // false.
        Assert.Equal("true", SrsServerSettings.Defaults["DISTANCE_ENABLED"]);
        Assert.Equal("false", SrsServerSettings.Defaults["LOS_ENABLED"]);
    }

    [Fact]
    public void ServerSettings_Build_ReflectsConfiguredExternalAwacsMode()
    {
        var disabled = SrsServerSettings.Build(new SrsBridgeConfig { ExternalAwacsModeEnabled = false });
        var enabled = SrsServerSettings.Build(new SrsBridgeConfig { ExternalAwacsModeEnabled = true });

        Assert.Equal("false", disabled["EXTERNAL_AWACS_MODE"]);
        Assert.Equal("true", enabled["EXTERNAL_AWACS_MODE"]);
    }

    [Fact]
    public void Radio_IsTuned_FalseWhenBelowThresholdOrDisabled()
    {
        var untuned = new SrsRadio { Freq = 1, Modulation = SrsModulation.AM };
        var disabled = new SrsRadio { Freq = 251_000_000, Modulation = SrsModulation.DISABLED };
        var tuned = new SrsRadio { Freq = 251_000_000, Modulation = SrsModulation.FM };

        Assert.False(untuned.IsTuned);
        Assert.False(disabled.IsTuned);
        Assert.True(tuned.IsTuned);
    }
}
