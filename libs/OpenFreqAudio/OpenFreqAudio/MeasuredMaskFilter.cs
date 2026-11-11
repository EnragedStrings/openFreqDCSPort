using System.Numerics;

namespace OpenFreqAudio;

/// <summary>Which mask sound the jet ambient types use on our own voice and the HUD-tape hot mic (local only).</summary>
public enum MaskProfile
{
    /// <summary>Profile 1: the measured mask (cavity measured from a mask / clean recording pair, then the intercom
    /// chain). See <see cref="MeasuredMaskFilter"/>.</summary>
    Measured = 1,

    /// <summary>Profile 2: the classic per-type mask low-pass (the only sound before profiles existed; also what
    /// everyone else's radio uses for us).</summary>
    Classic = 2,
}

/// <summary>
/// Mask profile 1, after the gain stage: the measured mask cavity (<see cref="MeasuredMaskCoefficients"/>, a
/// 768-tap minimum-phase FIR), then the intercom chain: 2nd-order Butterworth high-pass at 220 Hz and 8th-order
/// Butterworth low-pass at 5.2 kHz. These are the settings chosen by ear (clip r3_5). The cavity has a lot of net
/// gain (+16 dB at 500 Hz), so the output is brought back to the classic mask's loudness on speech by a fixed makeup
/// gain and kept under full scale by the same tanh soft limit the audition clips went through. Mono, one instance per
/// signal path, not thread-safe. 48 kHz only (the engine rate).
/// </summary>
internal sealed class MeasuredMaskFilter
{
    private const float HighPassHz = 220f;
    private const float LowPassHz = 5200f;

    // -12.2 dB: the classic A-10C mask's RMS over the measured chain's RMS on the same speech (audition clip levels).
    private const float MakeupGain = 0.2459f;

    private static readonly float[] Taps = MeasuredMaskCoefficients.CavityFir;

    // History stored twice in a row so the newest Taps.Length samples are always contiguous for the dot product.
    private readonly float[] _history = new float[MeasuredMaskCoefficients.CavityFir.Length * 2];
    private readonly float[] _reversedTaps;
    private int _pos;
    private readonly Biquad[] _chain;

    public MeasuredMaskFilter(int sampleRate)
    {
        if (sampleRate != MeasuredMaskCoefficients.SampleRate)
            throw new ArgumentException($"The measured mask is designed for {MeasuredMaskCoefficients.SampleRate} Hz",
                nameof(sampleRate));

        _reversedTaps = Taps.Reverse().ToArray();
        // 8th-order Butterworth = four sections with Q = 1 / (2 cos(theta_k)), theta_k = pi (2k - 1) / 16.
        _chain =
        [
            Biquad.HighPass(sampleRate, HighPassHz, 0.70710678f),
            .. Enumerable.Range(1, 4).Select(k =>
                Biquad.LowPass(sampleRate, LowPassHz, (float)(1.0 / (2.0 * Math.Cos(Math.PI * (2 * k - 1) / 16.0))))),
        ];
    }

    public float Process(float x)
    {
        int n = Taps.Length;
        _history[_pos] = x;
        _history[_pos + n] = x;
        // Newest sample at _pos + n; the window [_pos + 1, _pos + n] holds the last n samples oldest-first.
        var window = new ReadOnlySpan<float>(_history, _pos + 1, n);
        _pos = (_pos + 1) % n;

        float y = Dot(window, _reversedTaps);
        foreach (var bq in _chain) y = bq.Process(y);
        return MathF.Tanh(y * MakeupGain);
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0;
        var acc = Vector<float>.Zero;
        int w = Vector<float>.Count;
        for (; i <= a.Length - w; i += w) acc += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
        float sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>RBJ cookbook biquad (bilinear, cutoff prewarped), transposed direct form II.</summary>
    private sealed class Biquad
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;
        private float _z1, _z2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = (float)(b0 / a0); _b1 = (float)(b1 / a0); _b2 = (float)(b2 / a0);
            _a1 = (float)(a1 / a0); _a2 = (float)(a2 / a0);
        }

        public float Process(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }

        public static Biquad LowPass(int fs, float f, float q)
        {
            double w = 2 * Math.PI * f / fs, c = Math.Cos(w), a = Math.Sin(w) / (2 * q);
            return new Biquad((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + a, -2 * c, 1 - a);
        }

        public static Biquad HighPass(int fs, float f, float q)
        {
            double w = 2 * Math.PI * f / fs, c = Math.Cos(w), a = Math.Sin(w) / (2 * q);
            return new Biquad((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + a, -2 * c, 1 - a);
        }
    }
}
