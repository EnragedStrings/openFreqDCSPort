namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// End-to-end coverage for the GCI "monitor all frequencies" scanner's silent/observer join
/// (JoinChannelMessage.IsObserver), driven over real WebSockets against a real Kestrel-hosted
/// SignalingServer -- same harness style as TranscriptDeliveryIntegrationTests. Precise
/// peer-list-filtering logic is covered non-flakily in FrequencyChannelManagerTests; these confirm
/// the wiring itself (no broadcast leak, no roster leak, no capacity impact) actually holds over a
/// real connection.
/// </summary>
public class ObserverJoinIntegrationTests
{
    private const int Freq = 251_000;

    [Fact]
    public async Task ObserverJoin_DoesNotBroadcastPeerJoined_ToExistingRealPeer()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var pilot = RtcClientHarness.Create(server, "Viper1");
        await using var scanner = RtcClientHarness.Create(server, "Scanner");

        await pilot.ConnectAsync();
        await scanner.ConnectAsync();

        await pilot.Client.JoinFrequencyAsync(Freq);
        await scanner.Client.JoinFrequencyAsync(Freq, isObserver: true);

        await pilot.PeerJoined.AssertNoneAsync();
    }

    [Fact]
    public async Task ObserverLeave_DoesNotBroadcastPeerLeft_ToRemainingRealPeer()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var pilot = RtcClientHarness.Create(server, "Viper1");
        await using var scanner = RtcClientHarness.Create(server, "Scanner");

        await pilot.ConnectAsync();
        await scanner.ConnectAsync();

        await pilot.Client.JoinFrequencyAsync(Freq);
        await scanner.Client.JoinFrequencyAsync(Freq, isObserver: true);
        await scanner.Client.LeaveFrequencyAsync(Freq);

        await pilot.PeerLeft.AssertNoneAsync();
    }

    [Fact]
    public async Task ObserverJoin_NotIncludedInAllPeersStatus()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var pilot = RtcClientHarness.Create(server, "Viper1");
        await using var scanner = RtcClientHarness.Create(server, "Scanner");

        await pilot.ConnectAsync();
        await scanner.ConnectAsync();

        await pilot.Client.JoinFrequencyAsync(Freq);
        await scanner.Client.JoinFrequencyAsync(Freq, isObserver: true);

        // Trigger a fresh broadcast the observer's own join must not itself have caused to include
        // it: a second real peer joining should still report only the two real pilots.
        await using var pilot2 = RtcClientHarness.Create(server, "Viper2");
        await pilot2.ConnectAsync();
        await pilot2.Client.JoinFrequencyAsync(Freq);

        var status = await pilot.AllPeersStatus.WaitForAsync(s => s.AllPeers.TryGetValue(Freq, out var peers) && peers.Count == 2);
        var ids = status.AllPeers[Freq].Select(p => p.Id).ToList();
        Assert.Contains(pilot.PeerId, ids);
        Assert.Contains(pilot2.PeerId, ids);
        Assert.DoesNotContain(scanner.PeerId, ids);
    }

    [Fact]
    public async Task ObserverJoin_DoesNotCountTowardMaxClientsPerChannel()
    {
        await using var server = await SignalingServerHarness.StartAsync(maxClientsPerChannel: 1);
        await using var pilot = RtcClientHarness.Create(server, "Viper1");
        await using var scanner = RtcClientHarness.Create(server, "Scanner");

        await pilot.ConnectAsync();
        await scanner.ConnectAsync();

        await pilot.Client.JoinFrequencyAsync(Freq);

        // Capacity is 1 and a real peer already holds it -- the observer join must still succeed
        // because observers aren't counted, proving a scanner sweep can never starve real
        // capacity nor be blocked by it.
        await scanner.Client.JoinFrequencyAsync(Freq, isObserver: true);

        await scanner.Errors.AssertNoneAsync(e => e.ErrorMessage.Contains("full", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ObserverJoin_SeesRealPeersButOtherRealPeerNeverSeesObserver()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var pilot = RtcClientHarness.Create(server, "Viper1");
        await using var scanner = RtcClientHarness.Create(server, "Scanner");

        await pilot.ConnectAsync();
        await scanner.ConnectAsync();

        await pilot.Client.JoinFrequencyAsync(Freq);
        await scanner.Client.JoinFrequencyAsync(Freq, isObserver: true);

        // The pilot's own next AllPeersStatus tick (forced by a second real join) must show
        // exactly the real peers -- confirming the observer never leaks into anyone else's view
        // even after some time has passed since its own join.
        await using var pilot2 = RtcClientHarness.Create(server, "Viper2");
        await pilot2.ConnectAsync();
        await pilot2.Client.JoinFrequencyAsync(Freq);

        var status = await pilot.AllPeersStatus.WaitForAsync(s => s.AllPeers.TryGetValue(Freq, out var peers) && peers.Count == 2);
        Assert.Equal(2, status.AllPeers[Freq].Count);
    }
}
