using System.Text.Json;
using OpenFreq.Common.Eccm;
using OpenFreqServer;
using OpenFreqServer.Json;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>The HAVE QUICK / SINCGARS comm plan (docs/jamming/HAVE_QUICK.md): config → fingerprints → client.</summary>
public class EccmCommPlanIntegrationTests
{
    private const string ConfigJson = """
        {
          "eccm": {
            "missionDate": "2026-11-09",
            "hopRateHz": 80,
            "mwod": [ { "date": "2026-11-09", "wod": "300.050 251.000 277.775 312.600 225.025 399.975", "label": "D1" } ],
            "fmtFrequenciesKhz": [ 250000, 260000, 270000, 280000, 290000 ],
            "sincgars": { "transecKey": "BLUE-TEK", "lockouts": [ { "startKhz": 40000, "endKhz": 45000 } ] }
          }
        }
        """;

    [Fact]
    public void Config_ParsesTheEccmSection()
    {
        var config = JsonSerializer.Deserialize(ConfigJson, ServerJsonContext.Default.ServerConfig)!;
        Assert.NotNull(config.Eccm);
        Assert.True(config.Eccm!.Enabled);
        Assert.Single(config.Eccm.Mwod);
        Assert.Equal(80, config.Eccm.HopRateHz);
        Assert.Null(new ServerConfig().Eccm);
    }

    [Fact]
    public async Task Plan_IsSentRightAfterAuthenticate_WithoutKeyMaterial()
    {
        var parsed = JsonSerializer.Deserialize(ConfigJson, ServerJsonContext.Default.ServerConfig)!;
        await using var server = await SignalingServerHarness.StartAsync(configure: c => c.Eccm = parsed.Eccm);
        await using var client = RtcClientHarness.Create(server, "Hog1");

        await client.ConnectAsync();
        var message = await client.EccmCommPlan.WaitForAsync();

        var plan = message.Plan!;
        Assert.Equal("2026-11-09", plan.MissionDate);
        Assert.Equal(80, plan.HopRateHz);
        var wod = Assert.Single(plan.Mwod);
        Assert.Equal(HaveQuickWod.TryParse("300050 251000 277775 312600 225025 399975", out _)!
            .Fingerprint(new DateOnly(2026, 11, 9)), wod.Fingerprint);
        Assert.Equal(5, plan.FmtFrequenciesKhz!.Count);
        Assert.NotNull(plan.Sincgars!.KeyFingerprint);

        var wire = JsonSerializer.Serialize(plan, OpenFreq.Common.OpenFreqJsonContext.Default.EccmCommPlan);
        Assert.DoesNotContain("300.050", wire);
        Assert.DoesNotContain("BLUE-TEK", wire);
    }

    [Fact]
    public async Task NoEccmSection_SendsNoPlan()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        Assert.Null(server.Server.EccmCommPlan());
    }
}
