using OpenFreq.Common.Satcom;
using OpenFreqServer.Satcom;

namespace OpenFreqServer.Tests.Satcom;

public class DamaNetworkControllerTests
{
    private static readonly SatcomNetDefinition Net = new()
    {
        NetId = "n", DisplayName = "n", Waveform = SatcomWaveform.Dama5k
    };

    private static SatcomLinkResult GoodLink()
    {
        var leg = new SatcomLegResult(45, 90, 36_000_000, 0, 150, 60, 0.12, true, false, "");
        return new SatcomLinkResult
        {
            SatelliteId = "s", SatelliteName = "s",
            Uplink = leg, Downlink = leg,
            CombinedCn0DbHz = 60, EbN0Db = 20,
            RawBer = 1e-6, PostFecBer = 1e-9, FrameErrorRate = 0.0,
            PropagationLatencySeconds = 0.24,
            QualityState = SatcomLinkQualityState.Good,
            LinkAvailable = true,
            FailureReason = SatcomAcquisitionFailureReason.None,
            UnavailableReason = ""
        };
    }

    private static SatcomLinkResult NoLink() =>
        SatcomLinkResult.Unavailable(SatcomAcquisitionFailureReason.NoSatAssignment, "no satellite");

    /// <summary>Drives one terminal from a cold start through Searching/Synchronizing to Ready
    /// using a virtual clock (no real 8.96s/searching/sync waits), matching
    /// SatcomDamaStateMachineTests.ReadyStateMachine's timing but through the controller.</summary>
    private static long AdvanceToReady(DamaNetworkController controller, string clientId, SatcomNetDefinition? net = null)
    {
        net ??= Net;
        var t = 0L;
        controller.Update(clientId, net, loginReady: true, GoodLink(), pttPressed: false, receivingCarrier: false, priority: 0, t);
        t += (long)(SatcomDamaStateMachine.SearchingSeconds * 1000);
        controller.Update(clientId, net, loginReady: true, GoodLink(), pttPressed: false, receivingCarrier: false, priority: 0, t);
        t += (long)(SatcomDamaStateMachine.SynchronizingSeconds * 1000);
        var ready = controller.Update(clientId, net, loginReady: true, GoodLink(), pttPressed: false, receivingCarrier: false, priority: 0, t);
        Assert.Equal(DamaState.Ready, ready);
        return t;
    }

    [Fact]
    public void NotLoginReadyStaysOffline()
    {
        var controller = new DamaNetworkController();
        var state = controller.Update("c1", Net, loginReady: false, NoLink(), false, false, 0, 0);
        Assert.Equal(DamaState.Offline, state);
    }

    [Fact]
    public void PttFromReadyWithGoodLinkAndFreeCapacityReachesTx()
    {
        var controller = new DamaNetworkController();
        var t = AdvanceToReady(controller, "c1");

        var requesting = controller.Update("c1", Net, true, GoodLink(), pttPressed: true, false, 0, t + 10);
        Assert.Equal(DamaState.Requesting, requesting);

        var assigned = controller.Update("c1", Net, true, GoodLink(), pttPressed: true, false, 0, t + 20);
        Assert.Equal(DamaState.Assigned, assigned);

        var tx = controller.Update("c1", Net, true, GoodLink(), pttPressed: true, false, 0, t + 30);
        Assert.Equal(DamaState.Tx, tx);
    }

    [Fact]
    public void CapacityExhaustionDeniesServiceRegardlessOfGoodLinkMargin()
    {
        var controller = new DamaNetworkController();
        var clients = new[] { "c1", "c2", "c3", "c4", "c5" }; // default Dama5kHzScheduler capacity is 4
        var readyTimes = new Dictionary<string, long>();
        foreach (var c in clients)
            readyTimes[c] = AdvanceToReady(controller, c);

        // All 5 request+get assigned in sequence -- the 5th must be denied on capacity even though
        // its link margin is identical (good) to everyone else's.
        var results = new List<DamaState>();
        foreach (var c in clients)
        {
            var t = readyTimes[c] + 10;
            controller.Update(c, Net, true, GoodLink(), pttPressed: true, false, 0, t);
            results.Add(controller.Update(c, Net, true, GoodLink(), pttPressed: true, false, 0, t + 10));
        }

        Assert.Equal(4, results.Count(r => r == DamaState.Assigned));
        Assert.Contains(DamaState.ServiceDenied, results);
    }

    [Fact]
    public void HigherPriorityRequestPreemptsALowerPriorityAssignedTerminal()
    {
        var controller = new DamaNetworkController();
        var scheduler1Capacity = new SatcomNetDefinition
        {
            NetId = "solo", DisplayName = "solo", Waveform = SatcomWaveform.Dama5k
        };

        var t1 = AdvanceToReady(controller, "low", scheduler1Capacity);
        controller.Update("low", scheduler1Capacity, true, GoodLink(), pttPressed: true, false, priority: 0, t1 + 10);
        var lowAssigned = controller.Update("low", scheduler1Capacity, true, GoodLink(), pttPressed: true, false, priority: 0, t1 + 20);

        // Fill remaining capacity (default 4) with equal-priority terminals so the net is genuinely full.
        for (var i = 0; i < 3; i++)
        {
            var id = $"filler{i}";
            var t = AdvanceToReady(controller, id, scheduler1Capacity);
            controller.Update(id, scheduler1Capacity, true, GoodLink(), pttPressed: true, false, priority: 0, t + 10);
            controller.Update(id, scheduler1Capacity, true, GoodLink(), pttPressed: true, false, priority: 0, t + 20);
        }

        Assert.Equal(DamaState.Assigned, lowAssigned);

        var tHigh = AdvanceToReady(controller, "high", scheduler1Capacity);
        controller.Update("high", scheduler1Capacity, true, GoodLink(), pttPressed: true, false, priority: 10, tHigh + 10);
        var highAssigned = controller.Update("high", scheduler1Capacity, true, GoodLink(), pttPressed: true, false, priority: 10, tHigh + 20);

        Assert.Equal(DamaState.Assigned, highAssigned);
    }

    [Fact]
    public void RemoveClientReleasesItsHeldSlot()
    {
        var controller = new DamaNetworkController();
        var t = AdvanceToReady(controller, "c1");
        controller.Update("c1", Net, true, GoodLink(), pttPressed: true, false, 0, t + 10);
        controller.Update("c1", Net, true, GoodLink(), pttPressed: true, false, 0, t + 20);

        Assert.NotNull(controller.CurrentSlot("c1", Net.NetId));
        controller.RemoveClient("c1", Net.NetId);
        Assert.Null(controller.CurrentSlot("c1", Net.NetId));
    }
}
