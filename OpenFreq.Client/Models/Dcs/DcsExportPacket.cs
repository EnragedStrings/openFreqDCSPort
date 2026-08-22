using System.Collections.Generic;

namespace OpenFreq.Client.Models.Dcs;

public class DcsExportPacket
{
    public string Schema { get; set; } = string.Empty;
    public int Version { get; set; }
    public double ModelTime { get; set; }
    public string Theater { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string UnitName { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public bool IsInAircraft { get; set; }
    public bool IsInGame { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AltitudeMsl { get; set; }
    public double? HeadingRadians { get; set; }
    public double? PitchRadians { get; set; }
    public double? BankRadians { get; set; }
    public DcsVector3? Position { get; set; }
    public DcsVector3? Velocity { get; set; }
    public List<DcsRadioState> Radios { get; set; } = [];
    public DcsHeightmapInfo? Heightmap { get; set; }
}
