using System;

namespace OpenFreqAudio.Satcom;

/// <summary>
/// Speech-analysis stage: turns one frame of 8 kHz PCM into a SatcomVocoderFrame (pitch, voicing,
/// energy, LPC spectral envelope). LPC order 10 is the standard choice for 8 kHz narrowband
/// speech (roughly 2 poles per kHz of bandwidth plus a couple extra -- PUBLIC STANDARD DSP
/// practice, not SATCOM-specific).
/// </summary>
public sealed class SatcomVocoderEncoder
{
    public const int LpcOrder = 10;

    private readonly SatcomPitchEstimator _pitch;
    private readonly int _sampleRate;
    private int _sequence;

    public SatcomVocoderEncoder(int sampleRate)
    {
        _sampleRate = sampleRate;
        _pitch = new SatcomPitchEstimator(sampleRate);
    }

    public SatcomVocoderFrame Analyze(ReadOnlySpan<double> frame8kHz)
    {
        var autocorr = SatcomLpc.Autocorrelate(frame8kHz, LpcOrder);
        var (_, reflection1Indexed, _) = SatcomLpc.LevinsonDurbin(autocorr, LpcOrder);

        var (pitchHz, confidence) = _pitch.Estimate(frame8kHz);
        var voicing = pitchHz > 0 ? SatcomVoicingClassifier.Classify(frame8kHz, confidence) : 0.0;

        double energySum = 0;
        foreach (var s in frame8kHz) energySum += s * s;
        var rms = frame8kHz.Length > 0 ? Math.Sqrt(energySum / frame8kHz.Length) : 0.0;

        // Convert Levinson-Durbin's 1-indexed (order+1) output to the 0-indexed length-order
        // array SatcomVocoderFrame/SatcomFrameQuantizer expect.
        var reflection0Indexed = new double[LpcOrder];
        Array.Copy(reflection1Indexed, 1, reflection0Indexed, 0, LpcOrder);

        return new SatcomVocoderFrame
        {
            SequenceNumber = _sequence++,
            Energy = rms,
            PitchHz = pitchHz,
            Voicing = voicing,
            ReflectionCoefficients = reflection0Indexed
        };
    }

    public int SampleRate => _sampleRate;

    public void Reset()
    {
        _pitch.Reset();
        _sequence = 0;
    }
}
