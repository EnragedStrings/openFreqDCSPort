using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Common.Satcom;

namespace OpenFreqServer.Satcom;

/// <summary>
/// Single entry point SignalingServer uses for everything SATCOM: owns ephemeris, satellite
/// selection, the link engine, and the DAMA network controller, and turns their output into the
/// wire DTOs. Deliberately kept out of SignalingServer.cs itself (which stays the generic
/// signaling/relay server) -- SATCOM plugs into the existing message-dispatch/broadcast plumbing
/// through this one seam rather than being scattered through it.
/// </summary>
public sealed class SatcomServerCoordinator : IDisposable
{
    private readonly SatcomServerConfig _config;
    private readonly Dictionary<string, SatcomNetDefinition> _netsById;
    private readonly SatcomEphemerisService _ephemeris;
    private readonly SatcomSatelliteSelector _selector;
    private readonly SatcomLinkEngine _linkEngine;
    private readonly DamaNetworkController _dama;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<(string ClientId, string NetId), string> _channelKeys = new();
    private readonly ConcurrentDictionary<(string ClientId, string NetId), SatcomFrameDispositionModel> _dispositionModels = new();
    private int _dispositionSeedCounter;

    // Change-tracked diagnostic logging: fires on real transitions, not every ~500ms tick.
    private readonly ConcurrentDictionary<(string ClientId, string NetId), bool> _seenFirstGeometryUpdate = new();
    private readonly ConcurrentDictionary<(string ClientId, string NetId), string> _lastLoggedEvalSummary = new();

    public SatcomServerCoordinator(SatcomServerConfig config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _netsById = config.Nets.ToDictionary(n => n.NetId);
        _logger = loggerFactory.CreateLogger("Satcom");
        _ephemeris = new SatcomEphemerisService(config.Satellites, config, _logger);
        _selector = new SatcomSatelliteSelector();
        _linkEngine = new SatcomLinkEngine(config.Satellites, _ephemeris, _selector);
        _dama = new DamaNetworkController();
    }

    public void Start() => _ephemeris.Start();

    public SatcomNetDefinition? GetNet(string netId) => _netsById.GetValueOrDefault(netId);

    public IReadOnlyCollection<(string ClientId, string NetId)> ActiveSessions => _channelKeys.Keys.ToArray();

    public void HandleGeometryUpdate(string clientId, SatcomGeometryUpdateMessage msg, SatcomNetDefinition net, long nowMs)
    {
        var terminal = new SatcomTerminalState(msg.LatitudeDeg, msg.LongitudeDeg, msg.AltitudeMeters,
            msg.HeadingRad, msg.PitchRad, msg.BankRad, AttitudeIsApproximate: false,
            TunedFrequencyHz: msg.TunedFrequencyHz);

        _linkEngine.UpdateGeometry(clientId, net.NetId, terminal, msg.RadioPowered, msg.PttPressed,
            msg.TerrainLosClear, nowMs);
        _channelKeys[(clientId, net.NetId)] = msg.ChannelKey;

        var key = (clientId, net.NetId);
        if (_seenFirstGeometryUpdate.TryAdd(key, true))
        {
            _logger.LogInformation(
                "SATCOM geometry update received from {ClientId} for net {NetId} (channel {ChannelKey}): " +
                "lat={Lat:F4} lon={Lon:F4} alt={Alt:F0} powered={Powered} loginReady={LoginReady}",
                clientId, net.NetId, msg.ChannelKey, msg.LatitudeDeg, msg.LongitudeDeg, msg.AltitudeMeters,
                msg.RadioPowered, msg.LoginReady);
        }
    }

