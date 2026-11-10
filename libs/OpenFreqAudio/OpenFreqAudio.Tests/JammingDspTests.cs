using System.Diagnostics;
using OpenFreqAudio.Jamming;
using Xunit.Abstractions;

namespace OpenFreqAudio.Tests;

/// <summary>
/// Jammers injected into the slot receiver (docs/jamming/JAMMING_DESIGN.md, research-01 §2, §4): the effects
/// should emerge from the receiver, not be scripted, so these measure the detected audio.
/// </summary>
public class JammingDspTests
{
    private const int Fs = SlotHarness.Fs;
    private const int AmKhz = 251000;
    private const int FmKhz = 40000;

    private readonly ITestOutputHelper _out;

    public JammingDspTests(ITestOutputHelper output) => _out = output;

    private static JammerContribution Jammer(string id, float jnrDb, JammerTechnique technique = JammerTechnique.Tone,
        float offsetHz = 0f, JammerWaveformParams? p = null, bool responsive = false, float reactionMs = 0f,
        float hopHitFraction = 1f) =>
        new(id, jnrDb, technique, offsetHz, p ?? new JammerWaveformParams { Seed = 7 }, responsive, reactionMs,
            hopHitFraction);

    /// <summary>Run a harness for <paramref name="seconds"/>, skipping the first 0.25 s (AGC settling).</summary>
    private static float[] RunSettled(SlotHarness h, double seconds, out float[] reference)
    {
        int total = (int)(seconds * Fs);
        var output = h.Run(total);
        int skip = Fs / 4;
        reference = SlotHarness.FilteredVoice(h.FrequencyKhz, total)[skip..];
        return output[skip..];
    }

    private static double Db(double ratio) => 20 * Math.Log10(ratio);

    // ---- AM ----

    [Fact]
    public void Am_ToneJammer_BeatsWithTheCarrierAtItsOffset()
    {
        // An unmodulated wanted carrier at 30 dB C/N and a CW jammer 10 dB under it, 1 kHz off: the envelope
        // detector hears the heterodyne whistle at |Δf| (research-01 §4.1).
        double Beat(float? jnr, double hz)
        {
            var h = new SlotHarness(AmKhz, noiseSeed: 21);
            h.Add(30);
            if (jnr is float j) h.Slot.Jammers = [Jammer("cw", j, offsetHz: 1000f)];
            var o = RunSettled(h, 1.0, out _);
            return SlotHarness.ToneAmplitude(o, hz);
        }

        double whistle = Beat(20f, 1000);
        double offTone = Beat(20f, 1370);
        double clean = Beat(null, 1000);
        _out.WriteLine($"whistle {whistle:F4}, elsewhere {offTone:F4}, without jammer {clean:F4}");
        Assert.True(whistle > 20 * offTone);
        Assert.True(whistle > 20 * clean);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(10f)]
    [InlineData(20f)]
    public void Am_StrongNoiseJammer_DropsTheVoiceByAboutJs(float jsDb)
    {
        // AM has no capture: the AGC follows the total envelope, so the wanted voice falls by about J/S once
        // the jammer dominates (research-01 §4.4). Below the envelope detector's threshold the voice falls as
        // CNR² on top of that (research-01 §2.2), so a buried voice drops by up to about twice J/S.
        const float snr = 30f;
        double VoiceLevel(float? js)
        {
            var h = new SlotHarness(AmKhz, noiseSeed: 22);
            h.Add(snr, voice: SlotHarness.Voice);
            if (js is float j) h.Slot.Jammers = [Jammer("noise", snr + j, JammerTechnique.Noise)];
            var o = RunSettled(h, 2.0, out var reference);
            return SlotHarness.VoiceLevel(o, reference);
        }

        double drop = Db(VoiceLevel(null) / VoiceLevel(jsDb));
        double expected = 10 * Math.Log10(1 + Math.Pow(10, jsDb / 10)); // AGC normalizing to S + J
        _out.WriteLine($"J/S {jsDb} dB: voice down {drop:F1} dB (AGC alone predicts {expected:F1})");
        Assert.True(drop >= expected - 2, $"voice only fell {drop:F1} dB");
        Assert.True(drop <= expected + jsDb + 8, $"voice fell {drop:F1} dB");
    }

