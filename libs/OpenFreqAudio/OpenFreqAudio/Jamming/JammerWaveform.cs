using NWaves.Filters.Butterworth;

namespace OpenFreqAudio.Jamming;

/// <summary>
/// One jammer as one receiver slot hears it: synthesizes the jammer's complex baseband, sample by sample, in
/// the receiver's unit-noise scale (docs/jamming/research-01-physics.md §4.3, §6.2). The DSP adds the output to
/// the slot's I/Q sum after the noise and before the envelope, AGC and squelch, so the AM whistle, AGC capture,
/// squelch break and the rest emerge from the receiver rather than being scripted.
///
/// <para>Level: <c>10^(JnrDb/20)</c> times a complex envelope of unit average power, so JnrDb is the in-passband
/// jammer-to-noise ratio against the receiver's unit-power noise. Level changes are smoothed with the same time
/// constant as a transmitter's carrier (<see cref="CarrierSmoother"/>), so a physics tick never steps it.</para>
///
/// <para>State (carrier phase, PRNGs, gates, filters) lives here and is keyed by JammerId in the slot, so a new
/// contribution list with the same ids continues every waveform where it left off. Everything random comes from
/// <see cref="JammerRng"/> seeded by Params.Seed and the id, so it is deterministic.</para>
///
/// <para>The receiver detects the envelope for FM as well as AM (FM's discriminator is modelled by capture and
/// the threshold scale, not synthesized), and a bare carrier, however its frequency moves, has a flat envelope
/// and is silent on its own. So the tone-pattern techniques (stepped tones, wobbler, gulls) carry their pattern
/// as audio-tone modulation on the jammer's carrier, which is how a jammer gets "bagpipes" onto a voice net,
/// with the audio tone's phase integrated sample by sample (a chirp for the sweeps). Their carrier still sits
/// at CarrierOffsetHz, so over a wanted carrier you also get the heterodyne whistle.</para>
///
/// DSP thread only. Allocates only when created or when it first renders a technique that needs filters.
/// </summary>
internal sealed class JammerWaveform
{
    private const int Fs = AudioFormat.SampleRate;
    private const double TwoPi = 2.0 * Math.PI;

    /// <summary>Same IF as the receiver noise (BackgroundNoiseGenerator): 8 kHz, so ±4 kHz at baseband.</summary>
    public const double IfBandwidthHz = 8000.0;
    private const int IfOrder = 4;

    /// <summary>Hop-sized block for <see cref="JammerContribution.HopHitFraction"/> gating: 10 ms (100 hops/s).</summary>
    public const int HopBlockSamples = Fs / 100;

    // Raised-cosine edges: 2 ms for look-through, responsive on/off and the key-up of a new jammer
    // (long enough to be click-free, short enough to keep the rhythmic holes crisp), 1.5 ms hop crossfades.
    public const int GateRampSamples = Fs * 2 / 1000;
    public const int HopRampSamples = Fs * 3 / 2000;

    // Modulation depth of the tone-pattern techniques, and of the babble.
    private const double ToneModIndex = 0.9;
    private const double BabbleModIndex = 0.8;

    // Gulls: the pitch rises over this fraction of each cry, then falls over the rest ("quick rise, slow fall").
    private const double GullRiseFraction = 0.15;

    // Lowest pitch of a sweep when the params don't give one (Hz), and the stepped-tone scale when they don't
    // give tones. Estimates chosen to match the FM 24-33 descriptions (research-01 §4.3).
    private const float DefaultLowPitchHz = 300f;
    private static readonly float[] DefaultSteps = [600f, 750f, 900f, 1100f, 1350f, 1600f];

    // Random pulse: log-normal amplitude spread, 6 dB (research-01 §4.3).
    private static readonly double PulseSigma = 6.0 * Math.Log(10) / 20.0;

    public string Id { get; }

    /// <summary>Samples rendered since this jammer appeared on the slot; drives every periodic pattern.</summary>
    public long Clock { get; private set; }

    /// <summary>Generation stamp, so the slot can drop jammers that left its list.</summary>
    public long LastFrame;

    // Carrier phase (relative to the slot's channel), audio-tone phase for the pattern techniques, and
    // per-tone phases for MultiTone. All integrated per sample, wrapped to [0, 2π).
    internal double CarrierPhase;
    private double _audioPhase;
    private double[] _tonePhases = [];
    private float[] _toneGains = [];
    private double[] _toneCos = [];
    private double[] _toneSin = [];
    private double[] _toneStepCos = [];
    private double[] _toneStepSin = [];

