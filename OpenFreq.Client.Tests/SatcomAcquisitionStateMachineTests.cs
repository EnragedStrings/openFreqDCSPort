using OpenFreq.Client.Services.Satcom;

namespace OpenFreq.Client.Tests;

public class SatcomAcquisitionStateMachineTests
{
    private static readonly long AcquisitionMs = (long)(SatcomAcquisitionStateMachine.AcquisitionSeconds * 1000);

    [Fact]
    public void StartsInNormal()
    {
        var sm = new SatcomAcquisitionStateMachine();
        Assert.Equal(SatcomState.Normal, sm.State);
        Assert.Equal(0.0, sm.AcquisitionElapsedSeconds);
    }

    [Fact]
    public void LoginTriggerStartsAcquisition()
    {
        var sm = new SatcomAcquisitionStateMachine();
        var state = sm.Update(loginTrigger: true, bandActive: true, nowMs: 0);
        Assert.Equal(SatcomState.Acquiring, state);
        Assert.Equal(0.0, sm.AcquisitionElapsedSeconds);
    }

    [Fact]
    public void BandActiveAloneWithoutLoginTriggerDoesNotStartAcquisition()
    {
        // Sitting on e.g. channel 35 (in-band) without ever having logged in on channel 31+PRST
        // must never start an acquisition -- only channel 31 can log in.
        var sm = new SatcomAcquisitionStateMachine();
        var state = sm.Update(loginTrigger: false, bandActive: true, nowMs: 0);
        Assert.Equal(SatcomState.Normal, state);
    }

    [Fact]
    public void UnavailableJustBeforeAcquisitionCompletes()
    {
        var sm = new SatcomAcquisitionStateMachine();
        sm.Update(true, true, nowMs: 0);
        var state = sm.Update(true, true, nowMs: AcquisitionMs - 1);
        Assert.Equal(SatcomState.Acquiring, state);
        Assert.True(sm.AcquisitionElapsedSeconds < SatcomAcquisitionStateMachine.AcquisitionSeconds);
    }

    [Fact]
    public void ReadyAfterFullAcquisitionDuration()
    {
        var sm = new SatcomAcquisitionStateMachine();
        sm.Update(true, true, nowMs: 0);
        var state = sm.Update(true, true, nowMs: AcquisitionMs);
        Assert.Equal(SatcomState.Ready, state);
        Assert.Equal(SatcomAcquisitionStateMachine.AcquisitionSeconds, sm.AcquisitionElapsedSeconds);
    }

    [Fact]
    public void StaysReadyOnSubsequentUpdatesWhileStillInBand()
    {
        var sm = new SatcomAcquisitionStateMachine();
        sm.Update(true, true, nowMs: 0);
        sm.Update(true, true, nowMs: AcquisitionMs);
        var state = sm.Update(true, true, nowMs: AcquisitionMs + 10_000);
        Assert.Equal(SatcomState.Ready, state);
        Assert.Equal(SatcomAcquisitionStateMachine.AcquisitionSeconds, sm.AcquisitionElapsedSeconds);
    }

    [Fact]
    public void LeavingLoginConfigurationDuringAcquisitionCancelsAndResets()
    {
        var sm = new SatcomAcquisitionStateMachine();
        sm.Update(true, true, nowMs: 0);
        sm.Update(true, true, nowMs: AcquisitionMs / 2);
        // Moved off channel 31 to e.g. channel 32 mid-login -- still "in band" but the exact
        // login trigger (channel 31 + PRST) dropped, which must cancel the login attempt.
        var state = sm.Update(loginTrigger: false, bandActive: true, nowMs: AcquisitionMs / 2 + 100);
        Assert.Equal(SatcomState.Normal, state);
        Assert.Equal(0.0, sm.AcquisitionElapsedSeconds);
    }

