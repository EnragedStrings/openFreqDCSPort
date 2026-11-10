using System;
using Avalonia.Threading;

namespace OpenFreq.Utilities;

/// <summary>
/// "Run now if this is the UI thread, else post to it" — the pattern several view-models use to touch observable
/// collections bound to the UI.
/// </summary>
internal static class UiThread
{
    /// <summary>
    /// Test hook: run every action inline on the calling thread. Avalonia binds <see cref="Dispatcher.UIThread"/>
    /// to whichever thread first touches it, and the headless test host never pumps posted work, so without this
    /// a test sees its update only if its own thread happened to win that race (async tests resume on pool
    /// threads). Never set in the app.
    /// </summary>
    internal static bool RunInline { get; set; }

    public static void RunOrPost(Action action)
    {
        if (RunInline || Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
