using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Models;
using OpenFreqClient.Services.Interfaces;
using SharpHook;
using SharpHook.Data;

#if WINDOWS
using Vortice.DirectInput;
#endif

namespace OpenFreqClient.Services;

public class HotkeyService : IHotkeyService
{
    private readonly ILogger<HotkeyService> _logger;

    // Keyboard handling (cross-platform via SharpHook)
    private TaskPoolGlobalHook? _hook;
    private CancellationTokenSource? _cts;
    private Task? _hookTask;
    private readonly HashSet<KeyCode> _pressedKeys = [];
    private readonly Dictionary<KeyCode, HotkeyBinding> _activeKeyBindings = new();
    private bool _isCapturing;

    // Unified binding storage with custom comparer. Read from the keyboard hook's callback
    // thread and (on Windows) the joystick polling thread, and written from whatever thread
    // calls RegisterHotkey/UnregisterHotkey/CaptureNextHotkeyAsync (normally the UI thread) --
    // plain Dictionary isn't safe for that, so every access goes through _bindingsLock. This
    // used to be unsynchronized: CaptureNextHotkeyAsync rewrites _pttBindings with one entry per
    // joystick button (30+ for a HOTAS stick) in a tight loop while the polling thread is
    // concurrently calling TryGetValue on it 50x/sec, which can silently corrupt lookups on a
    // plain Dictionary -- a very plausible reason a real button press sometimes just isn't seen
    // during capture, with no exception or log line to show for it.
    private readonly object _bindingsLock = new();
    private readonly Dictionary<HotkeyBinding, List<Guid>> _pttBindings = new(new HotkeyBindingComparer());
    private readonly Dictionary<HotkeyBinding, List<Guid>> _squelchToggleBindings = new(new HotkeyBindingComparer());
    private readonly Dictionary<HotkeyBinding, List<Guid>> _toggleOverlayBindings = new(new HotkeyBindingComparer());

#if WINDOWS
    // DirectInput for joystick support (Windows only). Guarded by _joystickLock since
    // PollJoysticks runs on its own thread while AttachWindow/RegisterHotkey/etc. can be called
    // from the UI thread concurrently.
    private readonly object _joystickLock = new();
    private IDirectInput8? _directInput;
    private readonly List<IDirectInputDevice8> _joystickDevices = [];
    private readonly Dictionary<Guid, JoystickState> _previousJoystickStates = [];
    private readonly Dictionary<Guid, string> _deviceNames = [];
    private Thread? _pollingThread;
    private Thread? _enumerationThread;
    private IntPtr _windowHandle;

    /// <summary>Handoff for a pending RescanInputDevices() request. The polling thread is the
    /// sole owner of _directInput/_joystickDevices while it's alive (see PollJoysticks) -- a
    /// rescan must be carried out BY that thread, not by whoever called RescanInputDevices(),
    /// which used to dispose devices out from under the still-running polling thread and race
    /// its own native Poll()/Acquire() calls (that's what made rescans -- and Stop() -- take up
    /// to tens of seconds and could leave a device silently stuck returning stale state until
    /// the whole process was restarted). Set via Interlocked.CompareExchange by the requester,
    /// consumed via Interlocked.Exchange by the polling thread.</summary>
    private TaskCompletionSource<int>? _pendingRescan;
#endif

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;
    public event EventHandler<HotkeyReleasedEventArgs>? HotkeyReleased;
    public bool PttKeysPaused { get; private set; }

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        if (_hook != null) return;

        _cts = new CancellationTokenSource();

        // Initialize keyboard hook (cross-platform)
        _hook = new TaskPoolGlobalHook();
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;
        _hookTask = _hook.RunAsync();

        _logger.LogInformation("HotkeyService started (keyboard support enabled)");

#if WINDOWS
        // Initialize DirectInput for joystick support (Windows only)
        try
        {
            InitializeDirectInput();
            _logger.LogInformation("DirectInput initialized - joystick support enabled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize DirectInput - joystick support disabled");
        }
#endif
    }

    public void Stop()
    {
        if (_hook == null) return;

        // Cancel operations
        _cts?.Cancel();

#if WINDOWS
        // Stop DirectInput first
        StopDirectInput();
#endif

        // Stop keyboard hook
        _hook.KeyPressed -= OnKeyPressed;
        _hook.KeyReleased -= OnKeyReleased;
        _hook.Dispose();
        _hook = null;

        try
        {
            _hookTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Task was cancelled, expected
        }

        _hookTask = null;
        _cts?.Dispose();
        _cts = null;

        _pressedKeys.Clear();

        _logger.LogInformation("HotkeyService stopped");
    }

