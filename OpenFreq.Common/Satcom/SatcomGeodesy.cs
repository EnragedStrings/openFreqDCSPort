using System;

namespace OpenFreq.Common.Satcom;

/// <summary>ECEF (Earth-Centered, Earth-Fixed) Cartesian coordinates in meters.</summary>
public readonly record struct Ecef(double X, double Y, double Z)
{
    public static Ecef operator -(Ecef a, Ecef b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);
}

/// <summary>Satellite look angles and range from a ground/airborne terminal.</summary>
public readonly record struct SatelliteLookAngles(double AzimuthDeg, double ElevationDeg, double SlantRangeMeters)
{
    /// <summary>True when the satellite is above the local geometric horizon (elevation &gt; 0).
    /// PUBLIC STANDARD: standard satellite-visibility criterion; real terminals typically also
    /// require a small positive minimum elevation (mask angle) for a usable link, applied
    /// separately by the caller (see SatcomLinkBudget), not baked into this raw geometric test.</summary>
    public bool IsAboveHorizon => ElevationDeg > 0.0;
}

/// <summary>
/// WGS84 geodetic &lt;-&gt; ECEF conversion and topocentric (local horizon) look-angle computation.
/// Standard textbook geodesy (e.g. Vallado, "Fundamentals of Astrodynamics and Applications") --
/// PUBLIC STANDARD math, not SATCOM-specific. Shared by client (display/debug) and server
/// (authoritative link computation) so there is exactly one implementation of this physics.
/// </summary>
public static class SatcomGeodesy
{
    public const double WgsA = 6_378_137.0; // semi-major axis, meters. SOURCE_EXACT (WGS84 defining parameter)
    public const double WgsF = 1.0 / 298.257223563; // flattening. SOURCE_EXACT (WGS84 defining parameter)
    public const double WgsE2 = WgsF * (2.0 - WgsF); // first eccentricity squared. PHYSICAL_CALCULATION
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    public static Ecef GeodeticToEcef(double latDeg, double lonDeg, double altMeters)
    {
        var lat = latDeg * DegToRad;
        var lon = lonDeg * DegToRad;
        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);
        var n = WgsA / Math.Sqrt(1.0 - WgsE2 * sinLat * sinLat);

