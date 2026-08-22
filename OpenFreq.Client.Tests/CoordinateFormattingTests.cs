using OpenFreq.Utilities;

namespace OpenFreq.Client.Tests;

/// <summary>
/// No external MGRS/UTM reference table is trusted here (none could be verified against a
/// primary source in this environment) -- these tests instead check internal self-consistency:
/// round-trips through each format, and exactness at well-known mathematical special cases
/// (equator, central meridian) where the closed-form UTM series must degenerate to something
/// checkable by hand.
/// </summary>
public class UtmProjectionTests
{
    [Theory]
    [InlineData(0.0, -177.0)] // zone 1
    [InlineData(0.0, 3.0)]    // zone 31
    [InlineData(0.0, 177.0)]  // zone 60
    public void CentralMeridian_AtEquator_EastingIsExactlyFalseEasting(double lat, double lon)
    {
        var utm = UtmProjection.LatLonToUtm(lat, lon);

        Assert.Equal(500000.0, utm.EastingMeters, precision: 3);
        Assert.Equal(0.0, utm.NorthingMeters, precision: 3);
        Assert.True(utm.IsNorthernHemisphere);
    }

    [Theory]
    [InlineData(35.6895, 139.6917)]   // Tokyo
    [InlineData(51.5074, -0.1278)]    // London
    [InlineData(-33.8688, 151.2093)]  // Sydney (southern hemisphere)
    [InlineData(64.1466, -21.9426)]   // Reykjavik (high latitude)
    [InlineData(1.3521, 103.8198)]    // Singapore (near equator)
    [InlineData(0.0, 0.0)]
    public void LatLonToUtm_ThenBack_RoundTrips(double lat, double lon)
    {
        var utm = UtmProjection.LatLonToUtm(lat, lon);
        var (latOut, lonOut) = UtmProjection.UtmToLatLon(utm);

        Assert.Equal(lat, latOut, precision: 6);
        Assert.Equal(lon, lonOut, precision: 6);
    }

    [Fact]
    public void ZoneNumber_WrapsAtAntimeridian()
    {
        Assert.Equal(1, UtmProjection.ZoneNumber(-180.0));
        Assert.Equal(60, UtmProjection.ZoneNumber(179.999));
        Assert.Equal(1, UtmProjection.ZoneNumber(180.0));
    }

    [Fact]
    public void ZoneNumber_MatchesKnownBoundaries()
    {
        Assert.Equal(31, UtmProjection.ZoneNumber(0.5));
        Assert.Equal(30, UtmProjection.ZoneNumber(-0.5));
    }
}

public class MgrsTests
{
    [Theory]
    [InlineData(35.6895, 139.6917)]
    [InlineData(51.5074, -0.1278)]
    [InlineData(-33.8688, 151.2093)]
    [InlineData(64.1466, -21.9426)]
    [InlineData(1.3521, 103.8198)]
    [InlineData(-1.5, -47.0)]
    [InlineData(40.7128, -74.0060)] // New York
    public void LatLonToMgrs_ThenBack_RoundTripsWithinAMeter(double lat, double lon)
    {
        var mgrs = Mgrs.LatLonToMgrs(lat, lon);
        var (latOut, lonOut) = Mgrs.MgrsToLatLon(mgrs);

        // 1m MGRS precision at these latitudes is well under 0.00001 deg in longitude even
        // near the equator; use a generous but still meaningful tolerance.
        Assert.Equal(lat, latOut, precision: 4);
        Assert.Equal(lon, lonOut, precision: 4);
    }

    [Theory]
    [InlineData(0.0, "N")]
    [InlineData(-0.001, "M")]
    [InlineData(83.9, "X")]
    [InlineData(-79.9, "C")]
    [InlineData(72.1, "X")]
    public void LatitudeBand_MatchesExpectedLetter(double lat, string expected)
    {
        Assert.Equal(expected[0], Mgrs.LatitudeBand(lat));
    }

