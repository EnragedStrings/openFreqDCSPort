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
}
