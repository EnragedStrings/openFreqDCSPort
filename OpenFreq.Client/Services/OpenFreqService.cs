using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FalconBmsDataService.Models;
using FalconBmsDataService.Services;
using FalconRadioService.Models;
using FalconRadioService.Services;
using ManagedBass;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Models.Dcs;
using OpenFreq.Client.Services.Interfaces;
using OpenFreq.Client.Models;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreq.Utilities;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Audio;
using OpenFreqClient.Services.Interfaces;
using ErrorEventArgs = OpenFreq.Common.ErrorEventArgs;

namespace OpenFreqClient.Services;

/// <summary>
/// Service that integrates OpenFreqClient with BASS audio system and manages communication state
/// </summary>
public class OpenFreqService : IOpenFreqService
{
    public IOpenFreqService.OpenFreqStatus Status { get; set; } = IOpenFreqService.OpenFreqStatus.Disconnected;

    private IRtcClient? _client;
    private int _recordHandle;
    private readonly ConcurrentDictionary<int, List<int>> _activeTransmissionsAndMutedFrequencies = new();

    // Frequencies currently routed through the SATCOM vocoder path (see SetSatcomState) --
    // CalculateAudioParamsSync's terrestrial free-space/LOS model has no idea these exist and
    // computes it anyway (distance/terrain between the two AIRCRAFT), which is simply the wrong
    // question for a bent-pipe satellite relay: a receiver only needs its own leg to the
    // satellite, never direct LOS to the transmitter (see docs/SATCOM_SIMULATION.md's "Independent
    // per-leg evaluation" -- both server-authoritative and already correctly implemented there).
    // Without this, two aircraft could both show a fully "Good" server-computed SATCOM link and
    // still hear nothing from each other, purely because they happened to be terrestrially
    // LOS-blocked from one another -- see OnClientAudioDataReceived's SignalBlocked check below.
    private readonly ConcurrentDictionary<int, byte> _satcomActiveFrequencies = new();

    // Frequencies currently transmitting a synthesized tone (see StartToneTransmissionAsync)
    // rather than real mic audio. Subset of _activeTransmissionsAndMutedFrequencies' keys.
    private readonly ConcurrentDictionary<int, byte> _toneTransmissionFrequencies = new();
    private double _tonePhase;

    private class TunedFrequencyData(RadioStationData radioStation, bool isEnabled)
    {
        public RadioStationData RadioStation { get; set; } = radioStation;
        public bool IsEnabled { get; set; } = isEnabled;

        // KY-58/COMSEC + HAVE QUICK state for this slot, used to tag outgoing transmissions.
        // (Receive-side gating lives entirely in IPlaybackService/RadioPlayback.)
        public bool Enc { get; set; }
        public int EncKey { get; set; }
        public bool HqOn { get; set; }
    }

    // Keyed by (frequencyKhz, slotId) so multiple radio sets can tune the same frequency independently.
    private readonly ConcurrentDictionary<(int FreqKhz, Guid SlotId), TunedFrequencyData> _tunedSlots = new();

    // Which slotId is currently TX-ing on each frequency (needed for own-position lookup during record).
    private readonly ConcurrentDictionary<int, Guid> _activeTransmissionSlots = new();

    // Temporary diagnostic: last-logged TX enc state per frequency, so SendAudio's per-packet log
    // only fires on change rather than every 20ms packet. Nullable so the very first packet on a
    // frequency always logs once, even if Enc happens to be false -- a bool default would make an
    // always-false stream indistinguishable from "nothing logged yet".
    private readonly ConcurrentDictionary<int, bool?> _lastLoggedTxEncByFreq = new();

    private bool IsAnySlotTuned(int frequencyKhz) =>
        _tunedSlots.Keys.Any(k => k.FreqKhz == frequencyKhz);

    private TunedFrequencyData? GetAnyTunedSlot(int frequencyKhz) =>
        _tunedSlots.FirstOrDefault(kvp => kvp.Key.FreqKhz == frequencyKhz).Value;


    private IPlaybackService? _playbackService;
    private readonly Lock _streamCreationLock = new();

    private ISignalCalculator? _signalCalculator;
    private readonly IAcmiClientService _acmiClientService;
    private readonly SignalStrengthTracker _signalStrengthTracker;

    private int _recordingDeviceIndex;

    public int RecordingDeviceIndex
    {
        get => _recordingDeviceIndex;
        set
        {
            _recordingDeviceIndex = value;
            // If a recording session is already active, migrate it to the new device immediately.
            // Without this, the live recording stream keeps using the old (potentially dead) device
            // until the user stops and restarts transmission.
            if (_recordHandle != 0)
            {
                _logger.LogInformation(
                    "Recording device changed to BASS index {Index} while recording active — restarting capture",
                    value);
                RestartRecordingOnNewDevice(value);
            }
        }
    }

    private int _playbackDeviceIndex;

    private readonly Dictionary<string, Dictionary<int, string>>
        _peerStreams = new(); // Holds all peer streams, ordered by peer ID and frequency


    // Cache for audio params: Key is (PeerId, FrequencyKhz)
    private readonly ConcurrentDictionary<(string PeerId, int FrequencyKhz), AudioParamsCacheEntry> _audioParamsCache =
        new();

    // Pre-allocated sidetone conversion buffer — reused every recording callback (single-threaded).
    private float[] _sidetonePushBuffer = new float[4800]; // 100ms @ 48kHz, grows if needed
    private readonly MicLevelNormalizer _micNormalizer = new(OpenFreqRtcClient.SAMPLE_RATE);
    private DateTime _lastMicLevelEventUtc = DateTime.MinValue;
    private DateTime _nextEmptyTransmissionWarningUtc = DateTime.MinValue;

    // Cache duration - this effectively controls the rate of local physics calculations
    private readonly TimeSpan _audioParamsCacheDuration = TimeSpan.FromMilliseconds(50);

    // Cache cleanup
    private CancellationTokenSource? _cleanupCts;
    private CancellationTokenSource? _dcsPresenceCts;

    private const float SquelchLevelOff = 0f;
    private const float SquelchLevelOn = 1f;

    private readonly IRtcClientFactory _rtcClientFactory;
    private readonly IPlaybackServiceFactory _playbackServiceFactory;
    private readonly ISignalCalculatorFactory _signalCalculatorFactory;
    private string? _loadedDcsHeightmapPath;

