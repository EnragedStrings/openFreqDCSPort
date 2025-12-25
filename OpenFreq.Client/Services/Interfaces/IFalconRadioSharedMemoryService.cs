using System;
using FalconBmsDataService.Models;
using FalconRadioService.Models;

namespace FalconRadioService.Services;

public interface IFalconRadioSharedMemoryService : IDisposable
{
    // Service state
    ServiceState State { get; }
    double PollingFrequencyHz { get; set; }

    // Current data (thread-safe)
    string? LogbookName { get; }
    RadioChannel? GetRadioChannel(RadioType radioType);
    RadioDevice? GetRadioDevice(RadioDeviceType deviceType);
    ConnectionParameters? ConnectionParameters { get; }

    // Client status management
    ClientStatusFlags GetClientStatus();
    void SetClientStatus(ClientStatusFlags flags);
    void AddClientStatus(ClientStatusFlags flags);
    void RemoveClientStatus(ClientStatusFlags flags);

    // State change events
    event EventHandler<ServiceStateChangedEventArgs>? StateChanged;

    // Radio change events (fired only on subsequent changes)
    event EventHandler<RadioFrequencyChangedEventArgs>? FrequencyChanged;
    event EventHandler<RadioVolumeChangedEventArgs>? VolumeChanged;
    event EventHandler<RadioPttChangedEventArgs>? PttChanged;
    event EventHandler<RadioPowerChangedEventArgs>? PowerChanged;

    // Connection parameter changes
    event EventHandler<ConnectionParametersChangedEventArgs>? ConnectionParametersChanged;

    // Service control
    void Start();
    void Stop();
}