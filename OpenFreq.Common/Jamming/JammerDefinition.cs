using System.Text.Json.Serialization;
using OpenFreqAudio.Jamming;

namespace OpenFreq.Common.Jamming;

/// <summary>Jammer antenna pattern in azimuth (research-01 §1.4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JammerAntennaPattern>))]
public enum JammerAntennaPattern
{
    /// <summary>Omnidirectional in azimuth: full gain toward every receiver.</summary>
    Omni,

    /// <summary>Directional (LPDA/Yagi/array) pointed at <see cref="JammerDefinition.AzimuthDeg"/>: −3 dB at
    /// ±beamwidth/2, parabolic roll-off down to the front-to-back floor.</summary>
    Sector
}

/// <summary>How a jammer chooses what to radiate on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JammerTargetingMode>))]
public enum JammerTargetingMode
{
    /// <summary>The configured spot frequencies and bands, all the time (subject to the schedule).</summary>
    Scripted,

    /// <summary>Reserved for phase 2 (detect active frequencies, spot-jam the busiest). Until then a jammer in
    /// this mode radiates nothing.</summary>
    Auto
}

/// <summary>A point in DCS mission-local coordinates (x north, y altitude MSL, z east, metres) or in BMS
/// theater coordinates (x, y, z in feet, z = altitude), depending on where it is used.</summary>
public sealed class JammerVector3
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public JammerVector3()
    {
    }

    public JammerVector3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

/// <summary>A contiguous jammed band, kHz (inclusive edges).</summary>
public sealed class JammerBand
{
    public int StartKhz { get; set; }
    public int EndKhz { get; set; }

    public JammerBand()
    {
    }

    public JammerBand(int startKhz, int endKhz)
    {
        StartKhz = startKhz;
        EndKhz = endKhz;
    }
}

/// <summary>What a jammer radiates on. Scripted targeting only in phase 1; the auto fields are reserved.</summary>
public sealed class JammerTargeting
{
    public JammerTargetingMode Mode { get; set; } = JammerTargetingMode.Scripted;

    /// <summary>Spot frequencies, kHz. Each one gets a <see cref="SpotBandwidthKhz"/>-wide noise slot.</summary>
    public List<int> SpotFrequenciesKhz { get; set; } = [];

    /// <summary>Barrage / partial-band ranges. The jammer's power is spread evenly across each band.</summary>
    public List<JammerBand> Bands { get; set; } = [];

    /// <summary>Occupied bandwidth of each spot, kHz. 25 kHz matches one AM/FM channel (research-03 §4).</summary>
    public double SpotBandwidthKhz { get; set; } = 25.0;

    /// <summary>Keep 121.5 / 243.0 MHz guard out of bands (notched). An explicitly listed spot on guard is
    /// still jammed: listing it is the "explicitly targeted" opt-in.</summary>
    public bool ProtectGuard { get; set; } = true;

    /// <summary>Restricted frequencies (own nets, JRFL), kHz. Notched out of bands and never spot-jammed.</summary>
    public List<int> ProtectedFrequenciesKhz { get; set; } = [];

    // ---- Phase 2 (auto targeting), reserved: parsed and carried, not used yet. ----

    /// <summary>Auto mode: lowest frequency the brain may pick, kHz.</summary>
    public int? AutoBandStartKhz { get; set; }

    /// <summary>Auto mode: highest frequency the brain may pick, kHz.</summary>
    public int? AutoBandEndKhz { get; set; }

    /// <summary>Auto mode: how many spots at once (default: the preset's max simultaneous spots).</summary>
    public int? AutoMaxSpots { get; set; }

    /// <summary>Auto mode: dwell on a target before re-evaluating, ms.</summary>
    public double? AutoDwellMs { get; set; }

    /// <summary>Auto mode: delay before following a net that changed frequency, ms (research-02 §6.6: 10 s).</summary>
    public double? AutoReacquireDelayMs { get; set; }
}

/// <summary>Optional on/off schedule, relative to when the server (re)loaded the jammer.</summary>
public sealed class JammerSchedule
{
    /// <summary>Silent for this long after start, seconds.</summary>
    public double StartDelaySeconds { get; set; }

    /// <summary>Radiate for this long per cycle, seconds. Null or ≤0 with <see cref="OffSeconds"/> unset = always on.</summary>
    public double? OnSeconds { get; set; }

    /// <summary>Silent for this long per cycle, seconds.</summary>
    public double? OffSeconds { get; set; }

    /// <summary>Stop for good this long after start, seconds (null = never).</summary>
    public double? StopAfterSeconds { get; set; }

    /// <summary>Whether the jammer radiates <paramref name="elapsed"/> after start.</summary>
    public bool IsOn(TimeSpan elapsed)
    {
        var t = elapsed.TotalSeconds;
        if (t < StartDelaySeconds) return false;
        if (StopAfterSeconds is { } stop && t >= stop) return false;

        var on = OnSeconds ?? 0;
        var off = OffSeconds ?? 0;
        if (on <= 0 || off <= 0) return true;

        var phase = (t - StartDelaySeconds) % (on + off);
        return phase < on;
    }
}

