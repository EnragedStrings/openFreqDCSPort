using System;

namespace OpenFreqAudio.Satcom;

/// <summary>
/// Quantizes/dequantizes vocoder analysis frames to/from the compact encoded representation.
/// Deliberately coarse (5-bit pitch, 4-bit gain, 3-bit voicing, 5-bit per reflection coefficient)
/// to produce authentic low-bitrate speech characteristics per the design brief -- this is what
/// makes a clean SATCOM link still sound "compressed, synthetic, slightly robotic" rather than
/// identical to the raw microphone. SIMULATION APPROXIMATION: quantization step sizes are chosen
/// for plausible perceptual quality at a MELP-like ~2400bps-class bit budget, not derived from a
/// specific published codebook.
/// </summary>
public static class SatcomFrameQuantizer
{
    private const int PitchLevels = 32;
    private const int GainLevels = 16;
    private const int VoicingLevels = 8;
    private const int SpectralLevels = 32;

    private const double PitchMinHz = 60.0;
    private const double PitchMaxHz = 400.0;
    private const double GainMinDb = -60.0;
    private const double GainMaxDb = 6.0;

    public static SatcomEncodedFrame Quantize(SatcomVocoderFrame frame, int order)
    {
        byte pitchIndex;
        if (frame.PitchHz <= 0)
        {
            pitchIndex = 0;
        }
        else
        {
            var t = (frame.PitchHz - PitchMinHz) / (PitchMaxHz - PitchMinHz);
            pitchIndex = (byte)Math.Clamp(1 + (int)Math.Round(t * (PitchLevels - 2)), 1, PitchLevels - 1);
        }

        var gainDb = 20.0 * Math.Log10(Math.Max(1e-6, frame.Energy));
        var gainT = (gainDb - GainMinDb) / (GainMaxDb - GainMinDb);
        var gainIndex = (byte)Math.Clamp((int)Math.Round(gainT * (GainLevels - 1)), 0, GainLevels - 1);

        var voicingIndex = (byte)Math.Clamp((int)Math.Round(frame.Voicing * (VoicingLevels - 1)), 0, VoicingLevels - 1);

        var spectralIndices = new byte[order];
        for (var i = 0; i < order; i++)
        {
            var k = i < frame.ReflectionCoefficients.Length ? frame.ReflectionCoefficients[i] : 0.0;
            k = Math.Clamp(k, -0.999, 0.999);
            spectralIndices[i] = (byte)Math.Clamp((int)Math.Round((k + 1.0) / 2.0 * (SpectralLevels - 1)), 0, SpectralLevels - 1);
        }

        return new SatcomEncodedFrame
        {
            SequenceNumber = frame.SequenceNumber,
            PitchIndex = pitchIndex,
            GainIndex = gainIndex,
            VoicingIndex = voicingIndex,
            SpectralIndices = spectralIndices
        };
    }

    /// <summary>Returns (PitchHz, Energy, Voicing, Reflection) where Reflection is 1-indexed
    /// length order+1 (index 0 unused), matching SatcomLpc's convention.</summary>
    public static (double PitchHz, double Energy, double Voicing, double[] Reflection) Dequantize(
        SatcomEncodedFrame encoded, int order)
    {
        var pitchHz = encoded.PitchIndex == 0
            ? 0.0
            : PitchMinHz + (encoded.PitchIndex - 1) / (double)(PitchLevels - 2) * (PitchMaxHz - PitchMinHz);

        var gainDb = GainMinDb + (double)encoded.GainIndex / (GainLevels - 1) * (GainMaxDb - GainMinDb);
        var energy = Math.Pow(10.0, gainDb / 20.0);

        var voicing = (double)encoded.VoicingIndex / (VoicingLevels - 1);

        var reflection = new double[order + 1];
        for (var i = 0; i < order && i < encoded.SpectralIndices.Length; i++)
            reflection[i + 1] = (double)encoded.SpectralIndices[i] / (SpectralLevels - 1) * 2.0 - 1.0;

        return (pitchHz, energy, voicing, reflection);
    }
}