        var x = (n + altMeters) * cosLat * Math.Cos(lon);
        var y = (n + altMeters) * cosLat * Math.Sin(lon);
        var z = (n * (1.0 - WgsE2) + altMeters) * sinLat;
        return new Ecef(x, y, z);
    }

    /// <summary>Inverse of <see cref="GeodeticToEcef"/> via Bowring's method (closed-form,
    /// converges to sub-millimeter accuracy in one iteration for terrestrial/near-Earth
    /// altitudes). PUBLIC STANDARD (Bowring 1976), used to turn SGP4's ECEF satellite position
    /// back into lat/lon/alt for display and for the flat "StaticGeo" catalog's own consistency.</summary>
    public static (double LatDeg, double LonDeg, double AltMeters) EcefToGeodetic(Ecef p)
    {
        var lon = Math.Atan2(p.Y, p.X);
        var pRadius = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        if (pRadius < 1e-6)
        {
            // Degenerate: on the polar axis.
            var altPole = Math.Abs(p.Z) - WgsA * (1.0 - WgsF);
            return (p.Z >= 0 ? 90.0 : -90.0, 0.0, altPole);
        }

        var wgsB = WgsA * (1.0 - WgsF);
        var ep2 = (WgsA * WgsA - wgsB * wgsB) / (wgsB * wgsB);
        var theta = Math.Atan2(p.Z * WgsA, pRadius * wgsB);
        var sinTheta = Math.Sin(theta);
        var cosTheta = Math.Cos(theta);

        var lat = Math.Atan2(p.Z + ep2 * wgsB * sinTheta * sinTheta * sinTheta,
            pRadius - WgsE2 * WgsA * cosTheta * cosTheta * cosTheta);
        var sinLat = Math.Sin(lat);
        var n = WgsA / Math.Sqrt(1.0 - WgsE2 * sinLat * sinLat);
        var alt = pRadius / Math.Cos(lat) - n;

        return (lat * RadToDeg, lon * RadToDeg, alt);
    }

    /// <summary>GEO satellite position: equatorial (0 inclination), fixed longitude, altitude
    /// measured above the ellipsoid at that longitude/latitude=0. Used for StaticGeo-mode
    /// satellites only -- LiveTle-mode satellites get their ECEF position from SGP4 propagation
    /// (see SatcomOrbitMath) instead.</summary>
    public static Ecef GeoSatellitePosition(double longitudeDeg, double altitudeMeters) =>
        GeodeticToEcef(0.0, longitudeDeg, altitudeMeters);

    /// <summary>
    /// Azimuth/elevation/range of <paramref name="target"/> as seen from <paramref name="observerLatDeg"/>/
    /// <paramref name="observerLonDeg"/>/<paramref name="observerAltMeters"/>, via the standard ECEF -&gt;
    /// local-ENU (East-North-Up) topocentric transform.
    /// </summary>
    public static SatelliteLookAngles LookAngles(double observerLatDeg, double observerLonDeg,
        double observerAltMeters, Ecef target)
    {
        var observer = GeodeticToEcef(observerLatDeg, observerLonDeg, observerAltMeters);
        var delta = target - observer;
        var range = delta.Length();
        if (range < 1.0)
            return new SatelliteLookAngles(0, 90, range); // degenerate: essentially co-located

        var lat = observerLatDeg * DegToRad;
        var lon = observerLonDeg * DegToRad;
        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);
        var sinLon = Math.Sin(lon);
        var cosLon = Math.Cos(lon);

        // ECEF delta -> local ENU (East, North, Up) via the standard rotation matrix.
        var east = -sinLon * delta.X + cosLon * delta.Y;
        var north = -sinLat * cosLon * delta.X - sinLat * sinLon * delta.Y + cosLat * delta.Z;
        var up = cosLat * cosLon * delta.X + cosLat * sinLon * delta.Y + sinLat * delta.Z;

        var azimuthDeg = Math.Atan2(east, north) * RadToDeg;
        if (azimuthDeg < 0) azimuthDeg += 360.0;

        var horizontalRange = Math.Sqrt(east * east + north * north);
        var elevationDeg = Math.Atan2(up, horizontalRange) * RadToDeg;

        return new SatelliteLookAngles(azimuthDeg, elevationDeg, range);
    }

    /// <summary>Great-circle-style geocentric angle (degrees) between a satellite's sub-satellite
    /// point and a ground point, as seen from Earth's center -- used for the satellite's
    /// footprint/antenna pattern (how far off its own boresight this terminal is), which is a
    /// different angle than the terminal's own elevation to the satellite. Works for any
    /// sub-satellite point (not just GEO/equatorial), taking it directly rather than assuming
    /// latitude 0, so it applies unchanged to inclined LiveTle satellites.</summary>
    public static double GeocentricAngleFromSubsatellite(double subSatLatDeg, double subSatLonDeg,
        double observerLatDeg, double observerLonDeg)
    {
        var lat1 = subSatLatDeg * DegToRad;
        var lon1 = subSatLonDeg * DegToRad;
        var lat2 = observerLatDeg * DegToRad;
        var lon2 = observerLonDeg * DegToRad;

        // Spherical law of cosines -- adequate precision for a footprint gain lookup (not used
        // for range/timing, which use the full ECEF geometry above).
        var cosAngle = Math.Sin(lat1) * Math.Sin(lat2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Cos(lon2 - lon1);
        cosAngle = Math.Clamp(cosAngle, -1.0, 1.0);
        return Math.Acos(cosAngle) * RadToDeg;
    }

    /// <summary>
    /// SIMULATION APPROXIMATION: converts a DCS-local flat-tangent-plane offset (meters, the
    /// coordinate system OpenFreqDCS.lua/DcsExportService already use for terrestrial LOS) into
    /// an approximate lat/lon delta, using a local flat-Earth approximation centered on
    /// <paramref name="atLatDeg"/>. Accurate close to the reference latitude/for modest distances
    /// (a DCS theater is typically a few hundred km across); degrades at extreme range or near
    /// the poles (irrelevant for any current DCS map).
    /// </summary>
    public static (double LatDeg, double LonDeg) ApproximateLatLonFromLocalOffset(
        double atLatDeg, double atLonDeg, double deltaEastMeters, double deltaNorthMeters)
    {
        var lat = atLatDeg * DegToRad;
        var metersPerDegLat = (Math.PI / 180.0) * WgsA * (1.0 - WgsE2) /
                               Math.Pow(1.0 - WgsE2 * Math.Sin(lat) * Math.Sin(lat), 1.5);
        var metersPerDegLon = (Math.PI / 180.0) * WgsA * Math.Cos(lat) /
                               Math.Sqrt(1.0 - WgsE2 * Math.Sin(lat) * Math.Sin(lat));

        var lonDeg = atLonDeg + (metersPerDegLon > 1.0 ? deltaEastMeters / metersPerDegLon : 0.0);
        var latDeg = atLatDeg + deltaNorthMeters / metersPerDegLat;
        return (latDeg, lonDeg);
    }

    /// <summary>
    /// Line-segment/ellipsoid intersection: true if the straight segment from <paramref name="a"/>
    /// to <paramref name="b"/> passes through (or under) the WGS84 ellipsoid surface -- i.e. Earth
    /// itself blocks the path. PUBLIC STANDARD (quadratic ray/ellipsoid intersection, scaling to a
    /// unit sphere by the ellipsoid's semi-axes). This is the "Earth-horizon occlusion" check,
    /// distinct from DCS local terrain LOS: it answers "is the satellite geometrically below the
    /// curvature of the Earth from here", not "is there a mountain in the way locally".
    /// </summary>
    public static bool EllipsoidOccludes(Ecef a, Ecef b)
    {
        var wgsB = WgsA * (1.0 - WgsF);
        // Scale so the ellipsoid becomes the unit sphere; intersect the scaled segment with it.
        double sx = 1.0 / WgsA, sy = 1.0 / WgsA, sz = 1.0 / wgsB;
        var ax = a.X * sx; var ay = a.Y * sy; var az = a.Z * sz;
        var bx = b.X * sx; var by = b.Y * sy; var bz = b.Z * sz;
        var dx = bx - ax; var dy = by - ay; var dz = bz - az;

        var qa = dx * dx + dy * dy + dz * dz;
        if (qa < 1e-18) return false;
        var qb = 2.0 * (ax * dx + ay * dy + az * dz);
        var qc = ax * ax + ay * ay + az * az - 1.0;

        var disc = qb * qb - 4.0 * qa * qc;
        if (disc < 0.0) return false; // segment's infinite line never touches the ellipsoid

        var sqrtDisc = Math.Sqrt(disc);
        var t1 = (-qb - sqrtDisc) / (2.0 * qa);
        var t2 = (-qb + sqrtDisc) / (2.0 * qa);

        // Occluded only if an intersection lies strictly within the segment (0,1) -- both
        // endpoints are expected to already be outside/on the ellipsoid (terminal above ground,
        // satellite far above GEO altitude).
        return (t1 > 1e-9 && t1 < 1.0 - 1e-9) || (t2 > 1e-9 && t2 < 1.0 - 1e-9);
    }
}
