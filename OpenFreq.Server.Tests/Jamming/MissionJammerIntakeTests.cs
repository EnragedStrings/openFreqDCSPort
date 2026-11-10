using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common;
using OpenFreq.Common.Jamming;
using OpenFreqServer.Jamming;

namespace OpenFreqServer.Tests.Jamming;

/// <summary>Phase 6: mission jammers from the DCS server hook (docs/jamming/MISSION_JAMMERS.md).</summary>
public class MissionJammerIntakeTests
{
    private const string Secret = "0123456789abcdef-test-secret";

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string Unit(string name, bool alive = true, string kind = "ground", double lat = 42.0,
        double lon = 43.0, double alt = 500.0, string group = "G1") =>
        $$"""{"name":{{System.Text.Json.JsonSerializer.Serialize(name)}},"group":"{{group}}","kind":"{{kind}}","side":1,"type":"Ural-375","alive":{{(alive ? "true" : "false")}},"lat":{{lat}},"lon":{{lon}},"alt":{{alt}},"x":-281000.5,"y":{{alt}},"z":647000.25}""";

    private static string Json(long seq, string units, string boot = "5f3a9c01", long? ts = null,
        string mission = "Op Test|1731263000") =>
        $$"""{"v":1,"boot":"{{boot}}","seq":{{seq}},"ts":{{ts ?? Now}},"mission":"{{mission}}","units":[{{units}}]}""";

