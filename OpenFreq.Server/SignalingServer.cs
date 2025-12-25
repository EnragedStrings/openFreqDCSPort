using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Signaling;
using OpenFreq.Server;

namespace OpenFreq.Server;

public class SignalingServer
{
    private readonly HttpListener _httpListener;
    private readonly ServerConfig _config;
    private readonly ConcurrentDictionary<string, ClientSession> _clients = new();
    private readonly FrequencyChannelManager _channelManager = new();
    private readonly AudioStreamServer _audioServer;
    private readonly ILogger<SignalingServer> _logger;
    private CancellationTokenSource _cts = new();

    // High-performance logging delegates
    private static readonly Action<ILogger, int, Exception?> _logServerStarted =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1, nameof(StartAsync)),
            "OpenFreqServer listening on port {Port}");

    private static readonly Action<ILogger, string, Exception?> _logClientConnected =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(HandleWebSocketConnection)),
            "Client {ClientId} connected");

    private static readonly Action<ILogger, string, int, Exception?> _logClientAuthenticated =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(3, nameof(HandleAuthenticate)),
            "Client {ClientId} authenticated, audio port {AudioPort}");

    private static readonly Action<ILogger, string, double, Exception?> _logClientJoinedFrequency =
        LoggerMessage.Define<string, double>(
            LogLevel.Information,
            new EventId(4, nameof(HandleJoinChannel)),
            "Client {ClientId} joined frequency {Frequency}");

    private static readonly Action<ILogger, string, double, Exception?> _logClientLeftFrequency =
        LoggerMessage.Define<string, double>(
            LogLevel.Information,
            new EventId(5, nameof(LeaveCurrentChannel)),
            "Client {ClientId} left frequency {Frequency}");

    private static readonly Action<ILogger, string, bool, double, int, Exception?> _logTransmissionState =
        LoggerMessage.Define<string, bool, double, int>(
            LogLevel.Information,
            new EventId(6, nameof(HandleTransmission)),
            "Client {ClientId} transmission state: {IsTransmitting} on frequency {Frequency}, broadcasting to {PeerCount} peer(s)");

    private static readonly Action<ILogger, string, Exception?> _logClientCleanedUp =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(7, nameof(CleanupClient)),
            "Client {ClientId} cleaned up");

    public ConcurrentDictionary<string, ClientSession> Clients => _clients;
    public FrequencyChannelManager ChannelManager => _channelManager;

    public SignalingServer(ServerConfig config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger<SignalingServer>();
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://+:{config.WebSocketPort}/");
        _audioServer = new AudioStreamServer(_channelManager, _clients, loggerFactory, config.AudioBasePort);
    }

    public async Task StartAsync()
    {
        _httpListener.Start();
        _logServerStarted(_logger, _config.WebSocketPort, null);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();

                if (context.Request.IsWebSocketRequest)
                {
                    _ = Task.Run(() => HandleWebSocketConnection(context));
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting connection");
            }
        }
    }

    private async Task HandleWebSocketConnection(HttpListenerContext context)
    {
        WebSocketContext? wsContext = null;
        WebSocket? webSocket = null;
        string clientId = Guid.NewGuid().ToString();

        try
        {
            wsContext = await context.AcceptWebSocketAsync(null);
            webSocket = wsContext.WebSocket;

            var session = new ClientSession(clientId, webSocket);
            _clients[clientId] = session;

            _logClientConnected(_logger, clientId, null);

            await HandleClientMessages(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WebSocket connection for client {ClientId}", clientId);
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
            while (session.WebSocket.State == WebSocketState.Open)
            {
                var result = await session.WebSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await session.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None);
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
        catch (WebSocketException)
        {
            // Connection closed
        }
    }

    private async Task ProcessMessage(ClientSession session, string messageText)
    {
        try
        {
            var message = JsonSerializer.Deserialize<SignalingMessage>(messageText);
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

                default:
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Unknown message type: {MessageType}", message.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
            await SendError(session, $"Invalid message format {ex.Message}");
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

        if (string.IsNullOrEmpty(_config.ServerPassword) ||
            authMsg.Password == _config.ServerPassword)
        {
            session.IsAuthenticated = true;

            // Create audio session for this client
            var audioPort = await _audioServer.CreateAudioSession(session.Id);
            session.AudioPort = audioPort;

            await SendSuccess(session, "Authenticated successfully", session.Id, audioPort);
            _logClientAuthenticated(_logger, session.Id, audioPort, null);
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Client {ClientId} sent invalid password, closing connection", session.Id);
            
            await SendError(session, "Invalid password");
            _ = session.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Invalid password", CancellationToken.None);
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

        var channelCount = _channelManager.GetChannelCount(joinMsg.FrequencyMhz);

        if (channelCount >= _config.MaxClientsPerChannel)
        {
            await SendError(session, "Channel is full");
            return;
        }

        if (session.CurrentFrequencies.ContainsKey(joinMsg.FrequencyMhz))
        {
            await SendError(session, "Frequency already joined");
            return;
        }

        _channelManager.JoinChannel(joinMsg.FrequencyMhz, session.Id);
        
        session.CurrentFrequencies.TryAdd(joinMsg.FrequencyMhz, ClientSession.FrequencyClientStatus.Receiving);

        var peers = _channelManager.GetClientsInChannel(joinMsg.FrequencyMhz)
            .Where(id => id != session.Id)
            .ToList();

        await SendChannelState(session, joinMsg.FrequencyMhz, peers);

        BroadcastToChannel(
            joinMsg.FrequencyMhz,
            session.Id,
            SignalingMessageFactory.CreatePeerJoined(session.Id, joinMsg.FrequencyMhz));

        _logClientJoinedFrequency(_logger, session.Id, joinMsg.FrequencyMhz, null);
    }

    private async Task HandleLeaveChannel(ClientSession session, SignalingMessage message)
    {
        await LeaveCurrentChannel(session, message);
    }

    private async Task LeaveCurrentChannel(ClientSession session, SignalingMessage message)
    {
        if (session.CurrentFrequencies.Count == 0) return;

        var transmissionMsg = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(message.Payload);
        if (transmissionMsg == null) return;

        var frequencyMhz = transmissionMsg.FrequencyMhz;
        _channelManager.LeaveChannel(frequencyMhz, session.Id);
        
        BroadcastToChannel(
            frequencyMhz,
            session.Id,
            SignalingMessageFactory.CreatePeerLeft(session.Id, frequencyMhz));

        session.CurrentFrequencies.TryRemove(frequencyMhz, out var frequencyClientStatus);
        _logClientLeftFrequency(_logger, session.Id, frequencyMhz, null);
    }

    private async Task LeaveAllChannels(ClientSession session)
    {
        // ToArray to avoid modification during enumeration
        var frequencies = session.CurrentFrequencies.Keys.ToArray();
        
        foreach (var frequency in frequencies)
        {
            _channelManager.LeaveChannel(frequency, session.Id);

            BroadcastToChannel(
                frequency,
                session.Id,
                SignalingMessageFactory.CreatePeerLeft(session.Id, frequency));

            session.CurrentFrequencies.TryRemove(frequency, out var frequencyClientStatus);
            _logClientLeftFrequency(_logger, session.Id, frequency, null);
        }
    }

    private async Task HandleTransmission(ClientSession session, SignalingMessage message)
    {
        if (!session.IsAuthenticated) return;
        if (session.CurrentFrequencies.Count == 0) return;

        var transmissionMsg = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(message.Payload);
        if (transmissionMsg == null) return;

        if (session.CurrentFrequencies.TryGetValue(transmissionMsg.FrequencyMhz,
                out ClientSession.FrequencyClientStatus frequencyStatus))
        {
            session.CurrentFrequencies.TryUpdate(transmissionMsg.FrequencyMhz,
                transmissionMsg.Transmitting
                    ? ClientSession.FrequencyClientStatus.Transmitting
                    : ClientSession.FrequencyClientStatus.Receiving, frequencyStatus);
        }
        else return;

        if (!session.CurrentFrequencies.ContainsKey(transmissionMsg.FrequencyMhz)) return;

        var peersInChannel = _channelManager.GetClientsInChannel(transmissionMsg.FrequencyMhz)
            .Where(id => id != session.Id)
            .ToArray();

        _logTransmissionState(_logger, session.Id, transmissionMsg.Transmitting, 
            transmissionMsg.FrequencyMhz, peersInChannel.Length, null);

        // Broadcast transmission state to ALL other peers in the channel
        BroadcastToChannel(
            transmissionMsg.FrequencyMhz,
            session.Id,
            SignalingMessageFactory.CreateTransmissionEvent(
                session.Id,
                transmissionMsg.FrequencyMhz,
                transmissionMsg.Transmitting));
    }

    /// <summary>
    /// </summary>
    private void BroadcastToChannel(double frequencyMhz, string excludeClientId, SignalingMessage message)
    {
        var clients = _channelManager.GetClientsInChannel(frequencyMhz);

        foreach (var clientId in clients)
        {
            if (clientId == excludeClientId) continue;

            if (_clients.TryGetValue(clientId, out var session))
            {
                // Fire-and-forget: Don't await, don't block on slow clients
                _ = SendToClient(session, message)
                    .ContinueWith(t =>
                    {
                        if (!t.IsFaulted || t.Exception == null) return;
                        var ex = t.Exception.GetBaseException();
                        _logger.LogError(ex, "Error broadcasting to client {ClientId} in channel {Frequency}", 
                            clientId, frequencyMhz);
                    }, TaskScheduler.Default);

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Broadcasting {MessageType} to client {ClientId} in channel {Frequency}", 
                        message.Type, clientId, frequencyMhz);
            }
        }
    }

    private async Task SendToClient(ClientSession session, SignalingMessage message)
    {
        if (session.WebSocket.State == WebSocketState.Open)
        {
            var json = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(json);
            await session.WebSocket.SendAsync(
                new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }
    }

    private async Task SendError(ClientSession session, string error)
    {
        await SendToClient(session, SignalingMessageFactory.CreateError(error));
    }

    private async Task SendSuccess(ClientSession session, string message, string? peerId = null, int? audioPort = null)
    {
        await SendToClient(session, SignalingMessageFactory.CreateSuccess(message, peerId, audioPort));
    }

    private async Task SendChannelState(ClientSession session, double frequencyMhz, List<string> peers)
    {
        await SendToClient(session, SignalingMessageFactory.CreateChannelState(frequencyMhz, peers));
    }

    private async Task CleanupClient(string clientId)
    {
        if (_clients.TryRemove(clientId, out var session))
        {
            await LeaveAllChannels(session);

            _channelManager.LeaveAllChannels(clientId);
            _audioServer.RemoveSession(clientId);

            if (session.WebSocket.State == WebSocketState.Open)
            {
                await session.WebSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Cleanup",
                    CancellationToken.None);
            }

            session.WebSocket.Dispose();

            _logClientCleanedUp(_logger, clientId, null);
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        _audioServer.Stop();
        _httpListener.Stop();
        _httpListener.Close();
    }
}