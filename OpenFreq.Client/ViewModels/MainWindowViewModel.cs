using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Styles.Controls;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreqClient.Models;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;
using SharpHook.Data;

namespace OpenFreqClient.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IAudioService _audioService;
    private readonly IAcmiClientService _acmiClientService;
    private readonly IConfigurationService _configurationService;

    private readonly ILogger<MainWindowViewModel> _logger;

    [ObservableProperty] private ChannelCardListViewModel _channelList;
    [ObservableProperty] private SettingsViewModel _settings;

    [ObservableProperty] private bool _openFreqConnected;
    [ObservableProperty] private bool _tacviewConnected;
    [ObservableProperty] private ObservableCollection<TacviewAircraftItem> _tacviewFlightCallsigns = [];
    [ObservableProperty] private TacviewAircraftItem? _selectedTacviewCallsign;
    public record TacviewAircraftItem(string CallSign, string ObjectId)
    { 
        public override string ToString() => CallSign;
    }

    private CancellationTokenSource? _callsignUpdateCts;
    private Task? _callsignUpdateTask;

    [ObservableProperty] private string _statusMessage = "Disconnected";
    [ObservableProperty] private string _peerId = String.Empty;

    [ObservableProperty] private string _connectionStatusString = String.Empty;


    // Error handling properties
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private ObservableCollection<string> _errorLog = new();

    public ColorZoneMode AppBarColorZone =>
        (OpenFreqConnected && TacviewConnected) ? ColorZoneMode.PrimaryMid : ColorZoneMode.Accent;


    public MainWindowViewModel(
        IOpenFreqService openFreqService,
        IHotkeyService hotkeyService,
        IAudioService audioService,
        IAcmiClientService acmiClientService,
        IConfigurationService configurationService,
        ILogger<MainWindowViewModel> logger,
        ChannelCardListViewModel channelList,
        SettingsViewModel settings)
    {
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        _audioService = audioService;
        _acmiClientService = acmiClientService;
        _configurationService = configurationService;
        _logger = logger;
        _channelList = channelList;
        _settings = settings;

        // Subscribe to service events
        _openFreqService.ConnectionStateChanged += OnConnectionStateChanged;
        _openFreqService.StatusMessageReceived += OnStatusMessageReceived;
        _openFreqService.PeerActivityReceived += OnPeerActivityReceived;

        // Wire the ACMI transformation service with the position update
        _acmiClientService.TrackedAircraftTransformUpdated += (s, e) =>
        {
            _openFreqService.UpdateAircraftPosition(e.Transform.U, e.Transform.V, e.Transform.Altitude);
            Console.WriteLine($"[POS UPDATE] Updating to ({e.Transform.U:F0}, {e.Transform.V:F0}, {e.Transform.Altitude:F0})");
        };

        // Start listening for hotkeys
        _hotkeyService.Start();

        // Load config
        _ = LoadConfigurationAsync();

        UpdateConnectionStatusString();
    }

    partial void OnOpenFreqConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(AppBarColorZone));
    }

    [RelayCommand]
    private async Task ConnectDisconnectAsync()
    {
        try
        {
            if (_openFreqService.IsConnected)
            {
                _ = _openFreqService.DisconnectAsync();

                if (_acmiClientService.Status == AcmiConnectionStatus.Connected ||
                    _acmiClientService.Status == AcmiConnectionStatus.Connecting)
                {
                    _ = _acmiClientService.DisconnectAsync();
                }

                ClearError();
                return;
            }

            // Validate settings before connecting
            if (string.IsNullOrWhiteSpace(Settings.OpenFreqServerAddress))
            {
                ShowError("Server address is not configured. Please check Settings.");
                return;
            }

            // Initialize service with settings
            _openFreqService.Initialize(Settings.GetSettings(), Settings.RecordingDeviceIndex,
                Settings.PlaybackDeviceIndex);

            // Connect to server (channels will auto-join when authenticated)
            await _openFreqService.ConnectAsync();
            _openFreqService.LoadHeightmap(Settings.HeightmapPath);

            if (Settings.ConnectionMode == OpenFreqSettings.Mode.GCI)
            {
                _acmiClientService.ConnectionStatusChanged += OnTacviewConnectionStatusChanged;
                await _acmiClientService.ConnectAsync(Settings.TacviewServerAddress, Settings.TacviewServerPassword);
            }


            ClearError();
        }
        catch (InvalidOperationException ex)
        {
            ShowError($"Configuration error: {ex.Message}");
        }
        catch (TimeoutException)
        {
            ShowError("Connection timeout. Please check server address and network connection.");
        }
        catch (Exception ex)
        {
            ShowError($"Connection failed: {ex.Message}");
        }
    }

    private void OnTacviewConnectionStatusChanged(object? sender, AcmiConnectionEventArgs e)
    {
        UpdateConnectionStatusString();
        if (e.Status == AcmiConnectionStatus.Connected)
        {
            _callsignUpdateCts?.Cancel();
            _callsignUpdateCts = new CancellationTokenSource();
            _callsignUpdateTask = UpdateTacviewCallsigns(_callsignUpdateCts.Token);
        }
        else
        {
            _callsignUpdateCts?.Cancel();
            _callsignUpdateCts?.Dispose();
            _callsignUpdateCts = null;
        }
    }

    partial void OnSelectedTacviewCallsignChanged(TacviewAircraftItem selectedTacviewCallsign)
    {
        _acmiClientService?.SetTrackedAircraft(selectedTacviewCallsign.ObjectId);
    }

    private async Task UpdateTacviewCallsigns(CancellationToken cancellationToken)
    {
        while (_acmiClientService.Status == AcmiConnectionStatus.Connected
               && !cancellationToken.IsCancellationRequested)
        {
            if (SelectedTacviewCallsign != null)
            {
                var aircraft = _acmiClientService.GetAircraft(SelectedTacviewCallsign.ObjectId);
                ShowError($"{aircraft.CallSign}: {aircraft.Transform.U} | {aircraft.Transform.V} | {aircraft.Transform.Altitude}");
            }

            var currentAircraft = _acmiClientService.GetAllAircraft()
                .Select(ac => new TacviewAircraftItem(ac.CallSign, ac.ObjectId))
                .ToList();

            // Incremental update
            var currentIds = currentAircraft.Select(a => a.ObjectId).ToHashSet();
        
            // Remove items no longer present
            for (int i = TacviewFlightCallsigns.Count - 1; i >= 0; i--)
            {
                if (!currentIds.Contains(TacviewFlightCallsigns[i].ObjectId))
                {
                    TacviewFlightCallsigns.RemoveAt(i);
                }
            }

            // Add new items
            var existingIds = TacviewFlightCallsigns.Select(a => a.ObjectId).ToHashSet();
            foreach (var aircraft in currentAircraft)
            {
                if (aircraft.CallSign != string.Empty && !existingIds.Contains(aircraft.ObjectId))
                {
                    TacviewFlightCallsigns.Add(aircraft);
                }
            }
            
            try
            {
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
        }
    }

    [RelayCommand]
    private async Task ConnectToAcmiAsync()
    {
        await _acmiClientService.ConnectAsync(Settings.TacviewServerAddress, Settings.TacviewServerPassword);
    }

    [RelayCommand]
    private void ClearError()
    {
        HasError = false;
        ErrorMessage = "";
    }

    private void ShowError(string message)
    {
        _logger.LogError(message);
        HasError = true;
        ErrorMessage = message;
        StatusMessage = $"⚠️ {message}";

        // Add to error log with timestamp
        var logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        ErrorLog.Insert(0, logEntry);

        // Keep only last 50 errors
        while (ErrorLog.Count > 50)
        {
            ErrorLog.RemoveAt(ErrorLog.Count - 1);
        }
    }

    private void UpdateConnectionStatusString()
    {
        if (Settings.ConnectionMode == OpenFreqSettings.Mode.GCI)
        {
            ConnectionStatusString =
                $"OpenFreq {_openFreqService.Status.ToString()} | Tacview {_acmiClientService.Status.ToString()}";
        }
        else
        {
            ConnectionStatusString = $"OpenFreq {_openFreqService.Status.ToString()}";
        }
    }

    // Service event handlers
    private void OnConnectionStateChanged(object? sender, ConnectionState state)
    {
        UpdateConnectionStatusString();
        OpenFreqConnected = state == ConnectionState.Connected || state == ConnectionState.Authenticated;
        StatusMessage = state switch
        {
            ConnectionState.Disconnected => "Disconnected",
            ConnectionState.Connecting => "Connecting...",
            ConnectionState.Connected => "Connected",
            ConnectionState.Authenticated => "Authenticated",
            _ => "Unknown"
        };

        if (state == ConnectionState.Authenticated)
        {
            PeerId = _openFreqService.PeerId ?? "";
            ClearError();
        }
        else if (state == ConnectionState.Disconnected && OpenFreqConnected)
        {
            ShowError("Lost connection to server");
        }
    }

    private void OnStatusMessageReceived(object? sender, string message)
    {
        // Check if message contains error indicators
        if (message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            ShowError(message);
        }
        else
        {
            StatusMessage = message;
            ClearError();
        }
    }

    private void OnPeerActivityReceived(object? sender, PeerActivityEventArgs e)
    {
        // Could be used for a log or notifications panel
        StatusMessage = e.Message;
    }

    public void Dispose()
    {
        _ = SaveConfigurationAsync();
        
        _callsignUpdateCts?.Cancel();
        _callsignUpdateCts?.Dispose();

        ChannelList.Dispose();
        _openFreqService.Dispose();
        _hotkeyService.Dispose();
        _acmiClientService.Dispose();
        _configurationService.Dispose();
    }


    private async Task LoadConfigurationAsync()
    {
        try
        {
            var config = await _configurationService.LoadConfigurationAsync();

            // Load settings
            Settings.OpenFreqServerAddress = config.Settings.OpenFreqServerAddress;
            Settings.OpenFreqPassword = config.Settings.OpenFreqPassword;
            Settings.ConnectionMode = config.Settings.ConnectionMode;
            Settings.InputDeviceName = config.Settings.InputDeviceName;
            Settings.OutputDeviceName = config.Settings.OutputDeviceName;
            Settings.HeightmapPath = config.Settings.HeightmapPath;

            // Load audio settings
            Settings.LoadFromSettings(config.Settings);

            // Load channels
            foreach (var channelData in config.Channels)
            {
                var channel =
                    ChannelList.CreateChannel(channelData.FrequencyMhz, channelData.Name ?? "", channelData.Type);
                channel.IsEnabled = channelData.Enabled;
                channel.IsEditing = false;

                // Parse and set hotkey
                if (Enum.TryParse<KeyCode>(channelData.HotkeyCode, out var keyCode))
                {
                    channel.HotKey = keyCode;
                    if (keyCode != KeyCode.VcUndefined)
                    {
                        _hotkeyService.RegisterHotkey(keyCode, channel.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ShowError($"Failed to load configuration: {ex.Message}");
        }
    }

    public async Task SaveConfigurationAsync()
    {
        try
        {
            var config = new AppConfiguration
            {
                Settings = Settings.GetSettings(),
                Channels = ChannelList.Channels.Select(c => new ChannelData
                {
                    Name = c.Name,
                    FrequencyMhz = c.FrequencyMhz,
                    Type = c.Type,
                    HotkeyCode = c.HotKey.ToString(),
                    Enabled = c.IsEnabled
                }).ToList()
            };

            await _configurationService.SaveConfigurationAsync(config);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to save configuration: {ex.Message}");
        }
    }
}