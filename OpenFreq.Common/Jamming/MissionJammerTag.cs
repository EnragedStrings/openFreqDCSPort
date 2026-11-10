using System.Globalization;
using OpenFreqAudio.Jamming;

namespace OpenFreq.Common.Jamming;

/// <summary>
/// A parsed mission-jammer name tag (docs/jamming/MISSION_JAMMERS.md §1): <c>JAM:&lt;preset&gt;[;key=value...]</c> or
/// <c>JAM:@&lt;configId&gt;[;key=value...]</c>. <see cref="Overlay"/> carries only the fields the tag sets.
/// </summary>
public sealed class MissionJammerTag
{
    /// <summary>Preset id (<c>JAM:r934b</c>), or null for a template reference.</summary>
    public string? PresetId { get; init; }

    /// <summary>Server-config jammer id (<c>JAM:@sinitsa</c>), or null for a preset.</summary>
    public string? TemplateId { get; init; }

    /// <summary>Fields set by the tag's key=value pairs (everything else null / empty).</summary>
    public required JammerDefinition Overlay { get; init; }

    /// <summary>Targeting mode named by the tag (<c>mode=</c>), or null to keep the template's / scripted.</summary>
    public JammerTargetingMode? Mode { get; init; }

    public List<string> Warnings { get; } = [];

    /// <summary>
    /// Parses <paramref name="name"/> if it starts with <paramref name="prefix"/> (case-sensitive), else null. The tag
    /// ends at the first '#' (DCS's copy suffix, e.g. " #001"); surrounding spaces are trimmed. Returns null too when the tag has no preset/config id. Unknown keys and bad
    /// values become <see cref="Warnings"/> and are skipped.
    /// </summary>
    public static MissionJammerTag? Parse(string? name, string prefix = "JAM:")
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(prefix) || !name.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var body = name[prefix.Length..];
        var end = body.IndexOf('#');
        if (end >= 0) body = body[..end];

