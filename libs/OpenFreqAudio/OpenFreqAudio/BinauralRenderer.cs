using System;

namespace OpenFreqAudio;

/// <summary>
/// Places one mono radio signal at a fixed direction around the listener's head on headphones
/// (163rd vFS 3DAS "static rendering": radios referenced to the helmet, so no head tracking is needed).
///
/// Spherical-head model after Brown &amp; Duda (1998, "A structural model for binaural sound synthesis"):
///  - interaural time difference: a per-ear propagation delay around a sphere of radius 8.75 cm
///    (up to ~0.66 ms), rendered with a fractional delay line;
///  - head shadow: a one-pole/one-zero filter per ear whose high-frequency gain goes from about +6 dB
///    (ear facing the source) to -20 dB (ear in the shadow), DC gain 1.
///  - elevation: a spectral tilt on the source (first-order high shelf at 2 kHz, +0.15 dB per degree:
///    +45 deg is about +7 dB brighter, -15 deg about 2 dB duller). Real pinna elevation cues lie above
///    5 kHz, outside the radio band, so brightness ("higher = brighter") is the usable cue here. It is
///    what separates two radios at the same azimuth (3DAS AMDF: RT1 -15 deg and LARS +45 deg, both ahead).
/// Radio audio is band-limited to roughly 300-3000 Hz, where the time difference is the strongest
/// localisation cue, so this cheap model localises azimuth well; elevation and front/back remain weaker.
///
/// Thread use: <see cref="SetDirection"/> from any thread; <see cref="Process"/> only from the
/// audio callback. Direction changes are ramped over one block, so they don't click.
/// </summary>
public sealed class BinauralRenderer
{
    private const float HeadRadiusM = 0.0875f;
    private const float SpeedOfSoundMs = 343f;
    private const float AlphaMin = 0.1f;          // shadowed-ear HF gain (-20 dB)
    private const float ThetaMinDeg = 150f;       // angle from the ear axis where the shadow is deepest
    private const int DelayLength = 128;          // power of two, > max ITD in samples at 96 kHz
    private const int DelayMask = DelayLength - 1;
    private const float TiltDbPerDeg = 0.15f;    // elevation brightness cue
    private const float TiltMaxDb = 9f;
    private const float TiltCornerHz = 2000f;

    private readonly float _sampleRate;
    private readonly float[] _delayLine = new float[DelayLength];
    private int _writePos;

    private volatile float _targetAzDeg;
    private volatile float _targetElDeg;
    private float _appliedAzDeg = float.NaN;
    private float _appliedElDeg = float.NaN;

    private readonly Ear _left = new();
    private readonly Ear _right = new();
    private readonly Ear _tilt = new();          // elevation shelf on the mono source (filter state only)

    private sealed class Ear
    {
        public float Delay;          // samples, current (ramped)
        public float TargetDelay;
        public float B0 = 1f, B1, A1; // head-shadow filter
        public float X1, Y1;
    }

