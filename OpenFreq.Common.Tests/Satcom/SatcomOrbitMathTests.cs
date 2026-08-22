using System;
using OpenFreq.Common.Satcom;

namespace OpenFreq.Common.Tests.Satcom;

public class SatcomOrbitMathTests
{
    [Fact]
    public void GmstAdvancesAtApproximatelySiderealRatePerHour()
    {
        var t0 = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddHours(1);
        var g0 = SatcomOrbitMath.GmstDegrees(t0);
        var g1 = SatcomOrbitMath.GmstDegrees(t1);
        var delta = g1 - g0;
        if (delta < 0) delta += 360.0;
        // Sidereal rate ~360.9856 deg/day = ~15.0411 deg/hour (SOURCE_EXACT constant baked into
        // the GMST polynomial), not the solar 15.0 deg/hour.
        Assert.InRange(delta, 15.00, 15.08);
    }

    [Fact]
    public void GmstStaysInZeroToThreeSixtyRange()
    {
        for (var h = 0; h < 48; h++)
        {
            var g = SatcomOrbitMath.GmstDegrees(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(h));
            Assert.InRange(g, 0.0, 360.0);
        }
    }

    [Fact]
    public void TemeToEcefAndBackRoundTrips()
    {
        var utc = new DateTime(2025, 3, 15, 8, 30, 0, DateTimeKind.Utc);
        var teme = new Ecef(7_000_000.0, 1_500_000.0, 2_300_000.0);
        var ecef = SatcomOrbitMath.TemeToEcef(teme, utc);
        var back = SatcomOrbitMath.EcefToTeme(ecef, utc);

        Assert.InRange(back.X - teme.X, -0.01, 0.01);
        Assert.InRange(back.Y - teme.Y, -0.01, 0.01);
        Assert.InRange(back.Z - teme.Z, -0.01, 0.01);
    }

    [Fact]
    public void TemeToEcefRotatesXAxisByNegativeGmst()
    {
        // A TEME vector along its own X axis (pointing at the vernal equinox direction) must land,
        // in ECEF, at longitude = -GMST(t) mod 360 -- this is the defining property of the
        // TEME->PEF rotation and catches a sign-flip in the rotation matrix that a round-trip test
        // alone would not (round-tripping the same rotation forward then back cancels a sign error).
        var utc = new DateTime(2025, 3, 15, 8, 30, 0, DateTimeKind.Utc);
        var teme = new Ecef(7_000_000.0, 0.0, 0.0);
        var ecef = SatcomOrbitMath.TemeToEcef(teme, utc);

        var lonDeg = Math.Atan2(ecef.Y, ecef.X) * (180.0 / Math.PI);
        if (lonDeg < 0) lonDeg += 360.0;

        var expectedLonDeg = (360.0 - SatcomOrbitMath.GmstDegrees(utc)) % 360.0;

        var diff = Math.Abs(lonDeg - expectedLonDeg);
        diff = Math.Min(diff, 360.0 - diff);
        Assert.True(diff < 0.001, $"Expected longitude {expectedLonDeg}, got {lonDeg}");
    }

    [Fact]
    public void TemeToEcefPreservesZComponent()
    {
        // The TEME->PEF rotation is about the Z axis only -- Z must be untouched.
        var utc = new DateTime(2025, 3, 15, 8, 30, 0, DateTimeKind.Utc);
        var teme = new Ecef(1_000_000.0, 2_000_000.0, 6_500_000.0);
        var ecef = SatcomOrbitMath.TemeToEcef(teme, utc);
        Assert.Equal(teme.Z, ecef.Z, 6);
    }
}
