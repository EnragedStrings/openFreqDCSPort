using OpenFreqAudio.Jamming;

namespace OpenFreq.Common.Jamming;

/// <summary>Where a jammer preset lives.</summary>
public enum JammerPlatform
{
    Ground,
    Air
}

/// <summary>
/// One row of the research-03 §4 preset table. Spot EIRP there = P(dBW) + G − 2 dB feeder loss; the antenna gain
/// here is back-solved from that EIRP. <see cref="EstimatedFigures"/> names every figure that is an estimate
/// ([E] in the research) rather than sourced: the UI and docs must say so.
/// </summary>
public sealed record JammerPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required JammerPlatform Platform { get; init; }
    public required int MinFrequencyKhz { get; init; }
    public required int MaxFrequencyKhz { get; init; }
    public required double TxPowerW { get; init; }
    public int PowerAmplifiers { get; init; } = 1;
    public required double AntennaGainDbi { get; init; }
    public double FeederLossDb { get; init; } = 2.0;
    public required int MaxSimultaneousSpots { get; init; }
    public JammerTechnique Technique { get; init; } = JammerTechnique.Noise;

    /// <summary>Can follow frequency hoppers / react to transmissions (research-03 §4 "follower").</summary>
    public bool FollowerCapable { get; init; }

    /// <summary>Reaction delay when responsive, ms (research-02 §6.6: 2 ms modern, 10 ms legacy).</summary>
    public float ReactionMs { get; init; } = 10f;

    public float LookThroughPeriodMs { get; init; }
    public float LookThroughDuty { get; init; }

    /// <summary>Mast height (ground) or typical altitude (air), metres.</summary>
    public required double AntennaHeightMeters { get; init; }

    /// <summary>UHF SATCOM uplink jammer (phase 4).</summary>
    public bool SatcomUplink { get; init; }

    /// <summary>Auto mode: intercept receiver sensitivity, dBm [E: −110 typical; −120 for dedicated SIGINT
    /// platforms].</summary>
    public double DetectionSensitivityDbm { get; init; } = AutoTargetingDefaults.DetectionSensitivityDbm;

    /// <summary>Auto mode: search/DF/set-on time before a newly heard frequency is jammed, s [E: research-02
    /// §6.6, 10 s, range 2–60 s].</summary>
    public double ReacquireDelaySeconds { get; init; } = AutoTargetingDefaults.ReacquireDelaySeconds;

    /// <summary>Names of figures that are estimates ("TxPowerW", "AntennaGainDbi", "Coverage", ...).</summary>
    public IReadOnlySet<string> EstimatedFigures { get; init; } = new HashSet<string>();

    /// <summary>True when any figure of this preset is an estimate.</summary>
    public bool Estimated => EstimatedFigures.Count > 0;

    /// <summary>Spot EIRP per PA, dBW.</summary>
    public double SpotEirpDbw => 10.0 * Math.Log10(TxPowerW) + AntennaGainDbi - FeederLossDb;
}

/// <summary>The research-03 §4 preset table, and the merge of a config entry over its preset.</summary>
public static class JammerPresets
{
    private const string Power = "TxPowerW";
    private const string Gain = "AntennaGainDbi";
    private const string Coverage = "Coverage";
    private const string Spots = "MaxSimultaneousSpots";
    private const string Height = "AntennaHeightMeters";
    private const string Reaction = "ReactionMs";

    private static HashSet<string> E(params string[] figures) => [.. figures];

    /// <summary>Default sector beamwidth when a config asks for a sector without one (research-03 §4 note:
    /// "directional spot mode, gain 8 dBi, ±30° beam").</summary>
    public const double DefaultBeamwidthDeg = 60.0;

    /// <summary>Default sector front-to-back, dB [E: typical LPDA 15–25 dB].</summary>
    public const double DefaultFrontToBackDb = 20.0;

    /// <summary>Default transmitter frequency tolerance, ± Hz [E]: enough for an audible AM heterodyne.</summary>
    public const double DefaultCarrierToleranceHz = 300.0;