    public BinauralRenderer(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    /// <param name="azimuthDeg">0 = straight ahead, +90 = right ear, -90 = left ear, 180 = behind.</param>
    /// <param name="elevationDeg">+ = up.</param>
    public void SetDirection(float azimuthDeg, float elevationDeg)
    {
        _targetAzDeg = azimuthDeg;
        _targetElDeg = elevationDeg;
    }

    public void Reset()
    {
        Array.Clear(_delayLine);
        _left.X1 = _left.Y1 = _right.X1 = _right.Y1 = 0f;
        _tilt.X1 = _tilt.Y1 = 0f;
        _appliedAzDeg = float.NaN;
    }

    /// <summary>Adds <paramref name="mono"/> * <paramref name="gain"/>, rendered binaurally, into the
    /// interleaved stereo <paramref name="stereo"/> buffer (L, R, L, R ...).</summary>
    public void Process(ReadOnlySpan<float> mono, Span<float> stereo, int frames, float gain)
    {
        UpdateParameters();

        float ldStart = _left.Delay, rdStart = _right.Delay;
        float ldStep = (_left.TargetDelay - ldStart) / Math.Max(frames, 1);
        float rdStep = (_right.TargetDelay - rdStart) / Math.Max(frames, 1);

        for (int n = 0; n < frames; n++)
        {
            _delayLine[_writePos] = Shadow(_tilt, mono[n]);

            float l = ReadDelayed(ldStart + ldStep * (n + 1));
            float r = ReadDelayed(rdStart + rdStep * (n + 1));

            l = Shadow(_left, l);
            r = Shadow(_right, r);

            stereo[n * 2] += gain * l;
            stereo[n * 2 + 1] += gain * r;

            _writePos = (_writePos + 1) & DelayMask;
        }

        _left.Delay = _left.TargetDelay;
        _right.Delay = _right.TargetDelay;
    }

    private float ReadDelayed(float delaySamples)
    {
        int whole = (int)delaySamples;
        float frac = delaySamples - whole;
        float a = _delayLine[(_writePos - whole) & DelayMask];
        float b = _delayLine[(_writePos - whole - 1) & DelayMask];
        return a + (b - a) * frac;
    }

    private static float Shadow(Ear e, float x)
    {
        float y = e.B0 * x + e.B1 * e.X1 - e.A1 * e.Y1;
        e.X1 = x;
        e.Y1 = y;
        return y;
    }

    private void UpdateParameters()
    {
        float az = _targetAzDeg, el = _targetElDeg;
        if (az == _appliedAzDeg && el == _appliedElDeg) return;
        bool first = float.IsNaN(_appliedAzDeg);
        _appliedAzDeg = az;
        _appliedElDeg = el;

        double azR = az * Math.PI / 180.0, elR = el * Math.PI / 180.0;
        // Lateral component of the source direction along the interaural (right-ear) axis.
        double lateral = Math.Cos(elR) * Math.Sin(azR);
        ConfigureEar(_right, Math.Acos(Math.Clamp(lateral, -1.0, 1.0)));
        ConfigureEar(_left, Math.Acos(Math.Clamp(-lateral, -1.0, 1.0)));
        double tiltDb = Math.Clamp(el * TiltDbPerDeg, -TiltMaxDb, TiltMaxDb);
        SetShelf(_tilt, Math.Pow(10.0, tiltDb / 20.0), 2 * Math.PI * TiltCornerHz);

        if (first)
        {
            _left.Delay = _left.TargetDelay;
            _right.Delay = _right.TargetDelay;
        }
    }

    /// <param name="theta">angle between the source direction and this ear's axis, radians (0 = facing it).</param>
    private void ConfigureEar(Ear ear, double theta)
    {
        double a = HeadRadiusM, c = SpeedOfSoundMs;

        // Brown-Duda per-ear delay, offset by a/c so the ear facing the source has zero delay.
        double t = theta < Math.PI / 2 ? -(a / c) * Math.Cos(theta) : (a / c) * (theta - Math.PI / 2);
        ear.TargetDelay = (float)((t + a / c) * _sampleRate);

        // Head shadow H(s) = (alpha s + beta) / (s + beta), beta = 2c/a.
        double alpha = (1 + AlphaMin / 2) + (1 - AlphaMin / 2) * Math.Cos(theta * 180.0 / ThetaMinDeg);
        SetShelf(ear, alpha, 2 * c / a);
    }

    /// <summary>First-order shelf H(s) = (g s + w) / (s + w): DC gain 1, high-frequency gain g; bilinear transform.</summary>
    private void SetShelf(Ear f, double g, double w)
    {
        double k = 2 * _sampleRate;
        double a0 = k + w;
        f.B0 = (float)((w + g * k) / a0);
        f.B1 = (float)((w - g * k) / a0);
        f.A1 = (float)((w - k) / a0);
    }
}
