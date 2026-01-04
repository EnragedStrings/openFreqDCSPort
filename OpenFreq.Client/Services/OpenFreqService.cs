using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ManagedBass;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.Services;

/// <summary>
/// Service that integrates OpenFreqClient with BASS audio system and manages communication state
/// </summary>
public class OpenFreqService : IOpenFreqService
{
    public IOpenFreqService.OpenFreqStatus Status { get; set; } = IOpenFreqService.OpenFreqStatus.Disconnected;

    private OpenFreqRtcClient? _client;
    private int _recordHandle;
    private readonly HashSet<double> _activeTransmissions = new();

    private RadioPlayback? _playbackService;
    private DEMReader? _demReader;
    private FastPathAudioSim? _audioSim;

    public int RecordingDeviceIndex { get; set; }
    private int _playbackDeviceIndex;

    private Dictionary<string, Dictionary<double, string>>
        _peerStreams = new(); // Holds all peer streams, ordered by peer ID and frequency

    public OpenFreqService(ILogger<OpenFreqService> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public int PlaybackDeviceIndex
    {
        get => _playbackDeviceIndex;
        set
        {
            _playbackDeviceIndex = value;
            _playbackService?.ChangeOutputDevice(_playbackDeviceIndex);
        }
    }

    private bool _isInitialized;

    // Store own position for RF calculations
    private AircraftPosition? _ownPosition;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OpenFreqService> _logger;

    // Events for UI updates
    public event EventHandler<ConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? StatusMessageReceived;
    public event EventHandler<FrequencyStatusEventArgs>? FrequencyStatusChanged;
    public event EventHandler<PeerActivityEventArgs>? PeerActivityReceived;

    public bool IsConnected => _client?.IsConnected ?? false;
    public bool IsAuthenticated => _client?.IsAuthenticated ?? false;
    public string? PeerId => _client?.MyPeerId;


    /// <summary>
    /// Initialize the service with server settings and audio devices
    /// </summary>
    public void Initialize(OpenFreqSettings settings, int recordingDeviceIndex, int playbackDeviceIndex)
    {
        Console.WriteLine($"[SERVICE] Initialize called - IsInitialized: {_isInitialized}");
        if (_isInitialized)
        {
            Console.WriteLine($"[SERVICE] Calling Shutdown from Initialize");
            Shutdown();
        }

        // Create client with server settings
        Console.WriteLine($"[SERVICE] Creating new client");
        _client = new OpenFreqRtcClient(_loggerFactory.CreateLogger<OpenFreqRtcClient>(),
            settings.OpenFreqServerAddress, settings.OpenFreqPassword);
        Console.WriteLine($"[SERVICE] Client created: {_client.GetHashCode()}");

        // Subscribe to client events
        _client.ConnectionStateChanged += OnClientConnectionStateChanged;
        _client.Authenticated += OnClientAuthenticated;
        _client.FrequencyJoined += OnClientFrequencyJoined;
        _client.FrequencyLeft += OnClientFrequencyLeft;
        _client.PeerJoined += OnClientPeerJoined;
        _client.PeerLeft += OnClientPeerLeft;
        _client.TransmissionStateChanged += OnClientTransmissionStateChanged;
        _client.PeerTransmissionStateChanged += OnClientPeerTransmissionStateChanged;
        _client.AudioDataReceived += OnClientAudioDataReceived;
        _client.ErrorOccurred += OnClientErrorOccurred;

        RecordingDeviceIndex = recordingDeviceIndex;
        _playbackDeviceIndex = playbackDeviceIndex;

        _playbackService = new RadioPlayback(true);
        _playbackService.Initialize(playbackDeviceIndex);

        _isInitialized = true;

        OnStatusMessage("OpenFreq service initialized");
    }

    public async void Shutdown()
    {
        Console.WriteLine($"[SERVICE] Shutdown called - IsInitialized: {_isInitialized}");
        if (!_isInitialized) return;

        try
        {
            _playbackService?.StopAll().Wait(500);

            if (_client != null)
            {
                // Unsubscribe from events before disposing
                _client.ConnectionStateChanged -= OnClientConnectionStateChanged;
                _client.Authenticated -= OnClientAuthenticated;
                _client.FrequencyJoined -= OnClientFrequencyJoined;
                _client.FrequencyLeft -= OnClientFrequencyLeft;
                _client.PeerJoined -= OnClientPeerJoined;
                _client.PeerLeft -= OnClientPeerLeft;
                _client.TransmissionStateChanged -= OnClientTransmissionStateChanged;
                _client.PeerTransmissionStateChanged -= OnClientPeerTransmissionStateChanged;
                _client.AudioDataReceived -= OnClientAudioDataReceived;
                _client.ErrorOccurred -= OnClientErrorOccurred;

                Console.WriteLine($"[SERVICE] Disconnecting client: {_client.GetHashCode()}");
                await _client.DisconnectAsync();
                Status = IOpenFreqService.OpenFreqStatus.Disconnected;
                Console.WriteLine($"[SERVICE] Disposing client: {_client.GetHashCode()}");
                _client.Dispose();
                _client = null;
            }

            _isInitialized = false;
            Console.WriteLine($"[SERVICE] Shutdown complete");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SERVICE] Shutdown exception: {e}");
            Console.WriteLine(e);
            throw;
        }
    }

