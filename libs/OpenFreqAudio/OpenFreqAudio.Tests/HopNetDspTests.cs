using OpenFreqAudio.Eccm;
using OpenFreqAudio.Jamming;
using Xunit.Abstractions;

namespace OpenFreqAudio.Tests;

/// <summary>
/// HAVE QUICK / SINCGARS in the receiver (docs/jamming/HAVE_QUICK.md): the TRANSEC gate on net identity, and
/// jammer hop hits driven by the net's shared hop clock.
/// </summary>
public class HopNetDspTests
{
    private const int Fs = SlotHarness.Fs;
    private const int UhfKhz = 237_325;

    private readonly ITestOutputHelper _out;

    public HopNetDspTests(ITestOutputHelper output) => _out = output;

    private static HopNetIdentity Net(string key = "k1", long tod = 0, bool todOk = true) =>
        new(HopMode.HaveQuick, "A12.325", key, todOk, tod);

    private static HopSlotState Slot(HopNetIdentity id) => new(id, 100, 50);

    /// <summary>Voice correlation of a slot hearing one talker with the given identities.</summary>
    private static double Heard(HopSlotState? rx, bool rxHq, HopNetIdentity? tx, bool txHq)
    {
        var h = new SlotHarness(UhfKhz, noiseSeed: 5);
        h.Slot.HopNet = rx;
        h.Slot.HqOn = rxHq;
        var t = h.Add(25, voice: SlotHarness.Voice);
        t.Carrier.Eccm = tx;
        t.Carrier.HqOn = txHq;
        var o = h.Run(Fs / 2);
        var reference = SlotHarness.FilteredVoice(UhfKhz, Fs / 2);
        return SlotHarness.Correlation(o.AsSpan(Fs / 8), reference.AsSpan(Fs / 8));
    }

    [Fact]
    public void Gate_SameNetWodAndTod_IsHeard() =>
        Assert.True(Heard(Slot(Net()), true, Net(), true) > 0.8);

    [Fact]
    public void Gate_WrongWod_IsNotHeard() =>
        Assert.True(Heard(Slot(Net()), true, Net("k2"), true) < 0.2);

    [Fact]
    public void Gate_TodOutsideTheSyncWindow_IsNotHeard()
    {
        Assert.True(Heard(Slot(Net()), true, Net(tod: 40), true) > 0.8);
        Assert.True(Heard(Slot(Net()), true, Net(tod: 400), true) < 0.2);
        Assert.True(Heard(Slot(Net()), true, Net(todOk: false), true) < 0.2);
    }

    [Fact]
    public void Gate_SingleChannelAndHopping_DoNotMeet()
    {
        Assert.True(Heard(null, false, Net(), true) < 0.2);
        Assert.True(Heard(Slot(Net()), true, null, false) < 0.2);
        Assert.True(Heard(null, false, null, false) > 0.8);
    }

    // ---- shared hop clock ----

    private static (float[] Power, double StartHop) RenderOnClock(JammerContribution c, double startSeconds,
        int samples, ulong seed, double rate = 100)
    {
        var w = new JammerWaveform(c.JammerId, c.Params.Seed);
        var power = new float[samples];
        const int block = 480;
        var i = new float[block];
        var q = new float[block];
        var bp = new float[block / SlotReceiver.CaptureBlockSamples + 1];
        for (int at = 0; at < samples; at += block)
        {
            int len = Math.Min(block, samples - at);
            var timing = new HopTiming(seed, (startSeconds + (double)at / Fs) * rate, rate / Fs);
            w.Render(c, false, i.AsSpan(0, len), q.AsSpan(0, len),
                bp.AsSpan(0, (len + SlotReceiver.CaptureBlockSamples - 1) / SlotReceiver.CaptureBlockSamples),
                SlotReceiver.CaptureBlockSamples, timing);
            for (int n = 0; n < len; ++n) power[at + n] = i[n] * i[n] + q[n] * q[n];
        }
        return (power, startSeconds * rate);
    }

