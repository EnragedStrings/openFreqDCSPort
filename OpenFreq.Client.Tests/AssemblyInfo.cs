using System.Runtime.CompilerServices;
using OpenFreq.Utilities;
using Xunit;

// Kept: one thread for the whole run also keeps any remaining Dispatcher.UIThread use on a single thread.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Avalonia's Dispatcher.UIThread is a process-wide singleton that binds to whichever thread first touches it, and
// this headless test host never pumps posted work. View-models that "run now if on the UI thread, else post"
// (UiThread.RunOrPost) therefore only took effect when the test's own thread happened to win that race -- async
// tests resume on thread-pool threads, so RadioOverlayViewModelTests failed in roughly 1 run in 3. Run that work
// inline for the whole test assembly instead, so results never depend on thread identity.
internal static class TestAssemblySetup
{
    [ModuleInitializer]
    internal static void RunUiWorkInline() => UiThread.RunInline = true;
}