    [Fact]
    public void Am_WeakToneJammer_IsAudibleUnderTheVoice()
    {
        // No capture on AM: even 20 dB down, a CW jammer is a whistle under an intelligible voice.
        var h = new SlotHarness(AmKhz, noiseSeed: 23);
        h.Add(30, voice: SlotHarness.Voice);
        h.Slot.Jammers = [Jammer("cw", 10f, offsetHz: 1500f)];
        var o = RunSettled(h, 1.0, out var reference);

        var clean = new SlotHarness(AmKhz, noiseSeed: 23);
        clean.Add(30, voice: SlotHarness.Voice);
        var c = RunSettled(clean, 1.0, out _);

        double whistle = SlotHarness.ToneAmplitude(o, 1500), baseline = SlotHarness.ToneAmplitude(c, 1500);
        double corr = SlotHarness.Correlation(o, reference);
        _out.WriteLine($"whistle {whistle:F4} vs {baseline:F4} clean; voice correlation {corr:F3}");
        Assert.True(whistle > 5 * baseline);
        Assert.True(corr > 0.9);
    }

    // ---- Squelch ----

    [Theory]
    [InlineData(JammerTechnique.Tone, 10f, true)]
    [InlineData(JammerTechnique.Noise, 10f, true)]
    [InlineData(JammerTechnique.Tone, 0f, false)]
    [InlineData(JammerTechnique.Noise, 0f, false)]
    public void Squelch_OpensOnAJammerAloneAboveThreshold(JammerTechnique technique, float jnrDb, bool opens)
    {
        // Carrier squelch gates on IF power: a jammer over the +6 dB threshold holds it open with nobody
        // transmitting (research-01 §4.5); one under it doesn't.
        var h = new SlotHarness(127000, noiseSeed: 24);
        h.Slot.Jammers = [Jammer("j", jnrDb, technique, 700f)];
        var o = h.Run(Fs);
        int openAfter = Fs / 10;
        double rms = SlotHarness.Rms(o.AsSpan(openAfter));
        _out.WriteLine($"{technique} {jnrDb} dB: squelch {(h.Slot.SquelchOpen ? "open" : "closed")}, rms {rms:F4}");
        Assert.Equal(opens, h.Slot.SquelchOpen);
        if (!opens) Assert.Equal(0.0, rms);
        if (opens && technique == JammerTechnique.Noise) Assert.True(rms > 0.01, "a noise jammer alone is heard");
    }

    // ---- FM ----

    [Fact]
    public void Fm_CwJammerOverTheVoice_CapturesSilently_UnderIt_IsSuppressed()
    {
        const float snr = 20f;
        (double level, double corr, double rms) Run(float? js)
        {
            var h = new SlotHarness(FmKhz, noiseSeed: 25);
            h.Add(snr, voice: SlotHarness.Voice);
            if (js is float j) h.Slot.Jammers = [Jammer("cw", snr + j, offsetHz: 800f)];
            var o = RunSettled(h, 2.0, out var reference);
            return (SlotHarness.VoiceLevel(o, reference), SlotHarness.Correlation(o, reference), SlotHarness.Rms(o));
        }

        var clean = Run(null);
        var over = Run(6f);
        var under = Run(-6f);
        _out.WriteLine($"clean: voice {clean.level:F4} corr {clean.corr:F3} rms {clean.rms:F4}");
        _out.WriteLine($"+6 dB: voice {over.level:F4} corr {over.corr:F3} rms {over.rms:F4}");
        _out.WriteLine($"-6 dB: voice {under.level:F4} corr {under.corr:F3} rms {under.rms:F4}");

        // Captured: the voice is gone and so is nearly everything else ("subtle jamming").
        Assert.True(over.level < 0.05 * clean.level, "voice should vanish under a captured carrier");
        Assert.True(over.rms < 0.1 * clean.rms, "a captured CW jammer should be near-silent");
        // Suppressed: the voice is essentially untouched.
        Assert.True(under.corr > 0.95);
        Assert.True(under.level > 0.8 * clean.level);
    }