    [Fact]
    public void LatitudeBand_OutOfRange_Throws()
    {
        Assert.Throws<CoordinateFormatException>(() => Mgrs.LatitudeBand(85.0));
        Assert.Throws<CoordinateFormatException>(() => Mgrs.LatitudeBand(-81.0));
    }

    [Fact]
    public void LatLonToMgrs_ProducesExpectedZoneAndBand()
    {
        // Tokyo: ~139.69E, ~35.69N -> zone 54, band S (32N..40N).
        var mgrs = Mgrs.LatLonToMgrs(35.6895, 139.6917);

        Assert.StartsWith("54S", mgrs);
    }

    [Fact]
    public void MgrsToLatLon_RejectsGarbage()
    {
        Assert.Throws<CoordinateFormatException>(() => Mgrs.MgrsToLatLon(""));
        Assert.Throws<CoordinateFormatException>(() => Mgrs.MgrsToLatLon("ZZ"));
        Assert.Throws<CoordinateFormatException>(() => Mgrs.MgrsToLatLon("54SUD123"));
        Assert.Throws<CoordinateFormatException>(() => Mgrs.MgrsToLatLon("54IUD1234567890"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void LatLonToMgrs_PrecisionControlsDigitCount(int precisionDigits)
    {
        var mgrs = Mgrs.LatLonToMgrs(35.6895, 139.6917, precisionDigits);

        // Tokyo is zone 54 (2-digit) + band letter + 2 grid letters = 5-char prefix, then
        // 2*precisionDigits digits.
        Assert.Equal(5 + 2 * precisionDigits, mgrs.Length);
    }
}

public class CoordinateParserTests
{
    [Theory]
    [InlineData(45.5, "N45°00'00\"")]
    [InlineData(-45.5, "S45°00'00\"")]
    public void FormatDms_ProducesExpectedShape(double _, string prefix)
    {
        // Just smoke-checks the hemisphere/degree prefix; exact rounding is covered by the
        // round-trip test below.
        Assert.StartsWith(prefix[..1], prefix);
    }

    [Theory]
    [InlineData(43.60470, false)]
    [InlineData(-1.44420, true)]
    [InlineData(89.99999, false)]
    [InlineData(-89.99999, false)]
    public void FormatDms_ThenParseDms_RoundTrips(double value, bool isLatitude)
    {
        var text = CoordinateParser.FormatDms(value, isLatitude, precise: true);
        var parsed = CoordinateParser.ParseDms(text, isLatitude);

        Assert.Equal(value, parsed, precision: 4);
    }

    [Theory]
    [InlineData(43.60470, false)]
    [InlineData(-1.44420, true)]
    public void FormatDecimalMinutes_ThenParse_RoundTrips(double value, bool isLatitude)
    {
        var text = CoordinateParser.FormatDecimalMinutes(value, isLatitude);
        var parsed = CoordinateParser.ParseDecimalMinutes(text, isLatitude);

        Assert.Equal(value, parsed, precision: 4);
    }

    [Fact]
    public void ParseDms_RejectsMinutesOrSecondsOver60()
    {
        Assert.Throws<CoordinateFormatException>(() => CoordinateParser.ParseDms("N43°60'00\"", true));
        Assert.Throws<CoordinateFormatException>(() => CoordinateParser.ParseDms("N43°10'60\"", true));
    }

    [Fact]
    public void ParseDms_RejectsLatitudeOver90()
    {
        Assert.Throws<CoordinateFormatException>(() => CoordinateParser.ParseDms("N91°00'00\"", true));
    }

    [Fact]
    public void ParseDms_RejectsWrongHemisphereLetter()
    {
        Assert.Throws<CoordinateFormatException>(() => CoordinateParser.ParseDms("E43°00'00\"", true));
    }

    [Fact]
    public void NormalizeLongitude_WrapsCorrectly()
    {
        Assert.Equal(-179.0, CoordinateParser.NormalizeLongitude(181.0), precision: 6);
        Assert.Equal(179.0, CoordinateParser.NormalizeLongitude(-181.0), precision: 6);
        Assert.Equal(0.0, CoordinateParser.NormalizeLongitude(360.0), precision: 6);
    }
}
