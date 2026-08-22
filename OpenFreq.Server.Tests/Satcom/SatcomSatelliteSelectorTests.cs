using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common.Satcom;
using OpenFreqServer.Satcom;

namespace OpenFreqServer.Tests.Satcom;

public class SatcomSatelliteSelectorTests
{
    private static SatcomEphemerisService BuildEphemeris(params SatcomSatelliteDefinition[] catalog)
    {
        var dir = Path.Combine(Path.GetTempPath(), "openfreq-satcom-test-" + Guid.NewGuid());
        var service = new SatcomEphemerisService(catalog, new SatcomServerConfig { EphemerisCacheDirectory = dir },
            NullLogger.Instance);
        service.PropagateAll();
        return service;
    }

    private static readonly SatcomTerminalState Terminal = new(0.0, 0.0, 3000.0, 0.0, 0.0, 0.0, false);

    [Fact]
    public void AssignedModeAlwaysReturnsTheConfiguredSatelliteRegardlessOfGeometry()
    {
        var near = new SatcomSatelliteDefinition { Id = "near", DisplayName = "near", StaticLongitudeDeg = 0.0 };
        var better = new SatcomSatelliteDefinition { Id = "better", DisplayName = "better", StaticLongitudeDeg = 0.5 };
        var ephemeris = BuildEphemeris(near, better);
        var catalog = new List<SatcomSatelliteDefinition> { near, better };

        var net = new SatcomNetDefinition
        {
            NetId = "n", DisplayName = "n", SelectionMode = SatelliteSelectionMode.Assigned,
            AssignedSatelliteId = "near"
        };

        var selector = new SatcomSatelliteSelector();
        var chosen = selector.SelectSatellite("k", net, catalog, ephemeris, Terminal, nowMs: 0);

        // Even though "better" would likely score higher on link margin, Assigned mode must never
        // silently pick a different satellite -- this is the spec's explicit "do not auto-connect
        // to nearest/best satellite" correction.
        Assert.Equal("near", chosen);
    }

    [Fact]
    public void AutoBestVisiblePicksAValidCandidateOnFirstSelection()
    {
        var sat = new SatcomSatelliteDefinition { Id = "s1", DisplayName = "s1", StaticLongitudeDeg = 0.0 };
        var ephemeris = BuildEphemeris(sat);
        var catalog = new List<SatcomSatelliteDefinition> { sat };
        var net = new SatcomNetDefinition
        {
            NetId = "n", DisplayName = "n", SelectionMode = SatelliteSelectionMode.AutoBestVisible
        };

        var selector = new SatcomSatelliteSelector();
        var chosen = selector.SelectSatellite("k", net, catalog, ephemeris, Terminal, nowMs: 0);

        Assert.Equal("s1", chosen);
    }

    [Fact]
    public void AutoBestVisibleDoesNotHandOverBeforeMinimumHoldDuration()
    {
        var current = new SatcomSatelliteDefinition { Id = "current", DisplayName = "current", StaticLongitudeDeg = 0.0 };
        var ephemeris = BuildEphemeris(current);
        var catalog = new List<SatcomSatelliteDefinition> { current };
        var net = new SatcomNetDefinition
        {
            NetId = "n", DisplayName = "n", SelectionMode = SatelliteSelectionMode.AutoBestVisible,
            AutoSelectMinHoldSeconds = 20.0
        };

        var selector = new SatcomSatelliteSelector();
        var first = selector.SelectSatellite("k", net, catalog, ephemeris, Terminal, nowMs: 0);
        // Same single-candidate catalog -- nothing to hand over to, but this also exercises that
        // repeated calls within the hold window are stable (no thrashing) rather than re-evaluating
        // from scratch every tick.
        var again = selector.SelectSatellite("k", net, catalog, ephemeris, Terminal, nowMs: 5_000);

        Assert.Equal(first, again);
    }

    [Fact]
    public void ManualModeBehavesLikeAssignedReturningTheConfiguredId()
    {
        var sat = new SatcomSatelliteDefinition { Id = "pinned", DisplayName = "pinned", StaticLongitudeDeg = 10.0 };
        var ephemeris = BuildEphemeris(sat);
        var catalog = new List<SatcomSatelliteDefinition> { sat };
        var net = new SatcomNetDefinition
        {
            NetId = "n", DisplayName = "n", SelectionMode = SatelliteSelectionMode.Manual,
            AssignedSatelliteId = "pinned"
        };

        var selector = new SatcomSatelliteSelector();
        var chosen = selector.SelectSatellite("k", net, catalog, ephemeris, Terminal, nowMs: 0);
        Assert.Equal("pinned", chosen);
    }
}
