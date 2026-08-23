using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreqAudio;

namespace OpenFreqServer.SrsBridge;

/// <summary>
/// Entry point for the SRS (DCS-SimpleRadioStandalone) protocol bridge: a TCP listener speaking
/// SRS's newline-delimited JSON sync protocol, plus a UDP socket on the same port (matching real
/// SRS's own single-port convention) carrying voice audio. Each connected SRS client gets a
/// <see cref="SrsClientAdapter"/>, which owns the actual Opus transcoding both directions -- see
/// docs/SRS_BRIDGE.md for the overall plan and status.
/// </summary>
public sealed class SrsBridgeServer : IAsyncDisposable
{
    private readonly ServerConfig _config;
    private readonly IRtcClientFactory _rtcClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SrsBridgeServer> _logger;

    private readonly ConcurrentDictionary<string, SrsClientAdapter> _clients = new();
    private readonly ConcurrentDictionary<string, ClientSession> _openFreqClients;

    // Shadow clients (SRS players bridged onto the real OpenFreq server) are themselves entries
    // in _openFreqClients -- excluded here so a bridged SRS player doesn't get reflected back to
    // SRS clients as a fake "OpenFreq" peer of themselves.
    private readonly ConcurrentDictionary<string, byte> _shadowPeerIds = new();

    private TcpListener? _tcpListener;
    private UdpClient? _udpClient;
    private CancellationTokenSource _cts = new();
    private Task? _acceptLoopTask;
    private Task? _udpLoopTask;
    private Timer? _fakeRosterPushTimer;

    // Last pushed synthetic-entry state per OpenFreq client id, so the periodic push only sends
    // updates for what actually changed (name or joined-frequency set) instead of spamming every
    // connected SRS client every tick.
    private readonly Dictionary<string, (string Name, HashSet<int> FreqsKhz)> _lastPushedFakeEntries = new();
    private static readonly TimeSpan FakeRosterPushInterval = TimeSpan.FromSeconds(3);

    public SrsBridgeServer(ServerConfig config, IRtcClientFactory rtcClientFactory, ILoggerFactory loggerFactory,
        ConcurrentDictionary<string, ClientSession> openFreqClients)
    {
        _config = config;
        _rtcClientFactory = rtcClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SrsBridgeServer>();
        _openFreqClients = openFreqClients;
    }

    public void Start()
    {
        var bindAddress = IPAddress.Parse(_config.SrsBridge.BindAddress);
        var port = _config.SrsBridge.Port;

        _tcpListener = new TcpListener(bindAddress, port);
        _tcpListener.Start();

        _udpClient = new UdpClient(new IPEndPoint(bindAddress, port));

        _cts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _udpLoopTask = Task.Run(() => UdpLoopAsync(_cts.Token));
        _fakeRosterPushTimer = new Timer(_ => _ = PushFakeRosterUpdatesAsync(), null, FakeRosterPushInterval, FakeRosterPushInterval);

        _logger.LogInformation("SRS bridge listening on {Address}:{Port} (TCP+UDP)", _config.SrsBridge.BindAddress, port);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcpClient = await _tcpListener!.AcceptTcpClientAsync(ct);
                var adapter = new SrsClientAdapter(tcpClient, this, _config, _rtcClientFactory, _loggerFactory);
                _ = adapter.RunAsync(ct); // fire-and-forget: adapter manages its own lifetime/cleanup
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (ObjectDisposedException)
        {
            // Listener stopped.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SRS bridge TCP accept loop error");
        }
    }

    /// <summary>Handles SRS's raw 22-byte GUID "ping" packet (VOIP NAT keepalive/mapping probe)
    /// and real voice packets (anything longer) -- see SrsVoicePacket's byte layout. Both cases
    /// also (re)learn the sending client's UDP endpoint, matching real SRS's own
    /// UDPVoiceRouter.ProcessIncomingPacketsAsync/ProcessPendingPacketAsync behavior.</summary>
    private async Task UdpLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _udpClient!.ReceiveAsync(ct);

                if (result.Buffer.Length == 22)
                {
                    var guid = Encoding.ASCII.GetString(result.Buffer);
                    if (_clients.TryGetValue(guid, out var pingAdapter))
                        pingAdapter.VoipEndPoint = result.RemoteEndPoint;

                    // Echo back verbatim, matching real SRS's ping-pong keepalive.
                    await _udpClient.SendAsync(result.Buffer, result.RemoteEndPoint, ct);
                }
                else if (result.Buffer.Length > 22)
                {
                    var packet = SrsVoicePacket.TryDecode(result.Buffer);
                    if (packet == null) continue;

                    if (!_clients.TryGetValue(packet.ClientGuid, out var adapter))
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug("Voice packet from unknown SRS client {Guid}, ignoring", packet.ClientGuid);
                        continue;
                    }