    /// <summary>Full authoritative per-tick evaluation for one (client, net) session: link
    /// quality, DAMA state, and a short lookahead batch of already-decided frame dispositions with
    /// scheduled arrival timestamps.</summary>
    public SatcomLinkStateMessage Evaluate(string clientId, string netId, bool loginReady, bool debugRequested,
        int priority, long nowMs)
    {
        var net = _netsById[netId];
        var link = _linkEngine.Evaluate(clientId, netId, net, nowMs);

        var txClientId = _linkEngine.FindActiveTransmitter(netId, nowMs);
        var pttPressed = txClientId == clientId;
        var receivingCarrier = txClientId != null && txClientId != clientId;

        var damaState = _dama.Update(clientId, net, loginReady, link, pttPressed, receivingCarrier, priority, nowMs);

        var key = (clientId, netId);
        var debugAuthorized = debugRequested && _config.DebugTelemetryEnabled;

        var evalSummary = $"available={link.LinkAvailable} satellite={(string.IsNullOrEmpty(link.SatelliteId) ? "(none)" : link.SatelliteId)} " +
                          $"failureReason={link.FailureReason} quality={link.QualityState} dama={damaState} " +
                          $"ebN0={link.EbN0Db:F1}dB";
        if (!_lastLoggedEvalSummary.TryGetValue(key, out var lastEvalSummary) || lastEvalSummary != evalSummary)
        {
            _logger.LogInformation("SATCOM link eval for {ClientId}/{NetId}: {Summary}", clientId, netId, evalSummary);
            _lastLoggedEvalSummary[key] = evalSummary;
        }

        var message = new SatcomLinkStateMessage
        {
            ChannelKey = _channelKeys.GetValueOrDefault(key, ""),
            Available = link.LinkAvailable,
            FailureReason = link.FailureReason.ToString(),
            SatelliteId = link.SatelliteId,
            SatelliteName = link.SatelliteName,
            QualityState = link.QualityState.ToString(),
            DamaState = damaState.ToString(),
            DamaFrameIndex = _dama.CurrentFrameIndex(net, nowMs),
            DamaSlot = _dama.CurrentSlot(clientId, netId),
            PropagationLatencySeconds = link.PropagationLatencySeconds,
            FrameErrorRate = link.FrameErrorRate,
            DebugAuthorized = debugAuthorized
        };

        if (debugAuthorized)
        {
            message.CombinedCn0DbHz = link.CombinedCn0DbHz;
            message.EbN0Db = link.EbN0Db;
            message.RawBer = link.RawBer;
            message.PostFecBer = link.PostFecBer;
            message.UplinkElevationDeg = link.Uplink.ElevationDeg;
            message.UplinkAzimuthDeg = link.Uplink.AzimuthDeg;
            message.UplinkRangeMeters = link.Uplink.SlantRangeMeters;
            message.DownlinkElevationDeg = link.Downlink.ElevationDeg;
            message.DownlinkAzimuthDeg = link.Downlink.AzimuthDeg;
            message.DownlinkRangeMeters = link.Downlink.SlantRangeMeters;
        }

        if (link.LinkAvailable)
            message.FrameDispositions = BuildDispositionBatch(key, net, link, nowMs);

        return message;
    }

    private List<SatcomFrameDispositionDto> BuildDispositionBatch(
        (string ClientId, string NetId) key, SatcomNetDefinition net, SatcomLinkResult link, long nowMs)
    {
        const int lookaheadFrames = 8;

        var model = _dispositionModels.GetOrAdd(key, _ =>
            new SatcomFrameDispositionModel(Interlocked.Increment(ref _dispositionSeedCounter) ^ key.GetHashCode()));

        var rawFer = SatcomLinkBudget.FrameErrorRate(link.RawBer, net.FrameBits);
        // How far below the tracking threshold this link sits -- used the same way the earlier
        // per-client model derived burst severity from margin, just from the server's real Eb/N0 now.
        var burstSeverity = Math.Clamp(1.0 - (link.EbN0Db - net.TrackingEbN0ThresholdDb) / 10.0, 0.0, 1.0);

        var frameDurationMs = Math.Max(5.0, net.FrameDurationSeconds * 1000.0);
        var baseUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var propagationMs = link.PropagationLatencySeconds * 1000.0;

        var batch = new List<SatcomFrameDispositionDto>(lookaheadFrames);
        for (var i = 0; i < lookaheadFrames; i++)
        {
            var disposition = model.NextDisposition(rawFer, link.FrameErrorRate, burstSeverity);
            batch.Add(new SatcomFrameDispositionDto
            {
                ScheduledArrivalUnixMs = baseUnixMs + (long)(i * frameDurationMs) + (long)propagationMs,
                Disposition = disposition.ToString()
            });
        }
        return batch;
    }

    public List<SatcomSatelliteInfoDto> GetSatelliteInfoDtos() =>
        _config.Satellites.Select(sat =>
        {
            var pos = _ephemeris.GetPosition(sat.Id);
            return new SatcomSatelliteInfoDto
            {
                Id = sat.Id,
                DisplayName = sat.DisplayName,
                LatitudeDeg = pos?.LatitudeDeg ?? 0.0,
                LongitudeDeg = pos?.LongitudeDeg ?? sat.StaticLongitudeDeg,
                AltitudeMeters = pos?.AltitudeMeters ?? sat.StaticAltitudeMeters,
                IsStale = pos?.IsStale ?? true
            };
        }).ToList();

    public void RemoveClient(string clientId)
    {
        foreach (var key in _channelKeys.Keys.Where(k => k.ClientId == clientId).ToArray())
        {
            _channelKeys.TryRemove(key, out _);
            _dispositionModels.TryRemove(key, out _);
            _seenFirstGeometryUpdate.TryRemove(key, out _);
            _lastLoggedEvalSummary.TryRemove(key, out _);
            _linkEngine.RemoveClient(key.ClientId, key.NetId);
            _dama.RemoveClient(key.ClientId, key.NetId);
        }
    }

    public void Dispose() => _ephemeris.Dispose();
}
