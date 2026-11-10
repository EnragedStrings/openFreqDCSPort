using System.Globalization;
using System.Text.Json.Serialization;
using OpenFreq.Common.Jamming;
using OpenFreqAudio.Eccm;

namespace OpenFreq.Common.Eccm;

/// <summary>
/// The mission's ECCM comm plan as the server holds it (<c>OpenFreq.Server.json</c> → <c>eccm</c>): the "fill"
/// every aircraft gets. Contains raw key material (WODs, TRANSEC key), so it never goes on the wire as is; the
/// server sends <see cref="ToCommPlan"/> (fingerprints only). See docs/jamming/HAVE_QUICK.md.
/// </summary>
public sealed class EccmFill
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>Mission date (yyyy-MM-dd): the HAVE QUICK operating day (MWOD selection). Default: today, UTC.</summary>
    [JsonPropertyName("missionDate")] public string? MissionDate { get; set; }

    /// <summary>Hop rate, hops/s ([EST] default 100).</summary>
    [JsonPropertyName("hopRateHz")] public double? HopRateHz { get; set; }

    /// <summary>HAVE QUICK TOD sync window, ms ([EST] default 50).</summary>
    [JsonPropertyName("haveQuickSyncWindowMs")] public double? HaveQuickSyncWindowMs { get; set; }

    /// <summary>SINCGARS sync window, ms (default ±4000).</summary>
    [JsonPropertyName("sincgarsSyncWindowMs")] public double? SincgarsSyncWindowMs { get; set; }

    [JsonPropertyName("natoChannels")] public int? NatoChannels { get; set; }
    [JsonPropertyName("nonNatoChannels")] public int? NonNatoChannels { get; set; }

    /// <summary>MWOD: up to 6 dated WODs.</summary>
    [JsonPropertyName("mwod")] public List<EccmWodEntry> Mwod { get; set; } = [];

    /// <summary>FMT / training frequency list, kHz (5–16 entries). Default: a built-in list of 8.</summary>
    [JsonPropertyName("fmtFrequenciesKhz")] public List<int>? FmtFrequenciesKhz { get; set; }

    [JsonPropertyName("sincgars")] public EccmSincgarsFill? Sincgars { get; set; }

    /// <summary>Operating day: <see cref="MissionDate"/>, else today (UTC).</summary>
    public DateOnly OperatingDay(DateTime utcNow) =>
        DateOnly.TryParseExact(MissionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : DateOnly.FromDateTime(utcNow);

    /// <summary>
    /// What the server broadcasts: every WOD reduced to its fingerprint for its day, the TRANSEC key to a
    /// fingerprint, invalid entries dropped (and listed in <paramref name="errors"/>).
    /// </summary>
    public EccmCommPlan ToCommPlan(DateTime utcNow, List<string>? errors = null)
    {
        var plan = new EccmCommPlan
        {
            MissionDate = OperatingDay(utcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            MissionDateFromServer = !string.IsNullOrWhiteSpace(MissionDate),
            HopRateHz = HopRateHz is > 0 ? HopRateHz.Value : HopSlotState.DefaultHopRateHz,
            HaveQuickSyncWindowMs = HaveQuickSyncWindowMs is >= 0
                ? HaveQuickSyncWindowMs.Value
                : HopSlotState.DefaultHaveQuickSyncWindowMs,
            SincgarsSyncWindowMs = SincgarsSyncWindowMs is >= 0
                ? SincgarsSyncWindowMs.Value
                : HopSlotState.DefaultSincgarsSyncWindowMs,
            NatoChannels = NatoChannels is > 0 ? NatoChannels.Value : Hopset.DefaultNatoChannels,
            NonNatoChannels = NonNatoChannels is > 0 ? NonNatoChannels.Value : Hopset.DefaultNonNatoChannels,
            FmtFrequenciesKhz = FmtFrequenciesKhz?.ToList()
        };

        foreach (var entry in Mwod.Take(MwodSet.MaxEntries))
        {
            if (!DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var day))
            {
                errors?.Add($"eccm.mwod: bad date \"{entry.Date}\" (yyyy-MM-dd)");
                continue;
            }

            var wod = entry.Segments is { Count: > 0 }
                ? HaveQuickWod.TryParse(entry.Segments, out var err)
                : HaveQuickWod.TryParse(entry.Wod, out err);
            if (wod == null)
            {
                errors?.Add($"eccm.mwod {entry.Date}: {err}");
                continue;
            }

            plan.Mwod.Add(new EccmWodFingerprint
            {
                Date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Fingerprint = wod.Fingerprint(day),
                Label = entry.Label
            });
        }

        if (Mwod.Count > MwodSet.MaxEntries)
            errors?.Add($"eccm.mwod: only {MwodSet.MaxEntries} WODs fit in a radio; the rest are ignored");

        if (Sincgars != null)
        {
            var lockouts = Sincgars.Lockouts ?? [];
            plan.Sincgars = new EccmSincgarsPlan
            {
                KeyFingerprint = string.IsNullOrEmpty(Sincgars.TransecKey)
                    ? null
                    : EccmResolver.SincgarsKeyFingerprint(Sincgars.TransecKey, lockouts),
                Lockouts = lockouts.ToList()
            };
        }

        return plan;
    }
}