    private JammerRng _rng;
    private JammerRng _hopRng;

    // Level smoothing (dB) and the amplitude the last callback ended on.
    private bool _levelInitialized;
    private double _levelDb;
    private double _amplitude;

    private Gate _lookThrough;
    private Gate _responsive;
    private Gate _hop;
    private bool _hopHit;
    private long _wantedSamples = -1;

    // Noise filling (Noise, RandomPulse): white Gaussian through the receiver's IF, scaled to unit power.
    private LowPassFilter? _ifI;
    private LowPassFilter? _ifQ;
    private double _noiseScale;

    // Spark: the pulse train through the IF, so its edges are as sharp as the receiver can pass.
    private LowPassFilter? _sparkIf;

    // Random pulse: remaining samples of the current pulse and its amplitude.
    private int _pulseRemaining;
    private double _pulseAmplitude;

    // Babble (Recorded): speech-shaped noise under a syllabic envelope.
    private HighPassFilter? _babbleHp;
    private LowPassFilter? _babbleLp;
    private double _babbleTilt;
    private double _babbleScale;
    private double _syllableLevel;
    private double _syllableTarget;
    private int _syllableRemaining;

    public JammerWaveform(string id, int seed)
    {
        Id = id;
        _rng = JammerRng.For(seed, id, 1);
        _hopRng = JammerRng.For(seed, id, 2);
        CarrierPhase = _rng.NextDouble() * TwoPi;
        _audioPhase = _rng.NextDouble() * TwoPi;
    }

    /// <summary>True for techniques whose energy is noise rather than a carrier: on FM they are noise to the
    /// limiter (they raise the threshold), not a carrier it can capture onto quietly.</summary>
    public static bool IsNoiseLike(JammerTechnique technique) =>
        technique is JammerTechnique.Noise or JammerTechnique.RandomPulse;

    /// <summary>Magnitude response of the receiver IF (order-4 Butterworth, ±4 kHz) at an offset.</summary>
    public static double IfGain(double offsetHz)
    {
        double x = Math.Abs(offsetHz) / (IfBandwidthHz / 2.0);
        double x2 = x * x;
        double x4 = x2 * x2;
        return 1.0 / Math.Sqrt(1.0 + x4 * x4);
    }

    public bool IsFinite =>
        double.IsFinite(CarrierPhase) && double.IsFinite(_audioPhase) && double.IsFinite(_amplitude) &&
        double.IsFinite(_levelDb) && double.IsFinite(_syllableLevel);

