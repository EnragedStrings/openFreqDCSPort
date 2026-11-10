using OpenFreq.Common.Eccm;
using OpenFreqAudio.Eccm;

namespace OpenFreq.Client.Models.Dcs;

/// <summary>
/// A DCS radio's HAVE QUICK / SINCGARS state as OpenFreqDCS.lua exports it (docs/jamming/HAVE_QUICK.md,
/// research-05 §4). Only the A-10C II ARC-210 (ECCM page, <c>source = "cockpit"</c>) and the ARC-164's "A" knob
/// (<c>source = "knob"</c>, virtual HQ) report one; everything else is null (manual entry on the channel card).
/// </summary>
public sealed record DcsEccmState
{
    /// <summary>"hq1", "hq2", "sg", "sg_cs", "sg_cue" or "off".</summary>
    public string Mode { get; set; } = "off";

    /// <summary>"A12.300" (HQ) or "F001" (SINCGARS).</summary>
    public string? Net { get; set; }

    /// <summary>ARC-210 readiness line: "" = ready, "NO TIME", "NO WOD", "NO MWOD", "NO OPER DAY", "NO FMT",
    /// "INVALID NET ID", "NO FILL". Null when the source has none (the knob).</summary>
    public string? Status { get; set; }

    public int? EccmChannel { get; set; }

    /// <summary>SINCGARS late-entry label ("LE" / "LFn").</summary>
    public string? SgLateEntry { get; set; }

    /// <summary>"cockpit" or "knob".</summary>
    public string? Source { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public HopMode HopMode => Mode?.ToLowerInvariant() switch
    {
        "hq1" or "hq2" => HopMode.HaveQuick,
        "sg" or "sg_cs" or "sg_cue" => HopMode.Sincgars,
        _ => HopMode.None
    };

    /// <summary>The channel-card / resolver settings this cockpit state stands for. The cockpit's readiness line is
    /// passed through (it knows its own fill and TOD); the knob has none, so the radio is assumed GPS-timed.</summary>
    public EccmSlotSettings ToSettings() => new()
    {
        Mode = HopMode,
        Net = Net,
        CockpitStatus = Source == "cockpit" ? Status ?? "" : null,
        Source = Source == "knob" ? EccmSource.Knob : EccmSource.Cockpit,
        TodValid = true
    };
}
