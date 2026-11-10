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
    public async Task AutoJammer_FollowsRealTransmissions_AndTheCatalogFollowsWithinATick()
    {
        await using var server = await SignalingServerHarness.StartAsync(configure: config =>
        {
            config.Jamming = new JammingServerConfig
            {
                Enabled = true,
                Jammers =
                [
                    new JammerDefinition
                    {
                        Id = "auto-1", PresetId = "r934b", Latitude = 42.18, Longitude = 42.48,
                        Targeting = new JammerTargeting
                        {
                            Mode = JammerTargetingMode.Auto, AutoMaxSpots = 1,
                            ReacquireDelaySeconds = 1, DropAfterSeconds = 2
                        }
                    }
                ]
            };
        });
        await using var gci = RtcClientHarness.Create(server, "Magic");
        await using var viper = RtcClientHarness.Create(server, "Viper1");
        await gci.ConnectAsync();
        await viper.ConnectAsync();
        var first = await viper.JammerCatalog.WaitForAsync();
        Assert.Equal(AutoJammerState.Searching, first.Message.Jammers[0].AutoState);
        Assert.Empty(first.Message.Jammers[0].Emissions);

        // The controller talks on 251.0 from ~110 km away at FL200: found after the 1 s reacquire delay,
        // broadcast at the next 1 s tick.
        await gci.Client.JoinFrequencyAsync(251_000);
        await gci.Client.StartTransmissionAsync(251_000, 43.18, 42.48, 6000);
        var jammed = await viper.JammerCatalog.WaitForAsync(
            c => c.Message.Jammers[0].Emissions.Any(e => e.CenterKhz == 251_000), TimeSpan.FromSeconds(4));
        Assert.Equal(AutoJammerState.Jamming, jammed.Message.Jammers[0].AutoState);

        // CHATTERMARK: the net moves to 264.0; the jammer follows once the old net is quiet and the new one found.
        await gci.Client.StopTransmissionAsync(251_000);
        await gci.Client.JoinFrequencyAsync(264_000);
        await gci.Client.StartTransmissionAsync(264_000, 43.18, 42.48, 6000);
        var followed = await viper.JammerCatalog.WaitForAsync(
            c => c.Message.Jammers[0].Emissions.Any(e => e.CenterKhz == 264_000), TimeSpan.FromSeconds(6));
        Assert.DoesNotContain(followed.Message.Jammers[0].Emissions, e => e.CenterKhz == 251_000);

        // Silence: dropped after 2 s, back to searching.
        viper.JammerCatalog.Drain(); // earlier (empty) catalogs must not satisfy the wait below
        await gci.Client.StopTransmissionAsync(264_000);
        var dropped = await viper.JammerCatalog.WaitForAsync(c => c.Message.Jammers[0].Emissions.Count == 0,
            TimeSpan.FromSeconds(5));
        Assert.Equal(AutoJammerState.Searching, dropped.Message.Jammers[0].AutoState);
    }

    [Fact]
    public async Task AutoJammer_IgnoresTransmittersBeyondTheHorizon()
    {
        await using var server = await SignalingServerHarness.StartAsync(configure: config =>
        {
            config.Jamming = new JammingServerConfig
            {
                Enabled = true,
                Jammers =
                [
                    new JammerDefinition
                    {
                        Id = "auto-1", PresetId = "r934b", Latitude = 42.18, Longitude = 42.48,
                        Targeting = new JammerTargeting { Mode = JammerTargetingMode.Auto, ReacquireDelaySeconds = 0 }
                    }
                ]
            };
        });
        await using var gci = RtcClientHarness.Create(server, "Magic");
        await gci.ConnectAsync();
        await gci.JammerCatalog.WaitForAsync();

        await gci.Client.JoinFrequencyAsync(251_000);
        await gci.Client.StartTransmissionAsync(251_000, 47.18, 42.48, 300); // ~555 km, low
        await gci.JammerCatalog.AssertNoneAsync(c => c.Message.Jammers[0].Emissions.Count > 0,
            TimeSpan.FromMilliseconds(2500));
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