    [Fact]
    public void Fm_NoiseJammer_DegradesThenDenies()
    {
        // research-01 §2.3: noise J/S ≈ −6 dB degrades, ≥ 0 dB denies, well under that it's clean.
        const float snr = 30f;
        double Corr(float? js)
        {
            var h = new SlotHarness(FmKhz, noiseSeed: 26);
            h.Add(snr, voice: SlotHarness.Voice);
            if (js is float j) h.Slot.Jammers = [Jammer("noise", snr + j, JammerTechnique.Noise)];
            var o = RunSettled(h, 2.0, out var reference);
            return SlotHarness.Correlation(o, reference);
        }

        double clean = Corr(null), weak = Corr(-20f), degraded = Corr(-6f), denied = Corr(3f);
        _out.WriteLine($"voice correlation: clean {clean:F3}, -20 dB {weak:F3}, -6 dB {degraded:F3}, +3 dB {denied:F3}");
        Assert.True(weak > 0.95);
        Assert.True(degraded < weak - 0.1);
        Assert.True(denied < 0.3);
    }

    // ---- Gating ----

    private static float[] RenderPower(JammerContribution c, int samples, Func<int, bool>? wanted = null, int block = 480,
        JammerWaveform? w = null)
    {
        w ??= new JammerWaveform(c.JammerId, c.Params.Seed);
        var power = new float[samples];
        var i = new float[block];
        var q = new float[block];
        var bp = new float[block / SlotReceiver.CaptureBlockSamples + 1];
        for (int at = 0; at < samples; at += block)
        {
            int len = Math.Min(block, samples - at);
            w.Render(c, wanted?.Invoke(at) ?? false, i.AsSpan(0, len), q.AsSpan(0, len),
                bp.AsSpan(0, (len + SlotReceiver.CaptureBlockSamples - 1) / SlotReceiver.CaptureBlockSamples),
                SlotReceiver.CaptureBlockSamples);
            for (int n = 0; n < len; ++n) power[at + n] = i[n] * i[n] + q[n] * q[n];
        }
        return power;
    }

    [Fact]
    public void LookThrough_SilencesTheConfiguredDuty_WithSmoothEdges()
    {
        var p = new JammerWaveformParams { Seed = 1, LookThroughPeriodMs = 100f, LookThroughDuty = 0.2f };
        var c = Jammer("lt", 0f, p: p);
        var power = RenderPower(c, Fs * 2);
        // Amplitude 1 (0 dB): count samples below half amplitude.
        int off = power.Skip(Fs / 10).Count(x => MathF.Sqrt(x) < 0.5f);
        double measured = off / (double)(power.Length - Fs / 10);
        double maxStep = 0;
        for (int n = 1; n < power.Length; ++n)
            maxStep = Math.Max(maxStep, Math.Abs(Math.Sqrt(power[n]) - Math.Sqrt(power[n - 1])));
        _out.WriteLine($"silent fraction {measured:F4}, largest per-sample amplitude step {maxStep:F4}");
        Assert.InRange(measured, 0.19, 0.21);
        // A 2 ms raised cosine moves at most π/(2·96) per sample: no click.
        Assert.True(maxStep < 0.03);
    }

    [Fact]
    public void Responsive_SilentWithoutASignal_OnAfterTheReactionDelay_OffWhenItEnds()
    {
        var c = Jammer("resp", 0f, responsive: true, reactionMs: 150f);
        // Wanted signal from 0.5 s to 1.5 s.
        var power = RenderPower(c, 2 * Fs, at => at >= Fs / 2 && at < 3 * Fs / 2);
        double Mean(double from, double to) => power.AsSpan((int)(from * Fs), (int)((to - from) * Fs)).ToArray().Average();

        double before = Mean(0, 0.5), reacting = Mean(0.51, 0.64), on = Mean(0.66, 1.49), after = Mean(1.51, 2.0);
        _out.WriteLine($"before {before:F4}, reacting {reacting:F4}, on {on:F4}, after {after:F4}");
        Assert.Equal(0, before);
        Assert.Equal(0, reacting);
        Assert.InRange(on, 0.95, 1.05);
        Assert.Equal(0, after);

        // Onset within a few ms of 0.65 s.
        int first = Array.FindIndex(power, x => x > 0.25f);
        Assert.InRange(first, (int)(0.65 * Fs), (int)(0.655 * Fs));
    }

