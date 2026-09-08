using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;

namespace OpenFreqServer;

/// <summary>
/// Tracks every in-flight transmission that carries a TransmissionId (see
/// AudioTransmissionMessage's own doc comment) and, once its transcript arrives, decides who gets
/// to see it and in what form -- see TranscriptDeliveryMessage's own doc comment for the full
/// capability/LOS/stepping contract this implements. The transmitting client is never involved in
/// or aware of any of this: it just reports what it heard itself say, once, with word-level timing.
/// </summary>
public sealed class TranscriptDeliveryService
{
    // Long enough to comfortably outlast local speech-to-text processing (typically low single-
    // digit seconds) plus network jitter, short enough that a transmission whose transcript never
    // arrives (transcripts disabled, client crashed mid-transcribe, etc) doesn't linger forever.
    private static readonly TimeSpan PendingTransmissionTtl = TimeSpan.FromSeconds(60);

    // internal (not private) so OpenFreq.Server.Tests can construct one directly to exercise
    // OverlapsInTime with exact synthetic timestamps -- see that method's own comment.
    internal sealed class PendingTransmission
    {
        public required string PeerId { get; init; }
        public required int FrequencyKhz { get; init; }
        public double? Lat { get; init; }
        public double? Lon { get; init; }
        public double? Alt { get; init; }
        public DateTime StartedAtUtc { get; init; }
        public DateTime? EndedAtUtc { get; set; }
    }

    private readonly SignalingServer _signalingServer;
    private readonly ConcurrentDictionary<string, ClientSession> _clients;
    private readonly FrequencyChannelManager _channelManager;
    private readonly LosOracleService _losOracle;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, PendingTransmission> _pending = new();

    public TranscriptDeliveryService(SignalingServer signalingServer, ConcurrentDictionary<string, ClientSession> clients,
        FrequencyChannelManager channelManager, LosOracleService losOracle, ILogger logger)
    {
        _signalingServer = signalingServer;
        _clients = clients;
        _channelManager = channelManager;
        _losOracle = losOracle;
        _logger = logger;
    }

