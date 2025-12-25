using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;

namespace OpenFreq.Server;

public class AudioStreamServer
{
    private readonly ConcurrentDictionary<string, AudioStreamSession> _sessions = new();
    private readonly FrequencyChannelManager _channelManager;
    private readonly ConcurrentDictionary<string, ClientSession> _clients;
    private readonly ILogger<AudioStreamServer> _logger;
    private readonly UdpStreamManager _udpManager;
    private readonly int _basePort;
    private int _nextPortOffset = 0; // Thread-safe atomic counter
    private CancellationTokenSource _cts = new();

    // High-performance logging delegates
    private static readonly Action<ILogger, string, int, Exception?> _logAudioSessionCreated =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(1, nameof(CreateAudioSession)),
            "Created audio session for client {ClientId} on port {Port}");

    private static readonly Action<ILogger, string, int, Exception?> _logTransmittingOnFrequencies =
        LoggerMessage.Define<string, int>(
            LogLevel.Debug,
            new EventId(2, nameof(ForwardAudioToChannel)),
            "Client {ClientId} transmitting on {FrequencyCount} frequency(ies)");

    private static readonly Action<ILogger, string, Exception?> _logSessionRemoved =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(4, nameof(RemoveSession)),
            "Removed audio session for client {ClientId}");

    public AudioStreamServer(
        FrequencyChannelManager channelManager,
        ConcurrentDictionary<string, ClientSession> clients,
        ILoggerFactory loggerFactory,
        int basePort = 10000)
    {
        _channelManager = channelManager;
        _clients = clients;
        _logger = loggerFactory.CreateLogger<AudioStreamServer>();
        _basePort = basePort;
        _udpManager = new UdpStreamManager(loggerFactory.CreateLogger<UdpStreamManager>());
    }

    public async Task<int> CreateAudioSession(string clientId)
    {
        const int maxRetries = 10;
        UdpClient? udpClient = null;
        int port = 0;
        
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                // Atomically get next port offset (thread-safe)
                var portOffset = Interlocked.Increment(ref _nextPortOffset) - 1;
                port = _basePort + portOffset;
                udpClient = _udpManager.CreateUdpClient(clientId, port);
                
                // Successfully created - break out of retry loop
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                _logger.LogWarning("Port {Port} already in use (attempt {Attempt}/{MaxRetries}), trying next port", 
                    port, attempt + 1, maxRetries);
                
                udpClient?.Dispose();
                udpClient = null;
                
                if (attempt == maxRetries - 1)
                {
                    throw new InvalidOperationException(
                        $"Failed to allocate UDP port after {maxRetries} attempts. Base port: {_basePort}, last attempted: {port}", 
                        ex);
                }
            }
        }
        
        if (udpClient == null)
        {
            throw new InvalidOperationException("Failed to create UDP client");
        }
        
        var session = new AudioStreamSession
        {
            ClientId = clientId,
            Port = port,
            UdpClient = udpClient
        };

        _sessions[clientId] = session;

        _ = Task.Run(() => ReceiveAudioLoop(clientId, udpClient));

        _logAudioSessionCreated(_logger, clientId, port, null);
        return port;
    }

    private async Task ReceiveAudioLoop(string clientId, UdpClient udpClient)
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await udpClient.ReceiveAsync(_cts.Token);
                
                if (!_sessions.TryGetValue(clientId, out var session))
                    break;

                session.LastReceived = DateTime.UtcNow;
                session.RemoteEndPoint = result.RemoteEndPoint;

                // Parse packet with metadata
                var (metadata, audioData) = ParseAudioPacket(result.Buffer);
                
                if (metadata == null || audioData == null)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Client {ClientId} sent malformed audio packet, skipping", clientId);
                    continue;
                }

                if (metadata.Frequencies == null || metadata.Frequencies.Count == 0)
                    continue;

                // Verify client has joined all specified frequencies
                if (!_clients.TryGetValue(clientId, out var clientSession))
                    continue;

                var validFrequencies = metadata.Frequencies
                    .Where(freq => clientSession.CurrentFrequencies.ContainsKey(freq))
                    .ToList();

                if (validFrequencies.Count < metadata.Frequencies.Count)
                {
                    var invalid = metadata.Frequencies.Except(validFrequencies).ToList();
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Client {ClientId} attempted to transmit on unjoined frequencies: {Frequencies}", 
                            clientId, string.Join(", ", invalid));
                }

                if (validFrequencies.Count == 0)
                    continue;

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logTransmittingOnFrequencies(_logger, clientId, validFrequencies.Count, null);
                
                // Build packet once, reuse for all recipients
                var forwardPacket = CreateAudioPacket(metadata, audioData);
                
                // Forward audio to all specified frequencies
                foreach (var frequency in validFrequencies)
                {
                    ForwardAudioToChannel(frequency, clientId, forwardPacket);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in audio receive loop for client {ClientId}", clientId);
        }
    }

    /// <summary>
    /// Parses a UDP audio packet with metadata header
    /// Packet format: [2 bytes: header length][N bytes: JSON metadata][remaining: audio data]
    /// </summary>
    private (AudioPacketMetadata? metadata, byte[]? audioData) ParseAudioPacket(byte[] packet)
    {
        try
        {
            // Minimum packet size: 2 bytes header length + at least some metadata
            if (packet.Length < 4)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Packet too small: {Length} bytes", packet.Length);
                return (null, null);
            }

            // Read header length (first 2 bytes, big-endian)
            var headerLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(0, 2));            
            
            // Validate header length
            if (headerLength > packet.Length - 2)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Invalid header length: {HeaderLength}, packet size: {PacketLength}", 
                        headerLength, packet.Length);
                return (null, null);
            }

            // Extract metadata JSON
            var metadataBytes = new byte[headerLength];
            Array.Copy(packet, 2, metadataBytes, 0, headerLength);
            var metadataJson = Encoding.UTF8.GetString(metadataBytes);
            
            // Parse metadata
            var metadata = JsonSerializer.Deserialize<AudioPacketMetadata>(metadataJson);
            if (metadata == null)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Failed to deserialize metadata");
                return (null, null);
            }

            // Extract audio data (everything after header)
            var audioDataLength = packet.Length - 2 - headerLength;
            var audioData = new byte[audioDataLength];
            Array.Copy(packet, 2 + headerLength, audioData, 0, audioDataLength);

            return (metadata, audioData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing audio packet");
            return (null, null);
        }
    }

    /// <summary>
    /// Creates a UDP audio packet with metadata header
    /// Packet format: [2 bytes: header length][N bytes: JSON metadata][remaining: audio data]
    /// </summary>
    private static byte[] CreateAudioPacket(AudioPacketMetadata metadata, byte[] audioData)
    {
        var metadataJson = JsonSerializer.Serialize(metadata);
        var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
        var headerLength = (ushort)metadataBytes.Length;

        // Build packet: [2 bytes header length][metadata][audio data]
        var packet = new byte[2 + metadataBytes.Length + audioData.Length];
        
        // Write header length (big-endian)
        packet[0] = (byte)(headerLength >> 8);
        packet[1] = (byte)(headerLength & 0xFF);
        
        // Write metadata
        Array.Copy(metadataBytes, 0, packet, 2, metadataBytes.Length);
        
        // Write audio data
        Array.Copy(audioData, 0, packet, 2 + metadataBytes.Length, audioData.Length);

        return packet;
    }

    /// <summary>
    /// Forward pre-built audio packet to all clients in a channel
    /// Packets are dropped if a client can't keep up, preventing compounding delay
    /// </summary>
    private void ForwardAudioToChannel(
        double frequencyMhz, 
        string sourceClientId, 
        byte[] preBuiltPacket)
    {
        var clients = _channelManager.GetClientsInChannel(frequencyMhz);

        foreach (var clientId in clients)
        {
            if (clientId == sourceClientId) 
                continue;

            if (_sessions.TryGetValue(clientId, out var targetSession) && 
                targetSession.RemoteEndPoint != null)
            {
                // Returns false if packet was dropped due to congestion
                // This prevents buffer buildup and compounding delays
                var sent = _udpManager.SendPacketAsync(
                    clientId, 
                    targetSession.UdpClient, 
                    preBuiltPacket, 
                    targetSession.RemoteEndPoint);

                if (!sent && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Packet dropped for client {ClientId} due to congestion", clientId);
                }
            }
        }
    }

    public void RemoveSession(string clientId)
    {
        if (_sessions.TryRemove(clientId, out var session))
        {
            _udpManager.RemoveClient(clientId);
            session.UdpClient.Close();
            session.UdpClient.Dispose();
            _logSessionRemoved(_logger, clientId, null);
        }
    }

    public ClientStreamStats? GetClientStats(string clientId)
    {
        return _udpManager.GetStats(clientId);
    }

    public void Stop()
    {
        _cts.Cancel();
        
        foreach (var session in _sessions.Values)
        {
            _udpManager.RemoveClient(session.ClientId);
            session.UdpClient.Close();
            session.UdpClient.Dispose();
        }
        
        _sessions.Clear();
    }
}

public class AudioStreamSession
{
    public string ClientId { get; set; } = string.Empty;
    public int Port { get; set; }
    public UdpClient UdpClient { get; set; } = null!;
    public IPEndPoint? RemoteEndPoint { get; set; }
    public DateTime LastReceived { get; set; } = DateTime.UtcNow;
}