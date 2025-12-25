using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    private string _openFreqServerAddress = string.Empty;

    [ObservableProperty] private string _openFreqPassword = string.Empty;

    [ObservableProperty] private ObservableCollection<string> _playbackDeviceNames = new();
    [ObservableProperty] private ObservableCollection<string> _recordingDeviceNames = new();
    [ObservableProperty] private int _recordingDeviceIndex;
    [ObservableProperty] private int _playbackDeviceIndex;
    


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeIsGci))]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    private OpenFreqSettings.Mode _connectionMode = OpenFreqSettings.Mode.BMS;

    public bool ModeIsGci
    {
        get => ConnectionMode == OpenFreqSettings.Mode.GCI;
        set { ConnectionMode = value ? OpenFreqSettings.Mode.GCI : OpenFreqSettings.Mode.BMS; }
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    private string _tacviewServerAddress = string.Empty;

    [ObservableProperty] private string _tacviewServerPassword = string.Empty;
    [ObservableProperty] private string _heightmapPath = string.Empty;

    [ObservableProperty] private string _inputDeviceName = string.Empty;
    [ObservableProperty] private string _outputDeviceName = string.Empty;
    private readonly IAudioService _audioService;

    public bool IsReadyToConnect => OpenFreqServerAddress != string.Empty &&
                                    (
                                        (ModeIsGci && TacviewServerAddress != string.Empty &&
                                         HeightmapPath != string.Empty)
                                        || !ModeIsGci
                                    );

    partial void OnConnectionModeChanged(OpenFreqSettings.Mode value)
    {
        Debug.WriteLine($"ConnectionMode changed to: {value}");
        Debug.WriteLine($"ConnectionMode ToString: '{value.ToString()}'");
        Debug.WriteLine($"ConnectionMode type: {value.GetType().FullName}");
    }

    public SettingsViewModel(IAudioService audioService)
    {
        _audioService = audioService;
        InitializeAudioDevices();
    }

    private void InitializeAudioDevices()
    {
        _audioService.Init();

        var playbackDevices = _audioService.GetPlaybackDevices();
        PlaybackDeviceNames = new ObservableCollection<string>(playbackDevices);
        PlaybackDeviceIndex = _audioService.DefaultPlaybackDevice;

        var recordingDevices = _audioService.GetRecordingDevices();
        RecordingDeviceNames = new ObservableCollection<string>(recordingDevices);
        RecordingDeviceIndex = _audioService.DefaultRecordingDevice;
    }

    partial void OnRecordingDeviceIndexChanged(int value)
    {
        if (value >= 0 && value < RecordingDeviceNames.Count)
        {
            _inputDeviceName = RecordingDeviceNames[value];
        }
    }

    partial void OnPlaybackDeviceIndexChanged(int value)
    {
        if (value >= 0 && value < PlaybackDeviceNames.Count)
        {
            _outputDeviceName = PlaybackDeviceNames[value];
        }
    }

    public void LoadFromSettings(OpenFreqSettings settings)
    {
        OpenFreqServerAddress = settings.OpenFreqServerAddress;
        OpenFreqPassword = settings.OpenFreqPassword;
        ConnectionMode = settings.ConnectionMode;
        TacviewServerAddress = settings.TacviewServerAddress;
        TacviewServerPassword = settings.TacviewServerPassword;

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
    }

    public OpenFreqSettings GetSettings()
    {
        return new OpenFreqSettings
        {
            OpenFreqServerAddress = OpenFreqServerAddress,
            OpenFreqPassword = OpenFreqPassword,
            ConnectionMode = ConnectionMode,
            TacviewServerAddress = TacviewServerAddress,
            TacviewServerPassword = TacviewServerPassword,
            InputDeviceName = InputDeviceName,
            OutputDeviceName = OutputDeviceName,
            HeightmapPath = HeightmapPath
        };
    }
}