using OpenFreq.Common.Jamming;
using OpenFreqServer;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>The jammer catalog over a real connection: sent after authenticate, re-sent on change.</summary>
public class JammingIntegrationTests
{
    private static void WithJammer(ServerConfig config)
    {
        config.Jamming = new JammingServerConfig
        {
            Enabled = true,
            Jammers =
            [
                new JammerDefinition
                {
                    Id = "sinitsa-1", PresetId = "r934b", Latitude = 42.18, Longitude = 42.48,
                    Targeting = new JammerTargeting { SpotFrequenciesKhz = [251_000] }
                }
            ]
        };
    }

    [Fact]
    public async Task Catalog_IsSentRightAfterAuthenticate()
    {
        await using var server = await SignalingServerHarness.StartAsync(configure: WithJammer);
        await using var client = RtcClientHarness.Create(server, "Viper1");

        await client.ConnectAsync();
        var catalog = await client.JammerCatalog.WaitForAsync();

        var entry = Assert.Single(catalog.Message.Jammers);
        Assert.Equal("sinitsa-1", entry.Definition.Id);
        Assert.True(entry.Active);
        Assert.Equal(500, entry.Definition.TxPowerW);
        Assert.Equal(251_000, Assert.Single(entry.Emissions).CenterKhz);
    }

    [Fact]
    public async Task KillingTheJammer_IsBroadcastWithinOneTick()
    {
        await using var server = await SignalingServerHarness.StartAsync(configure: WithJammer);
        await using var client = RtcClientHarness.Create(server, "Viper1");
        await client.ConnectAsync();
        await client.JammerCatalog.WaitForAsync(c => c.Message.Jammers[0].Active);

        Assert.True(server.Server.Jamming!.SetJammerState("sinitsa-1", alive: false));

        var update = await client.JammerCatalog.WaitForAsync(c => !c.Message.Jammers[0].Active,
            TimeSpan.FromSeconds(3));
        Assert.Empty(update.Message.Jammers[0].Emissions);
        Assert.False(update.Message.Jammers[0].Definition.Alive);
    }

    [Fact]
    public async Task JammingDisabled_SendsNoCatalog()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var client = RtcClientHarness.Create(server, "Viper1");
        await client.ConnectAsync();

        Assert.Null(server.Server.Jamming);
        await client.JammerCatalog.AssertNoneAsync(window: TimeSpan.FromMilliseconds(1500));
    }
}
