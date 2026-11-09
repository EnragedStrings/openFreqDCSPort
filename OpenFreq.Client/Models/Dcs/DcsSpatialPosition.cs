namespace OpenFreq.Client.Models.Dcs;

/// <summary>163rd vFS 3DAS: helmet-referenced direction of a radio (static rendering).</summary>
public class DcsSpatialPosition
{
    /// <summary>Degrees: 0 = straight ahead, +90 = right ear, -90 = left ear.</summary>
    public double AzimuthDeg { get; set; }

    /// <summary>Degrees: + = up.</summary>
    public double ElevationDeg { get; set; }
}
