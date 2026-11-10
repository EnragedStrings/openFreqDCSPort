namespace OpenFreqAudio;

/// <summary>
/// Hot mic for the HUD Recorder feed: splits the pilot's mic into the raw "hot mic" copy (heard on the HUD tape while
/// not transmitting) and the copy that goes through the radio FX (own voice, as today), with a short linear crossfade
/// at each PTT edge so the tape never has both at once, a gap, or a click.
/// <list type="bullet">
/// <item>Not transmitting: hot = mic, radio = 0.</item>
/// <item>Transmitting: hot = 0, radio = the transmitted (normalized, gained, maybe tone-replaced) audio.</item>
/// <item>Key-up: over <see cref="FadeMs"/> the hot mic fades out while the radio copy fades in.</item>
/// <item>Release: over <see cref="FadeMs"/> the radio copy fades out (fed from the hot-mic samples, since nothing is
/// transmitted any more) while the hot mic fades back in.</item>
/// </list>
/// The two gains always add to 1, so for the same input the sum is the input. Runs on the mic callback thread.
/// </summary>
public sealed class HotMicCrossfader
{
    public const float FadeMs = 15f;

    private readonly float _step;
    private float _hotGain = 1f;

    public HotMicCrossfader(int sampleRate, float fadeMs = FadeMs)
    {
        _step = 1f / Math.Max(1f, sampleRate * fadeMs / 1000f);
    }

    /// <summary>Current hot-mic gain (1 = hot mic only, 0 = radio copy only).</summary>
    public float HotGain => _hotGain;

    /// <summary>True once a release has fully faded back to the hot mic: there is no radio-copy tail left.</summary>
    public bool RadioCopySilent => _hotGain >= 1f;

    /// <summary>Back to "not transmitting" at once (feature switched on/off).</summary>
    public void Reset() => _hotGain = 1f;

    /// <summary>
    /// One block. <paramref name="hot"/> is the hot-mic copy of the mic (same gain chain as transmit);
    /// <paramref name="transmitted"/> is what is being transmitted (ignored and may be empty when
    /// <paramref name="transmitting"/> is false). Writes the faded hot mic and radio copy.
    /// </summary>
    public void Process(ReadOnlySpan<float> hot, ReadOnlySpan<float> transmitted, bool transmitting,
        Span<float> hotOut, Span<float> radioOut)
    {
        var target = transmitting ? 0f : 1f;
        for (var i = 0; i < hot.Length; i++)
        {
            _hotGain = _hotGain < target ? MathF.Min(target, _hotGain + _step) : MathF.Max(target, _hotGain - _step);
            hotOut[i] = hot[i] * _hotGain;
            var radioSource = transmitting ? transmitted[i] : hot[i];
            radioOut[i] = radioSource * (1f - _hotGain);
        }
    }
}

/// <summary>The HUD Recorder feed's own copy of the capture frame, with the hot mic mixed in (see
/// <see cref="HotMicCrossfader"/>), kept under full scale by its own limiter. The session recording and monitor keep
/// the capture frame without it.</summary>
public static class HudFeedMix
{
    /// <summary>
    /// <paramref name="output"/> = limiter(<paramref name="capture"/> + <paramref name="hotMic"/> × gain in both
    /// channels). <paramref name="capture"/> is interleaved stereo, before the capture limiter;
    /// <paramref name="hotMic"/> is mono with one sample per frame. <paramref name="capture"/> is not modified.
    /// </summary>
    public static void Build(ReadOnlySpan<float> capture, ReadOnlySpan<float> hotMic, float gain, PeakLimiter limiter,
        Span<float> output)
    {
        capture.CopyTo(output);
        var frames = Math.Min(hotMic.Length, capture.Length / 2);
        for (var i = 0; i < frames; i++)
        {
            var s = gain * hotMic[i];
            output[i * 2] += s;
            output[i * 2 + 1] += s;
        }

        limiter.Process(output[..capture.Length]);
    }
}