    public OpenFreqService(IFalconSharedMemoryService falconSharedMemoryService,
        IFalconRadioSharedMemoryService falconRadioSharedMemoryService, IDcsExportService dcsExportService,
        ILogger<OpenFreqService> logger,
        ILoggerFactory loggerFactory, IAcmiClientService acmiClientService,
        IRtcClientFactory rtcClientFactory, IPlaybackServiceFactory playbackServiceFactory,
        ISignalCalculatorFactory signalCalculatorFactory)
    {
        _falconSharedMemoryService = falconSharedMemoryService;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _dcsExportService = dcsExportService;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _acmiClientService = acmiClientService;
        _rtcClientFactory = rtcClientFactory;
        _playbackServiceFactory = playbackServiceFactory;
        _signalCalculatorFactory = signalCalculatorFactory;

        // Initialize signal strength tracker with callback
        _signalStrengthTracker = new SignalStrengthTracker(
            onSignalStrengthChanged: (frequencyKhz, strengthData) =>
            {
                WeakReferenceMessenger.Default.Send(
                    new SignalStrengthTracker.SignalStrengthUpdateMessage(frequencyKhz, strengthData.StrengthPercent,
                        strengthData.SnrDb, strengthData.ReceivedDb));
            },
            updateIntervalMs: 100, // UI update rate
            signalTimeoutMs: 500 // How long until "no signal"
        );

        _dcsExportService.HeightmapChanged += OnDcsHeightmapChanged;
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

    public int AudioParamsUpdateFrequency { get; set; }

    public bool SidetoneEnabled
    {
        get;
        set
        {
            field = value;
            if (_playbackService != null) _playbackService.SidetoneEnabled = value;
        }
    }

    public bool MicNormalizationEnabled
    {
        get;
        set
        {
            field = value;
            // Continuous capture (for noise-floor tracking) is tied to this toggle, so the
            // mic stream needs to open/close when it flips while connected and idle.
            UpdateMicCaptureState();
        }
    } = true;

    public bool InputMeterEnabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            UpdateMicCaptureState();
            if (!value)
                MicLevelChanged?.Invoke(this, new MicLevelChangedEventArgs(0, 0));
        }
    }

    public double InputGain
    {
        get => field;
        set => field = Math.Clamp(value, 0.0d, 10.0d); // +20 dB ceiling
    } = 1.0d;

    public bool DcsLineOfSightEnabled { get; set; } = true;

    /// <summary>Server-broadcast SATCOM feature gate, mirroring DcsLineOfSightEnabled above.
    /// Not yet consumed by any live audio/link-quality code path in this pass -- SatcomLinkCalculator/
    /// SatcomAcquisitionStateMachine are built and tested but not wired into the real-time
    /// RadioPlayback pipeline yet (see docs/SATCOM_SIMULATION.md's limitations section). Exposed
    /// now so that wiring only needs to add a read of this flag, not another round-trip through
    /// the signaling protocol.</summary>
    public bool SatcomEnabled { get; set; } = true;

    public double SidetoneVolume
    {
        get => field;
        set
        {
            field = value;
            if (_playbackService != null) _playbackService.SidetoneVolume = (float)value;
        }
    } = 0.4;

    public double MasterVolume
    {
        get => field;
        set
        {
            field = value;
            if (_playbackService != null) _playbackService.MasterVolume = (float)value;
        }
    } = 1.0;

    public double AmbientNoiseVolume
    {
        get => field;
        set
        {
            field = value;
            if (_playbackService != null) _playbackService.AmbientNoiseVolume = (float)value;
        }
    } = 1.0;

    /// <summary>When true, capture auto-starts on entering game mode (flight) and auto-stops on leaving it.</summary>
    public bool AutoRecordInGameMode { get; set; }

    /// <summary>When true, own voice in the capture gets the full radio FX (AGC/squelch/SFX); when false it stays clean.</summary>
    public bool ApplyOwnVoiceSfx
    {
        get => field;
        set
        {
            field = value;
            if (_playbackService != null) _playbackService.OwnVoiceSfxEnabled = value;
        }
    } = true;

    /// <summary>Selects the capture output: file or playback device.</summary>
    public IOpenFreqService.CaptureSink Sink { get; set; } = IOpenFreqService.CaptureSink.File;

    /// <summary>BASS device index the capture mix is streamed to when <see cref="Sink"/> is Device.</summary>
    public int MonitorDeviceIndex { get; set; }

    /// <summary>Directory recordings are written to. Created if missing. Blank → "recordings" next to the executable.</summary>
    public string RecordingPath { get; set; } = "";

    /// <summary>True while a capture (file or device) is in progress.</summary>
    public bool IsRecording => _playbackService?.IsCapturing ?? false;

    /// <summary>Raised when recording starts (true) or stops (false).</summary>
    public event EventHandler<bool>? RecordingStateChanged;

    private bool _isInitialized;

    private readonly IFalconSharedMemoryService _falconSharedMemoryService;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly IDcsExportService _dcsExportService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OpenFreqService> _logger;

    // Events for UI updates
    public IOpenFreqService.Mode OwnPositionMode { get; private set; }
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<string>? StatusMessageReceived;
    public event EventHandler<string>? AudioPlaybackErrorOccurred;
    public event EventHandler<MicLevelChangedEventArgs>? MicLevelChanged;
    public event EventHandler<FrequencyConnectionStatusEventArgs>? FrequencyConnectionStatusChanged;
    public event EventHandler<FrequencyTransmissionStatusEventArgs>? FrequencyTransmissionStatusChanged;
    public event EventHandler<FrequencyJoinedEventArgs>? FrequencyJoined;
    public event EventHandler<PeerEventArgs>? PeerJoined;
    public event EventHandler<PeerEventArgs>? PeerLeft;
    public event EventHandler<PeerActivityEventArgs>? PeerActivityReceived;
    public event EventHandler<AllPeersStatusEventArgs>? AllPeersStatusChanged;

    public bool IsConnected => _client?.IsConnected ?? false;
    public bool IsAuthenticated => _client?.IsAuthenticated ?? false;
    public string? PeerId => _client?.MyPeerId;

    /// <summary>
    /// Initialize the service with server settings and audio devices
    /// </summary>
    public async Task Initialize(OpenFreqSettings settings, int recordingDeviceIndex, int playbackDeviceIndex)
    {
        if (_isInitialized)
        {
            _logger.LogDebug("Calling Shutdown from Initialize");
            await Shutdown();
        }

        // In BMS mode: use Nickname from connection params (= LogBook.Callsign(), set by BMS
        // before AttemptToConnect fires). LogbookName is from the Telemetry struct which is
        // initialised to "Wot Pilot?!" and only written after ClientReady() — too late.
        // NEVER fall back to settings.DisplayName in BMS mode — that is the manually-entered
        // GCI name. If Nickname is unavailable (ConnectionParameters reset between RCC close
        // and next poll), connect with an empty placeholder; UpdateDisplayNameAsync() will
        // push the real callsign immediately after authentication.
        var myDisplayName =
            settings.OwnPositionMode == IOpenFreqService.Mode.BMS
                ? (_falconRadioSharedMemoryService.ConnectionParameters?.Nickname ?? string.Empty)
                : settings.DisplayName;

        // Create client with server settings
        _logger.LogDebug("Creating new client");
        _client = _rtcClientFactory.Create(_loggerFactory,
            settings.OpenFreqServerAddress, settings.OpenFreqPassword, myDisplayName);
        _logger.LogDebug("Client created: {ClientHashCode}", _client.GetHashCode());

        // Subscribe to client events
        _client.ConnectionStateChanged += OnClientConnectionStateChanged;
        _client.Authenticated += OnClientAuthenticated;
        _client.FrequencyJoined += OnClientFrequencyJoined;
        _client.FrequencyLeft += OnClientFrequencyLeft;
        _client.PeerJoined += OnClientPeerJoined;
        _client.PeerLeft += OnClientPeerLeft;
        _client.TransmissionStateChanged += OnClientTransmissionStatusChanged;
        _client.PeerTransmissionStateChanged += OnClientPeerTransmissionStatusChanged;
        _client.AudioDataReceived += OnClientAudioDataReceived;
        _client.AllPeersStatusUpdateReceived += OnAllPeersStatusUpdateReceived;
        _client.ServerSettingsChanged += OnClientServerSettingsChanged;
        _client.ErrorOccurred += OnClientErrorOccurred;
        _client.SatcomLinkStateReceived += OnClientSatcomLinkStateReceived;
        _client.SatelliteEphemerisReceived += OnClientSatelliteEphemerisReceived;
        _client.DcsLosOracleRequestReceived += OnClientDcsLosOracleRequestReceived;

        RecordingDeviceIndex = recordingDeviceIndex;
        var previousPlaybackDeviceIndex = _playbackDeviceIndex;
        _playbackDeviceIndex = playbackDeviceIndex;

        if (_playbackService == null)
        {
            // First initialization: create RadioPlayback and init BASS device.
            _playbackService = _playbackServiceFactory.Create(_loggerFactory, playbackDeviceIndex);
            _playbackService.UserFacingError += OnPlaybackUserFacingError;
            _playbackService.Initialize();
            _logger.LogInformation("Playback Service initialized");
        }
        else if (previousPlaybackDeviceIndex != playbackDeviceIndex)
        {
            // Device changed: switch without tearing down BASS entirely.
            _logger.LogWarning(
                "Playback device changed {Old}→{New} — calling ChangeOutputDevice",
                previousPlaybackDeviceIndex, playbackDeviceIndex);
            _playbackService.UserFacingError += OnPlaybackUserFacingError;
            _playbackService.ChangeOutputDevice(playbackDeviceIndex);
        }
        else
        {
            // Same device, same instance — streams already stopped in Shutdown(). Just re-subscribe.
            _playbackService.UserFacingError += OnPlaybackUserFacingError;
            // Guard against the master stream having been stopped during the previous session
            _playbackService.EnsureMasterStreamRunning();
        }

        _playbackService.SidetoneEnabled = SidetoneEnabled;
        _playbackService.SidetoneVolume = (float)SidetoneVolume;
        _playbackService.AmbientNoiseVolume = (float)AmbientNoiseVolume;
        _playbackService.OwnVoiceSfxEnabled = ApplyOwnVoiceSfx;
        _playbackService.SetEncryptionToneAssets(
            LoadEmbeddedAudioAsset("AudioEffects/KY_58_TX.wav"),
            LoadEmbeddedAudioAsset("AudioEffects/KY_58_RX.wav"));
        _isInitialized = true;

        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;
        _falconSharedMemoryService.StateChanged += OnFalconStateChanged;

        // Initialize Audio Params cache cleanup
        _cleanupCts = new CancellationTokenSource();
        _ = CleanupAudioParamsCacheAsync(_cleanupCts.Token);

        _dcsPresenceCts = new CancellationTokenSource();
        _ = SendDcsPresenceUpdatesAsync(_dcsPresenceCts.Token);

        OnStatusMessage("OpenFreq service initialized");
    }

    private void OnFalconStateChanged(object? sender, ServiceStateChangedEventArgs e)
    {
        // BMS process died: the shared-memory service goes Connected -> Disconnected.
        // In BMS mode, tear down our session cleanly (stop recording, drop transmissions, clear tuned slots/streams, disconnect from the server)
        if (e is { OldState: ServiceState.Connected, NewState: ServiceState.Disconnected } &&
            OwnPositionMode == IOpenFreqService.Mode.BMS)
        {
            _logger.LogInformation("BMS process gone - disconnecting and resetting OpenFreq state");
            OnStatusMessage("BMS closed - disconnecting");
            // Fire-and-forget: StateChanged is raised from the polling thread, so we must  not block it on the async disconnect
            _ = Task.Run(async () =>
            {
                try
                {
                    await DisconnectAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disconnecting after BMS process exit");
                }
            });

            return;
        }

        if (e.NewState == ServiceState.Connected && _falconSharedMemoryService.TheaterTerrainDir != null)
        {
            var heightmapPath = Path.Join(_falconSharedMemoryService.TheaterTerrainDir, "NewTerrain", "HeightMaps",
                "HeightMap.raw");
            if (!File.Exists(heightmapPath))
            {
                _logger.LogError("Could not find heightmap path: {heightmapPath}", heightmapPath);
                return;
            }

            LoadHeightmap(heightmapPath);
        }
    }

    private void OnFlyingStateChanged(object? sender, FlyingStateChangedEventArgs e)
    {
        // This handler only loads the heightmap on takeoff (auto-record triggers on connect, see
        // OnClientAuthenticated).
        if (e is not { OldFlyingState: false, NewFlyingState: true }) return;
        var heightmapPath = Path.Join(_falconSharedMemoryService.TheaterTerrainDir, "NewTerrain", "HeightMaps",
            "HeightMap.raw");
        if (!File.Exists(heightmapPath))
        {
            _logger.LogError("Could not find heightmap path: {heightmapPath}", heightmapPath);
            return;
        }

        LoadHeightmap(heightmapPath);
    }

    public async Task Shutdown()
    {
        _logger.LogDebug("Shutdown called - IsInitialized: {IsInitialized}", _isInitialized);
        if (!_isInitialized) return;

        try
        {
            // Stop continuous mic capture explicitly: client events are unsubscribed below
            // before the disconnect, so the event-driven close path won't run here.
            StopMicCapture();

            if (_playbackService != null)
            {
                // Always stop recording before tearing streams down.
                StopRecording();
                _playbackService.UserFacingError -= OnPlaybackUserFacingError;
                // Stop individual streams without freeing the BASS device — RadioPlayback is
                // kept alive so the next Initialize() can reuse it without Bass.Free()+Bass.Init().
                // Full StopAll() (Bass.Free) only happens on device change or app Dispose().
                var activeStreams = _playbackService.GetActiveStreams();
                foreach (var streamId in activeStreams)
                    await _playbackService.StopStream(streamId);
            }

            if (_client != null)
            {
                // Unsubscribe from events before disposing
                _client.ConnectionStateChanged -= OnClientConnectionStateChanged;
                _client.Authenticated -= OnClientAuthenticated;
                _client.FrequencyJoined -= OnClientFrequencyJoined;
                _client.FrequencyLeft -= OnClientFrequencyLeft;
                _client.PeerJoined -= OnClientPeerJoined;
                _client.PeerLeft -= OnClientPeerLeft;
                _client.TransmissionStateChanged -= OnClientTransmissionStatusChanged;
                _client.PeerTransmissionStateChanged -= OnClientPeerTransmissionStatusChanged;
                _client.AudioDataReceived -= OnClientAudioDataReceived;
                _client.AllPeersStatusUpdateReceived -= OnAllPeersStatusUpdateReceived;
                _client.ServerSettingsChanged -= OnClientServerSettingsChanged;
                _client.ErrorOccurred -= OnClientErrorOccurred;
                _client.SatcomLinkStateReceived -= OnClientSatcomLinkStateReceived;
                _client.SatelliteEphemerisReceived -= OnClientSatelliteEphemerisReceived;
                _client.DcsLosOracleRequestReceived -= OnClientDcsLosOracleRequestReceived;

                _logger.LogDebug("Disconnecting client: {ClientHashCode}", _client.GetHashCode());
                await _client.DisconnectAsync();
                Status = IOpenFreqService.OpenFreqStatus.Disconnected;
                _logger.LogDebug("Disposing client: {ClientHashCode}", _client.GetHashCode());
                _client.Dispose();
                _client = null;
            }

            // Unsubscribe falcon shared memory events — subscribed on every Initialize,
            // so must be unsubscribed here to prevent accumulation across reconnects.
            _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;
            _falconSharedMemoryService.StateChanged -= OnFalconStateChanged;

            _peerStreams.Clear();
            _isInitialized = false;
            _logger.LogDebug("Shutdown complete");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Shutdown exception");
            throw;
        }
    }

    /// <summary>
    /// Connect to the OpenFreq server
    /// </summary>
    public async Task ConnectAsync(TimeSpan? connectTimeout = null)
    {
        if (!_isInitialized || _client == null)
        {
            throw new InvalidOperationException("Service not initialized. Call Initialize() first.");
        }

        OnStatusMessage($"Connecting to OpenFreq server {_client.ServerIp}...");
        Status = IOpenFreqService.OpenFreqStatus.Connecting;
        await _client.ConnectAsync(connectTimeout);
    }

    /// <summary>
    /// Start a combined session recording (incoming as heard + own voice rendered as if heard
    /// from the same position) to a timestamped .ogg. No-op if already recording or the
    /// playback subsystem is not initialized.
    /// </summary>
    public void StartRecording()
    {
        if (_playbackService == null || _playbackService.IsCapturing) return;

        try
        {
            if (Sink == IOpenFreqService.CaptureSink.Device)
            {
                _playbackService.StartMonitor(MonitorDeviceIndex);
                if (!_playbackService.IsMonitoring) return; // start failed; error already surfaced
                OnStatusMessage("Streaming audio to monitor device");
            }
            else
            {
                var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                var dir = string.IsNullOrWhiteSpace(RecordingPath)
                    ? Path.Combine(exeDir, "recordings")
                    : RecordingPath;
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"OpenFreq_{DateTime.Now:yyyyMMdd_HHmmss}.ogg");
                _playbackService.StartRecording(file);
                if (!_playbackService.IsRecording) return; // start failed; error already surfaced
                OnStatusMessage($"Recording to {file}");
            }

            RecordingStateChanged?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start capture");
            OnStatusMessage($"Failed to start capture: {ex.Message}");
        }
    }

    /// <summary>Stop the current capture. Always honoured (manual stop overrides auto). No-op if idle.</summary>
    public void StopRecording()
    {
        if (_playbackService is not { IsCapturing: true }) return;
        _playbackService.StopRecording();
        _playbackService.StopMonitor();
        OnStatusMessage("Recording stopped");
        RecordingStateChanged?.Invoke(this, false);
    }

    /// <summary>
    /// Disconnect from the server
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_client == null) return;

        // Always stop recording when disconnecting.
        StopRecording();

        // Stop all transmissions
        foreach (var frequencyKhz in _activeTransmissionsAndMutedFrequencies.Keys)
        {
            await StopTransmissionAsync(frequencyKhz);
        }

        await _client.DisconnectAsync();
        _activeTransmissionsAndMutedFrequencies.Clear();
        _activeTransmissionSlots.Clear();
        _toneTransmissionFrequencies.Clear();
        _tunedSlots.Clear();

        // Stop orphaned BASS streams before clearing the tracking dict.
        // PeerLeft events may not fire on abrupt disconnects; stopping here ensures
        // RadioPlayback stays clean so it can be reused on the next connect.
        if (_playbackService != null)
        {
            foreach (var freqs in _peerStreams.Values)
                foreach (var streamId in freqs.Values)
                    await _playbackService.StopStream(streamId);
        }

        _peerStreams.Clear();
        // Close continuous capture now that we're disconnected (also closed via the connection
        // event on unexpected drops, but be explicit on the graceful path).
        StopMicCapture();
        OnStatusMessage("Disconnected from OpenFreq server");
        Status = IOpenFreqService.OpenFreqStatus.Disconnected;
    }

    public bool IsFrequencyJoined(int frequencyKhz, Guid slotId)
    {
        return _tunedSlots.ContainsKey((frequencyKhz, slotId));
    }

    /// <summary>
    /// Join a frequency channel for a specific radio slot.
    /// The signalling server is joined only on the first slot; subsequent slots on the same
    /// frequency reuse the existing server connection.
    /// </summary>
    public async Task JoinFrequencyAsync(int frequencyKhz, Guid slotId, RadioStationData radioStationData)
    {
        if (_client == null || !_client.IsAuthenticated)
        {
            _logger.LogWarning("Not joining frequency {FrequencyKhz}, client is not authenticated", frequencyKhz);
            return;
        }

        if (radioStationData == null)
        {
            _logger.LogWarning("Not joining frequency {FrequencyKhz}, RadioStationData is null", frequencyKhz);
            return;
        }

        if (_tunedSlots.ContainsKey((frequencyKhz, slotId)))
        {
            _logger.LogDebug("Not joining frequency {FrequencyKhz} slot {SlotId}, already joined", frequencyKhz, slotId);
            return;
        }

        var isFirstSlot = !IsAnySlotTuned(frequencyKhz);

        // Set up local audio state BEFORE sending the join to the server
        _tunedSlots.TryAdd((frequencyKhz, slotId), new TunedFrequencyData(radioStationData, true));
        _signalStrengthTracker.SetSquelchState(frequencyKhz, false);
        _playbackService?.TuneFrequency(frequencyKhz, slotId);

        if (isFirstSlot)
        {
            await _client.JoinFrequencyAsync(frequencyKhz);
            OnStatusMessage($"Joined frequency {frequencyKhz / 1000.0:F3} MHz");
        }

        if (!isFirstSlot)
        {
            // Frequency already active on server — synthesise the connected event for this slot only.
            OnFrequencyConnectionStatusChanged(frequencyKhz, Channel.ChannelConnectionStatus.Connected, [], slotId);
            OnStatusMessage($"Tuned frequency {frequencyKhz / 1000.0:F3} MHz (additional slot)");
        }
    }

    /// <summary>
    /// Leave a frequency channel for a specific radio slot.
    /// The signalling server is left only when the last slot leaves.
    /// </summary>
    public async Task LeaveFrequencyAsync(int frequencyKhz, Guid slotId)
    {
        if (_client == null || !_client.IsAuthenticated)
        {
            _logger.LogWarning("Not leaving frequency {FrequencyKhz}, client is not authenticated", frequencyKhz);
            return;
        }

        // Stop transmission if this slot owns the active TX on this frequency.
        if (_activeTransmissionSlots.TryGetValue(frequencyKhz, out var txSlotId) && txSlotId == slotId)
        {
            await StopTransmissionAsync(frequencyKhz);
        }

        // Disconnect this slot immediately (before server leave so UI updates promptly).
        OnFrequencyConnectionStatusChanged(frequencyKhz, Channel.ChannelConnectionStatus.Disconnected, [], slotId);

        _tunedSlots.TryRemove((frequencyKhz, slotId), out _);
        _playbackService?.UntuneFrequency(frequencyKhz, slotId);

        if (!IsAnySlotTuned(frequencyKhz))
        {
            _signalStrengthTracker.RemoveFrequency(frequencyKhz);
            await _client.LeaveFrequencyAsync(frequencyKhz);
            OnStatusMessage($"Left frequency {frequencyKhz / 1000.0:F3} MHz");
        }
    }

    /// <summary>
    /// Start transmitting on a frequency from a specific radio slot.
    /// </summary>
    public async Task StartTransmissionAsync(int frequencyKhz, Guid slotId, List<int> mutedFrequencies)
    {
        if (_client == null || _playbackService == null)
        {
            throw new InvalidOperationException("Service not initialized");
        }

        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
        if (tunedFrequencyData is not { IsEnabled: true })
        {
            _logger.LogWarning("Trying to start transmission on unavailable frequency {FrequencyKhz} slot {SlotId}",
                frequencyKhz, slotId);
            OnStatusMessage($"Cannot transmit on {frequencyKhz / 1000d:F3}: radio is not joined/enabled");
            return;
        }

        // "Modified PTT tone": local cue that this transmission is encrypted.
        if (tunedFrequencyData.Enc)
            _playbackService.PlayTxTone();

        // Whether we're transitioning from idle → transmitting (i.e. first active TX).
        bool wasIdle = _activeTransmissionsAndMutedFrequencies.IsEmpty;

        // Add to active transmissions (TX is per-frequency; only one TX per frequency at a time)
        _activeTransmissionsAndMutedFrequencies.TryAdd(frequencyKhz, mutedFrequencies);
        _activeTransmissionSlots[frequencyKhz] = slotId;
        _playbackService.AddTransmittingFrequencies(mutedFrequencies);

        // Mute the noise
        //_playbackService.SetSquelchLevel(frequency, 1.0f);

        // Make sure the mic stream is live. With normalization enabled it's usually already
        // open for continuous noise-floor tracking; otherwise this opens it just for the
        // duration of the transmission.
        if (!StartMicCapture())
        {
            // Couldn't open the mic — roll this transmission back so we don't TX silence.
            _activeTransmissionsAndMutedFrequencies.TryRemove(frequencyKhz, out _);
            _activeTransmissionSlots.TryRemove(frequencyKhz, out _);
            return;
        }

        // On the idle → transmitting edge, mark the start time and arm sidetone monitoring.
        if (wasIdle)
        {
            _client.MarkTransmitStartTime();
            if (_playbackService != null) _playbackService.SidetoneEnabled = SidetoneEnabled;
        }

        await _client.StartTransmissionAsync(frequencyKhz);
        OnStatusMessage($"Transmitting on {frequencyKhz / 1000d:F3}");
    }

    /// <summary>
    /// Stop transmitting on a frequency
    /// </summary>
    public async Task StopTransmissionAsync(int frequencyKhz)
    {
        if (_client == null) return;

        // Remove from active transmissions
        _activeTransmissionsAndMutedFrequencies.TryRemove(frequencyKhz, out var mutedFrequencies);
        _activeTransmissionSlots.TryRemove(frequencyKhz, out _);
        if (mutedFrequencies != null)
        {
            _playbackService?.RemoveTransmittingFrequencies(mutedFrequencies);
        }

        // If NO more transmissions, clear sidetone and re-evaluate capture: the mic stays open
        // for continuous noise-floor tracking when normalization is enabled, otherwise it closes.
        if (_activeTransmissionsAndMutedFrequencies.IsEmpty)
        {
            if (_playbackService != null)
            {
                _playbackService.SidetoneEnabled = SidetoneEnabled;
                _playbackService.ClearSidetone();
            }

            UpdateMicCaptureState();
        }

        await _client.StopTransmissionAsync(frequencyKhz);
        OnStatusMessage($"Stopped transmitting on {frequencyKhz / 1000d:F3} MHz");
    }

    /// <inheritdoc />
    public async Task StartToneTransmissionAsync(int frequencyKhz, Guid slotId, List<int> mutedFrequencies)
    {
        _toneTransmissionFrequencies.TryAdd(frequencyKhz, 0);
        await StartTransmissionAsync(frequencyKhz, slotId, mutedFrequencies);
    }

    /// <inheritdoc />
    public async Task StopToneTransmissionAsync(int frequencyKhz)
    {
        _toneTransmissionFrequencies.TryRemove(frequencyKhz, out _);
        await StopTransmissionAsync(frequencyKhz);
    }

    /// <summary>
    /// Whether the shared mic stream should currently be open. The mic is held open while
    /// connected if either we're transmitting, or normalization is on (so the noise-floor
    /// estimator can keep tracking the room between talk-spurts).
    /// </summary>
    private bool WantMicCapture =>
        InputMeterEnabled || (IsConnected && (MicNormalizationEnabled || !_activeTransmissionsAndMutedFrequencies.IsEmpty));

    /// <summary>
    /// Opens or closes the shared mic capture stream to match <see cref="WantMicCapture"/>.
    /// Safe to call repeatedly — it's a no-op when already in the desired state.
    /// </summary>
    private void UpdateMicCaptureState()
    {
        if (WantMicCapture) StartMicCapture();
        else StopMicCapture();
    }

    /// <summary>
    /// Opens the shared microphone capture stream if it isn't already running. The same stream
    /// feeds the noise-floor estimator while idle and the TX path while transmitting.
    /// Returns true if a stream is running on return.
    /// </summary>
    private bool StartMicCapture()
    {
        if (_recordHandle != 0) return true;

        // RecordInit: Errors.Already is fine — AudioService.Init may have already done it.
        if (!Bass.RecordInit(RecordingDeviceIndex) && Bass.LastError != Errors.Already)
        {
            var initMsg = $"Failed to initialize recording device (BASS index {RecordingDeviceIndex}): {Bass.LastError}";
            _logger.LogError("{Message}", initMsg);
            AudioPlaybackErrorOccurred?.Invoke(this, initMsg);
            return false;
        }

        Bass.CurrentRecordingDevice = RecordingDeviceIndex;
        _logger.LogDebug("RecordingDeviceIndex set to {RecordingDeviceIndex}", RecordingDeviceIndex);

        _recordHandle = Bass.RecordStart(
            OpenFreqRtcClient.SAMPLE_RATE,
            1,
            BassFlags.RecordPause,
            Period: 2,
            RecordProcedure);

        if (_recordHandle == 0)
        {
            var startMsg = $"Failed to start recording on device (BASS index {RecordingDeviceIndex}): {Bass.LastError}";
            _logger.LogError("{Message}", startMsg);
            AudioPlaybackErrorOccurred?.Invoke(this, startMsg);
            return false;
        }

        if (!Bass.ChannelPlay(_recordHandle))
            _logger.LogWarning("ChannelPlay on record handle returned false: {Error}", Bass.LastError);

        return true;
    }

    /// <summary>
    /// Stops and frees the shared microphone capture stream if running. No-op when idle.
    /// </summary>
    private void StopMicCapture()
    {
        if (_recordHandle == 0) return;

        if (!Bass.ChannelStop(_recordHandle))
            _logger.LogWarning("ChannelStop on record handle {Handle} returned false: {Error}",
                _recordHandle, Bass.LastError);
        if (!Bass.StreamFree(_recordHandle))
            _logger.LogWarning("StreamFree on record handle {Handle} returned false: {Error}",
            _recordHandle, Bass.LastError);
        _recordHandle = 0;
        MicLevelChanged?.Invoke(this, new MicLevelChangedEventArgs(0, 0));
    }

    public async Task UpdateDisplayNameAsync(string newDisplayName)
    {
        if (_client is not { IsAuthenticated: true }) return;
        await _client.SetDisplayNameAsync(newDisplayName);
    }

    public void SetVolume(int frequencyKhz, Guid slotId, float volumeValue)
    {
        _playbackService?.SetFrequencyVolume(frequencyKhz, slotId, volumeValue);
    }

    public void SetPan(int frequencyKhz, Guid slotId, int pan)
    {
        _playbackService?.SetFrequencyPan(frequencyKhz, slotId, pan);
    }

    public void EnableFrequency(int frequencyKhz, Guid slotId)
    {
        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
        if (tunedFrequencyData == null) return;
        tunedFrequencyData.IsEnabled = true;
        _playbackService?.TuneFrequency(frequencyKhz, slotId);
        OnStatusMessage($"{frequencyKhz / 1000d:F3} enabled");
    }

    public void DisableFrequency(int frequencyKhz, Guid slotId)
    {
        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
        if (tunedFrequencyData == null) return;
        tunedFrequencyData.IsEnabled = false;
        // Only stop TX if this slot owns it and no other enabled slot remains.
        if (_activeTransmissionSlots.TryGetValue(frequencyKhz, out var txSlot) && txSlot == slotId &&
            !_tunedSlots.Any(k => k.Key.FreqKhz == frequencyKhz && k.Value.IsEnabled))
        {
            StopTransmissionAsync(frequencyKhz).Wait(50);
        }
        _playbackService?.UntuneFrequency(frequencyKhz, slotId);
        OnStatusMessage($"{frequencyKhz / 1000d:F3} disabled");
    }

    public void SetSquelch(int frequencyKhz, Guid slotId, bool isSquelchClosed)
    {
        _signalStrengthTracker.SetSquelchState(frequencyKhz, !isSquelchClosed);
        _playbackService?.SetSquelchLevel(frequencyKhz, slotId, isSquelchClosed ? 1f : 0f);
    }

    public void SetSatcomState(int frequencyKhz, Guid slotId, bool isActive, double frameErrorRate,
        double burstSeverity, double frameDurationSeconds = 0.0225, double propagationLatencySeconds = 0.0)
    {
        if (isActive) _satcomActiveFrequencies[frequencyKhz] = 0;
        else _satcomActiveFrequencies.TryRemove(frequencyKhz, out _);

        _playbackService?.SetSatcomState(frequencyKhz, slotId, isActive, frameErrorRate, burstSeverity,
            frameDurationSeconds, propagationLatencySeconds);
    }

    public void SetEncryption(int frequencyKhz, Guid slotId, bool enc, int encKey, bool hqOn, bool cryptoCapable)
    {
        var foundTxSlot = _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
        if (foundTxSlot)
        {
            tunedFrequencyData!.Enc = enc;
            tunedFrequencyData.EncKey = encKey;
            tunedFrequencyData.HqOn = hqOn;
        }
        _logger.LogWarning(
            "SetEncryption: freq={FreqKhz} slotId={SlotId} enc={Enc} encKey={EncKey} foundTxSlot={FoundTxSlot} knownTunedSlotsForFreq={KnownSlots}",
            frequencyKhz, slotId, enc, encKey, foundTxSlot,
            string.Join(",", _tunedSlots.Keys.Where(k => k.FreqKhz == frequencyKhz).Select(k => k.SlotId)));

        _playbackService?.SetSlotEncryption(frequencyKhz, slotId, enc, encKey, hqOn, cryptoCapable);
    }

    public void SetOwnPositionMode(IOpenFreqService.Mode newMode)
    {
        if (newMode == OwnPositionMode) return;

        switch (newMode)
        {
            case IOpenFreqService.Mode.BMS:
                {
                    _dcsExportService.Stop();
                    _acmiClientService.Stop();
                    if (_falconSharedMemoryService.State == ServiceState.Stopped)
                    {
                        _falconSharedMemoryService.Start();
                    }

                    break;
                }
            case IOpenFreqService.Mode.GCI:
                _dcsExportService.Stop();
                _falconSharedMemoryService.Stop();
                break;
            case IOpenFreqService.Mode.DCS:
                _acmiClientService.Stop();
                _falconSharedMemoryService.Stop();
                _falconRadioSharedMemoryService.Stop();
                if (_dcsExportService.State == ServiceState.Stopped)
                    _dcsExportService.Start();
                LoadCurrentDcsHeightmapIfAvailable();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(newMode), newMode, null);
        }

        OwnPositionMode = newMode;
    }

    /// <summary>
    /// Check if currently transmitting on a frequency
    /// </summary>
    public bool IsTransmitting(int frequency)
    {
        return _activeTransmissionsAndMutedFrequencies.ContainsKey(frequency);
    }

    /// <summary>
    /// Load heightmap for terrain-aware RF calculations
    /// </summary>
    public void LoadHeightmap(string path, int width = 32768, int height = 32768, int bytesPerSample = 2,
        double? cellSizeMeters = null)
    {
        _signalCalculator?.Dispose();
        _signalCalculator = null;

        // Heightmap is optional (see MainWindowViewModel.ConnectAsync) -- terrain-aware LOS/
        // attenuation just doesn't apply while it's unset, everything downstream already treats
        // _signalCalculator == null as "no terrain data available" rather than an error. A bad/
        // stale configured path must degrade the same way, not throw and break connecting.
        try
        {
            // Cell size = theater world size / DEM resolution
            var cellSize = cellSizeMeters ?? BmsHeightmapConverter.HEIGHTMAP_SIZE_METERS / width;
            _signalCalculator = _signalCalculatorFactory.Create(path, width, height, bytesPerSample, cellSize,
                _loggerFactory);
            OnStatusMessage($"Heightmap loaded: {path}");
            _logger.LogDebug($"Heightmap loaded: {path}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load heightmap from {Path} -- continuing without terrain data", path);
            OnStatusMessage($"Heightmap not loaded ({ex.Message}) -- continuing without terrain data");
        }
    }

    public double? SampleTerrainElevationMeters(double xMeters, double yMeters)
    {
        return _signalCalculator?.SampleElevation(xMeters, yMeters);
    }

    private void OnDcsHeightmapChanged(object? sender, DcsHeightmapChangedEventArgs e)
    {
        if (OwnPositionMode != IOpenFreqService.Mode.DCS) return;
        LoadDcsHeightmap(e.HeightmapInfo);
    }

    private void LoadCurrentDcsHeightmapIfAvailable()
    {
        var heightmapInfo = _dcsExportService.HeightmapInfo;
        if (heightmapInfo != null)
            LoadDcsHeightmap(heightmapInfo);
    }

    private void LoadDcsHeightmap(DcsHeightmapInfo heightmapInfo)
    {
        if (!heightmapInfo.Ready || string.IsNullOrWhiteSpace(heightmapInfo.RawPath))
            return;

        if (!File.Exists(heightmapInfo.RawPath))
        {
            _logger.LogWarning("DCS heightmap metadata exists, but raw file is missing: {Path}",
                heightmapInfo.RawPath);
            return;
        }

        if (string.Equals(_loadedDcsHeightmapPath, heightmapInfo.RawPath, StringComparison.OrdinalIgnoreCase))
            return;

        LoadHeightmap(heightmapInfo.RawPath, heightmapInfo.SamplesX, heightmapInfo.SamplesZ, 2,
            heightmapInfo.CellSizeMeters);
        _loadedDcsHeightmapPath = heightmapInfo.RawPath;
    }

    private bool RecordProcedure(int handle, IntPtr buffer, int length, IntPtr user)
    {
        try
        {
            if (length <= 0)
                return true;

            // While not transmitting we keep the mic open purely so the normalizer can track
            // the room's noise floor. Feed those idle samples to the estimator and return —
            // nothing is sent, monitored, or recorded until PTT is held. This also freezes the
            // gate during transmission: the estimate only advances on this idle path.
            if (_activeTransmissionsAndMutedFrequencies.Count == 0)
            {
                if (MicNormalizationEnabled)
                {
                    // Read directly from the input buffer;
                    // we'll actually allocate a GC array and copy below
                    // if we actually need to grab a copy for sending.
                    ReadOnlySpan<short> samples;
                    unsafe
                    {
                        samples = new ReadOnlySpan<short>((void*)buffer, length / sizeof(short));
                    }

                    PublishMicLevel(samples, InputGain);
                    _micNormalizer.UpdateNoiseFloor(samples);
                }
                else if (InputMeterEnabled)
                {
                    unsafe
                    {
                        PublishMicLevel(new ReadOnlySpan<short>((void*)buffer, length / sizeof(short)), InputGain);
                    }
                }

                return true;
            }

            // Handle first packet - BASS accumulates audio during initialization
            // Calculate expected size for 20ms at 48kHz, mono, 16-bit
            // 48000 samples/sec ÷ 50 = 960 samples per 20ms
            // 960 samples × 2 bytes/sample × 1 channel = 1920 bytes
            int expectedBytes = (OpenFreqRtcClient.SAMPLE_RATE / 50) * 2;

            // TODO: I dont think we need this anymore with the shorter dsp updates. Deactivated for now
            expectedBytes = 10000;

            if (length > expectedBytes)
            {
                _logger.LogWarning("Recording packet oversized: {Length} bytes, truncating to {ExpectedBytes}",
                    length, expectedBytes);
                // Option 1: Only use the LAST 20ms (most recent audio)
                buffer = IntPtr.Add(buffer, length - expectedBytes);
                length = expectedBytes;

                // Option 2: Skip first packet entirely
                //_isFirstPacket = false;
                //return true;
            }

            // Copy audio data once
            short[] audioData = new short[length / 2];
            Marshal.Copy(buffer, audioData, 0, audioData.Length);

            // Normalize transmit level so loud/quiet mics land near a common
            // reference. Applied before sidetone + send so the operator hears
            // (and peers receive) the same normalized audio.
            if (MicNormalizationEnabled)
                _micNormalizer.Process(audioData, audioData.Length);
            ApplyInputGain(audioData);
            PublishMicLevel(audioData);

            // If every currently-active transmission is tone-sourced (e.g. ARC-186 TONE), replace
            // the real mic samples with a synthesized tone before they go out. If a real voice
            // transmission is ALSO active this cycle, it wins — see StartToneTransmissionAsync.
            if (!_activeTransmissionsAndMutedFrequencies.IsEmpty &&
                _activeTransmissionsAndMutedFrequencies.Keys.All(_toneTransmissionFrequencies.ContainsKey))
            {
                GenerateTone(audioData);
            }

            // Convert mic to float once and fan out to sidetone (speaker loopback) and/or the
            // session recording (own voice, rendered through radio FX downstream).
            // Pre-allocated buffer avoids GC allocation on the hot audio path.
            bool wantSidetone = _playbackService is { SidetoneEnabled: true };
            bool wantRecord = _playbackService is { IsCapturing: true };
            if (wantSidetone || wantRecord)
            {
                int n = audioData.Length;
                if (n > _sidetonePushBuffer.Length)
                    _sidetonePushBuffer = new float[n * 2];
                for (int i = 0; i < n; i++)
                    _sidetonePushBuffer[i] = audioData[i] / (float)short.MaxValue;
                var span = _sidetonePushBuffer.AsSpan()[..n];
                if (wantSidetone) _playbackService!.PushSidetone(span);
                if (wantRecord) _playbackService!.PushOwnVoiceForRecording(span);
            }

            // Send to ALL active frequencies
            var frequenciesData =
                new List<(int frequencyKhz, double txPowerWatts, double ppm, Vector3? position, Vector3? velocity,
                    Vector3? dcsPosition, AmbientNoiseType ambientNoiseType, bool enc, int encKey, bool hqOn,
                    double? latitudeDeg, double? longitudeDeg, double? altitudeMeters)>();

            // List of frequencies that got disabled in the meantime
            var disabledFrequencies = new List<int>();
            foreach (var transmission in _activeTransmissionsAndMutedFrequencies)
            {
                var frequencyKhz = transmission.Key;
                _activeTransmissionSlots.TryGetValue(frequencyKhz, out var txSlotId);
                _tunedSlots.TryGetValue((frequencyKhz, txSlotId), out var radioStationData);
                if (radioStationData == null)
                {
                    _logger.LogError($"Frequency {frequencyKhz} has no RadioStationData");
                    continue;
                }

                if (!radioStationData.IsEnabled)
                {
                    _logger.LogWarning($"Recording on frequency {frequencyKhz} which is not active");
                    disabledFrequencies.Add(frequencyKhz);
                    continue;
                }

                var position = GetOwnPosition(frequencyKhz, txSlotId) ?? new Vector3(0, 0, 0);
                var velocity = GetOwnVelocity(frequencyKhz, txSlotId);
                var dcsPosition = GetOwnDcsLocalPosition(frequencyKhz, txSlotId);
                var geodeticPosition = GetOwnGeodeticPosition(frequencyKhz, txSlotId);

                var txPowerWatts = radioStationData.RadioStation.Preset.GetTxPower(GetRadioType(frequencyKhz));
                if (radioStationData.Enc != _lastLoggedTxEncByFreq.GetValueOrDefault(frequencyKhz))
                {
                    _logger.LogWarning(
                        "SendAudio TX metadata: freq={FreqKhz} txSlotId={TxSlotId} enc={Enc} encKey={EncKey}",
                        frequencyKhz, txSlotId, radioStationData.Enc, radioStationData.EncKey);
                    _lastLoggedTxEncByFreq[frequencyKhz] = radioStationData.Enc;
                }
                frequenciesData.Add((frequencyKhz,
                    txPowerWatts, radioStationData.RadioStation.Ppm,
                    position, velocity, dcsPosition, radioStationData.RadioStation.Preset.AmbientNoiseType,
                    radioStationData.Enc, radioStationData.EncKey, radioStationData.HqOn,
                    geodeticPosition?.LatitudeDeg, geodeticPosition?.LongitudeDeg, geodeticPosition?.AltitudeMeters));
            }

            if (frequenciesData.Count == 0)
            {
                var now = DateTime.UtcNow;
                if (now >= _nextEmptyTransmissionWarningUtc)
                {
                    _nextEmptyTransmissionWarningUtc = now.AddSeconds(1);
                    _logger.LogWarning("Mic audio captured while transmitting, but no valid frequency metadata was available");
                    OnStatusMessage("Mic audio captured, but no valid transmit frequency was available");
                }
                return true;
            }

            // Tell the recorder how to render our own voice "as if heard from the same position":
            // default (zero-distance) params for the transmitting radio + its ambient SFX.
            if (wantRecord && frequenciesData.Count > 0)
            {
                var first = frequenciesData[0];
                _playbackService!.SetOwnVoiceRecordParams(
                    FastPathAudioSim.GetDefaultAudioParams(first.frequencyKhz, (float)first.ppm),
                    first.ambientNoiseType);
            }

            _client?.SendAudio(audioData, frequenciesData);

            // Clean up any frequencies which might have been disabled in the meantime
            foreach (var disabledFrequency in disabledFrequencies)
            {
                _activeTransmissionsAndMutedFrequencies.TryRemove(disabledFrequency, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error sending audio: {ExMessage}", ex.Message);
            OnStatusMessage($"Error sending audio: {ex.Message}");
        }

        return true;
    }

    // ~1kHz attention tone, matching a real VHF-FM TONE key. Phase is kept in _tonePhase across
    // calls so consecutive 20ms buffers join without an audible click at the seam.
    private const double ToneFrequencyHz = 1000.0;
    private const double ToneAmplitude = 0.6 * short.MaxValue;

    private void GenerateTone(short[] buffer)
    {
        double phaseStep = 2.0 * Math.PI * ToneFrequencyHz / OpenFreqRtcClient.SAMPLE_RATE;
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (short)(ToneAmplitude * Math.Sin(_tonePhase));
            _tonePhase += phaseStep;
            if (_tonePhase > 2.0 * Math.PI) _tonePhase -= 2.0 * Math.PI;
        }
    }

    private void ApplyInputGain(short[] samples)
    {
        var gain = InputGain;
        if (Math.Abs(gain - 1.0d) < 0.0001d)
            return;

        for (var i = 0; i < samples.Length; i++)
        {
            var scaled = (int)Math.Round(samples[i] * gain);
            samples[i] = (short)Math.Clamp(scaled, short.MinValue + 1, short.MaxValue);
        }
    }

    private void PublishMicLevel(ReadOnlySpan<short> samples, double previewGain = 1.0d)
    {
        if (samples.Length == 0)
            return;

        var now = DateTime.UtcNow;
        if (now - _lastMicLevelEventUtc < TimeSpan.FromMilliseconds(50))
            return;

        _lastMicLevelEventUtc = now;

        double sumSquares = 0;
        var peak = 0;
        foreach (var sample in samples)
        {
            var adjusted = previewGain == 1.0d
                ? sample
                : (int)Math.Clamp(Math.Round(sample * previewGain), short.MinValue + 1, short.MaxValue);
            var value = adjusted == short.MinValue ? short.MaxValue : Math.Abs(adjusted);
            if (value > peak)
                peak = value;
            sumSquares += (double)adjusted * adjusted;
        }

        var rms = Math.Sqrt(sumSquares / samples.Length) / short.MaxValue;
        var peakNormalized = peak / (double)short.MaxValue;
        MicLevelChanged?.Invoke(this, new MicLevelChangedEventArgs(
            Math.Clamp(rms, 0, 1),
            Math.Clamp(peakNormalized, 0, 1)));
    }

    private Vector3? GetOwnPosition(int frequencyKhz, Guid slotId)
    {
        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
        if (tunedFrequencyData == null)
        {
            _logger.LogWarning("No frequency data found, assuming own position of (0,0,0)");
            return null;
        }

        switch (tunedFrequencyData.RadioStation.Type)
        {
            case RadioStationData.RadioStationType.BMS:
                if (_falconSharedMemoryService.State != ServiceState.Connected ||
                    _falconSharedMemoryService.Position == null) return null;

                return new Vector3(BmsHeightmapConverter.ToHeightmap(_falconSharedMemoryService.Position.X,
                    _falconSharedMemoryService.Position.Y, _falconSharedMemoryService.Position.Z));

            case RadioStationData.RadioStationType.DCS:
                if (_dcsExportService.State != ServiceState.Connected ||
                    _dcsExportService.Position == null) return null;

                return new Vector3(DcsHeightmapConverter.ToHeightmap(_dcsExportService.Position,
                    _dcsExportService.HeightmapInfo));

            case RadioStationData.RadioStationType.STATIONARY:
                var position = tunedFrequencyData.RadioStation.Vector3;
                return new Vector3(position.X, position.Y,
                    position.Z + tunedFrequencyData.RadioStation.Preset.AntennaElevation_m);

            case RadioStationData.RadioStationType.ACMI:
                var acmiAircraftId = tunedFrequencyData.RadioStation.AcmiAircraftId;
                if (acmiAircraftId == null) return null;

                var aircraft = _acmiClientService.GetAircraft(acmiAircraftId);
                if (aircraft == null) return null;
                return new Vector3(AcmiHeightmapConverter.ToHeightmap(aircraft.Transform.U, aircraft.Transform.V,
                    aircraft.Transform.Altitude + tunedFrequencyData.RadioStation.Preset.AntennaElevation_m));
            default:
                return null;
        }
    }

    private Vector3? GetOwnDcsLocalPosition(int frequencyKhz, Guid slotId)
    {
        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
        if (tunedFrequencyData?.RadioStation.Type != RadioStationData.RadioStationType.DCS)
            return null;

        if (_dcsExportService.State != ServiceState.Connected || _dcsExportService.Position == null)
            return null;

        var position = _dcsExportService.Position;
        return new Vector3(position.X, position.Y, position.Z);
    }

    /// <summary>Own geodetic position, when available -- unlike <see cref="GetOwnDcsLocalPosition"/>
    /// this travels on the wire in <see cref="FrequencyTransmission"/> so a receiver with no DCS
    /// export of its own (an SRS-bridged peer) can still compute a distance-based free-space signal
    /// estimate; see <see cref="FreeSpacePathModel"/>. DCS-mode only for now, same as the local-
    /// position case -- BMS/GCI have no geodetic fix to report.</summary>
    private (double LatitudeDeg, double LongitudeDeg, double AltitudeMeters)? GetOwnGeodeticPosition(
        int frequencyKhz, Guid slotId)
    {
        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
        if (tunedFrequencyData?.RadioStation.Type != RadioStationData.RadioStationType.DCS)
            return null;

        if (_dcsExportService.State != ServiceState.Connected)
            return null;

        return (_dcsExportService.Latitude, _dcsExportService.Longitude, _dcsExportService.AltitudeMsl);
    }

    private Vector3? GetOwnVelocity(int frequencyKhz, Guid slotId)
    {
        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
        if (tunedFrequencyData == null)
        {
            return null;
        }

        switch (tunedFrequencyData.RadioStation.Type)
        {
            case RadioStationData.RadioStationType.BMS:
                if (_falconSharedMemoryService.State != ServiceState.Connected ||
                    _falconSharedMemoryService.Velocity == null) return null;

                // BMS velocity is stored as (East, North, Up) in ft/s
                // Convert to (North, East, Up) in m/s to match heightmap/ACMI convention
                const double feetToMeters = 0.3048;
                var bmsVel = _falconSharedMemoryService.Velocity;

                return new Vector3(
                    bmsVel.Y * feetToMeters, // North (swap Y to first component)
                    bmsVel.X * feetToMeters, // East (swap X to second component)
                    bmsVel.Z * feetToMeters // Up (Z already inverted to Up in service)
                );

            case RadioStationData.RadioStationType.DCS:
                return _dcsExportService.Velocity == null
                    ? null
                    : new Vector3(DcsHeightmapConverter.VelocityToHeightmap(_dcsExportService.Velocity));

            case RadioStationData.RadioStationType.STATIONARY:
                // we are stationary, duh
                return null;

            case RadioStationData.RadioStationType.ACMI:
                var acmiAircraftId = tunedFrequencyData.RadioStation.AcmiAircraftId;
                if (acmiAircraftId == null) return null;

                var aircraft = _acmiClientService.GetAircraft(acmiAircraftId);
                if (aircraft == null) return null;

                return AcmiHeightmapConverter.GetVelocityVector(aircraft.Mach, aircraft.Transform.Altitude,
                    aircraft.Transform.Pitch,
                    aircraft.Transform.Yaw);
            default:
                return null;
        }
    }

    /// <summary>Loads a bundled encryption tone asset embedded via the AudioEffects/ logical name
    /// prefix (see OpenFreq.Client.csproj). Returns null if not found — encryption tones are then
    /// simply skipped rather than the app failing to start.</summary>
    private static byte[]? LoadEmbeddedAudioAsset(string logicalName)
    {
        var assembly = typeof(OpenFreqService).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalName);
        if (stream == null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private void OnPlaybackUserFacingError(string message)
    {
        _logger.LogError("RadioPlayback user-facing error: {Message}", message);
        AudioPlaybackErrorOccurred?.Invoke(this, message);
    }

    /// <summary>
    /// Stops the active recording stream and restarts it on <paramref name="deviceIndex"/>.
    /// Called from the <see cref="RecordingDeviceIndex"/> setter when a recording is live.
    /// Safe to call with _recordHandle == 0 (no-op).
    /// </summary>
    private void RestartRecordingOnNewDevice(int deviceIndex)
    {
        if (_recordHandle == 0) return;

        // RecordingDeviceIndex has already been updated to deviceIndex by the caller, so the
        // shared helpers pick up the new device. Reopen on it.
        StopMicCapture();
        if (StartMicCapture())
            _logger.LogInformation("Recording restarted on BASS device {Index}", deviceIndex);
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

        if (e.State == ConnectionState.Disconnected)
        {
            _tunedSlots.Clear();
            _activeTransmissionSlots.Clear();
        }

        // Open continuous capture once connected (for noise-floor tracking) / close it on drop.
        UpdateMicCaptureState();

        ConnectionStateChanged?.Invoke(this, e);
    }

    private void OnClientAuthenticated(object? sender, AuthenticationEventArgs e)
    {
        DcsLineOfSightEnabled = e.DcsLineOfSightEnabled;
        OnStatusMessage($"Authenticated - Peer ID: {e.PeerId}, Audio Port: {e.AudioPort}");
        OnStatusMessage($"DCS LOS constraint {(DcsLineOfSightEnabled ? "enabled" : "disabled")} by server");
        OnAllPeersStatusUpdateReceived(sender, new AllPeersStatusEventArgs(e.Peers));
        _ = _client?.SendModeUpdateAsync();

        if (AutoRecordInGameMode) StartRecording();
    }

    private void OnClientFrequencyJoined(object? sender, FrequencyJoinedEventArgs e)
    {
        FrequencyJoined?.Invoke(this, e);
        foreach (var peer in e.Peers)
            CreateAudioStreamForPeer(e.FrequencyKhz, peer.Id);

        // Update all slots tuned to this frequency to Connected.
        OnFrequencyConnectionStatusChanged(e.FrequencyKhz, Channel.ChannelConnectionStatus.Connected, e.Peers);
    }

    private void OnClientFrequencyLeft(object? sender, FrequencyLeftEventArgs e)
    {
        // Per-slot disconnection and playback untune are handled in LeaveFrequencyAsync.
        // Nothing extra needed here.
    }

    private void OnClientPeerJoined(object? sender, PeerEventArgs e)
    {
        CreateAudioStreamForPeer(e.FrequencyKhz, e.PeerId);
        PeerJoined?.Invoke(this, e);
    }

    private void CreateAudioStreamForPeer(int frequencyKhz, string peerId)
    {
        // Create stream for this peer-frequency combination
        string streamId = GetStreamId(peerId, frequencyKhz);

        // Start with default params (will update when we get position data)
        var audioParams = FastPathAudioSim.GetDefaultAudioParams(frequencyKhz);

        _playbackService?.StartPushStream(
            streamId,
            OpenFreqRtcClient.SAMPLE_RATE,
            1,
            audioParams
        );

        // Track it
        if (!_peerStreams.ContainsKey(peerId))
            _peerStreams[peerId] = new Dictionary<int, string>();
        _peerStreams[peerId][frequencyKhz] = streamId;
    }

    private static string GetStreamId(string peerId, double frequencyKhz)
    {
        return peerId + ":" + frequencyKhz;
    }

    private void OnClientPeerLeft(object? sender, PeerEventArgs e)
    {
        // Remove stream when peer leaves
        if (_peerStreams.TryGetValue(e.PeerId, out var freqs))
        {
            if (freqs.TryGetValue(e.FrequencyKhz, out var streamId))
            {
                _playbackService?.StopStream(streamId);
                freqs.Remove(e.FrequencyKhz);
            }
        }

        PeerLeft?.Invoke(this, e);
    }

    private void OnClientTransmissionStatusChanged(object? sender, TransmissionStateEventArgs e)
    {
        var status = e.IsTransmitting
            ? Channel.ChannelTransmissionStatus.Transmitting
            : Channel.ChannelTransmissionStatus.Idle;
        OnFrequencyTransmissionStatusChanged(e.FrequencyKhz, status, is3d: true);
    }

    private void OnClientPeerTransmissionStatusChanged(object? sender, PeerTransmissionEventArgs e)
    {
        OnPeerActivity(this,
            new PeerActivityEventArgs(e.FrequencyKhz,
                new PeerData(e.PeerId, e.PeerDisplayName,
                    e.IsTransmitting ? PeerData.PeerStatus.Transmitting : PeerData.PeerStatus.Receiving), e.Is3d));

        // Own TX is authoritative: don't let peer state overwrite Transmitting in subscribers
        OnFrequencyTransmissionStatusChanged(e.FrequencyKhz,
            _activeTransmissionsAndMutedFrequencies.ContainsKey(e.FrequencyKhz)
                ? Channel.ChannelTransmissionStatus.Transmitting
                : e.IsTransmitting
                    ? Channel.ChannelTransmissionStatus.Receiving
                    : Channel.ChannelTransmissionStatus.Idle,
            e.Is3d);
    }

    /// <summary>
    /// Route received audio to playback service with RF effects
    /// </summary>
    /// <summary>
    /// Route received audio to playback service with RF effects
    /// </summary>
    private void OnClientAudioDataReceived(object? sender, AudioDataEventArgs e)
    {
        if (e.AudioData.Length == 0)
        {
            _logger.LogWarning("Audio data received with 0 size");
            return;
        }

        if (e.Metadata.Frequencies.Count == 0)
        {
            _logger.LogWarning("Audio data received without frequencies, dropping");
            return;
        }

        foreach (var frequencyTransmission in e.Metadata.Frequencies)
        {
            if (_activeTransmissionsAndMutedFrequencies.ContainsKey(frequencyTransmission.Khz))
            {
                _logger.LogDebug("Receiving transmission when we are sending - dropping");
                continue;
            }

            var streamId = GetStreamId(e.PeerId, frequencyTransmission.Khz);

            var audioParams = CalculateAudioParamsSync(frequencyTransmission, e.PeerId);
            _signalStrengthTracker.UpdateSignalStrength(audioParams.RadioFrequencyKHz, audioParams);

            // SATCOM audio is never gated by terrestrial distance/LOS between the two aircraft --
            // see _satcomActiveFrequencies' own doc comment. audioParams itself is still computed
            // above (for the signal-strength UI and other callers that read it), just not trusted
            // to block delivery here.
            var terrestrialBlockApplies = !_satcomActiveFrequencies.ContainsKey(frequencyTransmission.Khz);

            if (audioParams.SignalBlocked && terrestrialBlockApplies)
            {
                if (_playbackService?.IsStreamActive(streamId) ?? false)
                {
                    _playbackService.UpdateStreamParams(streamId, audioParams);
                    _playbackService.ClearStreamBuffer(streamId);
                }

                continue;
            }

            lock (_streamCreationLock)
            {
                var streamExists = _playbackService?.IsStreamActive(streamId) ?? false;

                if (!streamExists)
                {
                    _logger.LogWarning(
                        "Lazy-creating stream {StreamId} on {FreqMhz:F3} MHz — PeerJoined arrived after audio",
                        streamId, frequencyTransmission.Khz / 1000.0);

                    _playbackService?.StartPushStream(
                        streamId,
                        OpenFreqRtcClient.SAMPLE_RATE,
                        1,
                        audioParams);

                    // TuneFrequency is called per-slot in JoinFrequencyAsync; no action needed here.
                    if (!IsAnySlotTuned(frequencyTransmission.Khz))
                    {
                        _logger.LogWarning(
                            "Lazy stream {StreamId}: frequency {FreqMhz:F3} MHz not tuned on any slot — audio will be silenced by DSP",
                            streamId, frequencyTransmission.Khz / 1000.0);
                    }
                }
                else
                {
                    // Update existing stream params
                    _playbackService?.UpdateStreamParams(streamId, audioParams);
                }
            }

            var ambientNoiseType = frequencyTransmission.AmbientNoiseType;

            // Push audio data immediately
            _playbackService?.PushAudioData(streamId, e.AudioData, ambientNoiseType,
                frequencyTransmission.Enc, frequencyTransmission.EncKey, frequencyTransmission.HqOn);
        }
    }

    private AudioParams CalculateAudioParamsSync(FrequencyTransmission frequencyTransmission, string peerId)
    {
        var cacheKey = (peerId, frequencyTransmission.Khz);

        // All slots on the same frequency share the same RadioStationData (position/velocity).
        // Pick any tuned slot's key for position lookup.
        var anySlotKey = _tunedSlots.Keys.FirstOrDefault(k => k.FreqKhz == frequencyTransmission.Khz);
        var ownPosition = anySlotKey != default ? GetOwnPosition(anySlotKey.FreqKhz, anySlotKey.SlotId) : null;
        var ownVelocity = anySlotKey != default ? GetOwnVelocity(anySlotKey.FreqKhz, anySlotKey.SlotId) : null;
        var dcsLineOfSight = RequestDcsLineOfSight(cacheKey, frequencyTransmission);
        var receiverData = GetAnyTunedSlot(frequencyTransmission.Khz);
        var receiverSensitivityDb = GetReceiverSensitivityDb(frequencyTransmission.Khz, receiverData);

        if (frequencyTransmission.Position == null || ownPosition == null || _signalCalculator == null)
        {
            // Inputs missing (e.g. a concealed frame without position).
            var fallbackParams = TryCreateDcsFreeSpaceAudioParams(frequencyTransmission, anySlotKey,
                receiverSensitivityDb)
                                 ?? CloneAudioParams(LastKnownOrDefaultAudioParams(cacheKey,
                                     frequencyTransmission.Khz));
            return PrepareReceivedAudioParams(fallbackParams, dcsLineOfSight, frequencyTransmission, anySlotKey);
        }

        // Check cache
        var now = DateTime.UtcNow;

        if (_audioParamsCache.TryGetValue(cacheKey, out var cached) &&
            (now - cached.LastCalculated) < _audioParamsCacheDuration)
        {
            return PrepareReceivedAudioParams(CloneAudioParams(cached.Params), dcsLineOfSight,
                frequencyTransmission, anySlotKey);
        }

        // Calculate — all slots on the same freq share the same RadioStationData, so any slot's data is fine.
        if (receiverData == null)
        {
            return PrepareReceivedAudioParams(CloneAudioParams(LastKnownOrDefaultAudioParams(cacheKey,
                frequencyTransmission.Khz)), dcsLineOfSight, frequencyTransmission, anySlotKey);
        }

        var audioParams = _signalCalculator.CalculateAudioParams(
            frequencyTransmission.Position.X, frequencyTransmission.Position.Y, frequencyTransmission.Position.Z,
            ownPosition.X, ownPosition.Y, ownPosition.Z,
            frequencyTransmission.Khz, (float)frequencyTransmission.Ppm,
            frequencyTransmission.TxPowerWatts, receiverSensitivityDb,
            txAltitudeIsMSL: true, rxAltitudeIsMSL: true,
            txVelocity: frequencyTransmission.Velocity?.ToTuple(),
            rxVelocity: ownVelocity?.ToTuple()
        );

        // Update cache
        _audioParamsCache[cacheKey] = new AudioParamsCacheEntry
        {
            Params = audioParams,
            LastCalculated = now
        };

#if DEBUG
        _logger.LogDebug("Calculated audio params {AudioParams}", audioParams);
#endif

        return PrepareReceivedAudioParams(CloneAudioParams(audioParams), dcsLineOfSight, frequencyTransmission,
            anySlotKey);
    }

    private AudioParams PrepareReceivedAudioParams(AudioParams audioParams, DcsLineOfSightResult? dcsLineOfSight,
        FrequencyTransmission frequencyTransmission, (int FreqKhz, Guid SlotId) slotKey)
    {
        ApplyDcsDistanceAndHorizonLoss(audioParams, frequencyTransmission, slotKey);
        return ApplyDcsLineOfSightLoss(audioParams, dcsLineOfSight);
    }

    // AM/FM-aware radio-type classification, shared by TX power and RX sensitivity lookups
    // below: FM (see FastPathAudioSim.bandConfigs for which real bands are FM) typically runs
    // more TX power and has worse RX sensitivity than AM at the same band, per
    // RadioStationPreset.GetTxPower/GetRxSensitivity.
    private static BackgroundNoiseGenerator.RadioType GetRadioType(int frequencyKhz) =>
        FastPathAudioSim.GetBandConfig(frequencyKhz).Modulation == ModulationType.FM
            ? BackgroundNoiseGenerator.RadioType.FM
            : RadioStationPreset.IsVHF(frequencyKhz)
                ? BackgroundNoiseGenerator.RadioType.VHF_AM
                : BackgroundNoiseGenerator.RadioType.UHF_AM;

    private static double GetReceiverSensitivityDb(int frequencyKhz, TunedFrequencyData? receiverData)
    {
        if (receiverData != null)
        {
            return receiverData.RadioStation.Preset.GetRxSensitivity(GetRadioType(frequencyKhz));
        }

        return RadioStationPreset.IsVHF(frequencyKhz) ? -113.0d : -107.0d;
    }

    /// <summary>Resolves a distance/altitude pair for the free-space fallback model, preferring
    /// DCS mission-local positions (most precise -- both sides in the same live mission) and
    /// falling back to geodetic lat/lon/alt when either side has no DCS-local position at all
    /// (e.g. the transmitter is an SRS-bridged peer, which only ever reports geodetic position --
    /// see FrequencyTransmission.LatitudeDeg/LongitudeDeg/AltitudeMeters).</summary>
    private (double DistanceMeters, double TxAltitudeMeters, double RxAltitudeMeters)?
        TryResolveDistanceAndAltitudes(FrequencyTransmission frequencyTransmission,
            (int FreqKhz, Guid SlotId) slotKey)
    {
        if (OwnPositionMode != IOpenFreqService.Mode.DCS || slotKey == default)
            return null;

        var ownDcsPosition = GetOwnDcsLocalPosition(slotKey.FreqKhz, slotKey.SlotId);
        if (ownDcsPosition != null && frequencyTransmission.DcsPosition != null)
        {
            var remote = frequencyTransmission.DcsPosition;
            var dx = remote.X - ownDcsPosition.X;
            var dy = remote.Y - ownDcsPosition.Y;
            var dz = remote.Z - ownDcsPosition.Z;
            return (Math.Sqrt(dx * dx + dy * dy + dz * dz), ownDcsPosition.Y, remote.Y);
        }

        var ownGeodetic = GetOwnGeodeticPosition(slotKey.FreqKhz, slotKey.SlotId);
        if (ownGeodetic != null && frequencyTransmission.LatitudeDeg != null &&
            frequencyTransmission.LongitudeDeg != null && frequencyTransmission.AltitudeMeters != null)
        {
            var distance = FreeSpacePathModel.GreatCircleDistanceMeters(
                ownGeodetic.Value.LatitudeDeg, ownGeodetic.Value.LongitudeDeg, ownGeodetic.Value.AltitudeMeters,
                frequencyTransmission.LatitudeDeg.Value, frequencyTransmission.LongitudeDeg.Value,
                frequencyTransmission.AltitudeMeters.Value);
            return (distance, ownGeodetic.Value.AltitudeMeters, frequencyTransmission.AltitudeMeters.Value);
        }

        return null;
    }

    private AudioParams? TryCreateDcsFreeSpaceAudioParams(FrequencyTransmission frequencyTransmission,
        (int FreqKhz, Guid SlotId) slotKey, double receiverSensitivityDb)
    {
        var resolved = TryResolveDistanceAndAltitudes(frequencyTransmission, slotKey);
        if (resolved == null)
            return null;

        return FreeSpacePathModel.CreateBaseAudioParams(frequencyTransmission.Khz,
            frequencyTransmission.TxPowerWatts, frequencyTransmission.Ppm,
            Math.Max(1.0d, resolved.Value.DistanceMeters), receiverSensitivityDb);
    }

    private void ApplyDcsDistanceAndHorizonLoss(AudioParams audioParams,
        FrequencyTransmission frequencyTransmission, (int FreqKhz, Guid SlotId) slotKey)
    {
        var resolved = TryResolveDistanceAndAltitudes(frequencyTransmission, slotKey);
        if (resolved == null)
            return;

        FreeSpacePathModel.ApplyHorizonLoss(audioParams, resolved.Value.DistanceMeters,
            resolved.Value.TxAltitudeMeters, resolved.Value.RxAltitudeMeters);
    }

    private static void ApplyAttenuation(AudioParams audioParams, double attenuationDb)
    {
        audioParams.ReceivedDb -= (float)attenuationDb;
        audioParams.ReceivedSnrDb -= (float)attenuationDb;
        UpdateFadingRates(audioParams);
    }

    private static void UpdateFadingRates(AudioParams audioParams)
    {
        var bandConfig = FastPathAudioSim.GetBandConfig(audioParams.RadioFrequencyKHz);
        audioParams.DropoutRate = FastPathAudioSim.CalculateDropoutRate(audioParams.ReceivedSnrDb, bandConfig);
        audioParams.DeepFadeRate = FastPathAudioSim.CalculateDeepFadeRate(audioParams.ReceivedSnrDb, bandConfig);
    }

    private DcsLineOfSightResult? RequestDcsLineOfSight(
        (string PeerId, int FrequencyKhz) cacheKey,
        FrequencyTransmission frequencyTransmission)
    {
        if (!DcsLineOfSightEnabled ||
            OwnPositionMode != IOpenFreqService.Mode.DCS ||
            frequencyTransmission.DcsPosition == null)
            return null;

        var position = frequencyTransmission.DcsPosition;
        return _dcsExportService.RequestLineOfSight(
            $"{cacheKey.PeerId}:{cacheKey.FrequencyKhz}",
            new DcsVector3(position.X, position.Y, position.Z));
    }

    private static AudioParams ApplyDcsLineOfSightLoss(AudioParams audioParams, DcsLineOfSightResult? result)
    {
        if (result is not { TerrainAvailable: true })
            return audioParams;

        var loss = Math.Clamp(result.Loss, 0.0d, 1.0d);
        if (loss <= 0.001d)
            return audioParams;

        var attenuationDb = loss >= 0.99d ? 180.0d : loss * 60.0d;
        ApplyAttenuation(audioParams, attenuationDb);

        audioParams.DropoutRate = Math.Max(audioParams.DropoutRate, (float)(loss * 8.0d));
        audioParams.DeepFadeRate = Math.Max(audioParams.DeepFadeRate, (float)(loss * 1.5d));
        if (loss >= 0.99d || audioParams.ReceivedSnrDb <= -18.0f)
            audioParams.SignalBlocked = true;
        return audioParams;
    }

    private static AudioParams CloneAudioParams(AudioParams source)
    {
        return new AudioParams
        {
            ReceivedDb = source.ReceivedDb,
            ReceivedSnrDb = source.ReceivedSnrDb,
            DropoutRate = source.DropoutRate,
            DeepFadeRate = source.DeepFadeRate,
            RadioFrequencyKHz = source.RadioFrequencyKHz,
            TuneOffsetPPM = source.TuneOffsetPPM,
            TerrainProfile = source.TerrainProfile,
            SignalBlocked = source.SignalBlocked
        };
    }

    // Last computed physics params for this source+freq, ignoring the cache freshness.
    // Fallback to flat default only when nothing was ever calculated.
    private AudioParams LastKnownOrDefaultAudioParams((string PeerId, int FrequencyKhz) cacheKey, int khz)
        => _audioParamsCache.TryGetValue(cacheKey, out var cached)
            ? cached.Params
            : FastPathAudioSim.GetDefaultAudioParams(khz);

    // Low-rate presence push so the server knows who's running DCS and roughly where, independent
    // of SATCOM state -- see DcsPresenceUpdateMessage's own doc comment. Interval matches
    // ClientSession.DcsPresenceStaleAfter's 5s staleness window with headroom for missed ticks.
    private static readonly TimeSpan DcsPresenceUpdateInterval = TimeSpan.FromSeconds(1.5);

    private async Task SendDcsPresenceUpdatesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(DcsPresenceUpdateInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_dcsExportService.State != ServiceState.Connected || _client is not { IsAuthenticated: true })
                    continue;

                await _client.SendDcsPresenceUpdateAsync(new DcsPresenceUpdateMessage
                {
                    LatitudeDeg = _dcsExportService.Latitude,
                    LongitudeDeg = _dcsExportService.Longitude,
                    AltitudeMeters = _dcsExportService.AltitudeMsl,
                    Theater = _dcsExportService.Theater
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
    }


    // Periodical Cache cleanup
    private async Task CleanupAudioParamsCacheAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(30);
                var keysToRemove = _audioParamsCache
                    .Where(kvp => kvp.Value.LastCalculated < cutoff)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _audioParamsCache.TryRemove(key, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
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

    private void OnFrequencyConnectionStatusChanged(int frequencyKhz, Channel.ChannelConnectionStatus connectionStatus,
        List<ChannelStateMessage.Peer> peers, Guid? slotId = null)
    {
        _logger.LogDebug("Frequency {FrequencyKhz}: {Status}", frequencyKhz, connectionStatus);
        FrequencyConnectionStatusChanged?.Invoke(this,
            new FrequencyConnectionStatusEventArgs(frequencyKhz, connectionStatus, peers, slotId));
    }

    private void OnFrequencyTransmissionStatusChanged(int frequencyKhz,
        Channel.ChannelTransmissionStatus transmissionStatus, bool is3d)
    {
        _logger.LogDebug("Frequency {FrequencyKhz}: {Status}", frequencyKhz, transmissionStatus);
        FrequencyTransmissionStatusChanged?.Invoke(this,
            new FrequencyTransmissionStatusEventArgs(frequencyKhz, transmissionStatus, is3d));
    }

    private void OnPeerActivity(object? sender, PeerActivityEventArgs args) =>
        PeerActivityReceived?.Invoke(this, args);

    private void OnAllPeersStatusUpdateReceived(object? sender, AllPeersStatusEventArgs args) =>
        AllPeersStatusChanged?.Invoke(this, args);

    private void OnClientServerSettingsChanged(object? sender, ServerSettingsEventArgs args)
    {
        DcsLineOfSightEnabled = args.DcsLineOfSightEnabled;
        OnStatusMessage($"DCS LOS constraint {(DcsLineOfSightEnabled ? "enabled" : "disabled")} by server");
    }

    public event EventHandler<SatcomLinkStateEventArgs>? SatcomLinkStateReceived;
    public event EventHandler<SatelliteEphemerisEventArgs>? SatelliteEphemerisReceived;

    private void OnClientSatcomLinkStateReceived(object? sender, SatcomLinkStateEventArgs args) =>
        SatcomLinkStateReceived?.Invoke(this, args);

    private void OnClientSatelliteEphemerisReceived(object? sender, SatelliteEphemerisEventArgs args) =>
        SatelliteEphemerisReceived?.Invoke(this, args);

    // Server-initiated request to referee terrain LOS between two arbitrary geodetic points via
    // this client's own live DCS instance, for a leg the server itself has no terrain data to
    // evaluate (an SRS-bridged peer) -- see DcsLosOracleRequestMessage's own doc comment. Answered
    // fully independently of DCS mode/state gating elsewhere in this file: any client with a live
    // DCS export can serve as an oracle for someone else's leg, not just its own.
    private static readonly TimeSpan DcsLosOracleTimeout = TimeSpan.FromMilliseconds(500);

    private async void OnClientDcsLosOracleRequestReceived(object? sender, DcsLosOracleRequestEventArgs args)
    {
        var request = args.Message;
        var result = await _dcsExportService.RequestRemoteLineOfSightAsync(
            request.FromLatitudeDeg, request.FromLongitudeDeg, request.FromAltitudeMeters,
            request.ToLatitudeDeg, request.ToLongitudeDeg, request.ToAltitudeMeters, DcsLosOracleTimeout);

        if (_client is not { IsAuthenticated: true }) return;

        await _client.SendDcsLosOracleResponseAsync(new DcsLosOracleResponseMessage
        {
            RequestId = request.RequestId,
            TerrainAvailable = result?.TerrainAvailable ?? false,
            Visible = result?.Visible ?? false,
            Loss = result?.Loss ?? 1.0
        });
    }

    public async Task SendSatcomGeometryUpdateAsync(SatcomGeometryUpdateMessage message)
    {
        if (_client is not { IsAuthenticated: true }) return;
        await _client.SendSatcomGeometryUpdateAsync(message);
    }

    public void Dispose()
    {
        // Stop the cache cleanup
        _cleanupCts?.Cancel();
        _cleanupCts?.Dispose();

        _dcsPresenceCts?.Cancel();
        _dcsPresenceCts?.Dispose();

        // Stop all transmissions and free the recording handle
        StopMicCapture();
        _activeTransmissionsAndMutedFrequencies.Clear();
        _toneTransmissionFrequencies.Clear();

        _playbackService?.StopAll();
        _signalCalculator?.Dispose();
        _dcsExportService.HeightmapChanged -= OnDcsHeightmapChanged;

        _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;
        _falconSharedMemoryService.StateChanged -= OnFalconStateChanged;

        // Unsubscribe from client events before disposing
        if (_client != null)
        {
            _client.ConnectionStateChanged -= OnClientConnectionStateChanged;
            _client.Authenticated -= OnClientAuthenticated;
            _client.FrequencyJoined -= OnClientFrequencyJoined;
            _client.FrequencyLeft -= OnClientFrequencyLeft;
            _client.PeerJoined -= OnClientPeerJoined;
            _client.PeerLeft -= OnClientPeerLeft;
            _client.TransmissionStateChanged -= OnClientTransmissionStatusChanged;
            _client.PeerTransmissionStateChanged -= OnClientPeerTransmissionStatusChanged;
            _client.AudioDataReceived -= OnClientAudioDataReceived;
            _client.AllPeersStatusUpdateReceived -= OnAllPeersStatusUpdateReceived;
            _client.ServerSettingsChanged -= OnClientServerSettingsChanged;
            _client.ErrorOccurred -= OnClientErrorOccurred;

            _client.Dispose();
        }
    }
}

// Event argument classes
public class FrequencyConnectionStatusEventArgs(
    int frequencyKhz,
    Channel.ChannelConnectionStatus connectionStatus,
    List<ChannelStateMessage.Peer> peers,
    Guid? slotId = null)
    : EventArgs
{
    public int FrequencyKhz { get; } = frequencyKhz;
    public Channel.ChannelConnectionStatus ConnectionStatus { get; } = connectionStatus;
    public List<ChannelStateMessage.Peer> Peers = peers;
    /// <summary>When set, only the channel with this Id should be updated; null means all channels on the frequency.</summary>
    public Guid? SlotId { get; } = slotId;
}

public class FrequencyTransmissionStatusEventArgs(
    int frequencyKhz,
    Channel.ChannelTransmissionStatus transmissionStatus,
    bool is3d) : EventArgs
{
    public int FrequencyKhz { get; } = frequencyKhz;
    public Channel.ChannelTransmissionStatus TransmissionStatus { get; } = transmissionStatus;
    public bool Is3d { get; } = is3d;
}

public class PeerActivityEventArgs(int frequencyKhz, PeerData peerData, bool is3d) : EventArgs
{
    public int FrequencyKhz { get; } = frequencyKhz;
    public PeerData PeerData { get; } = peerData;
    public bool Is3d { get; } = is3d;
}

// AudioParams Cache
internal class AudioParamsCacheEntry
{
    public required AudioParams Params { get; set; }
    public DateTime LastCalculated { get; set; }
}
