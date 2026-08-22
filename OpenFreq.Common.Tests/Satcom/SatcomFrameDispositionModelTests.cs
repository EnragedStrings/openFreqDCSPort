using OpenFreq.Common.Satcom;

namespace OpenFreq.Common.Tests.Satcom;

public class SatcomFrameDispositionModelTests
{
    [Fact]
    public void PerfectLinkAlwaysProducesClean()
    {
        var model = new SatcomFrameDispositionModel(seed: 1);
        for (var i = 0; i < 200; i++)
            Assert.Equal(SatcomFrameDisposition.Clean, model.NextDisposition(0.0, 0.0, 0.0));
    }

    [Fact]
    public void TotalFailureNeverProducesClean()
    {
        var model = new SatcomFrameDispositionModel(seed: 2);
        var sawNonClean = false;
        for (var i = 0; i < 200; i++)
        {
            var d = model.NextDisposition(1.0, 1.0, 1.0);
            Assert.NotEqual(SatcomFrameDisposition.Clean, d);
            if (d != SatcomFrameDisposition.Clean) sawNonClean = true;
        }
        Assert.True(sawNonClean);
    }

    [Fact]
    public void SameSeedProducesIdenticalSequence()
    {
        var a = new SatcomFrameDispositionModel(seed: 42);
        var b = new SatcomFrameDispositionModel(seed: 42);
        for (var i = 0; i < 100; i++)
            Assert.Equal(a.NextDisposition(0.2, 0.05, 0.3), b.NextDisposition(0.2, 0.05, 0.3));
    }

    [Fact]
    public void RawErrorsFullyRecoveredByFecProduceCorrectedNotClean()
    {
        // Raw errors always occur (rawFer=1) but FEC always fully recovers them (postFecFer=0) --
        // must show up as Corrected (FEC did real work), never silently reported as Clean.
        var model = new SatcomFrameDispositionModel(seed: 3);
        var sawCorrected = false;
        for (var i = 0; i < 50; i++)
        {
            var d = model.NextDisposition(rawFrameErrorRate: 1.0, postFecFrameErrorRate: 0.0, burstSeverity: 0.0);
            Assert.NotEqual(SatcomFrameDisposition.Clean, d);
            if (d == SatcomFrameDisposition.Corrected) sawCorrected = true;
        }
        Assert.True(sawCorrected);
    }
}
