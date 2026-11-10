using OpenFreq.Common.Jamming;

namespace OpenFreq.Common.Tests.Jamming;

public class JammerDetectionTests
{
    private const double JLat = 42.18, JLon = 42.48;

    private static JammerDefinition Jammer(Action<JammerDefinition>? configure = null)
    {
        var def = new JammerDefinition
        {
            Id = "j", PresetId = "r934b", Latitude = JLat, Longitude = JLon,
            Targeting = new JammerTargeting { Mode = JammerTargetingMode.Auto }
        };
        configure?.Invoke(def);
        return JammerPresets.Resolve(def);
    }

    [Fact]
    public void AircraftInsideTheHorizon_IsDetected()
    {
        // ~111 km, 6000 m: 10 W UHF arrives near −81 dBm, well above −110.
        Assert.True(JammerDetection.IsDetected(Jammer(), 251_000, JLat + 1.0, JLon, 6000));
        var dbm = JammerDetection.ReceivedDbm(10, 251_000, 111.2, 6000, 12)!.Value;
        Assert.InRange(dbm, -83, -79);
    }

    [Fact]
    public void BeyondTheRadioHorizon_IsNotDetected()
    {
        Assert.False(JammerDetection.IsDetected(Jammer(), 251_000, JLat + 5.0, JLon, 6000)); // ~555 km
        Assert.False(JammerDetection.IsDetected(Jammer(), 251_000, JLat + 0.45, JLon, 2)); // ground, ~50 km
        Assert.True(JammerDetection.IsDetected(Jammer(), 251_000, JLat + 0.1, JLon, 2)); // ground, ~11 km
    }

    [Fact]
    public void BelowSensitivity_IsNotDetected()
    {
        var deaf = Jammer(j => j.Targeting.DetectionSensitivityDbm = -60);
        Assert.False(JammerDetection.IsDetected(deaf, 251_000, JLat + 1.0, JLon, 6000));
    }

    [Fact]
    public void Defaults_ComeFromThePreset()
    {
        var t = Jammer().Targeting;
        Assert.Equal(-110, t.DetectionSensitivityDbm);
        Assert.Equal(10, t.ReacquireDelaySeconds);
        Assert.Equal(30, t.DropAfterSeconds);
        Assert.Equal(60, t.ActivityHalfLifeSeconds);
        Assert.Equal(3, t.AutoMaxSpots);
        Assert.Equal(-120, Jammer(j => j.PresetId = "ec130h").Targeting.DetectionSensitivityDbm);
    }

    [Fact]
    public void Resolve_DoesNotWriteBackIntoTheConfig()
    {
        var def = new JammerDefinition { Id = "j", PresetId = "r934b" };
        JammerPresets.Resolve(def);
        Assert.Null(def.Targeting.ReacquireDelaySeconds);
    }

    [Fact]
    public void TerrainBlocked_IsNotDetected_UnknownFallsBackToHorizon()
    {
        Assert.False(JammerDetection.IsDetected(Jammer(), 251_000, JLat + 1.0, JLon, 6000, lineOfSight: false));
        Assert.True(JammerDetection.IsDetected(Jammer(), 251_000, JLat + 1.0, JLon, 6000, lineOfSight: null));
        Assert.True(JammerDetection.IsDetected(Jammer(), 251_000, JLat + 1.0, JLon, 6000, lineOfSight: true));
    }

    [Fact]
    public void OutsideTuningRangeOrAutoBands_IsNotDetected()
    {
        Assert.False(JammerDetection.IsDetected(Jammer(), 50_000, JLat + 0.1, JLon, 6000)); // r934b: 100-400 MHz
        var uhfOnly = Jammer(j => j.Targeting.AutoBands = [new JammerBand(225_000, 400_000)]);
        Assert.False(JammerDetection.IsDetected(uhfOnly, 124_000, JLat + 0.1, JLon, 6000));
        Assert.True(JammerDetection.IsDetected(uhfOnly, 251_000, JLat + 0.1, JLon, 6000));
    }

    [Fact]
    public void ProtectedAndGuard_CannotBeTargeted()
    {
        var jammer = Jammer(j => j.Targeting.ProtectedFrequenciesKhz = [264_000]);
        Assert.False(JammerDetection.CanTarget(jammer, 243_000));
        Assert.False(JammerDetection.CanTarget(jammer, 121_500));
        Assert.False(JammerDetection.CanTarget(jammer, 264_000));
        Assert.True(JammerDetection.CanTarget(jammer, 251_000));
        Assert.True(JammerDetection.CanTarget(Jammer(j => j.Targeting.ProtectGuard = false), 243_000));
    }

