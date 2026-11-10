using OpenFreqClient.Services;

namespace OpenFreq.Client.Tests;

/// <summary>HUD Recorder hot mic, mic-callback side: same gain chain as a transmission, crossfade at PTT.</summary>
public class HotMicTapTests
{
    private const int Fs = 48_000;

    private static short[] Speechy(int n, int seed)
    {
        var rng = new Random(seed);
        // A 300 Hz "voice" at a modest level with a little noise: the normalizer has something to steer.
        return Enumerable.Range(0, n).Select(i =>
            (short)(3000 * Math.Sin(2 * Math.PI * 300 * i / Fs) + rng.Next(-200, 200))).ToArray();
    }

    /// <summary>What the transmit path does to a block: normalizer, then input gain.</summary>
    private static float[] TransmitChain(MicLevelNormalizer normalizer, short[] raw, double gain)
    {
        var pcm = (short[])raw.Clone();
        normalizer.Process(pcm, pcm.Length);
        HotMicTap.ApplyGain(pcm, gain);
        return pcm.Select(s => s / (float)short.MaxValue).ToArray();
    }

    [Fact]
    public void HotMicLevelMatchesTheTransmitChain()
    {
        var tap = new HotMicTap(Fs);
        var tx = new MicLevelNormalizer(Fs);
        for (var block = 0; block < 50; block++)
        {
            var raw = Speechy(960, block);
            var (hot, radio) = tap.Process(raw, [], transmitting: false, normalize: true, inputGain: 1.5);
            tx.UpdateNoiseFloor(raw);
            var expected = TransmitChain(tx, raw, 1.5);

            Assert.Equal(expected, hot.ToArray());
            Assert.All(radio.ToArray(), s => Assert.Equal(0f, s));
        }
    }

    [Fact]
    public void WithoutNormalizationOnlyTheInputGainApplies()
    {
        var tap = new HotMicTap(Fs);
        var raw = Speechy(480, 1);
        var (hot, _) = tap.Process(raw, [], false, normalize: false, inputGain: 2.0);
        for (var i = 0; i < raw.Length; i++)
            Assert.Equal(Math.Clamp(raw[i] * 2, short.MinValue + 1, short.MaxValue) / (float)short.MaxValue, hot.Span[i],
                6);
    }

    [Fact]
    public void TransmittingSwitchesTheFeedToTheRadioCopyAndBackWithoutBoth()
    {
        var tap = new HotMicTap(Fs);
        var raw = Speechy(4800, 2);
        var transmitted = raw.Select(s => s / (float)short.MaxValue * 0.8f).ToArray();

        tap.Process(raw, [], false, normalize: false, inputGain: 1.0);
        var (hot, radio) = tap.Process(raw, transmitted, true, normalize: false, inputGain: 1.0);
        Assert.Equal(0f, hot.Span[^1]);                   // fully on the radio copy by the end of the block
        Assert.Equal(transmitted[^1], radio.Span[^1]);
        Assert.False(tap.RadioCopySilent);

        (hot, radio) = tap.Process(raw, [], false, normalize: false, inputGain: 1.0);
        Assert.Equal(0f, radio.Span[^1]);                 // release faded back to the hot mic
        Assert.Equal(raw[^1] / (float)short.MaxValue, hot.Span[^1], 6);
        Assert.True(tap.RadioCopySilent);
    }

    [Fact]
    public void TransmittingWithoutTransmittedAudioIsTreatedAsIdle()
    {
        var tap = new HotMicTap(Fs);
        var raw = Speechy(480, 3);
        var (hot, radio) = tap.Process(raw, [], transmitting: true, normalize: false, inputGain: 1.0);
        Assert.Equal(raw[0] / (float)short.MaxValue, hot.Span[0], 6);
        Assert.Equal(0f, radio.Span[0]);
    }

    [Fact]
    public void ApplyGainMatchesTheOldTransmitBehaviour()
    {
        short[] s = [1000, -1000, 30000, -30000, 0];
        HotMicTap.ApplyGain(s, 1.5);
        Assert.Equal([1500, -1500, short.MaxValue, short.MinValue + 1, 0], s);
        short[] unity = [123];
        HotMicTap.ApplyGain(unity, 1.00001);
        Assert.Equal(123, unity[0]);
    }
}
