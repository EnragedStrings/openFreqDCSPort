using OpenFreq.Common.Satcom;

namespace OpenFreq.Common.Tests.Satcom;

public class SatcomLinkBudgetTests
{
    [Fact]
    public void FreeSpacePathLossIncreasesBySixDbWhenDistanceDoubles()
    {
        var near = SatcomLinkBudget.FreeSpacePathLossDb(1_000_000, 300_000_000);
        var far = SatcomLinkBudget.FreeSpacePathLossDb(2_000_000, 300_000_000);
        Assert.InRange(far - near, 6.0, 6.05);
    }

    [Fact]
    public void FreeSpacePathLossIncreasesBySixDbWhenFrequencyDoubles()
    {
        var low = SatcomLinkBudget.FreeSpacePathLossDb(10_000_000, 250_000_000);
        var high = SatcomLinkBudget.FreeSpacePathLossDb(10_000_000, 500_000_000);
        Assert.InRange(high - low, 6.0, 6.05);
    }

    [Fact]
    public void HigherAntennaGainImprovesReceivedPower()
    {
        var low = SatcomLinkBudget.ReceivedCarrierDbw(10, txAntennaGainDb: 0, pathLossDb: 150, rxAntennaGainDb: 0, implementationLossDb: 2);
        var high = SatcomLinkBudget.ReceivedCarrierDbw(10, txAntennaGainDb: 6, pathLossDb: 150, rxAntennaGainDb: 0, implementationLossDb: 2);
        Assert.True(high > low);
        Assert.Equal(6.0, high - low, 6);
    }

    [Fact]
    public void CombinedCarrierToNoiseNeverExceedsTheWorseLeg()
    {
        var combined = SatcomLinkBudget.CombineCarrierToNoiseDensityDbHz(uplinkDbHz: 60.0, downlinkDbHz: 40.0);
        Assert.True(combined <= 40.0 + 1e-6, $"Combined ({combined}) should not exceed the worse (40) leg");
    }

    [Fact]
    public void CombinedCarrierToNoiseDegradesWhenBothLegsAreEquallyMarginal()
    {
        // Two equal legs combine to ~3dB worse than either alone (classic cascaded-noise result).
        var combined = SatcomLinkBudget.CombineCarrierToNoiseDensityDbHz(50.0, 50.0);
        Assert.InRange(combined, 46.9, 47.1);
    }

    [Fact]
    public void HigherEbN0ProducesLowerBerForEveryModulation()
    {
        foreach (var modulation in new[] { SatcomModulation.NoncoherentFsk, SatcomModulation.Dpsk, SatcomModulation.Bpsk })
        {
            var good = SatcomLinkBudget.ModulationBer(12.0, modulation);
            var bad = SatcomLinkBudget.ModulationBer(-2.0, modulation);
            Assert.True(good < bad, $"{modulation}: expected BER at 12dB ({good}) < BER at -2dB ({bad})");
        }
    }

    [Fact]
    public void FecCodingGainReducesBerRelativeToRawAtSameEbN0()
    {
        var raw = SatcomLinkBudget.ModulationBer(2.0, SatcomModulation.NoncoherentFsk);
        var withFec = SatcomLinkBudget.PostFecBer(2.0, fecCodingGainDb: 6.0, fecStrength: 1.0, SatcomModulation.NoncoherentFsk);
        Assert.True(withFec < raw);
    }

    [Fact]
    public void FrameErrorRateIsMonotonicInBer()
    {
        var low = SatcomLinkBudget.FrameErrorRate(1e-6, 54);
        var high = SatcomLinkBudget.FrameErrorRate(1e-2, 54);
        Assert.True(low < high);
    }

    [Fact]
    public void BelowAcquisitionThresholdPreventsAcquisition()
    {
        var net = new SatcomNetDefinition
        {
            NetId = "n", DisplayName = "n",
            AcquisitionEbN0ThresholdDb = 5.0, TrackingEbN0ThresholdDb = 1.0
        };
        var tracker = new SatcomLinkQualityTracker();
        // Well below even the tracking threshold -- must never reach Good.
        var state = tracker.Update(ebN0Db: -20.0, net, nowMs: 0);
        Assert.Equal(SatcomLinkQualityState.Lost, state);
        state = tracker.Update(ebN0Db: -20.0, net, nowMs: 60_000);
        Assert.Equal(SatcomLinkQualityState.Lost, state);
    }
}

public class SatcomLinkQualityTrackerTests
{
    private static readonly SatcomNetDefinition Net = new()
    {
        NetId = "n", DisplayName = "n",
        AcquisitionEbN0ThresholdDb = 5.0, TrackingEbN0ThresholdDb = 1.0, HoldoverSeconds = 4.0
    };

    [Fact]
    public void RequiresAcquisitionThresholdNotJustTrackingThresholdToReachGoodFromLost()
    {
        var tracker = new SatcomLinkQualityTracker();
        // Above tracking (1dB) but below acquisition (5dB) -- must NOT jump straight to Good.
        var state = tracker.Update(ebN0Db: 3.0, Net, nowMs: 0);
        Assert.NotEqual(SatcomLinkQualityState.Good, state);
    }

    [Fact]
    public void ReachesGoodAboveAcquisitionThenStaysLockedAboveOnlyTrackingThreshold()
    {
        var tracker = new SatcomLinkQualityTracker();
        var acquired = tracker.Update(ebN0Db: 8.0, Net, nowMs: 0);
        Assert.Equal(SatcomLinkQualityState.Good, acquired);

        // Drops below acquisition but stays above tracking -- hysteresis keeps it locked
        // (Marginal), not instantly bounced back to needing re-acquisition.
        var marginal = tracker.Update(ebN0Db: 2.0, Net, nowMs: 1000);
        Assert.Equal(SatcomLinkQualityState.Marginal, marginal);
    }

    [Fact]
    public void HoldoverGracePeriodDelaysLostAfterSignalDrops()
    {
        var tracker = new SatcomLinkQualityTracker();
        tracker.Update(ebN0Db: 8.0, Net, nowMs: 0); // Good

        // Signal collapses entirely.
        var holdover = tracker.Update(ebN0Db: -50.0, Net, nowMs: 1000);
        Assert.Equal(SatcomLinkQualityState.Holdover, holdover);

        // Still within the holdover window -- not yet Lost.
        var stillHoldover = tracker.Update(ebN0Db: -50.0, Net, nowMs: 1000 + 3000);
        Assert.Equal(SatcomLinkQualityState.Holdover, stillHoldover);

        // Past the holdover window -- now Lost.
        var lost = tracker.Update(ebN0Db: -50.0, Net, nowMs: 1000 + 5000);
        Assert.Equal(SatcomLinkQualityState.Lost, lost);
    }

    [Fact]
    public void RecoveringDuringHoldoverAvoidsDroppingToLost()
    {
        var tracker = new SatcomLinkQualityTracker();
        tracker.Update(ebN0Db: 8.0, Net, nowMs: 0); // Good
        tracker.Update(ebN0Db: -50.0, Net, nowMs: 1000); // Holdover

        // Recovers above tracking before the holdover window expires.
        var recovered = tracker.Update(ebN0Db: 8.0, Net, nowMs: 2000);
        Assert.Equal(SatcomLinkQualityState.Good, recovered);
    }
}
