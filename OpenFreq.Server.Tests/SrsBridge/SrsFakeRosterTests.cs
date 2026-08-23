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
    private static ClientSession CreateSession(string id, string displayName, bool authenticated = true,
        params int[] joinedKhz)
    {
        var session = new ClientSession(id, displayName, new FakeWebSocket(), "127.0.0.1")
        {
            IsAuthenticated = authenticated
        };
        foreach (var khz in joinedKhz)
            session.CurrentFrequencies[khz] = ClientSession.FrequencyClientStatus.Receiving;
        return session;
    }

    private static SrsBridgeServer CreateBridge(ConcurrentDictionary<string, ClientSession> openFreqClients)
        => new(new ServerConfig(), null!, NullLoggerFactory.Instance, openFreqClients, null!);

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
