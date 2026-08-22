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
