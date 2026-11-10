using System.Globalization;
using System.Text;
using OpenFreqAudio.Eccm;

namespace OpenFreq.Common.Eccm;

/// <summary>
/// A HAVE QUICK II Word Of Day: 6 segments × 6 digits (research-02 §1.1). In the aircraft each segment is loaded
/// as a frequency in presets 20…15 (e.g. "300.050"), so a segment is written either as 6 digits or as
/// "ddd.ddd". Segment semantics beyond "segment 2 enables conferencing" are not public; OpenFreq treats all 36
/// digits as key material (conferencing is not modelled).
/// </summary>
public sealed class HaveQuickWod
{
    public const int SegmentCount = 6;
    public const int DigitsPerSegment = 6;

    /// <summary>The six segments, 6 digits each, no separators.</summary>
    public IReadOnlyList<string> Segments { get; }

    private HaveQuickWod(IReadOnlyList<string> segments) => Segments = segments;

    /// <summary>Parse 6 segments separated by spaces, commas, semicolons or slashes. Each segment is 6 digits,
    /// optionally written as a frequency "ddd.ddd". Returns null and an error when invalid.</summary>
    public static HaveQuickWod? TryParse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "WOD is empty";
            return null;
        }

        var parts = text.Split([' ', ',', ';', '/', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return TryParse(parts, out error);
    }

    /// <summary>Parse 6 segments given separately.</summary>
    public static HaveQuickWod? TryParse(IReadOnlyList<string>? segments, out string? error)
    {
        error = null;
        if (segments == null || segments.Count != SegmentCount)
        {
            error = $"WOD needs {SegmentCount} segments, got {segments?.Count ?? 0}";
            return null;
        }

        var result = new List<string>(SegmentCount);
        for (var i = 0; i < segments.Count; i++)
        {
            var digits = segments[i].Trim().Replace(".", "");
            if (digits.Length != DigitsPerSegment || !digits.All(char.IsAsciiDigit))
            {
                error = $"WOD segment {i + 1} \"{segments[i]}\" is not 6 digits";
                return null;
            }

            result.Add(digits);
        }

        return new HaveQuickWod(result);
    }

    /// <summary>One-way fingerprint for the operating day: what goes on the wire and seeds the hop sequence.
    /// The same WOD on another day gives another fingerprint (MWOD day keying).</summary>
    public string Fingerprint(DateOnly operatingDay) =>
        EccmFingerprint.Of("HQWOD", string.Concat(Segments), operatingDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>Display form, "300.050 251.000 …".</summary>
    public override string ToString() =>
        string.Join(' ', Segments.Select(s => s[..3] + "." + s[3..]));
}

/// <summary>A Multiple Word Of Day set: up to 6 WODs, each tagged with its day (research-02 §1.1).</summary>
public sealed class MwodSet
{
    public const int MaxEntries = 6;

    private readonly SortedDictionary<DateOnly, HaveQuickWod> _byDay = new();

    public IReadOnlyCollection<DateOnly> Days => _byDay.Keys;

    public int Count => _byDay.Count;

    /// <summary>Load a WOD for a day. Like the radio, a 7th day pushes out the oldest; a day already loaded is
    /// overwritten.</summary>
    public void Load(DateOnly day, HaveQuickWod wod)
    {
        _byDay[day] = wod;
        while (_byDay.Count > MaxEntries)
            _byDay.Remove(_byDay.Keys.First());
    }

    public HaveQuickWod? ForDay(DateOnly day) => _byDay.GetValueOrDefault(day);
}

/// <summary>The one-way fingerprint used for every piece of fill that must never go on the wire.</summary>
public static class EccmFingerprint
{
    public static string Of(params string[] parts)
    {
        var text = new StringBuilder("openfreq-eccm-v1");
        foreach (var p in parts) text.Append('|').Append(p);
        // Two independent FNV passes over different salts, folded, so the fingerprint isn't a raw FNV state.
        var a = HopSequence.Hash(text.ToString());
        var b = HopSequence.Hash("x" + text);
        return HopSequence.Mix(a, (long)b).ToString("x16", CultureInfo.InvariantCulture);
    }
}
