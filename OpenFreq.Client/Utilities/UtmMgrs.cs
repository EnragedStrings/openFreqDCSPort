using System;
using System.Globalization;
using System.Linq;

namespace OpenFreq.Utilities;

/// <summary>One resolved UTM position: zone, hemisphere, and metric easting/northing within it.</summary>
public readonly record struct UtmCoordinate(int Zone, bool IsNorthernHemisphere, double EastingMeters, double NorthingMeters);

/// <summary>
/// WGS84 geodetic &lt;-&gt; UTM (Universal Transverse Mercator) conversion, PUBLIC STANDARD closed-
/// form series formulas (Snyder, "Map Projections: A Working Manual", USGS Professional Paper
/// 1395 -- the standard reference implementation essentially every non-Karney UTM library is based
/// on). Deliberately hand-rolled rather than routed through DotSpatial.Projections (already a
/// dependency, used elsewhere for theater-local projections): DotSpatial ships UTM zone
/// ProjectionInfo definitions but no MGRS grid-square encode/decode of its own, and this keeps the
/// whole coordinate-format feature self-contained and unit-testable the same way the SATCOM
/// geodesy work was. Not valid at the poles (UTM itself isn't defined there; irrelevant for any
/// DCS theater).
/// </summary>
public static class UtmProjection
{
    private const double A = 6378137.0; // WGS84 semi-major axis, meters. SOURCE_EXACT
    private const double F = 1.0 / 298.257223563; // WGS84 flattening. SOURCE_EXACT
    private const double E2 = F * (2.0 - F); // first eccentricity squared
    private const double EP2 = E2 / (1.0 - E2); // second eccentricity squared
    private const double K0 = 0.9996; // UTM scale factor at the central meridian. SOURCE_EXACT
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    public static int ZoneNumber(double lonDeg)
    {
        var lon = CoordinateParser.NormalizeLongitude(lonDeg);
        var zone = (int)Math.Floor((lon + 180.0) / 6.0) + 1;
        return Math.Clamp(zone, 1, 60);
    }

    public static double CentralMeridianDeg(int zone) => (zone - 1) * 6.0 - 180.0 + 3.0;

    public static UtmCoordinate LatLonToUtm(double latDeg, double lonDeg)
    {
        var zone = ZoneNumber(lonDeg);
        var lat = latDeg * DegToRad;
        var lon = CoordinateParser.NormalizeLongitude(lonDeg) * DegToRad;
        var lon0 = CentralMeridianDeg(zone) * DegToRad;

        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);
        var tanLat = Math.Tan(lat);

        var n = A / Math.Sqrt(1.0 - E2 * sinLat * sinLat);
        var t = tanLat * tanLat;
        var c = EP2 * cosLat * cosLat;
        var aTerm = cosLat * (lon - lon0);

        var m = A * ((1 - E2 / 4 - 3 * E2 * E2 / 64 - 5 * E2 * E2 * E2 / 256) * lat
                     - (3 * E2 / 8 + 3 * E2 * E2 / 32 + 45 * E2 * E2 * E2 / 1024) * Math.Sin(2 * lat)
                     + (15 * E2 * E2 / 256 + 45 * E2 * E2 * E2 / 1024) * Math.Sin(4 * lat)
                     - (35 * E2 * E2 * E2 / 3072) * Math.Sin(6 * lat));

        var easting = K0 * n * (aTerm
                                 + (1 - t + c) * Math.Pow(aTerm, 3) / 6
                                 + (5 - 18 * t + t * t + 72 * c - 58 * EP2) * Math.Pow(aTerm, 5) / 120)
                      + 500000.0;

        var northing = K0 * (m + n * tanLat * (aTerm * aTerm / 2
                                                + (5 - t + 9 * c + 4 * c * c) * Math.Pow(aTerm, 4) / 24
                                                + (61 - 58 * t + t * t + 600 * c - 330 * EP2) * Math.Pow(aTerm, 6) / 720));

        var isNorthern = latDeg >= 0.0;
        if (!isNorthern) northing += 10_000_000.0;

