using System;

namespace OpenFreq.Common.Satcom;

/// <summary>
/// TEME (True Equator, Mean Equinox -- the frame SGP4 propagates in) -&gt; ECEF conversion via
/// Greenwich Mean Sidereal Time. PUBLIC STANDARD / SOURCE_DERIVED: the IAU-1982 GMST polynomial is
/// the standard formula reproduced in Vallado, "Fundamentals of Astrodynamics and Applications"
/// and in the original Spacetrack Report #3 / Vallado (2006) "Revisiting Spacetrack Report #3"
/// SGP4 reference paper. Polar motion (the sub-arcsecond wobble between the "pseudo-Earth-fixed"
/// frame this produces and true ITRF/ECEF) is a CALIBRATED_APPROXIMATION omission -- irrelevant at
/// the precision this simulation needs (its effect on look angles is far below one antenna-pattern
/// gain step).
/// </summary>
public static class SatcomOrbitMath
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>Julian date (UT1, approximated by UTC -- their difference is at most ~1 second,
    /// negligible for GMST-driven look angles) for a given UTC <see cref="DateTime"/>.</summary>
    public static double JulianDate(DateTime utc)
    {
        var u = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var y = u.Year;
        var m = u.Month;
        var d = u.Day + (u.Hour + (u.Minute + (u.Second + u.Millisecond / 1000.0) / 60.0) / 60.0) / 24.0;

        if (m <= 2) { y -= 1; m += 12; }
        var a = Math.Floor(y / 100.0);
        var b = 2 - a + Math.Floor(a / 4.0);
        return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + b - 1524.5;
    }

    /// <summary>Greenwich Mean Sidereal Time, degrees [0,360), via the IAU-1982 polynomial in
    /// Julian centuries since J2000.0 (Vallado eq. 3-45). SOURCE_DERIVED (published formula).</summary>
    public static double GmstDegrees(DateTime utc)
    {
        var jd = JulianDate(utc);
        var t = (jd - 2_451_545.0) / 36_525.0;

        // GMST in seconds of time (Vallado / IAU-1982), then converted to degrees.
        var gmstSeconds = 67_310.54841
            + (876_600.0 * 3_600.0 + 8_640_184.812866) * t
            + 0.093104 * t * t
            - 6.2e-6 * t * t * t;

        var gmstDeg = (gmstSeconds % 86_400.0) * (360.0 / 86_400.0);
        gmstDeg %= 360.0;
        if (gmstDeg < 0) gmstDeg += 360.0;
        return gmstDeg;
    }

    /// <summary>Rotates a TEME Cartesian position (meters) into (pseudo-)ECEF by -GMST about the Z
    /// axis -- the standard TEME-&gt;PEF step (Vallado eq. 3-90), which is what every consumer in
    /// this codebase actually needs (look angles, footprint geometry, ellipsoid occlusion all
    /// operate on ECEF).</summary>
    public static Ecef TemeToEcef(Ecef teme, DateTime utc)
    {
        var theta = GmstDegrees(utc) * DegToRad;
        var cosT = Math.Cos(theta);
        var sinT = Math.Sin(theta);

        var x = cosT * teme.X + sinT * teme.Y;
        var y = -sinT * teme.X + cosT * teme.Y;
        return new Ecef(x, y, teme.Z);
    }

    /// <summary>Inverse of <see cref="TemeToEcef"/> -- ECEF back to TEME, provided for symmetry
    /// and testing (not otherwise needed since this project only ever consumes ECEF downstream).</summary>
    public static Ecef EcefToTeme(Ecef ecef, DateTime utc)
    {
        var theta = GmstDegrees(utc) * DegToRad;
        var cosT = Math.Cos(theta);
        var sinT = Math.Sin(theta);

        var x = cosT * ecef.X - sinT * ecef.Y;
        var y = sinT * ecef.X + cosT * ecef.Y;
        return new Ecef(x, y, ecef.Z);
    }
}
