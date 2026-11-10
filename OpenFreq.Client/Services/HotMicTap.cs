using System;
using OpenFreqAudio;

namespace OpenFreqClient.Services;

/// <summary>
/// The mic-callback side of the HUD Recorder hot mic. Gives the raw mic the same gain chain as a transmission (mic
/// normalizer + input gain) so its level matches the radio copy, then splits it with <see cref="HotMicCrossfader"/>
/// into the hot mic (feed only) and the radio copy (own voice for the capture). The hot mic has its own normalizer:
/// running idle cockpit audio through the transmit normalizer would move the gain and limiter state the next
/// transmission starts from. Fed the same idle audio, its noise floor and gain settle where the transmit one's do.
/// </summary>
public sealed class HotMicTap
{
    private readonly MicLevelNormalizer _normalizer;
    private readonly HotMicCrossfader _crossfader;
    private short[] _pcm = [];
    private float[] _hot = [];
    private float[] _hotOut = [];
    private float[] _radioOut = [];

    public HotMicTap(int sampleRate)
    {
        _normalizer = new MicLevelNormalizer(sampleRate);
        _crossfader = new HotMicCrossfader(sampleRate);
    }

    /// <summary>No radio-copy release tail is in progress.</summary>
    public bool RadioCopySilent => _crossfader.RadioCopySilent;

    /// <summary>Feature switched on/off: start again from "not transmitting".</summary>
    public void Reset() => _crossfader.Reset();

    /// <summary>
    /// One mic block. <paramref name="raw"/> is the mic as captured; <paramref name="transmitted"/> is what is being
    /// transmitted, as floats (empty when not transmitting). Returns the hot mic for the feed and the radio copy
    /// for the capture, both valid until the next call.
    /// </summary>
    public (ReadOnlyMemory<float> Hot, ReadOnlyMemory<float> RadioCopy) Process(ReadOnlySpan<short> raw,
        ReadOnlySpan<float> transmitted, bool transmitting, bool normalize, double inputGain)
    {
        var n = raw.Length;
        if (_pcm.Length < n)
        {
            _pcm = new short[n * 2];
            _hot = new float[n * 2];
            _hotOut = new float[n * 2];
            _radioOut = new float[n * 2];
        }

        raw.CopyTo(_pcm);
        if (normalize)
        {
            // Same rule as the transmit normalizer: the noise floor only moves while not transmitting.
            if (!transmitting) _normalizer.UpdateNoiseFloor(raw);
            _normalizer.Process(_pcm, n);
        }

        ApplyGain(_pcm.AsSpan(0, n), inputGain);
        for (var i = 0; i < n; i++) _hot[i] = _pcm[i] / (float)short.MaxValue;

        if (transmitting && transmitted.Length < n)
            transmitting = false; // nothing to fade to: treat as idle rather than read past the end
        _crossfader.Process(_hot.AsSpan(0, n), transmitted, transmitting, _hotOut.AsSpan(0, n),
            _radioOut.AsSpan(0, n));
        return (_hotOut.AsMemory(0, n), _radioOut.AsMemory(0, n));
    }

    /// <summary>The input gain exactly as the transmit path applies it (rounded, clamped 16-bit).</summary>
    public static void ApplyGain(Span<short> samples, double gain)
    {
        if (Math.Abs(gain - 1.0d) < 0.0001d) return;
        for (var i = 0; i < samples.Length; i++)
        {
            var scaled = (int)Math.Round(samples[i] * gain);
            samples[i] = (short)Math.Clamp(scaled, short.MinValue + 1, short.MaxValue);
        }
    }
}