    private static byte[] Datagram(string json, string secret = Secret)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var mac = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
        return [.. Encoding.ASCII.GetBytes("OFJ1 " + mac + " "), .. body];
    }

    // ---------------------------------------------------------------- authentication / replay

    [Fact]
    public void AcceptsAnAuthenticDatagram()
    {
        var p = new MissionJammerProtocol(Secret);
        var report = p.TryAccept(Datagram(Json(1, Unit("JAM:r934b"))), DateTimeOffset.UtcNow);
        Assert.NotNull(report);
        Assert.Equal("Op Test|1731263000", report.Mission);
        var unit = Assert.Single(report.Units);
        Assert.Equal(647000.25, unit.Z);
    }

    [Fact]
    public void RejectsWrongSecretTamperingAndGarbage()
    {
        var p = new MissionJammerProtocol(Secret);
        Assert.Null(p.TryAccept(Datagram(Json(1, Unit("JAM:r934b")), "another-secret-of-16+"), DateTimeOffset.UtcNow));
        Assert.Equal("bad HMAC", p.LastRejection);

        var good = Datagram(Json(2, Unit("JAM:r934b")));
        good[^5] ^= 1; // flip a bit inside the JSON
        Assert.Null(p.TryAccept(good, DateTimeOffset.UtcNow));

        Assert.Null(p.TryAccept(Encoding.ASCII.GetBytes("hello"), DateTimeOffset.UtcNow));
        Assert.Null(p.TryAccept(new byte[MissionJammerProtocol.MaxDatagramBytes + 1], DateTimeOffset.UtcNow));
        Assert.Equal("oversized", p.LastRejection);
    }

    [Fact]
    public void RejectsReplaysAndStaleTimestamps()
    {
        var p = new MissionJammerProtocol(Secret);
        var now = DateTimeOffset.UtcNow;
        Assert.NotNull(p.TryAccept(Datagram(Json(5, "")), now));
        Assert.Null(p.TryAccept(Datagram(Json(5, "")), now));
        Assert.Null(p.TryAccept(Datagram(Json(4, "")), now));
        Assert.Equal("replayed sequence number", p.LastRejection);
        Assert.NotNull(p.TryAccept(Datagram(Json(6, "")), now));

        // A new hook boot restarts the sequence.
        Assert.NotNull(p.TryAccept(Datagram(Json(1, "", boot: "aa00bb11")), now));

        Assert.Null(p.TryAccept(Datagram(Json(2, "", boot: "aa00bb11", ts: Now - 3600)), now));
        Assert.StartsWith("timestamp", p.LastRejection);
    }

    // ---------------------------------------------------------------- unit → jammer

    private static MissionJammerReport Report(params string[] units) =>
        new MissionJammerProtocol(Secret).TryAccept(Datagram(Json(1, string.Join(',', units))), DateTimeOffset.UtcNow)!;

    [Fact]
    public void GroundUnitGetsTheMastHeightAirUnitDoesNot()
    {
        var warnings = new List<string>();
        var defs = MissionJammerProtocol.BuildDefinitions(
            Report(Unit("JAM:r934b;spot=251", alt: 500), Unit("JAM:il22pp", kind: "air", alt: 7000)), "JAM:", 200,
            _ => null, warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, defs.Count);
        var ground = defs[0];
        Assert.Equal("dcs:JAM:r934b;spot=251", ground.Id);
        Assert.Equal(512, ground.AltitudeMeters); // 500 m ground + 12 m mast
        Assert.Equal(512, ground.DcsPosition!.Y);
        Assert.Equal(-281000.5, ground.DcsPosition.X);
        Assert.Equal(42.0, ground.Latitude);
        Assert.Equal("red", ground.Coalition);
        Assert.Equal("JAM:r934b;spot=251", ground.Name);
        Assert.Equal(7000, defs[1].AltitudeMeters);
    }

    [Fact]
    public void GroupTagTemplatesAndBadTags()
    {
        var template = new JammerDefinition { Id = "sinitsa", PresetId = "r934b", Template = true, TxPowerW = 333 };
        var warnings = new List<string>();
        var defs = MissionJammerProtocol.BuildDefinitions(
            Report(
                Unit("Ural #001", group: "JAM:leer2"),
                Unit("JAM:@sinitsa;name=S1"),
                Unit("JAM:@nosuch"),
                Unit("JAM:nosuchpreset"),
                Unit("plain truck")),
            "JAM:", 200, id => id == "sinitsa" ? template.Clone() : null, warnings);

        Assert.Equal(["dcs:Ural #001", "dcs:JAM:@sinitsa;name=S1"], defs.Select(d => d.Id));
        Assert.Equal("leer2", defs[0].PresetId);
        Assert.Equal(333, defs[1].TxPowerW);
        Assert.Equal("S1", defs[1].Name);
        Assert.False(defs[1].Template);
        Assert.Equal(3, warnings.Count);
    }

    [Fact]
    public void DeadUnitWithoutPositionIsKeptAsNotAlive()
    {
        var report = Report(Unit("JAM:r934b"));
        report.Units[0].Alive = false;
        report.Units[0].Lat = report.Units[0].Lon = report.Units[0].X = report.Units[0].Z = null;
        var d = Assert.Single(MissionJammerProtocol.BuildDefinitions(report, "JAM:", 200, _ => null, new List<string>()));
        Assert.False(d.Alive);
        Assert.Null(d.Latitude);
    }

    [Fact]
    public void MaxJammersCapsTheList()
    {
        var warnings = new List<string>();
        var defs = MissionJammerProtocol.BuildDefinitions(Report(Unit("JAM:r934b;name=a"), Unit("JAM:r934b;name=b"),
            Unit("JAM:r934b;name=c")), "JAM:", 2, _ => null, warnings);
        Assert.Equal(2, defs.Count);
        Assert.Single(warnings);
    }

    // ---------------------------------------------------------------- coordinator

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    }

    private static JammingCoordinator Coordinator(Clock clock, params JammerDefinition[] config) =>
        new(new JammingServerConfig { Enabled = true, Jammers = config.ToList() }, NullLogger.Instance,
            () => clock.Now);

    private static JammerDefinition Mission(string id, bool alive = true, JammerTargetingMode mode =
        JammerTargetingMode.Scripted) => new()
    {
        Id = id, PresetId = "r934b", Alive = alive, Latitude = 42, Longitude = 43,
        Targeting = new JammerTargeting { Mode = mode, SpotFrequenciesKhz = [251_000] }
    };

    [Fact]
    public void MissionJammersAddToConfigJammersAndTemplatesNeverRadiate()
    {
        var clock = new Clock();
        var c = Coordinator(clock,
            new JammerDefinition { Id = "cfg", PresetId = "r934b", Latitude = 1, Longitude = 1,
                Targeting = new JammerTargeting { SpotFrequenciesKhz = [240_000] } },
            new JammerDefinition { Id = "tpl", PresetId = "r934b", Template = true, Latitude = 1, Longitude = 1,
                Targeting = new JammerTargeting { SpotFrequenciesKhz = [240_000] } });

        Assert.Equal(["cfg"], c.Jammers.Select(j => j.Id));
        Assert.NotNull(c.FindConfigJammer("tpl"));

        c.SetMissionJammers([Mission("dcs:a"), Mission("dcs:b", alive: false), Mission("cfg")]);
        var snapshot = c.Snapshot();
        Assert.Equal(["cfg", "dcs:a", "dcs:b"], snapshot.Select(e => e.Definition.Id));
        Assert.True(snapshot.Single(e => e.Definition.Id == "dcs:a").Active);
        Assert.False(snapshot.Single(e => e.Definition.Id == "dcs:b").Active); // dead: radiates nothing

        c.SetMissionJammers([Mission("dcs:a")]);
        Assert.Equal(["cfg", "dcs:a"], c.Jammers.Select(j => j.Id));

        c.ClearMissionJammers();
        Assert.Equal(["cfg"], c.Jammers.Select(j => j.Id));
    }

    [Fact]
    public void MissionJammersExpireWhenTheHookGoesQuiet()
    {
        var clock = new Clock();
        var c = Coordinator(clock);
        c.MissionStaleAfter = TimeSpan.FromSeconds(30);
        c.SetMissionJammers([Mission("dcs:a")]);

        clock.Now += TimeSpan.FromSeconds(29);
        Assert.Single(c.Snapshot());
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Empty(c.Snapshot());
    }

    [Fact]
    public void MovingJammerTriggersARebroadcast()
    {
        var clock = new Clock();
        var c = Coordinator(clock);
        c.SetMissionJammers([Mission("dcs:a")]);
        Assert.NotNull(c.NextBroadcast(periodicDue: false));
        Assert.Null(c.NextBroadcast(periodicDue: false));

        var moved = Mission("dcs:a");
        moved.Latitude = 42.01;
        c.SetMissionJammers([moved]);
        Assert.NotNull(c.NextBroadcast(periodicDue: false));
    }

    [Fact]
    public void AutoBrainSurvivesReReports()
    {
        var clock = new Clock();
        var c = Coordinator(clock);
        c.SetMissionJammers([Mission("dcs:auto", mode: JammerTargetingMode.Auto)]);
        c.ReportTransmission("tx1", 251_000, transmitting: true, latitude: 42.1, longitude: 43.1, altitudeMeters: 3000);
        for (var i = 0; i < 15; i++)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            c.ReportTransmission("tx1", 251_000, transmitting: true);
            c.SetMissionJammers([Mission("dcs:auto", mode: JammerTargetingMode.Auto)]); // re-reported every tick
            c.Snapshot();
        }

        var entry = c.Snapshot().Single();
        Assert.Contains(251_000, entry.AutoTargetsKhz!);
    }

    // ---------------------------------------------------------------- end to end

    [Fact]
    public void IntakeHandleFeedsTheCoordinatorAndAnEmptyMissionClearsIt()
    {
        var clock = new Clock();
        var c = Coordinator(clock);
        var intake = new MissionJammerIntake(new DcsJammerHookConfig { Enabled = true, Secret = Secret }, c,
            NullLogger.Instance);

        intake.Handle(Datagram(Json(1, Unit("JAM:r934b;spot=251"))));
        var j = Assert.Single(c.Jammers);
        Assert.Equal("dcs:JAM:r934b;spot=251", j.Id);
        Assert.Equal(500, j.TxPowerW); // resolved against the preset

        intake.Handle(Datagram(Json(2, "", mission: "")));
        Assert.Empty(c.Jammers);
    }

    [Fact]
    public void IntakeRefusesAShortSecret()
    {
        var intake = new MissionJammerIntake(new DcsJammerHookConfig { Enabled = true, Secret = "short", Port = 0 },
            Coordinator(new Clock()), NullLogger.Instance);
        Assert.False(intake.Start());
    }
}
