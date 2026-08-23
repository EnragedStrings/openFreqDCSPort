using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FalconBmsDataService.Models;
using FalconRadioService.Models;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Models.Dcs;
using OpenFreq.Client.Services.Interfaces;
using OpenFreqClient.Json;

namespace OpenFreq.Client.Services;

public sealed class DcsExportService(ILogger<DcsExportService> logger) : IDcsExportService
{
    private static readonly TimeSpan LosRequestInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LosResultTtl = TimeSpan.FromSeconds(2);
    private readonly object _stateLock = new();
    private readonly object _udpSendLock = new();
    private readonly ConcurrentDictionary<int, DcsRadioState> _radios = new();
    private readonly ConcurrentDictionary<string, DcsLineOfSightResult> _losResults = new();
    private readonly ConcurrentDictionary<string, DateTime> _losLastRequestUtc = new();

    // Pending remote (two-arbitrary-point) LOS requests awaiting this client's own DCS instance's
    // reply -- see RequestRemoteLineOfSightAsync/DcsLosRemoteRequestPacket. Unlike the self-
    // anchored _losResults cache above (polled, fire-and-forget), this answers one specific
    // server-initiated query, so it's a proper await rather than a cache lookup.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DcsLosRemoteResponsePacket>>
        _pendingRemoteLosRequests = new();
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _watchdogTask;
    private UdpClient? _udpClient;
    private ServiceState _state = ServiceState.Stopped;
    private DcsVector3? _position;
    private DcsVector3? _velocity;
    private double? _headingRadians;
    private double? _pitchRadians;
    private double? _bankRadians;
    private double _latitude;
    private double _longitude;
    private double _altitudeMsl;
    private DcsHeightmapInfo? _heightmapInfo;
    private string _theater = string.Empty;
    private string _unit = string.Empty;
    private string _unitName = string.Empty;
    private string _playerName = string.Empty;
    private bool _isInGame;

    public ServiceState State
    {
        get
        {
            lock (_stateLock) return _state;
        }
    }

    public int UdpPort { get; set; } = IDcsExportService.DefaultUdpPort;
    public int LosRequestPort { get; set; } = IDcsExportService.DefaultLosRequestPort;

    public DateTime? LastPacketUtc { get; private set; }

    public string Theater
    {
        get
        {
            lock (_stateLock) return _theater;
        }
    }

    public string Unit
    {
        get
        {
            lock (_stateLock) return _unit;
        }
    }

    public string UnitName
    {
        get
        {
            lock (_stateLock) return _unitName;
        }
    }

    public string PlayerName
    {
        get
        {
            lock (_stateLock) return _playerName;
        }
    }

    public bool IsInGame
    {
        get
        {
            lock (_stateLock) return _isInGame;
        }
    }

    public DcsVector3? Position
    {
        get
        {
            lock (_stateLock) return _position;
        }
    }

    public DcsVector3? Velocity
    {
        get
        {
            lock (_stateLock) return _velocity;
        }
    }

    public double? HeadingRadians
    {
        get
        {
            lock (_stateLock) return _headingRadians;
        }
    }

    public double? PitchRadians
    {
        get
        {
            lock (_stateLock) return _pitchRadians;
        }
    }

    public double? BankRadians
    {
        get
        {
            lock (_stateLock) return _bankRadians;
        }
    }

    public double Latitude
    {
        get
        {
            lock (_stateLock) return _latitude;
        }
    }

    public double Longitude
    {
        get
        {
            lock (_stateLock) return _longitude;
        }
    }

    public double AltitudeMsl
    {
        get
        {
            lock (_stateLock) return _altitudeMsl;
        }
    }

    public DcsHeightmapInfo? HeightmapInfo
    {
        get
        {
            lock (_stateLock) return _heightmapInfo?.Clone();
        }
    }