    /// <summary>Used when a config names no preset (or an unknown one) and leaves fields unset.</summary>
    public static readonly JammerPreset Generic = new()
    {
        Id = "generic", Name = "Generic jammer", Platform = JammerPlatform.Ground,
        MinFrequencyKhz = 20_000, MaxFrequencyKhz = 1_000_000, TxPowerW = 100, AntennaGainDbi = 2,
        MaxSimultaneousSpots = 1, AntennaHeightMeters = 10,
        EstimatedFigures = E(Power, Gain, Coverage, Spots, Height)
    };

    public static readonly IReadOnlyList<JammerPreset> All =
    [
        new()
        {
            Id = "r934b", Name = "R-934B/BMV Sinitsa", Platform = JammerPlatform.Ground,
            MinFrequencyKhz = 100_000, MaxFrequencyKhz = 400_000, TxPowerW = 500, PowerAmplifiers = 3,
            AntennaGainDbi = 8, MaxSimultaneousSpots = 3, FollowerCapable = true, ReactionMs = 2,
            AntennaHeightMeters = 12, EstimatedFigures = E(Gain, Spots, "PowerAmplifiers", Reaction)
        },
        new()
        {
            Id = "r934um2", Name = "R-934UM2 Groza-6", Platform = JammerPlatform.Ground,
            MinFrequencyKhz = 30_000, MaxFrequencyKhz = 3_000_000, TxPowerW = 500, PowerAmplifiers = 4,
            AntennaGainDbi = 9, MaxSimultaneousSpots = 28, FollowerCapable = true, ReactionMs = 2,
            AntennaHeightMeters = 15, EstimatedFigures = E(Power, Gain, "PowerAmplifiers", Reaction)
        },
        new()
        {
            Id = "r330b", Name = "R-330B/BMV Mandat-B", Platform = JammerPlatform.Ground,
            MinFrequencyKhz = 30_000, MaxFrequencyKhz = 100_000, TxPowerW = 1000, AntennaGainDbi = 6,
            MaxSimultaneousSpots = 6, LookThroughPeriodMs = 1000, LookThroughDuty = 0.1f,
            AntennaHeightMeters = 12, EstimatedFigures = E(Gain, "LookThrough")
        },
        new()
        {
            Id = "r378", Name = "R-378A/BMV", Platform = JammerPlatform.Ground,
            MinFrequencyKhz = 1_500, MaxFrequencyKhz = 30_000, TxPowerW = 1000, AntennaGainDbi = 2,
            MaxSimultaneousSpots = 4, AntennaHeightMeters = 10, EstimatedFigures = E(Power, Gain, Spots)
        },
        new()
        {
            Id = "pelena6", Name = "Pelena-6", Platform = JammerPlatform.Ground,
            MinFrequencyKhz = 20_000, MaxFrequencyKhz = 1_000_000, TxPowerW = 60, AntennaGainDbi = 2,
            MaxSimultaneousSpots = 2, AntennaHeightMeters = 3, EstimatedFigures = E(Gain, Spots)
        },
        new()
        {
            Id = "leer2", Name = "Leer-2", Platform = JammerPlatform.Ground,
            MinFrequencyKhz = 20_000, MaxFrequencyKhz = 2_700_000, TxPowerW = 200, AntennaGainDbi = 3,
            MaxSimultaneousSpots = 2, AntennaHeightMeters = 5, EstimatedFigures = E(Power, Gain, Spots)
        },
        new()
        {
            Id = "palantin", Name = "Palantin", Platform = JammerPlatform.Ground,
            MinFrequencyKhz = 3_000, MaxFrequencyKhz = 300_000, TxPowerW = 2000, AntennaGainDbi = 5,
            MaxSimultaneousSpots = 8, AntennaHeightMeters = 17, EstimatedFigures = E(Power, Gain, Spots)
        },
        new()
        {
            Id = "tirada_uhf", Name = "Tirada-2 (hypothetical UHF SATCOM uplink variant)",
            Platform = JammerPlatform.Ground, MinFrequencyKhz = 290_000, MaxFrequencyKhz = 320_000,
            TxPowerW = 1000, AntennaGainDbi = 22, MaxSimultaneousSpots = 2, AntennaHeightMeters = 5,
            SatcomUplink = true, EstimatedFigures = E(Power, Gain, Coverage, Spots)
        },
        new()
        {
            Id = "il22pp", Name = "Il-22PP Porubshchik", Platform = JammerPlatform.Air,
            MinFrequencyKhz = 30_000, MaxFrequencyKhz = 400_000, TxPowerW = 1000, AntennaGainDbi = 9,
            MaxSimultaneousSpots = 8, FollowerCapable = true, ReactionMs = 10, AntennaHeightMeters = 7000,
            EstimatedFigures = E(Power, Gain, Coverage, Spots, Reaction)
        },
        new()
        {
            Id = "mi8ppa", Name = "Mi-8 EW variant (generic comms)", Platform = JammerPlatform.Air,
            MinFrequencyKhz = 30_000, MaxFrequencyKhz = 400_000, TxPowerW = 500, AntennaGainDbi = 5,
            MaxSimultaneousSpots = 2, AntennaHeightMeters = 2000,
            EstimatedFigures = E(Power, Gain, Coverage, Spots)
        },
        new()
        {
            Id = "ec130h", Name = "EC-130H Compass Call", Platform = JammerPlatform.Air,
            MinFrequencyKhz = 30_000, MaxFrequencyKhz = 3_000_000, TxPowerW = 1500, AntennaGainDbi = 13,
            MaxSimultaneousSpots = 16, FollowerCapable = true, ReactionMs = 2, AntennaHeightMeters = 7000,
            DetectionSensitivityDbm = -120, ReacquireDelaySeconds = 5,
            EstimatedFigures = E(Power, Gain, Spots, Reaction)
        },
        new()
        {
            Id = "ea37b", Name = "EA-37B Compass Call", Platform = JammerPlatform.Air,
            MinFrequencyKhz = 30_000, MaxFrequencyKhz = 3_000_000, TxPowerW = 1500, AntennaGainDbi = 15,
            MaxSimultaneousSpots = 16, FollowerCapable = true, ReactionMs = 2, AntennaHeightMeters = 12_000,
            DetectionSensitivityDbm = -120, ReacquireDelaySeconds = 5,
            EstimatedFigures = E(Power, Gain, Coverage, Spots, Reaction)
        },
        new()
        {
            Id = "ea6b_usq113", Name = "EA-6B Prowler (USQ-113 + ALQ-99)", Platform = JammerPlatform.Air,
            MinFrequencyKhz = 20_000, MaxFrequencyKhz = 500_000, TxPowerW = 1000, PowerAmplifiers = 2,
            AntennaGainDbi = 10, MaxSimultaneousSpots = 2, FollowerCapable = true, ReactionMs = 10,
            AntennaHeightMeters = 7500, EstimatedFigures = E(Power, Gain, Reaction)
        },
        new()
        {
            Id = "ea18g_lb", Name = "EA-18G Growler (ALQ-99 LB + ALQ-227)", Platform = JammerPlatform.Air,
            MinFrequencyKhz = 64_000, MaxFrequencyKhz = 500_000, TxPowerW = 1000, AntennaGainDbi = 10,
            MaxSimultaneousSpots = 4, FollowerCapable = true, ReactionMs = 2, LookThroughPeriodMs = 500,
            LookThroughDuty = 0.1f, AntennaHeightMeters = 8500,
            EstimatedFigures = E(Power, Gain, Spots, Reaction, "LookThrough")
        },
        new()
        {
            Id = "quickfix", Name = "AN/ALQ-151(V)2 Quick Fix", Platform = JammerPlatform.Air,
            MinFrequencyKhz = 1_500, MaxFrequencyKhz = 80_000, TxPowerW = 1000, AntennaGainDbi = 0,
            MaxSimultaneousSpots = 2, AntennaHeightMeters = 500, EstimatedFigures = E(Gain, Spots)
        },
        new()
        {
            Id = "tlq17a", Name = "AN/TLQ-17A Traffic Jam", Platform = JammerPlatform.Ground,
            MinFrequencyKhz = 1_500, MaxFrequencyKhz = 80_000, TxPowerW = 550, AntennaGainDbi = 1.6,
            MaxSimultaneousSpots = 1, LookThroughPeriodMs = 1000, LookThroughDuty = 0.1f,
            AntennaHeightMeters = 10, EstimatedFigures = E(Gain, "LookThrough")
        },
        new()
        {
            Id = "ulq19", Name = "AN/ULQ-19 RACJAM", Platform = JammerPlatform.Ground,
            MinFrequencyKhz = 20_000, MaxFrequencyKhz = 80_000, TxPowerW = 250, AntennaGainDbi = 2,
            MaxSimultaneousSpots = 1, ReactionMs = 1000, LookThroughPeriodMs = 1000, LookThroughDuty = 0.1f,
            AntennaHeightMeters = 4, EstimatedFigures = E(Gain, "LookThrough")
        },
        new()
        {
            Id = "mlq34", Name = "AN/MLQ-34 TACJAM", Platform = JammerPlatform.Ground,
            MinFrequencyKhz = 20_000, MaxFrequencyKhz = 80_000, TxPowerW = 2000, PowerAmplifiers = 3,
            AntennaGainDbi = 8, MaxSimultaneousSpots = 3, AntennaHeightMeters = 10,
            EstimatedFigures = E(Power, Gain, Coverage, Spots, "PowerAmplifiers")
        },
        new()
        {
            Id = "generic_manpack", Name = "Generic man-portable comms jammer", Platform = JammerPlatform.Ground,
            MinFrequencyKhz = 20_000, MaxFrequencyKhz = 500_000, TxPowerW = 35, AntennaGainDbi = 1.6,
            MaxSimultaneousSpots = 2, AntennaHeightMeters = 2, EstimatedFigures = E(Power, Gain, Coverage, Spots)
        }
    ];

