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

/// <summary>Local loopback request to this client's own DCS instance, checking LOS between two
/// arbitrary geodetic points -- unlike DcsLosRequestPacket (always anchored on this aircraft's own
/// position), neither point here has to be this client's own aircraft. Answers a server-initiated
/// DcsLosOracleRequestMessage (see SignalingMessages.cs); see OpenFreqDCS.lua's
/// buildRemoteLosResponse for the DCS-side handling (coord.LLtoLO conversion + terrain.isVisible).
/// </summary>
public class DcsLosRemoteRequestPacket
{
    public string Schema { get; set; } = "openfreq.dcs.los.remote_request";
    public int Version { get; set; } = 1;
    public string RequestId { get; set; } = string.Empty;
    public int ClientPort { get; set; }
    public DcsGeoPoint From { get; set; } = new();
    public DcsGeoPoint To { get; set; } = new();
}

public class DcsGeoPoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double Alt { get; set; }
}

public class DcsLosRemoteResponsePacket
{
    public string Schema { get; set; } = string.Empty;
    public int Version { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public bool TerrainAvailable { get; set; }
    public bool Visible { get; set; }
    public double Loss { get; set; }
}

/// <summary>Local loopback request converting geodetic points to this map's DCS-local X/Y/Z
/// (coord.LLtoLO) -- used for jammers placed by lat/lon in the server config. See OpenFreqDCS.lua's
/// buildCoordResponse.</summary>
public class DcsCoordRequestPacket
{
    public string Schema { get; set; } = "openfreq.dcs.coord.request";
    public int Version { get; set; } = 1;
    public string RequestId { get; set; } = string.Empty;
    public int ClientPort { get; set; }
    public List<DcsCoordPoint> Points { get; set; } = [];
}

/// <summary>One point to convert. <see cref="Alt"/> null places it <see cref="Agl"/> metres above the
/// terrain.</summary>
public class DcsCoordPoint
{
    public string Id { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double? Alt { get; set; }
    public double? Agl { get; set; }
}

public class DcsCoordResponsePacket
{
    public string Schema { get; set; } = string.Empty;
    public int Version { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public bool Available { get; set; }
    public List<DcsCoordResult> Points { get; set; } = [];
}

public class DcsCoordResult
{
    public string Id { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
