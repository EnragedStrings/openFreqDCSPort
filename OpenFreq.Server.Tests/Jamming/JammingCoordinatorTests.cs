using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common.Jamming;
using OpenFreqAudio.Jamming;
using OpenFreqServer;
using OpenFreqServer.Json;
using OpenFreqServer.Jamming;

namespace OpenFreq.Server.Tests.Jamming;

public class JammingConfigTests
{
    // The example from docs/jamming/JAMMING_CONFIG.md, as an admin would write it.
    private const string ExampleJson = """
        {
          "jamming": {
            "enabled": true,
            "broadcastIntervalSeconds": 5,
            "jammers": [
              {
                "id": "sinitsa-1",
                "presetId": "r934b",
                "coalition": "red",
                "latitude": 42.18,
                "longitude": 42.48,
                "pattern": "Sector",
                "azimuthDeg": 270,
                "technique": "Noise",
                "targeting": {
                  "spotFrequenciesKhz": [ 251000, 264000 ],
                  "bands": [ { "startKhz": 225000, "endKhz": 235000 } ]
                },
                "schedule": { "startDelaySeconds": 60, "onSeconds": 300, "offSeconds": 120 }
              },
              {
                "id": "fm-barrage",
                "presetId": "r330b",
                "dcsPosition": { "x": -281000, "y": 30, "z": 647000 },
                "txPowerW": 500,
                "technique": "SteppedTones",
                "toneOffsetsHz": [ -1500, -500, 500, 1500 ],
                "targeting": { "bands": [ { "startKhz": 30000, "endKhz": 40000 } ] }
              }
            ]
          }
        }
        """;

    [Fact]
    public void Default_JammingIsOff_WithNoJammers()
    {
        var config = new ServerConfig();
        Assert.False(config.Jamming.Enabled);
        Assert.Empty(config.Jamming.Jammers);
        Assert.Equal(5.0, config.Jamming.BroadcastIntervalSeconds);
    }

    [Fact]
    public void DocExample_Parses()
    {
        var config = Json.Instance.Deserialize<ServerConfig>(ExampleJson)!;

        Assert.True(config.Jamming.Enabled);
        Assert.Equal(2, config.Jamming.Jammers.Count);
        var sinitsa = config.Jamming.Jammers[0];
        Assert.Equal("r934b", sinitsa.PresetId);
        Assert.Equal(JammerAntennaPattern.Sector, sinitsa.Pattern);
        Assert.Equal(JammerTechnique.Noise, sinitsa.Technique);
        Assert.Equal([251_000, 264_000], sinitsa.Targeting.SpotFrequenciesKhz);
        Assert.Equal(235_000, sinitsa.Targeting.Bands[0].EndKhz);
        Assert.Equal(300, sinitsa.Schedule!.OnSeconds);
        Assert.Null(sinitsa.TxPowerW); // left to the preset

        var fm = config.Jamming.Jammers[1];
        Assert.Equal(647_000, fm.DcsPosition!.Z);
        Assert.Equal(JammerTechnique.SteppedTones, fm.Technique);
        Assert.Equal(4, fm.ToneOffsetsHz!.Count);
        Assert.Equal(500, fm.TxPowerW);
    }

    [Fact]
    public void Config_RoundTrips()
    {
        var config = Json.Instance.Deserialize<ServerConfig>(ExampleJson)!;
        var back = Json.Instance.Deserialize<ServerConfig>(Json.Instance.Serialize(config))!;

        Assert.True(back.Jamming.Enabled);
        Assert.Equal(2, back.Jamming.Jammers.Count);
        Assert.Equal(42.18, back.Jamming.Jammers[0].Latitude);
        Assert.Equal(JammerAntennaPattern.Sector, back.Jamming.Jammers[0].Pattern);
        Assert.Equal(60, back.Jamming.Jammers[0].Schedule!.StartDelaySeconds);
        Assert.Equal(JammerTechnique.SteppedTones, back.Jamming.Jammers[1].Technique);
        Assert.True(back.Jamming.Jammers[1].Targeting.ProtectGuard);
    }
}

public class JammingCoordinatorTests
{
    private DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private JammingCoordinator Create(params JammerDefinition[] jammers) =>
        new(new JammingServerConfig { Enabled = true, Jammers = [.. jammers] }, NullLogger.Instance, () => _now);

    private static JammerDefinition Spot(string id, params int[] khz) => new()
    {
        Id = id, PresetId = "r934b", Latitude = 42, Longitude = 42,
        Targeting = new JammerTargeting { SpotFrequenciesKhz = [.. khz] }
    };

    [Fact]
    public void Snapshot_ResolvesPresets_AndRadiatesConfiguredSpots()
    {
        var coordinator = Create(Spot("a", 251_000));
        var entry = Assert.Single(coordinator.Snapshot());

        Assert.True(entry.Active);
        Assert.Equal(500, entry.Definition.TxPowerW);
        Assert.Equal(251_000, Assert.Single(entry.Emissions).CenterKhz);
    }

    [Fact]
    public void DuplicateAndMissingIds_AreHandled()
    {
        var coordinator = Create(Spot("a", 251_000), Spot("a", 260_000), Spot("", 270_000));
        Assert.Equal(["a", "jammer-3"], coordinator.Jammers.Select(j => j.Id));
    }

    [Fact]
    public void DisabledOrDeadJammer_IsListedButSilent()
    {
        var coordinator = Create(Spot("a", 251_000), Spot("b", 260_000));
        Assert.True(coordinator.SetJammerState("a", enabled: false));
        Assert.True(coordinator.SetJammerState("b", alive: false));
        Assert.False(coordinator.SetJammerState("nope", enabled: false));

        Assert.All(coordinator.Snapshot(), e =>
        {
            Assert.False(e.Active);
            Assert.Empty(e.Emissions);
        });
    }

    [Fact]
    public void Schedule_FollowsTheClock()
    {
        var jammer = Spot("a", 251_000);
        jammer.Schedule = new JammerSchedule { StartDelaySeconds = 10, OnSeconds = 5, OffSeconds = 5 };
        var coordinator = Create(jammer);

        Assert.False(coordinator.Snapshot()[0].Active);
        _now += TimeSpan.FromSeconds(11);
        Assert.True(coordinator.Snapshot()[0].Active);
        _now += TimeSpan.FromSeconds(5);
        Assert.False(coordinator.Snapshot()[0].Active);
    }

    [Fact]
    public void NextBroadcast_OnlyOnChangeOrWhenPeriodicIsDue()
    {
        var coordinator = Create(Spot("a", 251_000));

        Assert.NotNull(coordinator.NextBroadcast(periodicDue: false)); // first one always goes out
        Assert.Null(coordinator.NextBroadcast(periodicDue: false));
        Assert.NotNull(coordinator.NextBroadcast(periodicDue: true));

        coordinator.SetJammerState("a", alive: false);
        var changed = coordinator.NextBroadcast(periodicDue: false);
        Assert.NotNull(changed);
        Assert.False(changed[0].Active);
        Assert.Null(coordinator.NextBroadcast(periodicDue: false));
    }

    [Fact]
    public void GuardIsProtectedFromBarrage_ByDefault()
    {
        var jammer = new JammerDefinition
        {
            Id = "b", PresetId = "r934b", Latitude = 42, Longitude = 42,
            Targeting = new JammerTargeting { Bands = [new JammerBand(225_000, 260_000)] }
        };
        var emissions = Create(jammer).Snapshot()[0].Emissions;

        Assert.Equal(2, emissions.Count);
        Assert.DoesNotContain(emissions, e => e.StartKhz < 243_000 && e.EndKhz > 243_000);
    }
}
