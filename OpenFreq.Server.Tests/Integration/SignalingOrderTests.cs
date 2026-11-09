using System.Collections.Concurrent;
using OpenFreq.Common;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// The client sends signaling messages one at a time and in the order they were made, and a transmit
/// heartbeat can't outlive the stop that ended its transmission. Ported from upstream 36f948f, plus a
/// concurrent-caller test modelled on the server's SRS bridge, whose per-user shadow client is driven
/// fire-and-forget from several threads at once.
/// </summary>
public class SignalingOrderTests
{
    private const int Freq = 251_000; // 251.000 MHz
    private const int OtherFreq = 339_750; // 339.750 MHz

    [Fact]
    public async Task JoinLeaveBurst_ServerEndsInTheLastRequestedState()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await alice.ConnectAsync();

        // Make every call before awaiting any of them. Each must still reach the server in call order.
        var calls = new List<Task>();
        for (var i = 0; i < 25; i++)
        {
            calls.Add(alice.Client.JoinFrequencyAsync(Freq));
            calls.Add(alice.Client.LeaveFrequencyAsync(Freq));
        }

        calls.Add(alice.Client.JoinFrequencyAsync(Freq));
        await Task.WhenAll(calls);

        // The server handles a connection's messages in order, so once a later join is acknowledged,
        // everything above has been handled too.
        await alice.Client.JoinFrequencyAsync(OtherFreq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == OtherFreq);

        Assert.Equal(new[] { Freq, OtherFreq },
            server.Server.ChannelManager.GetClientChannels(alice.PeerId!).Order());
    }

    [Fact]
    public async Task RapidStartStop_EndsNotTransmitting()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await alice.ConnectAsync();
        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        // Every start launches a heartbeat. One that read "still transmitting" just before a stop used to
        // send after it, leaving the server showing us keyed until the next stop.
        for (var i = 0; i < 50; i++)
        {
            await alice.Client.StartTransmissionAsync(Freq);
            await alice.Client.StopTransmissionAsync(Freq);
        }

        // Several heartbeat periods (333 ms), so any straggler has had time to arrive.
        await Task.Delay(TimeSpan.FromSeconds(1));

        var status = server.Server.ChannelManager.GetAllChannelStates()[Freq].Single(p => p.Id == alice.PeerId).Status;
        Assert.Equal(PeerData.PeerStatus.Receiving, status);
    }

    [Fact]
    public async Task ConcurrentCallersOnManyThreads_SendsNeverOverlap()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await alice.ConnectAsync();
        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.Client.JoinFrequencyAsync(OtherFreq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == OtherFreq);

        // Like the SRS bridge's shadow client: starts fired from one thread, stops from another, other
        // traffic from a third, none awaiting the others. ClientWebSocket allows only one outstanding
        // SendAsync; an overlapping one throws, which the client reports through ErrorOccurred.
        using var go = new ManualResetEventSlim();
        var calls = new ConcurrentBag<Task>();
        Thread Spawn(Action body) => new(() =>
        {
            go.Wait();
            body();
        });

        var threads = new List<Thread>
        {
            Spawn(() =>
            {
                for (var i = 0; i < 200; i++) calls.Add(alice.Client.StartTransmissionAsync(Freq));
            }),
            Spawn(() =>
            {
                for (var i = 0; i < 200; i++) calls.Add(alice.Client.StopTransmissionAsync(Freq));
            }),
            Spawn(() =>
            {
                for (var i = 0; i < 200; i++) calls.Add(alice.Client.SendModeUpdateAsync());
            }),
            Spawn(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    calls.Add(alice.Client.StartTransmissionAsync(OtherFreq));
                    calls.Add(alice.Client.StopTransmissionAsync(OtherFreq));
                }
            }),
        };

        // And, like the bridge's radio-diff path, joins and leaves on yet more frequencies meanwhile.
        for (var t = 0; t < 4; t++)
        {
            var khz = 120_000 + t * 25;
            threads.Add(Spawn(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    calls.Add(alice.Client.JoinFrequencyAsync(khz));
                    calls.Add(alice.Client.LeaveFrequencyAsync(khz));
                }
            }));
        }

        foreach (var t in threads) t.Start();
        go.Set();
        foreach (var t in threads) t.Join();
        await Task.WhenAll(calls);

        await alice.Errors.AssertNoneAsync(window: TimeSpan.FromMilliseconds(500));

        // Still in sync with the server afterwards: a final stop on each frequency is the last word,
        // and no heartbeat from the burst outlives it.
        await alice.Client.StopTransmissionAsync(Freq);
        await alice.Client.StopTransmissionAsync(OtherFreq);
        await Task.Delay(TimeSpan.FromSeconds(1));

        var states = server.Server.ChannelManager.GetAllChannelStates();
        Assert.Equal(PeerData.PeerStatus.Receiving, states[Freq].Single(p => p.Id == alice.PeerId).Status);
        Assert.Equal(PeerData.PeerStatus.Receiving, states[OtherFreq].Single(p => p.Id == alice.PeerId).Status);
    }
}
