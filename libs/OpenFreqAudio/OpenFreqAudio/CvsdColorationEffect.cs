namespace OpenFreqAudio;

/// <summary>
/// Approximates the audible character of decoded 16 kb/s CVSD secure voice (as used by
/// KY-58/VINSON) on top of the existing AM radio chain: a light adaptive-step quantizer loosely
/// modeled on CVSD's own step-size adaptation, producing the "granular"/digitally-textured
/// quality real secure voice has versus clear analog AM (harsher consonants, persistent
/// low-level quantization texture).
///
/// This is a perceptual approximation, not a real CVSD encoder/decoder pair — it qualitatively
/// reproduces slope-overload avoidance (fast step growth on big jumps) and reduced granular
/// noise on quiet passages (step decay when the signal is nearly constant), which is what gives
/// real CVSD its character, without simulating the actual bitstream.
/// SIMULATION CHOICE: parameters are tuned by ear, not derived from a real CVSD codec.
/// </summary>
public sealed class CvsdColorationEffect
{
    private const float StepMin = 0.015f;
    private const float StepMax = 0.12f;
    private const float StepGrow = 1.3f;
    private const float StepDecay = 0.98f;

    // Blended with the dry signal rather than replacing it outright — a full-strength quantizer
    // read as harsh static rather than a subtle "digital" grain. Tune this (0 = no effect,
    // 1 = original full-strength quantizer) to taste.
    private const float WetMix = 0.4f;

    private float _step = StepMin;
    private float _quantized;

    /// <summary>Apply CVSD-like coloration to one sample and return the colored result.</summary>
    public float Process(float sample)
    {
        var error = sample - _quantized;
        if (Math.Abs(error) > _step)
            _step = Math.Min(StepMax, _step * StepGrow);
        else
            _step = Math.Max(StepMin, _step * StepDecay);

        _quantized += Math.Sign(error) * _step;
        return sample * (1f - WetMix) + _quantized * WetMix;
    }
}
