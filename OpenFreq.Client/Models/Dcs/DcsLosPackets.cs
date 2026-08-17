using System;
using System.Collections.Generic;

namespace OpenFreq.Client.Models.Dcs;

public class DcsLosRequestPacket
{
    public string Schema { get; set; } = "openfreq.dcs.los.request";
    public int Version { get; set; } = 1;
    public string RequestId { get; set; } = string.Empty;
    public int ClientPort { get; set; }
    public List<DcsLosCheck> Checks { get; set; } = [];
}

public class DcsLosCheck
{
    public string Id { get; set; } = string.Empty;
    public DcsVector3 Position { get; set; } = new();
}

public class DcsLosResponsePacket
{
    public string Schema { get; set; } = string.Empty;
    public int Version { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public bool TerrainAvailable { get; set; }
    public List<DcsLosResult> Results { get; set; } = [];
}

public class DcsLosResult
{
    public string Id { get; set; } = string.Empty;
    public double Loss { get; set; }
    public bool Visible { get; set; }
}

public class DcsLineOfSightResult
{
    public double Loss { get; set; }
    public bool Visible { get; set; }
    public bool TerrainAvailable { get; set; }
    public DateTime ReceivedUtc { get; set; }
}
