using System;

namespace OpenFreqAudio.Satcom;

/// <summary>
/// Linear predictive coding: autocorrelation analysis, Levinson-Durbin recursion, and an
/// all-pole synthesis filter. PUBLIC STANDARD textbook DSP (Levinson 1947/Durbin 1960; see any
/// standard speech-coding reference, e.g. Rabiner &amp; Schafer) -- the same underlying spectral-
/// envelope technique MELP/CELP-class codecs use, though this implementation makes no claim of
/// bitstream compatibility with any specific standard (see docs/SATCOM_SIMULATION.md).
///
/// Reflection coefficients (the Levinson-Durbin recursion's k[i] values, |k| &lt; 1 by construction)
/// are used as the spectral-envelope representation actually quantized/transmitted/corrupted,
/// rather than raw LPC coefficients -- this guarantees a stable synthesis filter even after
/// aggressive quantization or channel corruption, since re-deriving LPC coefficients from
/// clamped |k|&lt;1 reflection coefficients can never produce an unstable filter.
/// </summary>
public static class SatcomLpc
{
    public static double[] Autocorrelate(ReadOnlySpan<double> frame, int maxLag)
    {
        var result = new double[maxLag + 1];
        for (var lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (var n = 0; n < frame.Length - lag; n++)
                sum += frame[n] * frame[n + lag];
            result[lag] = sum;
        }
        return result;
    }

    /// <summary>Levinson-Durbin recursion. Returns 1-indexed arrays of length order+1 (index 0
    /// unused/placeholder) for both LPC coefficients and reflection coefficients, plus the final
    /// prediction error power.</summary>
    public static (double[] Lpc, double[] Reflection, double Error) LevinsonDurbin(double[] autocorr, int order)
    {
        var reflection = new double[order + 1];
        var a = new double[order + 1];
        a[0] = 1.0;
        var error = autocorr.Length > 0 ? autocorr[0] : 0.0;

        if (error <= 1e-12)
            return (a, reflection, 1e-12);

        for (var i = 1; i <= order; i++)
        {
            double acc = i < autocorr.Length ? autocorr[i] : 0.0;
            for (var j = 1; j < i; j++)
                acc -= a[j] * autocorr[i - j];

            var k = error > 1e-12 ? acc / error : 0.0;
            k = Math.Clamp(k, -0.999, 0.999); // guarantees a stable filter (see class doc)
            reflection[i] = k;

            var aNew = (double[])a.Clone();
            aNew[i] = k;
            for (var j = 1; j < i; j++)
                aNew[j] = a[j] - k * a[i - j];
            a = aNew;

            error *= 1.0 - k * k;
            if (error < 1e-12) error = 1e-12;
        }

        return (a, reflection, error);
    }

    /// <summary>Rebuilds stable LPC coefficients from (possibly quantized/corrupted, but always
    /// |k|&lt;1) reflection coefficients -- used by the decoder. <paramref name="reflection"/> must
    /// be 1-indexed length order+1, matching LevinsonDurbin's output shape.</summary>
    public static double[] ReflectionToLpc(double[] reflection, int order)
    {
        var a = new double[order + 1];
        a[0] = 1.0;
        for (var i = 1; i <= order; i++)
        {
            var k = i < reflection.Length ? Math.Clamp(reflection[i], -0.999, 0.999) : 0.0;
            var aNew = (double[])a.Clone();
            aNew[i] = k;
            for (var j = 1; j < i; j++)
                aNew[j] = a[j] - k * a[i - j];
            a = aNew;
        }
        return a;
    }
}

/// <summary>All-pole LPC synthesis filter: y[n] = excitation[n] + sum(a[i]*y[n-i]) -- the sign
/// here must match SatcomLpc.LevinsonDurbin/ReflectionToLpc's own convention (Rabiner &amp;
/// Schafer's autocorrelation method: prediction x&#770;[n] = sum a_i*x[n-i], so the reconstructed
/// signal is x[n] = e[n] + x&#770;[n]). Stateful across calls (keeps the last `order` output
/// samples) so consecutive frames don't click at the seam; <see cref="Reset"/> between unrelated
/// streams.</summary>
public sealed class LpcSynthesisFilter
{
    private readonly double[] _history;
    private readonly int _order;

    public LpcSynthesisFilter(int order)
    {
        _order = order;
        _history = new double[order];
    }

    /// <summary>Process one excitation sample through the filter defined by
    /// <paramref name="lpcCoefficients"/> (1-indexed, length order+1, from
    /// <see cref="SatcomLpc.ReflectionToLpc"/>). Output-limited: never returns NaN/Infinity or an
    /// extreme amplitude, regardless of how corrupted the coefficients are (see
    /// docs/SATCOM_SIMULATION.md's "Audio Artifact Safety" requirement).</summary>
    public double Process(double excitation, double[] lpcCoefficients)
    {
        double prediction = 0;
        for (var i = 1; i <= _order; i++)
            prediction += lpcCoefficients[i] * _history[i - 1];

        var output = excitation + prediction;
        if (double.IsNaN(output) || double.IsInfinity(output)) output = 0.0;
        output = Math.Clamp(output, -32000.0, 32000.0);

        for (var i = _order - 1; i > 0; i--)
            _history[i] = _history[i - 1];
        _history[0] = output;

        return output;
    }

    public void Reset() => Array.Clear(_history);
}