    [Fact]
    public void Responsive_KeysOnTheSlotsWantedTransmission()
    {
        // Through the receiver: no transmitter, no jamming (the slot stays squelched)...
        var h = new SlotHarness(AmKhz, noiseSeed: 27);
        var tx = h.Add(25, voice: SlotHarness.Voice);
        tx.Active = false;
        h.Slot.Jammers = [Jammer("resp", 30f, JammerTechnique.Noise, responsive: true, reactionMs: 50f)];
        var idle = h.Run(Fs / 2);
        Assert.Equal(0.0, SlotHarness.Rms(idle));
        Assert.False(h.Slot.SquelchOpen);

        // ...and once someone transmits, the jammer comes up and buries them.
        tx.Active = true;
        var jammed = h.Run(Fs);
        var reference = SlotHarness.FilteredVoice(AmKhz, Fs);
        double early = SlotHarness.Correlation(jammed.AsSpan(480, 1440), reference.AsSpan(480, 1440));
        double late = SlotHarness.Correlation(jammed.AsSpan(Fs / 2), reference.AsSpan(Fs / 2));
        _out.WriteLine($"voice correlation in the first 30 ms {early:F3}, after {late:F3}");
        Assert.True(late < 0.3);
    }

    [Fact]
    public void HopHitFraction_MatchesTheMeasuredBlockHitRate()
    {
        var c = Jammer("hop", 0f, hopHitFraction: 0.3f);
        int blocks = 3000;
        var power = RenderPower(c, blocks * JammerWaveform.HopBlockSamples);
        int hits = 0;
        for (int b = 0; b < blocks; ++b)
            if (power[b * JammerWaveform.HopBlockSamples + JammerWaveform.HopBlockSamples / 2] > 0.5f) hits++;
        double rate = hits / (double)blocks;
        _out.WriteLine($"hit rate {rate:F4} over {blocks} hops");
        Assert.InRange(rate, 0.27, 0.33);

        // Deterministic: the same seed and id hit the same hops.
        var again = RenderPower(c, blocks * JammerWaveform.HopBlockSamples);
        Assert.Equal(power, again);
    }

    [Fact]
    public void SameJammerId_ContinuesAcrossInterferenceUpdates()
    {
        // SetSlotInterference replaces RadioConfig.Jammers with a new list every physics tick. The same id
        // must carry on without resetting its phase or PRNG: the output is bit-identical to never updating.
        float[] Run(bool replaceEveryCallback, string idAfterFirst = "j1")
        {
            var h = new SlotHarness(AmKhz, noiseSeed: 28);
            h.Add(25, voice: SlotHarness.Voice);
            var p = new JammerWaveformParams { Seed = 3 };
            h.Slot.Jammers = [new JammerContribution("j1", 20f, JammerTechnique.Noise, 300f, p)];
            var output = new float[Fs / 2];
            for (int at = 0; at < output.Length; at += 480)
            {
                h.Step(output.AsSpan(at, 480));
                if (replaceEveryCallback)
                    h.Slot.Jammers = [new JammerContribution(idAfterFirst, 20f, JammerTechnique.Noise, 300f, p with { })];
            }
            return output;
        }

        var steady = Run(false);
        Assert.Equal(steady, Run(true));
        Assert.NotEqual(steady, Run(true, "j2"));
    }

    [Fact]
    public void NonFiniteJammerState_ResetsTheSlot_AndRecovers()
    {
        var h = new SlotHarness(AmKhz, noiseSeed: 29);
        h.Add(25, voice: SlotHarness.Voice);
        h.Slot.Jammers = [Jammer("j", 20f, JammerTechnique.Tone, 500f)];
        var buffer = new float[480];
        for (int i = 0; i < 20; ++i) h.Step(buffer);

        h.Slot.JammerStates["j"].CarrierPhase = double.NaN;
        var r = h.Step(buffer);
        Assert.True(r.WentNonFinite);
        Assert.All(buffer, x => Assert.Equal(0f, x));
        Assert.Empty(h.Slot.JammerStates);

        for (int i = 0; i < 20; ++i) h.Step(buffer);
        Assert.All(buffer, x => Assert.True(float.IsFinite(x)));
        Assert.True(SlotHarness.Rms(buffer) > 0.01);
        Assert.True(h.Slot.JammerStates["j"].IsFinite);

        // A non-finite contribution from physics never gets that far.
        h.Slot.Jammers = [Jammer("j", float.NaN, JammerTechnique.Tone, float.PositiveInfinity)];
        for (int i = 0; i < 5; ++i)
        {
            r = h.Step(buffer);
            Assert.False(r.WentNonFinite);
        }
    }

