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

    /// <summary>Detect active frequencies and spot-jam the busiest, with look-through, a reacquire delay and a
    /// protected list (server auto brain, phase 2). Scripted spots and bands, if any, are radiated as well.</summary>
    Auto
}

/// <summary>A point in DCS mission-local coordinates (x north, y altitude MSL, z east, metres) or in BMS
/// theater coordinates (x, y, z in feet, z = altitude), depending on where it is used.</summary>
public sealed class JammerVector3
{
    [JsonPropertyName("x")]
    public double X { get; set; }
    [JsonPropertyName("y")]
    public double Y { get; set; }
    [JsonPropertyName("z")]
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
    [JsonPropertyName("startKhz")]
    public int StartKhz { get; set; }
    [JsonPropertyName("endKhz")]
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

/// <summary>What a jammer radiates on: scripted spots and bands, plus the auto brain's picks in auto mode.</summary>
public sealed class JammerTargeting
{
    [JsonPropertyName("mode")]
    public JammerTargetingMode Mode { get; set; } = JammerTargetingMode.Scripted;

    /// <summary>Spot frequencies, kHz. Each one gets a <see cref="SpotBandwidthKhz"/>-wide noise slot.</summary>
    [JsonPropertyName("spotFrequenciesKhz")]
    public List<int> SpotFrequenciesKhz { get; set; } = [];

    /// <summary>Barrage / partial-band ranges. The jammer's power is spread evenly across each band.</summary>
    [JsonPropertyName("bands")]
    public List<JammerBand> Bands { get; set; } = [];

    /// <summary>Occupied bandwidth of each spot, kHz. 25 kHz matches one AM/FM channel (research-03 §4).</summary>
    [JsonPropertyName("spotBandwidthKhz")]
    public double SpotBandwidthKhz { get; set; } = 25.0;

    /// <summary>Keep 121.5 / 243.0 MHz guard out of bands (notched). An explicitly listed spot on guard is
    /// still jammed: listing it is the "explicitly targeted" opt-in.</summary>
    [JsonPropertyName("protectGuard")]
    public bool ProtectGuard { get; set; } = true;

    /// <summary>Restricted frequencies (own nets, JRFL), kHz. Notched out of bands and never spot-jammed.</summary>
    [JsonPropertyName("protectedFrequenciesKhz")]
    public List<int> ProtectedFrequenciesKhz { get; set; } = [];

    // ---- Auto targeting (phase 2): used when Mode is Auto; scripted spots and bands still apply alongside. ----

    /// <summary>Auto mode: only pick frequencies inside these ranges, kHz (empty = the whole tuning range).</summary>
    [JsonPropertyName("autoBands")]
    public List<JammerBand> AutoBands { get; set; } = [];

    /// <summary>Auto mode: most auto-picked spots at once (default: the preset's max simultaneous spots, less
    /// any scripted spots).</summary>
    [JsonPropertyName("autoMaxSpots")]
    public int? AutoMaxSpots { get; set; }

    /// <summary>Auto mode: weakest transmission the jammer's intercept receiver detects, dBm (default from the
    /// preset, typically −110).</summary>
    [JsonPropertyName("detectionSensitivityDbm")]
    public double? DetectionSensitivityDbm { get; set; }

    /// <summary>Auto mode: half-life of the activity score (decayed keyed seconds), seconds (default 60).</summary>
    [JsonPropertyName("activityHalfLifeSeconds")]
    public double? ActivityHalfLifeSeconds { get; set; }

    /// <summary>Auto mode: a target with no detected activity for this long is dropped, seconds (default 30).</summary>
    [JsonPropertyName("dropAfterSeconds")]
    public double? DropAfterSeconds { get; set; }

    /// <summary>Auto mode: a newly detected frequency is jammed only this long after it was first heard
    /// (search, DF, set-on), seconds (default from the preset, typically 10; research-02 §6.6).</summary>
    [JsonPropertyName("reacquireDelaySeconds")]
    public double? ReacquireDelaySeconds { get; set; }

