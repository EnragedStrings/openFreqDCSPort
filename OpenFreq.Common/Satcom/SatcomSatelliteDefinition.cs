namespace OpenFreq.Common.Satcom;

/// <summary>
/// Admin-configured satellite catalog entry. Position is NOT stored here -- it's either static
/// (StaticGeo mode) or computed each tick by SGP4 propagation (LiveTle mode, server-side only);
/// see SatcomSatellitePosition for the resolved runtime position both sides actually consume.
/// SIMULATION APPROXIMATION for footprint shape (real UHF MILSATCOM transponder coverage patterns
/// aren't publicly catalogued at this fidelity); ephemeris source and NORAD ID (when LiveTle) are
/// PROJECT/ADMIN config, not invented by this project -- ships with a StaticGeo default catalog so
/// the feature works without any admin setup, and never claims a specific real military satellite
/// currently carries a specific operational channel (see docs/SATCOM_SIMULATION.md).
/// </summary>
public sealed class SatcomSatelliteDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    public SatcomEphemerisMode EphemerisMode { get; init; } = SatcomEphemerisMode.StaticGeo;

    /// <summary>StaticGeo mode only: sub-satellite longitude, degrees (-180..180, east positive).</summary>
    public double StaticLongitudeDeg { get; init; }

    /// <summary>StaticGeo mode only: altitude above the WGS84 ellipsoid, meters. Standard GEO
    /// altitude unless overridden.</summary>
    public double StaticAltitudeMeters { get; init; } = 35_786_000.0;

    /// <summary>LiveTle mode only: NORAD catalog id to fetch from CelesTrak. Admin must supply a
    /// real, currently-valid id -- this project ships no default here and does not assert any
    /// particular id is correct (verify against current CelesTrak data before use).</summary>
    public int? NoradId { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>0..1 -- coarse stand-in for "this transponder/satellite is having a bad day"
    /// (station-keeping, eclipse, maintenance). CALIBRATED_APPROXIMATION, multiplies into the
    /// satellite EIRP adjustment used by the link budget.</summary>
    public double Health { get; init; } = 1.0;

    /// <summary>3 dB half-power footprint radius, degrees of geocentric angle from boresight.
    /// CALIBRATED_APPROXIMATION.</summary>
    public double FootprintHalfPowerDeg { get; init; } = 9.0;

    /// <summary>Geocentric angle beyond which gain is effectively zero. CALIBRATED_APPROXIMATION.</summary>
    public double FootprintCutoffDeg { get; init; } = 17.0;
}

/// <summary>Resolved runtime position for one satellite at one moment -- what both the server's
/// link engine and the client's display/debug panel actually consume, regardless of whether it
/// came from a static table lookup or an SGP4 propagation step.</summary>
public readonly record struct SatcomSatellitePosition(
    string SatelliteId, double LatitudeDeg, double LongitudeDeg, double AltitudeMeters,
    long ComputedAtUnixMs, bool IsStale)
{
    public Ecef ToEcef() => SatcomGeodesy.GeodeticToEcef(LatitudeDeg, LongitudeDeg, AltitudeMeters);
}
