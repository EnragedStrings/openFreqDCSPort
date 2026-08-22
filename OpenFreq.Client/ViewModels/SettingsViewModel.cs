using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenFreq.Client.Services.Interfaces;
using FalconBmsDataService.Services;
using FalconRadioService.Services;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Models;
using OpenFreq.Services.Acmi;
using OpenFreq.Utilities;
using OpenFreqClient.Models;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.ViewModels;

public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private Window MainWindow => ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!)
        .MainWindow!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    public partial string OpenFreqServerAddress { get; set; } = string.Empty;
    [ObservableProperty] public partial ObservableCollection<string> OpenFreqServerAddressHistory { get; set; } = [];
    [ObservableProperty] public partial string DisplayName { get; set; } = "Joe Pilot";
    [ObservableProperty] public partial string OpenFreqPassword { get; set; } = string.Empty;
    [ObservableProperty] public partial ObservableCollection<string> PlaybackDeviceNames { get; set; } = [];

    [ObservableProperty] public partial ObservableCollection<string> RecordingDeviceNames { get; set; } = [];

    [ObservableProperty] public partial bool HasPlaybackDevices { get; set; }
    [ObservableProperty] public partial bool HasRecordingDevices { get; set; }

    [ObservableProperty] public partial int RecordingDeviceIndex { get; set; }
    [ObservableProperty] public partial int PlaybackDeviceIndex { get; set; }
    [ObservableProperty] public partial string SelectedTheater { get; set; } = "Korea KTO";
    [ObservableProperty] public partial string MapLayer { get; set; } = "Carto";

    // BMS auto-detection
    private static readonly string[] DefaultTheaterNames = ["Korea KTO", "Balkans", "Ikaros", "ITO"];

    [ObservableProperty] public partial bool BmsInstallFound { get; set; }

    [ObservableProperty] public partial ObservableCollection<TheaterDefinition> TheaterDefinitions { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    public partial TheaterDefinition? SelectedBmsTheater { get; set; }

    [ObservableProperty] public partial ObservableCollection<string> AvailableTheaterNames { get; set; } = new(DefaultTheaterNames);


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeIsBms))]
    [NotifyPropertyChangedFor(nameof(ModeIsDcs))]
    [NotifyPropertyChangedFor(nameof(ModeIsGci))]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    private IOpenFreqService.Mode _connectionMode = IOpenFreqService.Mode.BMS;

    public bool ModeIsBms
    {
        get => ConnectionMode == IOpenFreqService.Mode.BMS;
        set
        {
            if (value) ConnectionMode = IOpenFreqService.Mode.BMS;
        }
    }

    public bool ModeIsDcs
    {
        get => ConnectionMode == IOpenFreqService.Mode.DCS;
        set
        {
            if (value) ConnectionMode = IOpenFreqService.Mode.DCS;
        }
    }

    public bool ModeIsGci
    {
        get => ConnectionMode == IOpenFreqService.Mode.GCI;
        set
        {
            if (value) ConnectionMode = IOpenFreqService.Mode.GCI;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    public partial string TacviewServerAddress { get; set; } = string.Empty;
    [ObservableProperty] public partial ObservableCollection<string> TacviewServerAddressHistory { get; set; } = [];

    [ObservableProperty] public partial string TacviewServerPassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    public partial string HeightmapPath { get; set; } = string.Empty;

    [ObservableProperty] public partial string InputDeviceName { get; set; } = string.Empty;

    [ObservableProperty] public partial string OutputDeviceName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterVolumeDb))]
    public partial double MasterVolume { get; set; } = 1.0;
    [ObservableProperty] public partial bool SidetoneEnabled { get; set; } = false;
    [ObservableProperty] public partial bool MicNormalizationEnabled { get; set; } = true;

    /// <summary>When true, per-channel Volume/Squelch controls in the UI override the
    /// cockpit-driven values for DCS/BMS channels. When false (default), cockpit controls win
    /// and manual per-channel controls are disabled for those channels.</summary>
    [ObservableProperty] public partial bool DcsManualRadioControlOverride { get; set; } = false;

    /// <summary>Requests the detailed SATCOM RF/DAMA debug telemetry bundle (see
    /// ChannelCardViewModel.SatcomDebugText) from the server. The server decides whether to
    /// actually honor this per SatcomServerConfig.DebugTelemetryEnabled -- this is a request, not
    /// a client-side quality control (see docs/SATCOM_SIMULATION.md "no client quality cheating").</summary>
    [ObservableProperty] public partial bool DebugMode { get; set; } = false;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputGainText))]
    [NotifyPropertyChangedFor(nameof(InputGainDb))]
    public partial double InputGain { get; set; } = 1.0;

    /// <summary>Master output volume in dB (0 dB = unity gain). Backed by the linear
    /// <see cref="MasterVolume"/> multiplier that the audio engine actually consumes.</summary>
    public double MasterVolumeDb
    {
        get => LinearGainToDb(MasterVolume);
        set => MasterVolume = DbToLinearGain(value);
    }

    /// <summary>Mic input gain in dB (0 dB = unity gain). Backed by the linear
    /// <see cref="InputGain"/> multiplier that the audio engine actually consumes.</summary>
    public double InputGainDb
    {
        get => LinearGainToDb(InputGain);
        set => InputGain = DbToLinearGain(value);
    }

    private const double MinDisplayableDb = -80.0;
    private static double LinearGainToDb(double linear) =>
        linear <= 0.0001 ? MinDisplayableDb : 20.0 * Math.Log10(linear);
    private static double DbToLinearGain(double db) => Math.Pow(10.0, db / 20.0);
    [ObservableProperty] public partial double SidetoneVolume { get; set; } = 0.4;
    [ObservableProperty] public partial double AmbientNoiseVolume { get; set; } = 1.0;
    [ObservableProperty] public partial bool AutoRecordInGameMode { get; set; } = false;
    [ObservableProperty] public partial string RecordingPath { get; set; } = AppDataPaths.ClientRecordingDirectory;
    /// <summary>False = capture to file, True = stream the capture mix to a playback device.</summary>
    [ObservableProperty] public partial bool StreamToDevice { get; set; } = false;
    /// <summary>Inverse of <see cref="StreamToDevice"/>, for the "Record to file" radio button.</summary>
    public bool RecordToFile
    {
        get => !StreamToDevice;
        set { if (value) StreamToDevice = false; }
    }
    /// <summary>When true, own voice in the capture gets the full radio FX; when false it stays clean.</summary>
    [ObservableProperty] public partial bool ApplyOwnVoiceSfx { get; set; } = true;
    /// <summary>List index into <see cref="PlaybackDeviceNames"/> for the monitor/stream output device.</summary>
    [ObservableProperty] public partial int MonitorDeviceIndex { get; set; }
    [ObservableProperty] public partial string MonitorDeviceName { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsDarkMode { get; set; }
    [ObservableProperty] public partial bool MinimizeOnConnect { get; set; } = true;

    public bool IsWindowsPlatform { get; } = OperatingSystem.IsWindows();

    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IAudioService _audioService;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly IFalconSharedMemoryService _falconSharedMemoryService;
    private readonly IDcsExportService _dcsExportService;
    private readonly IAcmiClientService _acmiClientService;
    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;

    // Window size & position. Nullable so values never captured (e.g. app closed while
    // minimized before UpdateWindowSettings ran) stay null instead of being saved as 0.
    private int? _left, _top, _width, _height, _windowState;
    private int? _maximizedScreenX, _maximizedScreenY, _maximizedScreenWidth, _maximizedScreenHeight;

    // Heightmap is optional in GCI mode -- it only adds terrain-aware LOS/attenuation when
    // present (see MainWindowViewModel.ConnectAsync/OpenFreqService.LoadHeightmap, both of which
    // already tolerate it being unset). This used to also require HeightmapPath for GCI; that's
    // been removed per explicit request -- connecting doesn't need one.
    public bool IsReadyToConnect => OpenFreqServerAddress != string.Empty;

    [ObservableProperty]
    public partial int BmsRadio1Pan { get; set; } = 0;

    [ObservableProperty]
    public partial int BmsRadio2Pan { get; set; } = 0;

    private Dictionary<string, int> _dcsRadioPans = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, HotkeyBinding> _dcsPttHotkeys = new(StringComparer.OrdinalIgnoreCase);

    // Seven global TX PTT keybind slots, keyed by radio position (1-7, the max radio count across
    // supported aircraft -- the C-130J-30, see DCS/README.md's "Supported aircraft" table).
    // "Radio N" always means the same keybind regardless of which aircraft is active -- e.g.
    // Radio 1 is the A-10's ARC-210 AND the F-16's ARC-164 AND the C-130's UHF1, whichever you're
    // currently flying -- matching how a HOTAS button binding works in real life. Backed by the
    // same _dcsPttHotkeys dictionary the per-channel-card PTT capture button (see
    // ChannelCardListViewModel) already reads/writes, keyed by
    // ChannelCardListViewModel.GetDcsRadioKey (just the slot number as a string), so editing a
    // slot here or capturing it directly on a channel card both affect the same binding.
    public HotkeyBinding? DcsRadio1PttHotkey
    {
        get => GetDcsPttHotkey("1");
        set { SetDcsPttHotkey("1", value); OnPropertyChanged(); OnPropertyChanged(nameof(DcsRadio1PttHotkeyDisplay)); }
    }
    public string DcsRadio1PttHotkeyDisplay => DcsRadio1PttHotkey?.DisplayName ?? "None";

    public HotkeyBinding? DcsRadio2PttHotkey
    {
        get => GetDcsPttHotkey("2");
        set { SetDcsPttHotkey("2", value); OnPropertyChanged(); OnPropertyChanged(nameof(DcsRadio2PttHotkeyDisplay)); }
    }
    public string DcsRadio2PttHotkeyDisplay => DcsRadio2PttHotkey?.DisplayName ?? "None";

    public HotkeyBinding? DcsRadio3PttHotkey
    {
        get => GetDcsPttHotkey("3");
        set { SetDcsPttHotkey("3", value); OnPropertyChanged(); OnPropertyChanged(nameof(DcsRadio3PttHotkeyDisplay)); }
    }
    public string DcsRadio3PttHotkeyDisplay => DcsRadio3PttHotkey?.DisplayName ?? "None";

    public HotkeyBinding? DcsRadio4PttHotkey
    {
        get => GetDcsPttHotkey("4");
        set { SetDcsPttHotkey("4", value); OnPropertyChanged(); OnPropertyChanged(nameof(DcsRadio4PttHotkeyDisplay)); }
    }
    public string DcsRadio4PttHotkeyDisplay => DcsRadio4PttHotkey?.DisplayName ?? "None";

    public HotkeyBinding? DcsRadio5PttHotkey
    {
        get => GetDcsPttHotkey("5");
        set { SetDcsPttHotkey("5", value); OnPropertyChanged(); OnPropertyChanged(nameof(DcsRadio5PttHotkeyDisplay)); }
    }
    public string DcsRadio5PttHotkeyDisplay => DcsRadio5PttHotkey?.DisplayName ?? "None";

    public HotkeyBinding? DcsRadio6PttHotkey
    {
        get => GetDcsPttHotkey("6");
        set { SetDcsPttHotkey("6", value); OnPropertyChanged(); OnPropertyChanged(nameof(DcsRadio6PttHotkeyDisplay)); }
    }
    public string DcsRadio6PttHotkeyDisplay => DcsRadio6PttHotkey?.DisplayName ?? "None";

    public HotkeyBinding? DcsRadio7PttHotkey
    {
        get => GetDcsPttHotkey("7");
        set { SetDcsPttHotkey("7", value); OnPropertyChanged(); OnPropertyChanged(nameof(DcsRadio7PttHotkeyDisplay)); }
    }
    public string DcsRadio7PttHotkeyDisplay => DcsRadio7PttHotkey?.DisplayName ?? "None";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BmsUhfSquelchHotkeyDisplay))]
    public partial HotkeyBinding? BmsUhfSquelchHotkey { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BmsVhfSquelchHotkeyDisplay))]
    public partial HotkeyBinding? BmsVhfSquelchHotkey { get; set; }

    public string BmsUhfSquelchHotkeyDisplay =>
        BmsUhfSquelchHotkey?.DisplayName ?? "None";

    public string BmsVhfSquelchHotkeyDisplay =>
        BmsVhfSquelchHotkey?.DisplayName ?? "None";

    /// <summary>Transmits on whichever channel is currently selected, instead of one fixed
    /// radio -- see LocationViewModel.SelectedChannel and IHotkeyService.GlobalPttChannelId.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalPttHotkeyDisplay))]
    public partial HotkeyBinding? GlobalPttHotkey { get; set; }

    public string GlobalPttHotkeyDisplay => GlobalPttHotkey?.DisplayName ?? "None";

    [ObservableProperty] public partial bool InputMeterEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicInputLevelText))]
    public partial double MicInputLevel { get; set; }

    public string MicInputLevelText => $"{MicInputLevel:P0}";

    public string InputGainText => $"{InputGainDb:F1} dB";


    // This is displayed in the Top Bar but shared throughout the app
    [ObservableProperty] public partial bool Is3dMode { get; set; }

    partial void OnIs3dModeChanged(bool value)
    {
        _logger.LogInformation("Game mode changed to {Mode}", value ? "In-game" : "Lobby");
        _openFreqService.Apply3dAudioEffects = value;
        _ = _openFreqService.NotifyModeAsync(value);
    }

    partial void OnConnectionModeChanged(IOpenFreqService.Mode value)
    {
        _logger.LogInformation("Connection mode changed to {Mode}", value);
        switch (value)
        {
            case IOpenFreqService.Mode.BMS:
                _dcsExportService.Stop();
                _falconSharedMemoryService.Start();
                _falconRadioSharedMemoryService.Start();
                _acmiClientService.DisconnectAsync().Wait(50);
                _acmiClientService.Stop();
                break;
            case IOpenFreqService.Mode.GCI:
                Is3dMode = false;
                _hotkeyService.ResumePttKeys();
                _dcsExportService.Stop();
                _falconSharedMemoryService.Stop();
                _falconRadioSharedMemoryService.Stop();
                _hotkeyService.UnregisterHotkeys(IHotkeyService.HotkeyType.SquelchToggle);
                break;
            case IOpenFreqService.Mode.DCS:
                _hotkeyService.ResumePttKeys();
                _falconSharedMemoryService.Stop();
                _falconRadioSharedMemoryService.Stop();
                _acmiClientService.DisconnectAsync().Wait(50);
                _acmiClientService.Stop();
                _hotkeyService.UnregisterHotkeys(IHotkeyService.HotkeyType.SquelchToggle);
                _dcsExportService.Start();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, null);
        }
    }

    partial void OnSelectedBmsTheaterChanged(TheaterDefinition? value)
    {
        if (value == null) return;
        HeightmapPath = value.HeightmapPath ?? string.Empty;
        SelectedTheater = value.Name;
    }

    private void InitializeBmsDetection()
    {
        var bmsDir = BmsDetectionService.GetBmsDirectory();
        if (bmsDir == null) return;

        List<TheaterDefinition> theaters;
        try { theaters = BmsDetectionService.GetInstalledTheaters(bmsDir); }
        catch { return; }

        if (theaters.Count == 0) return;

        BmsInstallFound = true;
        TheaterDefinitions = new ObservableCollection<TheaterDefinition>(theaters);

        foreach (var t in theaters)
        {
            // A malformed proj4 string must not crash us - skip the offending theater and keep the rest
            try
            {
                TheaterCoordinateConverter.RegisterTheater(t);
            }
            catch
            {
                _logger.LogError("Could not register the theater {Theater}", t.Name);
                continue;
            }
            if (!AvailableTheaterNames.Contains(t.Name))
                AvailableTheaterNames.Add(t.Name);
        }

        SelectedBmsTheater ??= TheaterDefinitions[0];
    }

    public SettingsViewModel(ILogger<SettingsViewModel> logger, IAudioService audioService, IFalconRadioSharedMemoryService falconRadioSharedMemoryService,
        IFalconSharedMemoryService falconSharedMemoryService, IAcmiClientService acmiClientService,
        IOpenFreqService openFreqService, IHotkeyService hotkeyService, IDcsExportService dcsExportService)
    {
        _logger = logger;
        _audioService = audioService;
        _falconSharedMemoryService = falconSharedMemoryService;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _dcsExportService = dcsExportService;
        _acmiClientService = acmiClientService;
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        InitializeAudioDevices();
        InitializeBmsDetection();
        _openFreqService.MicLevelChanged += OnMicLevelChanged;
    }

    private void InitializeAudioDevices()
    {
        _audioService.Init();

        var playbackDevices = _audioService.GetPlaybackDevices();
        PlaybackDeviceNames = new ObservableCollection<string>(playbackDevices);
        PlaybackDeviceIndex = _audioService.DefaultPlaybackDevice;
        HasPlaybackDevices = playbackDevices.Count > 0;

        var recordingDevices = _audioService.GetRecordingDevices();
        RecordingDeviceNames = new ObservableCollection<string>(recordingDevices);
        RecordingDeviceIndex = _audioService.DefaultRecordingDevice;
        HasRecordingDevices = recordingDevices.Count > 0;

        _audioService.PlaybackDevicesChanged += OnPlaybackDevicesChanged;
        _audioService.RecordingDevicesChanged += OnRecordingDevicesChanged;
    }

    partial void OnDisplayNameChanged(string value)
    {
        if (ConnectionMode is IOpenFreqService.Mode.GCI or IOpenFreqService.Mode.DCS &&
            _openFreqService.IsAuthenticated)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _openFreqService.UpdateDisplayNameAsync(value);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update display name to {DisplayName}", value);
                }
            });
        }
    }

    private void OnRecordingDevicesChanged(object? sender, DeviceChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _logger.LogInformation(
                "Recording devices changed: Old={Old}, New={New}, DeviceWasRemoved={Removed}",
                e.OldDeviceIndex, e.NewDeviceIndex, e.DeviceWasRemoved);

            RecordingDeviceNames.Clear();
            foreach (var device in e.Devices)
                RecordingDeviceNames.Add(device);
            HasRecordingDevices = e.Devices.Count > 0;

            // Update UI selection (may be same value — observable equality guard won't re-fire)
            RecordingDeviceIndex = e.NewDeviceIndex;

            // Always propagate the resolved BASS index directly to the service, bypassing the
            // [ObservableProperty] equality check. Without this, if the physical device changed
            // but the list index stayed the same, OnRecordingDeviceIndexChanged won't fire and
            // the service keeps a stale BASS device index.
            if (e.NewDeviceIndex >= 0)
            {
                var bassIndex = _audioService.GetRecordingBassIndex(e.NewDeviceIndex);
                if (bassIndex >= 0)
                {
                    _logger.LogInformation(
                        "Forcing recording BASS device switch to index {BassIndex} (DeviceWasRemoved={Removed})",
                        bassIndex, e.DeviceWasRemoved);
                    _openFreqService.RecordingDeviceIndex = bassIndex;
                }
                else
                {
                    _logger.LogWarning("No valid BASS recording device for list index {ListIndex}", e.NewDeviceIndex);
                }
            }
            else
            {
                _logger.LogError("Recording device change yielded no valid device (NewIndex={New})", e.NewDeviceIndex);
            }
        });
    }

    private void OnPlaybackDevicesChanged(object? sender, DeviceChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _logger.LogInformation(
                "Playback devices changed: Old={Old}, New={New}, DeviceWasRemoved={Removed}",
                e.OldDeviceIndex, e.NewDeviceIndex, e.DeviceWasRemoved);

            PlaybackDeviceNames.Clear();
            foreach (var device in e.Devices)
                PlaybackDeviceNames.Add(device);
            HasPlaybackDevices = e.Devices.Count > 0;

            // Update UI selection (may be same value — observable equality guard won't re-fire)
            PlaybackDeviceIndex = e.NewDeviceIndex;

            // Always propagate the resolved BASS index directly to the service, bypassing the
            // [ObservableProperty] equality check. Critical case: selected device is removed and
            // the fallback lands at the same list index — the observable setter is a no-op,
            // ChangeOutputDevice is never called, RadioPlayback silently plays to a dead device.
            if (e.NewDeviceIndex >= 0)
            {
                var bassIndex = _audioService.GetPlaybackBassIndex(e.NewDeviceIndex);
                if (bassIndex >= 0)
                {
                    _logger.LogInformation(
                        "Forcing playback BASS device switch to index {BassIndex} (DeviceWasRemoved={Removed})",
                        bassIndex, e.DeviceWasRemoved);
                    _openFreqService.PlaybackDeviceIndex = bassIndex;
                }
                else
                {
                    _logger.LogWarning("No valid BASS playback device for list index {ListIndex}", e.NewDeviceIndex);
                }
            }
            else
            {
                _logger.LogError("Playback device change yielded no valid device (NewIndex={New})", e.NewDeviceIndex);
            }
        });
    }

    partial void OnMasterVolumeChanged(double value) => _openFreqService.MasterVolume = value;

    partial void OnSidetoneEnabledChanged(bool value) => _openFreqService.SidetoneEnabled = value;

    partial void OnMicNormalizationEnabledChanged(bool value) => _openFreqService.MicNormalizationEnabled = value;

    partial void OnInputMeterEnabledChanged(bool value) => _openFreqService.InputMeterEnabled = value;

    partial void OnInputGainChanged(double value) => _openFreqService.InputGain = value;

    partial void OnSidetoneVolumeChanged(double value) => _openFreqService.SidetoneVolume = value;

    partial void OnAmbientNoiseVolumeChanged(double value) => _openFreqService.AmbientNoiseVolume = value;

    partial void OnAutoRecordInGameModeChanged(bool value) => _openFreqService.AutoRecordInGameMode = value;

    partial void OnRecordingPathChanged(string value) => _openFreqService.RecordingPath = value;

    partial void OnStreamToDeviceChanged(bool value)
    {
        _openFreqService.Sink = value
            ? IOpenFreqService.CaptureSink.Device
            : IOpenFreqService.CaptureSink.File;
        OnPropertyChanged(nameof(RecordToFile));
    }

    partial void OnApplyOwnVoiceSfxChanged(bool value) => _openFreqService.ApplyOwnVoiceSfx = value;

    partial void OnMonitorDeviceIndexChanged(int value)
    {
        if (value < 0 || value >= PlaybackDeviceNames.Count) return;
        MonitorDeviceName = PlaybackDeviceNames[value];
        _openFreqService.MonitorDeviceIndex = _audioService.GetPlaybackBassIndex(value);
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        Application.Current!.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    partial void OnRecordingDeviceIndexChanged(int value)
    {
        if (value >= 0 && value < RecordingDeviceNames.Count)
        {
            InputDeviceName = RecordingDeviceNames[value];
            _openFreqService.RecordingDeviceIndex = _audioService.GetRecordingBassIndex(value);
        }
    }

    partial void OnPlaybackDeviceIndexChanged(int value)
    {
        if (value >= 0 && value < PlaybackDeviceNames.Count)
        {
            OutputDeviceName = PlaybackDeviceNames[value];
            _openFreqService.PlaybackDeviceIndex = _audioService.GetPlaybackBassIndex(value);
        }
    }

    partial void OnBmsRadio1PanChanged(int value) { }

    partial void OnBmsRadio2PanChanged(int value) { }

    private void OnMicLevelChanged(object? sender, MicLevelChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => MicInputLevel = e.Peak);
    }

    public void LoadFromSettings(OpenFreqSettings settings)
    {
        OpenFreqServerAddress = settings.OpenFreqServerAddress;
        OpenFreqPassword = settings.OpenFreqPassword;
        OpenFreqServerAddressHistory = new ObservableCollection<string>(settings.OpenFreqServerAddressHistory);
        ConnectionMode = IsWindowsPlatform ? settings.OwnPositionMode : IOpenFreqService.Mode.GCI;
        TacviewServerAddress = settings.TacviewServerAddress;
        TacviewServerPassword = settings.TacviewServerPassword;
        TacviewServerAddressHistory = new ObservableCollection<string>(settings.TacviewServerAddressHistory);
        SelectedTheater = settings.SelectedTheater;
        MapLayer = settings.MapLayer;
        BmsRadio1Pan = settings.BmsRadio1Pan;
        BmsRadio2Pan = settings.BmsRadio2Pan;
        _dcsRadioPans = new Dictionary<string, int>(
            settings.DcsRadioPans ?? new Dictionary<string, int>(),
            StringComparer.OrdinalIgnoreCase);
        _dcsPttHotkeys = new Dictionary<string, HotkeyBinding>(
            settings.DcsPttHotkeys ?? new Dictionary<string, HotkeyBinding>(),
            StringComparer.OrdinalIgnoreCase);
        // Direct field assignment above doesn't raise property-changed notifications on its own,
        // so ChannelCardListViewModel.OnSettingsChanged (which re-pushes restored bindings onto
        // already-created DCS channel cards via ApplyDcsPttHotkeysOnUiThread) never fires unless
        // we fire these explicitly -- without this, a PTT hotkey saved to disk loads correctly
        // into this dictionary but never reappears on the channel card after a restart, since a
        // channel's PttHotKey is otherwise only set once, at creation.
        OnPropertyChanged(nameof(DcsRadio1PttHotkey));
        OnPropertyChanged(nameof(DcsRadio1PttHotkeyDisplay));
        OnPropertyChanged(nameof(DcsRadio2PttHotkey));
        OnPropertyChanged(nameof(DcsRadio2PttHotkeyDisplay));
        OnPropertyChanged(nameof(DcsRadio3PttHotkey));
        OnPropertyChanged(nameof(DcsRadio3PttHotkeyDisplay));
        OnPropertyChanged(nameof(DcsRadio4PttHotkey));
        OnPropertyChanged(nameof(DcsRadio4PttHotkeyDisplay));
        OnPropertyChanged(nameof(DcsRadio5PttHotkey));
        OnPropertyChanged(nameof(DcsRadio5PttHotkeyDisplay));
        OnPropertyChanged(nameof(DcsRadio6PttHotkey));
        OnPropertyChanged(nameof(DcsRadio6PttHotkeyDisplay));
        OnPropertyChanged(nameof(DcsRadio7PttHotkey));
        OnPropertyChanged(nameof(DcsRadio7PttHotkeyDisplay));
        GlobalPttHotkey = settings.GlobalPttHotkey;
        MasterVolume = settings.MasterVolume;
        SidetoneEnabled = settings.SidetoneEnabled;
        MicNormalizationEnabled = settings.MicNormalizationEnabled;
        InputGain = settings.InputGain <= 0 ? 1.0 : settings.InputGain;
        SidetoneVolume = settings.SidetoneVolume;
        MinimizeOnConnect = settings.MinimizeOnConnect;
        AmbientNoiseVolume = settings.AmbientNoiseVolume;
        AutoRecordInGameMode = settings.AutoRecordInGameMode;
        // Manual volume/squelch override always starts off, regardless of the saved value --
        // cockpit-driven control should be the default state on every launch.
        DcsManualRadioControlOverride = false;
        // Keep the computed AppData default when no path was saved.
        if (!string.IsNullOrWhiteSpace(settings.RecordingPath))
            RecordingPath = settings.RecordingPath;
        StreamToDevice = settings.CaptureSink == IOpenFreqService.CaptureSink.Device;
        ApplyOwnVoiceSfx = settings.ApplyOwnVoiceSfx;
        if (settings.DarkMode.HasValue)
        {
            IsDarkMode = settings.DarkMode.Value;
            Application.Current!.RequestedThemeVariant = IsDarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        else
        {
            // No saved preference — RequestedThemeVariant stays "Default" (follows OS).
            Dispatcher.UIThread.Post(() =>
            {
                IsDarkMode = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
            }, DispatcherPriority.Background);
        }

        // Restore audio device selection
        InputDeviceName = settings.InputDeviceName;
        OutputDeviceName = settings.OutputDeviceName;

        if (!string.IsNullOrEmpty(InputDeviceName))
        {
            RecordingDeviceIndex = RecordingDeviceNames.ToList().IndexOf(InputDeviceName);
            if (RecordingDeviceIndex < 0)
                RecordingDeviceIndex = _audioService.DefaultRecordingDevice;
        }

        if (!string.IsNullOrEmpty(OutputDeviceName))
        {
            PlaybackDeviceIndex = PlaybackDeviceNames.ToList().IndexOf(OutputDeviceName);
            if (PlaybackDeviceIndex < 0)
                PlaybackDeviceIndex = _audioService.DefaultPlaybackDevice;
        }

        // Monitor/stream output device (reuses the playback device list).
        MonitorDeviceName = settings.MonitorDeviceName;
        MonitorDeviceIndex = !string.IsNullOrEmpty(MonitorDeviceName)
            ? PlaybackDeviceNames.ToList().IndexOf(MonitorDeviceName)
            : _audioService.DefaultPlaybackDevice;
        if (MonitorDeviceIndex < 0)
            MonitorDeviceIndex = _audioService.DefaultPlaybackDevice;

        RestoreWindowPosition(settings);

        // Re-select BMS theater from saved heightmap path
        if (BmsInstallFound && !string.IsNullOrEmpty(HeightmapPath))
        {
            var match = TheaterDefinitions.FirstOrDefault(t =>
                string.Equals(t.HeightmapPath, HeightmapPath, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                SelectedBmsTheater = match;
            }
        }

        // Sync HeightmapPath from already-selected theater if path was empty (e.g. clean state)
        if (string.IsNullOrEmpty(HeightmapPath) && SelectedBmsTheater?.HeightmapPath != null)
            HeightmapPath = SelectedBmsTheater.HeightmapPath;
    }

    private void RestoreWindowPosition(OpenFreqSettings settings)
    {
        _left = settings.Left;
        _top = settings.Top;
        _width = settings.Width;
        _height = settings.Height;
        _windowState = settings.WindowState;
        _maximizedScreenX = settings.MaximizedScreenX;
        _maximizedScreenY = settings.MaximizedScreenY;
        _maximizedScreenWidth = settings.MaximizedScreenWidth;
        _maximizedScreenHeight = settings.MaximizedScreenHeight;

        // Window settings
        if (settings.Left == null || settings.Top == null || settings.Height == null || settings.Width == null ||
            settings.WindowState == null)
        {
            // There are no settings to restore.
            // So leave the windows size and position at their defaults.
            return;
        }

        if (settings.Width <= 0 || settings.Height <= 0)
        {
            // Safeguard against 0 values
            _left = _top = _width = _height = _windowState = null;
            return;
        }

        if (settings.WindowState == (int)WindowState.Maximized)
        {
            // Try to find the screen it was maximized on
            var screenToMaximeOn = FindScreenByBounds(
                settings.MaximizedScreenX,
                settings.MaximizedScreenY,
                settings.MaximizedScreenWidth,
                settings.MaximizedScreenHeight);

            if (screenToMaximeOn != null)
            {
                // Position window on that screen before maximizing
                MainWindow.Position = new PixelPoint(
                    screenToMaximeOn.WorkingArea.X + 100,
                    screenToMaximeOn.WorkingArea.Y + 100);
            }

            MainWindow.WindowState = WindowState.Maximized;
            return;
        }

        // Never restore to minimized
        if (settings.WindowState.Value == (int)WindowState.Minimized)
        {
            settings.WindowState = (int)WindowState.Normal;
        }

        var savedPosition = new PixelPoint(settings.Left.Value, settings.Top.Value);
        var screen = FindScreenContainingPositionInWorkingArea(savedPosition);
        if (screen == null)
        {
            // The saved window position (its top left corner) is not in the working area of an active screen.
            // So leave the windows size and position at their defaults.
            return;
        }

        const int min = 50;
        if (settings.Left.Value > screen.WorkingArea.X + screen.WorkingArea.Width - min
            || settings.Top.Value > screen.WorkingArea.Y + screen.WorkingArea.Height - min)
        {
            // The saved top left corner (position) is so close to the right or bottom edge of the screen's working area as to make the window difficult to access.
            // So leave the windows size and position at their defaults.
            return;
        }

        MainWindow.Position = savedPosition;

        MainWindow.Width = settings.Width.Value;
        MainWindow.Height = settings.Height.Value;
    }

    private Screen? FindScreenContainingPositionInWorkingArea(PixelPoint position)
    {
        return (
            // All active screens, not just any screens overlapping the window!
            from screen in ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!)
                .MainWindow!.Screens.All
            where screen.WorkingArea.Contains(position)
            select screen).FirstOrDefault();
    }

    private Screen? FindScreenByBounds(int? x, int? y, int? width, int? height)
    {
        if (!x.HasValue || !y.HasValue || !width.HasValue || !height.HasValue)
            return null;

        return MainWindow.Screens.All.FirstOrDefault(s =>
            s.Bounds.X == x.Value &&
            s.Bounds.Y == y.Value &&
            s.Bounds.Width == width.Value &&
            s.Bounds.Height == height.Value);
    }

    public OpenFreqSettings GetSettings()
    {
        return new OpenFreqSettings
        {
            OpenFreqServerAddress = OpenFreqServerAddress,
            OpenFreqPassword = OpenFreqPassword,
            OpenFreqServerAddressHistory = [.. OpenFreqServerAddressHistory],
            OwnPositionMode = ConnectionMode,
            TacviewServerAddress = TacviewServerAddress,
            TacviewServerPassword = TacviewServerPassword,
            TacviewServerAddressHistory = [.. TacviewServerAddressHistory],
            DisplayName = DisplayName,
            InputDeviceName = InputDeviceName,
            OutputDeviceName = OutputDeviceName,
            HeightmapPath = HeightmapPath,
            SelectedTheater = SelectedTheater,
            MapLayer = MapLayer,
            BmsRadio1Pan = BmsRadio1Pan,
            BmsRadio2Pan = BmsRadio2Pan,
            DcsRadioPans = new Dictionary<string, int>(_dcsRadioPans, StringComparer.OrdinalIgnoreCase),
            DcsPttHotkeys = new Dictionary<string, HotkeyBinding>(_dcsPttHotkeys, StringComparer.OrdinalIgnoreCase),
            GlobalPttHotkey = GlobalPttHotkey,
            BmsSquelchUhfHotkey = BmsUhfSquelchHotkey,
            BmsSquelchVhfHotkey = BmsVhfSquelchHotkey,
            MasterVolume = MasterVolume,
            SidetoneEnabled = SidetoneEnabled,
            MicNormalizationEnabled = MicNormalizationEnabled,
            InputGain = InputGain,
            SidetoneVolume = SidetoneVolume,
            MinimizeOnConnect = MinimizeOnConnect,
            AmbientNoiseVolume = AmbientNoiseVolume,
            AutoRecordInGameMode = AutoRecordInGameMode,
            DcsManualRadioControlOverride = DcsManualRadioControlOverride,
            RecordingPath = RecordingPath,
            CaptureSink = StreamToDevice
                ? IOpenFreqService.CaptureSink.Device
                : IOpenFreqService.CaptureSink.File,
            MonitorDeviceName = MonitorDeviceName,
            ApplyOwnVoiceSfx = ApplyOwnVoiceSfx,
            DarkMode = IsDarkMode,
            Left = _left,
            Top = _top,
            Width = _width,
            Height = _height,
            WindowState = _windowState,
            MaximizedScreenHeight = _maximizedScreenHeight,
            MaximizedScreenWidth = _maximizedScreenWidth,
            MaximizedScreenX = _maximizedScreenX,
            MaximizedScreenY = _maximizedScreenY
        };
    }

    public int GetDcsRadioPan(string radioId) =>
        _dcsRadioPans.TryGetValue(radioId, out var pan)
            ? Math.Clamp(pan, -100, 100)
            : 0;

    public void SetDcsRadioPan(string radioId, int pan)
    {
        if (string.IsNullOrWhiteSpace(radioId))
            return;

        _dcsRadioPans[radioId] = Math.Clamp(pan, -100, 100);
    }

    /// <summary>Raised when a setting that shouldn't wait for the normal clean-shutdown-only
    /// save (see App.axaml.cs's ShutdownRequested handler -- settings otherwise only persist to
    /// disk on a clean exit, so a crash/task-kill loses anything changed since the last save)
    /// changes. Currently just DCS PTT keybinds, since losing a captured keybind is a
    /// particularly frustrating way to lose unsaved state. MainWindowViewModel subscribes and
    /// triggers an immediate save.</summary>
    public event EventHandler? PersistImmediately;

    public HotkeyBinding? GetDcsPttHotkey(string radioId) =>
        _dcsPttHotkeys.GetValueOrDefault(radioId);

    public void SetDcsPttHotkey(string radioId, HotkeyBinding? hotkey)
    {
        if (string.IsNullOrWhiteSpace(radioId))
            return;

        if (hotkey == null)
            _dcsPttHotkeys.Remove(radioId);
        else
            _dcsPttHotkeys[radioId] = hotkey;

        PersistImmediately?.Invoke(this, EventArgs.Empty);
    }

    private const int MaxAddressHistory = 10;

    public void AddOpenFreqServerAddressToHistory() => AddToHistory(OpenFreqServerAddressHistory, OpenFreqServerAddress);

    public void AddTacviewServerAddressToHistory() => AddToHistory(TacviewServerAddressHistory, TacviewServerAddress);

    private static void AddToHistory(ObservableCollection<string> history, string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return;

        var existing = history.FirstOrDefault(h => string.Equals(h, value, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            history.Remove(existing);

        history.Insert(0, value);
        while (history.Count > MaxAddressHistory)
            history.RemoveAt(history.Count - 1);
    }

    public void UpdateWindowSettings()
    {
        // Always save the current state
        _windowState = (int)MainWindow.WindowState;

        switch (MainWindow.WindowState)
        {
            case WindowState.Minimized:
                return;

            case WindowState.Normal:
                _left = MainWindow.Position.X;
                _top = MainWindow.Position.Y;
                _width = (int)MainWindow.Width;
                _height = (int)MainWindow.Height;
                break;

            case WindowState.Maximized:
                var screen = MainWindow.Screens.ScreenFromWindow(MainWindow);
                if (screen != null)
                {
                    _maximizedScreenX = screen.Bounds.X;
                    _maximizedScreenY = screen.Bounds.Y;
                    _maximizedScreenWidth = screen.Bounds.Width;
                    _maximizedScreenHeight = screen.Bounds.Height;
                }

                // Don't update _left, _top, _width, _height - keep the last normal values
                break;
        }
    }

    public void Dispose()
    {
        _audioService.PlaybackDevicesChanged -= OnPlaybackDevicesChanged;
        _audioService.RecordingDevicesChanged -= OnRecordingDevicesChanged;
        _openFreqService.MicLevelChanged -= OnMicLevelChanged;
        _openFreqService.InputMeterEnabled = false;
    }
}