    /// <summary>A copy with its own lists.</summary>
    public JammerTargeting Clone()
    {
        var copy = (JammerTargeting)MemberwiseClone();
        copy.SpotFrequenciesKhz = SpotFrequenciesKhz?.ToList() ?? [];
        copy.Bands = Bands?.ToList() ?? [];
        copy.ProtectedFrequenciesKhz = ProtectedFrequenciesKhz?.ToList() ?? [];
        copy.AutoBands = AutoBands?.ToList() ?? [];
        return copy;
    }
}

/// <summary>Optional on/off schedule, relative to when the server (re)loaded the jammer.</summary>
public sealed class JammerSchedule
{
    /// <summary>Silent for this long after start, seconds.</summary>
    [JsonPropertyName("startDelaySeconds")]
    public double StartDelaySeconds { get; set; }

    /// <summary>Radiate for this long per cycle, seconds. Null or ≤0 with <see cref="OffSeconds"/> unset = always on.</summary>
    [JsonPropertyName("onSeconds")]
    public double? OnSeconds { get; set; }

    /// <summary>Silent for this long per cycle, seconds.</summary>
    [JsonPropertyName("offSeconds")]
    public double? OffSeconds { get; set; }

    /// <summary>Stop for good this long after start, seconds (null = never).</summary>
    [JsonPropertyName("stopAfterSeconds")]
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
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>A <see cref="JammerPresets"/> id (e.g. "r934b"). Fields set here override the preset.</summary>
    [JsonPropertyName("presetId")]
    public string? PresetId { get; set; }

    /// <summary>"red" / "blue" / "neutral" (informational in phase 1).</summary>
    [JsonPropertyName("coalition")]
    public string? Coalition { get; set; }

    /// <summary>Configured on/off switch.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Unit alive (DCS hook, phase 6). A dead jammer radiates nothing.</summary>
    [JsonPropertyName("alive")]
    public bool Alive { get; set; } = true;

    // ---- Position: geodetic and/or DCS-local and/or BMS theater ----

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }
    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    /// <summary>Antenna altitude MSL, metres. If unset, <see cref="HeightAglMeters"/> above the terrain.</summary>
    [JsonPropertyName("altitudeMeters")]
    public double? AltitudeMeters { get; set; }

    /// <summary>Antenna height above ground, metres (mast height; default from the preset).</summary>
    [JsonPropertyName("heightAglMeters")]
    public double? HeightAglMeters { get; set; }

    /// <summary>DCS mission-local position (x north, y altitude MSL, z east; metres). Used directly by DCS clients.</summary>
    [JsonPropertyName("dcsPosition")]
    public JammerVector3? DcsPosition { get; set; }

    /// <summary>Falcon BMS theater position (x, y, z feet as in BMS shared memory; z = altitude).</summary>
    [JsonPropertyName("bmsPosition")]
    public JammerVector3? BmsPosition { get; set; }

    // ---- Transmitter and antenna ----

    /// <summary>Transmitter power per power amplifier, W.</summary>
    [JsonPropertyName("txPowerW")]
    public double? TxPowerW { get; set; }

    /// <summary>Independent power amplifiers: up to this many emissions each get the full <see cref="TxPowerW"/>.
    /// More emissions time-share them (per-emission power × PAs / N).</summary>
    [JsonPropertyName("powerAmplifiers")]
    public int? PowerAmplifiers { get; set; }

    [JsonPropertyName("antennaGainDbi")]
    public double? AntennaGainDbi { get; set; }

    /// <summary>Antenna gain toward a SATCOM satellite (high elevation), dBi. Tactical comms jammers point their gain
    /// at the horizon, so it defaults to 0 dBi; a dedicated uplink jammer's dish to its full gain (phase 4).</summary>
    [JsonPropertyName("satcomAntennaGainDbi")]
    public double? SatcomAntennaGainDbi { get; set; }
    [JsonPropertyName("pattern")]
    public JammerAntennaPattern? Pattern { get; set; }

