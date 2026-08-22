using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenFreq.Utilities;

/// <summary>Which text representation a location's Latitude/Longitude is entered/displayed in.
/// The underlying stored value (LocationViewModel.Latitude/Longitude, LocationData.Latitude/
/// Longitude) is always plain decimal-degrees regardless of this choice -- these are purely
/// input/display formats that convert to/from that one canonical representation.</summary>
public enum CoordinateFormat
{
    /// <summary>e.g. 43.60470, -1.44420</summary>
    DecimalDegrees,

    /// <summary>Classic DMS, whole seconds. e.g. N43°36'17" W001°26'39"</summary>
    LatLongStandard,

    /// <summary>DMS with fractional (0.01") seconds. e.g. N43°36'17.53" W001°26'39.12"</summary>
    Precise,

    /// <summary>Degrees + decimal minutes (DDM). e.g. N43°36.283' W001°26.652'</summary>
    DecimalMinutes,

    /// <summary>Military Grid Reference System, e.g. 30TXT1234567890</summary>
    Mgrs
}

/// <summary>Thrown by <see cref="CoordinateParser"/> on invalid input, message is user-facing
/// (matches the ChannelCardViewModel.FrequencyMhzString convention of a throwing string-proxy
/// setter with a message the UI shows directly).</summary>
public sealed class CoordinateFormatException(string message) : ArgumentException(message);

/// <summary>
/// Parses/formats geographic coordinates in <see cref="CoordinateFormat"/>'s five representations,
/// always converging on/from plain WGS84 decimal-degree lat/lon (the one representation actually
/// stored in LocationData/LocationViewModel). MGRS support is built on <see cref="UtmProjection"/>
/// (a from-scratch WGS84 Transverse Mercator implementation, PUBLIC STANDARD formulas -- see that
/// class) rather than any external package, since DotSpatial.Projections (already a dependency,
/// used elsewhere for theater-local projections) has no MGRS grid-string encode/decode of its own.
/// </summary>
public static class CoordinateParser
{
    public static double NormalizeLongitude(double lonDeg)
    {
        // Half-open [-180, 180) so the antimeridian has one canonical representation
        // (-180, not +180) -- UtmProjection.ZoneNumber relies on this to place exactly-180
        // longitudes in zone 1 rather than zone 60.
        var lon = lonDeg % 360.0;
        if (lon < -180.0) lon += 360.0;
        if (lon >= 180.0) lon -= 360.0;
        return lon;
    }

    // ---- Decimal degrees ----

    public static string FormatDecimalDegrees(double lat, double lon) =>
        $"{lat.ToString("F5", CultureInfo.InvariantCulture)}, {lon.ToString("F5", CultureInfo.InvariantCulture)}";

    /// <summary>Single-axis decimal-degrees formatting, for a format-selector UI where latitude
    /// and longitude are edited in separate boxes (unlike <see cref="FormatDecimalDegrees"/>,
    /// which renders both together).</summary>
    public static string FormatDecimalDegreesAxis(double value) => value.ToString("F5", CultureInfo.InvariantCulture);

    public static double ParseDecimalDegrees(string input, bool isLatitude)
    {
        var text = (input ?? string.Empty).Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new CoordinateFormatException(isLatitude
                ? "Enter latitude in decimal degrees, e.g. 43.60470"
                : "Enter longitude in decimal degrees, e.g. -1.44420");

        ValidateRange(Math.Abs(value), isLatitude);
        return value;
    }

    // ---- DMS (LatLongStandard / Precise) ----

    private static readonly Regex DmsPattern = new(
        @"^\s*([NSEW])?\s*(\d{1,3})[°ºDd\s]+(\d{1,2})['’Mm\s]+(\d{1,2}(?:\.\d+)?)[""”Ss]?\s*([NSEW])?\s*$",
        RegexOptions.Compiled);

    public static string FormatDms(double value, bool isLatitude, bool precise)
    {
        var hemisphere = HemisphereLetter(value, isLatitude);
        var abs = Math.Abs(value);
        var degrees = (int)abs;
        var minutesFull = (abs - degrees) * 60.0;
        var minutes = (int)minutesFull;
        var seconds = (minutesFull - minutes) * 60.0;

        // Guard against rounding pushing seconds to 60 (e.g. 59.996" -> displayed as 60.00").
        var secondsText = precise ? seconds.ToString("F2", CultureInfo.InvariantCulture) : Math.Round(seconds).ToString(CultureInfo.InvariantCulture);
        if ((precise && seconds >= 59.995) || (!precise && Math.Round(seconds) >= 60))
        {
            seconds = 0;
            minutes++;
            if (minutes >= 60) { minutes = 0; degrees++; }
            secondsText = precise ? "0.00" : "0";
        }

        var degreesWidth = isLatitude ? 2 : 3;
        return $"{hemisphere}{degrees.ToString().PadLeft(degreesWidth, '0')}°{minutes:00}'{secondsText.PadLeft(precise ? 5 : 2, '0')}\"";
    }

