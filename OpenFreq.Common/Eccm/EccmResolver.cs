using System.Globalization;
using OpenFreq.Common.Jamming;
using OpenFreqAudio.Eccm;

namespace OpenFreq.Common.Eccm;

/// <summary>Where a slot's ECCM settings come from.</summary>
public enum EccmSource
{
    /// <summary>Entered on the channel card (any radio, GCI).</summary>
    Manual,

    /// <summary>Read from the cockpit (A-10C II ARC-210 ECCM page).</summary>
    Cockpit,

    /// <summary>"Virtual HAVE QUICK": the ARC-164 100 MHz knob at "A" selects the net; OpenFreq supplies the fill.</summary>
    Knob
}

/// <summary>Readiness strings, as the ARC-210 shows them ("" = ready).</summary>
public static class EccmReadiness
{
    public const string Ready = "";
    public const string NoTime = "NO TIME";
    public const string NoWod = "NO WOD";
    public const string NoMwod = "NO MWOD";
    public const string NoOperDay = "NO OPER DAY";
    public const string NoFmt = "NO FMT";
    public const string InvalidNetId = "INVALID NET ID";
    public const string NoNet = "NO NET";
}

/// <summary>
/// One radio slot's ECCM settings, whatever their source.
/// </summary>
public sealed record EccmSlotSettings
{
    public HopMode Mode { get; init; }

    /// <summary>HAVE QUICK net ("A12.300") or SINCGARS net ("F001" / "1").</summary>
    public string? Net { get; init; }

    /// <summary>A WOD entered by hand (6 segments); overrides the comm plan's MWOD.</summary>
    public string? ManualWod { get; init; }

    /// <summary>A SINCGARS TRANSEC key entered by hand; overrides the comm plan's.</summary>
    public string? ManualSincgarsKey { get; init; }

    /// <summary>The radio has a TOD (GPS / TOD receive / net sync). Manual default: true (GPS).</summary>
    public bool TodValid { get; init; } = true;

    /// <summary>The radio's TOD clock minus net time, ms (0 after GPS / TOD receive; anything after an emergency
    /// TOD start).</summary>
    public long TodOffsetMs { get; init; }

    /// <summary>The cockpit's own readiness line (ARC-210 <c>active_eccm_channel_HQI_status</c>), "" = ready; null
    /// when the cockpit doesn't report one.</summary>
    public string? CockpitStatus { get; init; }

    public EccmSource Source { get; init; } = EccmSource.Manual;

    public static readonly EccmSlotSettings Off = new();
}

/// <summary>The resolved state of a slot: what the DSP gate, the TX stamp and the jamming model use.</summary>
/// <param name="State">Null when the slot isn't hopping.</param>
/// <param name="Hopset">The channels this net hops over (null when not hopping or the net is invalid).</param>
/// <param name="Readiness">"" when ready, else the reason it can't hop ("NO TIME", "NO WOD", …).</param>
/// <param name="Description">Short text for the channel card, e.g. "HQ A12.325 · NATO N=2000".</param>
public sealed record EccmResolution(HopSlotState? State, Hopset? Hopset, string Readiness, string Description)
{
    public static readonly EccmResolution Off = new(null, null, EccmReadiness.Ready, "");

    public bool IsHopping => State != null;
    public bool IsReady => State != null && Readiness == EccmReadiness.Ready;
}

/// <summary>
/// Turns a slot's settings plus the comm plan into its hopping state (docs/jamming/HAVE_QUICK.md). Pure.
/// </summary>
public static class EccmResolver
{
    /// <summary>SINCGARS key used when neither the comm plan nor the player gives one, so SINCGARS nets work out of
    /// the box (nets are still separated by net number and lockouts).</summary>
    public const string DefaultSincgarsKey = "OPENFREQ-DEFAULT-TRANSEC";

