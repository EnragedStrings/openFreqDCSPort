namespace OpenFreq.Common.Jamming;

/// <summary>One slice of spectrum a jammer is radiating on right now.</summary>
public sealed class JammerEmission
{
    /// <summary>Centre frequency, kHz.</summary>
    public double CenterKhz { get; set; }

    /// <summary>Occupied bandwidth, kHz (25 for a spot, the band width for barrage / partial-band).</summary>
    public double BandwidthKhz { get; set; }

    /// <summary>Share of one PA's <see cref="JammerDefinition.TxPowerW"/> radiated in this slice (0..1).</summary>
    public double PowerFraction { get; set; } = 1.0;

    public JammerEmission()
    {
    }

    public JammerEmission(double centerKhz, double bandwidthKhz, double powerFraction)
    {
        CenterKhz = centerKhz;
        BandwidthKhz = bandwidthKhz;
        PowerFraction = powerFraction;
    }

    public double StartKhz => CenterKhz - BandwidthKhz / 2.0;
    public double EndKhz => CenterKhz + BandwidthKhz / 2.0;
}

/// <summary>Scripted targeting: what a resolved jammer radiates on (phase 1; the auto brain comes in phase 2).</summary>
public static class ScriptedTargeting
{
    /// <summary>Notch width around a protected frequency, kHz (one 25 kHz channel).</summary>
    public const double ProtectedNotchKhz = 25.0;

    /// <summary>
    /// The emissions of a resolved jammer that is on: one per configured spot (dropped if outside the tuning range
    /// or on a user-protected frequency; truncated to MaxSimultaneousSpots) and one or more per band (clipped to the
    /// tuning range, with protected frequencies — guard by default — notched out). Each configured target gets
    /// <see cref="JammingMath.PowerFractionPerEmission"/> of the PA power; a notched band's pieces share their
    /// band's share in proportion to width.
    /// </summary>
    public static List<JammerEmission> Emissions(JammerDefinition resolved)
    {
        var targeting = resolved.Targeting;
        if (targeting.Mode != JammerTargetingMode.Scripted) return [];

        var min = resolved.MinFrequencyKhz ?? 0;
        var max = resolved.MaxFrequencyKhz ?? int.MaxValue;
        var spotBw = targeting.SpotBandwidthKhz > 0 ? targeting.SpotBandwidthKhz : 25.0;

        var userProtected = targeting.ProtectedFrequenciesKhz ?? [];
        var notches = userProtected.Select(f => (double)f).ToList();
        if (targeting.ProtectGuard)
            notches.AddRange(JammingMath.GuardFrequenciesKhz.Select(f => (double)f));

        var spots = (targeting.SpotFrequenciesKhz ?? [])
            .Where(f => f >= min && f <= max)
            .Where(f => !userProtected.Any(p => Math.Abs(p - f) < spotBw / 2.0))
            .Distinct()
            .Take(Math.Max(1, resolved.MaxSimultaneousSpots ?? 1))
            .ToList();

        var bands = new List<List<(double Lo, double Hi)>>();
        foreach (var band in targeting.Bands ?? [])
        {
            var lo = (double)Math.Max(Math.Min(band.StartKhz, band.EndKhz), min);
            var hi = (double)Math.Min(Math.Max(band.StartKhz, band.EndKhz), max);
            if (hi - lo < 1e-6) continue;
            var pieces = Notch(lo, hi, notches);
            if (pieces.Count > 0) bands.Add(pieces);
        }

        var targets = spots.Count + bands.Count;
        var share = JammingMath.PowerFractionPerEmission(targets, resolved.PowerAmplifiers ?? 1);

        var result = spots.Select(f => new JammerEmission(f, spotBw, share)).ToList();
        foreach (var pieces in bands)
        {
            var total = pieces.Sum(p => p.Hi - p.Lo);
            result.AddRange(pieces.Select(p =>
                new JammerEmission((p.Lo + p.Hi) / 2.0, p.Hi - p.Lo, share * (p.Hi - p.Lo) / total)));
        }

        return result;
    }

    private static List<(double Lo, double Hi)> Notch(double lo, double hi, IEnumerable<double> notches)
    {
        var pieces = new List<(double Lo, double Hi)> { (lo, hi) };
        foreach (var n in notches.OrderBy(n => n))
        {
            var nLo = n - ProtectedNotchKhz / 2.0;
            var nHi = n + ProtectedNotchKhz / 2.0;
            pieces = pieces.SelectMany(p =>
            {
                if (nHi <= p.Lo || nLo >= p.Hi) return [p];
                var parts = new List<(double, double)>();
                if (nLo > p.Lo) parts.Add((p.Lo, nLo));
                if (nHi < p.Hi) parts.Add((nHi, p.Hi));
                return parts;
            }).ToList();
        }

        return pieces.Where(p => p.Hi - p.Lo > 1e-6).ToList();
    }
}
