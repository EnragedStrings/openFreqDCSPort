using System;
using NWaves.Filters.Butterworth;

namespace OpenFreqAudio.Satcom;

/// <summary>
/// Simple integer-ratio decimation/interpolation resampler between the network audio rate
/// (48 kHz, see RadioPlayback.SampleRate) and the vocoder's internal narrowband rate (8 kHz, per
/// the design brief's target). Anti-alias/reconstruction filtering reuses the same NWaves
/// Butterworth low-pass filter already used throughout this library (RadioPlayback,
/// OwnVoiceRadioRenderer) rather than introducing a new filter implementation. Not a
/// high-fidelity polyphase resampler -- adequate for narrowband communications-grade speech,
/// which is already band-limited well below 4 kHz by the vocoder's own LPC/pitch range.
/// </summary>
public sealed class SatcomDownsampler
{
    private readonly LowPassFilter _antiAlias;
    private readonly int _factor;
    private int _phase;

    public SatcomDownsampler(int inputRate, int outputRate)
    {
        if (inputRate % outputRate != 0)
            throw new ArgumentException($"inputRate ({inputRate}) must be an integer multiple of outputRate ({outputRate})");
        _factor = inputRate / outputRate;
        _antiAlias = new LowPassFilter(outputRate * 0.45 / inputRate, 4);
    }

    /// <summary>Feed one input-rate sample. Returns true and sets <paramref name="sampleOut"/>
    /// when this call produced a decimated output-rate sample (every Nth call).</summary>
    public bool Process(double sampleIn, out double sampleOut)
    {
        var filtered = _antiAlias.Process((float)sampleIn);
        _phase++;
        if (_phase >= _factor)
        {
            _phase = 0;
            sampleOut = filtered;
            return true;
        }

        sampleOut = 0.0;
        return false;
    }

    public void Reset()
    {
        _phase = 0;
    }
}

public sealed class SatcomUpsampler
{
    private readonly LowPassFilter _reconstruction;
    private readonly int _factor;

    public SatcomUpsampler(int inputRate, int outputRate)
    {
        if (outputRate % inputRate != 0)
            throw new ArgumentException($"outputRate ({outputRate}) must be an integer multiple of inputRate ({inputRate})");
        _factor = outputRate / inputRate;
        _reconstruction = new LowPassFilter(inputRate * 0.45 / outputRate, 4);
    }

    public int Factor => _factor;

    /// <summary>Produces Factor output-rate samples for one input-rate sample (zero-stuff +
    /// reconstruction low-pass, with gain compensation for the stuffed zeros).</summary>
    public void Process(double sampleIn, Span<double> samplesOut)
    {
        for (var i = 0; i < _factor && i < samplesOut.Length; i++)
        {
            var zeroStuffed = i == 0 ? sampleIn * _factor : 0.0;
            samplesOut[i] = _reconstruction.Process((float)zeroStuffed);
        }
    }
}
