using OpenFreq.Client.Services.Satcom;
using OpenFreq.Common.Satcom;

namespace OpenFreq.Client.Tests;

public class SatcomAntennaSelectorStateMachineTests
{
    [Fact]
    public void DefaultsToLowerBeforeFirstRead()
    {
        var sm = new SatcomAntennaSelectorStateMachine();
        Assert.Equal(SatcomAntennaSelection.Lower, sm.Selection);
    }

    [Fact]
    public void CleanOneLatchesUpper()
    {
        var sm = new SatcomAntennaSelectorStateMachine();
        Assert.Equal(SatcomAntennaSelection.Upper, sm.Update(1.0));
    }

    [Fact]
    public void CleanZeroLatchesLower()
    {
        var sm = new SatcomAntennaSelectorStateMachine();
        sm.Update(1.0); // move off the default so this is a real assertion, not a no-op
        Assert.Equal(SatcomAntennaSelection.Lower, sm.Update(0.0));
    }

    [Fact]
    public void MiddleDetentLeavesLastSelectionIntact()
    {
        var sm = new SatcomAntennaSelectorStateMachine();
        sm.Update(1.0); // latch Upper
        Assert.Equal(SatcomAntennaSelection.Upper, sm.Update(0.5));
    }

    [Fact]
    public void MiddleDetentLeavesDefaultLowerIntactIfNeverMoved()
    {
        var sm = new SatcomAntennaSelectorStateMachine();
        Assert.Equal(SatcomAntennaSelection.Lower, sm.Update(0.5));
    }

    [Fact]
    public void NullRawValueLeavesLastSelectionIntact()
    {
        var sm = new SatcomAntennaSelectorStateMachine();
        sm.Update(1.0); // latch Upper
        Assert.Equal(SatcomAntennaSelection.Upper, sm.Update(null));
    }

    [Theory]
    [InlineData(0.94)] // just outside tolerance below 1.0
    [InlineData(1.06)] // just outside tolerance above 1.0
    public void NearMissesOfUpperDoNotLatch(double rawValue)
    {
        var sm = new SatcomAntennaSelectorStateMachine();
        Assert.Equal(SatcomAntennaSelection.Lower, sm.Update(rawValue)); // stays at default
    }

    [Fact]
    public void WithinToleranceOfOneStillLatches()
    {
        // Half the tolerance, not the full amount -- testing exactly at the boundary
        // (1.0 - Tolerance) is floating-point-fragile, since the test's subtraction and the state
        // machine's own internal subtraction don't necessarily round identically.
        var sm = new SatcomAntennaSelectorStateMachine();
        Assert.Equal(SatcomAntennaSelection.Upper, sm.Update(1.0 - SatcomAntennaSelectorStateMachine.Tolerance / 2));
    }

    [Fact]
    public void RealTransitionSequenceEndsAtLower()
    {
        var sm = new SatcomAntennaSelectorStateMachine();

        sm.Update(1.0); // pilot selects Upper
        Assert.Equal(SatcomAntennaSelection.Upper, sm.Selection);

        sm.Update(0.5); // switch mid-travel toward Lower
        Assert.Equal(SatcomAntennaSelection.Upper, sm.Selection); // still Upper -- hasn't arrived yet

        sm.Update(0.0); // arrives at Lower
        Assert.Equal(SatcomAntennaSelection.Lower, sm.Selection);
    }
}