public sealed class EccmWodEntry
{
    [JsonPropertyName("date")] public string Date { get; set; } = "";

    /// <summary>The WOD as one string: 6 segments separated by spaces ("300.050 251.000 …" or "300050 …").</summary>
    [JsonPropertyName("wod")] public string? Wod { get; set; }

    /// <summary>Alternatively the 6 segments as an array.</summary>
    [JsonPropertyName("segments")] public List<string>? Segments { get; set; }

    [JsonPropertyName("label")] public string? Label { get; set; }
}

public sealed class EccmSincgarsFill
{
    /// <summary>TRANSEC key (any text; only its fingerprint leaves the server).</summary>
    [JsonPropertyName("transecKey")] public string? TransecKey { get; set; }

    /// <summary>Lockout bands removed from the hopset (kHz).</summary>
    [JsonPropertyName("lockouts")] public List<JammerBand>? Lockouts { get; set; }
}

/// <summary>
/// The comm plan as broadcast to clients (<c>EccmCommPlanUpdateMessage</c>): fingerprints, never WOD digits.
/// </summary>
public sealed class EccmCommPlan
{
    [JsonPropertyName("missionDate")] public string MissionDate { get; set; } = "";

    /// <summary>True when the date was set by the mission maker (not just the server's today).</summary>
    [JsonPropertyName("missionDateFromServer")] public bool MissionDateFromServer { get; set; }

    [JsonPropertyName("hopRateHz")] public double HopRateHz { get; set; } = HopSlotState.DefaultHopRateHz;

    [JsonPropertyName("haveQuickSyncWindowMs")]
    public double HaveQuickSyncWindowMs { get; set; } = HopSlotState.DefaultHaveQuickSyncWindowMs;

    [JsonPropertyName("sincgarsSyncWindowMs")]
    public double SincgarsSyncWindowMs { get; set; } = HopSlotState.DefaultSincgarsSyncWindowMs;

    [JsonPropertyName("natoChannels")] public int NatoChannels { get; set; } = Hopset.DefaultNatoChannels;
    [JsonPropertyName("nonNatoChannels")] public int NonNatoChannels { get; set; } = Hopset.DefaultNonNatoChannels;

    [JsonPropertyName("mwod")] public List<EccmWodFingerprint> Mwod { get; set; } = [];

    [JsonPropertyName("fmtFrequenciesKhz")] public List<int>? FmtFrequenciesKhz { get; set; }

    [JsonPropertyName("sincgars")] public EccmSincgarsPlan? Sincgars { get; set; }

    public DateOnly? MissionDay =>
        DateOnly.TryParseExact(MissionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}

public sealed class EccmWodFingerprint
{
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
}

public sealed class EccmSincgarsPlan
{
    /// <summary>Fingerprint of TRANSEC key + lockouts; null = the built-in default key.</summary>
    [JsonPropertyName("keyFingerprint")] public string? KeyFingerprint { get; set; }

    [JsonPropertyName("lockouts")] public List<JammerBand> Lockouts { get; set; } = [];
}