    /// <summary>Hit/miss per hop, sampled mid-hop. Hop k of the result is absolute hop index startHop + k.</summary>
    private static bool[] HopHits(float[] power, int hopSamples = 480) =>
        Enumerable.Range(0, power.Length / hopSamples).Select(k => power[k * hopSamples + hopSamples / 2] > 0.5f).ToArray();

    [Fact]
    public void SharedHopClock_TwoListenersHearTheSameHits()
    {
        var c = new JammerContribution("pbj", 0f, JammerTechnique.Tone, 0f, new JammerWaveformParams { Seed = 9 },
            HopHitFraction: 0.3f);
        ulong seed = Net().Seed;
        // Listener A from t = 1000 s, listener B joins 2.5 s later (its own waveform state, its own start).
        var (a, _) = RenderOnClock(c, 1000.0, Fs * 6, seed);
        var (b, _) = RenderOnClock(c, 1002.5, Fs * 3, seed);
        var hitsA = HopHits(a).Skip(250).Take(300).ToArray();
        var hitsB = HopHits(b).Take(300).ToArray();
        Assert.Equal(hitsA, hitsB);
        double rate = HopHits(a).Count(x => x) / (double)HopHits(a).Length;
        _out.WriteLine($"hit rate {rate:F3}");
        Assert.InRange(rate, 0.25, 0.35);

        // Another net (seed) gets a different pattern from the same jammer.
        var (other, _) = RenderOnClock(c, 1000.0, Fs * 6, Net("k9").Seed);
        Assert.NotEqual(HopHits(a), HopHits(other));
    }

    [Fact]
    public void Follower_CoversTheTailOfEveryDwell()
    {
        // 2 ms reaction at 100 hops/s: 80 % of each 10 ms dwell (research-02 §1.5, acceptance check 4).
        var c = new JammerContribution("follow", 0f, JammerTechnique.Tone, 0f, new JammerWaveformParams { Seed = 4 },
            Responsive: false, HopHitFraction: 0.8f, HopFollower: true);
        var (power, _) = RenderOnClock(c, 500.0, Fs * 2, Net().Seed);
        var on = power.Skip(Fs / 10).Count(x => MathF.Sqrt(x) > 0.5f) / (double)(power.Length - Fs / 10);
        _out.WriteLine($"on fraction {on:F3}");
        Assert.InRange(on, 0.76, 0.84);
        // every hop is hit: the jammer arrives 2 ms (96 samples) into each dwell (plus a 1.5 ms ramp each way)
        for (int hop = 20; hop < 150; ++hop)
        {
            Assert.True(power[hop * 480 + 300] > 0.9f);
            Assert.True(power[hop * 480 + 90] < 0.1f);
        }
    }

    [Fact]
    public void SlotHopClock_FollowsNetTimeAndTheRadiosTod()
    {
        double t = 2000.0;
        var receiver = new SlotReceiver { NetTimeSeconds = () => t };
        var slot = new RadioPlayback.RadioConfig(UhfKhz) { HopNet = Slot(Net()) };
        var first = receiver.HopTimingFor(slot, 480)!.Value;
        Assert.Equal(Net().Seed, first.Seed);
        Assert.Equal(200_000.0, first.HopsAtStart, 6);
        Assert.Equal(100.0 / Fs, first.HopsPerSample, 12);

        // Between re-anchors it advances by samples, ignoring callback jitter...
        t += 0.013;
        Assert.Equal(200_001.0, receiver.HopTimingFor(slot, 480)!.Value.HopsAtStart, 6);
        // ...and re-anchors when it drifts past the resync threshold.
        t += 1.0;
        Assert.Equal((t) * 100, receiver.HopTimingFor(slot, 480)!.Value.HopsAtStart, 6);

        // A radio whose TOD runs 30 ms ahead is 3 hops ahead; single-channel slots have no clock.
        var ahead = new RadioPlayback.RadioConfig(UhfKhz) { HopNet = Slot(Net(tod: 30)) };
        Assert.Equal(t * 100 + 3, receiver.HopTimingFor(ahead, 480)!.Value.HopsAtStart, 6);
        Assert.Null(receiver.HopTimingFor(new RadioPlayback.RadioConfig(UhfKhz), 480));
    }
}
