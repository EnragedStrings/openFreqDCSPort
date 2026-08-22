namespace OpenFreqAudio.Satcom;

/// <summary>Unquantized speech-analysis frame -- the output of SatcomVocoderEncoder's analysis
/// stage, before quantization. Sequence/timestamp identify the frame for jitter/reordering
/// handling upstream; the rest are the estimated MELP-inspired parameters.</summary>
public sealed class SatcomVocoderFrame
{
    public int SequenceNumber { get; set; }

    /// <summary>RMS amplitude of the analyzed frame (linear, not dB).</summary>
    public double Energy { get; set; }

    /// <summary>Estimated fundamental frequency, Hz. 0 = unvoiced/no periodicity found.</summary>
    public double PitchHz { get; set; }

    /// <summary>0 (unvoiced) .. 1 (fully voiced) -- see SatcomVoicingClassifier.</summary>
    public double Voicing { get; set; }

    /// <summary>LPC reflection coefficients, 0-indexed, length = LPC order. |k| &lt; 1 always (see
    /// SatcomLpc) -- this is the frame's spectral-envelope representation.</summary>
    public double[] ReflectionCoefficients { get; set; } = [];
}

/// <summary>Quantized, corruptible representation of one SatcomVocoderFrame -- the "encoded
/// frame" the channel-error model operates on (see docs/SATCOM_SIMULATION.md's "Encoded Frame
/// Representation" -- this is a compact logical representation, not a claim of matching any real
/// codec's actual bitstream layout).</summary>
public sealed class SatcomEncodedFrame
{
    public int SequenceNumber { get; set; }

    /// <summary>0 = unvoiced (no pitch); 1..31 = quantized pitch index.</summary>
    public byte PitchIndex { get; set; }

    public byte GainIndex { get; set; }
    public byte VoicingIndex { get; set; }

    /// <summary>One quantized index per LPC reflection coefficient.</summary>
    public byte[] SpectralIndices { get; set; } = [];
}
