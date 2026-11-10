using OpenFreq.Common.Jamming;

namespace OpenFreq.Common.Eccm;

/// <summary>
/// The set of channels a hopping net hops over (25 kHz channels, kHz centres, sorted). Used for the jamming hit
/// fraction μ = |hopset ∩ jammed spectrum| / N (research-02 §6.3), computed analytically by binary search.
/// </summary>
public sealed class Hopset
{
    /// <summary>HAVE QUICK / SINCGARS channel width, kHz.</summary>
    public const double ChannelWidthKhz = 25.0;

    public const int UhfStartKhz = 225_000;
    public const int UhfEndKhz = 399_975;
    public const int SincgarsStartKhz = 30_000;
    public const int SincgarsEndKhz = 87_975;

    /// <summary>Default HQ hopset sizes (research-02 §1.2, [EST]).</summary>
    public const int DefaultNatoChannels = 2000;
    public const int DefaultNonNatoChannels = 4000;

    /// <summary>Default FMT / training list (8 frequencies, [EST]: research-02 §1.2 "5–16"), kHz. Used when the comm
    /// plan has none.</summary>
    public static readonly IReadOnlyList<int> DefaultFmtFrequenciesKhz =
        [235_000, 252_500, 267_750, 281_250, 296_000, 311_500, 347_250, 364_750];

    /// <summary>Spectrum the UHF combat hopsets never use: guard 243.0 (±100 kHz) and the ILS glideslope pairs
    /// 328.6–335.4 MHz (research-02 §1.2).</summary>
    public static readonly IReadOnlyList<(int StartKhz, int EndKhz)> UhfExclusions =
        [(242_900, 243_100), (328_600, 335_400)];

    private readonly int[] _channels;

    public Hopset(string name, IEnumerable<int> channelsKhz)
    {
        Name = name;
        _channels = channelsKhz.Distinct().Order().ToArray();
    }

    public string Name { get; }

    /// <summary>N, the number of channels.</summary>
    public int Count => _channels.Length;

    public IReadOnlyList<int> ChannelsKhz => _channels;

    /// <summary>Channel centres strictly inside (lo, hi), kHz.</summary>
    public int CountStrictlyInside(double loKhz, double hiKhz)
    {
        if (hiKhz <= loKhz) return 0;
        var first = UpperBound(loKhz);
        var last = LowerBound(hiKhz); // first index >= hi
        return Math.Max(0, last - first);
    }

    /// <summary>Channels a jammer emission touches: every channel whose 25 kHz passband overlaps it (a spot on a
    /// channel centre hits that channel only).</summary>
    public int ChannelsHitBy(double centerKhz, double bandwidthKhz)
    {
        var half = (Math.Max(0.0, bandwidthKhz) + ChannelWidthKhz) / 2.0;
        return CountStrictlyInside(centerKhz - half, centerKhz + half);
    }

    /// <summary>μ for a set of emissions: channels hit by any of them (overlaps merged) / N.</summary>
    public double HitFraction(IEnumerable<(double CenterKhz, double BandwidthKhz)> emissions)
    {
        if (Count == 0) return 0.0;
        var spans = emissions
            .Select(e =>
            {
                var half = (Math.Max(0.0, e.BandwidthKhz) + ChannelWidthKhz) / 2.0;
                return (Lo: e.CenterKhz - half, Hi: e.CenterKhz + half);
            })
            .OrderBy(s => s.Lo)
            .ToList();
        if (spans.Count == 0) return 0.0;

        var hit = 0;
        var (lo, hi) = spans[0];
        foreach (var (l, h) in spans.Skip(1))
        {
            if (l <= hi)
            {
                hi = Math.Max(hi, h);
                continue;
            }
            hit += CountStrictlyInside(lo, hi);
            (lo, hi) = (l, h);
        }
        hit += CountStrictlyInside(lo, hi);
        return Math.Clamp((double)hit / Count, 0.0, 1.0);
    }

    /// <summary>μ for one emission.</summary>
    public double HitFraction(JammerEmission emission) => HitFraction([(emission.CenterKhz, emission.BandwidthKhz)]);

    public bool Contains(int khz) => Array.BinarySearch(_channels, khz) >= 0;

    // first index with channel > x
    private int UpperBound(double x)
    {
        int lo = 0, hi = _channels.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_channels[mid] <= x) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // first index with channel >= x
    private int LowerBound(double x)
    {
        int lo = 0, hi = _channels.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_channels[mid] < x) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ------------------------------------------------------------------ builders

    /// <summary>Every 25 kHz channel in [start, end] not inside an excluded band.</summary>
    public static List<int> Raster(int startKhz, int endKhz, IEnumerable<(int StartKhz, int EndKhz)>? excluded = null)
    {
        var ex = (excluded ?? []).Select(b => (Lo: Math.Min(b.StartKhz, b.EndKhz), Hi: Math.Max(b.StartKhz, b.EndKhz)))
            .ToList();
        var list = new List<int>();
        for (var f = startKhz; f <= endKhz; f += (int)ChannelWidthKhz)
        {
            var blocked = false;
            foreach (var (lo, hi) in ex)
                if (f >= lo && f <= hi)
                {
                    blocked = true;
                    break;
                }
            if (!blocked) list.Add(f);
        }
        return list;
    }

    /// <summary>
    /// A HAVE QUICK combat hopset: <paramref name="channelCount"/> channels spread evenly over the 225–400 MHz raster
    /// less <see cref="UhfExclusions"/>. The real NATO / non-NATO tables are not public; an even spread keeps μ for a
    /// band proportional to its width ([EST]). <paramref name="phase"/> (0..1) offsets the pick, so the NATO and
    /// non-NATO tables are different sets.
    /// </summary>
    public static Hopset HaveQuickCombat(string name, int channelCount, double phase)
    {
        var raster = Raster(UhfStartKhz, UhfEndKhz, UhfExclusions);
        var n = Math.Clamp(channelCount, 1, raster.Count);
        var picks = new int[n];
        for (var j = 0; j < n; j++)
            picks[j] = raster[Math.Min(raster.Count - 1, (int)Math.Floor((j + phase) * raster.Count / n))];
        return new Hopset(name, picks);
    }

    /// <summary>The NATO (.x25) hopset.</summary>
    public static Hopset Nato(int channelCount = DefaultNatoChannels) =>
        HaveQuickCombat("HQ NATO", channelCount, 0.25);

    /// <summary>The non-NATO (.x50) hopset.</summary>
    public static Hopset NonNato(int channelCount = DefaultNonNatoChannels) =>
        HaveQuickCombat("HQ non-NATO", channelCount, 0.75);

    /// <summary>A training / FMT net's frequency list.</summary>
    public static Hopset Fmt(IEnumerable<int>? frequenciesKhz) =>
        new("HQ FMT", (frequenciesKhz ?? DefaultFmtFrequenciesKhz).Where(f => f is >= UhfStartKhz and <= UhfEndKhz));

    /// <summary>SINCGARS: every channel 30.000–87.975 MHz (2320) less the lockout bands.</summary>
    public static Hopset Sincgars(IEnumerable<(int StartKhz, int EndKhz)>? lockouts = null) =>
        new("SINCGARS", Raster(SincgarsStartKhz, SincgarsEndKhz, lockouts));
}
