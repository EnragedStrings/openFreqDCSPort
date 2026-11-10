using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Common.Signaling;
using OpenFreqServer.Jamming;
using OpenFreqServer.Satcom;

namespace OpenFreqServer;

public class SignalingServer
{
    private WebApplication? _app;
    private readonly ServerConfig _config;
    private readonly ConcurrentDictionary<string, ClientSession> _clients = new();
    private readonly FrequencyChannelManager _channelManager = new();
    private readonly IAudioStreamServer _audioServer;
    private readonly ILogger<SignalingServer> _logger;
    private readonly SatcomServerCoordinator? _satcom;
    private readonly JammingCoordinator? _jamming;
    private CancellationTokenSource _cts = new();

    // Jammer catalog: checked for changes this often, re-broadcast at least every BroadcastInterval.
    private static readonly TimeSpan JammingTickInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan SatcomTickInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SatcomEphemerisBroadcastInterval = TimeSpan.FromSeconds(10);

    // Guards against a single wedged socket stalling a broadcast indefinitely. Kept well above
    // the worst-case send latency seen during synchronized channel tune bursts (clients all jumping to 3D) so we
    // don't disconnect slow clients. Dead clients are reaped by the RTP watchdog anyway

    private const double WebsocketTimeoutMillis = 10000;
    private readonly TimeSpan _rtpTimeoutDuration;
    private readonly TimeSpan _watchdogInterval;

    // Collect peer-list updates are collected so we don't send them out all at once
    private readonly SemaphoreSlim _peerUpdateSignal = new(0, 1);
    private static readonly TimeSpan PeerUpdateDebounce = TimeSpan.FromMilliseconds(250);

    // Pending remote-oracle LOS requests awaiting a client's DcsLosOracleResponseMessage, keyed by
    // RequestId -- see RequestRemoteLineOfSightAsync. Entries are removed by whichever side
    // resolves first (the response arriving, or the caller's timeout), so a slow/never-responding
    // client can't leak these.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DcsLosOracleResponseMessage>>
        _pendingLosOracleRequests = new();

    /// <summary>Shared with the SRS bridge (SrsBridgeServer.LosOracle just forwards to this same
    /// instance) so both consumers see one consistent cache/sticky-oracle state.</summary>
    public LosOracleService LosOracle { get; }

    /// <summary>The jammer set, or null when jamming is disabled in the config (docs/jamming).</summary>
    public JammingCoordinator? Jamming => _jamming;

    private OpenFreq.Common.Eccm.EccmCommPlan? _eccmCommPlan;
    private DateOnly _eccmCommPlanDay;
    private readonly Lock _eccmLock = new();

    /// <summary>The comm plan clients get (fingerprints only), or null when <c>eccm</c> is not configured. Built on
    /// first use and again when the UTC day rolls over (an unset mission date means "today").</summary>
    public OpenFreq.Common.Eccm.EccmCommPlan? EccmCommPlan()
    {
        if (_config.Eccm is not { Enabled: true } fill) return null;
        var now = DateTime.UtcNow;
        lock (_eccmLock)
        {
            var today = DateOnly.FromDateTime(now);
            if (_eccmCommPlan == null || _eccmCommPlanDay != today)
            {
                var errors = new List<string>();
                _eccmCommPlan = fill.ToCommPlan(now, errors);
                _eccmCommPlanDay = today;
                foreach (var error in errors) _logger.LogWarning("ECCM comm plan: {Error}", error);
                _logger.LogInformation(
                    "ECCM comm plan: mission date {Date}, {Wods} WOD(s), {Fmt} FMT frequencies, hop rate {Rate}/s",
                    _eccmCommPlan.MissionDate, _eccmCommPlan.Mwod.Count,
                    _eccmCommPlan.FmtFrequenciesKhz?.Count ?? OpenFreq.Common.Eccm.Hopset.DefaultFmtFrequenciesKhz.Count,
                    _eccmCommPlan.HopRateHz);
            }
            return _eccmCommPlan;
        }
    }

    private readonly TranscriptDeliveryService _transcriptDelivery;

