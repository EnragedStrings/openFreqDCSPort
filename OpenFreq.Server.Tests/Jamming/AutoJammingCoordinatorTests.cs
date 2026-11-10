using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common.Jamming;
using OpenFreqServer;
using OpenFreqServer.Jamming;
using OpenFreqServer.Json;

namespace OpenFreq.Server.Tests.Jamming;

/// <summary>The coordinator's auto brain wiring: transmissions + positions in, catalog emissions out (fake clock).</summary>
public class AutoJammingCoordinatorTests
{
    private const double JLat = 42.18, JLon = 42.48;
    private DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private JammingCoordinator Create(params JammerDefinition[] jammers) =>
        new(new JammingServerConfig { Enabled = true, Jammers = [.. jammers] }, NullLogger.Instance, () => _now);

    private static JammerDefinition Auto(string id = "auto", int? pas = null, params int[] scriptedSpots) => new()
    {
        Id = id, PresetId = "r934b", Latitude = JLat, Longitude = JLon, PowerAmplifiers = pas,
        Targeting = new JammerTargeting { Mode = JammerTargetingMode.Auto, SpotFrequenciesKhz = [.. scriptedSpots] }
    };

    /// <summary>Advances the fake clock in 1 s ticks, taking a snapshot each tick like the broadcast loop.</summary>
    private List<OpenFreq.Common.JammerCatalogEntryDto> Advance(JammingCoordinator coordinator, double seconds,
        Action? eachTick = null)
    {
        var snapshot = coordinator.Snapshot();
        for (var i = 0; i < (int)Math.Round(seconds); i++)
        {
            _now += TimeSpan.FromSeconds(1);
            eachTick?.Invoke();
            snapshot = coordinator.Snapshot();
        }

        return snapshot;
    }

    [Fact]
    public void InRangeTransmitter_IsJammedAfterTheReacquireDelay()
    {
        var coordinator = Create(Auto());
        coordinator.ReportTransmission("viper1", 251_000, true, JLat + 1.0, JLon, 6000);

        var entry = Advance(coordinator, 9)[0];
        Assert.False(entry.Active);
        Assert.Equal(AutoJammerState.Searching, entry.AutoState);

        entry = Advance(coordinator, 1)[0];
        Assert.True(entry.Active);
        Assert.Equal(AutoJammerState.Jamming, entry.AutoState);
        Assert.Equal([251_000], entry.AutoTargetsKhz!);
        var emission = Assert.Single(entry.Emissions);
        Assert.Equal(251_000, emission.CenterKhz);
        Assert.Equal(25, emission.BandwidthKhz);
    }

    [Fact]
    public void OutOfRange_Unlocated_Blocked_OrProtected_IsNotJammed()
    {
        var coordinator = Create(Auto());
        coordinator.LineOfSight = (_, tx, _, _, _, _, _, _) => tx == "behind-hill" ? false : null;
        coordinator.ReportTransmission("far", 251_000, true, JLat + 5.0, JLon, 6000);
        coordinator.ReportTransmission("nowhere", 252_000, true);
        coordinator.ReportTransmission("behind-hill", 253_000, true, JLat + 0.5, JLon, 3000);
        coordinator.ReportTransmission("guard", 243_000, true, JLat + 0.5, JLon, 3000);
        coordinator.ReportTransmission("vhf-fm", 40_000, true, JLat + 0.5, JLon, 3000); // below r934b's range

        var entry = Advance(coordinator, 30)[0];
        Assert.Empty(entry.Emissions);
        Assert.Equal(AutoJammerState.Searching, entry.AutoState);
    }

    [Fact]
    public void PresencePosition_LocatesATransmitterThatSentNone()
    {
        var coordinator = Create(Auto());
        coordinator.ReportTransmitterPosition("viper1", JLat + 1.0, JLon, 6000);
        coordinator.ReportTransmission("viper1", 251_000, true);

        Assert.Equal([251_000], Advance(coordinator, 10)[0].AutoTargetsKhz!);
    }

    [Fact]
    public void ScriptedAndAuto_Coexist_AndSplitThePower()
    {
        var coordinator = Create(Auto(pas: 1, scriptedSpots: 300_000));
        coordinator.ReportTransmission("viper1", 251_000, true, JLat + 1.0, JLon, 6000);

        var before = Advance(coordinator, 1)[0];
        Assert.Equal(1.0, Assert.Single(before.Emissions).PowerFraction);

        var after = Advance(coordinator, 10)[0];
        Assert.Equal([300_000.0, 251_000.0], after.Emissions.Select(e => e.CenterKhz));
        Assert.All(after.Emissions, e => Assert.Equal(0.5, e.PowerFraction, 6));
    }

    [Fact]
    public void Silence_DropsTheTarget()
    {
        var coordinator = Create(Auto());
        coordinator.ReportTransmission("viper1", 251_000, true, JLat + 1.0, JLon, 6000);
        Advance(coordinator, 5, () => coordinator.ReportTransmission("viper1", 251_000, true));
        coordinator.ReportTransmission("viper1", 251_000, false);

        Assert.True(Advance(coordinator, 29)[0].Active); // keyed until 5 s; jammed from 10 s
        Assert.False(Advance(coordinator, 1)[0].Active); // 30 s of silence
    }

    [Fact]
    public void LostKeyUp_ExpiresAfterTheHeartbeatStops()
    {
        var coordinator = Create(Auto());
        coordinator.ReportTransmission("viper1", 251_000, true, JLat + 1.0, JLon, 6000);
        Assert.True(Advance(coordinator, 10)[0].Active);

        // No heartbeat after t=0: expired after 5 s (last heard at 5 s), dropped 30 s after that.
        Assert.True(Advance(coordinator, 24)[0].Active);
        Assert.False(Advance(coordinator, 1)[0].Active);
    }

