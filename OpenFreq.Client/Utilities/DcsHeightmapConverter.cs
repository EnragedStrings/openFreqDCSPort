using OpenFreq.Client.Models.Dcs;

namespace OpenFreq.Utilities;

public static class DcsHeightmapConverter
{
    public static (double x, double y, double altitudeMeters) ToHeightmap(DcsVector3 position,
        DcsHeightmapInfo? heightmapInfo)
    {
        var x = position.X;
        var y = position.Z;

        if (heightmapInfo != null)
        {
            x -= heightmapInfo.OriginX;
            y -= heightmapInfo.OriginZ;
        }

        return (x, y, position.Y);
    }

    public static (double x, double y, double z) VelocityToHeightmap(DcsVector3 velocity)
    {
        return (velocity.X, velocity.Z, velocity.Y);
    }
}
