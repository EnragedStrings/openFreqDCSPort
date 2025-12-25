using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Signaling;
using OpenFreqClient;
using OpusSharp.Core;

namespace OpenFreq.Common;

public class OpenFreqRtcClient : IDisposable
{
    // Audio configuration constants
    public const int SAMPLE_RATE = 48000;
    public const int CHANNELS = 1;
    public const int FRAME_SIZE_MS = 20;
    public const int OPUS_SAMPLES_PER_FRAME = SAMPLE_RATE / (1000 / FRAME_SIZE_MS) * CHANNELS;
    public const int DEFAULT_PORT = 9987;

    // Events for UI integration
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<AuthenticationEventArgs>? Authenticated;
    public event EventHandler<FrequencyJoinedEventArgs>? FrequencyJoined;
    public event EventHandler<FrequencyLeftEventArgs>? FrequencyLeft;
    public event EventHandler<PeerEventArgs>? PeerJoined;
    public event EventHandler<PeerEventArgs>? PeerLeft;
    public event EventHandler<TransmissionStateEventArgs>? TransmissionStateChanged;
    public event EventHandler<PeerTransmissionEventArgs>? PeerTransmissionStateChanged;
    public event EventHandler<AudioDataEventArgs>? AudioDataReceived;
    public event EventHandler<ErrorEventArgs>? ErrorOccurred;
    
    private OpusEncoder _opusEncoder;
    private OpusDecoder _opusDecoder;
    private byte[] _opusBuffer = new byte[4000];
    public const int OPUS_FRAME_SIZE = 960;

    // Connection state
    private readonly string _serverIp;
    private readonly string _password;
    private ClientWebSocket? _webSocket;
    private UdpClient? _audioClient;
    private IPEndPoint? _serverAudioEndpoint;
    private string? _myPeerId;
    private int _audioPort;
    private bool _isConnected;
    private bool _isAuthenticated;
    private readonly CancellationTokenSource _cts = new();
    private string clientId = Guid.NewGuid().ToString();

    // Transmission state
    private readonly Dictionary<double, bool> _frequencyTransmissionState = new();
    private readonly Dictionary<double, HashSet<string>> _frequencyPeers = new();

    // Aircraft position state (thread-safe)
    private readonly object _positionLock = new();
    private AircraftPosition _currentPosition = new();
    private readonly ILogger<OpenFreqRtcClient> _logger;

    // Properties
    public string? MyPeerId => _myPeerId;
    public int AudioPort => _audioPort;
    public bool IsConnected => _isConnected;
    public bool IsAuthenticated => _isAuthenticated;
    public IReadOnlyDictionary<double, bool> FrequencyTransmissionState => _frequencyTransmissionState;

    public OpenFreqRtcClient(ILogger<OpenFreqRtcClient> logger, string serverIp, string password)
    {
        _logger = logger;
        _serverIp = serverIp;
        _password = password;
        _opusEncoder = new OpusEncoder(SAMPLE_RATE, CHANNELS, OpusPredefinedValues.OPUS_APPLICATION_AUDIO);
        _opusDecoder = new OpusDecoder(SAMPLE_RATE, CHANNELS);
    }

    /// <summary>
    /// Set the aircraft position for UDP packet metadata
    /// Thread-safe - can be called from any service (ACMI, BMS, etc.)
    /// </summary>
    public void SetPosition(double x, double y, double z)
    {
        lock (_positionLock)
        {
            _currentPosition.X = x;
            _currentPosition.Y = y;
            _currentPosition.Z = z;
        }
    }

    /// <summary>
    /// Get the current aircraft position
    /// </summary>
    public AircraftPosition GetPosition()
    {
        lock (_positionLock)
        {
            return new AircraftPosition
            {
                X = _currentPosition.X,
                Y = _currentPosition.Y,
                Z = _currentPosition.Z
            };
        }
    }

    /// <summary>
    /// Connect to the OpenFreq server and authenticate
    /// </summary>
    public async Task ConnectAsync()
    {
        try
        {
            // Connect WebSocket
            var ipPort = Util.ResolveAddress(_serverIp, DEFAULT_PORT);

            // we need to wrap IPv6 into [] for a valid URI
            IPAddress? ip;
            if (IPAddress.TryParse(ipPort.ipAddress, out ip))
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    ipPort.ipAddress = $"[{ipPort.ipAddress}]"; // Wrap IPv6 address in square brackets
                }
            }

            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(new Uri($"ws://{ipPort.ipAddress}:{ipPort.port}"), _cts.Token);

            _isConnected = true;
            OnConnectionStateChanged(ConnectionState.Connected);