    public static double ParseDms(string input, bool isLatitude)
    {
        var text = (input ?? string.Empty).Trim();
        var match = DmsPattern.Match(text);
        if (!match.Success)
            throw new CoordinateFormatException(isLatitude
                ? "Enter latitude as e.g. N43°36'17\" (degrees° minutes' seconds\")"
                : "Enter longitude as e.g. W001°26'39\" (degrees° minutes' seconds\")");

        var hemisphereText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[5].Value;
        var degrees = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var minutes = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);

        if (minutes >= 60 || seconds >= 60)
            throw new CoordinateFormatException("Minutes and seconds must each be less than 60");

        var value = degrees + minutes / 60.0 + seconds / 3600.0;

        var negative = ApplyHemisphere(ref value, hemisphereText, isLatitude);
        ValidateRange(value, isLatitude);
        return negative ? -value : value;
    }

    // ---- Decimal minutes (DDM) ----

    private static readonly Regex DdmPattern = new(
        @"^\s*([NSEW])?\s*(\d{1,3})[°ºDd\s]+(\d{1,2}(?:\.\d+)?)['’Mm]?\s*([NSEW])?\s*$",
        RegexOptions.Compiled);

    public static string FormatDecimalMinutes(double value, bool isLatitude)
    {
        var hemisphere = HemisphereLetter(value, isLatitude);
        var abs = Math.Abs(value);
        var degrees = (int)abs;
        var minutes = (abs - degrees) * 60.0;

        if (minutes >= 59.995) { minutes = 0; degrees++; }

        var degreesWidth = isLatitude ? 2 : 3;
        return $"{hemisphere}{degrees.ToString().PadLeft(degreesWidth, '0')}°{minutes.ToString("00.000", CultureInfo.InvariantCulture)}'";
    }

    public static double ParseDecimalMinutes(string input, bool isLatitude)
    {
        var text = (input ?? string.Empty).Trim();
        var match = DdmPattern.Match(text);
        if (!match.Success)
            throw new CoordinateFormatException(isLatitude
                ? "Enter latitude as e.g. N43°36.283' (degrees° decimal-minutes')"
                : "Enter longitude as e.g. W001°26.652' (degrees° decimal-minutes')");

        var hemisphereText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[4].Value;
        var degrees = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var minutes = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

        if (minutes >= 60)
            throw new CoordinateFormatException("Minutes must be less than 60");

        var value = degrees + minutes / 60.0;

        var negative = ApplyHemisphere(ref value, hemisphereText, isLatitude);
        ValidateRange(value, isLatitude);
        return negative ? -value : value;
    }

    // ---- Shared DMS/DDM helpers ----

    private static char HemisphereLetter(double value, bool isLatitude) =>
        isLatitude ? (value >= 0 ? 'N' : 'S') : (value >= 0 ? 'E' : 'W');

    /// <summary>Applies an explicit hemisphere letter if present; otherwise the value's own sign
    /// (already non-negative here, so this only matters when no letter was given -- in that case
    /// the caller must supply a signed value up front, which ParseDms/ParseDecimalMinutes don't,
    /// so a missing hemisphere letter defaults to the positive (N/E) hemisphere).</summary>
    private static bool ApplyHemisphere(ref double value, string hemisphereText, bool isLatitude)
    {
        if (string.IsNullOrEmpty(hemisphereText)) return false;
        var letter = char.ToUpperInvariant(hemisphereText[0]);
        var expectedNegative = isLatitude ? letter == 'S' : letter == 'W';
        var expectedPositive = isLatitude ? letter == 'N' : letter == 'E';
        if (!expectedNegative && !expectedPositive)
            throw new CoordinateFormatException(isLatitude ? "Latitude hemisphere must be N or S" : "Longitude hemisphere must be E or W");
        return expectedNegative;
    }

    private static void ValidateRange(double absValue, bool isLatitude)
    {
        var max = isLatitude ? 90.0 : 180.0;
        if (absValue > max)
            throw new CoordinateFormatException(isLatitude ? "Latitude must be between 90S and 90N" : "Longitude must be between 180W and 180E");
    }
}