#if WINDOWS
    private void InitializeDirectInput()
    {
        // Get window handle - try to get from main window, fallback to desktop. Start() runs
        // before App.axaml.cs assigns desktop.MainWindow, so mainWindow is always null here and
        // this always falls through to GetDesktopWindow() -- a window owned by explorer.exe, not
        // this process. That's fine as a temporary placeholder (DirectInput needs *a* window to
        // set the cooperative level against before it will acquire anything), but every device
        // acquired against it must get rebound once the real window exists, via AttachWindow --
        // otherwise DirectInput's background/focus-tracking state stays anchored to a window that
        // outlives this app's process, which is what caused bindings to require a full Windows
        // restart (not just an app restart) to recover.
        try
        {
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (mainWindow != null)
            {
                _windowHandle = mainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            }
        }
        catch
        {
            _windowHandle = IntPtr.Zero;
        }

        // Fallback: use desktop window
        if (_windowHandle == IntPtr.Zero)
        {
            _windowHandle = GetDesktopWindow();
        }

        // Create DirectInput instance
        _directInput = DInput.DirectInput8Create();

        AcquireAttachedDevices();

        if (_joystickDevices.Count > 0)
        {
            // Start polling thread
            _pollingThread = new Thread(PollJoysticks)
            {
                IsBackground = true,
                Name = "DirectInput Polling Thread"
            };
            _pollingThread.Start();

            // Separate thread for periodic re-enumeration (see EnumerateNewDevicesLoop) so a
            // slow GetDevices()/Acquire() call never blocks reading button/POV state from
            // devices already acquired.
            _enumerationThread = new Thread(EnumerateNewDevicesLoop)
            {
                IsBackground = true,
                Name = "DirectInput Enumeration Thread"
            };
            _enumerationThread.Start();

            _logger.LogInformation("Started joystick polling thread for {Count} device(s)", _joystickDevices.Count);
        }
        else
        {
            _logger.LogWarning("No joystick devices found or acquired");
        }
    }

    /// <summary>Enumerates attached GameControl devices and acquires each one not already held,
    /// adding it to _joystickDevices/_previousJoystickStates/_deviceNames. Assumes _directInput
    /// is already set. Only ever safe to call from InitializeDirectInput() (before any polling
    /// thread exists) or from the polling thread itself (see PerformFullReacquire) -- never from
    /// an external caller while the polling thread is alive, since IDirectInputDevice8 objects
    /// aren't safe to touch from two threads at once.</summary>
    private void AcquireAttachedDevices()
    {
        if (_directInput == null) return;

        var devices = _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);

        foreach (var deviceInstance in devices)
        {
            try
            {
                var device = _directInput.CreateDevice(deviceInstance.InstanceGuid);

                // Set data format to joystick
                device.SetDataFormat<RawJoystickState>();

                // CRITICAL: Background + NonExclusive for background reading
                device.SetCooperativeLevel(
                    _windowHandle,
                    CooperativeLevel.Background | CooperativeLevel.NonExclusive);

                // Acquire the device
                var result = device.Acquire();

                if (result.Success)
                {
                    lock (_joystickLock)
                    {
                        _joystickDevices.Add(device);
                        _previousJoystickStates[deviceInstance.InstanceGuid] = new JoystickState();
                        _deviceNames[deviceInstance.InstanceGuid] = deviceInstance.ProductName;
                    }

                    _logger.LogInformation(
                        "Acquired joystick: {DeviceName} (GUID: {Guid}, Buttons: {ButtonCount}, POVs: {PovCount}, Axes: {AxeCount})",
                        deviceInstance.ProductName, deviceInstance.InstanceGuid,
                        device.Capabilities.ButtonCount, device.Capabilities.PovCount, device.Capabilities.AxeCount);
                }
                else
                {
                    _logger.LogWarning("Failed to acquire joystick: {DeviceName} - {Result}",
                        deviceInstance.ProductName, result);
                    device.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acquiring joystick: {DeviceName}", deviceInstance.ProductName);
            }
        }
    }

    /// <summary>Runs TryAcquireNewDevices() on its own schedule, on its own thread, completely
    /// independent of PollJoysticks. This used to be a periodic check inline in PollJoysticks'
    /// own loop -- but DirectInput's device enumeration (GetDevices/CreateDevice/Acquire) can
    /// itself block for many seconds on this hardware (measured at ~30s more than once), and
    /// with enumeration and button/POV polling sharing one thread, every such stall froze ALL
    /// input reading, not just the enumeration -- silently swallowing any quick press-and-release
    /// that happened to land entirely inside the stall (currentState and previousState both read
    /// "not pressed" once the loop finally resumed, so no transition was ever seen). Splitting
    /// enumeration onto its own thread means a slow GetDevices() call only delays discovering new
    /// devices, and never blocks reading state from devices already acquired.</summary>
    private void EnumerateNewDevicesLoop()
    {
        _logger.LogDebug("Joystick enumeration thread started");

        var lastEnumeration = DateTime.UtcNow;

        while (_cts is { Token.IsCancellationRequested: false })
        {
            try
            {
                var pendingRescan = Interlocked.Exchange(ref _pendingRescan, null);
                if (pendingRescan != null)
                {
                    _logger.LogInformation("Rescanning input devices...");
                    TryAcquireNewDevices();
                    lastEnumeration = DateTime.UtcNow;

                    int rescanCount;
                    lock (_joystickLock) { rescanCount = _joystickDevices.Count; }

                    _logger.LogInformation("Input device rescan complete - {Count} joystick(s) found", rescanCount);
                    pendingRescan.TrySetResult(rescanCount);
                }
                else if ((DateTime.UtcNow - lastEnumeration).TotalSeconds >= 5)
                {
                    lastEnumeration = DateTime.UtcNow;
                    TryAcquireNewDevices();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during periodic device enumeration");
            }

            Thread.Sleep(200);
        }

        _logger.LogDebug("Joystick enumeration thread stopped");
    }

    private void PollJoysticks()
    {
        _logger.LogDebug("Joystick polling thread started");

        while (_cts is { Token.IsCancellationRequested: false })
        {
            try
            {
                List<IDirectInputDevice8> devicesSnapshot;
                lock (_joystickLock) { devicesSnapshot = _joystickDevices.ToList(); }

                foreach (var device in devicesSnapshot)
                {
                    try
                    {
                        // Poll the device
                        device.Poll();

                        // Get current state
                        var currentState = device.GetCurrentState<JoystickState, RawJoystickState, JoystickUpdate>();

                        var deviceGuid = device.DeviceInfo.InstanceGuid; // Get GUID from device

                        JoystickState? previousState;
                        lock (_joystickLock)
                        {
                            if (!_previousJoystickStates.TryGetValue(deviceGuid, out previousState))
                            {
                                _previousJoystickStates[deviceGuid] = currentState;
                                continue;
                            }
                        }

                        // Compare button states
                        for (var i = 0; i < currentState.Buttons.Length && i < previousState.Buttons.Length; i++)
                        {
                            var currentPressed = currentState.Buttons[i];
                            var previousPressed = previousState.Buttons[i];

                            switch (currentPressed)
                            {
                                // Button pressed (false -> true)
                                case true when !previousPressed:
                                    OnJoystickButtonPressed(deviceGuid, i);
                                    break;
                                // Button released (true -> false)
                                case false when previousPressed:
                                    OnJoystickButtonReleased(deviceGuid, i);
                                    break;
                            }
                        }

                        // Log POV/hat-switch changes too -- not wired into any binding yet, but
                        // if a "button" a user is pressing is actually a POV/hat direction (some
                        // HOTAS coolie/castle switches report as a POV rather than a Buttons[]
                        // entry) the button-only comparison above would never see it at all, so
                        // this is here purely to make that visible in the log.
                        for (var p = 0; p < currentState.PointOfViewControllers.Length && p < previousState.PointOfViewControllers.Length; p++)
                        {
                            if (currentState.PointOfViewControllers[p] != previousState.PointOfViewControllers[p])
                            {
                                _logger.LogDebug("Joystick POV {Index} changed: {DeviceName} - {Previous} -> {Current}",
                                    p, GetDeviceName(deviceGuid), previousState.PointOfViewControllers[p], currentState.PointOfViewControllers[p]);
                            }
                        }

                        // Update previous state
                        lock (_joystickLock) { _previousJoystickStates[deviceGuid] = currentState; }
                    }
                    catch (SharpGen.Runtime.SharpGenException ex) when (ex.HResult == unchecked((int)0x8007001E))
                    {
                        // DIERR_INPUTLOST: access to the device was temporarily lost -- this fires
                        // on entirely routine events (focus change, a UAC prompt, lock screen,
                        // sleep/resume, another app briefly taking exclusive input), NOT just on a
                        // real unplug. Per Microsoft's own guidance the correct response is simply
                        // to reacquire; only evict the device if that reacquire itself fails, which
                        // is the actual signal it's gone. Treating every INPUTLOST as a removal
                        // (as this used to) tore the device down on routine events far more often
                        // than real disconnects, and since those devices only ever got rebuilt
                        // against the same fallback window (see InitializeDirectInput/AttachWindow),
                        // repeated churn is what left bindings dead until a full Windows restart.
                        var guid = device.DeviceInfo.InstanceGuid;
                        var name = GetDeviceName(guid);
                        try
                        {
                            var reacquire = device.Acquire();
                            if (reacquire.Success)
                            {
                                _logger.LogDebug("Reacquired joystick after temporary input loss: {DeviceName} ({Guid})", name, guid);
                            }
                            else
                            {
                                _logger.LogInformation("Joystick disconnected: {DeviceName} ({Guid}), removing", name, guid);
                                EvictDevice(device, guid);
                            }
                        }
                        catch (Exception reacquireEx)
                        {
                            _logger.LogInformation(reacquireEx, "Joystick disconnected: {DeviceName} ({Guid}), removing", name, guid);
                            EvictDevice(device, guid);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error polling joystick {Guid}", device.DeviceInfo.InstanceGuid);
                    }
                }

                Thread.Sleep(20); // 50Hz polling rate
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in joystick polling loop");
                Thread.Sleep(100); // Back off on error
            }
        }

        // This thread is the sole owner of _directInput/_joystickDevices while it's alive (see
        // PerformFullReacquire/AcquireAttachedDevices) -- release everything here, on the way
        // out, instead of letting StopDirectInput reach in from the calling thread while this
        // thread might still be mid-Poll()/mid-Acquire(). That cross-thread teardown used to be
        // exactly what corrupted device state until the whole process was restarted.
        lock (_joystickLock)
        {
            foreach (var device in _joystickDevices)
            {
                try { device.Unacquire(); device.Dispose(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Error releasing joystick device"); }
            }

            _joystickDevices.Clear();
            _previousJoystickStates.Clear();
            _deviceNames.Clear();
        }

        _directInput?.Dispose();
        _directInput = null;

        // Fail any rescan that snuck in right as we were cancelled, rather than leaving its
        // caller blocked until its own timeout.
        Interlocked.Exchange(ref _pendingRescan, null)?.TrySetResult(0);

        _logger.LogDebug("Joystick polling thread stopped");
    }

    private string GetDeviceName(Guid deviceGuid)
    {
        lock (_joystickLock)
        {
            return _deviceNames.GetValueOrDefault(deviceGuid, deviceGuid.ToString());
        }
    }

    /// <summary>Removes a device that's actually gone (a reacquire attempt itself failed), as
    /// opposed to a routine, recoverable DIERR_INPUTLOST -- see the catch block in
    /// PollJoysticks that calls this.</summary>
    private void EvictDevice(IDirectInputDevice8 device, Guid guid)
    {
        lock (_joystickLock)
        {
            _joystickDevices.Remove(device);
            _previousJoystickStates.Remove(guid);
            _deviceNames.Remove(guid);
        }
        try { device.Unacquire(); device.Dispose(); } catch { /* don't care */ }
    }

    private void OnJoystickButtonPressed(Guid deviceGuid, int buttonIndex)
    {
        var deviceName = GetDeviceName(deviceGuid);

        var binding = new JoystickButtonBinding(deviceGuid, deviceName, buttonIndex);

        _logger.LogDebug("Joystick button pressed: {Binding}", binding.DisplayName);

        // Check PTT bindings
        if (TryGetChannels(_pttBindings, binding) is { } pttChannels)
        {
            if (!PttKeysPaused)
            {
                HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(
                    IHotkeyService.HotkeyType.Ptt, pttChannels));
            }
        }

        // Check squelch toggle bindings
        if (TryGetChannels(_squelchToggleBindings, binding) is { } squelchChannels)
        {
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(
                IHotkeyService.HotkeyType.SquelchToggle, squelchChannels));
        }
    }

    private void OnJoystickButtonReleased(Guid deviceGuid, int buttonIndex)
    {
        var deviceName = GetDeviceName(deviceGuid);

        var binding = new JoystickButtonBinding(deviceGuid, deviceName, buttonIndex);

        _logger.LogDebug("Joystick button released: {Binding}", binding.DisplayName);

        // Check PTT bindings
        if (TryGetChannels(_pttBindings, binding) is { } pttChannels)
        {
            if (!PttKeysPaused)
            {
                HotkeyReleased?.Invoke(this, new HotkeyReleasedEventArgs(
                    IHotkeyService.HotkeyType.Ptt, pttChannels));
            }
        }

        // Check squelch toggle bindings
        if (TryGetChannels(_squelchToggleBindings, binding) is { } squelchChannels)
        {
            HotkeyReleased?.Invoke(this, new HotkeyReleasedEventArgs(
                IHotkeyService.HotkeyType.SquelchToggle, squelchChannels));
        }
    }

    private void TryAcquireNewDevices()
    {
        if (_directInput == null) return;
        try
        {
            HashSet<Guid> knownGuids;
            lock (_joystickLock) { knownGuids = new HashSet<Guid>(_joystickDevices.Select(d => d.DeviceInfo.InstanceGuid)); }

            var attached = _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
            foreach (var deviceInstance in attached)
            {
                if (knownGuids.Contains(deviceInstance.InstanceGuid)) continue;
                try
                {
                    var device = _directInput.CreateDevice(deviceInstance.InstanceGuid);
                    device.SetDataFormat<RawJoystickState>();
                    device.SetCooperativeLevel(_windowHandle, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                    var result = device.Acquire();
                    if (result.Success)
                    {
                        lock (_joystickLock)
                        {
                            _joystickDevices.Add(device);
                            _previousJoystickStates[deviceInstance.InstanceGuid] = new JoystickState();
                            _deviceNames[deviceInstance.InstanceGuid] = deviceInstance.ProductName;
                        }
                        _logger.LogInformation("Joystick reconnected: {DeviceName} ({Guid})", deviceInstance.ProductName, deviceInstance.InstanceGuid);
                    }
                    else
                    {
                        device.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to re-acquire joystick {DeviceName}", deviceInstance.ProductName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during device rescan");
        }
    }

    private void StopDirectInput()
    {
        // The enumeration thread also touches _directInput (GetDevices/CreateDevice/Acquire) --
        // wait for it to notice cancellation before disposing anything out from under it. It
        // owns no state to release itself (only PollJoysticks does, see below), so a timeout
        // here just means proceeding with a background thread still possibly mid-call; log it
        // and continue rather than blocking shutdown indefinitely on it.
        if (_enumerationThread != null)
        {
            if (!_enumerationThread.Join(TimeSpan.FromSeconds(10)))
            {
                _logger.LogWarning("Joystick enumeration thread did not stop within 10s");
            }

            _enumerationThread = null;
        }

        if (_pollingThread != null)
        {
            // The polling thread releases _joystickDevices/_directInput itself right before it
            // returns (see the end of PollJoysticks) -- wait for that instead of disposing them
            // from here, which used to race the thread's own native Poll()/Acquire() calls (this
            // is the same class of bug RescanInputDevices had). Callers cancel _cts before
            // calling this, so the thread should notice quickly; 10s is a generous ceiling for a
            // slow native call to unwind, not the expected case.
            if (!_pollingThread.Join(TimeSpan.FromSeconds(10)))
            {
                _logger.LogWarning(
                    "Joystick polling thread did not stop within 10s -- leaving DirectInput state alone rather than risk disposing it out from under a still-running thread");
                return;
            }

            _pollingThread = null;
        }
        else if (_directInput != null)
        {
            // No polling thread was ever started (0 devices were acquired), so nothing else can
            // be touching DirectInput concurrently -- safe to release it directly.
            _directInput.Dispose();
            _directInput = null;
        }

        _logger.LogDebug("DirectInput stopped and cleaned up");
    }

    public void AttachWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || windowHandle == _windowHandle) return;

        var previousHandle = _windowHandle;
        _windowHandle = windowHandle;

        // Re-bind every currently-acquired device's cooperative level to the app's own window
        // instead of whatever placeholder Start() used (see InitializeDirectInput) -- otherwise
        // DirectInput's background/focus-tracking state for these devices stays anchored to a
        // window that outlives this process, and only a full Windows restart (which tears down
        // that window) clears it. New devices picked up later by TryAcquireNewDevices already use
        // the updated _windowHandle automatically.
        List<IDirectInputDevice8> devicesSnapshot;
        lock (_joystickLock) { devicesSnapshot = _joystickDevices.ToList(); }

        foreach (var device in devicesSnapshot)
        {
            try
            {
                device.Unacquire();
                device.SetCooperativeLevel(_windowHandle, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                device.Acquire();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rebind joystick {Guid} to app window", device.DeviceInfo.InstanceGuid);
            }
        }

        _logger.LogDebug("Rebound {Count} joystick device(s) from window {Previous:X} to app window {Current:X}",
            devicesSnapshot.Count, previousHandle.ToInt64(), windowHandle.ToInt64());
    }

    public List<JoystickDeviceInfo> GetAvailableJoysticks()
    {
        var result = new List<JoystickDeviceInfo>();

        if (_directInput == null)
        {
            return result;
        }

        try
        {
            var devices = _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);

            foreach (var deviceInstance in devices)
            {
                try
                {
                    // Try to get button count from capabilities
                    var device = _directInput.CreateDevice(deviceInstance.InstanceGuid);
                    var caps = device.Capabilities;

                    result.Add(new JoystickDeviceInfo
                    {
                        InstanceGuid = deviceInstance.InstanceGuid,
                        DeviceName = deviceInstance.InstanceName,
                        ProductName = deviceInstance.ProductName,
                        ButtonCount = caps.ButtonCount
                    });

                    device.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error getting joystick info for {DeviceName}", deviceInstance.ProductName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enumerating joystick devices");
        }

        return result;
    }

    public bool IsJoystickConnected(Guid deviceInstanceGuid)
    {
        lock (_joystickLock)
        {
            return _joystickDevices.Any(d => d.DeviceInfo.InstanceGuid == deviceInstanceGuid);
        }
    }

    // Win32 API import for getting desktop window handle
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
#endif

    public void PausePttKeys()
    {
        PttKeysPaused = true;
        _logger.LogDebug("PTT keys paused");
    }

    public void ResumePttKeys()
    {
        PttKeysPaused = false;
        _logger.LogDebug("PTT keys resumed");
    }

    // NEW: Unified binding registration
    public void RegisterHotkey(IHotkeyService.HotkeyType type, HotkeyBinding binding, Guid channelId)
    {
        lock (_bindingsLock)
        {
            var bindings = GetBindingsDictionary(type);

            if (bindings.TryGetValue(binding, out var channelList))
            {
                if (!channelList.Contains(channelId))
                {
                    channelList.Add(channelId);
                    _logger.LogDebug("Added channel {ChannelId} to existing {Type} binding: {Binding}",
                        channelId, type, binding.DisplayName);
                }
            }
            else
            {
                bindings[binding] = [channelId];
                _logger.LogInformation("Registered {Type} hotkey: {Binding} for channel {ChannelId}",
                    type, binding.DisplayName, channelId);
            }
        }
    }

    public void UnregisterHotkey(IHotkeyService.HotkeyType type, HotkeyBinding binding, Guid channelId)
    {
        lock (_bindingsLock)
        {
            var bindings = GetBindingsDictionary(type);

            if (bindings.TryGetValue(binding, out var channelList))
            {
                channelList.Remove(channelId);

                if (channelList.Count == 0)
                {
                    bindings.Remove(binding);
                }

                _logger.LogDebug("Unregistered {Type} hotkey: {Binding} for channel {ChannelId}",
                    type, binding.DisplayName, channelId);
            }
        }
    }

    public void UnregisterHotkeys(IHotkeyService.HotkeyType type)
    {
        int count;
        lock (_bindingsLock)
        {
            var bindings = GetBindingsDictionary(type);
            count = bindings.Count;
            bindings.Clear();
        }

        _logger.LogInformation("Unregistered all {Count} {Type} hotkeys", count, type);
    }

    // Unified capture (returns keyboard or joystick binding)
    public async Task<HotkeyBinding?> CaptureNextHotkeyAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<HotkeyBinding?>();

        var currentlyDown = new HashSet<KeyCode>();
        var pressOrder = new List<KeyCode>();

        void OnCaptureKeyPressed(object? sender, KeyboardHookEventArgs e)
        {
            var key = e.Data.KeyCode;

            if (key == KeyCode.VcEscape)
            {
                tcs.TrySetResult(null);
                return;
            }

            if (currentlyDown.Add(key) && !pressOrder.Contains(key))
                pressOrder.Add(key);
        }

        void OnCaptureKeyReleased(object? sender, KeyboardHookEventArgs e)
        {
            currentlyDown.Remove(e.Data.KeyCode);

            if (currentlyDown.Count != 0 || pressOrder.Count == 0)
                return;

            var nonModifiers = pressOrder.Where(k => !IsModifierKey(k)).ToList();
            KeyboardBinding binding;

            if (nonModifiers.Count > 0)
            {
                var primaryKey = nonModifiers.Last();
                binding = new KeyboardBinding(
                    primaryKey,
                    shift: pressOrder.Any(k => k is KeyCode.VcLeftShift or KeyCode.VcRightShift),
                    ctrl: pressOrder.Any(k => k is KeyCode.VcLeftControl or KeyCode.VcRightControl),
                    alt: pressOrder.Any(k => k is KeyCode.VcLeftAlt or KeyCode.VcRightAlt));
            }
            else
            {
                // Modifier-only combo, last pressed modifier becomes the primary key
                binding = new KeyboardBinding(pressOrder.Last());
            }

            tcs.TrySetResult(binding);
        }

#if WINDOWS
        // Create a mapping of unique capture IDs to bindings
        var captureIdToBinding = new Dictionary<Guid, JoystickButtonBinding>();

        List<IDirectInputDevice8> captureDevicesSnapshot;
        lock (_joystickLock) { captureDevicesSnapshot = _joystickDevices.ToList(); }

        // Save the original bindings and temporarily hijack _pttBindings so every joystick
        // button (not just ones already bound to something) reports as a PTT press while we're
        // capturing -- all under one lock, so the polling thread's concurrent TryGetValue calls
        // (see TryGetChannels) never see this dictionary half-written.
        Dictionary<HotkeyBinding, List<Guid>> originalPttBindings;
        Dictionary<HotkeyBinding, List<Guid>> originalSquelchBindings;
        lock (_bindingsLock)
        {
            originalPttBindings = new Dictionary<HotkeyBinding, List<Guid>>(_pttBindings, new HotkeyBindingComparer());
            originalSquelchBindings =
                new Dictionary<HotkeyBinding, List<Guid>>(_squelchToggleBindings, new HotkeyBindingComparer());

            foreach (var device in captureDevicesSnapshot)
            {
                var deviceGuid = device.DeviceInfo.InstanceGuid;
                var deviceName = GetDeviceName(deviceGuid);
                var buttonCount = device.Capabilities.ButtonCount;

                for (int i = 0; i < buttonCount; i++)
                {
                    var binding = new JoystickButtonBinding(deviceGuid, deviceName, i);
                    var uniqueCaptureId = Guid.NewGuid(); // UNIQUE ID per button
                    _pttBindings[binding] = [uniqueCaptureId];
                    captureIdToBinding[uniqueCaptureId] = binding; // Map ID -> binding
                }
            }
        }

        void OnJoystickCaptured(object? sender, HotkeyPressedEventArgs e)
        {
            if (e.Type == IHotkeyService.HotkeyType.Ptt)
            {
                // Look up which specific button was pressed by its unique ID
                foreach (var channelId in e.ChannelIds)
                {
                    if (captureIdToBinding.TryGetValue(channelId, out var binding))
                    {
                        tcs.TrySetResult(binding);
                        return;
                    }
                }
            }
        }

        HotkeyPressed += OnJoystickCaptured;
#endif

        try
        {
            if (_hook == null)
            {
                throw new InvalidOperationException("Hotkey service is not started");
            }

            _isCapturing = true;
            _hook.KeyPressed += OnCaptureKeyPressed;
            _hook.KeyReleased += OnCaptureKeyReleased;

            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            _isCapturing = false;
            if (_hook != null)
            {
                _hook.KeyPressed -= OnCaptureKeyPressed;
                _hook.KeyReleased -= OnCaptureKeyReleased;
            }

#if WINDOWS
            // Restore original bindings
            HotkeyPressed -= OnJoystickCaptured;
            lock (_bindingsLock)
            {
                _pttBindings.Clear();
                _squelchToggleBindings.Clear();

                foreach (var kvp in originalPttBindings)
                {
                    _pttBindings[kvp.Key] = kvp.Value;
                }

                foreach (var kvp in originalSquelchBindings)
                {
                    _squelchToggleBindings[kvp.Key] = kvp.Value;
                }
            }
#endif
        }
    }

    public int RescanInputDevices()
    {
#if WINDOWS
        if (_hook == null)
        {
            _logger.LogDebug("Ignoring input device rescan request - hotkey service not started");
            return 0;
        }

        if (_pollingThread == null)
        {
            // No polling thread is running (e.g. 0 joysticks were ever found at startup), so
            // nothing else can be touching DirectInput concurrently -- safe to reinitialize
            // directly from this thread. StopDirectInput() is a no-op beyond releasing any
            // existing _directInput handle in this state (there are no devices to release).
            _logger.LogInformation("Rescanning input devices...");
            try
            {
                StopDirectInput();
                InitializeDirectInput();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rescan input devices");
                return 0;
            }

            int directCount;
            lock (_joystickLock) { directCount = _joystickDevices.Count; }
            _logger.LogInformation("Input device rescan complete - {Count} joystick(s) found", directCount);
            return directCount;
        }

        // A polling thread already owns _directInput/_joystickDevices (see PollJoysticks) --
        // hand the rescan to IT via _pendingRescan instead of tearing objects down from this
        // thread, which used to race that thread's own native Poll()/Acquire() calls. That race
        // is what made a rescan (or the ordinary Stop() shutdown path) intermittently take tens
        // of seconds and could leave a device silently returning stale state until the whole
        // process was restarted -- see PollJoysticks/StopDirectInput for the full story.
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _pendingRescan, tcs, null) != null)
        {
            _logger.LogDebug("Ignoring input device rescan request - one is already in progress");
            return 0;
        }

        if (!tcs.Task.Wait(TimeSpan.FromSeconds(10)))
        {
            _logger.LogWarning("Input device rescan timed out after 10s");
            return 0;
        }

        return tcs.Task.Result;
#else
        return 0;
#endif
    }

    /// <summary>Thread-safe lookup into one of the binding dictionaries -- returns a snapshot
    /// copy of the channel list (never the live list) so callers can invoke events after
    /// releasing _bindingsLock without racing a concurrent Register/Unregister/capture. Used by
    /// the keyboard hook callback (cross-platform) and, on Windows, the joystick poll loop.</summary>
    private List<Guid>? TryGetChannels(Dictionary<HotkeyBinding, List<Guid>> bindings, HotkeyBinding binding)
    {
        lock (_bindingsLock)
        {
            return bindings.TryGetValue(binding, out var channels) ? new List<Guid>(channels) : null;
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (_isCapturing) return;
        if (_pressedKeys.Contains(e.Data.KeyCode))
            return; // Already pressed

        _pressedKeys.Add(e.Data.KeyCode);

        var key = e.Data.KeyCode;
        var mask = e.RawEvent.Mask;
        var binding = new KeyboardBinding(key,
            shift: (mask & EventMask.Shift) != EventMask.None && key is not (KeyCode.VcLeftShift or KeyCode.VcRightShift),
            ctrl: (mask & EventMask.Ctrl) != EventMask.None && key is not (KeyCode.VcLeftControl or KeyCode.VcRightControl),
            alt: (mask & EventMask.Alt) != EventMask.None && key is not (KeyCode.VcLeftAlt or KeyCode.VcRightAlt));

        // Check PTT bindings
        if (TryGetChannels(_pttBindings, binding) is { } pttChannels)
        {
            if (!PttKeysPaused)
            {
                _activeKeyBindings[e.Data.KeyCode] = binding;
                HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(
                    IHotkeyService.HotkeyType.Ptt, pttChannels));
            }
        }

        // Check squelch toggle bindings
        if (TryGetChannels(_squelchToggleBindings, binding) is { } squelchChannels)
        {
            _activeKeyBindings[e.Data.KeyCode] = binding;
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(
                IHotkeyService.HotkeyType.SquelchToggle, squelchChannels));
        }

        // Check overlay toggle bindings
        if (TryGetChannels(_toggleOverlayBindings, binding) is { } overlayChannels)
        {
            _activeKeyBindings[e.Data.KeyCode] = binding;
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(
                IHotkeyService.HotkeyType.ToggleOverlay, overlayChannels));
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (_isCapturing) return;
        _pressedKeys.Remove(e.Data.KeyCode);

        // Use the binding recorded at press time so modifier release order doesn't matter
        if (!_activeKeyBindings.Remove(e.Data.KeyCode, out var binding))
            return;

        // Check PTT bindings
        if (TryGetChannels(_pttBindings, binding) is { } pttChannels)
        {
            if (!PttKeysPaused)
            {
                HotkeyReleased?.Invoke(this, new HotkeyReleasedEventArgs(
                    IHotkeyService.HotkeyType.Ptt, pttChannels));
            }
        }

        // Check squelch toggle bindings
        if (TryGetChannels(_squelchToggleBindings, binding) is { } squelchChannels)
        {
            HotkeyReleased?.Invoke(this, new HotkeyReleasedEventArgs(
                IHotkeyService.HotkeyType.SquelchToggle, squelchChannels));
        }
    }

    private static bool IsModifierKey(KeyCode keyCode) => keyCode is
        KeyCode.VcLeftShift or KeyCode.VcRightShift or
        KeyCode.VcLeftControl or KeyCode.VcRightControl or
        KeyCode.VcLeftAlt or KeyCode.VcRightAlt or
        KeyCode.VcLeftMeta or KeyCode.VcRightMeta;

    private Dictionary<HotkeyBinding, List<Guid>> GetBindingsDictionary(IHotkeyService.HotkeyType type)
    {
        return type switch
        {
            IHotkeyService.HotkeyType.Ptt => _pttBindings,
            IHotkeyService.HotkeyType.SquelchToggle => _squelchToggleBindings,
            IHotkeyService.HotkeyType.ToggleOverlay => _toggleOverlayBindings,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    public void Dispose()
    {
        Stop();
    }
}

public class HotkeyPressedEventArgs(IHotkeyService.HotkeyType type, List<Guid> channelIds) : EventArgs
{
    public IHotkeyService.HotkeyType Type { get; } = type;
    public List<Guid> ChannelIds { get; } = channelIds;
}

public class HotkeyReleasedEventArgs(IHotkeyService.HotkeyType type, List<Guid> channelIds) : EventArgs
{
    public IHotkeyService.HotkeyType Type { get; } = type;
    public List<Guid> ChannelIds { get; } = channelIds;
}
