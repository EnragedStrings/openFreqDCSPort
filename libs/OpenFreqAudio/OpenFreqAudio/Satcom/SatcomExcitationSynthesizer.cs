using System;

namespace OpenFreqAudio.Satcom;

/// <summary>
/// Mixed excitation source for LPC synthesis: blends a periodic pulse train (voiced speech) with
/// white noise (unvoiced speech) according to the frame's voicing probability, rather than
/// synthesizing every frame as a pure pitch oscillator (which is what produces a cheap "robot
/// voice" effect) or hard-switching between two modes. SIMULATION APPROXIMATION of MELP's mixed
/// excitation concept -- real MELP uses multiple frequency-band voicing strengths and a jittery/
/// aperiodic pulse model for degraded voicing; this uses a single broadband voicing mix with a
/// raised-cosine pulse shape, close enough perceptually for this project's purposes without
/// claiming bitstream-level MELP fidelity (see docs/SATCOM_SIMULATION.md).
/// </summary>
public sealed class SatcomExcitationSynthesizer
{
    private readonly Random _rng;
    private double _phase;

    public SatcomExcitationSynthesizer(int seed) => _rng = new Random(seed);

    /// <summary>One excitation sample. <paramref name="voicing"/> in [0,1]; <paramref name="pitchHz"/>
    /// may be 0 (unvoiced) regardless of voicing value, in which case only the noise component
    /// contributes.</summary>
    public double NextSample(double pitchHz, double voicing, double gain, int sampleRate)
    {
        var periodic = 0.0;
        if (pitchHz > 0 && sampleRate > 0)
        {
            _phase += pitchHz / sampleRate;
            if (_phase >= 1.0) _phase -= Math.Floor(_phase);

            const double pulseWidth = 0.12; // fraction of one pitch period
            if (_phase < pulseWidth)
                periodic = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * _phase / pulseWidth));

            // Normalize so the pulse's average power is comparable to the noise branch below,
            // independent of pulse width, so voicing crossfades smoothly rather than jumping in
            // loudness as pitch changes.
            periodic *= Math.Sqrt(1.0 / pulseWidth) * 0.6;
        }
        else
        {
            voicing = 0.0; // no pitch estimate at all -> fully noise-excited regardless of the frame's voicing value
        }

        var noise = _rng.NextDouble() * 2.0 - 1.0;
        var mixed = voicing * periodic + (1.0 - voicing) * noise;
        return mixed * gain;
    }

    public void Reset() => _phase = 0.0;
}