        return new UtmCoordinate(zone, isNorthern, easting, northing);
    }

    public static (double LatDeg, double LonDeg) UtmToLatLon(UtmCoordinate utm)
    {
        var e1 = (1 - Math.Sqrt(1 - E2)) / (1 + Math.Sqrt(1 - E2));
        var northing = utm.IsNorthernHemisphere ? utm.NorthingMeters : utm.NorthingMeters - 10_000_000.0;

        var m = northing / K0;
        var mu = m / (A * (1 - E2 / 4 - 3 * E2 * E2 / 64 - 5 * E2 * E2 * E2 / 256));

        var phi1 = mu
                   + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu)
                   + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
                   + (151 * Math.Pow(e1, 3) / 96) * Math.Sin(6 * mu)
                   + (1097 * Math.Pow(e1, 4) / 512) * Math.Sin(8 * mu);

        var sinPhi1 = Math.Sin(phi1);
        var cosPhi1 = Math.Cos(phi1);
        var tanPhi1 = Math.Tan(phi1);

        var n1 = A / Math.Sqrt(1 - E2 * sinPhi1 * sinPhi1);
        var t1 = tanPhi1 * tanPhi1;
        var c1 = EP2 * cosPhi1 * cosPhi1;
        var r1 = A * (1 - E2) / Math.Pow(1 - E2 * sinPhi1 * sinPhi1, 1.5);
        var d = (utm.EastingMeters - 500000.0) / (n1 * K0);

        var lat = phi1 - (n1 * tanPhi1 / r1) * (d * d / 2
                                                 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * EP2) * Math.Pow(d, 4) / 24
                                                 + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * EP2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);

        var lon0 = CentralMeridianDeg(utm.Zone) * DegToRad;
        var lon = lon0 + (d
                          - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6
                          + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * EP2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / cosPhi1;

        return (lat * RadToDeg, lon * RadToDeg);
    }
}

/// <summary>
/// MGRS (Military Grid Reference System) grid-string encode/decode, built on
/// <see cref="UtmProjection"/>. PUBLIC STANDARD (NGA/NIMA MGRS specification); latitude-band and
/// 100km-square grid-letter derivation follows the standard public algorithm (the same one nearly
/// every open MGRS implementation is based on). CALIBRATED_APPROXIMATION note: the handful of real
/// UTM zone irregularities around Norway/Svalbard are not modeled (plain 6°-wide zones only) --
/// irrelevant for any DCS map, which never crosses those latitudes/longitudes.
/// </summary>
public static class Mgrs
{
    private const string LatBands = "CDEFGHJKLMNPQRSTUVWX"; // 20 bands, -80..84, skips I and O
    private const string ColumnLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // 24 letters, skips I and O
    private const string RowLetters = "ABCDEFGHJKLMNPQRSTUV"; // 20 letters, skips I and O

    /// <summary>Standard MGRS latitude band letter for a given latitude, -80..84.</summary>
    public static char LatitudeBand(double latDeg)
    {
        if (latDeg is < -80.0 or > 84.0)
            throw new CoordinateFormatException("MGRS is only defined between 80S and 84N");

        if (latDeg >= 72.0) return 'X'; // 72..84, the one 12-degree-tall band
        var index = (int)Math.Floor((latDeg + 80.0) / 8.0);
        return LatBands[Math.Clamp(index, 0, LatBands.Length - 1)];
    }

    public static string LatLonToMgrs(double latDeg, double lonDeg, int precisionDigits = 5)
    {
        precisionDigits = Math.Clamp(precisionDigits, 1, 5);
        var utm = UtmProjection.LatLonToUtm(latDeg, lonDeg);
        var band = LatitudeBand(latDeg);

        var (colLetter, rowLetter) = GridSquareLetters(utm.Zone, utm.EastingMeters, utm.NorthingMeters);

        var divisor = Math.Pow(10, 5 - precisionDigits);
        var eastingDigits = ((long)(utm.EastingMeters % 100000.0 / divisor)).ToString().PadLeft(precisionDigits, '0');
        var northingDigits = ((long)(utm.NorthingMeters % 100000.0 / divisor)).ToString().PadLeft(precisionDigits, '0');

        return $"{utm.Zone}{band}{colLetter}{rowLetter}{eastingDigits}{northingDigits}";
    }