    [Fact]
    public void Disconnect_EndsTheKeyings_AndForgetsThePosition()
    {
        var coordinator = Create(Auto());
        coordinator.ReportTransmission("viper1", 251_000, true, JLat + 1.0, JLon, 6000);
        Advance(coordinator, 3, () => coordinator.ReportTransmission("viper1", 251_000, true));
        coordinator.ReportTransmitterGone("viper1");

        Assert.True(Advance(coordinator, 7)[0].Active); // heard 0-3 s, jammed at 10 s

        // The same id keying again without a position cannot be located any more.
        coordinator.ReportTransmission("viper1", 251_000, true);
        Assert.True(Advance(coordinator, 22, () => coordinator.ReportTransmission("viper1", 251_000, true))[0].Active);
        Assert.False(Advance(coordinator, 1)[0].Active); // 30 s after 3 s
    }

    [Fact]
    public void Qsy_IsFollowedAfterTheReacquireDelay()
    {
        var jammer = Auto();
        jammer.Targeting.AutoMaxSpots = 1;
        var coordinator = Create(jammer);
        var freq = 251_000;
        coordinator.ReportTransmitterPosition("gci", JLat + 0.3, JLon, 500);
        coordinator.ReportTransmitterPosition("viper1", JLat + 1.0, JLon, 6000);

        // GCI and the fighter alternate 2 s calls every 5 s.
        var tick = 0;
        void Talk()
        {
            tick++;
            var phase = tick % 5;
            var tx = tick / 5 % 2 == 0 ? "gci" : "viper1";
            coordinator.ReportTransmission(tx, freq, phase < 2);
            if (phase == 2) coordinator.ReportTransmission(tx, freq, false);
        }

        Assert.Equal([251_000], Advance(coordinator, 30, Talk)[0].AutoTargetsKhz!);

        coordinator.ReportTransmission("gci", freq, false);
        coordinator.ReportTransmission("viper1", freq, false);
        freq = 264_000; // CHATTERMARK: the net moves
        var entry = Advance(coordinator, 9, Talk)[0];
        Assert.Equal([251_000], entry.AutoTargetsKhz!);
        entry = Advance(coordinator, 3, Talk)[0];
        Assert.Equal([264_000], entry.AutoTargetsKhz!);
        Assert.Equal(264_000, Assert.Single(entry.Emissions).CenterKhz);
    }

    [Fact]
    public void ScheduleOff_ResetsTheBrain()
    {
        var jammer = Auto();
        jammer.Schedule = new JammerSchedule { OnSeconds = 20, OffSeconds = 10 };
        var coordinator = Create(jammer);
        coordinator.ReportTransmission("viper1", 251_000, true, JLat + 1.0, JLon, 6000);
        void Heartbeat() => coordinator.ReportTransmission("viper1", 251_000, true);

        Assert.True(Advance(coordinator, 10, Heartbeat)[0].Active);
        Assert.False(Advance(coordinator, 12, Heartbeat)[0].Active); // off at 20 s
        Assert.False(Advance(coordinator, 16, Heartbeat)[0].Active); // on again at 30 s, reacquiring
        Assert.True(Advance(coordinator, 2, Heartbeat)[0].Active);
    }

    [Fact]
    public void TargetChange_IsABroadcastChange()
    {
        var coordinator = Create(Auto());
        Assert.NotNull(coordinator.NextBroadcast(periodicDue: false));
        coordinator.ReportTransmission("viper1", 251_000, true, JLat + 1.0, JLon, 6000);
        for (var i = 0; i < 9; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            Assert.Null(coordinator.NextBroadcast(periodicDue: false));
        }

        _now += TimeSpan.FromSeconds(1);
        var update = coordinator.NextBroadcast(periodicDue: false);
        Assert.NotNull(update);
        Assert.Equal(251_000, Assert.Single(update[0].Emissions).CenterKhz);
    }

    [Fact]
    public void AutoConfig_ParsesFromJson()
    {
        const string json = """
            {
              "jamming": {
                "enabled": true,
                "jammers": [
                  {
                    "id": "sinitsa-auto",
                    "presetId": "r934b",
                    "latitude": 42.18,
                    "longitude": 42.48,
                    "lookThroughPeriodMs": 1000,
                    "lookThroughDuty": 0.1,
                    "targeting": {
                      "mode": "Auto",
                      "autoBands": [ { "startKhz": 225000, "endKhz": 400000 } ],
                      "autoMaxSpots": 2,
                      "detectionSensitivityDbm": -105,
                      "activityHalfLifeSeconds": 90,
                      "dropAfterSeconds": 20,
                      "reacquireDelaySeconds": 15,
                      "protectedFrequenciesKhz": [ 262000, 270500 ]
                    }
                  }
                ]
              }
            }
            """;
        var config = Json.Instance.Deserialize<ServerConfig>(json)!;
        var resolved = JammerPresets.Resolve(Assert.Single(config.Jamming.Jammers));
        var t = resolved.Targeting;

        Assert.Equal(JammerTargetingMode.Auto, t.Mode);
        Assert.Equal(225_000, Assert.Single(t.AutoBands).StartKhz);
        Assert.Equal(2, t.AutoMaxSpots);
        Assert.Equal(-105, t.DetectionSensitivityDbm);
        Assert.Equal(90, t.ActivityHalfLifeSeconds);
        Assert.Equal(20, t.DropAfterSeconds);
        Assert.Equal(15, t.ReacquireDelaySeconds);
        Assert.Equal([262_000, 270_500], t.ProtectedFrequenciesKhz);
    }
}
