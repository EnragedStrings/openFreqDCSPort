using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreqServer;

namespace OpenFreq.Server.Tests;

/// <summary>
/// Covers LosOracleService's non-blocking cache behavior -- TryGetLineOfSight must never block
/// on a network round trip, so with no real SignalingServer to answer requests, every call here
/// stays "unknown" (null) rather than throwing or hanging. Selection logic itself (direct/sticky/
/// fallback) is exercised indirectly: these tests confirm the cold-cache path is safe to call even
/// when SelectOracle would pick a candidate, since the fire-and-forget refresh's failure is swallowed.
/// Shared by the SRS bridge and native transcript-delivery gating -- see the service's own doc
/// comment.
/// </summary>
public class LosOracleServiceTests
{
    private static ClientSession CreateSession(string id, bool presenceFresh)
    {
        var session = new ClientSession(id, id, new FakeWebSocket(), "127.0.0.1") { IsAuthenticated = true };
        if (presenceFresh)
        {
            session.DcsLatitudeDeg = 45.0;
            session.DcsLongitudeDeg = 45.0;
            session.DcsAltitudeMeters = 3000;
            session.DcsTheater = "Caucasus";
            session.DcsPresenceUpdatedUtc = DateTime.UtcNow;
        }

        return session;
    }

    [Fact]
    public void ColdPair_NoConnectedClients_ReturnsNullWithoutThrowing()
    {
        var clients = new ConcurrentDictionary<string, ClientSession>();
        var service = new LosOracleService(null!, clients, NullLogger.Instance);

        var result = service.TryGetLineOfSight("pair-1", otherEndClientId: null,
            45.0, 45.0, 3000, 45.1, 45.1, 3000);

        Assert.Null(result);
    }

    [Fact]
    public void ColdPair_OtherEndNotPresenceFresh_ReturnsNullWithoutThrowing()
    {
        var clients = new ConcurrentDictionary<string, ClientSession>();
        clients["peer-1"] = CreateSession("peer-1", presenceFresh: false);
        var service = new LosOracleService(null!, clients, NullLogger.Instance);

        var result = service.TryGetLineOfSight("pair-1", "peer-1", 45.0, 45.0, 3000, 45.1, 45.1, 3000);

        Assert.Null(result);
    }

    [Fact]
    public void RepeatedCalls_OnColdPair_DoNotThrow()
    {
        var clients = new ConcurrentDictionary<string, ClientSession>();
        clients["peer-1"] = CreateSession("peer-1", presenceFresh: true);
        var service = new LosOracleService(null!, clients, NullLogger.Instance);

        // Repeated calls exercise the "already refreshing" branch too -- must stay non-blocking
        // and non-throwing even though the fire-and-forget RequestRemoteLineOfSightAsync call
        // against a null SignalingServer will fault internally.
        for (var i = 0; i < 5; i++)
        {
            var result = service.TryGetLineOfSight("pair-1", "peer-1", 45.0, 45.0, 3000, 45.1, 45.1, 3000);
            Assert.Null(result);
        }
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
