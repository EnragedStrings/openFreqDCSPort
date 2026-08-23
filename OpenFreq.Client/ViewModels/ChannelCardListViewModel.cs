using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FalconBmsDataService.Models;
using FalconBmsDataService.Services;
using FalconRadioService.Models;
using FalconRadioService.Services;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Index.Quadtree;
using OpenFreq.Client.Models.Dcs;
using OpenFreq.Client.Models;
using OpenFreq.Client.Services.Interfaces;
using OpenFreq.Client.Services.Satcom;
using OpenFreq.Common;
using OpenFreq.Common.Satcom;
using OpenFreq.Services.Acmi;
using OpenFreq.Utilities;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.Views.Util;
using SharpHook.Data;

namespace OpenFreqClient.ViewModels;

public partial class ChannelCardListViewModel : ViewModelBase, IDisposable
{
    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IAcmiClientService _acmiClientService;
    private readonly ILogger<ChannelCardListViewModel> _logger;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly IFalconSharedMemoryService _falconSharedMemoryService;
    private readonly IDcsExportService _dcsExportService;
    private readonly SettingsViewModel _settings;

    private const string BmsLocationName = "BMS Channels";

    // Friendly display names for the DCS location card, keyed by DCS internal unit name (see
    // OpenFreqDCS.lua's aircraftBuilders table). Falls back to the raw unit string for any
    // aircraft OpenFreqDCS.lua doesn't build radios for yet.
    private static readonly Dictionary<string, string> DcsAircraftDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A-10C_2"] = "A-10C II",
        ["F-16C_50"] = "F-16C Viper",
        ["C-130J-30"] = "C-130J Super Hercules",
        ["UH-60L"] = "UH-60L Black Hawk"
    };

    private static string GetDcsLocationName(string unit) =>
        "DCS " + (!string.IsNullOrWhiteSpace(unit) && DcsAircraftDisplayNames.TryGetValue(unit, out var name)
            ? name
            : "Radios");

    // Simultaneous guard monitoring (e.g. the A-10's ARC-210 TR+G, the UH-60's ARC-164/ARC-186):
    // joined/left silently in the background per radio key rather than as its own channel card.
    // See SyncGuardMonitorOnUiThread.
    private readonly Dictionary<string, Guid> _guardMonitorSlotIds = new();
    private readonly Dictionary<string, int> _guardMonitorJoinedFrequencyKhz = new();

    // SATCOM only exists on the A-10C II's ARC-210 today (see OpenFreqDCS.lua's
    // buildA10C2Radios/OpenFreqDCSConfig.a10c2.satcom) -- one shared instance is correct as long
    // as that remains true. Fed only when processing that specific radio (see
    // SyncDcsRadioOnUiThread) so other radios' syncs can't reset its acquisition progress.
    //
    // This is the ONLY SATCOM state still computed client-side: the local ARC-210 cockpit
    // login/acquisition animation (device=0 id=552/553), which is purely a radio-mode-transition
    // UI/timing concern with no network dimension. Satellite selection, link budget, DAMA network
    // access, and channel-error/frame-disposition decisions are all server-authoritative -- this
    // ViewModel just reports geometry/state to the server and displays what it pushes back (see
    // UpdateSatcomLinkQuality, OnSatcomLinkStateReceived, OnSatelliteEphemerisReceived, and
    // docs/SATCOM_SIMULATION.md).
    private readonly SatcomAcquisitionStateMachine _arc210SatcomStateMachine = new();

    /// <summary>DAMA (channels 31-40, PRST login required) SATCOM net id -- matches
    /// SatcomServerConfig.Default's "a10-arc210-satcom" entry.</summary>
    private const string DamaSatcomNetId = "a10-arc210-satcom";

    /// <summary>Half-duplex/dedicated (channels 26-30, no login, tuned directly to an assigned
    /// transponder frequency) SATCOM net id -- matches SatcomServerConfig.Default's
    /// "a10-arc210-satcom-dedicated" entry.</summary>
    private const string DedicatedSatcomNetId = "a10-arc210-satcom-dedicated";

    /// <summary>Latest satellite positions from the server's low-rate broadcast, keyed by
    /// satellite id -- used to compute az/el for the local (bounded-range) terrain-LOS ray and for
    /// debug display. Empty until the first SatelliteEphemerisUpdateMessage arrives.</summary>
    private readonly Dictionary<string, SatcomSatelliteInfoDto> _satcomSatellites = new();

    /// <summary>Which satellite id this client last heard it's assigned to, per channel -- the
    /// direction the local terrain-LOS ray is cast toward (see SyncArc210SatcomOnUiThread).</summary>
    private string? _satcomLastAssignedSatelliteId;

    /// <summary>Last-logged (available, satelliteId, failureReason, qualityState, damaState)
    /// summary per channel, so OnSatcomLinkStateReceived (which fires on the server's ~500ms tick)
    /// only logs when something actually changes, not every tick.</summary>
    private readonly Dictionary<Guid, string> _satcomLastLoggedLinkSummary = new();

    /// <summary>SyncDcsRadioOnUiThread otherwise only runs when DcsExportService.RadioChanged
    /// fires, which is gated on an actual field DIFFERING from the previous DCS export frame
    /// (frequency/volume/on-off/enc/squelch/tone/satcom flags -- see DcsExportService.
    /// HasRadioChanged). While the cockpit sits perfectly stable on Channel 31 + PRST, none of
    /// those fields necessarily change frame to frame, so the ARC-210 SATCOM acquisition timer
    /// (which needs repeated Update() calls with an advancing "now" to actually count up) could
    /// silently stall at 0.0/5s indefinitely instead of completing -- this periodic tick re-syncs
    /// the ARC-210 specifically often enough that the countdown, and the Channel 31-40 band-sustain
    /// check, both advance in real time regardless of whether anything else about the radio changed.</summary>
    private DispatcherTimer? _arc210SatcomTicker;
    public LocationViewModel? FalconLocation { get; private set; }
    public LocationViewModel? DcsLocation { get; private set; }

    private readonly Lock _channelImportLock = new();

    // Last-logged BMS volume signature — dedupes the volume diagnostic so it only
    // logs when raw/gain values actually change (no per-poll spam).
    private string? _lastVolumeDiagSignature;

    public SettingsViewModel Settings => _settings;

    [ObservableProperty]
    public partial LocationViewModel? SelectedLocation { get; set; }

    [ObservableProperty]
    public partial bool IsLocationPanelExpanded { get; set; } = true;

    [RelayCommand]
    private void ToggleLocationPanel() => IsLocationPanelExpanded = !IsLocationPanelExpanded;

    // This actually holds all of our Locations
    [ObservableProperty]
    public partial ObservableCollection<LocationViewModel> AllLocations { get; private set; } = [];

    // Single global ACMI callsign list shared across all LocationViewModels
    public ObservableCollection<LocationViewModel.TacviewAircraftItem> GlobalTacviewCallsigns { get; } = [];
    private CancellationTokenSource? _callsignUpdateCts;

    // Collection used to display filtered locations (BMS or GCI mode)
    public IEnumerable<LocationViewModel> Locations =>
        _settings.ConnectionMode == IOpenFreqService.Mode.BMS
            ? AllLocations.Where(g => g.RadioStationData.Type == RadioStationData.RadioStationType.BMS)
            : _settings.ConnectionMode == IOpenFreqService.Mode.DCS
                ? AllLocations.Where(g => g.RadioStationData.Type == RadioStationData.RadioStationType.DCS)
                : AllLocations.Where(g => g.RadioStationData.Type is not RadioStationData.RadioStationType.BMS
                    and not RadioStationData.RadioStationType.DCS);


    public ChannelCardListViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService,
        IAcmiClientService acmiClientService, ILogger<ChannelCardListViewModel> logger,
        IFalconRadioSharedMemoryService falconRadioSharedMemoryService,
        IFalconSharedMemoryService falconSharedMemoryService, IDcsExportService dcsExportService,
        SettingsViewModel settingsViewModel)
    {
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        _acmiClientService = acmiClientService;
        _logger = logger;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _falconSharedMemoryService = falconSharedMemoryService;
        _dcsExportService = dcsExportService;
        _settings = settingsViewModel;
        _settings.PropertyChanged += OnSettingsChanged;
        SyncGlobalPttHotkeyRegistration();

        // Subscribe to BMS Frequency update messages
        _falconRadioSharedMemoryService.ConnectionParametersChanged +=
            OnConnectionParametersChanged;
        _falconRadioSharedMemoryService.FrequencyChanged += OnBmsFrequencyChanged;
        _falconRadioSharedMemoryService.PttChanged += OnBmsPttChanged;
        _falconRadioSharedMemoryService.PowerChanged += OnRadioPowerChanged;
        _falconRadioSharedMemoryService.VolumeChanged += OnRadioVolumeChanged;
        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;
        _falconSharedMemoryService.StateChanged += OnFalconSharedMemoryStateChanged;
        _dcsExportService.RadioChanged += OnDcsRadioChanged;
        _dcsExportService.ToneChanged += OnDcsToneChanged;
        _dcsExportService.GameModeChanged += OnDcsGameModeChanged;
        _dcsExportService.StateChanged += OnDcsStateChanged;
        _dcsExportService.AircraftChanged += OnDcsAircraftChanged;

        _openFreqService.ConnectionStateChanged += OnOpenFreqConnectionStateChanged;
        _openFreqService.SatcomLinkStateReceived += OnSatcomLinkStateReceived;
        _openFreqService.SatelliteEphemerisReceived += OnSatelliteEphemerisReceived;
        _acmiClientService.ConnectionStatusChanged += OnAcmiConnectionStatusChangedForCallsigns;

        // Sync initial ACMI state in case already connected before this VM was created
        if (_acmiClientService.Status == AcmiConnectionStatus.Connected)
            StartCallsignPolling();

        AllLocations.CollectionChanged += OnAllLocationsChanged;

        // Subscribe to transmission messages
        WeakReferenceMessenger.Default.Register<StartTransmissionMessage>(this,
            async (r, m) => await HandleStartTransmissionAsync(m));
        WeakReferenceMessenger.Default.Register<StopTransmissionMessage>(this,
            async (r, m) => await HandleStopTransmissionAsync(m));
        WeakReferenceMessenger.Default.Register<LocationViewModel.LocationDeleteRequestedMessage>(this,
            async (r, m) => await DeleteLocation(m.LocationId));
        WeakReferenceMessenger.Default.Register<ChannelPanUpdateMessage>(this,
            (r, m) =>
            {
                _openFreqService.SetPan(m.FrequencyKhz, m.ChannelId, m.Pan);

                var dcsChannel = DcsLocation?.Channels.FirstOrDefault(c => c.Id == m.ChannelId);
                if (!string.IsNullOrWhiteSpace(dcsChannel?.DcsRadioId))
                    _settings.SetDcsRadioPan(dcsChannel.DcsRadioId, m.Pan);
            });
        WeakReferenceMessenger.Default.Register<TransmitBlockedMessage>(this,
            (r, m) => _logger.LogWarning("PTT blocked on \"{ChannelName}\": {Reason}", m.ChannelName, m.Reason));
        WeakReferenceMessenger.Default.Register<ChannelPttHotkeyUpdateMessage>(this,
            (r, m) =>
            {
                var dcsChannel = DcsLocation?.Channels.FirstOrDefault(c => c.Id == m.ChannelId);
                if (!string.IsNullOrWhiteSpace(dcsChannel?.DcsRadioId))
                    _settings.SetDcsPttHotkey(dcsChannel.DcsRadioId, m.Hotkey);
            });
        WeakReferenceMessenger.Default.Register<ChannelEncryptionUpdateMessage>(this,
            (r, m) => _openFreqService.SetEncryption(m.FrequencyKhz, m.ChannelId, m.Enc, m.EncKey, m.HqOn,
                m.CryptoCapable));
        WeakReferenceMessenger.Default.Register<ChannelVolumeUpdateMessage>(this,
            (r, m) => _openFreqService.SetVolume(m.FrequencyKhz, m.ChannelId, (float)m.Volume));
        WeakReferenceMessenger.Default.Register<LocationViewModel.LocationSelectionRequestedMessage>(this,
            (r, m) => SelectedLocation = Locations.FirstOrDefault(g => g.Id == m.LocationId));

        // See _arc210SatcomTicker's own doc comment: RadioChanged alone isn't a reliable enough
        // trigger to advance the SATCOM acquisition timer/band-sustain check in real time.
        _arc210SatcomTicker = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            OnArc210SatcomTick);
        _arc210SatcomTicker.Start();
    }

    private void OnArc210SatcomTick(object? sender, EventArgs e)
    {
        if (DcsLocation == null || !_settings.ModeIsDcs) return;

        var arc210 = _dcsExportService.GetRadios().FirstOrDefault(r => r.Name == "ARC-210");
        if (arc210 != null)
            SyncDcsRadioOnUiThread(arc210);
    }

    private async void OnFalconSharedMemoryStateChanged(object? sender, ServiceStateChangedEventArgs e)
    {
        // Clean up in case the SHMEM has disconnected (BMS likely crashed)
        if (_settings.ConnectionMode != IOpenFreqService.Mode.BMS || e.NewState == ServiceState.Connected ||
            FalconLocation == null) return;
        await DeleteLocation(FalconLocation);
        FalconLocation = null;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ConnectionMode))
        {
            OnPropertyChanged(nameof(Locations));
            if (SelectedLocation == null || !Locations.Contains(SelectedLocation))
                SelectedLocation = Locations.FirstOrDefault();
        }
        else if (e.PropertyName == nameof(SettingsViewModel.BmsRadio1Pan) && FalconLocation != null)
        {
            foreach (var ch in FalconLocation.Channels
                         .Where(c => c.BmsRadioType is RadioType.Radio1 or RadioType.Guard))
                ch.Pan = _settings.BmsRadio1Pan;
        }
        else if (e.PropertyName == nameof(SettingsViewModel.BmsRadio2Pan) && FalconLocation != null)
        {
            foreach (var ch in FalconLocation.Channels
                         .Where(c => c.BmsRadioType == RadioType.Radio2))
                ch.Pan = _settings.BmsRadio2Pan;
        }
        else if (e.PropertyName is nameof(SettingsViewModel.DcsRadio1PttHotkey)
                 or nameof(SettingsViewModel.DcsRadio2PttHotkey)
                 or nameof(SettingsViewModel.DcsRadio3PttHotkey)
                 or nameof(SettingsViewModel.DcsRadio4PttHotkey)
                 or nameof(SettingsViewModel.DcsRadio5PttHotkey)
                 or nameof(SettingsViewModel.DcsRadio6PttHotkey)
                 or nameof(SettingsViewModel.DcsRadio7PttHotkey))
        {
            ApplyDcsPttHotkeysOnUiThread();
        }
        else if (e.PropertyName == nameof(SettingsViewModel.GlobalPttHotkey))
        {
            SyncGlobalPttHotkeyRegistration();
        }
    }

    private HotkeyBinding? _registeredGlobalPttHotkey;

    /// <summary>Keeps the global PTT keybind (see IHotkeyService.GlobalPttChannelId and
    /// LocationViewModel.OnHotkeyPressed/OnHotkeyReleased, which resolve it to whichever channel
    /// is currently selected) registered with the hotkey service in sync with Settings, since
    /// registration needs the actual old binding to unregister -- Settings only exposes the
    /// current value.</summary>
    private void SyncGlobalPttHotkeyRegistration()
    {
        if (_registeredGlobalPttHotkey != null)
            _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.Ptt, _registeredGlobalPttHotkey,
                IHotkeyService.GlobalPttChannelId);

        _registeredGlobalPttHotkey = _settings.GlobalPttHotkey;

        if (_registeredGlobalPttHotkey != null)
            _hotkeyService.RegisterHotkey(IHotkeyService.HotkeyType.Ptt, _registeredGlobalPttHotkey,
                IHotkeyService.GlobalPttChannelId);
    }

    /// <summary>Pushes the 7 global PTT keybind slots (SettingsViewModel.DcsRadio1PttHotkey etc.)
    /// down to any currently-visible DCS channel cards -- needed because a channel's PttHotKey is
    /// otherwise only set once, at creation (see SyncDcsChannelOnUiThread), so a keybind edited in
    /// Settings while already sitting in a matching aircraft wouldn't otherwise take effect until
    /// the next resync.</summary>
    private void ApplyDcsPttHotkeysOnUiThread()
    {
        if (DcsLocation == null) return;

        void Apply()
        {
            foreach (var channel in DcsLocation.Channels)
            {
                if (channel.DcsRadioId == null) continue;
                channel.PttHotKey = _settings.GetDcsPttHotkey(channel.DcsRadioId);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void OnAllLocationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Locations));
        if (SelectedLocation == null)
            SelectedLocation = Locations.FirstOrDefault();
    }

    [RelayCommand]
    private void AddLocation()
    {
        var (lat, lon) = TheaterCoordinateConverter.GetCenterLatLon(_settings.SelectedTheater);
        var location = CreateLocation(
            new LocationData
            {
                Name = $"Location #{AllLocations.Count + 1}",
                Latitude = lat,
                Longitude = lon,
                AltitudeFt = 30000
            },
            editMode: true);
        SelectedLocation = location;
    }

    public void EnsureDefaultGciLocation()
    {
        if (!_settings.ModeIsGci || Locations.Any()) return;

        var (lat, lon) = TheaterCoordinateConverter.GetCenterLatLon(_settings.SelectedTheater);
        var location = CreateLocation(
            new LocationData
            {
                Name = "Default",
                Latitude = lat,
                Longitude = lon,
                AltitudeFt = 30000
            });

        var lobby1 = location.CreateChannel(1234, "BMS Lobby 1", false);
        lobby1.PttHotKey = new KeyboardBinding(KeyCode.VcF1);

        var lobby2 = location.CreateChannel(339750, "BMS Lobby 2", false);
        lobby2.PttHotKey = new KeyboardBinding(KeyCode.VcF2);

        SelectedLocation = location;
    }


    private void OnOpenFreqConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        var e = args.State;
        if (e == ConnectionState.Authenticated && _settings.ModeIsBms)
        {
            _logger.LogDebug("OpenFreq authenticated, importing and joining BMS channels");

            _ = Task.Run(async () =>
            {
                try
                {
                    await ImportBmsRadioChannels();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to import/join BMS channels");
                }
            });
        }
        else if (e == ConnectionState.Authenticated && _settings.ModeIsDcs)
        {
            _logger.LogDebug("OpenFreq authenticated, importing and joining DCS channels");

            _ = Task.Run(async () =>
            {
                try
                {
                    await ImportDcsRadioChannels();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to import/join DCS channels");
                }
            });
        }
        else if (e == ConnectionState.Authenticated && _settings.ModeIsGci)
        {
            foreach (var location in AllLocations)
            {
                if (location.RadioStationData.Type is not RadioStationData.RadioStationType.BMS
                    and not RadioStationData.RadioStationType.DCS)
                {
                    location.JoinAllChannelsAsync().Wait(100);
                }
            }
        }
    }

    private static float ComputeGainFromBmsVolume(int rawVolume)
    {
        // BMS knob: ~600 = loudest, 10000 = mute (inverted scale).
        // Floor set slightly below the loudest reading (600) so the very top of the knob reliably hits max gain
        const float dxMin = 600f;
        const float dxMax = 10000f;
        // Calibrated empirically to the BMS *AI-voice* loudness curve - the BMS volume knob acts as an overall-volume control, so we match the active-RMS of BMS recordings at matched knob position
        const float dbMin = -88.75f;
        const float dbMax = 2.0f;

        var clampedDx = Math.Clamp(rawVolume, dxMin, dxMax);
        var t = (dxMax - clampedDx) / (dxMax - dxMin);
        var db = dbMin + t * (dbMax - dbMin);
        var gain = (float)Math.Pow(10.0, db / 20.0);
        return gain < 0.00001f ? 0f : gain;
    }

    private void OnRadioVolumeChanged(object? sender, RadioVolumeChangedEventArgs e)
    {
        if (!_settings.ModeIsBms || FalconLocation == null) return;

        _logger.LogDebug($"VOLUME {e.OldVolume} -> {e.NewVolume}");

        var gain = ComputeGainFromBmsVolume(e.NewVolume);

        // REMOVE ME WHEN UHF/VHF LOUDNESS BUG FIXED
        LogBmsVolumeDiagnostics("knob change");
        // *****************************************
        var channels = FalconLocation?.Channels.Where(c => c.BmsRadioType == e.RadioType).ToList();
        if (channels == null) return;
        foreach (var channel in channels)
        {
            _openFreqService.SetVolume(channel.FrequencyKhz, channel.Id, gain);
        }
    }

    private void OnRadioPowerChanged(object? sender, RadioPowerChangedEventArgs e)
    {
        if (!_settings.ModeIsBms || FalconLocation == null) return;

        var channels = FalconLocation.Channels.Where(c => c.BmsRadioType == e.RadioType).ToList();
        foreach (var channel in channels)
        {
            // Skip 9999 - it's BMS's parking frequency and should never be joined
            if (channel.FrequencyKhz == IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
                continue;

            // Trigger Join/Leave
            if (!e.NewPower && channel.ConnectionStatus == Channel.ChannelConnectionStatus.Connected ||
                e.NewPower && channel.ConnectionStatus != Channel.ChannelConnectionStatus.Connected)
            {
                channel.ToggleJoinLeave();
            }
        }
    }

    private async void OnConnectionParametersChanged(object? sender,
        ConnectionParametersChangedEventArgs e)
    {
        if (!_settings.ModeIsBms) return;

        // Delete the BMS location on both TerminateClient and plain MP disconnect (ReadyToTransmit → false).
        if (e.NewParameters.TerminateClient || (e.OldParameters.ReadyToTransmit && !e.NewParameters.ReadyToTransmit))
        {
            if (FalconLocation != null)
            {
                await DeleteLocation(FalconLocation);
                FalconLocation = null;
            }

            return;
        }

        // Re-sync channel power states when BMS signals it's ready (radios may have been
        // off during AttemptingToConnect and only enabled once ReadyToTransmit is set).
        if (!e.OldParameters.ReadyToTransmit && e.NewParameters.ReadyToTransmit)
        {
            SyncBmsChannelPowerStates();
        }
    }

    private void SyncBmsChannelPowerStates()
    {
        if (FalconLocation == null) return;
        foreach (var type in Enum.GetValues<RadioType>())
        {
            var radioChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
            if (radioChannel == null) continue;
            var isPowerOn = radioChannel.IsOn &&
                            radioChannel.Frequency != IFalconRadioSharedMemoryService.BmsRadioOffFrequency;
            foreach (var channel in FalconLocation.Channels.Where(c => c.BmsRadioType == type).ToList())
            {
                if (channel.FrequencyKhz == IFalconRadioSharedMemoryService.BmsRadioOffFrequency) continue;
                var isConnected = channel.ConnectionStatus == Channel.ChannelConnectionStatus.Connected;
                if (isPowerOn != isConnected)
                    channel.ToggleJoinLeave();
            }
        }

        SyncBmsChannelVolumeStates();
    }

    private void SyncBmsChannelVolumeStates()
    {
        if (FalconLocation == null) return;
        foreach (var type in Enum.GetValues<RadioType>())
        {
            var radioChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
            if (radioChannel == null || radioChannel.RxVolume <= 0) continue;
            var gain = ComputeGainFromBmsVolume(radioChannel.RxVolume);
            foreach (var channel in FalconLocation.Channels.Where(c => c.BmsRadioType == type).ToList())
            {
                _openFreqService.SetVolume(channel.FrequencyKhz, channel.Id, gain);
            }
        }

        LogBmsVolumeDiagnostics("sync");
    }

    /// <summary>
    /// Diagnostic for the COM1/COM2 volume-mismatch issue: logs every BMS radio's raw RxVolume and the gain it maps to
    /// </summary>
    private void LogBmsVolumeDiagnostics(string trigger)
    {
        if (!_settings.ModeIsBms) return;

        var parts = new List<string>();
        foreach (var type in Enum.GetValues<RadioType>())
        {
            var radioChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
            if (radioChannel == null) continue;
            var gain = ComputeGainFromBmsVolume(radioChannel.RxVolume);
            parts.Add($"{type} raw={radioChannel.RxVolume} gain={gain:F3}");
        }

        var signature = string.Join(" | ", parts);
        if (signature == _lastVolumeDiagSignature) return;
        _lastVolumeDiagSignature = signature;

        _logger.LogInformation("BMS volume map ({Trigger}): {Volumes}", trigger, signature);
    }

    private void OnFlyingStateChanged(object? sender, FlyingStateChangedEventArgs e)
    {
        if (!_settings.ModeIsBms) return;

        _logger.LogDebug($"FalconSharedMemoryServiceOnFlyingStateChanged: {e.OldFlyingState} -> {e.NewFlyingState}");
        if (!e.OldFlyingState && e.NewFlyingState)
        {
            _hotkeyService.PausePttKeys();
        }
        else if (e.OldFlyingState && !e.NewFlyingState)
        {
            _hotkeyService.ResumePttKeys();
        }
    }

    private async Task ImportBmsRadioChannels()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            lock (_channelImportLock)
            {
                if (FalconLocation == null)
                {
                    FalconLocation = CreateLocation(BmsLocationName, RadioStationPresets.FighterF16,
                        RadioStationData.RadioStationType.BMS);
                }
                else
                {
                    FalconLocation.LeaveAllChannelsAsync().Wait(300);
                    FalconLocation.Channels.Clear();
                }

                // Create the Channels
                foreach (var type in Enum.GetValues<RadioType>())
                {
                    var falconChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
                    if (falconChannel != null &&
                        FalconLocation.Channels.All(c => c.FrequencyKhz != falconChannel.Frequency))
                    {
                        var channelName = type switch
                        {
                            RadioType.Radio1 => "Radio 1",
                            RadioType.Radio2 => "Radio 2",
                            RadioType.Guard => "Guard",
                            _ => type.ToString()
                        };
                        var channel = FalconLocation.CreateChannel(falconChannel.Frequency,
                            channelName,
                            false, type);
                        channel.IsEditable = false;

                        var channelIsPowerOn = _falconRadioSharedMemoryService.GetRadioChannel(type)?.IsOn ?? false;
                        _logger.LogDebug($"CHANNEL {channel.FrequencyKhz}: {channelIsPowerOn}");
                        if (channelIsPowerOn)
                        {
                            channel.Join();
                        }
                        else
                        {
                            channel.Leave();
                        }

                        // set hotkeys and Pan from Settings
                        switch (type)
                        {
                            case RadioType.Radio1:
                                channel.PttHotKey = new KeyboardBinding(KeyCode.VcF1);
                                channel.SquelchHotKey = _settings.BmsUhfSquelchHotkey;
                                channel.Pan = _settings.BmsRadio1Pan;
                                break;
                            case RadioType.Radio2:
                                channel.PttHotKey = new KeyboardBinding(KeyCode.VcF2);
                                channel.SquelchHotKey = _settings.BmsVhfSquelchHotkey;
                                channel.Pan = _settings.BmsRadio2Pan;
                                break;
                            case RadioType.Guard:
                                channel.PttHotKey = new KeyboardBinding(KeyCode.VcF3);
                                channel.SquelchHotKey = _settings.BmsUhfSquelchHotkey;
                                channel.Pan = _settings.BmsRadio1Pan;
                                break;
                            default:
                                _logger.LogWarning("Unknown radio type: " + type);
                                break;
                        }
                    }
                }
            }

            // Apply current BMS volume levels — SyncBmsChannelPowerStates (which calls
            // SyncBmsChannelVolumeStates) only fires on ReadyToTransmit false→true transition.
            // When ReadyToTransmit stays true across a reconnect that transition never fires,
            // so we sync volumes explicitly here after the channel list is built.
            SyncBmsChannelVolumeStates();
        });
    }

    private async Task ImportDcsRadioChannels()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            lock (_channelImportLock)
            {
                EnsureDcsLocation();
                foreach (var radio in _dcsExportService.GetRadios())
                    SyncDcsRadioOnUiThread(radio);
            }
        });
    }

    private void EnsureDcsLocation()
    {
        if (DcsLocation == null)
        {
            DcsLocation = CreateLocation(GetDcsLocationName(_dcsExportService.Unit), RadioStationPresets.FighterGeneric,
                RadioStationData.RadioStationType.DCS);
            DcsLocation.EditMode = false;
            SelectedLocation = DcsLocation;
            return;
        }

        if (!AllLocations.Contains(DcsLocation))
            AllLocations.Add(DcsLocation);
    }

    private void OnDcsAircraftChanged(object? sender, DcsAircraftChangedEventArgs e)
    {
        if (!_settings.ModeIsDcs || DcsLocation == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            DcsLocation.Name = GetDcsLocationName(e.NewUnit);
            RemoveDcsChannelsNotInCurrentAircraft();
        });
    }

    /// <summary>Channel cards are identified by slot number alone (see GetDcsRadioKey) so they're
    /// reused across aircraft switches -- but that means a slot the new aircraft doesn't have at
    /// all (e.g. slot 3 when switching from the A-10's 3 radios to the F-16's 2) would otherwise
    /// never get told to go away; DcsExportService only fires a per-radio "off" update for slots
    /// that still exist, and this one no longer does. Runs after AircraftChanged, by which point
    /// DcsExportService.ApplyPacket has already applied the new aircraft's radio set (see
    /// DcsExportService.cs's ApplyPacket -- stale-slot removal happens before AircraftChanged
    /// fires), so _dcsExportService.GetRadios() here already reflects it.</summary>
    private void RemoveDcsChannelsNotInCurrentAircraft()
    {
        if (DcsLocation == null) return;

        var liveSlots = _dcsExportService.GetRadios().Select(r => r.Slot.ToString()).ToHashSet();
        foreach (var channel in DcsLocation.Channels.ToList())
        {
            if (channel.DcsRadioId != null && !liveSlots.Contains(channel.DcsRadioId))
                WeakReferenceMessenger.Default.Send(new ChannelDeleteRequestedMessage(channel.Id, channel.FrequencyKhz));
        }
    }

    private void OnDcsRadioChanged(object? sender, DcsRadioChangedEventArgs e)
    {
        if (!_settings.ModeIsDcs) return;

        Dispatcher.UIThread.Post(() =>
        {
            lock (_channelImportLock)
            {
                EnsureDcsLocation();
                SyncDcsRadioOnUiThread(e.NewRadio);
            }
        });
    }

    /// <summary>ARC-186 TONE: key/unkey a transmission on that radio's channel the same way real
    /// PTT would, except OpenFreqService substitutes a synthesized tone for the mic buffer while
    /// it's the only active transmission (see OpenFreqService.RecordProcedure).</summary>
    private void OnDcsToneChanged(object? sender, DcsToneChangedEventArgs e)
    {
        if (!_settings.ModeIsDcs) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (DcsLocation == null) return;

            var channel = DcsLocation.Channels.FirstOrDefault(c => c.DcsRadioId == GetDcsRadioKey(e.Radio));
            if (channel == null || channel.ConnectionStatus == Channel.ChannelConnectionStatus.Disconnected) return;

            if (e.NewToneOn)
            {
                var mutedFrequencies = new List<int> { channel.FrequencyKhz };
                _openFreqService.StartToneTransmissionAsync(channel.FrequencyKhz, channel.Id, mutedFrequencies)
                    .Wait(TimeSpan.FromMilliseconds(500));
            }
            else
            {
                _openFreqService.StopToneTransmissionAsync(channel.FrequencyKhz)
                    .Wait(TimeSpan.FromMilliseconds(500));
            }
        });
    }

    private async void OnDcsStateChanged(object? sender, ServiceStateChangedEventArgs e)
    {
        if (!_settings.ModeIsDcs) return;

        if (e.NewState == ServiceState.Connected && _openFreqService.IsAuthenticated)
        {
            await ImportDcsRadioChannels();
            return;
        }

        await Task.CompletedTask;
    }

    private void OnDcsGameModeChanged(object? sender, DcsGameModeChangedEventArgs e)
    {
        if (!_settings.ModeIsDcs) return;

        Dispatcher.UIThread.Post(() => _settings.Is3dMode = e.NewIsInGame);
    }

    /// <summary>Synthetic network-channel identity DAMA (channels 31-40, PRST login) SATCOM
    /// traffic joins while active, distinct from whatever real dial frequency DCS happens to be
    /// reporting for the ARC-210 (DCS keeps reporting its last-tuned LOS dial frequency, typically
    /// ~133.000 MHz, even in SATCOM mode -- see FrequencyDisplayText). Deliberately far outside any
    /// frequency a DCS radio could actually be dialed to in this sim, so DAMA traffic can never
    /// collide with a real LOS channel. Half-duplex/dedicated SATCOM (channels 26-30) does NOT use
    /// this -- it stays on its own real tuned frequency, since that IS the assigned transponder
    /// frequency the operator dialed in (see SyncDcsRadioOnUiThread).</summary>
    public const int DamaSatcomVirtualFrequencyKhz = 999_000;

    /// <summary>Last logged half-duplex/dedicated (26-30) active state, so entering/leaving it gets
    /// its own log line the same way DAMA acquisition transitions do.</summary>
    private bool _lastDedicatedSatcomActive;

    private void SyncDcsRadioOnUiThread(DcsRadioState radio)
    {
        if (DcsLocation == null) return;

        var key = GetDcsRadioKey(radio);

        // SATCOM only exists on the A-10C II's ARC-210 today (see OpenFreqDCS.lua's
        // buildA10C2Radios). Gate on the radio name, not slot number -- slot 1 is a different
        // radio on other aircraft, and feeding their (always-false) SatcomSelected into this
        // shared state machine would reset the ARC-210's acquisition progress mid-sync. Computed
        // BEFORE the channel sync below so the channel joins the right network frequency (real
        // dial vs. synthetic DAMA) from the very same sync pass, not one frame behind.
        //
        // Two independent SATCOM bands: DAMA (31-40, PRST login via the state machine below, joins
        // the synthetic DamaSatcomVirtualFrequencyKhz) and dedicated/half-duplex (26-30, no login,
        // stays on the real tuned frequency -- see DcsRadioState.SatcomDedicatedActive). A channel
        // is in at most one at a time; the knob can't be in both bands simultaneously.
        SatcomState? satcomState = null;
        var effectiveFrequencyKhz = radio.FrequencyKhz;
        string? satcomNetId = null;
        double? satcomTunedFrequencyHz = null;
        var satcomLoginReady = false;

        if (radio.Name == "ARC-210")
        {
            // Power is folded in here (not in the Lua export) so both signals fail safe the
            // instant the radio loses power, regardless of whatever the channel/selector
            // arguments happen to read at that moment.
            var loginTrigger = radio.IsOn && radio.SatcomSelected;
            var bandActive = radio.IsOn && radio.SatcomBandActive;
            var previousSatcomState = _arc210SatcomStateMachine.State;
            satcomState = _arc210SatcomStateMachine.Update(loginTrigger, bandActive, Environment.TickCount64);
            if (satcomState != previousSatcomState)
            {
                _logger.LogInformation(
                    "SATCOM DAMA acquisition {Old} -> {New} (loginTrigger={LoginTrigger} bandActive={BandActive} radioOn={RadioOn})",
                    previousSatcomState, satcomState, loginTrigger, bandActive, radio.IsOn);
            }

            var dedicatedActive = radio.IsOn && radio.SatcomDedicatedActive;
            if (dedicatedActive != _lastDedicatedSatcomActive)
            {
                _logger.LogInformation("SATCOM dedicated (26-30) {State}", dedicatedActive ? "ENTERED" : "LEFT");
                _lastDedicatedSatcomActive = dedicatedActive;
            }

            if (satcomState != SatcomState.Normal)
            {
                effectiveFrequencyKhz = DamaSatcomVirtualFrequencyKhz;
                satcomNetId = DamaSatcomNetId;
                satcomLoginReady = satcomState == SatcomState.Ready;
            }
            else if (dedicatedActive)
            {
                // Real dialed frequency IS the SATCOM carrier here -- no substitution, and no
                // login delay: active as soon as it's tuned and the radio is powered.
                satcomNetId = DedicatedSatcomNetId;
                satcomTunedFrequencyHz = radio.FrequencyHz;
                satcomLoginReady = true;
            }
        }

        var channel = SyncDcsChannelOnUiThread(
            key: key,
            name: radio.Name,
            frequencyKhz: effectiveFrequencyKhz,
            shouldBeJoined: IsUsableDcsRadio(radio),
            volume: radio.Volume,
            allowTransmit: true,
            enc: radio.Enc,
            encKey: radio.EncKey,
            hqOn: radio.HqOn,
            squelchOn: radio.SquelchOn);

        if (channel != null && satcomState != null)
        {
            channel.SatcomAcquisitionState = satcomState.Value;
            channel.SatcomAcquisitionElapsedSeconds = _arc210SatcomStateMachine.AcquisitionElapsedSeconds;
        }

        if (channel != null && satcomNetId != null)
        {
            UpdateSatcomLinkQuality(channel, satcomNetId, satcomLoginReady, radio.IsOn, satcomTunedFrequencyHz);
        }

        SyncGuardMonitorOnUiThread(radio, key);
    }

    /// <summary>Reports this aircraft's SATCOM geometry/state to the server every DCS export
    /// frame -- the server is authoritative for satellite assignment, link budget, and DAMA (see
    /// docs/SATCOM_SIMULATION.md). This ViewModel no longer computes any of that itself; it only
    /// sends what only the client can know (own position/attitude, cockpit login-ready state, PTT,
    /// a local DCS terrain-LOS check toward the last-known assigned satellite, and -- for the
    /// dedicated/half-duplex net -- its own tuned carrier frequency) and displays whatever the
    /// server pushes back via OnSatcomLinkStateReceived.
    ///
    /// KNOWN LIMITATION: switching a channel between the DAMA and dedicated nets (or vice versa)
    /// leaves the previous net's server-side session keyed under the same channel id but no longer
    /// updated with fresh geometry; it isn't explicitly torn down (only RemoveClient on disconnect
    /// does that today), so its evaluation results could theoretically still arrive interleaved
    /// with the new net's for a channel that just switched. Not expected to matter in practice
    /// (switching nets mid-flight is rare and self-corrects once fresh updates for the new net
    /// dominate), but worth fixing with explicit per-net teardown in a follow-up pass.</summary>
    private void UpdateSatcomLinkQuality(ChannelCardViewModel channel, string netId, bool loginReady,
        bool radioPowered, double? tunedFrequencyHz)
    {
        var pttPressed = channel.TransmissionStatus == Channel.ChannelTransmissionStatus.Transmitting;

        var terrainLosClear = ComputeSatcomTerrainLosClear(channel.Id);

        var message = new SatcomGeometryUpdateMessage
        {
            ChannelKey = channel.Id.ToString(),
            NetId = netId,
            LatitudeDeg = _dcsExportService.Latitude,
            LongitudeDeg = _dcsExportService.Longitude,
            AltitudeMeters = _dcsExportService.AltitudeMsl,
            HeadingRad = _dcsExportService.HeadingRadians,
            PitchRad = _dcsExportService.PitchRadians,
            BankRad = _dcsExportService.BankRadians,
            RadioPowered = radioPowered,
            LoginReady = loginReady,
            PttPressed = pttPressed,
            TerrainLosClear = terrainLosClear,
            DebugRequested = _settings.DebugMode,
            TunedFrequencyHz = tunedFrequencyHz,
            Priority = 0
        };

        _logger.LogDebug(
            "SATCOM geometry update \"{ChannelName}\" net={NetId}: lat={Lat:F4} lon={Lon:F4} alt={Alt:F0} " +
            "powered={Powered} loginReady={LoginReady} ptt={Ptt} terrainLos={TerrainLos} tunedHz={TunedHz}",
            channel.Name, netId, message.LatitudeDeg, message.LongitudeDeg, message.AltitudeMeters,
            radioPowered, loginReady, pttPressed, terrainLosClear, tunedFrequencyHz);

        _ = _openFreqService.SendSatcomGeometryUpdateAsync(message);
    }

    /// <summary>Local DCS terrain-LOS check toward the last-known assigned satellite's direction,
    /// via the existing land.isVisible-backed RequestLineOfSight mechanism (same one used for
    /// terrestrial peer LOS) -- cast to a single bounded point along that az/el direction (~50 km
    /// horizontal, with a rise/floor that clears any real-world terrain) rather than the full
    /// ~35,786 km to the satellite itself, which land.isVisible was never meant to span. Defaults
    /// to "clear" (fail-open, matching this codebase's existing terrestrial-LOS convention) when no
    /// satellite is known yet or terrain data isn't available.</summary>
    private bool ComputeSatcomTerrainLosClear(Guid channelId)
    {
        const double horizontalRangeMeters = 50_000.0;
        const double minClearanceMeters = 9_000.0; // above any real-world terrain (Everest ~8,850 m)

        if (_satcomLastAssignedSatelliteId == null ||
            !_satcomSatellites.TryGetValue(_satcomLastAssignedSatelliteId, out var sat) ||
            _dcsExportService.Position == null)
            return true;

        var satEcef = SatcomGeodesy.GeodeticToEcef(sat.LatitudeDeg, sat.LongitudeDeg, sat.AltitudeMeters);
        var look = SatcomGeodesy.LookAngles(_dcsExportService.Latitude, _dcsExportService.Longitude,
            _dcsExportService.AltitudeMsl, satEcef);
        if (!look.IsAboveHorizon)
            return true; // below horizon is already handled by the server's elevation-mask gate

        var azRad = look.AzimuthDeg * Math.PI / 180.0;
        var elRad = look.ElevationDeg * Math.PI / 180.0;
        var riseMeters = Math.Max(horizontalRangeMeters * Math.Tan(elRad), minClearanceMeters);

        // DCS world axes: X = true north, Z = east, Y = up (meters).
        var own = _dcsExportService.Position;
        var target = new DcsVector3(
            own.X + horizontalRangeMeters * Math.Cos(azRad),
            own.Y + riseMeters,
            own.Z + horizontalRangeMeters * Math.Sin(azRad));

        var result = _dcsExportService.RequestLineOfSight($"satcom:{channelId}", target);
        return result is not { TerrainAvailable: true } || result.Visible;
    }

    private void OnSatcomLinkStateReceived(object? sender, SatcomLinkStateEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var channel = DcsLocation?.Channels.FirstOrDefault(c => c.Id.ToString() == e.Message.ChannelKey);
            if (channel == null) return;

            var msg = e.Message;
            _satcomLastAssignedSatelliteId = string.IsNullOrEmpty(msg.SatelliteId) ? null : msg.SatelliteId;

            var summary = $"available={msg.Available} satellite={(string.IsNullOrEmpty(msg.SatelliteId) ? "(none)" : msg.SatelliteId)} " +
                          $"failureReason={msg.FailureReason} quality={msg.QualityState} dama={msg.DamaState}";
            if (!_satcomLastLoggedLinkSummary.TryGetValue(channel.Id, out var lastSummary) || lastSummary != summary)
            {
                _logger.LogInformation("SATCOM link state for \"{ChannelName}\": {Summary}", channel.Name, summary);
                _satcomLastLoggedLinkSummary[channel.Id] = summary;
            }

            channel.SatcomSatelliteName = msg.SatelliteName;
            channel.SatcomQualityState = Enum.TryParse<SatcomLinkQualityState>(msg.QualityState, out var qs)
                ? qs : SatcomLinkQualityState.Lost;
            channel.SatcomFailureReason = Enum.TryParse<SatcomAcquisitionFailureReason>(msg.FailureReason, out var fr)
                ? fr : SatcomAcquisitionFailureReason.None;
            channel.DamaState = Enum.TryParse<DamaState>(msg.DamaState, out var ds) ? ds : DamaState.Offline;

            channel.SatcomDebugText = msg.DebugAuthorized
                ? $"C/N0 {msg.CombinedCn0DbHz:F1} dBHz | Eb/N0 {msg.EbN0Db:F1} dB | rawBER {msg.RawBer:E1} | " +
                  $"postFEC BER {msg.PostFecBer:E1} | up {msg.UplinkElevationDeg:F1}deg/{msg.UplinkRangeMeters / 1000.0:F0}km | " +
                  $"down {msg.DownlinkElevationDeg:F1}deg/{msg.DownlinkRangeMeters / 1000.0:F0}km | " +
                  $"frame {msg.DamaFrameIndex} slot {msg.DamaSlot}"
                : "";

            // Burst severity isn't sent explicitly -- derive a reasonable proxy from quality state
            // for the existing (already-correct, frame-level) vocoder channel model; the server's
            // real per-frame Clean/Corrected/Corrupted/Erased decisions (msg.FrameDispositions) are
            // authoritative for WHICH frames are affected and are available for a future pass that
            // has RadioPlayback consume that precomputed queue directly instead of re-deriving FER
            // locally into SatcomChannelErrorModel's own dice roll.
            var burstSeverity = msg.QualityState switch
            {
                nameof(SatcomLinkQualityState.Good) => 0.0,
                nameof(SatcomLinkQualityState.Marginal) => 0.2,
                nameof(SatcomLinkQualityState.Degraded) => 0.6,
                _ => 1.0
            };

            _openFreqService.SetSatcomState(channel.FrequencyKhz, channel.Id, isActive: msg.Available,
                msg.FrameErrorRate, burstSeverity, propagationLatencySeconds: msg.PropagationLatencySeconds);
        });
    }

    private void OnSatelliteEphemerisReceived(object? sender, SatelliteEphemerisEventArgs e)
    {
        foreach (var sat in e.Satellites)
            _satcomSatellites[sat.Id] = sat;
    }

    /// <summary>Some radios (the A-10's ARC-210 in TR+G, the UH-60's ARC-164/ARC-186) listen on a
    /// guard frequency simultaneously with their tuned frequency, without retuning away from it.
    /// That doesn't map to a regular tuned channel, so it's joined/left silently here instead of
    /// getting its own channel card. <see cref="DcsRadioState.SecondaryFrequencyHz"/> is 0 unless
    /// that radio is currently guarding (see each aircraft's buildXRadios() in OpenFreqDCS.lua).
    /// Tracked per radio key since one aircraft can have more than one simultaneously-guarding
    /// radio at different guard frequencies (e.g. the UH-60's UHF and VHF radios).</summary>
    private void SyncGuardMonitorOnUiThread(DcsRadioState radio, string radioKey)
    {
        if (DcsLocation == null || !_openFreqService.IsAuthenticated) return;

        var guardFrequencyKhz = radio.SecondaryFrequencyKhz;
        var shouldMonitor = IsUsableDcsRadio(radio) && guardFrequencyKhz > 0;

        if (shouldMonitor)
        {
            if (!_guardMonitorSlotIds.TryGetValue(radioKey, out var slotId))
            {
                slotId = Guid.NewGuid();
                _guardMonitorSlotIds[radioKey] = slotId;
            }

            if (!_guardMonitorJoinedFrequencyKhz.ContainsKey(radioKey))
            {
                _openFreqService.JoinFrequencyAsync(guardFrequencyKhz, slotId, DcsLocation.RadioStationData)
                    .Wait(TimeSpan.FromMilliseconds(500));
                _openFreqService.SetSquelch(guardFrequencyKhz, slotId, isSquelchClosed: true);
                _guardMonitorJoinedFrequencyKhz[radioKey] = guardFrequencyKhz;
            }
            _openFreqService.SetVolume(guardFrequencyKhz, slotId, (float)radio.Volume);
        }
        else if (_guardMonitorJoinedFrequencyKhz.TryGetValue(radioKey, out var joinedFrequencyKhz) &&
                 _guardMonitorSlotIds.TryGetValue(radioKey, out var slotId))
        {
            _openFreqService.LeaveFrequencyAsync(joinedFrequencyKhz, slotId)
                .Wait(TimeSpan.FromMilliseconds(500));
            _guardMonitorJoinedFrequencyKhz.Remove(radioKey);
        }
    }

    private ChannelCardViewModel? SyncDcsChannelOnUiThread(string key, string name, int frequencyKhz, bool shouldBeJoined,
        double volume, bool allowTransmit, bool enc, int encKey, bool hqOn, bool squelchOn)
    {
        if (DcsLocation == null) return null;

        var channel = DcsLocation.Channels.FirstOrDefault(c => c.DcsRadioId == key);
        if (channel == null)
        {
            channel = DcsLocation.CreateChannel(Math.Max(frequencyKhz, IDcsExportService.RadioOffFrequencyKhz),
                name, false);
            channel.DcsRadioId = key;
            channel.IsEditable = false;
            channel.Pan = _settings.GetDcsRadioPan(key);
            // Only set on creation, same as Pan above -- afterward it's the user's own capture
            // via the channel card's PTT button (see ChannelPttHotkeyUpdateMessage), which
            // persists back through _settings so it survives the next DCS resync.
            channel.PttHotKey = allowTransmit ? _settings.GetDcsPttHotkey(key) : null;
        }

        var oldFrequencyKhz = channel.FrequencyKhz;
        var targetFrequencyKhz = Math.Max(frequencyKhz, IDcsExportService.RadioOffFrequencyKhz);
        var frequencyChangedWhileConnected = oldFrequencyKhz != targetFrequencyKhz &&
                                             channel.ConnectionStatus == Channel.ChannelConnectionStatus.Connected;

        if (frequencyChangedWhileConnected)
        {
            _logger.LogDebug("DCS radio {RadioKey} changed frequency {OldFrequencyKhz} -> {NewFrequencyKhz}",
                key, oldFrequencyKhz, targetFrequencyKhz);
            _openFreqService.LeaveFrequencyAsync(oldFrequencyKhz, channel.Id)
                .Wait(TimeSpan.FromMilliseconds(500));
            channel.ConnectionStatus = Channel.ChannelConnectionStatus.Disconnected;
        }

        channel.Name = name;
        channel.FrequencyKhz = targetFrequencyKhz;
        channel.CryptoCapable = true;
        channel.Enc = enc;
        channel.EncKey = encKey;
        channel.HqOn = hqOn;

        if (!_openFreqService.IsAuthenticated) return channel;

        if (shouldBeJoined)
        {
            if (frequencyChangedWhileConnected ||
                channel.ConnectionStatus != Channel.ChannelConnectionStatus.Connected ||
                !_openFreqService.IsFrequencyJoined(channel.FrequencyKhz, channel.Id))
            {
                _openFreqService.JoinFrequencyAsync(channel.FrequencyKhz, channel.Id, DcsLocation.RadioStationData)
                    .Wait(TimeSpan.FromMilliseconds(500));
            }

            // Cockpit drives volume and squelch by default; the manual-override setting lets the
            // channel's own Volume/Squelch controls (see ChannelCardViewModel) take over instead.
            if (!_settings.DcsManualRadioControlOverride)
            {
                channel.Volume = volume;
                channel.IsSquelchEnabled = squelchOn;
            }
            _openFreqService.SetPan(channel.FrequencyKhz, channel.Id, channel.Pan);
        }
        else if (channel.ConnectionStatus == Channel.ChannelConnectionStatus.Connected)
        {
            _openFreqService.LeaveFrequencyAsync(channel.FrequencyKhz, channel.Id)
                .Wait(TimeSpan.FromMilliseconds(500));
        }

        return channel;
    }

    private static bool IsUsableDcsRadio(DcsRadioState radio) =>
        radio.IsOn && radio.FrequencyKhz > IDcsExportService.RadioOffFrequencyKhz;

    // Scoped by unit so aircraft with overlapping slot numbers (e.g. every aircraft's first
    // radio is slot 1) don't collide in persisted settings (pan, PTT hotkey).
    // Slot-only (not aircraft-scoped): "Radio 1" is meant to mean the same thing -- same channel
    // card, same pan, same PTT keybind -- whichever aircraft you're in, matching how a HOTAS
    // button binding works in real life. This also reuses one channel card per slot across an
    // aircraft switch instead of creating a parallel, orphaned set per aircraft.
    private static string GetDcsRadioKey(DcsRadioState radio) => radio.Slot.ToString();

    private void OnBmsPttChanged(object? sender, RadioPttChangedEventArgs e)
    {
        if (!_settings.ModeIsBms) return;

        // Do NOT capture keys twice - for non-flying, we want to use the callbacks from our HotKey service
        if (!_hotkeyService.PttKeysPaused || _falconSharedMemoryService.IsFlying == false)
            return;

        if (FalconLocation == null)
        {
            _logger.LogWarning("Ignoring PTT: no Falcon location");
            return;
        }

        var channel = FalconLocation.Channels.FirstOrDefault(c => c.BmsRadioType == e.RadioType);
        if (channel == null || channel.ConnectionStatus == Channel.ChannelConnectionStatus.Disconnected) return;
        switch (e)
        {
            // mute only the transmitting frequency
            case { OldPtt: false, NewPtt: true }:
                var mutedFrequencies = new List<int> { channel.FrequencyKhz };
                _openFreqService.StartTransmissionAsync(channel.FrequencyKhz, channel.Id, mutedFrequencies).Wait();
                break;
            case { OldPtt: true, NewPtt: false }:
                _openFreqService.StopTransmissionAsync(channel.FrequencyKhz).Wait();
                break;
        }
    }

    private void OnBmsFrequencyChanged(object? sender, RadioFrequencyChangedEventArgs e)
    {
        if (!_settings.ModeIsBms) return;
        if (FalconLocation == null)
        {
            _logger.LogWarning("Unclean state: FalconLocation is null, reimporting");
            ImportBmsRadioChannels().Wait(100);
            return;
        }

        _logger.LogDebug(
            $"OnBmsFrequencyChanged: {e.OldFrequencyKhz} -> {e.NewFrequencyKhz}");


        lock (_channelImportLock)
        {
            // make sure we set the power correctly
            var channelIsPowerOn = _falconRadioSharedMemoryService.GetRadioChannel(e.RadioType)?.IsOn ?? false;

            // Try to change an existing frequency - this should be the case in 99% of the time
            if (FalconLocation.ChangeChannelFrequency(e.OldFrequencyKhz, e.NewFrequencyKhz, channelIsPowerOn))
            {
                // Explicitly join the channel that was just updated if it's powered on
                // 9999 is BMS's "radio off" parking frequency - never join it
                var updatedChannel =
                    FalconLocation.Channels.FirstOrDefault(c => c.BmsRadioType == e.RadioType);
                if (updatedChannel != null && channelIsPowerOn &&
                    e.NewFrequencyKhz != IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
                {
                    _logger.LogDebug($"Explicitly joining updated channel: {e.NewFrequencyKhz}");
                    updatedChannel.Join();
                    _openFreqService.SetPan(e.NewFrequencyKhz, updatedChannel.Id, updatedChannel.Pan);
                }

                // Still make sure to join all channels - e.g. when switching back from guard mode
                foreach (var type in Enum.GetValues<RadioType>())
                {
                    var falconChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
                    if (falconChannel is not { IsOn: true }) continue;
                    if (falconChannel.Frequency == 9999) continue; // Skip parking frequency

                    foreach (var channel in FalconLocation.Channels)
                    {
                        if (channel.FrequencyKhz == falconChannel.Frequency &&
                            channel.FrequencyKhz != e.NewFrequencyKhz)
                        {
                            _logger.LogDebug($"Loop joining channel: {channel.FrequencyKhz}");
                            channel.Join();
                        }
                    }
                }

                return;
            }
        }

        // Fallback - for some reason there is no channel on the old frequency, create a new one
        lock (_channelImportLock)
        {
            _logger.LogWarning("OnBmsFrequencyChanged for an unknown frequency : {NewFrequencyKhz}", e.NewFrequencyKhz);
            var channelIsPowerOn = _falconRadioSharedMemoryService.GetRadioChannel(e.RadioType)?.IsOn ?? false;

            var newChannel = Dispatcher.UIThread.InvokeAsync(() =>
            {
                var channel = FalconLocation.CreateChannel(
                    e.NewFrequencyKhz,
                    BmsLocationName,
                    false);

                // Only join if the radio is powered on AND it's not the 9999 parking frequency
                if (channelIsPowerOn && e.NewFrequencyKhz != IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
                {
                    channel.Join();
                }
                else
                {
                    channel.Leave();
                }

                return channel;
            }).GetAwaiter().GetResult();

            // Only call JoinFrequencyAsync if the radio is powered on and not 9999
            if (channelIsPowerOn && e.NewFrequencyKhz != IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
            {
                JoinFrequencyAsync(newChannel.FrequencyKhz, newChannel.Id, FalconLocation.RadioStationData)
                    .Wait(TimeSpan.FromMilliseconds(500));
            }

            switch (e.RadioType)
            {
                case RadioType.Radio1:
                    newChannel.Pan = _settings.BmsRadio1Pan;
                    break;
                case RadioType.Radio2:
                    newChannel.Pan = _settings.BmsRadio2Pan;
                    break;
                case RadioType.Guard:
                    newChannel.Pan = _settings.BmsRadio1Pan;
                    break;
            }
        }
    }


    private async Task HandleStartTransmissionAsync(StartTransmissionMessage msg)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.StartTransmissionAsync(msg.FrequencyKhz, msg.ChannelId, msg.MutedRadioChannels);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start transmission: {ex.Message}");
        }
    }

    private async Task HandleStopTransmissionAsync(StopTransmissionMessage msg)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.StopTransmissionAsync(msg.FrequencyKhz);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to stop transmission: {ex.Message}");
        }
    }

    public async Task JoinFrequencyAsync(int frequencyKhz, Guid slotId, RadioStationData radioStationData)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.JoinFrequencyAsync(frequencyKhz, slotId, radioStationData);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to join frequency {frequencyKhz / 1000d:F3}: {ex.Message}");
        }
    }

    public async Task LeaveFrequencyAsync(int frequencyKhz, Guid slotId)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.LeaveFrequencyAsync(frequencyKhz, slotId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to leave frequency {frequencyKhz / 1000d:F3}: {ex.Message}");
        }
    }

    public async Task LeaveAllChannelsAsync()
    {
        foreach (var location in Locations)
        {
            await location.LeaveAllChannelsAsync();
        }
    }

    private void OnAcmiConnectionStatusChangedForCallsigns(object? sender, AcmiConnectionEventArgs e)
    {
        if (e.Status == AcmiConnectionStatus.Connected)
            StartCallsignPolling();
        else
            StopCallsignPolling();
    }

    private void StartCallsignPolling()
    {
        StopCallsignPolling();
        _callsignUpdateCts = new CancellationTokenSource();
        _ = PollCallsignsAsync(_callsignUpdateCts.Token);
    }

    private void StopCallsignPolling()
    {
        _callsignUpdateCts?.Cancel();
        _callsignUpdateCts?.Dispose();
        _callsignUpdateCts = null;
        Dispatcher.UIThread.Post(() => GlobalTacviewCallsigns.Clear());
    }

    private async Task PollCallsignsAsync(CancellationToken cancellationToken)
    {
        while (_acmiClientService.Status == AcmiConnectionStatus.Connected
               && !cancellationToken.IsCancellationRequested)
        {
            var currentAircraft = _acmiClientService.GetAllAircraft()
                .Select(ac => new LocationViewModel.TacviewAircraftItem(ac.CallSign, ac.ObjectId))
                .ToList();

            var currentIds = currentAircraft.Select(a => a.ObjectId).ToHashSet();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Remove stale
                for (int i = GlobalTacviewCallsigns.Count - 1; i >= 0; i--)
                {
                    if (!currentIds.Contains(GlobalTacviewCallsigns[i].ObjectId))
                        GlobalTacviewCallsigns.RemoveAt(i);
                }

                // Add new
                var existingIds = GlobalTacviewCallsigns.Select(a => a.ObjectId).ToHashSet();
                foreach (var aircraft in currentAircraft)
                {
                    if (aircraft.CallSign == string.Empty || existingIds.Contains(aircraft.ObjectId))
                        continue;

                    // Insert in sorted position by CallSign
                    var insertAt = 0;
                    while (insertAt < GlobalTacviewCallsigns.Count
                           && string.Compare(GlobalTacviewCallsigns[insertAt].CallSign,
                               aircraft.CallSign, StringComparison.OrdinalIgnoreCase) <= 0)
                        insertAt++;
                    GlobalTacviewCallsigns.Insert(insertAt, aircraft);
                }
            });

            try
            {
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public LocationViewModel CreateLocation(string name, RadioStationPreset preset,
        RadioStationData.RadioStationType radioStationType,
        bool editMode = false)
    {
        var location = new LocationViewModel(_openFreqService, _hotkeyService, _acmiClientService,
            _settings, name, preset, radioStationType, GlobalTacviewCallsigns, editMode: editMode);
        AllLocations.Add(location);
        return location;
    }

    public LocationViewModel CreateLocation(LocationData locationData, bool editMode = false)
    {
        var location = new LocationViewModel(_openFreqService, _hotkeyService, _acmiClientService,
            _settings, locationData.Name, locationData.RadioStationData.Preset,
            locationData.RadioStationData.Type, GlobalTacviewCallsigns,
            locationData.Latitude, locationData.Longitude, locationData.AltitudeFt, editMode);
        AllLocations.Add(location);
        return location;
    }


    public async Task DeleteLocation(LocationViewModel location)
    {
        if (SelectedLocation == location)
            SelectedLocation = null;
        await location.LeaveAllChannelsAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AllLocations.Remove(location);
            location.Dispose();
        });
    }

    public async Task DeleteLocation(Guid locationId)
    {
        if (Locations.FirstOrDefault(cg => cg.Id == locationId) is not { } location)
            return;

        if (await ConfirmationDialogService.ShowAsync(
                title: "Confirm deletion",
                message: $"Are you sure you want to delete the Location \"{location.Name}\"?",
                cancelText: "Cancel",
                confirmText: "Delete"))
        {
            await DeleteLocation(location);
        }
    }

    public void Dispose()
    {
        _arc210SatcomTicker?.Stop();
        _arc210SatcomTicker = null;
        _falconRadioSharedMemoryService.ConnectionParametersChanged -= OnConnectionParametersChanged;
        _falconRadioSharedMemoryService.FrequencyChanged -= OnBmsFrequencyChanged;
        _falconRadioSharedMemoryService.PttChanged -= OnBmsPttChanged;
        _falconRadioSharedMemoryService.PowerChanged -= OnRadioPowerChanged;
        _falconRadioSharedMemoryService.VolumeChanged -= OnRadioVolumeChanged;
        _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;
        _falconSharedMemoryService.StateChanged -= OnFalconSharedMemoryStateChanged;
        _dcsExportService.RadioChanged -= OnDcsRadioChanged;
        _dcsExportService.ToneChanged -= OnDcsToneChanged;
        _dcsExportService.GameModeChanged -= OnDcsGameModeChanged;
        _dcsExportService.StateChanged -= OnDcsStateChanged;
        _openFreqService.ConnectionStateChanged -= OnOpenFreqConnectionStateChanged;
        _openFreqService.SatcomLinkStateReceived -= OnSatcomLinkStateReceived;
        _openFreqService.SatelliteEphemerisReceived -= OnSatelliteEphemerisReceived;
        AllLocations.CollectionChanged -= OnAllLocationsChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
        _acmiClientService.ConnectionStatusChanged -= OnAcmiConnectionStatusChangedForCallsigns;
        _callsignUpdateCts?.Cancel();
        _callsignUpdateCts?.Dispose();
    }
}
