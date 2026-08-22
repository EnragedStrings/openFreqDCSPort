using OpenFreq.Common.Satcom;

namespace OpenFreq.Common.Tests.Satcom;

public class SatcomGeodesyTests
{
    [Fact]
    public void SatelliteDirectlyAboveObserverIsNearNinetyDegreesElevation()
    {
        var satPos = SatcomGeodesy.GeoSatellitePosition(0.0, 35_786_000.0);
        var look = SatcomGeodesy.LookAngles(0.0, 0.0, 0.0, satPos);
        Assert.True(look.ElevationDeg > 89.0, $"Expected near-zenith elevation, got {look.ElevationDeg}");
        Assert.True(look.IsAboveHorizon);
    }

    [Fact]
    public void SatelliteOnFarSideOfEarthIsBelowHorizon()
    {
        var satPos = SatcomGeodesy.GeoSatellitePosition(0.0, 35_786_000.0);
        var look = SatcomGeodesy.LookAngles(0.0, 180.0, 0.0, satPos);
        Assert.False(look.IsAboveHorizon);
    }

    [Fact]
    public void SlantRangeIsApproximatelyGeoAltitudeWhenOverhead()
    {
        var satPos = SatcomGeodesy.GeoSatellitePosition(0.0, 35_786_000.0);
        var look = SatcomGeodesy.LookAngles(0.0, 0.0, 0.0, satPos);
        Assert.InRange(look.SlantRangeMeters, 35_700_000.0, 35_800_000.0);
    }

    [Fact]
    public void GeocentricAngleIsZeroAtSubsatellitePoint()
    {
        var angle = SatcomGeodesy.GeocentricAngleFromSubsatellite(45.0, 0.0, 45.0, 0.0);
        Assert.InRange(angle, -0.001, 0.001);
    }

    [Fact]
    public void GeodeticToEcefAndBackRoundTrips()
    {
        var ecef = SatcomGeodesy.GeodeticToEcef(37.5, -122.3, 1200.0);
        var (lat, lon, alt) = SatcomGeodesy.EcefToGeodetic(ecef);
        Assert.InRange(lat, 37.4999, 37.5001);
        Assert.InRange(lon, -122.3001, -122.2999);
        Assert.InRange(alt, 1199.9, 1200.1);
    }

    [Fact]
    public void EllipsoidDoesNotOccludeAnOverheadGeoSatellite()
    {
        var observer = SatcomGeodesy.GeodeticToEcef(0.0, 0.0, 3000.0);
        var satPos = SatcomGeodesy.GeoSatellitePosition(0.0, 35_786_000.0);
        Assert.False(SatcomGeodesy.EllipsoidOccludes(observer, satPos));
    }

    [Fact]
    public void EllipsoidOccludesASatelliteOnTheOppositeSideOfEarth()
    {
        var observer = SatcomGeodesy.GeodeticToEcef(0.0, 0.0, 3000.0);
        var satPos = SatcomGeodesy.GeoSatellitePosition(180.0, 35_786_000.0);
        Assert.True(SatcomGeodesy.EllipsoidOccludes(observer, satPos));
    }
}
