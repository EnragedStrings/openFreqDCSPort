using OpenFreq.Common.Satcom;
using OpenFreqServer.Satcom;

namespace OpenFreqServer.Tests.Satcom;

public class SatcomDataServiceTests
{
    private static readonly SatcomNetDefinition Net = new()
    {
        NetId = "n", DisplayName = "n", BitRateBps = 2400
    };

    [Fact]
    public void ZeroPayloadTransfersInstantlyWithNoPackets()
    {
        var result = SatcomDataService.SimulateTransfer(0, Net, new Dama5kHzScheduler(), 0.0, new Random(1));
        Assert.True(result.Success);
        Assert.Equal(0, result.PayloadPackets);
        Assert.Equal(0.0, result.ElapsedSeconds);
    }

    [Fact]
    public void TransferIsNotInstantaneousForNonTrivialPayload()
    {
        var result = SatcomDataService.SimulateTransfer(10_000, Net, new Dama5kHzScheduler(), 0.0, new Random(1));
        Assert.True(result.Success);
        Assert.True(result.ElapsedSeconds > 0.0, "10KB over a 2400bps-class channel must take real time");
    }

    [Fact]
    public void HigherPostFecBerIncreasesElapsedTimeForTheSamePayload()
    {
        var good = SatcomDataService.SimulateTransfer(10_000, Net, new Dama5kHzScheduler(), postFecBer: 1e-6, new Random(1));
        var poor = SatcomDataService.SimulateTransfer(10_000, Net, new Dama5kHzScheduler(), postFecBer: 1e-3, new Random(1));

        Assert.True(good.Success);
        Assert.True(poor.Success);
        Assert.True(poor.ElapsedSeconds > good.ElapsedSeconds,
            $"Poor RF ({poor.ElapsedSeconds}s) should take longer than good RF ({good.ElapsedSeconds}s)");
    }

    [Fact]
    public void ExtremelyPoorLinkFailsRatherThanClaimingInstantDelivery()
    {
        var result = SatcomDataService.SimulateTransfer(10_000, Net, new Dama5kHzScheduler(), postFecBer: 0.5, new Random(1));
        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void SharedCapacityReducesPerClientGoodputComparedToDedicatedCapacity()
    {
        var shared = SatcomDataService.EffectiveGoodputBps(Net, new Dama5kHzScheduler(capacityPerFrame: 4), packetErrorRate: 0.0);
        var dedicated = SatcomDataService.EffectiveGoodputBps(Net, new Dama5kHzScheduler(capacityPerFrame: int.MaxValue), packetErrorRate: 0.0);
        Assert.True(shared < dedicated);
    }

    [Fact]
    public void SameSeedProducesDeterministicRetransmissionCount()
    {
        var a = SatcomDataService.SimulateTransfer(5_000, Net, new Dama5kHzScheduler(), postFecBer: 1e-3, new Random(7));
        var b = SatcomDataService.SimulateTransfer(5_000, Net, new Dama5kHzScheduler(), postFecBer: 1e-3, new Random(7));
        Assert.Equal(a.Retransmissions, b.Retransmissions);
    }
}
