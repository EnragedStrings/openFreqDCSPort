using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenFreqAudio.Satcom;

/// <summary>
/// Autocorrelation-based pitch (fundamental frequency) estimation over the human-voice range,
/// with a median-filter continuity constraint to reject octave errors/spurious peaks from cockpit
/// noise, breathing, and consonants -- PUBLIC STANDARD technique (normalized autocorrelation
/// pitch detection is textbook DSP), tuned here specifically to be robust against the noisy
/// aircraft-cabin conditions called out in the design brief rather than clean studio speech.
/// </summary>
public sealed class SatcomPitchEstimator
{
    private readonly int _sampleRate;
    private readonly int _minLag;
    private readonly int _maxLag;
    private readonly Queue<double> _recentPitches = new();
    private double _lastPitchHz;

    public SatcomPitchEstimator(int sampleRate, double minHz = 70.0, double maxHz = 400.0)
    {
        _sampleRate = sampleRate;
        _maxLag = Math.Max(1, (int)(sampleRate / minHz));
        _minLag = Math.Max(1, (int)(sampleRate / maxHz));
    }

    /// <summary>Returns (pitchHz, confidence 0..1). pitchHz is 0 when the frame is judged
    /// unvoiced/silent (no reliable periodicity found).</summary>
    public (double PitchHz, double Confidence) Estimate(ReadOnlySpan<double> frame)
    {
        double energy0 = 0;
        for (var n = 0; n < frame.Length; n++) energy0 += frame[n] * frame[n];
        if (energy0 < 1e-6)
            return (0.0, 0.0);

        var bestCorr = 0.0;
        var bestLag = 0;
        var maxLag = Math.Min(_maxLag, frame.Length - 1);

        for (var lag = _minLag; lag <= maxLag; lag++)
        {
            double corr = 0, energyLag = 0;
            for (var n = 0; n < frame.Length - lag; n++)
            {
                corr += frame[n] * frame[n + lag];
                energyLag += frame[n + lag] * frame[n + lag];
            }

            var denom = Math.Sqrt(energy0 * energyLag);
            var normalized = denom > 1e-9 ? corr / denom : 0.0;
            if (normalized > bestCorr)
            {
                bestCorr = normalized;
                bestLag = lag;
            }
        }

        const double voicingThreshold = 0.3;
        if (bestLag == 0 || bestCorr < voicingThreshold)
        {
            _recentPitches.Clear();
            _lastPitchHz = 0;
            return (0.0, bestCorr);
        }

        var pitchHz = (double)_sampleRate / bestLag;

        _recentPitches.Enqueue(pitchHz);
        while (_recentPitches.Count > 5) _recentPitches.Dequeue();
        var median = _recentPitches.OrderBy(x => x).ElementAt(_recentPitches.Count / 2);

        // Reject a lone wild outlier (e.g. an octave error) in favor of recent continuity, but
        // still let a genuine pitch change through once it's no longer an outlier.
        if (_lastPitchHz > 0 && Math.Abs(pitchHz - median) > median * 0.5)
            pitchHz = median;

        _lastPitchHz = pitchHz;
        return (pitchHz, bestCorr);
    }

    public void Reset()
    {
        _recentPitches.Clear();
        _lastPitchHz = 0;
    }
}

/// <summary>
/// Voiced/unvoiced/mixed classification. Represented as a continuous 0..1 "voicing probability"
/// (0 = pure noise-excited/unvoiced, 1 = pure periodic/voiced) rather than three discrete states,
/// consistent with MELP's mixed-excitation concept -- see SatcomExcitationSynthesizer, which uses
/// this value to directly blend periodic and noise excitation rather than hard-switching between
/// two synthesis modes (which is what produces the "cheap voice changer" sound the design brief
/// explicitly calls out to avoid).
/// </summary>
public static class SatcomVoicingClassifier
{
    public static double Classify(ReadOnlySpan<double> frame, double pitchConfidence)
    {
        var zcr = ZeroCrossingRate(frame);
        var fromCorrelation = Math.Clamp(pitchConfidence, 0.0, 1.0);
        var fromZcr = Math.Clamp(1.0 - zcr * 4.0, 0.0, 1.0); // high zero-crossing rate => fricative/unvoiced
        return Math.Clamp(0.7 * fromCorrelation + 0.3 * fromZcr, 0.0, 1.0);
    }

    private static double ZeroCrossingRate(ReadOnlySpan<double> frame)
    {
        if (frame.Length < 2) return 0.0;
        var crossings = 0;
        for (var i = 1; i < frame.Length; i++)
            if (frame[i] != 0 && Math.Sign(frame[i]) != Math.Sign(frame[i - 1]))
                crossings++;
        return (double)crossings / frame.Length;
    }
}
