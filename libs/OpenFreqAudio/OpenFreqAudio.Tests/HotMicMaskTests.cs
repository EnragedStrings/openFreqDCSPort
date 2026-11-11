using Microsoft.Extensions.Logging.Abstractions;

namespace OpenFreqAudio.Tests;

/// <summary>
/// HUD-tape hot mic and own voice in the capture: the pilot's own mask, no radio effects.
/// </summary>
public class HotMicMaskTests
{
    private const int Rate = 48000;

    private static float[] Sine(double hz, int n, float amp = 0.3f)
    {
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = amp * (float)Math.Sin(2 * Math.PI * hz * i / Rate);
        return s;
    }

    private static double Rms(float[] s, int from)
    {
        double sum = 0;
        for (int i = from; i < s.Length; i++) sum += s[i] * (double)s[i];
        return Math.Sqrt(sum / (s.Length - from));
    }

    public static TheoryData<AmbientNoiseType> AllTypes()
    {
        var data = new TheoryData<AmbientNoiseType>();
        foreach (var t in Enum.GetValues<AmbientNoiseType>()) data.Add(t);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void MicOnly_AddsNoCockpitAmbience(AmbientNoiseType type)
    {
        var fx = AmbientNoiseEffectFactory.Create(type, Rate);
        var buf = new float[Rate / 2];
        fx.ApplyMicOnly(buf, 0, buf.Length, 1f);
        Assert.All(buf, s => Assert.Equal(0f, s));
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void MicOnly_AtZeroVolume_IsTheDryMic(AmbientNoiseType type)
    {
        var fx = AmbientNoiseEffectFactory.Create(type, Rate);
        var dry = Sine(1000, 4800);
        var buf = (float[])dry.Clone();
        fx.ApplyMicOnly(buf, 0, buf.Length, 0f);
        Assert.Equal(dry, buf);
    }

    [Fact]
    public void MicOnly_A10_MufflesTheHighsLikeTheMask()
    {
        double Gain(double hz)
        {
            var fx = AmbientNoiseEffectFactory.Create(AmbientNoiseType.AirA10, Rate);
            var buf = Sine(hz, Rate / 2, 0.05f); // below the ALC knee: linear, so the gains compare filters
            fx.ApplyMicOnly(buf, 0, buf.Length, 1f);
            return Rms(buf, Rate / 10) / Rms(Sine(hz, Rate / 2, 0.05f), Rate / 10);
        }

        Assert.True(Gain(6000) < Gain(300) * 0.6, $"6 kHz {Gain(6000):F3} vs 300 Hz {Gain(300):F3}");
    }

    [Fact]
    public void OwnVoice_HasNoRadioBandPass()
    {
        // With no ambient type, own voice is the mic untouched: a 100 Hz tone (below the old 300 Hz radio high-pass)
        // and a 5 kHz tone (above the old band edge) both pass at full level.
        foreach (var hz in new[] { 100.0, 5000.0 })
        {
            var r = new OwnVoiceRadioRenderer(Rate, NullLogger.Instance) { AmbientNoise = AmbientNoiseType.None };
            var dry = Sine(hz, Rate / 4);
            var buf = (float[])dry.Clone();
            r.Process(buf, buf.Length, 1f);
            Assert.Equal(dry, buf);
        }
    }
}