/// <summary>
/// One communications jammer (docs/jamming/JAMMING_DESIGN.md). Used three ways: as written in the server config
/// (any field may be omitted and is then taken from <see cref="PresetId"/>), resolved by
/// <see cref="JammerPresets.Resolve"/> (every field filled), and on the wire in <c>JammerCatalogUpdateMessage</c>
/// (always resolved). Nullable physical fields mean "not set, use the preset".
/// </summary>
public sealed class JammerDefinition
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }

    /// <summary>A <see cref="JammerPresets"/> id (e.g. "r934b"). Fields set here override the preset.</summary>
    public string? PresetId { get; set; }

    /// <summary>"red" / "blue" / "neutral" (informational in phase 1).</summary>
    public string? Coalition { get; set; }

    /// <summary>Configured on/off switch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Unit alive (DCS hook, phase 6). A dead jammer radiates nothing.</summary>
    public bool Alive { get; set; } = true;

    // ---- Position: geodetic and/or DCS-local and/or BMS theater ----

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>Antenna altitude MSL, metres. If unset, <see cref="HeightAglMeters"/> above the terrain.</summary>
    public double? AltitudeMeters { get; set; }

    /// <summary>Antenna height above ground, metres (mast height; default from the preset).</summary>
    public double? HeightAglMeters { get; set; }

    /// <summary>DCS mission-local position (x north, y altitude MSL, z east; metres). Used directly by DCS clients.</summary>
    public JammerVector3? DcsPosition { get; set; }

    /// <summary>Falcon BMS theater position (x, y, z feet as in BMS shared memory; z = altitude).</summary>
    public JammerVector3? BmsPosition { get; set; }

    // ---- Transmitter and antenna ----

    /// <summary>Transmitter power per power amplifier, W.</summary>
    public double? TxPowerW { get; set; }

    /// <summary>Independent power amplifiers: up to this many emissions each get the full <see cref="TxPowerW"/>.
    /// More emissions time-share them (per-emission power × PAs / N).</summary>
    public int? PowerAmplifiers { get; set; }

    public double? AntennaGainDbi { get; set; }
    public JammerAntennaPattern? Pattern { get; set; }

    /// <summary>Sector boresight, degrees true.</summary>
    public double? AzimuthDeg { get; set; }

    /// <summary>Sector −3 dB beamwidth, degrees.</summary>
    public double? BeamwidthDeg { get; set; }

    /// <summary>Sector front-to-back (floor below boresight gain), dB.</summary>
    public double? FrontToBackDb { get; set; }

    public double? FeederLossDb { get; set; }

    /// <summary>Tuning range, kHz. Targets outside it are dropped.</summary>
    public int? MinFrequencyKhz { get; set; }

    public int? MaxFrequencyKhz { get; set; }

    /// <summary>Most simultaneous spots the system can hold.</summary>
    public int? MaxSimultaneousSpots { get; set; }

    // ---- Technique ----

    [JsonConverter(typeof(JsonStringEnumConverter<JammerTechnique>))]
    public JammerTechnique? Technique { get; set; }

    /// <summary>Tone offsets for MultiTone / SteppedTones, Hz from the jammer centre.</summary>
    public List<float>? ToneOffsetsHz { get; set; }

    public float? StepIntervalMs { get; set; }
    public float? SweepSpanHz { get; set; }
    public float? SweepRateHz { get; set; }
    public float? PulseRateHz { get; set; }
    public float? PulseDuty { get; set; }

    /// <summary>Look-through period, ms (0 = none).</summary>
    public float? LookThroughPeriodMs { get; set; }

    /// <summary>Fraction (0..1) of each look-through period spent silent.</summary>
    public float? LookThroughDuty { get; set; }

    /// <summary>Responsive (follower / preamble) jamming: radiate only while a wanted signal is on the channel.</summary>
    public bool? Responsive { get; set; }

    /// <summary>Reaction delay for responsive jamming, ms.</summary>
    public float? ReactionMs { get; set; }

    /// <summary>Transmitter frequency tolerance, ± Hz. Each jammer gets a fixed offset in this range, seeded by
    /// its id, so the heterodyne note is the same for everyone.</summary>
    public double? CarrierToleranceHz { get; set; }

    // ---- Targeting and schedule ----

    public JammerTargeting Targeting { get; set; } = new();

    public JammerSchedule? Schedule { get; set; }

    /// <summary>A shallow copy (lists are copied, nested objects shared).</summary>
    public JammerDefinition Clone()
    {
        var copy = (JammerDefinition)MemberwiseClone();
        copy.ToneOffsetsHz = ToneOffsetsHz?.ToList();
        return copy;
    }
}
