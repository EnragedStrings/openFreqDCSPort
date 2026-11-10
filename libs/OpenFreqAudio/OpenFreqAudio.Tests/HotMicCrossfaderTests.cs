namespace OpenFreqAudio.Tests;

/// <summary>HUD Recorder hot mic: the PTT crossfade and the feed's own mix.</summary>
public class HotMicCrossfaderTests
{
    private const int Fs = 48_000;
    private static readonly int FadeSamples = (int)(Fs * HotMicCrossfader.FadeMs / 1000f); // 720

    private static float[] Sine(int n, float amp, double hz = 1000, int phaseOffset = 0) =>
        Enumerable.Range(phaseOffset, n).Select(i => amp * (float)Math.Sin(2 * Math.PI * hz * i / Fs)).ToArray();

    private static (float[] Hot, float[] Radio) Run(HotMicCrossfader x, float[] hot, float[] tx, bool transmitting)
    {
        var h = new float[hot.Length];
        var r = new float[hot.Length];
        x.Process(hot, tx, transmitting, h, r);
        return (h, r);
    }

    [Fact]
    public void IdleIsHotMicOnly()
    {
        var x = new HotMicCrossfader(Fs);
        var mic = Sine(480, 0.5f);
        var (h, r) = Run(x, mic, [], transmitting: false);
        Assert.Equal(mic, h);
        Assert.All(r, s => Assert.Equal(0f, s));
        Assert.True(x.RadioCopySilent);
    }

    [Fact]
    public void SteadyTransmissionIsRadioCopyOnlyNeverBoth()
    {
        var x = new HotMicCrossfader(Fs);
        var mic = Sine(4800, 0.5f);
        var tx = Sine(4800, 0.3f);
        var (h, r) = Run(x, mic, tx, transmitting: true);

        for (var i = FadeSamples; i < mic.Length; i++)
        {
            Assert.Equal(0f, h[i]);
            Assert.Equal(tx[i], r[i]);
        }
    }

    [Fact]
    public void KeyUpCrossfadeTakesFifteenMillisecondsAndTheGainsAddToOne()
    {
        var x = new HotMicCrossfader(Fs);
        var ones = Enumerable.Repeat(1f, 2000).ToArray();
        var (h, r) = Run(x, ones, ones, transmitting: true);

        for (var i = 0; i < ones.Length; i++)
            Assert.Equal(1f, h[i] + r[i], 5); // same input in both: no level bump, no gap
        for (var i = 1; i < ones.Length; i++)
            Assert.True(h[i] <= h[i - 1]); // monotonic
        Assert.True(h[FadeSamples - 2] > 0f);
        Assert.Equal(0f, h[FadeSamples]);
        Assert.InRange(FadeSamples, 0.010 * Fs, 0.020 * Fs);
    }

    [Fact]
    public void ReleaseFadesTheRadioCopyOutFromTheHotMicAndTheHotMicBackIn()
    {
        var x = new HotMicCrossfader(Fs);
        Run(x, new float[2000], new float[2000], transmitting: true); // fully keyed
        Assert.Equal(0f, x.HotGain);

        var mic = Enumerable.Repeat(0.4f, 2000).ToArray();
        var (h, r) = Run(x, mic, [], transmitting: false);
        Assert.False(r[0] == 0f);                    // the radio copy tail runs on the hot-mic samples...
        Assert.Equal(0.4f, h[10] + r[10], 5);         // ...and the two add up to the mic
        Assert.Equal(0f, r[FadeSamples]);
        Assert.Equal(0.4f, h[FadeSamples], 5);
        Assert.True(x.RadioCopySilent);
    }

    [Fact]
    public void PttEdgesDoNotClick()
    {
        // Talking through both edges: hot and radio copy are the same sine at different levels (the radio copy's
        // transmit chain differs). The largest sample step must stay near the sine's own, far below a hard switch.
        var x = new HotMicCrossfader(Fs);
        var n = 4800;
        var mic = Sine(n, 0.5f);
        var tx = Sine(n, 0.25f);
        var feed = new List<float>();
        void Block(int from, int len, bool ptt)
        {
            var h = new float[len];
            var r = new float[len];
            x.Process(mic.AsSpan(from, len), tx.AsSpan(from, len), ptt, h, r);
            for (var i = 0; i < len; i++) feed.Add(h[i] + r[i]);
        }

        Block(0, 1200, false);
        Block(1200, 1200, true);
        Block(2400, 1200, false);
        Block(3600, 1200, true);

        var sineStep = 0.5 * 2 * Math.PI * 1000 / Fs; // ≈ 0.065
        var maxStep = feed.Zip(feed.Skip(1), (a, b) => Math.Abs(b - a)).Max();
        Assert.True(maxStep < sineStep * 1.1, $"max step {maxStep}");
    }

    [Fact]
    public void FeedMixAddsTheHotMicCentredUnderItsOwnLimiterAndLeavesTheCaptureAlone()
    {
        const int frames = 4800;
        // Loud radio traffic (0.9 in both channels) and loud talking at once.
        var capture = new float[frames * 2];
        var tone = Sine(frames, 0.9f, 700);
        for (var i = 0; i < frames; i++) capture[2 * i] = capture[2 * i + 1] = tone[i];
        var captureBefore = (float[])capture.Clone();
        var hot = Sine(frames, 0.9f, 1100);

        var limiter = new PeakLimiter(Fs, channels: 2);
        var output = new float[frames * 2];
        HudFeedMix.Build(capture, hot, 0.70710678f, limiter, output);

        Assert.Equal(captureBefore, capture); // session recording / monitor get the frame without the hot mic
        Assert.True(output.Max(Math.Abs) <= 1.0f, $"peak {output.Max(Math.Abs)}");
        Assert.True(limiter.LimitedSamples > 0); // it needed the headroom
    }

    [Fact]
    public void FeedMixIsCaptureIdenticalWhenTheHotMicIsSilent()
    {
        const int frames = 960;
        var capture = Sine(frames * 2, 0.3f);
        var output = new float[frames * 2];
        var limiter = new PeakLimiter(Fs, channels: 2);
        HudFeedMix.Build(capture, new float[frames], 0.707f, limiter, output);

        // Quiet input: the limiter only delays it by its lookahead.
        var d = limiter.LatencySamples * 2;
        for (var i = d; i < output.Length; i++) Assert.Equal(capture[i - d], output[i], 5);
    }
}