    public static string SincgarsKeyFingerprint(string key, IEnumerable<JammerBand>? lockouts)
    {
        var lo = string.Join(",", (lockouts ?? [])
            .Select(b => (Math.Min(b.StartKhz, b.EndKhz), Math.Max(b.StartKhz, b.EndKhz)))
            .Order()
            .Select(b => $"{b.Item1}-{b.Item2}"));
        return EccmFingerprint.Of("SGKEY", key, lo);
    }

    /// <summary>The fill a cockpit that says it is ready gets when OpenFreq has no WOD for it (no comm plan, no
    /// manual WOD): one shared default per day, so ready ARC-210s on the same net still talk.</summary>
    public static string DefaultWodFingerprint(DateOnly day) =>
        EccmFingerprint.Of("HQWOD-DEFAULT", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>"F001" style SINCGARS net: digits are padded, text is upper-cased.</summary>
    public static string NormalizeSincgarsNet(string? net)
    {
        var t = (net ?? "").Trim().ToUpperInvariant();
        if (t.Length == 0) return "";
        if (t.All(char.IsAsciiDigit) && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return $"F{n:000}";
        return t;
    }

    public static EccmResolution Resolve(EccmSlotSettings settings, EccmCommPlan? plan, DateTime utcNow)
    {
        return settings.Mode switch
        {
            HopMode.HaveQuick => ResolveHaveQuick(settings, plan, utcNow),
            HopMode.Sincgars => ResolveSincgars(settings, plan),
            _ => EccmResolution.Off
        };
    }

    private static EccmResolution ResolveHaveQuick(EccmSlotSettings s, EccmCommPlan? plan, DateTime utcNow)
    {
        var day = plan?.MissionDay ?? DateOnly.FromDateTime(utcNow);
        var hopRate = plan?.HopRateHz ?? HopSlotState.DefaultHopRateHz;
        var window = plan?.HaveQuickSyncWindowMs ?? HopSlotState.DefaultHaveQuickSyncWindowMs;
        var cockpit = s.CockpitStatus?.Trim().ToUpperInvariant();

        var net = HaveQuickNet.TryParse(s.Net);
        var kind = net?.Kind ?? HaveQuickNetKind.Invalid;
        var netText = net?.ToString() ?? (s.Net ?? "").Trim().ToUpperInvariant();

        Hopset? hopset = kind switch
        {
            HaveQuickNetKind.Nato or HaveQuickNetKind.HqI => Hopset.Nato(plan?.NatoChannels ?? Hopset.DefaultNatoChannels),
            HaveQuickNetKind.NonNato => Hopset.NonNato(plan?.NonNatoChannels ?? Hopset.DefaultNonNatoChannels),
            HaveQuickNetKind.Training or HaveQuickNetKind.Fmt => Hopset.Fmt(plan?.FmtFrequenciesKhz),
            _ => null
        };

        // The fill: a hand-entered WOD, else the comm plan's MWOD for the day, else (cockpit says ready) a default.
        string key = "";
        string? keyProblem = null;
        string wodNote;
        if (!string.IsNullOrWhiteSpace(s.ManualWod))
        {
            var wod = HaveQuickWod.TryParse(s.ManualWod, out _);
            if (wod != null)
            {
                key = wod.Fingerprint(day);
                wodNote = "manual WOD";
            }
            else
            {
                keyProblem = EccmReadiness.NoWod;
                wodNote = "WOD invalid";
            }
        }
        else if (plan?.Mwod.FirstOrDefault(w => w.Date == day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                 is { } planned && !string.IsNullOrEmpty(planned.Fingerprint))
        {
            key = planned.Fingerprint;
            wodNote = planned.Label is { Length: > 0 } label ? $"MWOD {label}" : "MWOD";
        }
        else if (plan?.Mwod.Count > 0)
        {
            keyProblem = EccmReadiness.NoMwod;
            wodNote = "no WOD for " + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else if (cockpit == "")
        {
            key = DefaultWodFingerprint(day);
            wodNote = "cockpit fill";
        }
        else
        {
            keyProblem = EccmReadiness.NoWod;
            wodNote = "no WOD";
        }

        var todValid = s.TodValid && cockpit != EccmReadiness.NoTime;

        // What the radio shows: the cockpit's own complaint first (it knows its fill), else ours.
        string readiness;
        if (!string.IsNullOrEmpty(cockpit)) readiness = cockpit;
        else if (net == null || kind == HaveQuickNetKind.Invalid) readiness = EccmReadiness.InvalidNetId;
        else if (keyProblem != null) readiness = keyProblem;
        else if (hopset == null || hopset.Count == 0) readiness = EccmReadiness.NoFmt;
        else if (!todValid) readiness = EccmReadiness.NoTime;
        else readiness = EccmReadiness.Ready;

        // A radio that isn't ready still hops (its identity is stamped on TX) but matches nobody.
        if (readiness is EccmReadiness.NoWod or EccmReadiness.NoMwod or EccmReadiness.NoOperDay
            or EccmReadiness.InvalidNetId or EccmReadiness.NoFmt)
            key = "";

        var identity = new HopNetIdentity(HopMode.HaveQuick, netText, key, todValid, todValid ? s.TodOffsetMs : 0);
        var kindText = kind switch
        {
            HaveQuickNetKind.Nato => "NATO",
            HaveQuickNetKind.NonNato => "non-NATO",
            HaveQuickNetKind.HqI => "HQ I",
            HaveQuickNetKind.Training => "training",
            HaveQuickNetKind.Fmt => "FMT",
            _ => "invalid"
        };
        var description = $"HQ {netText} · {kindText}" + (hopset != null ? $" N={hopset.Count}" : "") + $" · {wodNote}";
        return new EccmResolution(new HopSlotState(identity, hopRate, window), hopset, readiness, description);
    }

    private static EccmResolution ResolveSincgars(EccmSlotSettings s, EccmCommPlan? plan)
    {
        var hopRate = plan?.HopRateHz ?? HopSlotState.DefaultHopRateHz;
        var window = plan?.SincgarsSyncWindowMs ?? HopSlotState.DefaultSincgarsSyncWindowMs;
        var lockouts = plan?.Sincgars?.Lockouts ?? [];
        var hopset = Hopset.Sincgars(lockouts.Select(b => (b.StartKhz, b.EndKhz)));
        var net = NormalizeSincgarsNet(s.Net);

        string key;
        string keyNote;
        if (!string.IsNullOrWhiteSpace(s.ManualSincgarsKey))
        {
            key = SincgarsKeyFingerprint(s.ManualSincgarsKey.Trim(), lockouts);
            keyNote = "manual key";
        }
        else if (!string.IsNullOrEmpty(plan?.Sincgars?.KeyFingerprint))
        {
            key = plan.Sincgars.KeyFingerprint;
            keyNote = "plan key";
        }
        else
        {
            key = SincgarsKeyFingerprint(DefaultSincgarsKey, lockouts);
            keyNote = "default key";
        }

        var cockpit = s.CockpitStatus?.Trim().ToUpperInvariant();
        var todValid = s.TodValid && cockpit != EccmReadiness.NoTime;
        string readiness = net.Length == 0 ? EccmReadiness.NoNet
            : !todValid ? EccmReadiness.NoTime
            : EccmReadiness.Ready;
        if (net.Length == 0) key = "";

        var identity = new HopNetIdentity(HopMode.Sincgars, net, key, todValid, todValid ? s.TodOffsetMs : 0);
        var description = $"SG {(net.Length > 0 ? net : "—")} · N={hopset.Count}" +
                          (lockouts.Count > 0 ? $" ({lockouts.Count} lockouts)" : "") + $" · {keyNote}";
        return new EccmResolution(new HopSlotState(identity, hopRate, window), hopset, readiness, description);
    }
}
