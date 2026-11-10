using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenFreq.Common.Eccm;

/// <summary>What a HAVE QUICK net number selects (research-02 §1.1).</summary>
public enum HaveQuickNetKind
{
    /// <summary>Not a valid net number (the radio gives a warning tone; ARC-210 shows "INVALID NET ID").</summary>
    Invalid,

    /// <summary>HQ I basic training nets A00.000–A00.400 (5 nets): hop over the training/FMT frequency list.</summary>
    Training,

    /// <summary>Frequency Managed Training nets A00.025–A01.525: hop over the FMT list loaded in the radio.</summary>
    Fmt,

    /// <summary>Combat net, suffix .x25: NATO hopset.</summary>
    Nato,

    /// <summary>Combat net, suffix .x50: non-NATO hopset.</summary>
    NonNato,

    /// <summary>Combat net, suffix .x00 outside the training range: HQ I style net. OpenFreq gives it the NATO
    /// hopset ([EST]).</summary>
    HqI
}

/// <summary>
/// A HAVE QUICK net number A00.000–A99.975 (25 kHz steps). The last two digits select the hop table: .x25 NATO,
/// .x50 non-NATO, .x00 HQ I; A00.000–A00.400 are the 5 HQ I training nets and A00.025–A01.525 the FMT nets.
/// .x75 outside the FMT range is invalid.
/// </summary>
public readonly record struct HaveQuickNet(int Major, int Minor)
{
    private static readonly Regex Pattern = new(@"^\s*A?\s*(\d{1,2})\s*[.\s]\s*(\d{3})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Parse "A12.300", "a12.300", "12.300" or "A12 300". Null when the text isn't a net number
    /// (the major/minor are out of range or the minor isn't a 25 kHz step).</summary>
    public static HaveQuickNet? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Pattern.Match(text);
        if (!m.Success) return null;
        var major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (major is < 0 or > 99 || minor is < 0 or > 975 || minor % 25 != 0) return null;
        return new HaveQuickNet(major, minor);
    }

    /// <summary>Thousandths of the net number: A12.300 → 12300.</summary>
    public int Value => Major * 1000 + Minor;

    public HaveQuickNetKind Kind
    {
        get
        {
            var suffix = Minor % 100;
            if (Major == 0 && Minor <= 400 && suffix == 0) return HaveQuickNetKind.Training;
            if (Value is >= 25 and <= 1525 && suffix != 0) return HaveQuickNetKind.Fmt;
            return suffix switch
            {
                25 => HaveQuickNetKind.Nato,
                50 => HaveQuickNetKind.NonNato,
                0 => HaveQuickNetKind.HqI,
                _ => HaveQuickNetKind.Invalid
            };
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"A{Major:00}.{Minor:000}");
}
