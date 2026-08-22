using OpenFreq.Common.Satcom;
using OpenFreqServer.Satcom;

namespace OpenFreqServer.Tests.Satcom;

public class DamaFrameSchedulerTests
{
    [Fact]
    public void FiveKHzAndTwentyFiveKHzSchedulersHaveDistinctFramePeriods()
    {
        var five = new Dama5kHzScheduler();
        var twentyFive = new Dama25kHzScheduler();

        Assert.NotEqual(five.FramePeriodSeconds, twentyFive.FramePeriodSeconds);
        Assert.Equal(8.96, five.FramePeriodSeconds, 3);
    }

    [Fact]
    public void GrantsSlotsUpToCapacityThenDeniesWithoutAPreemptingPriority()
    {
        var scheduler = new Dama5kHzScheduler(capacityPerFrame: 2);

        var slot1 = scheduler.TryAssignSlot("a", priority: 0, nowMs: 0, out var evicted1);
        var slot2 = scheduler.TryAssignSlot("b", priority: 0, nowMs: 0, out var evicted2);
        var slot3 = scheduler.TryAssignSlot("c", priority: 0, nowMs: 0, out var evicted3);

        Assert.NotNull(slot1);
        Assert.NotNull(slot2);
        Assert.Null(slot3); // at capacity, equal priority -- genuine denial
        Assert.Null(evicted1);
        Assert.Null(evicted2);
        Assert.Null(evicted3);
        Assert.Equal(2, scheduler.ActiveHolders.Count);
    }

    [Fact]
    public void HigherPriorityRequestPreemptsLowestPriorityHolderWhenAtCapacity()
    {
        var scheduler = new Dama5kHzScheduler(capacityPerFrame: 1);

        scheduler.TryAssignSlot("low", priority: 0, nowMs: 0, out _);
        var slot = scheduler.TryAssignSlot("high", priority: 5, nowMs: 100, out var evicted);

        Assert.NotNull(slot);
        Assert.Equal("low", evicted);
        Assert.Single(scheduler.ActiveHolders);
        Assert.Equal("high", scheduler.ActiveHolders.Single().ClientId);
    }

    [Fact]
    public void EqualOrLowerPriorityCannotPreemptAtCapacity()
    {
        var scheduler = new Dama5kHzScheduler(capacityPerFrame: 1);

        scheduler.TryAssignSlot("incumbent", priority: 3, nowMs: 0, out _);
        var slot = scheduler.TryAssignSlot("challenger", priority: 3, nowMs: 100, out var evicted);

        Assert.Null(slot);
        Assert.Null(evicted);
        Assert.Equal("incumbent", scheduler.ActiveHolders.Single().ClientId);
    }

    [Fact]
    public void ReleasingASlotFreesCapacityForANewRequest()
    {
        var scheduler = new Dama5kHzScheduler(capacityPerFrame: 1);

        scheduler.TryAssignSlot("a", priority: 0, nowMs: 0, out _);
        scheduler.ReleaseSlot("a");
        var slot = scheduler.TryAssignSlot("b", priority: 0, nowMs: 100, out _);

        Assert.NotNull(slot);
    }

    [Fact]
    public void RequestingClientAlreadyHoldingASlotGetsTheSameSlotBackWithoutConsumingAnotherOne()
    {
        var scheduler = new Dama5kHzScheduler(capacityPerFrame: 1);

        var first = scheduler.TryAssignSlot("a", priority: 0, nowMs: 0, out _);
        var second = scheduler.TryAssignSlot("a", priority: 0, nowMs: 50, out var evicted);

        Assert.Equal(first, second);
        Assert.Null(evicted);
    }

    [Fact]
    public void FrameIndexAdvancesWithTime()
    {
        var scheduler = new Dama5kHzScheduler();
        var frame0 = scheduler.CurrentFrameIndex(nowMs: 0);
        var frameLater = scheduler.CurrentFrameIndex(nowMs: (long)(Dama5kHzScheduler.FramePeriodSecondsConst * 1000) + 1);
        Assert.True(frameLater > frame0);
    }
}