    [Fact]
    public void JammerWithoutLatLon_DetectsNothing()
    {
        var jammer = Jammer(j =>
        {
            j.Latitude = null;
            j.Longitude = null;
            j.DcsPosition = new JammerVector3(0, 10, 0);
        });
        Assert.False(JammerDetection.IsDetected(jammer, 251_000, JLat, JLon, 6000));
    }
}

public class AutoTargetingBrainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static JammerDefinition Jammer(int? maxSpots = null, Action<JammerTargeting>? targeting = null,
        float lookThroughMs = 0, float lookThroughDuty = 0, string preset = "r934b")
    {
        var def = new JammerDefinition
        {
            Id = "j", PresetId = preset, Latitude = 42.18, Longitude = 42.48,
            LookThroughPeriodMs = lookThroughMs, LookThroughDuty = lookThroughDuty,
            Targeting = new JammerTargeting { Mode = JammerTargetingMode.Auto, AutoMaxSpots = maxSpots }
        };
        targeting?.Invoke(def.Targeting);
        return JammerPresets.Resolve(def);
    }

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    /// <summary>Keys of <paramref name="length"/> s every <paramref name="every"/> s in [from, to).</summary>
    private static IEnumerable<DetectedKeying> Net(int khz, double from, double to, double every, double length)
    {
        for (var t = from; t < to; t += every)
            yield return new DetectedKeying(khz, At(t), At(Math.Min(t + length, to)));
    }

    /// <summary>Steps the brain once per <paramref name="step"/> s from its last time to <paramref name="until"/>,
    /// passing every keying (the brain clips them itself).</summary>
    private static void Run(AutoTargetingBrain brain, JammerDefinition jammer, List<DetectedKeying> keyings,
        double from, double until, double step = 1.0)
    {
        for (var t = from + step; t <= until + 1e-9; t += step)
        {
            var now = At(t);
            brain.Update(jammer, now,
                keyings.Where(k => k.Start <= now).Select(k => k.End > now ? k with { End = null } : k).ToList());
        }
    }

    [Fact]
    public void NewFrequency_IsJammedOnlyAfterTheReacquireDelay()
    {
        var jammer = Jammer();
        var brain = new AutoTargetingBrain(T0);
        var keys = Net(251_000, 0, 60, 5, 2).ToList();

        Run(brain, jammer, keys, 0, 9);
        Assert.Empty(brain.TargetsKhz);
        Assert.Equal(AutoJammerState.Searching, brain.State);

        Run(brain, jammer, keys, 9, 10);
        Assert.Equal([251_000], brain.TargetsKhz);
        Assert.Equal(AutoJammerState.Jamming, brain.State);
    }

    [Fact]
    public void ProtectedAndGuardFrequencies_AreNeverJammed()
    {
        var jammer = Jammer(targeting: t => t.ProtectedFrequenciesKhz = [264_000]);
        var brain = new AutoTargetingBrain(T0);
        var keys = Net(243_000, 0, 60, 2, 1.5).Concat(Net(264_000, 0, 60, 2, 1.5))
            .Concat(Net(251_000, 0, 60, 30, 1)).ToList();

        Run(brain, jammer, keys, 0, 60);
        Assert.Equal([251_000], brain.TargetsKhz);
    }

    [Fact]
    public void PicksTheBusiest_UpToMaxSpots()
    {
        var jammer = Jammer(maxSpots: 2);
        var brain = new AutoTargetingBrain(T0);
        var keys = Net(251_000, 0, 120, 10, 1) // quiet net, heard first
            .Concat(Net(260_000, 3, 120, 4, 3)) // busiest
            .Concat(Net(270_000, 3, 120, 5, 2))
            .ToList();

        Run(brain, jammer, keys, 0, 60);
        Assert.Equal([260_000, 270_000], brain.TargetsKhz.Order());
        Assert.True(brain.ScoreOf(260_000) > brain.ScoreOf(270_000));
        Assert.True(brain.ScoreOf(270_000) > brain.ScoreOf(251_000));
    }

    [Fact]
    public void Target_IsDroppedAfterSilence_AndMustBeReacquired()
    {
        var jammer = Jammer();
        var brain = new AutoTargetingBrain(T0);
        var keys = new List<DetectedKeying> { new(251_000, At(0), At(5)) };

        Run(brain, jammer, keys, 0, 34);
        Assert.Equal([251_000], brain.TargetsKhz); // last heard at 5 s
        Run(brain, jammer, keys, 34, 35);
        Assert.Empty(brain.TargetsKhz); // 30 s of silence

        // The net comes back: the jammer has to find it again.
        keys.Add(new DetectedKeying(251_000, At(40), At(60)));
        Run(brain, jammer, keys, 35, 49);
        Assert.Empty(brain.TargetsKhz);
        Run(brain, jammer, keys, 49, 50);
        Assert.Equal([251_000], brain.TargetsKhz);
    }

    [Fact]
    public void Qsy_TheJammerFollowsOnlyAfterTheReacquireDelay()
    {
        var jammer = Jammer(maxSpots: 1);
        var brain = new AutoTargetingBrain(T0);
        // The net talks on A until 60 s, then switches to B (calls 2 s every 5 s).
        var keys = Net(251_000, 0, 60, 5, 2).Concat(Net(262_000, 61, 200, 5, 2)).ToList();

        Run(brain, jammer, keys, 0, 60);
        Assert.Equal([251_000], brain.TargetsKhz);

        Run(brain, jammer, keys, 60, 70);
        Assert.Equal([251_000], brain.TargetsKhz); // B first heard at 61: not yet
        Run(brain, jammer, keys, 70, 71);
        Assert.Equal([262_000], brain.TargetsKhz);

        // And it does not flap back to the old, busier-history frequency.
        Run(brain, jammer, keys, 71, 100);
        Assert.Equal([262_000], brain.TargetsKhz);
    }

    [Fact]
    public void LookThrough_OnlyHearsAJammedNetInTheSilentWindows()
    {
        // 1 s period, silent for the last 10 % of it: [0.9, 1.0) of every second.
        var jammer = Jammer(lookThroughMs: 1000, lookThroughDuty: 0.1f);
        var brain = new AutoTargetingBrain(T0);
        var keys = new List<DetectedKeying> { new(251_000, At(0), At(3)) };

        Run(brain, jammer, keys, 0, 10, step: 0.25);
        Assert.Equal([251_000], brain.TargetsKhz);

        // Short keys inside the jamming part of each period are missed: the target still drops 30 s after 3 s.
        for (var t = 11.0; t < 40; t += 1) keys.Add(new DetectedKeying(251_000, At(t + 0.1), At(t + 0.5)));
        Run(brain, jammer, keys, 10, 32.75, step: 0.25);
        Assert.Equal([251_000], brain.TargetsKhz);
        Run(brain, jammer, keys, 32.75, 33.25, step: 0.25);
        Assert.Empty(brain.TargetsKhz);
    }

    [Fact]
    public void LookThrough_KeysOverlappingTheWindows_KeepTheTarget()
    {
        var jammer = Jammer(lookThroughMs: 1000, lookThroughDuty: 0.1f);
        var brain = new AutoTargetingBrain(T0);
        var keys = Net(251_000, 0, 120, 10, 3).ToList(); // 3 s calls always span a look window

        Run(brain, jammer, keys, 0, 120, step: 0.5);
        Assert.Equal([251_000], brain.TargetsKhz);
    }

    [Fact]
    public void ScriptedSpots_ReduceTheAutoCapacity()
    {
        var jammer = Jammer(targeting: t => t.SpotFrequenciesKhz = [300_000, 310_000]); // r934b: 3 spots
        var brain = new AutoTargetingBrain(T0);
        var keys = Net(251_000, 0, 60, 3, 2).Concat(Net(260_000, 0, 60, 4, 2)).ToList();

        Run(brain, jammer, keys, 0, 30);
        Assert.Equal([251_000], brain.TargetsKhz);
        var e = ScriptedTargeting.Emissions(jammer, brain.TargetsKhz);
        Assert.Equal([300_000.0, 310_000.0, 251_000.0], e.Select(x => x.CenterKhz));
    }

    [Fact]
    public void Reset_ForgetsTargets()
    {
        var jammer = Jammer();
        var brain = new AutoTargetingBrain(T0);
        var keys = Net(251_000, 0, 60, 5, 2).ToList();
        Run(brain, jammer, keys, 0, 20);
        Assert.NotEmpty(brain.TargetsKhz);

        brain.Reset(At(20));
        Assert.Empty(brain.TargetsKhz);
        Run(brain, jammer, keys, 20, 29);
        Assert.Empty(brain.TargetsKhz);
        Run(brain, jammer, keys, 29, 30);
        Assert.Equal([251_000], brain.TargetsKhz);
    }

    [Fact]
    public void SameInputs_SameResult_RegardlessOfTickRate()
    {
        var jammer = Jammer(maxSpots: 2);
        var keys = Net(251_000, 0, 90, 7, 2).Concat(Net(260_000, 5, 90, 3, 1))
            .Concat(Net(270_000, 20, 90, 4, 2)).ToList();
        var coarse = new AutoTargetingBrain(T0);
        var fine = new AutoTargetingBrain(T0);
        Run(coarse, jammer, keys, 0, 90, step: 1.0);
        Run(fine, jammer, keys, 0, 90, step: 0.1);
        Assert.Equal(coarse.TargetsKhz.Order(), fine.TargetsKhz.Order());
        Assert.InRange(Math.Abs(coarse.ScoreOf(260_000) - fine.ScoreOf(260_000)), 0, 0.5);
    }
}