    /// <summary>
    /// Render this callback: writes <paramref name="outI"/>/<paramref name="outQ"/> (one complex sample each),
    /// already scaled by level and gates, and the mean expected power of each <paramref name="blockSize"/>-sample
    /// sub-block into <paramref name="blockPower"/> (deterministic part only, for FM capture decisions).
    /// </summary>
    /// <param name="c">This jammer's current contribution.</param>
    /// <param name="wantedPresent">Whether a wanted transmission (after TRANSEC) is on the slot this callback,
    /// for responsive jamming.</param>
    public void Render(JammerContribution c, bool wantedPresent, Span<float> outI, Span<float> outQ,
        Span<float> blockPower, int blockSize)
    {
        int samples = outI.Length;
        var p = c.Params;
        var technique = c.Technique;

        // Level, smoothed like a carrier. A non-finite target holds the last good level.
        double targetDb = c.JnrDb;
        if (!_levelInitialized && double.IsFinite(targetDb))
        {
            _levelDb = targetDb;
            _amplitude = Math.Pow(10, _levelDb / 20.0);
            _levelInitialized = true;
        }
        double ampStart = _amplitude;
        if (_levelInitialized && double.IsFinite(targetDb))
        {
            double alpha = 1 - Math.Exp(-(double)samples / Fs / CarrierSmoother.TimeConstantSeconds);
            _levelDb += alpha * (targetDb - _levelDb);
            _amplitude = Math.Pow(10, _levelDb / 20.0);
        }
        double ampSlope = (_amplitude - ampStart) / samples;
        if (!_levelInitialized)
        {
            outI.Clear();
            outQ.Clear();
            blockPower.Clear();
            Clock += samples;
            return;
        }

        double carrierOffset = double.IsFinite(c.CarrierOffsetHz) ? c.CarrierOffsetHz : 0.0;
        double carrierStep = TwoPi * carrierOffset / Fs;
        double carrierGain = IfGain(carrierOffset);

        // Gates. Look-through: silent for LookThroughDuty of every period.
        long ltPeriod = p.LookThroughPeriodMs > 0 && p.LookThroughDuty > 0
            ? Math.Max(1, (long)Math.Round(p.LookThroughPeriodMs * Fs / 1000.0))
            : 0;
        long ltOnSamples = ltPeriod > 0
            ? (long)Math.Round(ltPeriod * (1.0 - Math.Clamp(p.LookThroughDuty, 0f, 1f)))
            : 0;

        // Responsive: on ReactionMs after a wanted signal appears, off when it goes.
        long reaction = (long)Math.Round(Math.Max(0f, c.ReactionMs) * Fs / 1000.0);
        if (wantedPresent && _wantedSamples < 0) _wantedSamples = 0;
        if (!wantedPresent) _wantedSamples = -1;

        // Hop hits: per hop-sized block, a seeded Bernoulli(HopHitFraction).
        float hopFraction = float.IsFinite(c.HopHitFraction) ? c.HopHitFraction : 1f;
        bool hopping = hopFraction < 1f;

        PrepareTechnique(technique, p, carrierOffset);

        // Technique parameters for the loop.
        double pulseRate = Math.Max(0.1, float.IsFinite(p.PulseRateHz) ? p.PulseRateHz : 50.0);
        double pulseDuty = Math.Clamp(float.IsFinite(p.PulseDuty) ? p.PulseDuty : 0.1, 0.001, 1.0);
        double sweepRate = Math.Max(0.0, float.IsFinite(p.SweepRateHz) ? p.SweepRateHz : 4.0);
        double sweepSpan = Math.Max(0.0, float.IsFinite(p.SweepSpanHz) ? p.SweepSpanHz : 2000.0);
        double lowPitch = p.ToneOffsetsHz.Count > 0 && float.IsFinite(p.ToneOffsetsHz[0])
            ? Math.Abs(p.ToneOffsetsHz[0])
            : DefaultLowPitchHz;
        double toneNorm = 1.0 / Math.Sqrt(1.0 + ToneModIndex * ToneModIndex / 2.0);
        double stepSamples = Math.Max(1.0, (float.IsFinite(p.StepIntervalMs) ? p.StepIntervalMs : 150.0) * Fs / 1000.0);
        long sparkPeriod = Math.Max(1, (long)Math.Round(Fs / pulseRate));
        long sparkOn = Math.Max(1, (long)Math.Round(sparkPeriod * pulseDuty));
        double sparkScale = 1.0 / Math.Sqrt((double)sparkOn / sparkPeriod);
        double pulseProbability = pulseRate / Fs;
        double meanPulseSamples = pulseDuty / pulseRate * Fs;
        int numTones = _tonePhases.Length;
        double multiNorm = numTones > 0 ? 1.0 / Math.Sqrt(numTones) : 1.0;

        // The carrier (and MultiTone's tones) rotate by a fixed step, so turn a phasor instead of calling
        // Cos/Sin every sample; each callback restarts it from the integrated phase, so it can't drift.
        double carCos = Math.Cos(CarrierPhase), carSin = Math.Sin(CarrierPhase);
        double stepCos = Math.Cos(carrierStep), stepSin = Math.Sin(carrierStep);
        PrepareTonePhasors(p, numTones);

        float gateInc = 1f / GateRampSamples;
        float hopInc = 1f / HopRampSamples;
        int blocks = blockPower.Length;
        blockPower.Clear();

        for (int n = 0; n < samples; ++n)
        {
            long clock = Clock + n;

            // Gates, each a raised-cosine ramp toward its target. All start closed, so a jammer that
            // appears keys up over 2 ms instead of clicking in.
            bool ltOn = ltPeriod == 0 || clock % ltPeriod < ltOnSamples;
            bool respOn = !c.Responsive || (_wantedSamples >= 0 && _wantedSamples + n >= reaction);
            if (hopping && clock % HopBlockSamples == 0)
                _hopHit = _hopRng.NextDouble() < hopFraction;
            bool hopOn = !hopping || _hopHit;
            double gate = _lookThrough.Step(ltOn, gateInc) * _responsive.Step(respOn, gateInc) * _hop.Step(hopOn, hopInc);

            double amp = (ampStart + ampSlope * (n + 1)) * gate;

            // The technique's complex envelope e = er + j·ei, unit average power, and the deterministic
            // part of its instantaneous power.
            double er, ei, envPower;
            switch (technique)
            {
                case JammerTechnique.Noise:
                    er = _ifI!.Process((float)_rng.NextGaussian()) * _noiseScale;
                    ei = _ifQ!.Process((float)_rng.NextGaussian()) * _noiseScale;
                    envPower = 1.0;
                    break;

                case JammerTechnique.MultiTone when numTones > 0:
                    er = 0;
                    ei = 0;
                    for (int k = 0; k < numTones; ++k)
                    {
                        double tc = _toneCos[k], ts = _toneSin[k];
                        er += _toneGains[k] * tc;
                        ei += _toneGains[k] * ts;
                        _toneCos[k] = tc * _toneStepCos[k] - ts * _toneStepSin[k];
                        _toneSin[k] = tc * _toneStepSin[k] + ts * _toneStepCos[k];
                    }
                    er *= multiNorm;
                    ei *= multiNorm;
                    envPower = 1.0;
                    break;

                case JammerTechnique.SteppedTones:
                {
                    // Ping-pong through the scale: up, then back down ("bagpipes").
                    var steps = p.ToneOffsetsHz.Count > 0 ? p.ToneOffsetsHz : DefaultSteps;
                    int count = steps.Count;
                    long step = (long)(clock / stepSamples);
                    int idx = 0;
                    if (count > 1)
                    {
                        int cycle = 2 * count - 2;
                        idx = (int)(step % cycle);
                        if (idx >= count) idx = cycle - idx;
                    }
                    float pitch = steps[idx];
                    er = ToneEnvelope(float.IsFinite(pitch) ? Math.Abs(pitch) : 0.0, toneNorm, out envPower);
                    ei = 0;
                    break;
                }

                case JammerTechnique.Swept:
                {
                    // Wobbler: the tone's pitch swings sinusoidally over the span at the sweep rate.
                    double t = (double)clock / Fs;
                    double pitch = lowPitch + sweepSpan * 0.5 * (1.0 + Math.Sin(TwoPi * sweepRate * t));
                    er = ToneEnvelope(pitch, toneNorm, out envPower);
                    ei = 0;
                    break;
                }

                case JammerTechnique.Gulls:
                {
                    // Quick rise, slow fall, every 1/SweepRateHz.
                    double cyc = sweepRate > 0 ? (double)clock / Fs * sweepRate : 0.0;
                    double ph = cyc - Math.Floor(cyc);
                    double frac = ph < GullRiseFraction
                        ? ph / GullRiseFraction
                        : 1.0 - (ph - GullRiseFraction) / (1.0 - GullRiseFraction);
                    er = ToneEnvelope(lowPitch + sweepSpan * frac, toneNorm, out envPower);
                    ei = 0;
                    break;
                }

                case JammerTechnique.Spark:
                {
                    double on = clock % sparkPeriod < sparkOn ? sparkScale : 0.0;
                    er = _sparkIf!.Process((float)on);
                    ei = 0;
                    envPower = er * er;
                    break;
                }

                case JammerTechnique.RandomPulse:
                {
                    if (_pulseRemaining <= 0 && _rng.NextDouble() < pulseProbability)
                    {
                        double width = -Math.Log(1.0 - _rng.NextDouble()) * meanPulseSamples;
                        _pulseRemaining = (int)Math.Clamp(width, 4, Fs / 20);
                        // Log-normal with E[a²] = 1 (see BackgroundNoiseGenerator), scaled for the duty.
                        _pulseAmplitude = Math.Exp(_rng.NextGaussian() * PulseSigma - PulseSigma * PulseSigma)
                                          / Math.Sqrt(pulseDuty);
                    }
                    double a = 0;
                    if (_pulseRemaining > 0)
                    {
                        a = _pulseAmplitude;
                        _pulseRemaining--;
                    }
                    er = _ifI!.Process((float)(a * _rng.NextGaussian())) * _noiseScale;
                    ei = _ifQ!.Process((float)(a * _rng.NextGaussian())) * _noiseScale;
                    envPower = a * a;
                    break;
                }

                case JammerTechnique.Recorded:
                    er = BabbleEnvelope();
                    ei = 0;
                    envPower = er * er;
                    break;

                case JammerTechnique.Tone:
                case JammerTechnique.MultiTone:
                default:
                    er = 1;
                    ei = 0;
                    envPower = 1.0;
                    break;
            }

            if (technique != JammerTechnique.MultiTone)
            {
                er *= carrierGain;
                ei *= carrierGain;
                envPower *= carrierGain * carrierGain;
            }

            outI[n] = (float)(amp * (er * carCos - ei * carSin));
            outQ[n] = (float)(amp * (er * carSin + ei * carCos));
            double nextCos = carCos * stepCos - carSin * stepSin;
            carSin = carCos * stepSin + carSin * stepCos;
            carCos = nextCos;

            int b = n / blockSize;
            if (b < blocks) blockPower[b] += (float)(amp * amp * envPower);
        }

        for (int b = 0; b < blocks; ++b)
        {
            int len = Math.Min(blockSize, samples - b * blockSize);
            if (len > 0) blockPower[b] /= len;
        }

        CarrierPhase = Advance(CarrierPhase, carrierStep, samples);
        if (technique == JammerTechnique.MultiTone)
            for (int k = 0; k < numTones; ++k)
                _tonePhases[k] = Advance(_tonePhases[k], TwoPi * p.ToneOffsetsHz[k] / Fs, samples);

        Clock += samples;
        if (_wantedSamples >= 0) _wantedSamples += samples;
    }

