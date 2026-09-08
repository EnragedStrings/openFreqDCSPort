using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OpenFreqServer;

/// <summary>
/// Picks a currently-connected OpenFreq client to referee terrain LOS between two arbitrary
/// geodetic points -- the server itself has no terrain data of its own (see
/// SignalingServer.RequestRemoteLineOfSightAsync for the wire mechanics this drives). Used by two
/// independent callers: the SRS bridge (a leg involving an SRS-bridged peer) and native transcript-
/// delivery gating (SignalingServer's pending-transmission tracking, deciding whether a bot client
/// could plausibly have heard a given transmitter). Selection, in order:
///   1. The far end of the leg, if it's itself a connected OpenFreq client with fresh DCS presence
///      -- it already knows its own position, so this is the cheapest and most accurate choice.
///      (A bot client is never selected here -- it has no DCS presence of its own to be fresh.)
///   2. Whichever oracle already answered for this exact (source, receiver) pair last time, if
///      still connected and fresh -- avoids jittery results from different aircrafts' altitude/
///      precision assumptions each cycle.
///   3. Any other connected client with fresh DCS presence.
///   4. None available -- callers fall back to their own default (geometric-only signal model for
///      the SRS bridge; withholding delivery for transcript gating -- see
///      TranscriptDeliveryMessage's own doc comment).
///
/// Theater matching is deliberately not enforced here: SRS reports no theater at all (only lat/
/// lon/alt), so it can't be cross-checked for an all-SRS pair, and in practice one OpenFreq server
/// serves one DCS mission/map at a time, so any connected presence is already on the right map.
///
/// Never blocks a caller on a network round trip -- TryGetLineOfSight always returns immediately
/// from cache (or null/"unknown" on a cold pair) and kicks off a background refresh, matching how
/// DcsExportService's own local LOS cache behaves for the same reason (audio processing runs on a
/// tight per-buffer cadence that can't wait on a WebSocket+UDP round trip).
/// </summary>
public sealed class LosOracleService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ResultTtl = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private sealed class CacheEntry
    {
        public bool Visible;
        public DateTime UpdatedUtc;
        public DateTime RefreshStartedUtc;
    }

    private readonly SignalingServer _signalingServer;
    private readonly ConcurrentDictionary<string, ClientSession> _openFreqClients;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, string> _stickyOracleByPair = new();

    public LosOracleService(SignalingServer signalingServer,
        ConcurrentDictionary<string, ClientSession> openFreqClients, ILogger logger)
    {
        _signalingServer = signalingServer;
        _openFreqClients = openFreqClients;
        _logger = logger;
    }

    /// <summary>Returns the last known LOS result for this pair (true = clear, false = blocked),
    /// or null if unknown (no oracle has answered yet, or none is currently available) -- callers
    /// should treat null as "don't apply any LOS-based degradation," not as blocked. pairKey should
    /// be stable per (source, receiver) leg, e.g. "{sourcePeerId}:{receiverClientId-or-srsGuid}".
    /// otherEndClientId is the far end's OpenFreq client id if known (case 1 above), or null/empty
    /// if the far end isn't a connected OpenFreq client (e.g. it's another SRS player, or a bot).</summary>
    public bool? TryGetLineOfSight(string pairKey, string? otherEndClientId,
        double fromLat, double fromLon, double fromAlt, double toLat, double toLon, double toAlt)
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(pairKey, out var entry) && now - entry.UpdatedUtc <= ResultTtl)
        {
            if (now - entry.RefreshStartedUtc >= RefreshInterval)
            {
                entry.RefreshStartedUtc = now;
                _ = RefreshAsync(pairKey, otherEndClientId, fromLat, fromLon, fromAlt, toLat, toLon, toAlt);
            }

            return entry.Visible;
        }

        _ = RefreshAsync(pairKey, otherEndClientId, fromLat, fromLon, fromAlt, toLat, toLon, toAlt);
        return null;
    }

    private async Task RefreshAsync(string pairKey, string? otherEndClientId,
        double fromLat, double fromLon, double fromAlt, double toLat, double toLon, double toAlt)
    {
        try
        {
            var oracle = SelectOracle(pairKey, otherEndClientId);
            if (oracle == null) return;

            var response = await _signalingServer.RequestRemoteLineOfSightAsync(oracle,
                fromLat, fromLon, fromAlt, toLat, toLon, toAlt, RequestTimeout);
            if (response is not { TerrainAvailable: true }) return;

            _cache[pairKey] = new CacheEntry
            {
                Visible = response.Visible,
                UpdatedUtc = DateTime.UtcNow,
                RefreshStartedUtc = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LOS oracle refresh failed for {PairKey}", pairKey);
        }
    }

    private ClientSession? SelectOracle(string pairKey, string? otherEndClientId)
    {
        if (!string.IsNullOrEmpty(otherEndClientId) &&
            _openFreqClients.TryGetValue(otherEndClientId, out var direct) && direct.IsDcsPresenceFresh)
        {
            return direct;
        }

        if (_stickyOracleByPair.TryGetValue(pairKey, out var stickyId) &&
            _openFreqClients.TryGetValue(stickyId, out var sticky) && sticky.IsDcsPresenceFresh)
        {
            return sticky;
        }

        var candidate = _openFreqClients.Values.FirstOrDefault(c => c.IsDcsPresenceFresh);
        if (candidate != null)
            _stickyOracleByPair[pairKey] = candidate.Id;

        return candidate;
    }
}