                    adapter.VoipEndPoint = result.RemoteEndPoint;
                    _ = adapter.HandleIncomingVoiceAsync(packet);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (ObjectDisposedException)
        {
            // Socket stopped.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SRS bridge UDP loop error");
        }
    }

    /// <summary>Sends a raw SRS-formatted voice packet to one client's learned UDP endpoint.
    /// No-op if the endpoint isn't known yet (client hasn't sent its first ping/voice packet).</summary>
    public async Task SendVoiceAsync(byte[] packetBytes, IPEndPoint? endpoint)
    {
        if (endpoint == null || _udpClient == null) return;
        try
        {
            await _udpClient.SendAsync(packetBytes, endpoint, _cts.Token);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Best-effort UDP send -- transient network issues or shutdown are not fatal.
        }
    }

    public void RegisterClient(string guid, SrsClientAdapter adapter) => _clients[guid] = adapter;

    public void UnregisterClient(string guid) => _clients.TryRemove(guid, out _);

    /// <summary>Called once a bridged SRS player's shadow OpenFreq connection is authenticated, so
    /// its own <see cref="ClientSession"/> is excluded from the fake-roster synthesis below.</summary>
    public void RegisterShadowPeer(string peerId) => _shadowPeerIds[peerId] = 0;

    public void UnregisterShadowPeer(string peerId) => _shadowPeerIds.TryRemove(peerId, out _);

    public List<SrsClient> GetRosterSnapshot() =>
        _clients.Values.Select(a => a.LastKnownState).Where(c => c != null).Select(c => c!)
            .Concat(BuildFakeOpenFreqEntries())
            .ToList();

    /// <summary>Synthesizes an SRS-shaped roster entry per real (non-shadow) OpenFreq client, so
    /// SRS users can at least see who else is on the server -- SRS's own roster protocol has no
    /// field to represent a peer speaking a different protocol, so this is the only way to make
    /// them visible at all. Modulation is a frequency-band guess (OpenFreq doesn't track AM/FM
    /// explicitly) -- fine for display, unlike the voice-packet path where a wrong guess causes a
    /// real SRS client to reject audio.</summary>
    private IEnumerable<SrsClient> BuildFakeOpenFreqEntries()
    {
        foreach (var session in _openFreqClients.Values)
        {
            if (!session.IsAuthenticated || _shadowPeerIds.ContainsKey(session.Id)) continue;

            var radios = SrsPlayerRadioInfo.CreateDefaultRadios();
            var khzList = session.CurrentFrequencies.Keys.Take(SrsPlayerRadioInfo.MaxRadios).ToList();
            for (var i = 0; i < khzList.Count; i++)
            {
                var khz = khzList[i];
                radios[i] = new SrsRadio
                {
                    Freq = khz * 1000.0,
                    Modulation = FastPathAudioSim.GetBandConfig(khz).Modulation == ModulationType.FM
                        ? SrsModulation.FM
                        : SrsModulation.AM
                };
            }

            yield return new SrsClient
            {
                ClientGuid = SrsGuid.FromOpenFreqId(session.Id),
                Name = string.IsNullOrWhiteSpace(session.DisplayName)
                    ? "OpenFreq Player"
                    : $"{session.DisplayName} (OpenFreq)",
                Coalition = 0,
                RadioInfo = new SrsPlayerRadioInfo { Radios = radios }
            };
        }
    }

    /// <summary>Periodic diff-and-push: sends RADIO_UPDATE for new/changed fake OpenFreq entries
    /// and CLIENT_DISCONNECT for ones that left, so connected SRS clients' rosters stay live
    /// without needing to reconnect/re-SYNC.</summary>
    private async Task PushFakeRosterUpdatesAsync()
    {
        if (_clients.IsEmpty) return; // nobody to push to

        try
        {
            var current = BuildFakeOpenFreqEntries().ToDictionary(c => c.ClientGuid);
            var currentShape = current.ToDictionary(kvp => kvp.Key,
                kvp => (kvp.Value.Name, FreqsKhz: (kvp.Value.RadioInfo?.Radios ?? [])
                    .Where(r => r.IsTuned).Select(r => (int)Math.Round(r.Freq / 1000.0)).ToHashSet()));

            foreach (var (guid, client) in current)
            {
                if (_lastPushedFakeEntries.TryGetValue(guid, out var previous) &&
                    previous.Name == currentShape[guid].Name &&
                    previous.FreqsKhz.SetEquals(currentShape[guid].FreqsKhz))
                    continue; // unchanged

                await MulticastAsync(new SrsNetworkMessage
                {
                    MsgType = SrsNetworkMessage.MessageType.RADIO_UPDATE,
                    Client = client
                }, excludeGuid: null);
            }

            foreach (var guid in _lastPushedFakeEntries.Keys.Except(current.Keys).ToList())
            {
                await MulticastAsync(new SrsNetworkMessage
                {
                    MsgType = SrsNetworkMessage.MessageType.CLIENT_DISCONNECT,
                    Client = new SrsClient { ClientGuid = guid }
                }, excludeGuid: null);
            }

            _lastPushedFakeEntries.Clear();
            foreach (var (guid, shape) in currentShape)
                _lastPushedFakeEntries[guid] = shape;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pushing fake OpenFreq roster updates to SRS clients");
        }
    }

    public async Task MulticastAsync(SrsNetworkMessage message, string? excludeGuid)
    {
        var deliveries = _clients
            .Where(kvp => kvp.Key != excludeGuid)
            .Select(kvp => kvp.Value.DeliverAsync(message));

        await Task.WhenAll(deliveries);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        if (_fakeRosterPushTimer != null)
            await _fakeRosterPushTimer.DisposeAsync();

        _tcpListener?.Stop();
        _udpClient?.Dispose();

        var pending = new List<Task?> { _acceptLoopTask, _udpLoopTask }
            .Where(t => t != null).Select(t => t!).ToList();
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch { /* best-effort */ }

        foreach (var adapter in _clients.Values.ToList())
            await adapter.DisposeAsync();

        _cts.Dispose();
    }
}