    private static double Advance(double theta, double step, int samples)
    {
        theta = (theta + step * samples) % TwoPi;
        return theta < 0 ? theta + TwoPi : theta;
    }

    private void PrepareTonePhasors(JammerWaveformParams p, int numTones)
    {
        if (_toneCos.Length != numTones)
        {
            _toneCos = new double[numTones];
            _toneSin = new double[numTones];
            _toneStepCos = new double[numTones];
            _toneStepSin = new double[numTones];
        }
        for (int k = 0; k < numTones; ++k)
        {
            float f = p.ToneOffsetsHz.Count > k && float.IsFinite(p.ToneOffsetsHz[k]) ? p.ToneOffsetsHz[k] : 0f;
            double step = TwoPi * f / Fs;
            _toneCos[k] = Math.Cos(_tonePhases[k]);
            _toneSin[k] = Math.Sin(_tonePhases[k]);
            _toneStepCos[k] = Math.Cos(step);
            _toneStepSin[k] = Math.Sin(step);
        }
    }

    /// <summary>AM tone modulation at <paramref name="pitch"/>, phase-integrated so pitch changes are glitch-free.</summary>
    private double ToneEnvelope(double pitch, double norm, out double power)
    {
        double e = (1.0 + ToneModIndex * Math.Cos(_audioPhase)) * norm;
        _audioPhase = Wrap(_audioPhase + TwoPi * pitch / Fs);
        power = e * e;
        return e;
    }

