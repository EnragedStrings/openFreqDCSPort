using Xunit;

// RadioOverlayViewModelTests pass individually but were flaky as part of the full run: xUnit runs
// test classes in parallel on separate threads by default, and Avalonia's Dispatcher.UIThread is a
// process-wide singleton that binds to whichever thread first touches it. Once some other test
// class claims that thread, RadioOverlayViewModel's PostRebuildDisplayedChannels sees
// Dispatcher.UIThread.CheckAccess() return false on its own thread and posts to a dispatcher queue
// nothing in this headless test host ever pumps -- so the update silently never happens before the
// assertion runs. Disabling parallelization keeps every test class on one thread for the whole run,
// so there's no other thread to lose that race to. The suite is fast enough (well under a second)
// that running it sequentially instead of in parallel is not a real cost.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
