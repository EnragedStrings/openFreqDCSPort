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
using OpenFreq.Client.Services.Satcom;
using OpenFreq.Common.Satcom;
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
    [NotifyPropertyChangedFor(nameof(FrequencyDisplayText))]
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

    /// <summary>Upper bound for a tunable frequency in MHz. Raised above the UHF military band
    /// ceiling (400 MHz) specifically so a manually-created GCI channel can be pointed at one of
    /// ChannelCardListViewModel.GetSatcomVirtualFrequencyKhz's six synthetic frequencies (around
    /// 999.001-999.006 MHz) -- for manual two-client SATCOM testing without a second DCS instance.
    /// Superseded in practice by <see cref="IsManualSatcomMode"/> (which sets the frequency for
    /// you), but kept as the outer bound since that mode just writes into FrequencyKhz like any
    /// other path. See docs/SATCOM_SIMULATION.md.</summary>
    private const double MaxFrequencyMhz = 1000d;

    /// <summary>Manually-created (GCI/stationary) channel set to SATCOM mode instead of a real
    /// dial frequency -- the client-side equivalent of a DCS ARC-210 whose cockpit controls are in
    /// the SATCOM configuration, for testing/using SATCOM without a DCS instance driving this
    /// channel. Only meaningful for editable channels; DCS-synced channels get this from
    /// SatcomAcquisitionState instead (see FrequencyDisplayText/SatcomSubtitleText below, which
    /// both treat the two as equivalent).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrequencyDisplayText), nameof(SatcomSubtitleText), nameof(HasSatcomSubtitleText))]
    public partial bool IsManualSatcomMode { get; set; }

    /// <summary>Which of the six virtual SATCOM channels/nets (see DcsRadioState.SatcomChannel and
    /// OpenFreqDCS.lua's argument-561 tracking) this manual channel is on. Only takes effect while
    /// <see cref="IsManualSatcomMode"/> is on. Advance with NextSatcomChannel/PreviousSatcomChannel
    /// below rather than setting directly, so it stays wrapped to 1-6 the same way the real
    /// cockpit pushbutton does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SatcomSubtitleText), nameof(HasSatcomSubtitleText))]
    public partial int ManualSatcomChannel { get; set; } = 1;

    /// <summary>The real (non-SATCOM) frequency this channel was tuned to before SATCOM mode was
    /// turned on, restored when it's turned back off.</summary>
    private int _preManualSatcomFrequencyKhz;

    partial void OnIsManualSatcomModeChanged(bool value)
    {
        if (value)
        {
            _preManualSatcomFrequencyKhz = FrequencyKhz;
            FrequencyKhz = ChannelCardListViewModel.GetSatcomVirtualFrequencyKhz(ManualSatcomChannel);
        }
        else
        {
            FrequencyKhz = _preManualSatcomFrequencyKhz;
        }
    }

    partial void OnManualSatcomChannelChanged(int value)
    {
        if (IsManualSatcomMode)
            FrequencyKhz = ChannelCardListViewModel.GetSatcomVirtualFrequencyKhz(value);
    }

    [RelayCommand]
    private void NextSatcomChannel() => ManualSatcomChannel = ManualSatcomChannel % 6 + 1;

    [RelayCommand]
    private void PreviousSatcomChannel() => ManualSatcomChannel = (ManualSatcomChannel + 4) % 6 + 1;

    /// <summary>What the read-only frequency readout should show. DCS OBSERVED BEHAVIOR: the
    /// ARC-210's cockpit dial (and therefore DCS's export) keeps showing its last-tuned
    /// frequency (typically ~133.000 MHz, wherever PRST last parked it) even once SATCOM is
    /// selected -- there's no real "SATCOM channel" for DCS to report. Shown for as long as the
    /// cockpit controls are in the SATCOM configuration (during acquisition and once ready
    /// alike), not just once SatcomAcquisitionState reaches Ready, since the frequency is
    /// already misleading the moment the switches move. IsManualSatcomMode (a manually-created
    /// channel's own SATCOM toggle) shows the same text for the same reason.</summary>
    public string FrequencyDisplayText =>
        IsManualSatcomMode || SatcomAcquisitionState != SatcomState.Normal ? "SATCOM VOICE" : $"{FrequencyMhzString} MHz";

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

    /// <summary>Whether this is the currently-selected radio -- the target of the global PTT
    /// keybind (see LocationViewModel.SelectedChannel/SelectChannel). Set by clicking the card;
    /// distinct from transmitting.</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

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

        WeakReferenceMessenger.Default.Send(new ChannelPttHotkeyUpdateMessage(Id, newValue));
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

    /// <summary>Makes this the target of the global PTT keybind (see
    /// LocationViewModel.SelectedChannel). Called when the card itself is clicked; does not
    /// transmit -- use the card's dedicated PTT button or this radio's own PTT hotkey for that.</summary>
    public void Select() => _parentLocationViewModel.SelectChannel(this);

    /// <summary>ARC-210 SATCOM acquisition state (see SatcomAcquisitionStateMachine). Normal for
    /// every channel except the ARC-210 card while its cockpit controls are in/near the SATCOM
    /// configuration. Set from DCS sync -- see ChannelCardListViewModel.SyncDcsChannelOnUiThread.
    /// Named "AcquisitionState" (not "SatcomState") to avoid colliding with the SatcomState enum
    /// type of the same simple name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSatcomAcquiring), nameof(IsSatcomActive), nameof(SatcomStatusText),
        nameof(FrequencyDisplayText), nameof(HasSatcomLinkStatusText), nameof(HasDamaStatusText),
        nameof(SatcomSubtitleText), nameof(HasSatcomSubtitleText))]
    public partial SatcomState SatcomAcquisitionState { get; set; } = SatcomState.Normal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SatcomStatusText), nameof(SatcomSubtitleText), nameof(HasSatcomSubtitleText))]
    public partial double SatcomAcquisitionElapsedSeconds { get; set; }

    public bool IsSatcomAcquiring => SatcomAcquisitionState == SatcomState.Acquiring;
    public bool IsSatcomActive => SatcomAcquisitionState == SatcomState.Ready;

    public string? SatcomStatusText => SatcomAcquisitionState switch
    {
        SatcomState.Acquiring =>
            $"SATCOM ACQ {SatcomAcquisitionElapsedSeconds:F1}/{SatcomAcquisitionStateMachine.AcquisitionSeconds:F0}",
        SatcomState.Ready => "SATCOM",
        _ => null
    };

    /// <summary>DAMA network-access state (see SatcomDamaStateMachine) for the ARC-210 card,
    /// Offline for every other channel. Set from DCS sync alongside SatcomAcquisitionState.
    /// Informational/debug only -- does not itself gate PTT (SatcomAcquisitionState +
    /// CanStartTransmit above already do), so a slow/denied DAMA request never blocks
    /// transmission, only reflects what would realistically be happening on the network layer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DamaStatusText), nameof(HasDamaStatusText), nameof(SatcomSubtitleText),
        nameof(HasSatcomSubtitleText))]
    public partial DamaState DamaState { get; set; } = DamaState.Offline;

    public string? DamaStatusText => DamaState switch
    {
        DamaState.Offline => null,
        DamaState.Searching => "DAMA SEARCHING",
        DamaState.Synchronizing => "DAMA SYNC",
        DamaState.Ready => "DAMA RDY",
        DamaState.Requesting => "DAMA REQ",
        DamaState.Assigned => "DAMA ASSIGNED",
        DamaState.Tx => "DAMA TX",
        DamaState.Rx => "DAMA RX",
        DamaState.ServiceDenied => "DAMA DENIED",
        DamaState.LostSync => "DAMA LOST SYNC",
        _ => null
    };

    /// <summary>Whether DamaStatusText actually has content right now -- bind badge visibility to
    /// this, NOT to IsSatcomActive alone, otherwise the badge renders empty (just its background)
    /// for the window between reaching Ready and the server's first DamaState update arriving.</summary>
    public bool HasDamaStatusText => !string.IsNullOrEmpty(DamaStatusText);

    /// <summary>Everything below is server-authoritative SATCOM link state (see
    /// ChannelCardListViewModel's SatcomLinkStateMessage handling and
    /// docs/SATCOM_SIMULATION.md) -- this card only ever displays what the server pushed down,
    /// never computes or lets the user override it (no client-side quality/BER/FEC/satellite
    /// controls).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SatcomLinkStatusText), nameof(HasSatcomLinkStatusText),
        nameof(SatcomSubtitleText), nameof(HasSatcomSubtitleText))]
    public partial string SatcomSatelliteName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SatcomLinkStatusText), nameof(HasSatcomLinkStatusText),
        nameof(SatcomSubtitleText), nameof(HasSatcomSubtitleText))]
    public partial SatcomLinkQualityState SatcomQualityState { get; set; } = SatcomLinkQualityState.Lost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SatcomLinkStatusText), nameof(HasSatcomLinkStatusText),
        nameof(SatcomSubtitleText), nameof(HasSatcomSubtitleText))]
    public partial SatcomAcquisitionFailureReason SatcomFailureReason { get; set; } = SatcomAcquisitionFailureReason.None;

    /// <summary>Debug-only telemetry bundle (only populated when the server has granted this
    /// session debug access -- see MainWindowViewModel.DebugMode); never shown as a normal-user
    /// control.</summary>
    [ObservableProperty] public partial string SatcomDebugText { get; set; } = "";

    public string? SatcomLinkStatusText
    {
        get
        {
            if (!IsSatcomActive) return null;
            if (SatcomFailureReason != SatcomAcquisitionFailureReason.None)
                return $"SATCOM: {SatcomFailureReason}";
            if (string.IsNullOrEmpty(SatcomSatelliteName)) return null;
            return $"SATCOM: {SatcomSatelliteName} ({SatcomQualityState})";
        }
    }

    /// <summary>Whether SatcomLinkStatusText actually has content right now -- bind badge
    /// visibility to this, NOT to IsSatcomActive alone, otherwise the badge renders empty (just
    /// its background) for the window between reaching Ready and the server's first
    /// SatcomLinkStateMessage arriving.</summary>
    public bool HasSatcomLinkStatusText => !string.IsNullOrEmpty(SatcomLinkStatusText);

    /// <summary>Single combined SATCOM status line shown as a subtitle directly under
    /// FrequencyDisplayText ("SATCOM VOICE") -- acquisition countdown while logging in, then the
    /// server-reported link/satellite status and DAMA state once logged in, instead of separate
    /// badges elsewhere on the card.</summary>
    public string? SatcomSubtitleText
    {
        get
        {
            if (IsManualSatcomMode) return $"SATCOM CH {ManualSatcomChannel}";
            if (SatcomAcquisitionState == SatcomState.Normal) return null;
            if (IsSatcomAcquiring) return SatcomStatusText;

            var parts = new List<string>();
            if (HasSatcomLinkStatusText) parts.Add(SatcomLinkStatusText!);
            if (HasDamaStatusText) parts.Add(DamaStatusText!);
            return parts.Count > 0 ? string.Join("  •  ", parts) : "SATCOM";
        }
    }

    public bool HasSatcomSubtitleText => !string.IsNullOrEmpty(SatcomSubtitleText);

    /// <summary>Whether pressing PTT on this channel right now should actually start a
    /// transmission. Centralizes every guard (disconnected, DCS guard-monitor pseudo-channel,
    /// BMS 3D mode, and SATCOM still acquiring) in one place so it's enforced identically
    /// whether PTT comes from this card's own button/hotkey (StartTransmission below) or from
    /// the global PTT keybind resolving to this channel as the selected one
    /// (LocationViewModel.OnHotkeyPressed).</summary>
    public bool CanStartTransmit => GetTransmitBlockReason() == null;

    /// <summary>Same guards as <see cref="CanStartTransmit"/>, but reports WHICH one is blocking
    /// (null = not blocked) -- used to give visible feedback (see StartTransmission below) instead
    /// of PTT silently doing nothing, which was previously indistinguishable from a hotkey/button
    /// that simply never fired at all.</summary>
    private string? GetTransmitBlockReason()
    {
        if (ConnectionStatus == Channel.ChannelConnectionStatus.Disconnected)
            return $"channel is disconnected (status={ConnectionStatus})";
        if (IsEditing)
            return "channel card is in edit mode";
        if (IsSatcomAcquiring)
            return $"SATCOM still acquiring ({SatcomAcquisitionElapsedSeconds:F1}/{SatcomAcquisitionStateMachine.AcquisitionSeconds:F0}s)";
        if (RadioStationData.Type == RadioStationData.RadioStationType.DCS &&
            DcsRadioId?.Contains(":guard", StringComparison.OrdinalIgnoreCase) == true)
            return "this is a guard-monitor pseudo-channel (no PTT)";
        if (RadioStationData.Type == RadioStationData.RadioStationType.BMS &&
            Settings is { ModeIsGci: false, Is3dMode: true })
            return "BMS 3D mode -- use the in-cockpit comms switch instead";
        return null;
    }

    public void StartTransmission()
    {
        var blockReason = GetTransmitBlockReason();
        if (blockReason != null)
        {
            WeakReferenceMessenger.Default.Send(new TransmitBlockedMessage(Id, Name ?? DcsRadioId ?? "?", blockReason));
            return;
        }

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

/// <summary>PTT was pressed but ChannelCardViewModel.CanStartTransmit blocked it -- logged by
/// ChannelCardListViewModel so a blocked PTT is visible (terminal/log file) instead of silently
/// doing nothing, which is otherwise indistinguishable from a hotkey/button that never fired.</summary>
public class TransmitBlockedMessage(Guid channelId, string channelName, string reason)
{
    public Guid ChannelId { get; } = channelId;
    public string ChannelName { get; } = channelName;
    public string Reason { get; } = reason;
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

public class ChannelPttHotkeyUpdateMessage(Guid channelId, HotkeyBinding? hotkey)
{
    public Guid ChannelId { get; } = channelId;
    public HotkeyBinding? Hotkey { get; } = hotkey;
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