    /// <summary>One sample of babble: speech-shaped noise under a random syllabic envelope, AM on the carrier.</summary>
    private double BabbleEnvelope()
    {
        if (_syllableRemaining <= 0)
        {
            // Syllables of 80-250 ms; a quarter of them are pauses.
            _syllableRemaining = (int)((0.08 + 0.17 * _rng.NextDouble()) * Fs);
            _syllableTarget = _rng.NextDouble() < 0.25 ? 0.05 : 0.4 + 0.6 * _rng.NextDouble();
        }
        _syllableRemaining--;
        // ~15 ms attack/decay on the syllable level.
        _syllableLevel += (_syllableTarget - _syllableLevel) * (1.0 / (0.015 * Fs));

        double w = _rng.NextGaussian();
        // A gentle low-frequency tilt (speech energy peaks around 500 Hz), then the voice band.
        _babbleTilt += (w - _babbleTilt) * (TwoPi * 600.0 / Fs);
        double shaped = _babbleLp!.Process(_babbleHp!.Process((float)(0.7 * _babbleTilt * 4.0 + 0.3 * w)));
        double x = Math.Clamp(0.35 * _syllableLevel * shaped * _babbleScale, -1.0, 1.0);
        // E[x²] ≈ 0.35² · E[level²] ≈ 0.048; normalize the AM envelope to unit mean power.
        const double norm = 0.98510; // 1 / sqrt(1 + 0.8² · 0.048)
        return (1.0 + BabbleModIndex * x) * norm;
    }

