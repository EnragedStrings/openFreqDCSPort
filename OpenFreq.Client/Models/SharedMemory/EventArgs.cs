using System;
using FalconBmsDataService.Models;

namespace FalconRadioService.Models;

public class ServiceStateChangedEventArgs : EventArgs
{
    public ServiceState OldState { get; }
    public ServiceState NewState { get; }
    public DateTime Timestamp { get; }

    public ServiceStateChangedEventArgs(ServiceState oldState, ServiceState newState)
    {
        OldState = oldState;
        NewState = newState;
        Timestamp = DateTime.UtcNow;
    }
}

public class RadioFrequencyChangedEventArgs : EventArgs
{
    public RadioType RadioType { get; }
    public int OldFrequency { get; }
    public int NewFrequency { get; }

    public RadioFrequencyChangedEventArgs(RadioType radioType, int oldFreq, int newFreq)
    {
        RadioType = radioType;
        OldFrequency = oldFreq;
        NewFrequency = newFreq;
    }
}

public class RadioVolumeChangedEventArgs : EventArgs
{
    public RadioType RadioType { get; }
    public int OldVolume { get; }
    public int NewVolume { get; }

    public RadioVolumeChangedEventArgs(RadioType radioType, int oldVol, int newVol)
    {
        RadioType = radioType;
        OldVolume = oldVol;
        NewVolume = newVol;
    }
}

public class RadioPttChangedEventArgs : EventArgs
{
    public RadioType RadioType { get; }
    public bool OldPtt { get; }
    public bool NewPtt { get; }

    public RadioPttChangedEventArgs(RadioType radioType, bool oldPtt, bool newPtt)
    {
        RadioType = radioType;
        OldPtt = oldPtt;
        NewPtt = newPtt;
    }
}

public class RadioPowerChangedEventArgs : EventArgs
{
    public RadioType RadioType { get; }
    public bool OldPower { get; }
    public bool NewPower { get; }

    public RadioPowerChangedEventArgs(RadioType radioType, bool oldPower, bool newPower)
    {
        RadioType = radioType;
        OldPower = oldPower;
        NewPower = newPower;
    }
}

public class ConnectionParametersChangedEventArgs : EventArgs
{
    public ConnectionParameters OldParameters { get; }
    public ConnectionParameters NewParameters { get; }

    public ConnectionParametersChangedEventArgs(ConnectionParameters oldParams, ConnectionParameters newParams)
    {
        OldParameters = oldParams;
        NewParameters = newParams;
    }
}