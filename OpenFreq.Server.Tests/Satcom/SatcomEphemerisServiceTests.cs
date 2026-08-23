using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common.Satcom;
using OpenFreqServer.Satcom;

namespace OpenFreqServer.Tests.Satcom;

public class SatcomEphemerisServiceTests
{
    private static SatcomServerConfig TestConfig(string cacheDir) => new()
    {
        EphemerisCacheDirectory = cacheDir
    };

    [Fact]
    public void StaticGeoSatelliteResolvesToItsConfiguredPositionWithoutAnyNetworkAccess()
    {
        var catalog = new List<SatcomSatelliteDefinition>
        {
            new() { Id = "s1", DisplayName = "S1", EphemerisMode = SatcomEphemerisMode.StaticGeo,
                StaticLongitudeDeg = 42.0, StaticAltitudeMeters = 35_786_000.0 }
        };
        var dir = Path.Combine(Path.GetTempPath(), "openfreq-satcom-test-" + Guid.NewGuid());
        var service = new SatcomEphemerisService(catalog, TestConfig(dir), NullLogger.Instance);

        service.PropagateAll();

        var pos = service.GetPosition("s1");
        Assert.NotNull(pos);
        Assert.Equal(42.0, pos.Value.LongitudeDeg, 3);
        Assert.False(pos.Value.IsStale);
    }

    [Fact]
    public void UnknownSatelliteHasNoPositionUntilPropagated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openfreq-satcom-test-" + Guid.NewGuid());
        var service = new SatcomEphemerisService([], TestConfig(dir), NullLogger.Instance);
        Assert.Null(service.GetPosition("nope"));
    }

    [Fact]
    public void DisabledSatelliteNeverGetsAPosition()
    {
        var catalog = new List<SatcomSatelliteDefinition>
        {
            new() { Id = "s1", DisplayName = "S1", Enabled = false, StaticLongitudeDeg = 10.0 }
        };
        var dir = Path.Combine(Path.GetTempPath(), "openfreq-satcom-test-" + Guid.NewGuid());
        var service = new SatcomEphemerisService(catalog, TestConfig(dir), NullLogger.Instance);

        service.PropagateAll();

        Assert.Null(service.GetPosition("s1"));
    }

    [Fact]
    public void DiskCacheFallback_PropagatesFromCachedTleInsteadOfStaticWhenNoInMemoryPositionExists()
    {
        // Real TLE data this project's own SatcomEphemerisService fetched and cached for UFO 1
        // (NORAD 22563) -- see TryPropagateFromDiskCache's doc comment: CachingRemoteTleProvider
        // only reads its own on-disk cache when it's within EphemerisFetchIntervalHours; once a
        // fetch fails with no in-memory position yet (e.g. a server restart during a network
        // outage), the old behavior snapped straight to the satellite's placeholder Static*
        // fields (0 deg longitude) even though a perfectly usable, if stale, cached TLE sat right
        // there on disk. This test drives that disk-read path directly, independent of network.
        const int noradId = 22563;
        var dir = Path.Combine(Path.GetTempPath(), "openfreq-satcom-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"sat_{noradId}.tle"),
            "2026-08-23 08:06:37Z\n" +
            "UFO 1 (USA 98)\n" +
            "1 22563U 93015A   26234.46605948 -.00000137  00000+0  00000+0 0  9995\n" +
            "2 22563  25.9646  71.2773 0001879 273.9621  81.4770  0.99250589125354\n");

        var sat = new SatcomSatelliteDefinition
        {
            Id = "ufo-1", DisplayName = "UFO 1", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = noradId
        };
        var service = new SatcomEphemerisService([sat], TestConfig(dir), NullLogger.Instance);

        var ok = service.TryPropagateFromDiskCache(sat, noradId, DateTime.UtcNow,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.True(ok);
        var pos = service.GetPosition("ufo-1");
        Assert.NotNull(pos);
        Assert.True(pos.Value.IsStale); // never presented as a live/fresh position
        // Real propagated longitude from that TLE, not the satellite definition's unset (0.0)
        // Static* placeholder -- proves this came from the cached elements, not the old fallback.
        Assert.NotEqual(0.0, pos.Value.LongitudeDeg);
    }

    [Fact]
    public void DiskCacheFallback_ReturnsFalseWhenNoCacheFileExists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openfreq-satcom-test-" + Guid.NewGuid());
        var sat = new SatcomSatelliteDefinition
        {
            Id = "ufo-1", DisplayName = "UFO 1", EphemerisMode = SatcomEphemerisMode.LiveTle, NoradId = 22563
        };
        var service = new SatcomEphemerisService([sat], TestConfig(dir), NullLogger.Instance);

        var ok = service.TryPropagateFromDiskCache(sat, 22563, DateTime.UtcNow,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.False(ok);
        Assert.Null(service.GetPosition("ufo-1"));
    }

    [Fact]
    public void LiveTleSatelliteWithoutAConfiguredNoradIdFallsBackToItsStaticPosition()
    {
        // LiveTle mode but no NoradId set -- must never crash, must fall back to Static* fields
        // (see docs/SATCOM_SIMULATION.md's offline/never-hard-fail requirement).
        var catalog = new List<SatcomSatelliteDefinition>
        {
            new() { Id = "s1", DisplayName = "S1", EphemerisMode = SatcomEphemerisMode.LiveTle,
                NoradId = null, StaticLongitudeDeg = 77.0 }
        };
        var dir = Path.Combine(Path.GetTempPath(), "openfreq-satcom-test-" + Guid.NewGuid());
        var service = new SatcomEphemerisService(catalog, TestConfig(dir), NullLogger.Instance);

        service.PropagateAll();

        var pos = service.GetPosition("s1");
        Assert.NotNull(pos);
        Assert.Equal(77.0, pos.Value.LongitudeDeg, 3);
        Assert.True(pos.Value.IsStale); // fallback is explicitly marked stale, not presented as live
    }
}