    private static readonly Dictionary<string, JammerPreset> ById =
        All.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public static JammerPreset? Find(string? id) =>
        id != null && ById.TryGetValue(id, out var preset) ? preset : null;

    /// <summary>A copy of <paramref name="definition"/> with every unset physical field taken from its preset
    /// (or <see cref="Generic"/>), and defaults filled, so consumers never see a null.</summary>
    public static JammerDefinition Resolve(JammerDefinition definition)
    {
        var preset = Find(definition.PresetId) ?? Generic;
        var r = definition.Clone();

        r.Name ??= string.IsNullOrWhiteSpace(definition.Id) ? preset.Name : definition.Id;
        r.TxPowerW ??= preset.TxPowerW;
        r.PowerAmplifiers ??= preset.PowerAmplifiers;
        r.AntennaGainDbi ??= preset.AntennaGainDbi;
        r.Pattern ??= JammerAntennaPattern.Omni;
        r.AzimuthDeg ??= 0.0;
        r.BeamwidthDeg ??= DefaultBeamwidthDeg;
        r.FrontToBackDb ??= DefaultFrontToBackDb;
        r.FeederLossDb ??= preset.FeederLossDb;
        r.MinFrequencyKhz ??= preset.MinFrequencyKhz;
        r.MaxFrequencyKhz ??= preset.MaxFrequencyKhz;
        r.MaxSimultaneousSpots ??= preset.MaxSimultaneousSpots;
        r.HeightAglMeters ??= preset.AntennaHeightMeters;
        r.Technique ??= preset.Technique;
        r.ToneOffsetsHz ??= [];
        r.StepIntervalMs ??= 150f;
        r.SweepSpanHz ??= 2000f;
        r.SweepRateHz ??= 4f;
        r.PulseRateHz ??= 50f;
        r.PulseDuty ??= 0.1f;
        r.LookThroughPeriodMs ??= preset.LookThroughPeriodMs;
        r.LookThroughDuty ??= preset.LookThroughDuty;
        r.Responsive ??= false;
        r.ReactionMs ??= preset.ReactionMs;
        r.CarrierToleranceHz ??= DefaultCarrierToleranceHz;
        r.Targeting ??= new JammerTargeting();
        r.Targeting.AutoMaxSpots ??= r.MaxSimultaneousSpots;
        r.Targeting.DetectionSensitivityDbm ??= preset.DetectionSensitivityDbm;
        r.Targeting.ActivityHalfLifeSeconds ??= AutoTargetingDefaults.ActivityHalfLifeSeconds;
        r.Targeting.DropAfterSeconds ??= AutoTargetingDefaults.DropAfterSeconds;
        r.Targeting.ReacquireDelaySeconds ??= preset.ReacquireDelaySeconds;
        return r;
    }
}