    /// <summary>Call when a transmitting:true AudioTransmissionMessage carries a TransmissionId --
    /// idempotent, since the 333ms PTT heartbeat resends the same id repeatedly while a key stays
    /// down; only the first call for a given id actually records a start time.</summary>
    public void RecordTransmissionStart(string transmissionId, string peerId, int frequencyKhz,
        double? lat, double? lon, double? alt)
    {
        _pending.TryAdd(transmissionId, new PendingTransmission
        {
            PeerId = peerId,
            FrequencyKhz = frequencyKhz,
            Lat = lat,
            Lon = lon,
            Alt = alt,
            StartedAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>Call when a transmitting:false AudioTransmissionMessage carries a TransmissionId --
    /// fixes the transmission's end time so later overlap-window math against other transmissions
    /// is exact instead of an open-ended "still going" guess. A no-op if the id was never started
    /// (or already expired) -- nothing to end.</summary>
    public void RecordTransmissionEnd(string transmissionId)
    {
        if (_pending.TryGetValue(transmissionId, out var record))
            record.EndedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Sweeps out transmissions that ended long enough ago that no transcript is still
    /// plausibly in flight for them. Called periodically from SignalingServer's existing idle
    /// watchdog loop, not on its own timer. Deliberately never prunes a transmission that hasn't
    /// ended yet, no matter how old -- an unusually long transmission is still legitimately "in
    /// progress," not stale.</summary>
    public void PruneExpired()
    {
        var cutoff = DateTime.UtcNow - PendingTransmissionTtl;
        foreach (var (id, record) in _pending)
        {
            if (record.EndedAtUtc is { } endedAtUtc && endedAtUtc < cutoff)
                _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Delivers (a possibly per-listener-trimmed, possibly withheld) TranscriptDeliveryMessage
    /// to every eligible bot listener on the transmission's frequency. See the class/message doc
    /// comments for the full contract -- this is the entire decision in one place.</summary>
    public async Task HandleTranscriptAsync(ClientSession fromSession, TransmissionTranscriptMessage payload)
    {
        if (!_pending.TryGetValue(payload.TransmissionId, out var record) || record.PeerId != fromSession.Id)
        {
            _logger.LogDebug(
                "Dropping transcript for unknown/expired/mismatched transmission {TransmissionId} from {PeerId}",
                payload.TransmissionId, fromSession.Id);
            return;
        }

        // Other transmissions on the same frequency whose active window overlapped this one at
        // all -- candidates for a "stepped" redaction, subject to each individual listener's own
        // audibility to them (checked per-listener below, not here).
        var overlappingOthers = _pending.Values
            .Where(other => !ReferenceEquals(other, record) &&
                             other.FrequencyKhz == record.FrequencyKhz &&
                             OverlapsInTime(record, other))
            .ToList();

        var deliveries = _channelManager.GetClientsInChannel(record.FrequencyKhz)
            .Where(listenerId => listenerId != record.PeerId)
            .Select(listenerId => DeliverToOneListenerAsync(listenerId, record, payload, overlappingOthers));

        await Task.WhenAll(deliveries);
    }

    private async Task DeliverToOneListenerAsync(string listenerId, PendingTransmission record,
        TransmissionTranscriptMessage payload, List<PendingTransmission> overlappingOthers)
    {
        if (!_clients.TryGetValue(listenerId, out var listener) || !listener.WantsTranscripts)
            return;

        // No declared listening position for this frequency at all -- there's nothing to gate
        // against, so this listener simply gets the full, untrimmed transcript. See
        // TranscriptDeliveryMessage's own doc comment: "declare a position, or you get everything."
        if (!listener.FrequencyListenerPositions.TryGetValue(record.FrequencyKhz, out var listenerPos))
        {
            await DeliverAsync(listener, record, payload, dropWindows: null);
            return;
        }

        if (!CanHear(record, listenerId, listenerPos))
            return; // confirmed blocked, or LOS unresolved with both positions known -- fail closed

        List<(double StartSec, double EndSec)>? dropWindows = null;
        foreach (var other in overlappingOthers)
        {
            if (!CanHear(other, listenerId, listenerPos))
                continue; // this listener can't (confirm they can) hear the other transmitter -- no step for them

            var overlapStart = record.StartedAtUtc > other.StartedAtUtc ? record.StartedAtUtc : other.StartedAtUtc;
            var recordEnd = record.EndedAtUtc ?? DateTime.UtcNow;
            var otherEnd = other.EndedAtUtc ?? DateTime.UtcNow;
            var overlapEnd = recordEnd < otherEnd ? recordEnd : otherEnd;
            if (overlapEnd <= overlapStart) continue;

            dropWindows ??= [];
            dropWindows.Add(((overlapStart - record.StartedAtUtc).TotalSeconds,
                (overlapEnd - record.StartedAtUtc).TotalSeconds));
        }

        await DeliverAsync(listener, record, payload, dropWindows);
    }

    /// <summary>Can this listener plausibly hear the given transmission? True whenever gating can't
    /// be evaluated at all (transmitter reported no position -- permissive default, matching "you
    /// get everything" for an ungateable leg). When the transmitter's position IS known, defers to
    /// the shared LosOracleService -- explicit true only on a confirmed clear line of sight;
    /// confirmed-blocked and "not yet resolved" both come back false (fail-closed), per the chosen
    /// terrain-LOS-fidelity tradeoff over a faster geometric approximation.</summary>
    private bool CanHear(PendingTransmission tx, string listenerId, (double Lat, double Lon, double Alt) listenerPos)
    {
        if (tx.Lat is not { } txLat || tx.Lon is not { } txLon || tx.Alt is not { } txAlt)
            return true;

        var visible = _losOracle.TryGetLineOfSight($"{tx.PeerId}:{listenerId}", tx.PeerId,
            txLat, txLon, txAlt, listenerPos.Lat, listenerPos.Lon, listenerPos.Alt);

        return visible ?? false;
    }

    // internal (not private) so OpenFreq.Server.Tests can exercise the pure interval/redaction
    // math with exact synthetic timestamps -- avoids flaky real-wall-clock-timing unit tests for
    // logic that has nothing to do with actual timing precision.
    internal static bool OverlapsInTime(PendingTransmission a, PendingTransmission b)
    {
        var aEnd = a.EndedAtUtc ?? DateTime.UtcNow;
        var bEnd = b.EndedAtUtc ?? DateTime.UtcNow;
        return a.StartedAtUtc < bEnd && b.StartedAtUtc < aEnd;
    }

    /// <summary>Concatenates surviving words' text (each word carries its own leading whitespace,
    /// the same convention whisper.cpp itself uses when reconstructing sentence text from tokens --
    /// see TranscriptWordDto's own doc comment) after dropping any word whose span overlaps any
    /// drop window at all, even partially.</summary>
    internal static string BuildText(List<TranscriptWordDto> words, List<(double StartSec, double EndSec)>? dropWindows)
    {
        if (dropWindows is not { Count: > 0 })
            return string.Concat(words.Select(w => w.Text)).Trim();

        var kept = words.Where(w => !dropWindows.Any(d => w.StartSec < d.EndSec && w.EndSec > d.StartSec));
        return string.Concat(kept.Select(w => w.Text)).Trim();
    }

    private async Task DeliverAsync(ClientSession listener, PendingTransmission record,
        TransmissionTranscriptMessage payload, List<(double StartSec, double EndSec)>? dropWindows)
    {
        var text = BuildText(payload.Words, dropWindows);
        if (string.IsNullOrWhiteSpace(text))
            return; // nothing intelligible survived the redaction -- same as fully LOS-blocked

        if (!_clients.TryGetValue(record.PeerId, out var fromSession))
            return; // transmitter disconnected before delivery could happen

        await _signalingServer.SendTranscriptDeliveryAsync(listener, new TranscriptDeliveryMessage
        {
            TransmissionId = payload.TransmissionId,
            FromPeerId = record.PeerId,
            FromDisplayName = fromSession.DisplayName ?? "Unnamed",
            FrequencyKhz = record.FrequencyKhz,
            Text = text,
            Language = payload.Language,
            FromLatitudeDeg = record.Lat,
            FromLongitudeDeg = record.Lon,
            FromAltitudeMeters = record.Alt
        });
    }
}
