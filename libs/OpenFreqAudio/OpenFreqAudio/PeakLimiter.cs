namespace OpenFreqAudio;

/// <summary>
/// Lookahead peak limiter. It delays the audio by <see cref="LatencySamples"/>,
/// so it can turn the gain down before a peak reaches the output.
/// Without it, the only way to keep peaks below full scale is to clamp them, which clips.
///
/// For each sample, it finds the gain that sample needs to stay under the ceiling. Then:
///   1. It takes the smallest needed gain over the last lookahead + 1 samples.
///   2. It lets that gain rise again slowly (the release), but follows it down at once.
///   3. It averages the result over the last lookahead + 1 samples.
/// Every value in that average is at or below the gain of the sample that goes out next,
/// so the output never exceeds the ceiling, and the gain ramps down in a straight line
/// over the lookahead instead of in one step.
///
/// With more than one channel, a "sample" here is a frame (one sample from each channel).
/// All channels get the same gain, from the loudest of them, so panning doesn't shift.
/// </summary>
public sealed class PeakLimiter
{
    // 2 ms is small next to Opus framing and the jitter buffer.
    private const float LookaheadSeconds = 0.002f;

    // -1 dBFS, so Opus has some headroom - its decoded peaks can be a little higher than the input.
    private const float Ceiling = 0.891f;

    private const float ReleaseTau = 0.06f;

    // Gains above this count as "not limiting", so rounding doesn't count as limiting.
    private const float LimitingThreshold = 0.99f;

    private readonly int _channels;

    // Input frames waiting to go out, interleaved.
    private readonly float[] _delay;
    private int _delayPos; // first sample of the oldest frame

    // Sliding minimum of the needed gain as a deque of (gain, sample index)
    // with gains increasing from head to tail, in a ring buffer.
    // A sample whose gain is at or above a newer one's can
    // never be the minimum again, so it is dropped from the tail.
    private readonly (float Gain, int Index)[] _min;
    private int _minHead;
    private int _minCount;
    // Wraps after ~12 hours of continuous audio at 48 kHz, so we're not concerned about overflow.
    // (Ages are differences of two indices, which stay correct across the wrap anyway)
    private int _sampleIndex;

    private readonly float _releaseAlpha;
    private float _released = 1;

    // Moving average of the released gain.
    private readonly float[] _average;
    private int _averagePos;
    private double _averageSum;

    /// <summary>Samples since <see cref="Reset"/> that left with a gain below 0.99.</summary>
    public int LimitedSamples { get; private set; }

    /// <summary>Largest gain reduction since <see cref="Reset"/>, in dB (positive).</summary>
    public float MaxReductionDb { get; private set; }

    /// <summary>How long a sample takes to go through the limiter.</summary>
    public int LatencySamples => _delay.Length / _channels;

    /// <param name="sampleRate">Samples (frames) per second.</param>
    /// <param name="channels">Interleaved channels in each frame.</param>
    public PeakLimiter(int sampleRate, int channels = 1)
    {
        int lookahead = (int)(sampleRate * LookaheadSeconds);
        _channels = channels;
        _delay = new float[lookahead * channels];
        // Fence posts: A delay of 96 samples means we're considering 97 (the new input + delay).
        _min = new (float, int)[lookahead + 1];
        _average = new float[lookahead + 1];
        _releaseAlpha = 1 - MathF.Exp(-1f / (sampleRate * ReleaseTau));
        Reset();
    }

    /// <summary>
    /// Takes one sample in and returns the sample from <see cref="LatencySamples"/> ago,
    /// with its peaks limited to the ceiling. For a mono limiter only.
    /// </summary>
    public float Process(float x)
    {
        Process(new Span<float>(ref x));
        return x;
    }

    /// <summary>
    /// Limits interleaved frames in place. Each frame that comes out is the one from
    /// <see cref="LatencySamples"/> frames ago.
    /// </summary>
    public void Process(Span<float> frames)
    {
        if (frames.Length % _channels != 0)
            throw new ArgumentException($"Length must be a multiple of the channel count ({_channels})", nameof(frames));

        for (int f = 0; f < frames.Length; f += _channels)
        {
            var frame = frames.Slice(f, _channels);

            // The loudest channel sets the gain for all of them.
            // (A NaN fails the comparison, so it can't get into the limiter's state.)
            float magnitude = 0;
            foreach (float s in frame)
            {
                float a = MathF.Abs(s);
                if (a > magnitude) magnitude = a;
            }

            float gain = NextGain(magnitude);

            // Our delay line - read the frame from LatencySamples ago and apply the calculated gain.
            for (int c = 0; c < _channels; c++)
            {
                float x = frame[c];
                frame[c] = _delay[_delayPos + c] * gain;
                _delay[_delayPos + c] = x;
            }
            _delayPos = (_delayPos + _channels) % _delay.Length;
        }
    }

    /// <summary>
    /// Adds the next sample's magnitude to the window, and returns the gain for the sample
    /// that leaves the delay line now.
    /// </summary>
    private float NextGain(float magnitude)
    {
        // The largest gain that keeps this sample at or below Ceiling (or unity if already below)
        float needed = magnitude > Ceiling ? Ceiling / magnitude : 1f;

        int window = _min.Length;
        // If the oldest entry in our minimum gain deque passed its expiration
        // (i.e. now - then >= window size), pop it.
        if (_minCount > 0 && _sampleIndex - _min[_minHead].Index >= window)
        {
            _minHead = (_minHead + 1) % window;
            _minCount--;
        }

        // Now look at the tail (most recent entries).
        // Pop while the needed gain of our new sample is smaller or equal,
        // which ensures the deque is increasing in gain.
        while (_minCount > 0 && _min[(_minHead + _minCount - 1) % window].Gain >= needed)
            _minCount--;

        // Push our new gain to the tail.
        _min[(_minHead + _minCount) % window] = (needed, _sampleIndex);
        _minCount++;
        _sampleIndex++;

        // By enforcing that invariant above (that the deque is increasing in gain),
        // the head must be the smallest gain (i.e. the most attenuation)
        // we need within our lookahead window.
        float windowMin = _min[_minHead].Gain;

        // The core of the limiter: attenuate immediately, but recover exponentially.
        _released = windowMin < _released
            ? windowMin
            : _released + _releaseAlpha * (windowMin - _released);

        // Our output gain for this sample is going to be the mean of all the `_released`
        // values we've seen over the last window. Remove the oldest, add the newest.
        _averageSum += _released - _average[_averagePos];
        // Push to the circular buffer
        _average[_averagePos] = _released;
        _averagePos = (_averagePos + 1) % _average.Length;
        float gain = (float)(_averageSum / _average.Length);

        // Stats tracking - how many samples have we limited? By how much (max)?
        if (gain < LimitingThreshold)
        {
            LimitedSamples++;
            MaxReductionDb = MathF.Max(MaxReductionDb, -20 * MathF.Log10(gain));
        }

        return gain;
    }

    /// <summary>
    /// Empties the delay line and resets the gain and the statistics.
    /// Call it before audio that doesn't follow on from the last (such as a new talk-spurt),
    /// so it doesn't start with the end of the old audio.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_delay);
        _delayPos = 0;
        _minHead = 0;
        _minCount = 0;
        _sampleIndex = 0;
        _released = 1;
        Array.Fill(_average, 1f);
        _averagePos = 0;
        _averageSum = _average.Length;
        LimitedSamples = 0;
        MaxReductionDb = 0;
    }
}
