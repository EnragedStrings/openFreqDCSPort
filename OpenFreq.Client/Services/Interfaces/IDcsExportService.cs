using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    double? PitchRadians { get; }
    double? BankRadians { get; }
    double Latitude { get; }
    double Longitude { get; }
    double AltitudeMsl { get; }
    DcsHeightmapInfo? HeightmapInfo { get; }

    DcsRadioState? GetRadio(int slot);
    IReadOnlyList<DcsRadioState> GetRadios();
    DcsLineOfSightResult? RequestLineOfSight(string key, DcsVector3 remotePosition);

    /// <summary>Asks this client's own live DCS instance to check terrain LOS between two
    /// arbitrary geodetic points -- see DcsExportService's own doc comment on the implementation.
    /// </summary>
    Task<DcsLosRemoteResponsePacket?> RequestRemoteLineOfSightAsync(double fromLat, double fromLon,
        double fromAlt, double toLat, double toLon, double toAlt, TimeSpan timeout);

    event EventHandler<ServiceStateChangedEventArgs>? StateChanged;
    event EventHandler<DcsRadioChangedEventArgs>? RadioChanged;
    event EventHandler<DcsPttChangedEventArgs>? PttChanged;
    event EventHandler<DcsToneChangedEventArgs>? ToneChanged;
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

public class DcsToneChangedEventArgs(DcsRadioState radio, bool oldToneOn, bool newToneOn) : EventArgs
{
    public DcsRadioState Radio { get; } = radio;
    public bool OldToneOn { get; } = oldToneOn;
    public bool NewToneOn { get; } = newToneOn;
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