    /// <summary>
    /// Connect to the OpenFreq server
    /// </summary>
    public async Task ConnectAsync()
    {
        if (!_isInitialized || _client == null)
        {
            throw new InvalidOperationException("Service not initialized. Call Initialize() first.");
        }

        OnStatusMessage("Connecting to OpenFreq server...");
        Status = IOpenFreqService.OpenFreqStatus.Connecting;
        await _client.ConnectAsync();
    }

    /// <summary>
    /// Disconnect from the server
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_client == null) return;

        // Stop all transmissions
        foreach (var frequencyMhz in _activeTransmissions.ToList())
        {
            await StopTransmissionAsync(frequencyMhz);
        }

        await _client.DisconnectAsync();
        OnStatusMessage("Disconnected from OpenFreq server");
        Status = IOpenFreqService.OpenFreqStatus.Disconnected;
    }

    /// <summary>
    /// Join a frequency channel
    /// </summary>
    public async Task JoinFrequencyAsync(double frequencyMhz)
    {
        if (_client == null || !_client.IsAuthenticated)
        {
            _logger.LogWarning("Not joining frequency {FrequencyMhz}, client is not authenticated", frequencyMhz);
            return;
        }

        await _client.JoinFrequencyAsync(frequencyMhz);
        OnStatusMessage($"Joined frequency {frequencyMhz}");
        _playbackService.TuneFrequency(frequencyMhz);

        // TODO
        _playbackService.SetSquelchLevel(frequencyMhz, 0.1f);
    }

    /// <summary>
    /// Leave a frequency channel
    /// </summary>
    public async Task LeaveFrequencyAsync(double frequencyMhz)
    {
        if (_client == null || !_client.IsAuthenticated)
        {
            _logger.LogWarning("Not leaving frequency {FrequencyMhz}, client is not authenticated", frequencyMhz);
            return;
        }

        // Stop transmission if active
        if (_activeTransmissions.Contains(frequencyMhz))
        {
            await StopTransmissionAsync(frequencyMhz);
        }

        await _client.LeaveFrequencyAsync(frequencyMhz);
        OnStatusMessage($"Left frequency {frequencyMhz}");
    }

    /// <summary>
    /// Start transmitting on a frequency
    /// </summary>
    public async Task StartTransmissionAsync(double frequency)
    {
        if (_client == null || _playbackService == null)
        {
            throw new InvalidOperationException("Service not initialized");
        }

        // Add to active transmissions
        _activeTransmissions.Add(frequency);
        // Mute the noise
        //_playbackService.SetSquelchLevel(frequency, 1.0f);

        // If this is the FIRST transmission, start recording
        if (_recordHandle == 0)
        {
            _playbackService.SetSquelchLevel(frequency, 0.01f);
            Bass.RecordInit(RecordingDeviceIndex);
            Bass.CurrentRecordingDevice = RecordingDeviceIndex;

            Console.WriteLine("RecordingDeviceIndex set to " + RecordingDeviceIndex);

            _recordHandle = Bass.RecordStart(
                OpenFreqRtcClient.SAMPLE_RATE,
                OpenFreqRtcClient.CHANNELS,
                BassFlags.RecordPause,
                Period: 20,
                RecordProcedure);

            if (_recordHandle == 0)
            {
                _activeTransmissions.Clear();
                OnStatusMessage($"Failed to start recording: {Bass.LastError}");
                return;
            }

            Bass.ChannelPlay(_recordHandle);
        }

        await _client.StartTransmissionAsync(frequency);
        OnStatusMessage($"Transmitting on {frequency}");
    }

    /// <summary>
    /// Stop transmitting on a frequency
    /// </summary>
    public async Task StopTransmissionAsync(double frequencyMhz)
    {
        if (_client == null) return;
        
        // Remove from active transmissions
        _activeTransmissions.Remove(frequencyMhz);

        // If NO more transmissions, stop recording
        if (_activeTransmissions.Count == 0 && _recordHandle != 0)
        {
            Bass.ChannelStop(_recordHandle);
            Bass.StreamFree(_recordHandle);
            _recordHandle = 0;
        }

        await _client.StopTransmissionAsync(frequencyMhz);
        OnStatusMessage($"Stopped transmitting on {frequencyMhz}");
    }

    /// <summary>
    /// Update the aircraft position for UDP packet metadata
    /// Call this from ACMI service, BMS shared memory service, or other position providers
    /// Thread-safe and can be called frequently without performance concerns
    /// </summary>
    public void UpdateAircraftPosition(double x, double y, double z)
    {
        _client?.SetPosition(x, y, z);

        // Store own position for RF calculations
        _ownPosition = new AircraftPosition { X = x, Y = y, Z = z };
    }

    /// <summary>
    /// Get the current aircraft position
    /// </summary>
    public AircraftPosition? GetAircraftPosition()
    {
        return _client?.GetPosition();
    }

    /// <summary>
    /// Check if currently transmitting on a frequency
    /// </summary>
    public bool IsTransmitting(int frequency)
    {
        return _activeTransmissions.Contains(frequency);
    }

    /// <summary>
    /// Load heightmap for terrain-aware RF calculations
    /// </summary>
    public void LoadHeightmap(string path, int width = 32768, int height = 32768, int bytesPerSample = 2)
    {
        _demReader?.Dispose();
        _demReader = new DEMReader(path, width, height, bytesPerSample);
        _audioSim = new FastPathAudioSim(_demReader, 0, 0, 20, _loggerFactory.CreateLogger<FastPathAudioSim>());
        OnStatusMessage($"Heightmap loaded: {path}");
    }

    private bool RecordProcedure(int handle, IntPtr buffer, int length, IntPtr user)
    {
        // If not transmitting on any frequency, skip
        if (_activeTransmissions.Count == 0)
            return true;

        try
        {
            // Handle first packet - BASS accumulates audio during initialization
            // Calculate expected size for 20ms at 48kHz, mono, 16-bit
            // 48000 samples/sec ÷ 50 = 960 samples per 20ms
            // 960 samples × 2 bytes/sample × 1 channel = 1920 bytes
            int expectedBytes = (OpenFreqRtcClient.SAMPLE_RATE / 50) * 2 * OpenFreqRtcClient.CHANNELS;
            
            // TODO: I dont think we need this anymore with the shorter dsp updates. Deactivated for now
            expectedBytes = 10000;

            if (length > expectedBytes)
            {
                Console.WriteLine(
                    $"[Recording] Packet oversized: {length} bytes, truncating to {expectedBytes}");
                // Option 1: Only use the LAST 20ms (most recent audio)
                buffer = IntPtr.Add(buffer, length - expectedBytes);
                length = expectedBytes;

                // Option 2: Skip first packet entirely (uncomment to use instead)
                //_isFirstPacket = false;
                //return true;
            }
            // Copy audio data once
            byte[] audioData = new byte[length];
            Marshal.Copy(buffer, audioData, 0, length);

            // Send to ALL active frequencies
            var position = GetAircraftPosition();
            if (position == null)
                position = new AircraftPosition { X = 0, Y = 0, Z = 0 };

            _client?.SendAudio(audioData, position, _activeTransmissions.ToList());
        }
        catch (Exception ex)
        {
            OnStatusMessage($"Error sending audio: {ex.Message}");
        }

        return true;
    }

    // Client event handlers
    private void OnClientConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        Status = e.State switch
        {
            ConnectionState.Connected => IOpenFreqService.OpenFreqStatus.Connected,
            ConnectionState.Disconnected => IOpenFreqService.OpenFreqStatus.Disconnected,
            ConnectionState.Connecting => IOpenFreqService.OpenFreqStatus.Connecting,
            ConnectionState.Authenticated => IOpenFreqService.OpenFreqStatus.Authenticated,
            _ => Status
        };

        ConnectionStateChanged?.Invoke(this, e.State);
    }

    private void OnClientAuthenticated(object? sender, AuthenticationEventArgs e)
    {
        OnStatusMessage($"Authenticated - Peer ID: {e.PeerId}, Audio Port: {e.AudioPort}");
    }

    private void OnClientFrequencyJoined(object? sender, FrequencyJoinedEventArgs e)
    {
        _playbackService?.TuneFrequency(e.FrequencyMhz);
        foreach (var peer in e.Peers)
        {
            CreateAudioStreamForPeer(e.FrequencyMhz, peer);
        }

        OnFrequencyStatusChanged(e.FrequencyMhz, Channel.ChannelStatus.Connected);
    }

    private void OnClientFrequencyLeft(object? sender, FrequencyLeftEventArgs e)
    {
        _playbackService?.UntuneFrequency(e.FrequencyMhz);
        OnFrequencyStatusChanged(e.FrequencyMhz, Channel.ChannelStatus.Disconnected);
    }

    private void OnClientPeerJoined(object? sender, PeerEventArgs e)
    {
        CreateAudioStreamForPeer(e.FrequencyMhz, e.PeerId);
        OnPeerActivity($"Peer {e.PeerId[..Math.Min(8, e.PeerId.Length)]} joined {e.FrequencyMhz}");
    }

    private void CreateAudioStreamForPeer(double frequencyMhz, string peerId)
    {
        // Create stream for this peer-frequency combination
        string streamId = GetStreamId(peerId, frequencyMhz);

        // Start with default params (will update when we get position data)
        var audioParams = FastPathAudioSim.GetDefaultAudioParams(frequencyMhz);

        _playbackService.StartPushStream(
            streamId,
            OpenFreqRtcClient.SAMPLE_RATE,
            OpenFreqRtcClient.CHANNELS,
            audioParams
        );

        // Track it
        if (!_peerStreams.ContainsKey(peerId))
            _peerStreams[peerId] = new Dictionary<double, string>();
        _peerStreams[peerId][frequencyMhz] = streamId;
    }

    private static string GetStreamId(string peerId, double frequencyMhz)
    {
        return peerId + ":" + frequencyMhz;
    }

    private void OnClientPeerLeft(object? sender, PeerEventArgs e)
    {
        // Remove stream when peer leaves
        if (_peerStreams.TryGetValue(e.PeerId, out var freqs))
        {
            if (freqs.TryGetValue(e.FrequencyMhz, out var streamId))
            {
                _playbackService.StopStream(streamId);
                freqs.Remove(e.FrequencyMhz);
            }
        }

        OnPeerActivity($"Peer {e.PeerId[..Math.Min(8, e.PeerId.Length)]} left {e.FrequencyMhz}");
    }

    private void OnClientTransmissionStateChanged(object? sender, TransmissionStateEventArgs e)
    {
        var status = e.IsTransmitting ? Channel.ChannelStatus.Transmitting : Channel.ChannelStatus.Connected;
        OnFrequencyStatusChanged(e.FrequencyMhz, status);
    }

    private void OnClientPeerTransmissionStateChanged(object? sender, PeerTransmissionEventArgs e)
    {
        var state = e.IsTransmitting ? "transmitting" : "stopped";
        OnPeerActivity($"Peer {e.PeerId} {state} on {e.FrequencyMhz}");

        var streamId = GetStreamId(e.PeerId, e.FrequencyMhz);
        if (e.IsTransmitting)
        {
            //_playbackService?.OnWebSocketPTTPress(streamId);
            OnFrequencyStatusChanged(e.FrequencyMhz, Channel.ChannelStatus.Receiving);
        }
        else
        {
            //_playbackService?.OnWebSocketPTTRelease(streamId);
            OnFrequencyStatusChanged(e.FrequencyMhz, Channel.ChannelStatus.Connected);
        }
    }

    /// <summary>
    /// Route received audio to playback service with RF effects
    /// </summary>
    private void OnClientAudioDataReceived(object? sender, AudioDataEventArgs e)
    {
        if (e.AudioData == null || e.AudioData.Length == 0)
        {
            _logger.LogWarning($"Audio data received with 0 size");
            return;
        }

        if (e.Metadata.Frequencies.Count == 0)
        {
            _logger.LogWarning($"Audio data received without frequencies, dropping");
        }


        foreach (var frequencyTransmission in e.Metadata.Frequencies)
        {
            AudioParams audioParams;
            if (e.Metadata.Position == null || _ownPosition == null)
            {
                _logger.LogDebug($"No position data, using defaults for {frequencyTransmission}");
                audioParams = FastPathAudioSim.GetDefaultAudioParams(frequencyTransmission.Mhz);
            }
            else
            {
                audioParams = _audioSim.CalculateAudioParams(
                    e.Metadata.Position.X, e.Metadata.Position.Y, e.Metadata.Position.Z,
                    _ownPosition.X, _ownPosition.Y, _ownPosition.Z,
                    frequencyTransmission.Mhz);
            }

            _logger.LogDebug($"Audio params: Gain={audioParams.Gain}, SNR={audioParams.SNR_dB}");

            var streamId = GetStreamId(e.PeerId, frequencyTransmission.Mhz);
            // Check if stream exists
            bool streamExists = _playbackService.IsStreamActive(streamId);
            _logger.LogDebug($"Stream {streamId} exists: {streamExists}");

            if (!streamExists)
            {
                _logger.LogDebug(
                    $"Creating new stream: {streamId}, SR={OpenFreqRtcClient.SAMPLE_RATE}, CH={OpenFreqRtcClient.CHANNELS}");
                _playbackService.StartPushStream(
                    streamId,
                    OpenFreqRtcClient.SAMPLE_RATE,
                    OpenFreqRtcClient.CHANNELS,
                    audioParams);
            }

            if (frequencyTransmission.BeginMarker)
                _logger.LogDebug($"Pushing START MARKER to Stream");
            else if (frequencyTransmission.EndMarker)
                _logger.LogDebug($"Pushing END MARKER to Stream");
            else
            {
                _logger.LogDebug($"Pushing NO MARKER to Stream");
            }
            _playbackService.PushAudioData(streamId, e.AudioData, frequencyTransmission.BeginMarker, frequencyTransmission.EndMarker);
        }
    }

    private void OnClientErrorOccurred(object? sender, ErrorEventArgs e)
    {
        _logger.LogError(e.ErrorMessage);
        OnStatusMessage($"Error: {e.ErrorMessage}");
    }

    // Event raising methods
    private void OnStatusMessage(string message) =>
        StatusMessageReceived?.Invoke(this, message);

    private void OnFrequencyStatusChanged(double frequencyMhz, Channel.ChannelStatus status)
    {
        Console.WriteLine($"{frequencyMhz}: {status}");
        FrequencyStatusChanged?.Invoke(this, new FrequencyStatusEventArgs(frequencyMhz, status));
    }

    private void OnPeerActivity(string message) =>
        PeerActivityReceived?.Invoke(this, new PeerActivityEventArgs(message));

    public void Dispose()
    {
        // Stop all transmissions and free recording handle
        Bass.ChannelStop(_recordHandle);
        Bass.StreamFree(_recordHandle);

        _recordHandle = 0;
        _activeTransmissions.Clear();

        _playbackService?.StopAll();
        _demReader?.Dispose();

        // Unsubscribe from client events before disposing
        if (_client != null)
        {
            _client.ConnectionStateChanged -= OnClientConnectionStateChanged;
            _client.Authenticated -= OnClientAuthenticated;
            _client.FrequencyJoined -= OnClientFrequencyJoined;
            _client.FrequencyLeft -= OnClientFrequencyLeft;
            _client.PeerJoined -= OnClientPeerJoined;
            _client.PeerLeft -= OnClientPeerLeft;
            _client.TransmissionStateChanged -= OnClientTransmissionStateChanged;
            _client.PeerTransmissionStateChanged -= OnClientPeerTransmissionStateChanged;
            _client.AudioDataReceived -= OnClientAudioDataReceived;
            _client.ErrorOccurred -= OnClientErrorOccurred;

            _client.Dispose();
        }
    }
}

// Event argument classes
public class FrequencyStatusEventArgs : EventArgs
{
    public double FrequencyMhz { get; }
    public Channel.ChannelStatus Status { get; }

    public FrequencyStatusEventArgs(double frequencyMhz, Channel.ChannelStatus status)
    {
        FrequencyMhz = frequencyMhz;
        Status = status;
    }
}

public class PeerActivityEventArgs : EventArgs
{
    public string Message { get; }

    public PeerActivityEventArgs(string message)
    {
        Message = message;
    }
}