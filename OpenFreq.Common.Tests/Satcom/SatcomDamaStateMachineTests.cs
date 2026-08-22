using OpenFreq.Common.Satcom;

namespace OpenFreq.Common.Tests.Satcom;

public class SatcomDamaStateMachineTests
{
    [Fact]
    public void StartsOffline()
    {
        var sm = new SatcomDamaStateMachine();
        Assert.Equal(DamaState.Offline, sm.State);
    }

    [Fact]
    public void StaysOfflineWhileLoginNotReady()
    {
        var sm = new SatcomDamaStateMachine();
        var state = sm.Update(loginReady: false, linkAvailable: true, combinedMarginDb: 10, false, false, 0);
        Assert.Equal(DamaState.Offline, state);
    }

    [Fact]
    public void ProgressesSearchingSynchronizingReadyOnceLoginReadyAndLinkAvailable()
    {
        var sm = new SatcomDamaStateMachine();
        sm.Update(true, true, 10, false, false, 0); // Offline -> Searching

        var stillSearching = sm.Update(true, true, 10, false, false,
            (long)(SatcomDamaStateMachine.SearchingSeconds * 1000) - 1);
        Assert.Equal(DamaState.Searching, stillSearching);

        var synchronizing = sm.Update(true, true, 10, false, false,
            (long)(SatcomDamaStateMachine.SearchingSeconds * 1000));
        Assert.Equal(DamaState.Synchronizing, synchronizing);

        var syncStart = (long)(SatcomDamaStateMachine.SearchingSeconds * 1000);
        var ready = sm.Update(true, true, 10, false, false,
            syncStart + (long)(SatcomDamaStateMachine.SynchronizingSeconds * 1000));
        Assert.Equal(DamaState.Ready, ready);
    }

    [Fact]
    public void SearchingWaitsForLinkAvailableBeforeAdvancing()
    {
        var sm = new SatcomDamaStateMachine();
        sm.Update(true, false, -999, false, false, 0); // Offline -> Searching, no satellite yet

        // Even after plenty of time, no satellite -> stuck in Searching.
        var state = sm.Update(true, false, -999, false, false, 60_000);
        Assert.Equal(DamaState.Searching, state);
    }

    private static SatcomDamaStateMachine ReadyStateMachine()
    {
        var sm = new SatcomDamaStateMachine();
        var t = 0L;
        sm.Update(true, true, 10, false, false, t);
        t += (long)(SatcomDamaStateMachine.SearchingSeconds * 1000);
        sm.Update(true, true, 10, false, false, t);
        t += (long)(SatcomDamaStateMachine.SynchronizingSeconds * 1000);
        sm.Update(true, true, 10, false, false, t);
        Assert.Equal(DamaState.Ready, sm.State);
        return sm;
    }

    [Fact]
    public void PttFromReadyGoesThroughRequestingToAssignedToTx()
    {
        var sm = ReadyStateMachine();

        var requesting = sm.Update(true, true, 10, pttPressed: true, false, 100_000);
        Assert.Equal(DamaState.Requesting, requesting);

        var assigned = sm.Update(true, true, 10, pttPressed: true, false, 100_100);
        Assert.Equal(DamaState.Assigned, assigned);

        var tx = sm.Update(true, true, 10, pttPressed: true, false, 100_200);
        Assert.Equal(DamaState.Tx, tx);

        var backToReady = sm.Update(true, true, 10, pttPressed: false, false, 100_300);
        Assert.Equal(DamaState.Ready, backToReady);
    }

    [Fact]
    public void PoorMarginDuringRequestCausesServiceDeniedThenRecoversToReady()
    {
        var sm = ReadyStateMachine();

        var requesting = sm.Update(true, true, -10, pttPressed: true, false, 100_000);
        Assert.Equal(DamaState.Requesting, requesting);

        var denied = sm.Update(true, true, -10, pttPressed: true, false, 100_100);
        Assert.Equal(DamaState.ServiceDenied, denied);

        var recovered = sm.Update(true, true, -10, pttPressed: true, false, 100_200);
        Assert.Equal(DamaState.Ready, recovered);
    }

    [Fact]
    public void ReceivingCarrierFromReadyGoesToRxAndBackWhenItStops()
    {
        var sm = ReadyStateMachine();

        var rx = sm.Update(true, true, 10, pttPressed: false, receivingCarrier: true, 100_000);
        Assert.Equal(DamaState.Rx, rx);

        var backToReady = sm.Update(true, true, 10, pttPressed: false, receivingCarrier: false, 100_100);
        Assert.Equal(DamaState.Ready, backToReady);
    }

    [Fact]
    public void LosingLinkFromReadyGoesToLostSyncThenBackToSearching()
    {
        var sm = ReadyStateMachine();

        var lost = sm.Update(true, linkAvailable: false, -999, false, false, 100_000);
        Assert.Equal(DamaState.LostSync, lost);

        var searching = sm.Update(true, linkAvailable: true, 10, false, false, 100_100);
        Assert.Equal(DamaState.Searching, searching);
    }

    [Fact]
    public void LoginNoLongerReadyForcesOfflineFromAnyState()
    {
        var sm = ReadyStateMachine();
        var state = sm.Update(loginReady: false, linkAvailable: true, 10, true, true, 999_999);
        Assert.Equal(DamaState.Offline, state);
    }

    [Fact]
    public void DoesNotBlockOrThrowAcrossManyRapidToggles()
    {
        var sm = new SatcomDamaStateMachine();
        for (var t = 0L; t < 50_000; t += 50)
        {
            var login = (t / 500) % 2 == 0;
            var link = (t / 300) % 2 == 0;
            var ptt = (t / 200) % 2 == 0;
            sm.Update(login, link, link ? 5 : -5, ptt, !ptt, t);
        }
        // No exception, no hang -- structural safety only; specific end state isn't asserted.
    }
}