    /// <summary>Sector boresight, degrees true.</summary>
    [JsonPropertyName("azimuthDeg")]
    public double? AzimuthDeg { get; set; }

    /// <summary>Sector −3 dB beamwidth, degrees.</summary>
    [JsonPropertyName("beamwidthDeg")]
    public double? BeamwidthDeg { get; set; }

    /// <summary>Sector front-to-back (floor below boresight gain), dB.</summary>
    [JsonPropertyName("frontToBackDb")]
    public double? FrontToBackDb { get; set; }

    [JsonPropertyName("feederLossDb")]
    public double? FeederLossDb { get; set; }

    /// <summary>Tuning range, kHz. Targets outside it are dropped.</summary>
    [JsonPropertyName("minFrequencyKhz")]
    public int? MinFrequencyKhz { get; set; }

    [JsonPropertyName("maxFrequencyKhz")]
    public int? MaxFrequencyKhz { get; set; }

    /// <summary>Most simultaneous spots the system can hold.</summary>
    [JsonPropertyName("maxSimultaneousSpots")]
    public int? MaxSimultaneousSpots { get; set; }

    // ---- Technique ----

    [JsonConverter(typeof(JsonStringEnumConverter<JammerTechnique>))]
    [JsonPropertyName("technique")]
    public JammerTechnique? Technique { get; set; }

    /// <summary>Tone offsets for MultiTone / SteppedTones, Hz from the jammer centre.</summary>
    [JsonPropertyName("toneOffsetsHz")]
    public List<float>? ToneOffsetsHz { get; set; }

    [JsonPropertyName("stepIntervalMs")]
    public float? StepIntervalMs { get; set; }
    [JsonPropertyName("sweepSpanHz")]
    public float? SweepSpanHz { get; set; }
    [JsonPropertyName("sweepRateHz")]
    public float? SweepRateHz { get; set; }
    [JsonPropertyName("pulseRateHz")]
    public float? PulseRateHz { get; set; }
    [JsonPropertyName("pulseDuty")]
    public float? PulseDuty { get; set; }

    /// <summary>Look-through period, ms (0 = none).</summary>
    [JsonPropertyName("lookThroughPeriodMs")]
    public float? LookThroughPeriodMs { get; set; }

    /// <summary>Fraction (0..1) of each look-through period spent silent.</summary>
    [JsonPropertyName("lookThroughDuty")]
    public float? LookThroughDuty { get; set; }

    /// <summary>Responsive (follower / preamble) jamming: radiate only while a wanted signal is on the channel.</summary>
    [JsonPropertyName("responsive")]
    public bool? Responsive { get; set; }

    /// <summary>Reaction delay for responsive jamming, ms.</summary>
    [JsonPropertyName("reactionMs")]
    public float? ReactionMs { get; set; }

    /// <summary>Transmitter frequency tolerance, ± Hz. Each jammer gets a fixed offset in this range, seeded by
    /// its id, so the heterodyne note is the same for everyone.</summary>
    [JsonPropertyName("carrierToleranceHz")]
    public double? CarrierToleranceHz { get; set; }

    // ---- Targeting and schedule ----

    [JsonPropertyName("targeting")]
    public JammerTargeting Targeting { get; set; } = new();

    [JsonPropertyName("schedule")]
    public JammerSchedule? Schedule { get; set; }

    /// <summary>A shallow copy (lists and the targeting are copied, other nested objects shared).</summary>
    public JammerDefinition Clone()
    {
        var copy = (JammerDefinition)MemberwiseClone();
        copy.ToneOffsetsHz = ToneOffsetsHz?.ToList();
        copy.Targeting = Targeting?.Clone()!;
        return copy;
    }
}
