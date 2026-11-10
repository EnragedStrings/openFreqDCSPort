using OpenFreq.Common;
using OpenFreqAudio;

namespace OpenFreq.Common.Tests;

public class FrequencyTransmissionTests
{
    [Fact]
    public void DefaultConstructor_DefaultsAreZeroAndNull()
    {
        var ft = new FrequencyTransmission();
        Assert.Equal(0, ft.Khz);
        Assert.Equal(0.0, ft.TxPowerWatts);
        Assert.Equal(0.0, ft.Ppm);
        Assert.Null(ft.Position);
        Assert.Null(ft.DcsPosition);
        Assert.Null(ft.Velocity);
        Assert.False(ft.In3d);
        Assert.Equal(AmbientNoiseType.None, ft.AmbientNoiseType);
    }

    [Fact]
    public void FullConstructor_SetsAllFields()
    {
        var pos = new Vector3(1, 2, 3);
        var vel = new Vector3(4, 5, 6);
        var dcsPos = new Vector3(7, 8, 9);

        var ft = new FrequencyTransmission(
            khz: 251000,
            txPowerWatts: 25.0,
            ppm: 1.5,
            position: pos,
            velocity: vel,
            dcsPosition: dcsPos,
            in3d: true);

        Assert.Equal(251000, ft.Khz);
        Assert.Equal(25.0, ft.TxPowerWatts);
        Assert.Equal(1.5, ft.Ppm);
        Assert.Same(pos, ft.Position);
        Assert.Same(dcsPos, ft.DcsPosition);
        Assert.Same(vel, ft.Velocity);
        Assert.True(ft.In3d);
        Assert.Equal(AmbientNoiseType.None, ft.AmbientNoiseType);
    }

    [Fact]
    public void Constructor_AmbientNoiseType_Preserved()
    {
        var ft = new FrequencyTransmission(135100, 10, 0, null, null, false, AmbientNoiseType.AirF16);
        Assert.Equal(AmbientNoiseType.AirF16, ft.AmbientNoiseType);
    }

    [Fact]
    public void Constructor_NullPositionAndVelocity_Allowed()
    {
        var ft = new FrequencyTransmission(243000, 5, 0, null, null, false);
        Assert.Null(ft.Position);
        Assert.Null(ft.Velocity);
    }

    [Fact]
    public void DefaultConstructor_EncryptionFieldsDefaultToClear()
    {
        var ft = new FrequencyTransmission();
        Assert.False(ft.Enc);
        Assert.Equal(0, ft.EncKey);
        Assert.False(ft.HqOn);
    }

    [Fact]
    public void Constructor_EncryptionFields_Preserved()
    {
        var ft = new FrequencyTransmission(251000, 10, 0, null, null, false,
            enc: true, encKey: 4, hqOn: true);
        Assert.True(ft.Enc);
        Assert.Equal(4, ft.EncKey);
        Assert.True(ft.HqOn);
    }

    [Fact]
    public void EccmIdentity_RoundTripsOnTheWire_AndIsOmittedWhenNull()
    {
        var metadata = new AudioPacketMetadata
        {
            ClientId = "a",
            Frequencies =
            [
                new FrequencyTransmission(237325, 10, 0, null, null, true, hqOn: true)
                {
                    Eccm = new OpenFreqAudio.Eccm.HopNetIdentity(OpenFreqAudio.Eccm.HopMode.HaveQuick, "A12.325",
                        "0123456789abcdef", true, -20)
                },
                new FrequencyTransmission(251000, 10, 0, null, null, true)
            ]
        };
        var json = System.Text.Json.JsonSerializer.Serialize(metadata, OpenFreqJsonContext.Default.AudioPacketMetadata);
        Assert.Contains("\"eccm\":{\"mode\":\"HaveQuick\",\"net\":\"A12.325\"", json);
        Assert.Equal(1, json.Split("\"eccm\"").Length - 1);

        var back = System.Text.Json.JsonSerializer.Deserialize(json, OpenFreqJsonContext.Default.AudioPacketMetadata)!;
        var id = back.Frequencies[0].Eccm!;
        Assert.Equal(OpenFreqAudio.Eccm.HopMode.HaveQuick, id.Mode);
        Assert.Equal("0123456789abcdef", id.KeyFingerprint);
        Assert.True(id.TodValid);
        Assert.Equal(-20, id.TodOffsetMs);
        Assert.Null(back.Frequencies[1].Eccm);

        // An older client's packet (no eccm) still parses, as single channel / legacy HqOn.
        var old = System.Text.Json.JsonSerializer.Deserialize("{\"id\":\"o\",\"frequencies\":[{\"khz\":251000,\"hqOn\":true}]}",
            OpenFreqJsonContext.Default.AudioPacketMetadata)!;
        Assert.Null(old.Frequencies[0].Eccm);
        Assert.True(old.Frequencies[0].HqOn);
    }
}
