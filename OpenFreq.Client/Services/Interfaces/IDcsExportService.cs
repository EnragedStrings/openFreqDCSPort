using System;
using System.Collections.Generic;
using OpenFreq.Client.Models.Dcs;
using FalconBmsDataService.Models;
using FalconRadioService.Models;

namespace OpenFreq.Client.Services.Interfaces;

public interface IDcsExportService : IDisposable, ILifecycleService
{
    public const int DefaultUdpPort = 34321;
    public const int DefaultLosRequestPort = 34322;
    public const int RadioOffFrequencyKhz = 0;

    ServiceState State { get; }
    int UdpPort { get; set; }
    int LosRequestPort { get; set; }
    DateTime? LastPacketUtc { get; }
    string Theater { get; }
    string Unit { get; }
    string UnitName { get; }
    string PlayerName { get; }
    bool IsInGame { get; }
    DcsVector3? Position { get; }
    DcsVector3? Velocity { get; }
    double? HeadingRadians { get; }
    DcsHeightmapInfo? HeightmapInfo { get; }

    DcsRadioState? GetRadio(DcsRadioSlot slot);
    IReadOnlyList<DcsRadioState> GetRadios();
    DcsLineOfSightResult? RequestLineOfSight(string key, DcsVector3 remotePosition);

    event EventHandler<ServiceStateChangedEventArgs>? StateChanged;
    event EventHandler<DcsRadioChangedEventArgs>? RadioChanged;
    event EventHandler<DcsPttChangedEventArgs>? PttChanged;
    event EventHandler<DcsGameModeChangedEventArgs>? GameModeChanged;
    event EventHandler<DcsAircraftChangedEventArgs>? AircraftChanged;
    event EventHandler<DcsHeightmapChangedEventArgs>? HeightmapChanged;
}

public class DcsRadioChangedEventArgs(DcsRadioState? oldRadio, DcsRadioState newRadio) : EventArgs
{
    public DcsRadioState? OldRadio { get; } = oldRadio;
    public DcsRadioState NewRadio { get; } = newRadio;
}

public class DcsPttChangedEventArgs(DcsRadioState radio, bool oldPtt, bool newPtt) : EventArgs
{
    public DcsRadioState Radio { get; } = radio;
    public bool OldPtt { get; } = oldPtt;
    public bool NewPtt { get; } = newPtt;
}

public class DcsGameModeChangedEventArgs(bool oldIsInGame, bool newIsInGame) : EventArgs
{
    public bool OldIsInGame { get; } = oldIsInGame;
    public bool NewIsInGame { get; } = newIsInGame;
}

public class DcsAircraftChangedEventArgs(string oldUnit, string newUnit, string oldUnitName, string newUnitName) : EventArgs
{
    public string OldUnit { get; } = oldUnit;
    public string NewUnit { get; } = newUnit;
    public string OldUnitName { get; } = oldUnitName;
    public string NewUnitName { get; } = newUnitName;
}

public class DcsHeightmapChangedEventArgs(DcsHeightmapInfo heightmapInfo) : EventArgs
{
    public DcsHeightmapInfo HeightmapInfo { get; } = heightmapInfo;
}