    public static (double LatDeg, double LonDeg) MgrsToLatLon(string mgrsInput)
    {
        var text = (mgrsInput ?? string.Empty).Replace(" ", "").Trim().ToUpperInvariant();

        var i = 0;
        while (i < text.Length && char.IsDigit(text[i])) i++;
        if (i is 0 or > 2 || i >= text.Length)
            throw new CoordinateFormatException("Enter an MGRS grid reference, e.g. 30TXT1234567890");
        if (!int.TryParse(text[..i], out var zone) || zone is < 1 or > 60)
            throw new CoordinateFormatException("MGRS zone number must be 1-60");

        if (i + 3 > text.Length)
            throw new CoordinateFormatException("Enter an MGRS grid reference, e.g. 30TXT1234567890");
        var band = text[i];
        var colLetter = text[i + 1];
        var rowLetter = text[i + 2];
        if (!LatBands.Contains(band))
            throw new CoordinateFormatException($"'{band}' is not a valid MGRS latitude band letter");
        if (!ColumnLetters.Contains(colLetter))
            throw new CoordinateFormatException($"'{colLetter}' is not a valid MGRS grid-square column letter");
        if (!RowLetters.Contains(rowLetter))
            throw new CoordinateFormatException($"'{rowLetter}' is not a valid MGRS grid-square row letter");

        var digits = text[(i + 3)..];
        if (digits.Length == 0 || digits.Length % 2 != 0 || digits.Length > 10)
            throw new CoordinateFormatException("MGRS easting/northing digits must be an even count, up to 10 (5+5)");

        var precisionDigits = digits.Length / 2;
        var eastingDigits = digits[..precisionDigits];
        var northingDigits = digits[precisionDigits..];
        var scale = Math.Pow(10, 5 - precisionDigits);
        var eastingWithinSquare = long.Parse(eastingDigits, CultureInfo.InvariantCulture) * scale;
        var northingWithinSquare = long.Parse(northingDigits, CultureInfo.InvariantCulture) * scale;

        // Resolve the 100km square's absolute easting/northing: the grid letters alone are
        // ambiguous across the whole globe (they repeat), so use the band's approximate northing
        // to pick the right multiple of 2,000,000m (the row-letter cycle period) -- standard MGRS
        // decode approach.
        var isNorthern = band >= 'N';
        var columnIndex = ColumnLetters.IndexOf(colLetter);
        var columnStart = zone % 3 switch { 1 => 0, 2 => 8, _ => 16 };
        var relativeColumn = ((columnIndex - columnStart) % 24 + 24) % 24;
        var easting100km = (relativeColumn + 1) * 100000.0 + eastingWithinSquare;

        var rowIndex = RowLetters.IndexOf(rowLetter);
        var rowStart = zone % 2 == 1 ? 0 : 5;
        var relativeRow = ((rowIndex - rowStart) % 20 + 20) % 20;

        var approxBandNorthing = ApproximateNorthingForBand(band, isNorthern);
        var northingBase = Math.Floor(approxBandNorthing / 2_000_000.0) * 2_000_000.0;
        var northing100km = northingBase + relativeRow * 100000.0 + northingWithinSquare;
        // The row cycle can land one 2,000,000m block short/long of the true value near a
        // boundary; nudge to whichever candidate is actually closest to the band's own range.
        var candidates = new[] { northing100km - 2_000_000.0, northing100km, northing100km + 2_000_000.0 };
        northing100km = candidates.OrderBy(c => Math.Abs(c - approxBandNorthing)).First();

        // ApproximateNorthingForBand (and therefore northing100km above) works in the "true"
        // northing convention -- southern-hemisphere false-northing (+10,000,000m) subtracted
        // out, matching what UtmToLatLon itself subtracts back off. UtmCoordinate.NorthingMeters
        // is defined as the raw UTM-reported value (false-northing already applied for southern
        // points), so it has to be re-added here before building the coordinate.
        var reportedNorthing = isNorthern ? northing100km : northing100km + 10_000_000.0;

        var utm = new UtmCoordinate(zone, isNorthern, easting100km, reportedNorthing);
        return UtmProjection.UtmToLatLon(utm);
    }

    /// <summary>Rough northing (meters, UTM-hemisphere-relative) for the middle of a latitude
    /// band, used only to disambiguate which 2,000,000m row-letter cycle an MGRS square is in.</summary>
    private static double ApproximateNorthingForBand(char band, bool isNorthern)
    {
        var index = LatBands.IndexOf(band);
        var midLatDeg = index < 0 ? 0.0 : -80.0 + index * 8.0 + 4.0;
        var utm = UtmProjection.LatLonToUtm(midLatDeg, 3.0); // any zone's central meridian works for this estimate
        return isNorthern ? utm.NorthingMeters : utm.NorthingMeters - 10_000_000.0;
    }

    private static (char ColumnLetter, char RowLetter) GridSquareLetters(int zone, double eastingMeters, double northingMeters)
    {
        var columnIndex = (int)Math.Floor(eastingMeters / 100000.0) - 1; // 1-based 100km band -> 0-based index
        var columnStart = zone % 3 switch { 1 => 0, 2 => 8, _ => 16 };
        var colLetter = ColumnLetters[((columnStart + columnIndex) % 24 + 24) % 24];

        var rowIndex = (int)Math.Floor(northingMeters / 100000.0);
        var rowStart = zone % 2 == 1 ? 0 : 5;
        var rowLetter = RowLetters[((rowStart + rowIndex) % 20 + 20) % 20];

        return (colLetter, rowLetter);
    }
}