    /// <summary>Create the filters and tone tables a technique needs, the first time it renders.</summary>
    private void PrepareTechnique(JammerTechnique technique, JammerWaveformParams p, double carrierOffset)
    {
        switch (technique)
        {
            case JammerTechnique.Noise:
            case JammerTechnique.RandomPulse:
                if (_ifI == null)
                {
                    double cutoff = IfBandwidthHz / 2.0 / Fs;
                    _ifI = new LowPassFilter(cutoff, IfOrder);
                    _ifQ = new LowPassFilter(cutoff, IfOrder);
                    // Unit power: two quadratures of unit-variance input through Σh².
                    _noiseScale = 1.0 / Math.Sqrt(2.0 * PowerGain(new LowPassFilter(cutoff, IfOrder), null));
                }
                break;

            case JammerTechnique.Spark:
                _sparkIf ??= new LowPassFilter(IfBandwidthHz / 2.0 / Fs, IfOrder);
                break;

            case JammerTechnique.MultiTone:
            {
                int count = p.ToneOffsetsHz.Count;
                if (_tonePhases.Length != count)
                {
                    // Random starting phases, so K equal tones don't all peak together.
                    _tonePhases = new double[count];
                    _toneGains = new float[count];
                    for (int k = 0; k < count; ++k)
                        _tonePhases[k] = _rng.NextDouble() * TwoPi;
                }
                for (int k = 0; k < count; ++k)
                {
                    float f = p.ToneOffsetsHz[k];
                    _toneGains[k] = float.IsFinite(f) ? (float)IfGain(carrierOffset + f) : 0f;
                }
                break;
            }

            case JammerTechnique.Recorded:
                if (_babbleHp == null)
                {
                    _babbleHp = new HighPassFilter(250.0 / Fs, 2);
                    _babbleLp = new LowPassFilter(3000.0 / Fs, 4);
                    _babbleScale = 1.0 / Math.Sqrt(BabbleShapingPower());
                    _syllableLevel = 0;
                }
                break;
        }
    }

    /// <summary>Output power of the babble shaping for unit-variance white input, measured once.</summary>
    private static double BabbleShapingPower()
    {
        var hp = new HighPassFilter(250.0 / Fs, 2);
        var lp = new LowPassFilter(3000.0 / Fs, 4);
        var rng = new JammerRng(12345);
        double tilt = 0, sum = 0;
        const int n = Fs * 2;
        for (int i = 0; i < n; ++i)
        {
            double w = rng.NextGaussian();
            tilt += (w - tilt) * (TwoPi * 600.0 / Fs);
            double y = lp.Process(hp.Process((float)(0.7 * tilt * 4.0 + 0.3 * w)));
            sum += y * y;
        }
        return sum / n;
    }

    /// <summary>Σh² of a filter (its power gain for white noise), from its impulse response.</summary>
    private static double PowerGain(LowPassFilter lp, HighPassFilter? hp)
    {
        double sum = 0;
        for (int n = 0; n < 4096; ++n)
        {
            float x = n == 0 ? 1f : 0f;
            if (hp != null) x = hp.Process(x);
            double h = lp.Process(x);
            sum += h * h;
        }
        return sum;
    }

    private static double Wrap(double theta)
    {
        if (theta >= TwoPi) theta -= TwoPi;
        else if (theta < 0) theta += TwoPi;
        if (theta >= TwoPi || theta < 0) theta %= TwoPi;
        return theta < 0 ? theta + TwoPi : theta;
    }

    /// <summary>A 0..1 gate that ramps toward its target with a raised-cosine edge.</summary>
    private struct Gate
    {
        private float _pos;

        public double Step(bool on, float inc)
        {
            if (on)
            {
                if (_pos < 1f) _pos = Math.Min(1f, _pos + inc);
            }
            else if (_pos > 0f)
            {
                _pos = Math.Max(0f, _pos - inc);
            }

            if (_pos >= 1f) return 1.0;
            if (_pos <= 0f) return 0.0;
            return 0.5 - 0.5 * Math.Cos(Math.PI * _pos);
        }
    }
}