    public event EventHandler<ServiceStateChangedEventArgs>? StateChanged;
    public event EventHandler<DcsRadioChangedEventArgs>? RadioChanged;
    public event EventHandler<DcsPttChangedEventArgs>? PttChanged;
    public event EventHandler<DcsToneChangedEventArgs>? ToneChanged;
    public event EventHandler<DcsGameModeChangedEventArgs>? GameModeChanged;
    public event EventHandler<DcsAircraftChangedEventArgs>? AircraftChanged;
    public event EventHandler<DcsHeightmapChangedEventArgs>? HeightmapChanged;

    public DcsRadioState? GetRadio(int slot)
    {
        return _radios.TryGetValue(slot, out var radio) ? radio.Clone() : null;
    }

    public IReadOnlyList<DcsRadioState> GetRadios()
    {
        return _radios.Values
            .OrderBy(r => r.Slot)
            .Select(r => r.Clone())
            .ToList();
    }

    public DcsLineOfSightResult? RequestLineOfSight(string key, DcsVector3 remotePosition)
    {
        if (string.IsNullOrWhiteSpace(key) || remotePosition == null || State != ServiceState.Connected)
            return null;

        var now = DateTime.UtcNow;
        if (!_losLastRequestUtc.TryGetValue(key, out var lastRequest) ||
            now - lastRequest >= LosRequestInterval)
        {
            _losLastRequestUtc[key] = now;
            SendLosRequest(key, remotePosition);
        }

        if (!_losResults.TryGetValue(key, out var result))
            return null;

        return now - result.ReceivedUtc <= LosResultTtl ? result : null;
    }

    /// <summary>Asks this client's own live DCS instance to check terrain LOS between two
    /// arbitrary geodetic points -- neither has to be this aircraft's own position, unlike <see
    /// cref="RequestLineOfSight"/>. Answers a server-initiated remote-oracle query (see
    /// OpenFreqService's DcsLosOracleRequestReceived handler); returns null if this client has no
    /// DCS connection, the request can't be sent, or no reply arrives within <paramref
    /// name="timeout"/>.</summary>
    public async Task<DcsLosRemoteResponsePacket?> RequestRemoteLineOfSightAsync(double fromLat, double fromLon,
        double fromAlt, double toLat, double toLon, double toAlt, TimeSpan timeout)
    {
        var udpClient = _udpClient;
        if (udpClient == null || State != ServiceState.Connected)
            return null;

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<DcsLosRemoteResponsePacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRemoteLosRequests.TryAdd(requestId, tcs))
            return null;

