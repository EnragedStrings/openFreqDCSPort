using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace OpenFreqServer;

public class ClientSession(string id, string displayName, WebSocket webSocket, string ip) : IDisposable
{
    private int _disposed;
    public bool IsDisposed => _disposed == 1;
    public string Id { get; } = id;
    public WebSocket WebSocket { get; } = webSocket;
    public bool IsAuthenticated { get; set; }
    public ConcurrentDictionary<int, FrequencyClientStatus> CurrentFrequencies { get; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string? DisplayName { get; set; } = displayName;
    public string Ip { get; set; } = ip;
    public bool Is3d { get; set; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);

    /// <summary>Opt-in capability declared at authenticate time -- see
    /// AuthenticateMessage.WantsTranscripts's own doc comment. A normal GUI client never sets this;
    /// a bot client does.</summary>
    public bool WantsTranscripts { get; set; }

    /// <summary>The position (if any) this client declared when joining a given frequency -- see
    /// JoinChannelMessage.Lat/Lon/Alt. Only meaningful alongside WantsTranscripts: a bot operating
    /// multiple named positions (e.g. "Nellis Tower" on one frequency, "Luke Tower" on another)
    /// declares each one's own position separately. Absent for a frequency means "unknown position,"
    /// not "no position" -- see TranscriptDeliveryMessage's own doc comment on how that's treated.</summary>
    public ConcurrentDictionary<int, (double Lat, double Lon, double Alt)> FrequencyListenerPositions { get; } = new();

    // Latest SATCOM state this client reported (message-driven; consumed by SignalingServer's
    // independent SatcomTickLoopAsync, which runs on its own cadence rather than only reacting to
    // message arrival). Null/default NetId means "not currently using SATCOM".
    public string? SatcomNetId { get; set; }
    public bool SatcomLoginReady { get; set; }
    public bool SatcomDebugRequested { get; set; }
    public int SatcomPriority { get; set; }

    // Latest DCS presence this client reported (message-driven, low-rate, independent of SATCOM
    // state -- see DcsPresenceUpdateMessage). Used by SrsLosOracleService to pick a live DCS client
    // as a remote terrain-LOS oracle for SRS-bridged legs. Null DcsTheater/DcsPresenceUpdatedUtc
    // means "not currently a usable oracle" (never reported, or stale -- see IsDcsPresenceFresh).
    public double? DcsLatitudeDeg { get; set; }
    public double? DcsLongitudeDeg { get; set; }
    public double? DcsAltitudeMeters { get; set; }
    public string? DcsTheater { get; set; }
    public DateTime? DcsPresenceUpdatedUtc { get; set; }

    // Matches the ~1-2s push interval (OpenFreqService.SendDcsPresenceUpdatesAsync) with headroom
    // for one missed tick before treating the client as no longer a usable oracle.
    private static readonly TimeSpan DcsPresenceStaleAfter = TimeSpan.FromSeconds(5);

    public bool IsDcsPresenceFresh =>
        DcsPresenceUpdatedUtc is { } updatedUtc && DateTime.UtcNow - updatedUtc <= DcsPresenceStaleAfter;

    public enum FrequencyClientStatus
    {
        Transmitting, Receiving
    }

    public void UpdateActivity()
    {
        LastActivity = DateTime.UtcNow;
    }

    public void Dispose()
    {
        // Atomically sets the _disposed flag - if it was != 0 already, we have already cleaned up
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SendLock.Dispose();
        WebSocket.Dispose();
    }
}