    [Fact]
    public void SatcomSlot_IgnoresJammers()
    {
        var h = new SlotHarness(AmKhz, noiseSeed: 30);
        h.Slot.IsSatcomActive = true;
        h.Slot.Jammers = [Jammer("j", 40f, JammerTechnique.Noise)];
        var o = h.Run(Fs / 4);
        Assert.Empty(h.Slot.JammerStates);
        Assert.All(o, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void JammerThatLeavesTheList_IsForgotten()
    {
        var h = new SlotHarness(AmKhz, noiseSeed: 31);
        h.Slot.Jammers = [Jammer("a", 10f), Jammer("b", 10f)];
        h.Run(960);
        Assert.Equal(2, h.Slot.JammerStates.Count);
        h.Slot.Jammers = [Jammer("b", 10f)];
        h.Run(480);
        Assert.Equal(["b"], h.Slot.JammerStates.Keys);
        h.Slot.Jammers = [];
        h.Run(480);
        Assert.Empty(h.Slot.JammerStates);
    }

    // ---- Waveforms ----

    public static IEnumerable<object[]> Techniques() =>
        Enum.GetValues<JammerTechnique>().Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Techniques))]
    public void EveryTechnique_HasUnitAveragePower_AndIsDeterministic(JammerTechnique technique)
    {
        // JnrDb means average in-passband power over the unit noise, whatever the waveform.
        var p = new JammerWaveformParams { Seed = 5, ToneOffsetsHz = technique == JammerTechnique.MultiTone ? [-1500f, 200f, 1700f] : [] };
        var c = Jammer("w", 0f, technique, 0f, p);
        int samples = 20 * Fs;
        var power = RenderPower(c, samples);
        double mean = power.Skip(Fs / 10).Average(x => (double)x);
        _out.WriteLine($"{technique}: mean power {10 * Math.Log10(mean):F2} dB");
        Assert.InRange(10 * Math.Log10(mean), -1.0, 1.0);
        Assert.Equal(power.AsSpan(0, Fs).ToArray(), RenderPower(c, Fs));
    }

    [Theory]
    [InlineData(JammerTechnique.SteppedTones)]
    [InlineData(JammerTechnique.Swept)]
    [InlineData(JammerTechnique.Gulls)]
    [InlineData(JammerTechnique.Spark)]
    [InlineData(JammerTechnique.RandomPulse)]
    [InlineData(JammerTechnique.Recorded)]
    [InlineData(JammerTechnique.Noise)]
    public void AudibleTechniques_AreHeardAloneOnAm(JammerTechnique technique)
    {
        // With nobody transmitting, the doctrinal sounds are heard on their own (a bare CW is not: its
        // envelope is flat, which is exactly FM 24-33's point about unmodulated jamming).
        var h = new SlotHarness(AmKhz, noiseSeed: 32);
        h.Slot.Jammers = [Jammer("j", 25f, technique, 400f)];
        var o = h.Run(Fs);
        double rms = SlotHarness.Rms(o.AsSpan(Fs / 4));
        _out.WriteLine($"{technique}: rms {rms:F4}");
        Assert.True(rms > 0.02);
    }

    [Fact]
    public void Tone_AloneOnAm_IsAQuietCarrier()
    {
        // A dead carrier holds the squelch open and quiets the receiver: the AGC turns the hiss down by its C/N.
        double Rms(JammerTechnique technique, out bool open)
        {
            var h = new SlotHarness(AmKhz, noiseSeed: 33);
            h.Slot.Jammers = [Jammer("j", 25f, technique, 400f)];
            var o = h.Run(Fs);
            open = h.Slot.SquelchOpen;
            return SlotHarness.Rms(o.AsSpan(Fs / 4));
        }

        double tone = Rms(JammerTechnique.Tone, out bool open), noise = Rms(JammerTechnique.Noise, out _);
        _out.WriteLine($"tone alone: rms {tone:F4}; noise jammer at the same level {noise:F4}");
        Assert.True(open);
        Assert.True(tone < 0.25 * noise);
    }

    [Fact]
    public void SteppedTones_StepThroughTheirPitches()
    {
        var p = new JammerWaveformParams { Seed = 1, ToneOffsetsHz = [600f, 1200f], StepIntervalMs = 200f };
        var h = new SlotHarness(AmKhz, noiseSeed: 34);
        h.Slot.Jammers = [Jammer("bag", 25f, JammerTechnique.SteppedTones, 0f, p)];
        var o = h.Run(Fs);
        // Steps: 0-200 ms 600 Hz, 200-400 ms 1200 Hz, 400-600 ms 600 Hz...
        var first = o.AsSpan((int)(0.45 * Fs), (int)(0.1 * Fs));
        var second = o.AsSpan((int)(0.65 * Fs), (int)(0.1 * Fs));
        Assert.True(SlotHarness.ToneAmplitude(first, 600) > 5 * SlotHarness.ToneAmplitude(first, 1200));
        Assert.True(SlotHarness.ToneAmplitude(second, 1200) > 5 * SlotHarness.ToneAmplitude(second, 600));
    }

    [Fact]
    public void NoJammers_LeavesTheGoldenOutputAlone()
    {
        // An empty list is the no-jammer path exactly (SlotReceiverGoldenTests pins its numbers).
        var a = SlotReceiverGoldenTests.Build("fm-capture");
        var b = SlotReceiverGoldenTests.Build("fm-capture");
        b.Slot.Jammers = [];
        Assert.Equal(a.Run(Fs / 2), b.Run(Fs / 2));
    }

    // ---- Performance ----

    [Fact]
    public void FourJammersOnFourSlots_RunWellUnderRealTime()
    {
        var slots = new List<SlotHarness>();
        for (int s = 0; s < 4; ++s)
        {
            var h = new SlotHarness(s % 2 == 0 ? AmKhz : FmKhz, noiseSeed: 40 + s);
            h.Add(20, voice: SlotHarness.Voice);
            var tones = new JammerWaveformParams { Seed = s, ToneOffsetsHz = [-900f, 300f, 1300f] };
            h.Slot.Jammers =
            [
                Jammer("noise", 15f, JammerTechnique.Noise),
                Jammer("babble", 12f, JammerTechnique.Recorded, 250f),
                Jammer("comb", 10f, JammerTechnique.MultiTone, 0f, tones),
                Jammer("pulse", 18f, JammerTechnique.RandomPulse, p: new JammerWaveformParams { Seed = s, LookThroughPeriodMs = 200f, LookThroughDuty = 0.1f }),
            ];
            slots.Add(h);
        }

        var block = new float[480]; // 10 ms
        double Time()
        {
            foreach (var h in slots)
                for (int i = 0; i < 20; ++i) h.Step(block); // warm up (JIT, filter creation)

            const int blocks = 300; // 3 s of audio
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < blocks; ++i)
                foreach (var h in slots) h.Step(block);
            return sw.Elapsed.TotalMilliseconds / blocks;
        }

        double perBlockMs = Time();
        var saved = slots.Select(h => h.Slot.Jammers).ToList();
        foreach (var h in slots) h.Slot.Jammers = [];
        double baselineMs = Time();
        _out.WriteLine($"4 slots x 4 jammers: {perBlockMs:F3} ms per 10 ms block ({perBlockMs * 10:F1}% of real time); " +
                       $"same slots without jammers {baselineMs:F3} ms");
        // Loose, for slow shared CI machines and Debug builds: ~1-2 ms here on a desktop.
        Assert.True(perBlockMs < 8.0, $"{perBlockMs:F2} ms per 10 ms block");
        GC.KeepAlive(saved);
    }
}