            // Start message receiver
            _ = Task.Run(ReceiveMessagesAsync, _cts.Token);

            // Authenticate
            await SendMessageAsync(SignalingMessageFactory.CreateAuthenticate(_password));

            // Wait for authentication response with timeout
            var authWaitTask = Task.Delay(5000, _cts.Token);
            var startTime = DateTime.UtcNow;
            while (!_isAuthenticated && (DateTime.UtcNow - startTime).TotalSeconds < 5)
            {
                await Task.Delay(100, _cts.Token);
            }

            if (!_isAuthenticated)
            {
                throw new TimeoutException("Authentication timeout");
            }

            // Setup audio UDP client
            _audioClient = new UdpClient();
            _serverAudioEndpoint = new IPEndPoint(IPAddress.Parse(_serverIp), _audioPort);

            // Send initial packet to establish NAT connection
            await _audioClient.SendAsync(new byte[] { 0 }, 1, _serverAudioEndpoint);

            // Start audio receiver
            _ = Task.Run(ReceiveAudioAsync, _cts.Token);
        }
        catch (Exception ex)
        {
            _isConnected = false;
            OnConnectionStateChanged(ConnectionState.Disconnected);
            OnError($"Connection failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Join a frequency channel
    /// </summary>
    public async Task JoinFrequencyAsync(double frequency)
    {
        if (!_isAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        await SendMessageAsync(SignalingMessageFactory.CreateJoin(frequency));

        if (!_frequencyPeers.ContainsKey(frequency))
        {
            _frequencyPeers[frequency] = new HashSet<string>();
        }

        _frequencyTransmissionState[frequency] = false;
    }

    /// <summary>
    /// Leave a frequency channel
    /// </summary>
    public async Task LeaveFrequencyAsync(double frequency)
    {
        if (!_isAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        await SendMessageAsync(SignalingMessageFactory.CreateLeave(frequency));

        _frequencyPeers.Remove(frequency);
        _frequencyTransmissionState.Remove(frequency);
        OnFrequencyLeft(frequency);
    }

    /// <summary>
    /// Start transmitting on a frequency
    /// </summary>
    public async Task StartTransmissionAsync(double frequencyMhz)
    {
        if (!_isAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        if (!_frequencyTransmissionState.ContainsKey(frequencyMhz))
        {
            throw new InvalidOperationException($"Not joined to frequency {frequencyMhz}");
        }

        _frequencyTransmissionState[frequencyMhz] = true;
        await SendMessageAsync(SignalingMessageFactory.CreateTransmission(frequencyMhz, true));
        OnTransmissionStateChanged(frequencyMhz, true);

        // Start heartbeat for this frequency
        _ = Task.Run(() => TransmissionHeartbeatAsync(frequencyMhz), _cts.Token);
    }

    /// <summary>
    /// Stop transmitting on a frequency
    /// </summary>
    public async Task StopTransmissionAsync(double frequencyMhz)
    {
        if (!_isAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        if (!_frequencyTransmissionState.ContainsKey(frequencyMhz))
        {
            return;
        }

        _frequencyTransmissionState[frequencyMhz] = false;
        await SendMessageAsync(SignalingMessageFactory.CreateTransmission(frequencyMhz, false));
        OnTransmissionStateChanged(frequencyMhz, false);
    }

    /// <summary>
    /// Send audio data to the server with packet metadata
    /// </summary>
    public async Task SendAudioDataAsync(byte[] audioData)
    {
        if (_audioClient == null || _serverAudioEndpoint == null)
        {
            return;
        }

        try
        {
            // Get list of frequencies currently transmitting
            var transmittingFrequencies = _frequencyTransmissionState
                .Where(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .ToList();

            // Only send if transmitting on at least one frequency
            if (transmittingFrequencies.Count == 0)
            {
                return;
            }

            // Create packet with metadata (includes position)
            var packet = CreateAudioPacket(transmittingFrequencies, audioData);

            // Send packet
            await _audioClient.SendAsync(packet, packet.Length, _serverAudioEndpoint);
        }
        catch (Exception ex)
        {
            OnError($"Error sending audio data: {ex.Message}");
        }
    }

    /// <summary>
    /// Send audio data synchronously (for callbacks) with packet metadata
    /// </summary>
    public void SendAudioDataSync(List<double> frequencies, byte[] audioData)
    {
        if (_audioClient == null || _serverAudioEndpoint == null || frequencies.Count == 0)
        {
            Console.WriteLine($"[SEND] Skipped - client:{_audioClient != null}, endpoint:{_serverAudioEndpoint != null}, freq:{frequencies.Count}"); // 👈
            return;
        }

        try
        {
            // Encode PCM data to Opus
            byte[] opusData = new byte[1920];
        
            int opusPacketSize = _opusEncoder.Encode(audioData, OPUS_SAMPLES_PER_FRAME, opusData, opusData.Length);

            Console.Out.WriteLine($"Opus Encoded {audioData.Length} -> {opusPacketSize}");
            if (opusPacketSize > 0)
            {
                // Create packet with only the actual encoded bytes
                var packet = CreateAudioPacket(frequencies, new ArraySegment<byte>(opusData, 0, opusPacketSize).ToArray());

                Console.WriteLine($"[SEND] Sending {packet.Length} bytes to {_serverAudioEndpoint}"); // 👈
            
                // Send packet
                int bytesSent = _audioClient.Send(packet, packet.Length, _serverAudioEndpoint);
                Console.WriteLine($"[SEND] Actually sent {bytesSent} bytes"); // 👈
            }
        }
        catch (Exception ex)
        {
            OnError($"Error sending audio data: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a UDP audio packet with metadata header including position
    /// Packet format: [2 bytes: header length][N bytes: JSON metadata][remaining: audio data]
    /// </summary>
    private byte[] CreateAudioPacket(List<double> frequencies, byte[] audioData)
    {
        // Get current position (thread-safe)
        AircraftPosition position;
        lock (_positionLock)
        {
            position = new AircraftPosition
            {
                X = _currentPosition.X,
                Y = _currentPosition.Y,
                Z = _currentPosition.Z
            };
        }

        var metadata = new AudioPacketMetadata
        {
            clientId = _myPeerId,
            Frequencies = frequencies,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Position = position
        };

        var metadataJson = JsonSerializer.Serialize(metadata);
        var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
        var headerLength = (ushort)metadataBytes.Length;

        // Build packet: [2 bytes header length][metadata][audio data]
        var packet = new byte[2 + metadataBytes.Length + audioData.Length];

        // Write header length (BIG-ENDIAN)
        packet[0] = (byte)(headerLength >> 8);
        packet[1] = (byte)(headerLength & 0xFF);

        // Write metadata
        Array.Copy(metadataBytes, 0, packet, 2, metadataBytes.Length);

        // Write audio data
        Array.Copy(audioData, 0, packet, 2 + metadataBytes.Length, audioData.Length);

        return packet;
    }

    /// <summary>
    /// Disconnect from the server
    /// </summary>
    public async Task DisconnectAsync()
    {
        _cts.Cancel();

        if (_webSocket?.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting",
                CancellationToken.None);
        }

        _isConnected = false;
        _isAuthenticated = false;
        OnConnectionStateChanged(ConnectionState.Disconnected);
    }

    private async Task TransmissionHeartbeatAsync(double frequency)
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            if (!_frequencyTransmissionState.TryGetValue(frequency, out var isTransmitting) || !isTransmitting)
            {
                break;
            }

            await SendMessageAsync(SignalingMessageFactory.CreateTransmission(frequency, true));
            await Task.Delay(333, _cts.Token); // ~3 times per second
        }
    }

    private async Task ReceiveAudioAsync()
    {
        _logger.LogDebug("ReceiveAudioAsync() started"); // 👈 Verify task is running
        if (_audioClient == null) return;

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _audioClient.ReceiveAsync(_cts.Token);
            
                if (result.Buffer.Length < 2)
                {
                    continue; // Packet too small
                }

                // Read header length (BIG-ENDIAN)
                ushort headerLength = (ushort)((result.Buffer[0] << 8) | result.Buffer[1]);
                if (result.Buffer.Length < 2 + headerLength)
                {
                    Console.WriteLine(
                        $"[CLIENT] Packet incomplete: header={headerLength}, packet={result.Buffer.Length}");
                    continue; // Packet incomplete
                }

                // Parse metadata from JSON
                AircraftPosition? senderPosition = null;
                double frequency = 0;
                string peerId = string.Empty;
                try
                {
                    // Extract metadata JSON
                    string metadataJson = Encoding.UTF8.GetString(result.Buffer, 2, headerLength);
                    var metadata = JsonSerializer.Deserialize<AudioPacketMetadata>(metadataJson);

                    if (metadata != null)
                    {
                        peerId = metadata.clientId;
                        senderPosition = metadata.Position;
                        // Use first frequency if available
                        if (metadata.Frequencies != null && metadata.Frequencies.Count > 0)
                        {
                            frequency = metadata.Frequencies[0];
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse metadata: {ex.Message}");
                    continue; // Skip malformed packets
                }

                // Extract audio data (skip header length and metadata)
                int audioDataStart = 2 + headerLength;
                int audioDataLength = result.Buffer.Length - audioDataStart;

                if (audioDataLength > 0)
                {
                    byte[] opusAudioData = new byte[audioDataLength];
                    Array.Copy(result.Buffer, audioDataStart, opusAudioData, 0, audioDataLength);

                    var decoded = new byte[OPUS_SAMPLES_PER_FRAME * 2]; // 👈 Fix: Should be 1920 bytes (960 samples * 2 bytes)
                    _opusDecoder.Decode(opusAudioData, audioDataLength, decoded, OPUS_SAMPLES_PER_FRAME, false);
                    
                    // Pass audio data WITH position and frequency metadata
                    OnAudioDataReceived(peerId, decoded, senderPosition, frequency);
                }
            }

            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                OnError($"Error receiving audio: {ex.Message}");
            }
        }
    }

    private async Task ReceiveMessagesAsync()
    {
        if (_webSocket == null) return;

        var buffer = new byte[8192];
        var messageBuffer = new StringBuilder();

        try
        {
            while (_webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing",
                        CancellationToken.None);
                    _isConnected = false;
                    OnConnectionStateChanged(ConnectionState.Disconnected);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        var message = messageBuffer.ToString();
                        messageBuffer.Clear();
                        HandleMessage(message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            OnError($"WebSocket error: {ex.Message}");
            _isConnected = false;
            OnConnectionStateChanged(ConnectionState.Disconnected);
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<SignalingMessage>(json);
            if (message == null) return;

            switch (message.Type)
            {
                case SignalingMessageTypes.Success:
                    var success = SignalingMessageFactory.DeserializePayload<SuccessMessage>(message.Payload);
                    if (success != null)
                    {
                        if (success.PeerId != null)
                        {
                            _myPeerId = success.PeerId;
                            _audioPort = success.AudioPort ?? 0;
                            _isAuthenticated = true;
                            OnConnectionStateChanged(ConnectionState.Authenticated);
                            OnAuthenticated(_myPeerId, _audioPort);
                        }
                    }

                    break;

                case SignalingMessageTypes.Error:
                    var error = SignalingMessageFactory.DeserializePayload<ErrorMessage>(message.Payload);
                    if (error != null)
                    {
                        OnError(error.Error);
                    }

                    break;

                case SignalingMessageTypes.PeerJoined:
                    var joined = SignalingMessageFactory.DeserializePayload<PeerJoinedMessage>(message.Payload);
                    if (joined != null)
                    {
                        if (_frequencyPeers.TryGetValue(joined.FrequencyMhz, out var peers))
                        {
                            peers.Add(joined.PeerId);
                        }

                        OnPeerJoined(joined.PeerId, joined.FrequencyMhz);
                    }

                    break;

                case SignalingMessageTypes.PeerLeft:
                    var left = SignalingMessageFactory.DeserializePayload<PeerLeftMessage>(message.Payload);
                    if (left != null)
                    {
                        if (_frequencyPeers.TryGetValue(left.FrequencyMhz, out var peers))
                        {
                            peers.Remove(left.PeerId);
                        }

                        OnPeerLeft(left.PeerId, left.FrequencyMhz);
                    }

                    break;

                case SignalingMessageTypes.Transmission:
                    var transmission =
                        SignalingMessageFactory.DeserializePayload<TransmissionEventMessage>(message.Payload);
                    if (transmission != null && transmission.PeerId != _myPeerId)
                    {
                        OnPeerTransmissionStateChanged(transmission.PeerId, transmission.FrequencyMhz,
                            transmission.Transmitting);
                    }

                    break;

                case SignalingMessageTypes.ChannelState:
                    var channelState = SignalingMessageFactory.DeserializePayload<ChannelStateMessage>(message.Payload);
                    if (channelState != null)
                    {
                        _frequencyPeers[channelState.FrequencyMhz] = new HashSet<string>(channelState.Peers);
                        OnFrequencyJoined(channelState.FrequencyMhz, channelState.Peers);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            OnError($"Error handling message: {ex.Message}");
        }
    }

    private async Task SendMessageAsync(SignalingMessage message)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        try
        {
            var json = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (Exception ex)
        {
            OnError($"Error sending message: {ex.Message}");
        }
    }

    // Event raising methods
    private void OnConnectionStateChanged(ConnectionState state) =>
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state));

    private void OnAuthenticated(string peerId, int audioPort) =>
        Authenticated?.Invoke(this, new AuthenticationEventArgs(peerId, audioPort));

    private void OnFrequencyJoined(double frequencyMhz, List<string> peers) =>
        FrequencyJoined?.Invoke(this, new FrequencyJoinedEventArgs(frequencyMhz, peers));

    private void OnFrequencyLeft(double frequencyMhz) =>
        FrequencyLeft?.Invoke(this, new FrequencyLeftEventArgs(frequencyMhz));

    private void OnPeerJoined(string peerId, double frequencyMhz) =>
        PeerJoined?.Invoke(this, new PeerEventArgs(peerId, frequencyMhz));

    private void OnPeerLeft(string peerId, double frequencyMhz) =>
        PeerLeft?.Invoke(this, new PeerEventArgs(peerId, frequencyMhz));

    private void OnTransmissionStateChanged(double frequencyMhz, bool isTransmitting) =>
        TransmissionStateChanged?.Invoke(this, new TransmissionStateEventArgs(frequencyMhz, isTransmitting));

    private void OnPeerTransmissionStateChanged(string peerId, double frequencyMhz, bool isTransmitting) =>
        PeerTransmissionStateChanged?.Invoke(this, new PeerTransmissionEventArgs(peerId, frequencyMhz, isTransmitting));

    private void OnAudioDataReceived(string peerId, byte[] audioData, AircraftPosition? senderPosition = null,
        double frequencyMhz = 0) =>
        AudioDataReceived?.Invoke(this, new AudioDataEventArgs(peerId, audioData, senderPosition, frequencyMhz));

    private void OnError(string errorMessage) =>
        ErrorOccurred?.Invoke(this, new ErrorEventArgs(errorMessage));

    public void Dispose()
    {
        Console.WriteLine($"[CLIENT] Dispose called - Instance: {GetHashCode()}");
        _cts.Cancel();

        _audioClient?.Close();
        _audioClient?.Dispose();

        if (_webSocket?.State == WebSocketState.Open)
        {
            _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None).Wait(1000);
        }

        _webSocket?.Dispose();
        _cts.Dispose();
    }
}

// Event argument classes
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Authenticated
}

public class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionState State { get; }
    public ConnectionStateChangedEventArgs(ConnectionState state) => State = state;
}

public class AuthenticationEventArgs : EventArgs
{
    public string PeerId { get; }
    public int AudioPort { get; }

    public AuthenticationEventArgs(string peerId, int audioPort)
    {
        PeerId = peerId;
        AudioPort = audioPort;
    }
}

public class FrequencyJoinedEventArgs(double frequencyMhz, List<string> peers) : EventArgs
{
    public List<string> Peers { get;  } = peers;
    public double FrequencyMhz { get; } = frequencyMhz;
}

public class FrequencyLeftEventArgs : EventArgs
{
    public double FrequencyMhz { get; }
    public FrequencyLeftEventArgs(double frequencyMhz) => FrequencyMhz = frequencyMhz;
}

public class PeerEventArgs : EventArgs
{
    public string PeerId { get; }
    public double FrequencyMhz { get; }

    public PeerEventArgs(string peerId, double frequencyMhz)
    {
        PeerId = peerId;
        FrequencyMhz = frequencyMhz;
    }
}

public class TransmissionStateEventArgs : EventArgs
{
    public double FrequencyMhz { get; }
    public bool IsTransmitting { get; }

    public TransmissionStateEventArgs(double frequencyMhz, bool isTransmitting)
    {
        FrequencyMhz = frequencyMhz;
        IsTransmitting = isTransmitting;
    }
}

public class PeerTransmissionEventArgs : EventArgs
{
    public string PeerId { get; }
    public double FrequencyMhz { get; }
    public bool IsTransmitting { get; }

    public PeerTransmissionEventArgs(string peerId, double frequencyMhz, bool isTransmitting)
    {
        PeerId = peerId;
        FrequencyMhz = frequencyMhz;
        IsTransmitting = isTransmitting;
    }
}

public class AudioDataEventArgs : EventArgs
{
    public string PeerId { get; }
    public byte[] AudioData { get; }
    public AircraftPosition? SenderPosition { get; }
    public double FrequencyMhz { get; }

    public AudioDataEventArgs(string peerId, byte[] audioData, AircraftPosition? senderPosition = null,
        double frequencyMhz = 0)
    {
        PeerId = peerId;
        AudioData = audioData;
        SenderPosition = senderPosition;
        FrequencyMhz = frequencyMhz;
    }
}

public class ErrorEventArgs : EventArgs
{
    public string ErrorMessage { get; }
    public ErrorEventArgs(string errorMessage) => ErrorMessage = errorMessage;
}