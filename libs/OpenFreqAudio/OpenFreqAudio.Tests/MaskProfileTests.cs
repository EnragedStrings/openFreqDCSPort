using Microsoft.Extensions.Logging.Abstractions;

namespace OpenFreqAudio.Tests;

/// <summary>Mask profiles: 1 = measured mask (local own voice / hot mic), 2 = classic (everything else).</summary>
public class MaskProfileTests
{
    private const int Rate = 48000;

    private static readonly AmbientNoiseType[] Jets =
        [AmbientNoiseType.AirA10, AmbientNoiseType.AirF16, AmbientNoiseType.AirF15, AmbientNoiseType.AirGeneric];

    private static float[] Voice(int n, float amp = 0.3f)
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
            s[i] = amp * (float)(0.6 * Math.Sin(2 * Math.PI * 180 * i / Rate) + 0.3 * Math.Sin(2 * Math.PI * 1100 * i / Rate)
                                 + 0.1 * Math.Sin(2 * Math.PI * 3300 * i / Rate));
        return s;
    }

    private static double Gain(float[] buf, float amp, int from)
    {
        double sum = 0;
        for (int i = from; i < buf.Length; i++) sum += buf[i] * (double)buf[i];
        return 20 * Math.Log10(Math.Sqrt(sum / (buf.Length - from)) / (amp / Math.Sqrt(2)));
    }

    public static TheoryData<AmbientNoiseType> AllTypes()
    {
        var d = new TheoryData<AmbientNoiseType>();
        foreach (var t in Enum.GetValues<AmbientNoiseType>()) d.Add(t);
        return d;
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void ClassicProfile_IsExactlyTheSoundBeforeProfiles(AmbientNoiseType type)
    {
        var a = Voice(Rate / 4); var b = (float[])a.Clone();
        AmbientNoiseEffectFactory.Create(type, Rate).ApplyPreFade(a, 0, a.Length, 1f);
        AmbientNoiseEffectFactory.Create(type, Rate, mask: MaskProfile.Classic).ApplyPreFade(b, 0, b.Length, 1f);
        Assert.Equal(a, b);
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void MeasuredProfile_ChangesOnlyTheJetTypes(AmbientNoiseType type)
    {
        var classic = Voice(Rate / 4); var measured = (float[])classic.Clone();
        AmbientNoiseEffectFactory.Create(type, Rate).ApplyMicOnly(classic, 0, classic.Length, 1f);
        AmbientNoiseEffectFactory.Create(type, Rate, mask: MaskProfile.Measured).ApplyMicOnly(measured, 0, measured.Length, 1f);
        if (Jets.Contains(type)) Assert.NotEqual(classic, measured);
        else Assert.Equal(classic, measured);
    }

    [Fact]
    public void IncomingRadioAudio_KeepsTheClassicMask()
    {
        var fx = new RadioEffect(Rate, 1, NullLogger.Instance);
        Assert.Equal(MaskProfile.Classic, fx.MaskProfile);
    }

    [Fact]
    public void MeasuredFilter_HasTheChosenShape()
    {
        // Small level, so the tanh soft limit is linear. Expected from the design (clip r3_5 chain + -12.2 dB makeup):
        // +3.9 dB at 500 Hz (the cavity resonance), about -43 dB at 6 kHz (past the 5.2 kHz intercom cut).
        double At(double hz)
        {
            var f = new MeasuredMaskFilter(Rate);
            const float amp = 0.001f;
            var buf = new float[Rate / 2];
            for (int i = 0; i < buf.Length; i++) buf[i] = f.Process(amp * (float)Math.Sin(2 * Math.PI * hz * i / Rate));
            return Gain(buf, amp, Rate / 10);
        }

        Assert.InRange(At(500), 3.9 - 1.0, 3.9 + 1.0);
        Assert.InRange(At(6000), -43.0 - 2.0, -43.0 + 2.0);
        Assert.True(At(500) - At(1400) > 10, "the 1.3-1.5 kHz notch below the 500 Hz resonance");
    }

    [Fact]
    public void MeasuredFilter_StaysUnderFullScale_AndIsSilentOnSilence()
    {
        var f = new MeasuredMaskFilter(Rate);
        var loud = Voice(Rate, amp: 1f);
        foreach (var s in loud) Assert.InRange(f.Process(s), -1f, 1f);

        var g = new MeasuredMaskFilter(Rate);
        for (int i = 0; i < 1000; i++) Assert.Equal(0f, g.Process(0f));
    }

    [Fact]
    public void A10Ambience_HasNoN1N2EngineWhine()
    {
        // The 480 Hz (N1) and 750 Hz (N2) engine tones were removed from the A-10C everywhere; the inverter stays.
        var bed = new float[Rate * 4];
        AmbientNoiseEffectFactory.Create(AmbientNoiseType.AirA10, Rate).ApplyPreFade(bed, 0, bed.Length, 1f);

        double Power(double hz)
        {
            double w = 2 * Math.PI * hz / Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = Rate; i < bed.Length; i++) { double s0 = bed[i] + c * s1 - s2; s2 = s1; s1 = s0; }
            return s1 * s1 + s2 * s2 - c * s1 * s2;
        }

        double Db(double a, double b) => 10 * Math.Log10(a / b);
        // A tone stands far above its neighbours; noise doesn't.
        Assert.True(Db(Power(480), (Power(455) + Power(505)) / 2) < 6, "480 Hz tone present");
        Assert.True(Db(Power(750), (Power(725) + Power(775)) / 2) < 6, "750 Hz tone present");
        Assert.True(Db(Power(398.4), (Power(370) + Power(430)) / 2) > 20, "the 400 Hz inverter whine is still there");
    }
}