        var parts = body.Split(';', StringSplitOptions.TrimEntries);
        var head = parts[0];
        if (head.Length == 0 || head.Contains('=') || head.Contains(' ')) return null;
        parts = parts.Where(s => s.Length > 0).ToArray();
        string? preset = null, template = null;
        if (head.StartsWith('@'))
            template = head[1..];
        else
            preset = head.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(preset) && string.IsNullOrWhiteSpace(template)) return null;

        var overlay = new JammerDefinition { Enabled = true, Targeting = new JammerTargeting() };
        var warnings = new List<string>();
        JammerTargetingMode? modeSet = null;

        foreach (var part in parts.Skip(1))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                warnings.Add($"'{part}' is not key=value");
                continue;
            }

            var key = part[..eq].Trim().ToLowerInvariant();
            var value = part[(eq + 1)..].Trim();
            switch (key)
            {
                case "mode":
                    if (Enum.TryParse<JammerTargetingMode>(value, ignoreCase: true, out var mode))
                        modeSet = mode;
                    else warnings.Add($"mode '{value}' is not scripted or auto");
                    break;
                case "spot":
                    if (TryMhzList(value, out var spots))
                    {
                        overlay.Targeting.SpotFrequenciesKhz = spots;
                    }
                    else warnings.Add($"spot '{value}' is not a list of MHz");
                    break;
                case "protect":
                    if (TryMhzList(value, out var protect))
                    {
                        overlay.Targeting.ProtectedFrequenciesKhz = protect;
                    }
                    else warnings.Add($"protect '{value}' is not a list of MHz");
                    break;
                case "band":
                    if (TryBands(value, out var bands))
                    {
                        overlay.Targeting.Bands = bands;
                    }
                    else warnings.Add($"band '{value}' is not lo-hi MHz ranges");
                    break;
                case "autoband":
                    if (TryBands(value, out var autoBands))
                    {
                        overlay.Targeting.AutoBands = autoBands;
                    }
                    else warnings.Add($"autoband '{value}' is not lo-hi MHz ranges");
                    break;
                case "power":
                    if (TryPositive(value, out var power)) overlay.TxPowerW = power;
                    else warnings.Add($"power '{value}' is not a positive number of watts");
                    break;
                case "gain":
                    if (TryNumber(value, out var gain) && gain is >= -20 and <= 60) overlay.AntennaGainDbi = gain;
                    else warnings.Add($"gain '{value}' is not dBi");
                    break;
                case "az":
                    if (TryNumber(value, out var az))
                    {
                        overlay.AzimuthDeg = ((az % 360) + 360) % 360;
                        overlay.Pattern = JammerAntennaPattern.Sector;
                    }
                    else warnings.Add($"az '{value}' is not degrees");
                    break;
                case "bw":
                    if (TryPositive(value, out var bw) && bw <= 360)
                    {
                        overlay.BeamwidthDeg = bw;
                        overlay.Pattern = JammerAntennaPattern.Sector;
                    }
                    else warnings.Add($"bw '{value}' is not a beamwidth in degrees");
                    break;
                case "tech":
                    if (Enum.TryParse<JammerTechnique>(value, ignoreCase: true, out var tech)) overlay.Technique = tech;
                    else warnings.Add($"tech '{value}' is not a technique");
                    break;
                case "resp":
                    if (value is "1" or "true" or "yes") overlay.Responsive = true;
                    else if (value is "0" or "false" or "no") overlay.Responsive = false;
                    else warnings.Add($"resp '{value}' is not 0/1");
                    break;
                case "name":
                    if (value.Length > 0) overlay.Name = value;
                    break;
                default:
                    warnings.Add($"unknown key '{key}'");
                    break;
            }
        }

        var tag = new MissionJammerTag
        {
            PresetId = string.IsNullOrWhiteSpace(preset) ? null : preset,
            TemplateId = string.IsNullOrWhiteSpace(template) ? null : template,
            Overlay = overlay,
            Mode = modeSet
        };
        tag.Warnings.AddRange(warnings);
        return tag;
    }

    /// <summary>
    /// The jammer definition for a unit carrying this tag: a copy of the template (or a fresh definition of the
    /// preset) with the tag's fields laid over it. The caller sets id, position and alive. Targeting from the tag
    /// replaces only the targeting fields it names.
    /// </summary>
    public JammerDefinition BuildDefinition(JammerDefinition? template)
    {
        var d = template?.Clone() ?? new JammerDefinition { PresetId = PresetId };
        d.Template = false;
        d.Enabled = true;
        var o = Overlay;
        d.Name = o.Name ?? d.Name;
        d.TxPowerW = o.TxPowerW ?? d.TxPowerW;
        d.AntennaGainDbi = o.AntennaGainDbi ?? d.AntennaGainDbi;
        d.Pattern = o.Pattern ?? d.Pattern;
        d.AzimuthDeg = o.AzimuthDeg ?? d.AzimuthDeg;
        d.BeamwidthDeg = o.BeamwidthDeg ?? d.BeamwidthDeg;
        d.Technique = o.Technique ?? d.Technique;
        d.Responsive = o.Responsive ?? d.Responsive;

        d.Targeting ??= new JammerTargeting();
        var t = o.Targeting;
        if (Mode is { } mode) d.Targeting.Mode = mode;
        if (t.SpotFrequenciesKhz.Count > 0) d.Targeting.SpotFrequenciesKhz = t.SpotFrequenciesKhz.ToList();
        if (t.Bands.Count > 0) d.Targeting.Bands = t.Bands.ToList();
        if (t.AutoBands.Count > 0) d.Targeting.AutoBands = t.AutoBands.ToList();
        if (t.ProtectedFrequenciesKhz.Count > 0) d.Targeting.ProtectedFrequenciesKhz = t.ProtectedFrequenciesKhz.ToList();
        return d;
    }

    private static bool TryNumber(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool TryPositive(string s, out double value) => TryNumber(s, out value) && value > 0;

    private static int MhzToKhz(double mhz) => (int)Math.Round(mhz * 1000.0);

    private static bool TryMhzList(string s, out List<int> khz)
    {
        khz = [];
        foreach (var item in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryPositive(item, out var mhz) || mhz > 100_000) return false;
            khz.Add(MhzToKhz(mhz));
        }

        return khz.Count > 0;
    }

    private static bool TryBands(string s, out List<JammerBand> bands)
    {
        bands = [];
        foreach (var item in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = item.IndexOf('-', 1);
            if (dash <= 0 || !TryPositive(item[..dash], out var lo) || !TryPositive(item[(dash + 1)..], out var hi) ||
                hi <= lo || hi > 100_000)
                return false;
            bands.Add(new JammerBand(MhzToKhz(lo), MhzToKhz(hi)));
        }

        return bands.Count > 0;
    }
}
