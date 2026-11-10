using System;
using System.Threading;

namespace OpenFreqClient.Services;

/// <summary>
/// One OpenFreq client per profile. The first instance creates the named mutex
/// "Local\OpenFreq.Client{profile}" and keeps the handle open for its lifetime (it never owns
/// it, so there is no thread affinity and nothing to abandon -- the OS frees it when the process
/// exits, which is also what the self-update relaunch waits for). A second launch finds the
/// mutex, asks the first instance to show its window (a named event, Windows only) and exits.
/// A launch from the DCS hook (--launched-by-dcs) exits without showing anything, so DCS keeps
/// focus.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _name;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

    private SingleInstanceGuard(Mutex mutex, string name)
    {
        _mutex = mutex;
        _name = name;
    }

    public static string NameFor(string profileSuffix) => @"Local\OpenFreq.Client" + profileSuffix;

    /// <summary>The guard if this is the first instance, otherwise null.</summary>
    public static SingleInstanceGuard? TryAcquire(string name)
    {
        var mutex = new Mutex(false, name, out var createdNew);
        if (createdNew)
            return new SingleInstanceGuard(mutex, name);
        mutex.Dispose();
        return null;
    }

    /// <summary>Asks the running instance to bring its window to the front.</summary>
    public static bool SignalShow(string name)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name + ".Show");
            return handle.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Calls <paramref name="onShow"/> (on a pool thread) whenever another launch
    /// signals. Windows only; a no-op elsewhere.</summary>
    public void ListenForShow(Action onShow)
    {
        if (!OperatingSystem.IsWindows() || _showEvent != null)
            return;
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _name + ".Show");
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => onShow(), null,
                Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            _showEvent?.Dispose();
            _showEvent = null;
        }
    }

    public void Dispose()
    {
        _showWait?.Unregister(null);
        _showWait = null;
        _showEvent?.Dispose();
        _showEvent = null;
        _mutex.Dispose();
    }
}
