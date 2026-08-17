using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FalconBmsDataService.Models;
using OpenFreq.Client.Models;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;
using SharpHook.Data;

namespace OpenFreqClient.ViewModels;

public partial class ChannelCardViewModel : ViewModelBase, IDisposable
{
    private readonly IHotkeyService _hotkeyService;
    private readonly LocationViewModel _parentLocationViewModel;
    public SettingsViewModel? Settings { get; }

    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrequencyMhzString))]
    [NotifyPropertyChangedFor(nameof(Type))]
    public partial int FrequencyKhz { get; set; }

    /// <summary>
    /// Frequency display string in MHz
    /// </summary>
    public string FrequencyMhzString
    {
        get => (FrequencyKhz / 1000d).ToString("F3", CultureInfo.InvariantCulture);
        set
        {
            const NumberStyles styles = NumberStyles.AllowDecimalPoint
                                        | NumberStyles.AllowLeadingWhite
                                        | NumberStyles.AllowTrailingWhite;

            // Accept either '.' or ',' as the decimal separator; normalize to '.'.
            var normalized = (value ?? string.Empty).Replace(',', '.');

            if (!double.TryParse(normalized, styles, CultureInfo.InvariantCulture, out var mhz))
                throw new ArgumentException("Enter a frequency in MHz, e.g. 251.000");

            if (mhz <= 0 || mhz > MaxFrequencyMhz)
                throw new ArgumentException($"Frequency must be between 0 and {MaxFrequencyMhz:F0} MHz");

            FrequencyKhz = (int)Math.Round(mhz * 1000d);
        }
    }

    /// <summary>Upper bound for a tunable frequency in MHz (UHF military band ceiling).</summary>
    private const double MaxFrequencyMhz = 400d;

    [ObservableProperty] public partial string? Name { get; set; }

    [ObservableProperty] public partial float RxDb { get; set; }

    // this is just to display it in the UI
    public Channel.ChannelType Type
    {
        get
        {
            return (FrequencyKhz / 1000d) switch
            {
                < 200 and > 30 => Channel.ChannelType.VHF,
                > 200 => Channel.ChannelType.UHF,
                _ => Channel.ChannelType.Custom
            };
        }
    }

    // Direct mapping to BMS RadioType or null in GCI mode.
    // We cant use a sane frequency->type mapping because BMS likes to set lobby frequencies, e.g. 1.234 MHz
    public RadioType? BmsRadioType { get; set; }

    // Stable DCS radio key, e.g. "Arc210" or "Arc164:guard".
    public string? DcsRadioId { get; set; }

    [ObservableProperty] public partial double SignalStrengthPercent { get; set; }
    /// <summary>Received signal-to-noise ratio, in dB.</summary>
    [ObservableProperty] public partial double SignalStrengthSnrDb { get; set; }
    /// <summary>Received signal level, in dBm.</summary>
    [ObservableProperty] public partial double SignalStrengthReceivedDb { get; set; }

    [ObservableProperty]
    public partial Channel.ChannelConnectionStatus ConnectionStatus { get; set; } =
        Channel.ChannelConnectionStatus.Disconnected;

    [ObservableProperty]
    public partial Channel.ChannelTransmissionStatus TransmissionStatus { get; set; } =
        Channel.ChannelTransmissionStatus.Idle;

    [ObservableProperty] public partial bool IsEditing { get; set; }

    [ObservableProperty] private bool _channelWasChanged;

    // Hotkey binding
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplay), nameof(HasPttHotkey))]
    public partial HotkeyBinding? PttHotKey { get; set; } = null;

    public bool HasPttHotkey => PttHotKey != null;


    [ObservableProperty] public partial bool IsCapturingPttHotkey { get; set; }

    public string HotkeyDisplay => PttHotKey?.DisplayName ?? "None";

    [ObservableProperty] public partial HotkeyBinding? SquelchHotKey { get; set; }

    // Reference to the data of the RadioStationGroup
    [ObservableProperty] public partial RadioStationData RadioStationData { get; set; }

    [ObservableProperty] public partial bool IsEditable { get; set; }

    /// <summary>Pan: -100 = full left, 0 = center, +100 = full right.</summary>
    [ObservableProperty]
    public partial int Pan { get; set; } = 0;

    public bool ShowManualPanControl =>
        RadioStationData.Type == RadioStationData.RadioStationType.DCS;

    /// <summary>Linear volume gain (1.0 = unity/0 dB). For DCS/BMS channels this is normally
    /// driven by the cockpit volume knob (see ChannelCardListViewModel.SyncDcsChannelOnUiThread);
    /// it's only user-editable when <see cref="CanAdjustRadioControls"/> is true.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeDb))]
    public partial double Volume { get; set; } = 1.0;

    /// <summary>Volume in dB (0 dB = unity), for the UI slider. Backed by <see cref="Volume"/>.</summary>
    public double VolumeDb
    {
        get => Volume <= 0.0001 ? -80.0 : 20.0 * Math.Log10(Volume);
        set => Volume = Math.Pow(10.0, value / 20.0);
    }

    /// <summary>Whether the Volume/Squelch controls are user-editable right now. Non-DCS channels
    /// (GCI/stationary/BMS) have no cockpit to drive them and are always adjustable; DCS channels
    /// are cockpit-driven by default and only become adjustable when the user has explicitly
    /// enabled the manual-override setting.</summary>
    public bool CanAdjustRadioControls =>
        RadioStationData.Type != RadioStationData.RadioStationType.DCS ||
        (Settings?.DcsManualRadioControlOverride ?? false);

    /// <summary>KY-58/COMSEC encryption engaged. Synced automatically from the cockpit for DCS
    /// channels; user-editable for manually-created (GCI/stationary/BMS) channels.</summary>
    [ObservableProperty] public partial bool Enc { get; set; }

    /// <summary>Encryption key channel (1-6). 0 = none.</summary>
    [ObservableProperty] public partial int EncKey { get; set; } = 1;

    /// <summary>Selectable key channels for the manual encryption key picker.</summary>
    public static int[] EncKeyOptions { get; } = [1, 2, 3, 4, 5, 6];

    /// <summary>HAVE QUICK frequency-hopping engaged.</summary>
    [ObservableProperty] public partial bool HqOn { get; set; }

    /// <summary>Whether this channel can decrypt/encrypt at all. Defaults to true — both DCS
    /// channels (the cockpit has a KY-58/COMSEC panel) and manually-created GCI/stationary
    /// channels (assumed to represent a station with compatible secure comms gear) can attempt
    /// to decrypt; <see cref="Enc"/>/<see cref="EncKey"/> determine whether they actually do.</summary>
    [ObservableProperty] public partial bool CryptoCapable { get; set; } = true;

    /// <summary>Only DCS-sourced channels sync Enc/EncKey/HqOn/CryptoCapable automatically; other
    /// types expose them as user-editable controls.</summary>
    public bool ShowManualEncryptionControls =>
        RadioStationData.Type != RadioStationData.RadioStationType.DCS;

    [ObservableProperty] public partial bool IsSquelchEnabled { get; set; } = true;

    // Store original values when entering edit mode
    private int _originalFrequencyKhz;


    [RelayCommand]
    public void BmsLobby1Clicked()
    {
        Name = "BMS Lobby 1";
        FrequencyKhz = 1234;
        PttHotKey = new KeyboardBinding(KeyCode.VcF1);
        ToggleEditing();
    }

    [RelayCommand]
    public void BmsLobby2Clicked()
    {
        Name = "BMS Lobby 2";
        FrequencyKhz = 339750;
        PttHotKey = new KeyboardBinding(KeyCode.VcF2);
        ToggleEditing();
    }


    public ChannelCardViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService, string name,
        int frequencyKhz, bool isInEditMode,
        RadioStationData radioStationData,
        LocationViewModel parentLocationViewModel, SettingsViewModel settings, bool isEditable = true,
        RadioType? bmsRadioType = null)
    {
        Name = name;
        FrequencyKhz = frequencyKhz;
        IsEditing = isInEditMode;
        _hotkeyService = hotkeyService;
        RadioStationData = radioStationData;
        _parentLocationViewModel = parentLocationViewModel;
        Settings = settings;
        IsEditable = isEditable;
        BmsRadioType = bmsRadioType;
        if (Settings != null)
            Settings.PropertyChanged += OnSettingsPropertyChanged;

        WeakReferenceMessenger.Default.Register<SignalStrengthTracker.SignalStrengthUpdateMessage>(this,
            (_, m) =>
            {
                if (FrequencyKhz == m.FrequencyKhz)
                {
                    SignalStrengthPercent = m.StrengthPercent;
                    SignalStrengthSnrDb = m.SnrDb;
                    SignalStrengthReceivedDb = m.ReceivedDb;
                }
            });
    }

    [RelayCommand]
    public void ToggleEditing()
    {
        if (!IsEditing)
        {
            // Entering edit mode - store current values
            _originalFrequencyKhz = FrequencyKhz;
        }
        else
        {
            // Exiting edit mode - send update if changed
            var message = new ChannelUpdatedMessage(
                Id,
                _originalFrequencyKhz,
                FrequencyKhz,
                ConnectionStatus,
                Pan,
                _parentLocationViewModel.IsBmsLocation
            );

            WeakReferenceMessenger.Default.Send(message);
        }

        IsEditing = !IsEditing;
    }

    [RelayCommand]
    private async Task BeginCaptureHotkeyAsync()
    {
        IsCapturingPttHotkey = true;
        try
        {
            var capturedKey = await _hotkeyService.CaptureNextHotkeyAsync();
            PttHotKey = capturedKey;
        }
        catch (OperationCanceledException)
        {
            // Capture was cancelled
        }
        finally
        {
            IsCapturingPttHotkey = false;
        }
    }

    partial void OnPttHotKeyChanging(HotkeyBinding? oldValue, HotkeyBinding? newValue)
    {
        // Unregister old binding
        if (oldValue != null)
        {
            _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.Ptt, oldValue, Id);
        }
    }

    partial void OnPttHotKeyChanged(HotkeyBinding? oldValue, HotkeyBinding? newValue)
    {
        // Register new binding
        if (newValue != null)
        {
            _hotkeyService.RegisterHotkey(IHotkeyService.HotkeyType.Ptt, newValue, Id);
        }
    }

    partial void OnSquelchHotKeyChanging(HotkeyBinding? oldValue, HotkeyBinding? newValue)
    {
        // Unregister old binding
        if (oldValue != null)
        {
            _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.SquelchToggle, oldValue, Id);
        }
    }

    partial void OnSquelchHotKeyChanged(HotkeyBinding? oldValue, HotkeyBinding? newValue)
    {
        // Register new binding
        if (newValue != null)
        {
            _hotkeyService.RegisterHotkey(IHotkeyService.HotkeyType.SquelchToggle, newValue, Id);
        }
    }

    [RelayCommand]
    private void ClearPttHotkey()
    {
        if (PttHotKey == null) return;
        _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.Ptt, PttHotKey, Id);
        PttHotKey = null;
    }

    partial void OnFrequencyKhzChanged(int value)
    {
        _channelWasChanged = true;
    }


    [RelayCommand]
    public void DeleteChannel()
    {
        WeakReferenceMessenger.Default.Send(new ChannelDeleteRequestedMessage(Id, FrequencyKhz));
    }

    public void StartTransmission()
    {
        if (ConnectionStatus == Channel.ChannelConnectionStatus.Disconnected)
            return;

        if (RadioStationData.Type == RadioStationData.RadioStationType.DCS &&
            DcsRadioId?.Contains(":guard", StringComparison.OrdinalIgnoreCase) == true)
            return;

        // Don't allow click transmissions in BMS 3d mode - use the comms switch there.
        if (RadioStationData.Type == RadioStationData.RadioStationType.BMS &&
            Settings is { ModeIsGci: false, Is3dMode: true })
            return;

        // mute only the transmitting frequency
        var mutedFrequencies = new List<int> { FrequencyKhz };
        WeakReferenceMessenger.Default.Send(new StartTransmissionMessage(Id, FrequencyKhz, RadioStationData,
            mutedFrequencies));
    }

    public void StopTransmission()
    {
        if (ConnectionStatus == Channel.ChannelConnectionStatus.Disconnected)
            return;

        if (RadioStationData.Type == RadioStationData.RadioStationType.DCS &&
            DcsRadioId?.Contains(":guard", StringComparison.OrdinalIgnoreCase) == true)
            return;

        // Don't allow click transmissions in BMS 3d mode - use the comms switch there.
        if (RadioStationData.Type == RadioStationData.RadioStationType.BMS &&
            Settings is { ModeIsGci: false, Is3dMode: true })
            return;

        WeakReferenceMessenger.Default.Send(new StopTransmissionMessage(Id, FrequencyKhz));
    }

    partial void OnPanChanged(int value)
    {
        WeakReferenceMessenger.Default.Send(new ChannelPanUpdateMessage(Id, FrequencyKhz, value));
    }

    partial void OnVolumeChanged(double value)
    {
        WeakReferenceMessenger.Default.Send(new ChannelVolumeUpdateMessage(Id, FrequencyKhz, value));
    }

    partial void OnEncChanged(bool value) => SendEncryptionUpdate();
    partial void OnEncKeyChanged(int value) => SendEncryptionUpdate();
    partial void OnHqOnChanged(bool value) => SendEncryptionUpdate();
    partial void OnCryptoCapableChanged(bool value) => SendEncryptionUpdate();

    private void SendEncryptionUpdate()
    {
        WeakReferenceMessenger.Default.Send(
            new ChannelEncryptionUpdateMessage(Id, FrequencyKhz, Enc, EncKey, HqOn, CryptoCapable));
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ConnectionMode))
            OnPropertyChanged(nameof(ShowManualPanControl));
        if (e.PropertyName == nameof(SettingsViewModel.DcsManualRadioControlOverride))
            OnPropertyChanged(nameof(CanAdjustRadioControls));
    }

    [RelayCommand]
    public void ToggleJoinLeave()
    {
        WeakReferenceMessenger.Default.Send(new ChannelJoinLeaveRequestedMessage(Id, FrequencyKhz,
            ConnectionStatus != Channel.ChannelConnectionStatus.Connected, RadioStationData));
    }

    public void Join()
    {
        WeakReferenceMessenger.Default.Send(new ChannelJoinLeaveRequestedMessage(Id, FrequencyKhz,
            true, RadioStationData));
    }

    public void Leave()
    {
        WeakReferenceMessenger.Default.Send(new ChannelJoinLeaveRequestedMessage(Id, FrequencyKhz,
            false, RadioStationData));
    }

    [RelayCommand]
    public void ToggleSquelch() => IsSquelchEnabled = !IsSquelchEnabled;

    partial void OnIsSquelchEnabledChanged(bool value) =>
        WeakReferenceMessenger.Default.Send(new SquelchEnabledDisabledMessage(channelId: Id,
            frequencyKhz: FrequencyKhz, squelchEnabled: value));

    public void Dispose()
    {
        if (PttHotKey != null)
        {
            _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.Ptt, PttHotKey, Id);
        }
        if (Settings != null)
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
        WeakReferenceMessenger.Default.Unregister<SignalStrengthTracker.SignalStrengthUpdateMessage>(this);
    }
}

