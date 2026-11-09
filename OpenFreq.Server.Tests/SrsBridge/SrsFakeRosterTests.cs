using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreqServer;
using OpenFreqServer.SrsBridge;

namespace OpenFreq.Server.Tests.SrsBridge;

/// <summary>
/// Covers synthesizing fake SRS-shaped roster entries for real OpenFreq clients, so SRS's own
/// roster (which has no field for a peer speaking a different protocol) at least shows who else
/// is on the server. See SrsBridgeServer.BuildFakeOpenFreqEntries/GetRosterSnapshot.
/// </summary>
public class SrsFakeRosterTests
{
    // xUnit builds a fresh instance per test, so each test gets its own channel table.
    private readonly FrequencyChannelManager _channels = new();

    private ClientSession CreateSession(string id, string displayName, bool authenticated = true,
        params int[] joinedKhz)
    {
        var session = new ClientSession(id, displayName, new FakeWebSocket(), "127.0.0.1")
        {
            IsAuthenticated = authenticated
        };
        foreach (var khz in joinedKhz)
            _channels.JoinChannel(khz, id, displayName);
        return session;
    }

    private SrsBridgeServer CreateBridge(ConcurrentDictionary<string, ClientSession> openFreqClients)
        => new(new ServerConfig(), null!, NullLoggerFactory.Instance, openFreqClients, null!, _channels);

    [Fact]
    public void GetRosterSnapshot_IncludesFakeEntryForRealOpenFreqClient()
    {
        var clients = new ConcurrentDictionary<string, ClientSession>();
        clients["peer-1"] = CreateSession("peer-1", "Viper1", joinedKhz: 251_000);

        var bridge = CreateBridge(clients);

        var roster = bridge.GetRosterSnapshot();

        var fake = Assert.Single(roster);
        Assert.Equal("Viper1 (OpenFreq)", fake.Name);
        Assert.Equal(22, fake.ClientGuid.Length);
        Assert.NotNull(fake.RadioInfo);
        Assert.Contains(fake.RadioInfo!.Radios, r => r.IsTuned && (int)Math.Round(r.Freq / 1000.0) == 251_000);
    }

    [Fact]
    public void GetRosterSnapshot_ExcludesUnauthenticatedClients()
    {
        var clients = new ConcurrentDictionary<string, ClientSession>();
        clients["peer-1"] = CreateSession("peer-1", "NotYetAuthed", authenticated: false, joinedKhz: 30_000);

        var bridge = CreateBridge(clients);

        Assert.Empty(bridge.GetRosterSnapshot());
    }

    [Fact]
    public void GetRosterSnapshot_ExcludesRegisteredShadowPeers()
    {
        var clients = new ConcurrentDictionary<string, ClientSession>();
        clients["srs-shadow-1"] = CreateSession("srs-shadow-1", "Hog 6-9 (SRS)", joinedKhz: 251_000);

        var bridge = CreateBridge(clients);
        bridge.RegisterShadowPeer("srs-shadow-1");

        Assert.Empty(bridge.GetRosterSnapshot());
    }

    [Fact]
    public void GetRosterSnapshot_SameOpenFreqIdProducesStableGuid()
    {
        var clients = new ConcurrentDictionary<string, ClientSession>();
        clients["peer-1"] = CreateSession("peer-1", "Viper1", joinedKhz: 251_000);
        var bridge = CreateBridge(clients);

        var first = bridge.GetRosterSnapshot().Single().ClientGuid;
        var second = bridge.GetRosterSnapshot().Single().ClientGuid;

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetRosterSnapshot_ExcludesSilentlyObservedFrequencies()
    {
        // A GCI scanner's observer joins must never surface in the fake roster -- that would
        // reveal to SRS users that someone is silently listening on their frequency.
        var clients = new ConcurrentDictionary<string, ClientSession>();
        clients["gci-1"] = CreateSession("gci-1", "Magic", joinedKhz: 251_000);
        _channels.JoinChannel(135_000, "gci-1", "Magic", isObserver: true);
        _channels.JoinChannel(30_000, "gci-1", "Magic", isObserver: true);

        var bridge = CreateBridge(clients);

        var entry = Assert.Single(bridge.GetRosterSnapshot());
        var tuned = entry.RadioInfo!.Radios.Where(r => r.IsTuned)
            .Select(r => (int)Math.Round(r.Freq / 1000.0)).ToList();
        Assert.Equal([251_000], tuned);
    }

    [Fact]
    public void GetRosterSnapshot_ObserverOnlyClient_ListedWithNoTunedRadios()
    {
        // Same as a connected client with no radios tuned: the client itself is visible on the
        // server, but nothing about what it is silently scanning is.
        var clients = new ConcurrentDictionary<string, ClientSession>();
        clients["gci-1"] = CreateSession("gci-1", "Magic");
        _channels.JoinChannel(251_000, "gci-1", "Magic", isObserver: true);

        var bridge = CreateBridge(clients);

        var entry = Assert.Single(bridge.GetRosterSnapshot());
        Assert.DoesNotContain(entry.RadioInfo!.Radios, r => r.IsTuned);
    }

    private sealed class FakeWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