        try
        {
            var request = new DcsLosRemoteRequestPacket
            {
                RequestId = requestId,
                ClientPort = UdpPort,
                From = new DcsGeoPoint { Lat = fromLat, Lon = fromLon, Alt = fromAlt },
                To = new DcsGeoPoint { Lat = toLat, Lon = toLon, Alt = toAlt }
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, ClientJsonContext.Default.DcsLosRemoteRequestPacket);
            lock (_udpSendLock)
            {
                udpClient.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, LosRequestPort));
            }

            using var cts = new CancellationTokenSource(timeout);
            await using var registration = cts.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            _pendingRemoteLosRequests.TryRemove(requestId, out _);
        }
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_state != ServiceState.Stopped) return;
            ChangeState(ServiceState.Disconnected);
        }

        _cts = new CancellationTokenSource();
        try
        {
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, UdpPort));
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _watchdogTask = Task.Run(() => WatchdogLoopAsync(_cts.Token));
            logger.LogInformation("DCS export listener started on UDP {Port}", UdpPort);
        }
        catch (Exception ex)
        {
            _cts.Dispose();
            _cts = null;
            _udpClient?.Dispose();
            _udpClient = null;
            lock (_stateLock)
            {
                ChangeState(ServiceState.Stopped);
            }
            logger.LogError(ex, "Failed to start DCS export listener on UDP {Port}", UdpPort);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udpClient?.Close();

        try
        {
            _receiveTask?.Wait(TimeSpan.FromSeconds(2));
            _watchdogTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // shutdown path
        }

        _udpClient?.Dispose();
        _udpClient = null;
        _cts?.Dispose();
        _cts = null;
        _radios.Clear();
        _losResults.Clear();
        _losLastRequestUtc.Clear();

        foreach (var pending in _pendingRemoteLosRequests)
            if (_pendingRemoteLosRequests.TryRemove(pending.Key, out var tcs))
                tcs.TrySetCanceled();

        lock (_stateLock)
        {
            _position = null;
            _velocity = null;
            _headingRadians = null;
            _pitchRadians = null;
            _bankRadians = null;
            _latitude = 0;
            _longitude = 0;
            _altitudeMsl = 0;
            _heightmapInfo = null;
            _theater = string.Empty;
            _unit = string.Empty;
            _unitName = string.Empty;
            _playerName = string.Empty;
            _isInGame = false;
            LastPacketUtc = null;
            ChangeState(ServiceState.Stopped);
        }

        logger.LogInformation("DCS export listener stopped");
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(cancellationToken);
                ProcessUdpPacket(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process DCS export packet");
            }
        }
    }

    private void ProcessUdpPacket(byte[] buffer)
    {
        using var document = JsonDocument.Parse(buffer);
        if (!document.RootElement.TryGetProperty("schema", out var schemaElement))
            return;

        var schema = schemaElement.GetString();
        if (string.Equals(schema, "openfreq.dcs.export", StringComparison.OrdinalIgnoreCase))
        {
            var packet = JsonSerializer.Deserialize(buffer, ClientJsonContext.Default.DcsExportPacket);
            if (packet != null)
                ApplyPacket(packet);
        }
        else if (string.Equals(schema, "openfreq.dcs.los.response", StringComparison.OrdinalIgnoreCase))
        {
            var packet = JsonSerializer.Deserialize(buffer, ClientJsonContext.Default.DcsLosResponsePacket);
            if (packet != null)
                ApplyLosResponse(packet);
        }
        else if (string.Equals(schema, "openfreq.dcs.los.remote_response", StringComparison.OrdinalIgnoreCase))
        {
            var packet = JsonSerializer.Deserialize(buffer, ClientJsonContext.Default.DcsLosRemoteResponsePacket);
            if (packet != null && _pendingRemoteLosRequests.TryRemove(packet.RequestId, out var tcs))
                tcs.TrySetResult(packet);
        }
    }

    private void SendLosRequest(string key, DcsVector3 remotePosition)
    {
        var udpClient = _udpClient;
        if (udpClient == null)
            return;

        var request = new DcsLosRequestPacket
        {
            RequestId = $"{DateTime.UtcNow.Ticks}:{key}",
            ClientPort = UdpPort,
            Checks =
            [
                new DcsLosCheck
                {
                    Id = key,
                    Position = new DcsVector3(remotePosition.X, remotePosition.Y, remotePosition.Z)
                }
            ]
        };

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, ClientJsonContext.Default.DcsLosRequestPacket);
            lock (_udpSendLock)
            {
                udpClient.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, LosRequestPort));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to send DCS LOS request for {Key}", key);
        }
    }

    private void ApplyLosResponse(DcsLosResponsePacket packet)
    {
        var now = DateTime.UtcNow;
        foreach (var result in packet.Results)
        {
            if (string.IsNullOrWhiteSpace(result.Id))
                continue;

            _losResults[result.Id] = new DcsLineOfSightResult
            {
                Loss = Math.Clamp(result.Loss, 0.0d, 1.0d),
                Visible = result.Visible,
                TerrainAvailable = packet.TerrainAvailable,
                ReceivedUtc = now
            };
        }
    }

    private async Task WatchdogLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var last = LastPacketUtc;
            if (last == null || DateTime.UtcNow - last.Value <= TimeSpan.FromSeconds(3)) continue;

            List<DcsRadioState> expiredRadios;
            lock (_stateLock)
            {
                if (_state == ServiceState.Connected)
                    ChangeState(ServiceState.Disconnected);
                _isInGame = false;
                _position = null;
                _velocity = null;
                _headingRadians = null;
            _pitchRadians = null;
            _bankRadians = null;
            _latitude = 0;
            _longitude = 0;
            _altitudeMsl = 0;
                expiredRadios = _radios.Values.Select(r => r.Clone()).ToList();
                _radios.Clear();
            }

            foreach (var oldRadio in expiredRadios)
            {
                var offRadio = oldRadio.Clone();
                offRadio.FrequencyHz = 0;
                offRadio.SecondaryFrequencyHz = 0;
                offRadio.IsOn = false;
                offRadio.Ptt = false;
                offRadio.ToneOn = false;

                RadioChanged?.Invoke(this, new DcsRadioChangedEventArgs(oldRadio.Clone(), offRadio.Clone()));
                if (oldRadio.Ptt)
                    PttChanged?.Invoke(this, new DcsPttChangedEventArgs(offRadio.Clone(), true, false));
                if (oldRadio.ToneOn)
                    ToneChanged?.Invoke(this, new DcsToneChangedEventArgs(offRadio.Clone(), true, false));
            }

            _losResults.Clear();
            _losLastRequestUtc.Clear();
        }
    }

    private void ApplyPacket(DcsExportPacket packet)
    {
        if (!string.Equals(packet.Schema, "openfreq.dcs.export", StringComparison.OrdinalIgnoreCase))
            return;

        LastPacketUtc = DateTime.UtcNow;

        string oldUnit;
        string oldUnitName;
        bool oldIsInGame;
        DcsHeightmapInfo? heightmapForEvent = null;

        lock (_stateLock)
        {
            oldUnit = _unit;
            oldUnitName = _unitName;
            oldIsInGame = _isInGame;

            _theater = packet.Theater;
            _unit = packet.Unit;
            _unitName = packet.UnitName;
            _playerName = packet.PlayerName;
            _isInGame = packet.IsInAircraft && packet.IsInGame;
            _position = packet.Position;
            _velocity = packet.Velocity;
            _headingRadians = packet.HeadingRadians;
            _pitchRadians = packet.PitchRadians;
            _bankRadians = packet.BankRadians;
            _latitude = packet.Latitude;
            _longitude = packet.Longitude;
            _altitudeMsl = packet.AltitudeMsl;

            if (packet.Heightmap is { Ready: true } hm &&
                (_heightmapInfo == null ||
                 !string.Equals(_heightmapInfo.RawPath, hm.RawPath, StringComparison.OrdinalIgnoreCase) ||
                 _heightmapInfo.SamplesX != hm.SamplesX ||
                 _heightmapInfo.SamplesZ != hm.SamplesZ))
            {
                _heightmapInfo = hm.Clone();
                heightmapForEvent = _heightmapInfo.Clone();
            }

            if (_state != ServiceState.Connected)
                ChangeState(ServiceState.Connected);
        }

        var seenRadios = new HashSet<int>();
        foreach (var newRadio in packet.Radios)
        {
            var normalized = NormalizeRadio(newRadio);
            seenRadios.Add(normalized.Slot);
            _radios.TryGetValue(normalized.Slot, out var oldRadio);
            _radios[normalized.Slot] = normalized.Clone();

            if (oldRadio == null || HasRadioChanged(oldRadio, normalized))
                RadioChanged?.Invoke(this, new DcsRadioChangedEventArgs(oldRadio?.Clone(), normalized.Clone()));

            if (oldRadio != null && oldRadio.Ptt != normalized.Ptt)
                PttChanged?.Invoke(this, new DcsPttChangedEventArgs(normalized.Clone(), oldRadio.Ptt, normalized.Ptt));
            else if (oldRadio == null && normalized.Ptt)
                PttChanged?.Invoke(this, new DcsPttChangedEventArgs(normalized.Clone(), false, true));

            if (oldRadio != null && oldRadio.ToneOn != normalized.ToneOn)
                ToneChanged?.Invoke(this, new DcsToneChangedEventArgs(normalized.Clone(), oldRadio.ToneOn, normalized.ToneOn));
            else if (oldRadio == null && normalized.ToneOn)
                ToneChanged?.Invoke(this, new DcsToneChangedEventArgs(normalized.Clone(), false, true));
        }

        // DCS OBSERVED BEHAVIOR: while the sim is paused (pause menu open), the export script
        // can't read cockpit arguments and reports an empty radios list -- NOT "every radio just
        // turned off". Only run the stale-radio-off cleanup below when this packet actually
        // reported at least one radio; an empty list just means "no fresh data this frame", so
        // radios are left exactly at their last-known state and pick back up the moment real data
        // resumes. Genuinely leaving the aircraft/game is already tracked separately via
        // GameModeChanged (IsInAircraft/IsInGame below), not by this per-radio cleanup.
        if (packet.Radios.Count > 0)
        {
            foreach (var staleSlot in _radios.Keys.Where(slot => !seenRadios.Contains(slot)).ToList())
            {
                if (!_radios.TryRemove(staleSlot, out var oldRadio)) continue;

                var offRadio = oldRadio.Clone();
                offRadio.FrequencyHz = 0;
                offRadio.SecondaryFrequencyHz = 0;
                offRadio.IsOn = false;
                offRadio.Ptt = false;
                offRadio.ToneOn = false;

                RadioChanged?.Invoke(this, new DcsRadioChangedEventArgs(oldRadio.Clone(), offRadio.Clone()));
                if (oldRadio.Ptt)
                    PttChanged?.Invoke(this, new DcsPttChangedEventArgs(offRadio.Clone(), true, false));
                if (oldRadio.ToneOn)
                    ToneChanged?.Invoke(this, new DcsToneChangedEventArgs(offRadio.Clone(), true, false));
            }
        }

        if (!string.Equals(oldUnit, packet.Unit, StringComparison.Ordinal) ||
            !string.Equals(oldUnitName, packet.UnitName, StringComparison.Ordinal))
        {
            AircraftChanged?.Invoke(this,
                new DcsAircraftChangedEventArgs(oldUnit, packet.Unit, oldUnitName, packet.UnitName));
        }

        if (oldIsInGame != IsInGame)
            GameModeChanged?.Invoke(this, new DcsGameModeChangedEventArgs(oldIsInGame, IsInGame));

        if (heightmapForEvent != null)
            HeightmapChanged?.Invoke(this, new DcsHeightmapChangedEventArgs(heightmapForEvent));
    }

    private static DcsRadioState NormalizeRadio(DcsRadioState radio)
    {
        radio.Volume = Math.Clamp(radio.Volume, 0.0d, 1.0d);
        if (radio.FrequencyHz < 0) radio.FrequencyHz = 0;
        if (radio.SecondaryFrequencyHz < 0) radio.SecondaryFrequencyHz = 0;
        return radio;
    }

    private static bool HasRadioChanged(DcsRadioState oldRadio, DcsRadioState newRadio) =>
        oldRadio.FrequencyHz != newRadio.FrequencyHz ||
        oldRadio.SecondaryFrequencyHz != newRadio.SecondaryFrequencyHz ||
        oldRadio.Modulation != newRadio.Modulation ||
        Math.Abs(oldRadio.Volume - newRadio.Volume) > 0.01d ||
        oldRadio.IsOn != newRadio.IsOn ||
        oldRadio.Ptt != newRadio.Ptt ||
        oldRadio.Name != newRadio.Name ||
        oldRadio.Enc != newRadio.Enc ||
        oldRadio.EncKey != newRadio.EncKey ||
        oldRadio.HqOn != newRadio.HqOn ||
        oldRadio.SquelchOn != newRadio.SquelchOn ||
        oldRadio.ToneOn != newRadio.ToneOn ||
        oldRadio.SatcomSelected != newRadio.SatcomSelected ||
        oldRadio.SatcomBandActive != newRadio.SatcomBandActive ||
        oldRadio.SatcomDedicatedActive != newRadio.SatcomDedicatedActive;

    private void ChangeState(ServiceState newState)
    {
        var oldState = _state;
        if (oldState == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new ServiceStateChangedEventArgs(oldState, newState));
    }

    public void Dispose()
    {
        Stop();
    }
}