public class ChannelUpdatedMessage(
    Guid channelId,
    int oldFrequencyKhz,
    int newFrequencyKhz,
    Channel.ChannelConnectionStatus oldConnectionStatus,
    int currentPan,
    bool isBmsChannel)
{
    public Guid ChannelId { get; } = channelId;
    public int OldFrequencyKhz { get; } = oldFrequencyKhz;
    public int NewFrequencyKhz { get; } = newFrequencyKhz;
    public Channel.ChannelConnectionStatus OldConnectionStatus { get; } = oldConnectionStatus;
    public bool IsBmsChannel { get; } = isBmsChannel;
    public int CurrentPan { get; } = currentPan;

    public bool NeedsReconnect => !IsBmsChannel &&
                                  OldFrequencyKhz != NewFrequencyKhz &&
                                  OldConnectionStatus == Channel.ChannelConnectionStatus.Connected;
}

public class ChannelJoinLeaveRequestedMessage(
    Guid channelId,
    int frequencyKhz,
    bool join,
    RadioStationData radioStationData)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public bool Join { get; } = join;
    public RadioStationData RadioStationData { get; } = radioStationData;
}

public class SquelchEnabledDisabledMessage(
    Guid channelId,
    int frequencyKhz,
    bool squelchEnabled)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public bool SquelchEnabled { get; } = squelchEnabled;
}

public class StartTransmissionMessage(
    Guid channelId,
    int frequencyKhz,
    RadioStationData radioStationData,
    List<int> mutedRadioChannels)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public RadioStationData RadioStationData { get; } = radioStationData;
    public List<int> MutedRadioChannels { get; } = mutedRadioChannels;
}

public class StopTransmissionMessage(Guid channelId, int frequencyKhz)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
}

public class ChannelDeleteRequestedMessage(Guid channelId, int frequencyKhz)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
}

public class ChannelPanUpdateMessage(Guid channelId, int frequencyKhz, int pan)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public int Pan { get; } = pan;
}

public class ChannelVolumeUpdateMessage(Guid channelId, int frequencyKhz, double volume)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public double Volume { get; } = volume;
}

public class ChannelEncryptionUpdateMessage(
    Guid channelId,
    int frequencyKhz,
    bool enc,
    int encKey,
    bool hqOn,
    bool cryptoCapable)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public bool Enc { get; } = enc;
    public int EncKey { get; } = encKey;
    public bool HqOn { get; } = hqOn;
    public bool CryptoCapable { get; } = cryptoCapable;
}