    [Fact]
    public void ReenteringAfterCancelRequiresANewFullAcquisition()
    {
        var sm = new SatcomAcquisitionStateMachine();
        sm.Update(true, true, nowMs: 0);
        sm.Update(true, true, nowMs: AcquisitionMs / 2);
        sm.Update(false, false, nowMs: AcquisitionMs / 2 + 100); // cancel, fully off SATCOM

        var restartAt = AcquisitionMs / 2 + 200;
        sm.Update(true, true, nowMs: restartAt); // re-select channel 31 + PRST
        var stillAcquiring = sm.Update(true, true, nowMs: restartAt + AcquisitionMs - 1);
        Assert.Equal(SatcomState.Acquiring, stillAcquiring);

        var ready = sm.Update(true, true, nowMs: restartAt + AcquisitionMs);
        Assert.Equal(SatcomState.Ready, ready);
    }

    [Fact]
    public void LeavingTheBandAfterReadyLogsOutImmediately()
    {
        var sm = new SatcomAcquisitionStateMachine();
        sm.Update(true, true, nowMs: 0);
        sm.Update(true, true, nowMs: AcquisitionMs);
        Assert.Equal(SatcomState.Ready, sm.State);

        var state = sm.Update(loginTrigger: false, bandActive: false, nowMs: AcquisitionMs + 100);
        Assert.Equal(SatcomState.Normal, state);
        Assert.Equal(0.0, sm.AcquisitionElapsedSeconds);
    }

    [Fact]
    public void RoamingWithinTheBandAfterReadyStaysLoggedInWithoutReacquiring()
    {
        // The core band-sustain requirement: once Ready, moving off the exact channel 31 + PRST
        // login configuration (loginTrigger false) but staying somewhere in the 31-40 band
        // (bandActive true, e.g. channel 35) must NOT log out or restart acquisition.
        var sm = new SatcomAcquisitionStateMachine();
        sm.Update(true, true, nowMs: 0);
        sm.Update(true, true, nowMs: AcquisitionMs);
        Assert.Equal(SatcomState.Ready, sm.State);

        var state = sm.Update(loginTrigger: false, bandActive: true, nowMs: AcquisitionMs + 100);
        Assert.Equal(SatcomState.Ready, state);
        Assert.Equal(SatcomAcquisitionStateMachine.AcquisitionSeconds, sm.AcquisitionElapsedSeconds);

        // Stays Ready indefinitely while roaming the band.
        var stillReady = sm.Update(loginTrigger: false, bandActive: true, nowMs: AcquisitionMs + 60_000);
        Assert.Equal(SatcomState.Ready, stillReady);
    }

    [Fact]
    public void LeavingTheBandThenReturningRequiresFreshChannel31Login()
    {
        var sm = new SatcomAcquisitionStateMachine();
        sm.Update(true, true, nowMs: 0);
        sm.Update(true, true, nowMs: AcquisitionMs);
        Assert.Equal(SatcomState.Ready, sm.State);

        sm.Update(loginTrigger: false, bandActive: false, nowMs: AcquisitionMs + 100); // leave band -> logout

        // Simply re-entering the band (without going back to channel 31 + PRST) must NOT log
        // back in -- only a fresh login trigger can.
        var afterReturnToBandOnly = sm.Update(loginTrigger: false, bandActive: true, nowMs: AcquisitionMs + 200);
        Assert.Equal(SatcomState.Normal, afterReturnToBandOnly);

        // Only returning to the exact login configuration restarts acquisition.
        var reacquiring = sm.Update(loginTrigger: true, bandActive: true, nowMs: AcquisitionMs + 300);
        Assert.Equal(SatcomState.Acquiring, reacquiring);
    }

    [Fact]
    public void DoesNotBlockOrThrowAcrossManyRapidToggles()
    {
        var sm = new SatcomAcquisitionStateMachine();
        var selected = false;
        for (var t = 0L; t < 100_000; t += 100)
        {
            selected = !selected;
            sm.Update(selected, selected, t);
        }
        // No exception, no hang -- this is the "do not crash or block" requirement. Final state
        // is whatever it is; toggling every 100ms never reaches the acquisition threshold.
        Assert.NotEqual(SatcomState.Ready, sm.State);
    }
}