    // High-performance logging delegates
    private static readonly Action<ILogger, int, Exception?> LogServerStarted =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1, nameof(StartAsync)),
            "OpenFreqServer listening on port {Port}");

    private static readonly Action<ILogger, string, string, Exception?> LogClientConnected =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2, nameof(HandleWebSocketConnection)),
            "{DisplayName} ({ClientId}) connected");

    private static readonly Action<ILogger, string, string, int, Exception?> LogClientAuthenticated =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Information,
            new EventId(3, nameof(HandleAuthenticate)),
            "{DisplayName} ({ClientId}) authenticated, audio port {AudioPort}");

    private static readonly Action<ILogger, string, string, double, Exception?> LogClientJoinedFrequency =
        LoggerMessage.Define<string, string, double>(
            LogLevel.Information,
            new EventId(4, nameof(HandleJoinChannel)),
            "{DisplayName} ({ClientId}) joined frequency {Frequency:F3} MHz");

    private static readonly Action<ILogger, string, string, double, Exception?> LogClientLeftFrequency =
        LoggerMessage.Define<string, string, double>(
            LogLevel.Information,
            new EventId(5, nameof(LeaveCurrentChannel)),
            "{DisplayName} ({ClientId}) left frequency {Frequency:F3} MHz");

    private static readonly Action<ILogger, string, string, bool, double, int, Exception?> LogTransmissionState =
        LoggerMessage.Define<string, string, bool, double, int>(
            LogLevel.Information,
            new EventId(6, nameof(HandleTransmission)),
            "{DisplayName} ({ClientId}) transmission: {IsTransmitting} on {Frequency:F3} MHz, broadcasting to {PeerCount} peer(s)");

    private static readonly Action<ILogger, string, string, Exception?> LogClientCleanedUp =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(7, nameof(CleanupClient)),
            "{DisplayName} ({ClientId}) cleaned up");

    private static readonly Action<ILogger, string, string, Exception?> LogClientRtpTimeout =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(8, nameof(IdleWatchdogAsync)),
            "{DisplayName} ({ClientId}) removed: RTP heartbeat timeout (60 s)");

    public ConcurrentDictionary<string, ClientSession> Clients => _clients;
    public FrequencyChannelManager ChannelManager => _channelManager;
    public IAudioStreamServer AudioServer => _audioServer;

    /// <summary>
    /// The actual TCP port the WebSocket endpoint is bound to. Only meaningful after
    /// <see cref="StartAsync"/> has completed. Useful when the configured port is 0
    /// (let the OS pick a free port), e.g. in integration tests.
    /// </summary>
    public int? BoundWebSocketPort { get; private set; }

    private static string GetDisplayName(ClientSession session) =>
        !string.IsNullOrWhiteSpace(session.DisplayName) ? session.DisplayName : "Unnamed";

    public SignalingServer(ServerConfig config, ILoggerFactory loggerFactory)
        : this(config, loggerFactory, null, null, null)
    {
    }

    /// <summary>
    /// Test-friendly constructor. Allows injecting a fake audio relay (avoids binding a
    /// real UDP port) and shortening the idle-watchdog timings for deterministic tests.
    /// </summary>
    /// <param name="audioServer">Audio relay to use; null builds the real <see cref="AudioStreamServer"/>.</param>
    /// <param name="rtpTimeout">Idle RTP timeout before cleanup; null uses the production default (60 s).</param>
    /// <param name="watchdogInterval">How often the idle watchdog runs; null uses the production default (30 s).</param>
    public SignalingServer(
        ServerConfig config,
        ILoggerFactory loggerFactory,
        IAudioStreamServer? audioServer,
        TimeSpan? rtpTimeout,
        TimeSpan? watchdogInterval)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger<SignalingServer>();
        _rtpTimeoutDuration = rtpTimeout ?? TimeSpan.FromMinutes(1);
        _watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(30);
        _audioServer = audioServer ?? new AudioStreamServer(_channelManager, _clients, loggerFactory, config.AudioPort);
        _satcom = config.SatcomEnabled ? new SatcomServerCoordinator(config.Satcom, loggerFactory) : null;
        _jamming = config.Jamming.Enabled
            ? new JammingCoordinator(config.Jamming, loggerFactory.CreateLogger<JammingCoordinator>())
            {
                BroadcastInterval = TimeSpan.FromSeconds(Math.Max(1.0, config.Jamming.BroadcastIntervalSeconds))
            }
            : null;
        LosOracle = new LosOracleService(this, _clients, loggerFactory.CreateLogger<LosOracleService>());
        if (_jamming != null)
        {
            // Auto jammers' intercept geometry: the transmitter's own DCS referees first (non-blocking, cached).
            _jamming.LineOfSight = (jammerId, txId, jLat, jLon, jAlt, txLat, txLon, txAlt) =>
                LosOracle.TryGetLineOfSight($"jam:{jammerId}:{txId}", txId, jLat, jLon, jAlt, txLat, txLon, txAlt);
        }

        _transcriptDelivery = new TranscriptDeliveryService(this, _clients, _channelManager, LosOracle,
            loggerFactory.CreateLogger<TranscriptDeliveryService>());

        // Build Kestrel application
        var builder = WebApplication.CreateBuilder();

        // Configure Kestrel
        builder.WebHost.UseKestrel(options =>
        {
            // Listen on all interfaces
            options.ListenAnyIP(config.WebSocketPort, listenOptions =>
            {
                // Performance tuning
                listenOptions.Protocols = HttpProtocols.Http1;
            });

            // Connection limits
            options.Limits.MaxConcurrentConnections = 1000;
            options.Limits.MaxConcurrentUpgradedConnections = 1000;

            // WebSocket keep-alive
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        });

        // Replace default logging with LoggerFactory
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggerFactory);

        // Disable unnecessary services to keep it lightweight
        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        _app = builder.Build();

        // Configure WebSocket options
        _app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
        });

        // WebSocket signaling endpoint
        _app.Map("/", async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleWebSocketConnection(webSocket, context);
            }
            else
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connection required");
            }
        });
    }

    public async Task StartAsync()
    {
        LogServerStarted(_logger, _config.WebSocketPort, null);

        // Raise the thread-pool floor so a synchronized reconnect/tune burst (a whole flight
        // jumping to 3D at once) doesn't starve on the pool's growth
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorker, 200), Math.Max(minIo, 200));

        try
        {
            await _app!.StartAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server");
            throw;
        }

        BoundWebSocketPort = ResolveBoundPort();

        _ = IdleWatchdogAsync(_cts.Token);
        _ = PeerUpdateBroadcastLoopAsync(_cts.Token);

        if (_satcom != null)
        {
            _satcom.Start();
            _ = SatcomTickLoopAsync(_cts.Token);
        }

        if (_jamming != null)
            _ = JammingBroadcastLoopAsync(_cts.Token);
    }

    private int? ResolveBoundPort()
    {
        if (_config.WebSocketPort != 0)
            return _config.WebSocketPort;

        // Configured port 0 -> Kestrel picked a free port; read it back from the server's resolved addresses
        var addresses = _app?.Services.GetService<IServer>()?
            .Features.Get<IServerAddressesFeature>()?.Addresses;

        if (addresses == null) return null;

        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }

        return null;
    }

    private async Task IdleWatchdogAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_watchdogInterval, ct);

                _transcriptDelivery.PruneExpired();

                var now = DateTime.UtcNow;
                foreach (var (clientId, session) in _clients)
                {
                    if (!session.IsAuthenticated) continue;

                    var lastRtp = _audioServer.GetLastRtpReceived(clientId);
                    if (lastRtp == null) continue; // audio session not yet created

                    if (now - lastRtp.Value <= _rtpTimeoutDuration) continue; // timeout not reached

                    LogClientRtpTimeout(_logger, GetDisplayName(session), clientId, null);
                    _ = CleanupClient(clientId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down — expected
        }
    }

    private async Task HandleWebSocketConnection(WebSocket webSocket, HttpContext httpContext)
    {
        string clientId = Guid.NewGuid().ToString();

        try
        {
            var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _logger.LogDebug("Client {ClientId} connecting from {RemoteIp}", clientId, remoteIp);

            var session = new ClientSession(clientId, string.Empty, webSocket, remoteIp);
            _clients[clientId] = session;

            LogClientConnected(_logger, GetDisplayName(session), clientId, null);

            await HandleClientMessages(session);
        }
        catch (Exception ex)
        {
            var displayName = _clients.TryGetValue(clientId, out var session)
                ? GetDisplayName(session)
                : "Unnamed";
            _logger.LogError(ex, "Error in WebSocket connection for {DisplayName} ({ClientId})", displayName, clientId);
        }
        finally
        {
            await CleanupClient(clientId);
        }
    }

    private async Task HandleClientMessages(ClientSession session)
    {
        var buffer = new byte[8192];

        try
        {
            while (session.WebSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await session.WebSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation(
                        "{DisplayName} ({ClientId}) sent close frame: {Status} \"{Description}\"",
                        GetDisplayName(session), session.Id,
                        result.CloseStatus, result.CloseStatusDescription ?? "");

                    if (session.WebSocket.State == WebSocketState.CloseReceived)
                    {
                        await session.WebSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            CancellationToken.None);
                    }
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessMessage(session, message);
                }

                session.UpdateActivity();
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            // Transport dropped without a close handshake (TCP reset / network loss)
            _logger.LogInformation(
                "{DisplayName} ({ClientId}) disconnected abruptly: {Error} (TCP reset / network loss)",
                GetDisplayName(session), session.Id, ex.WebSocketErrorCode);
        }
        catch (OperationCanceledException)
        {
            // Server shutting down
            _logger.LogDebug("{DisplayName} ({ClientId}) connection cancelled during shutdown", GetDisplayName(session),
                session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling messages for {DisplayName} ({ClientId})",
                GetDisplayName(session), session.Id);
        }
    }

    private async Task ProcessMessage(ClientSession session, string messageText)
    {
        try
        {
            var message = Json.Json.Instance.Deserialize<SignalingMessage>(messageText);
            if (message == null) return;

            switch (message.Type)
            {
                case "authenticate":
                    await HandleAuthenticate(session, message);
                    break;

                case "join":
                    await HandleJoinChannel(session, message);
                    break;

                case "leave":
                    await HandleLeaveChannel(session, message);
                    break;

                case "transmission":
                    await HandleTransmission(session, message);
                    break;
                case "mode-update":
                    await HandleModeUpdate(session, message);
                    break;
                case "set-display-name":
                    await SetDisplayName(session, message);
                    break;
                case SignalingMessageTypes.SatcomGeometryUpdate:
                    await HandleSatcomGeometryUpdate(session, message);
                    break;
                case SignalingMessageTypes.DcsPresenceUpdate:
                    await HandleDcsPresenceUpdate(session, message);
                    break;
                case SignalingMessageTypes.DcsLosOracleResponse:
                    HandleDcsLosOracleResponse(message);
                    break;
                case SignalingMessageTypes.TransmissionTranscript:
                    await HandleTransmissionTranscript(session, message);
                    break;

                default:
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Unknown message type: {MessageType}", message.Type);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON from {DisplayName} ({ClientId})", GetDisplayName(session), session.Id);
            await SendError(session, "Invalid message format");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message from {DisplayName} ({ClientId})", GetDisplayName(session),
                session.Id);
            await SendError(session, $"Server error processing message");
        }
    }

    private async Task HandleAuthenticate(ClientSession session, SignalingMessage message)
    {
        var authMsg = SignalingMessageFactory.DeserializePayload<AuthenticateMessage>(message.Payload);

        if (authMsg == null)
        {
            await SendError(session, "Invalid authentication message");
            return;
        }

        session.DisplayName = authMsg.DisplayName;
        session.WantsTranscripts = authMsg.WantsTranscripts;

        // Reject clients whose version is incompatible with the server build (patch-level semver differences are allowed)
        var serverVersion = OpenFreqVersion.Current;
        if (!OpenFreqVersion.AreCompatible(authMsg.Version, serverVersion))
        {
            _logger.LogWarning(
                "Rejected {DisplayName} ({ClientId}): version mismatch (client {ClientVersion}, server {ServerVersion})",
                GetDisplayName(session), session.Id, authMsg.Version ?? "unknown", serverVersion);

            await SendToClient(session, SignalingMessageFactory.CreateError(
                $"Version mismatch: client {authMsg.Version ?? "unknown"}, server {serverVersion}",
                serverVersion));

            if (session.WebSocket.State == WebSocketState.Open)
            {
                await session.WebSocket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Version mismatch",
                    CancellationToken.None);
            }

            return;
        }

        if (string.IsNullOrEmpty(_config.ServerPassword) ||
            authMsg.Password == _config.ServerPassword)
        {
            session.IsAuthenticated = true;

            var audioPort = _audioServer.CreateAudioSession(session.Id);
            LogClientAuthenticated(_logger, GetDisplayName(session), session.Id, audioPort, null);

            await SendSuccess(session, "Authenticated", session.Id, audioPort, _config.EnableOpusCompression);

            if (_jamming != null)
                await SendToClient(session, SignalingMessageFactory.CreateJammerCatalogUpdate(_jamming.Snapshot()));

            if (EccmCommPlan() is { } commPlan)
                await SendToClient(session, SignalingMessageFactory.CreateEccmCommPlanUpdate(commPlan));
        }
        else
        {
            await SendError(session, "Authentication failed");

            // Disconnect on auth failure
            await Task.Delay(1000); // Brief delay to prevent brute force
            if (session.WebSocket.State == WebSocketState.Open)
            {
                await session.WebSocket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Authentication failed",
                    CancellationToken.None);
            }
        }
    }

    private async Task HandleJoinChannel(ClientSession session, SignalingMessage message)
    {
        if (!session.IsAuthenticated)
        {
            await SendError(session, "Not authenticated");
            return;
        }

        var joinMsg = SignalingMessageFactory.DeserializePayload<JoinChannelMessage>(message.Payload);

        if (joinMsg == null)
        {
            await SendError(session, "Invalid join message");
            return;
        }

        // Declared listening position for transcript-delivery gating -- see
        // JoinChannelMessage.Lat/Lon/Alt's own doc comment. Updated even on an idempotent rejoin
        // below, so a bot re-declaring/changing its position takes effect immediately.
        if (joinMsg.LatitudeDeg is { } joinLat && joinMsg.LongitudeDeg is { } joinLon &&
            joinMsg.AltitudeMeters is { } joinAlt)
            session.FrequencyListenerPositions[joinMsg.FrequencyKhz] = (joinLat, joinLon, joinAlt);
        else
            session.FrequencyListenerPositions.TryRemove(joinMsg.FrequencyKhz, out _);

        // Membership and the capacity check are decided atomically by the channel manager. A
        // silent observer (GCI "monitor all" scanner) never competes with real players for
        // capacity -- it doesn't count toward the limit and must never itself be blocked by it
        // either, or a scanner sweep would randomly fail to pick up frequencies that happen to
        // already be at capacity.
        var joinResult = _channelManager.JoinChannel(
            joinMsg.FrequencyKhz,
            session.Id,
            session.DisplayName ?? "Unnamed",
            session.Is3d,
            _config.MaxClientsPerChannel,
            joinMsg.IsObserver);

        switch (joinResult)
        {
            case AlreadyInChannel:
                // Idempotent rejoin. A reconnect race (the RTC client auto-rejoins while the
                // app layer also rejoins a radio channel) can send a duplicate join on the same
                // session. Just resend the current channel state.
                await SendChannelState(session, joinMsg.FrequencyKhz,
                    CollectChannelPeers(joinMsg.FrequencyKhz, session.Id));
                return;

            case ChannelFull:
                // Not a member, so the position declared above must not linger.
                session.FrequencyListenerPositions.TryRemove(joinMsg.FrequencyKhz, out _);
                await SendError(session, "Channel is full");
                return;

            case ChannelJoined:
                break;

            default: throw new UnreachableException();
        }

        await SendChannelState(session, joinMsg.FrequencyKhz,
            CollectChannelPeers(joinMsg.FrequencyKhz, session.Id));

        if (!joinMsg.IsObserver)
        {
            // A silent observer (GCI "monitor all" scanner) must never be announced -- see
            // JoinChannelMessage.IsObserver's own doc comment.
            await BroadcastToChannel(
                joinMsg.FrequencyKhz,
                session.Id,
                SignalingMessageFactory.CreatePeerJoined(session.Id, session.DisplayName, joinMsg.FrequencyKhz));
        }

        LogClientJoinedFrequency(_logger, GetDisplayName(session), session.Id, joinMsg.FrequencyKhz / 1000d, null);

        if (_config.BroadcastPeerUpdates)
            RequestPeerUpdateBroadcast();
    }

    /// <summary>
    /// The peers a client should be told about on a frequency: every real member there except
    /// the client itself, skipping any whose session has since gone away. GetPeersInChannel
    /// excludes silent observers -- an observer sees the same "who's really here" view as
    /// everyone else, it just never appears in that view itself.
    /// </summary>
    private List<ChannelStateMessage.Peer> CollectChannelPeers(int frequencyKhz, string excludeClientId)
    {
        List<ChannelStateMessage.Peer> peers = [];
        foreach (var peer in _channelManager.GetPeersInChannel(frequencyKhz))
        {
            if (peer.Id == excludeClientId) continue;
            if (!_clients.TryGetValue(peer.Id, out var clientSession)) continue;
            peers.Add(new ChannelStateMessage.Peer(clientSession.Id, clientSession.DisplayName ?? "Unnamed"));
        }

        return peers;
    }

    private async Task HandleLeaveChannel(ClientSession session, SignalingMessage message)
    {
        await LeaveCurrentChannel(session, message);
    }

    private async Task LeaveCurrentChannel(ClientSession session, SignalingMessage message)
    {
        if (!_channelManager.IsInAnyChannel(session.Id)) return;

        var transmissionMsg = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(message.Payload);
        if (transmissionMsg == null) return;

        var frequencyKhz = transmissionMsg.FrequencyKhz;
        var wasMember = _channelManager.LeaveChannel(frequencyKhz, session.Id, out var wasObserver);

        // Cleared even if not a member: a declared position is meaningless off the frequency.
        session.FrequencyListenerPositions.TryRemove(frequencyKhz, out _);

        if (!wasMember) return;
        _jamming?.ReportTransmission(session.Id, frequencyKhz, transmitting: false);

        // A silent observer (GCI "monitor all" scanner) is never announced, coming or going.
        if (!wasObserver)
        {
            await BroadcastToChannel(
                frequencyKhz,
                session.Id,
                SignalingMessageFactory.CreatePeerLeft(session.Id, frequencyKhz));
        }

        LogClientLeftFrequency(_logger, GetDisplayName(session), session.Id, frequencyKhz / 1000d, null);

        if (_config.BroadcastPeerUpdates)
            RequestPeerUpdateBroadcast();
    }

    private async Task LeaveAllChannels(ClientSession session)
    {
        // Leave everything in one atomic step, then announce: no join can slip in between
        // reading the client's channels and removing it from them.
        var left = _channelManager.LeaveAllChannels(session.Id);

        foreach (var (frequency, wasObserver) in left)
        {
            if (!wasObserver)
            {
                await BroadcastToChannel(
                    frequency,
                    session.Id,
                    SignalingMessageFactory.CreatePeerLeft(session.Id, frequency));
            }

            LogClientLeftFrequency(_logger, GetDisplayName(session), session.Id, frequency / 1000d, null);
        }

        session.FrequencyListenerPositions.Clear();

        if (_config.BroadcastPeerUpdates)
            RequestPeerUpdateBroadcast();
    }

    private async Task HandleTransmission(ClientSession session, SignalingMessage message)
    {
        if (!session.IsAuthenticated) return;
        if (!_channelManager.IsInAnyChannel(session.Id)) return;

        var transmissionMsg = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(message.Payload);
        if (transmissionMsg == null) return;

        // Record the transmit state on the peer entry itself, so the channel snapshot sent
        // out as allPeersStatus reports who is talking. It used to say "receiving" for
        // everyone forever, which fought the per-event updates clients apply on top: any
        // peer-list broadcast landing mid-transmission cleared the sender's TX indicator.
        var peersInChannel = _channelManager.SetTransmissionState(
            transmissionMsg.FrequencyKhz, session.Id, transmissionMsg.Transmitting, transmissionMsg.Is3d);

        if (peersInChannel is null) return;

        _jamming?.ReportTransmission(session.Id, transmissionMsg.FrequencyKhz, transmissionMsg.Transmitting,
            transmissionMsg.LatitudeDeg, transmissionMsg.LongitudeDeg, transmissionMsg.AltitudeMeters);

        if (transmissionMsg.TransmissionId is { Length: > 0 } transmissionId)
        {
            if (transmissionMsg.Transmitting)
                _transcriptDelivery.RecordTransmissionStart(transmissionId, session.Id,
                    transmissionMsg.FrequencyKhz, transmissionMsg.LatitudeDeg, transmissionMsg.LongitudeDeg,
                    transmissionMsg.AltitudeMeters);
            else
                _transcriptDelivery.RecordTransmissionEnd(transmissionId);
        }

        LogTransmissionState(_logger, GetDisplayName(session), session.Id, transmissionMsg.Transmitting,
            transmissionMsg.FrequencyKhz / 1000d, peersInChannel.Length, null);

        await BroadcastToChannel(
            transmissionMsg.FrequencyKhz,
            session.Id,
            SignalingMessageFactory.CreateTransmissionEvent(
                session.Id,
                transmissionMsg.FrequencyKhz,
                transmissionMsg.Transmitting,
                transmissionMsg.Is3d));
    }

    private Task HandleModeUpdate(ClientSession session, SignalingMessage message)
    {
        if (!session.IsAuthenticated) return Task.CompletedTask;

        var modeMsg = SignalingMessageFactory.DeserializePayload<ModeUpdateMessage>(message.Payload);
        if (modeMsg == null) return Task.CompletedTask;

        session.Is3d = modeMsg.Is3d;
        _channelManager.UpdateIs3d(session.Id, modeMsg.Is3d);

        RequestPeerUpdateBroadcast();
        return Task.CompletedTask;
    }

    private Task HandleSatcomGeometryUpdate(ClientSession session, SignalingMessage message)
    {
        if (!session.IsAuthenticated || _satcom == null) return Task.CompletedTask;

        var geometryMsg = SignalingMessageFactory.DeserializePayload<SatcomGeometryUpdateMessage>(message.Payload);
        if (geometryMsg == null) return Task.CompletedTask;

        var net = _satcom.GetNet(geometryMsg.NetId);
        if (net == null) return Task.CompletedTask; // unknown net id -- ignore rather than trust client-supplied config

        _satcom.HandleGeometryUpdate(session.Id, geometryMsg, net, Environment.TickCount64);

        // Cache the last-seen login/debug/priority request alongside the session so the tick loop
        // (which runs independently of message arrival) has something to evaluate against.
        session.SatcomNetId = geometryMsg.NetId;
        session.SatcomLoginReady = geometryMsg.LoginReady;
        session.SatcomDebugRequested = geometryMsg.DebugRequested;
        session.SatcomPriority = geometryMsg.Priority;

        return Task.CompletedTask;
    }

    private Task HandleDcsPresenceUpdate(ClientSession session, SignalingMessage message)
    {
        if (!session.IsAuthenticated) return Task.CompletedTask;

        var presenceMsg = SignalingMessageFactory.DeserializePayload<DcsPresenceUpdateMessage>(message.Payload);
        if (presenceMsg == null) return Task.CompletedTask;

        session.DcsLatitudeDeg = presenceMsg.LatitudeDeg;
        session.DcsLongitudeDeg = presenceMsg.LongitudeDeg;
        session.DcsAltitudeMeters = presenceMsg.AltitudeMeters;
        session.DcsTheater = presenceMsg.Theater;
        session.DcsPresenceUpdatedUtc = DateTime.UtcNow;
        _jamming?.ReportTransmitterPosition(session.Id, presenceMsg.LatitudeDeg, presenceMsg.LongitudeDeg,
            presenceMsg.AltitudeMeters);

        return Task.CompletedTask;
    }

    private void HandleDcsLosOracleResponse(SignalingMessage message)
    {
        var responseMsg = SignalingMessageFactory.DeserializePayload<DcsLosOracleResponseMessage>(message.Payload);
        if (responseMsg == null) return;

        if (_pendingLosOracleRequests.TryRemove(responseMsg.RequestId, out var tcs))
            tcs.TrySetResult(responseMsg);
    }

    private async Task HandleTransmissionTranscript(ClientSession session, SignalingMessage message)
    {
        if (!session.IsAuthenticated) return;

        var transcriptMsg = SignalingMessageFactory.DeserializePayload<TransmissionTranscriptMessage>(message.Payload);
        if (transcriptMsg == null) return;

        await _transcriptDelivery.HandleTranscriptAsync(session, transcriptMsg);
    }

    /// <summary>Relays one transcript to one bot-capable client -- see TranscriptDeliveryService,
    /// which is the sole caller and has already decided this listener should receive exactly this
    /// (possibly trimmed) text.</summary>
    public async Task SendTranscriptDeliveryAsync(ClientSession session, TranscriptDeliveryMessage message)
    {
        await SendToClient(session, SignalingMessageFactory.CreateTranscriptDelivery(message));
    }

    /// <summary>Asks <paramref name="oracleSession"/> to referee terrain LOS between two arbitrary
    /// geodetic points via its own live DCS instance (see DcsLosOracleRequestMessage's own doc
    /// comment) -- used for legs involving an SRS-bridged peer, which the server itself has no
    /// terrain data to evaluate. Returns null if the client never responds within
    /// <paramref name="timeout"/>, disconnects mid-request, or reports no terrain data available;
    /// callers should fall back to the geometric-only signal model in that case, not block on it.
    /// </summary>
    public async Task<DcsLosOracleResponseMessage?> RequestRemoteLineOfSightAsync(ClientSession oracleSession,
        double fromLat, double fromLon, double fromAlt, double toLat, double toLon, double toAlt, TimeSpan timeout)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<DcsLosOracleResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingLosOracleRequests.TryAdd(requestId, tcs))
            return null; // requestId collision is astronomically unlikely, but fail closed rather than clobber

        try
        {
            var request = SignalingMessageFactory.CreateDcsLosOracleRequest(new DcsLosOracleRequestMessage
            {
                RequestId = requestId,
                FromLatitudeDeg = fromLat,
                FromLongitudeDeg = fromLon,
                FromAltitudeMeters = fromAlt,
                ToLatitudeDeg = toLat,
                ToLongitudeDeg = toLon,
                ToAltitudeMeters = toAlt
            });
            await SendToClient(oracleSession, request);

            using var cts = new CancellationTokenSource(timeout);
            await using var registration = cts.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingLosOracleRequests.TryRemove(requestId, out _);
        }
    }

    private async Task SatcomTickLoopAsync(CancellationToken ct)
    {
        if (_satcom == null) return;
        var sinceLastEphemerisBroadcast = TimeSpan.Zero;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(SatcomTickInterval, ct);
                var nowMs = Environment.TickCount64;

                foreach (var (clientId, netId) in _satcom.ActiveSessions)
                {
                    if (!_clients.TryGetValue(clientId, out var session) || !session.IsAuthenticated) continue;
                    if (session.SatcomNetId != netId) continue; // client has since switched/left this net

                    try
                    {
                        var linkState = _satcom.Evaluate(clientId, netId, session.SatcomLoginReady,
                            session.SatcomDebugRequested, session.SatcomPriority, nowMs);
                        await SendToClient(session, SignalingMessageFactory.CreateSatcomLinkState(linkState));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SATCOM: error evaluating link state for {ClientId} on net {NetId}", clientId, netId);
                    }
                }

                sinceLastEphemerisBroadcast += SatcomTickInterval;
                if (sinceLastEphemerisBroadcast >= SatcomEphemerisBroadcastInterval)
                {
                    sinceLastEphemerisBroadcast = TimeSpan.Zero;
                    await BroadcastToAllChannels(
                        SignalingMessageFactory.CreateSatelliteEphemerisUpdate(_satcom.GetSatelliteInfoDtos()));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down — expected
        }
    }

    /// <summary>Broadcasts the jammer catalog on every change (checked each JammingTickInterval) and at least
    /// every JammingCoordinator.BroadcastInterval, modelled on the SATCOM ephemeris broadcast.</summary>
    private async Task JammingBroadcastLoopAsync(CancellationToken ct)
    {
        if (_jamming == null) return;
        var sinceLastBroadcast = TimeSpan.Zero;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(JammingTickInterval, ct);
                sinceLastBroadcast += JammingTickInterval;

                try
                {
                    var snapshot = _jamming.NextBroadcast(sinceLastBroadcast >= _jamming.BroadcastInterval);
                    if (snapshot == null) continue;
                    sinceLastBroadcast = TimeSpan.Zero;
                    await BroadcastToAllChannels(SignalingMessageFactory.CreateJammerCatalogUpdate(snapshot));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Jamming: catalog broadcast failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down — expected
        }
    }

    private async Task SetDisplayName(ClientSession session, SignalingMessage message)
    {
        var setDisplayNameMsg = SignalingMessageFactory.DeserializePayload<DisplayNameMessage>(message.Payload);

        if (setDisplayNameMsg == null)
        {
            await SendError(session, "Invalid set display name message");
            return;
        }

        session.DisplayName = setDisplayNameMsg.DisplayName;
        _channelManager.UpdateDisplayName(session.Id, setDisplayNameMsg.DisplayName);

        if (_config.BroadcastPeerUpdates)
            RequestPeerUpdateBroadcast();
    }

    /// <summary>
    /// Schedules a non-blocking coalesced full-peer-state broadcast.
    /// </summary>
    private void RequestPeerUpdateBroadcast()
    {
        try { _peerUpdateSignal.Release(); }
        catch (SemaphoreFullException) { /* a broadcast is already queued */ }
    }

    private async Task PeerUpdateBroadcastLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _peerUpdateSignal.WaitAsync(ct);
                // Let a burst settle so a flight jumping to 3d together yields one broadcast.
                await Task.Delay(PeerUpdateDebounce, ct);
                await BroadcastToAllChannels(
                    SignalingMessageFactory.CreateAllPeersStatusMessage(_channelManager.GetAllChannelStates()));
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down — expected
        }
    }

    private async Task BroadcastToAllChannels(SignalingMessage message)
    {
        var broadcastTasks = _clients.Values
            .Where(session => session.IsAuthenticated)
            .Select(async session =>
            {
                await SendToClient(session, message);

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(
                        "Broadcasting {MessageType} to {DisplayName} ({ClientId})",
                        message.Type, GetDisplayName(session), session.Id);
            });

        await Task.WhenAll(broadcastTasks);
    }

    public Task BroadcastServerSettingsAsync()
    {
        return BroadcastToAllChannels(
            SignalingMessageFactory.CreateServerSettings(
                _config.DcsLineOfSightEnabled, _config.SatcomEnabled));
    }

    private async Task BroadcastToChannel(int frequencyKhz, string excludeClientId, SignalingMessage message)
    {
        var clients = _channelManager.GetClientsInChannel(frequencyKhz);

        var broadcastTasks = clients
            .Where(clientId => clientId != excludeClientId)
            .Select(async clientId =>
            {
                if (_clients.TryGetValue(clientId, out var session))
                {
                    await SendToClient(session, message);

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug(
                            "Broadcasting {MessageType} to {DisplayName} ({ClientId}) in channel {Frequency:F3} MHz",
                            message.Type, GetDisplayName(session), clientId, frequencyKhz / 1000d);
                }
            });

        await Task.WhenAll(broadcastTasks);
    }

    private async Task SendToClient(ClientSession session, SignalingMessage message)
    {
        if (session.IsDisposed)
            return;

        if (session.WebSocket.State != WebSocketState.Open)
        {
            await CleanupClient(session.Id);
            return;
        }

        await session.SendLock.WaitAsync();
        try
        {
            if (session.WebSocket.State != WebSocketState.Open)
                return;

            var json = Json.Json.Instance.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(json);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(WebsocketTimeoutMillis));

            await session.WebSocket.SendAsync(
                new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            _logger.LogWarning(ex, "WebSocket error for {ClientId}", session.Id);
            await CleanupClient(session.Id);
        }
        finally
        {
            session.SendLock.Release();
        }
    }

    private async Task SendError(ClientSession session, string error)
    {
        await SendToClient(session, SignalingMessageFactory.CreateError(error));
    }

    private async Task SendSuccess(ClientSession session, string message, string? peerId = null, int? audioPort = null,
        bool opusEnabled = true)
    {
        await SendToClient(session,
            SignalingMessageFactory.CreateSuccess(message, _channelManager.GetAllChannelStates(), peerId, audioPort,
                opusEnabled, _config.DcsLineOfSightEnabled, _config.SatcomEnabled));
    }

    private async Task SendChannelState(ClientSession session, int frequencyKhz, List<ChannelStateMessage.Peer> peers)
    {
        await SendToClient(session, SignalingMessageFactory.CreateChannelState(frequencyKhz, peers));
    }

    private async Task CleanupClient(string clientId)
    {
        if (_clients.TryRemove(clientId, out var session))
        {
            await LeaveAllChannels(session);

            // Not redundant with the above: that awaits a broadcast per channel, and the
            // client's own message pump runs concurrently (this can be called fire-and-forget
            // from the idle watchdog), so a join can land in one of those gaps. Sweep again.
            _channelManager.LeaveAllChannels(clientId);
            session.FrequencyListenerPositions.Clear();
            _audioServer.RemoveSession(clientId);
            _satcom?.RemoveClient(clientId);
            _jamming?.ReportTransmitterGone(clientId);

            if (session.WebSocket.State == WebSocketState.Open)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await session.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Cleanup",
                        cts.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                    // Graceful close failed or timed out — force abort so client detects disconnect
                    session.WebSocket.Abort();
                }
            }

            session.Dispose();

            LogClientCleanedUp(_logger, GetDisplayName(session), clientId, null);
        }
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping signaling server...");

        // Stop audio server first
        _audioServer.Stop();
        _satcom?.Dispose();

        // Notify connected clients before cancelling so they receive a proper close frame.
        // CloseAsync (full handshake) ensures the frame is transmitted before we return —
        // the client's echo close frame proves delivery. Per-client 500ms timeout prevents
        // any single slow client from blocking shutdown.
        var closeTasks = _clients.Values
            .Where(s => s.WebSocket.State == WebSocketState.Open)
            .Select(async s =>
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await s.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Server shutting down",
                        closeCts.Token);
                }
                catch
                {
                    // don't care
                }
            });
        try
        {
            await Task.WhenAll(closeTasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // don't care
        }

        // Cancel the CTS
        _cts.Cancel();
        _cts.Dispose();

        // Stop the web app
        if (_app != null)
        {
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await _app.StopAsync(shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("App shutdown timed out");
            }

            await _app.DisposeAsync();
        }

        _logger.LogInformation("Signaling server stopped");
    }
}
