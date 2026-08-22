using System;

namespace OpenFreqAudio.Satcom;

public enum SatcomFrameOutcome
{
    /// <summary>Frame decodes correctly.</summary>
    Clean,

    /// <summary>Frame decodes but one or more parameters are corrupted -- still synthesizes
    /// speech, just with wrong pitch/spectral detail/gain/voicing (see Corrupt below).</summary>
    Corrupted,

    /// <summary>Frame is uncorrectable -- decoder must conceal (see SatcomDecoderState.Conceal),
    /// not attempt to synthesize from garbage parameters.</summary>
    Lost
}

/// <summary>
/// Simulates SATCOM RF channel effects against the encoded parameter frame (not raw PCM samples)
/// -- per-frame outcome (clean/corrupted/lost) driven by frame error rate, plus burst-correlated
/// consecutive losses (a real antenna-shadowing event lasts multiple frames, not one independent
/// coin-flip at a time) and perceptually-differentiated per-parameter corruption. Seeded RNG for
/// deterministic/reproducible testing (same input + same link conditions + same seed -> same
/// corrupted output).
/// </summary>
public sealed class SatcomChannelErrorModel
{
    private readonly Random _rng;
    private int _burstFramesRemaining;

    public SatcomChannelErrorModel(int seed) => _rng = new Random(seed);

    /// <param name="frameErrorRate">0..1, from SatcomLinkBudget.FrameErrorRate.</param>
    /// <param name="burstSeverity">0..1, how far below threshold the link is -- higher values
    /// produce longer correlated burst losses (e.g. derived from how deep into the "digital
    /// cliff" the current link margin sits).</param>
    public SatcomFrameOutcome NextOutcome(double frameErrorRate, double burstSeverity)
    {
        frameErrorRate = Math.Clamp(frameErrorRate, 0.0, 1.0);
        burstSeverity = Math.Clamp(burstSeverity, 0.0, 1.0);

        if (_burstFramesRemaining > 0)
        {
            _burstFramesRemaining--;
            return PickLostOrCorrupted(frameErrorRate, burstSeverity);
        }

        if (_rng.NextDouble() >= frameErrorRate)
            return SatcomFrameOutcome.Clean;

        if (burstSeverity > 0.3 && _rng.NextDouble() < burstSeverity)
            _burstFramesRemaining = 1 + (int)(burstSeverity * 6);

        return PickLostOrCorrupted(frameErrorRate, burstSeverity);
    }

    /// <summary>Once a frame is determined "bad", decides fully Lost vs. still-decodable-but-
    /// Corrupted with a probability that scales with severity: near threshold, mild corruption
    /// dominates (matches the design brief's "occasional subtle artifact" for moderate FER);
    /// well below threshold, total loss dominates and eventually becomes certain as
    /// frameErrorRate/burstSeverity approach 1 (matches "no decoded audio" at 0% quality --
    /// without this, occasional Corrupted frames could keep resetting
    /// SatcomDecoderState.ConsecutiveLossCount forever even at FER=1.0, which would make the
    /// link never actually go silent no matter how bad conditions get).</summary>
    private SatcomFrameOutcome PickLostOrCorrupted(double frameErrorRate, double burstSeverity)
    {
        var lostProbability = Math.Clamp(frameErrorRate * 1.5 + burstSeverity * 0.3, 0.0, 1.0);
        return _rng.NextDouble() < lostProbability ? SatcomFrameOutcome.Lost : SatcomFrameOutcome.Corrupted;
    }

    /// <summary>
    /// Applies independent, perceptually-motivated damage to each dequantized parameter for a
    /// Corrupted (not Lost) frame: pitch errors read as a wrong-harmonic buzz, spectral errors as
    /// a warped/metallic vowel, gain errors as a loudness glitch (always clamped -- never a
    /// dangerous spike), voicing errors as a vowel-turns-noisy/consonant-turns-buzzy flip.
    /// </summary>
    public (double PitchHz, double Energy, double Voicing, double[] Reflection) Corrupt(
        double pitchHz, double energy, double voicing, double[] reflection)
    {
        if (pitchHz > 0 && _rng.NextDouble() < 0.4)
            pitchHz = Math.Clamp(pitchHz * (0.5 + _rng.NextDouble()), 60.0, 400.0);

        if (_rng.NextDouble() < 0.3)
            energy = Math.Clamp(energy * (0.3 + _rng.NextDouble() * 1.4), 0.0, 4.0);

        if (_rng.NextDouble() < 0.3)
            voicing = Math.Clamp(voicing + (_rng.NextDouble() - 0.5), 0.0, 1.0);

        if (_rng.NextDouble() < 0.5 && reflection.Length > 1)
        {
            var corrupted = (double[])reflection.Clone();
            var idx = _rng.Next(1, corrupted.Length);
            corrupted[idx] = Math.Clamp(corrupted[idx] + (_rng.NextDouble() - 0.5) * 0.6, -0.999, 0.999);
            reflection = corrupted;
        }

        return (pitchHz, energy, voicing, reflection);
    }
}
