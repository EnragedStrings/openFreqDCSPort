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

    // Unified binding storage with custom comparer
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
    private IntPtr _windowHandle;
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

        // Enumerate and acquire joystick devices
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

                    _logger.LogInformation("Acquired joystick: {DeviceName} (GUID: {Guid})",
                        deviceInstance.ProductName, deviceInstance.InstanceGuid);
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

        if (_joystickDevices.Count > 0)
        {
            // Start polling thread
            _pollingThread = new Thread(PollJoysticks)
            {
                IsBackground = true,
                Name = "DirectInput Polling Thread"
            };
            _pollingThread.Start();

            _logger.LogInformation("Started joystick polling thread for {Count} device(s)", _joystickDevices.Count);
        }
        else
        {
            _logger.LogWarning("No joystick devices found or acquired");
        }
    }

    private void PollJoysticks()
    {
        _logger.LogDebug("Joystick polling thread started");

        var lastRescan = DateTime.UtcNow;

        while (_cts is { Token.IsCancellationRequested: false })
        {
            try
            {
                // Re-enumerate every 5s to pick up replugged devices
                if ((DateTime.UtcNow - lastRescan).TotalSeconds >= 5)
                {
                    lastRescan = DateTime.UtcNow;
                    TryAcquireNewDevices();
                }

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
        if (_pttBindings.TryGetValue(binding, out var pttChannels))
        {
            if (!PttKeysPaused)
            {
                HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(
                    IHotkeyService.HotkeyType.Ptt, pttChannels));
            }
        }

        // Check squelch toggle bindings
        if (_squelchToggleBindings.TryGetValue(binding, out var squelchChannels))
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
        if (_pttBindings.TryGetValue(binding, out var pttChannels))
        {
            if (!PttKeysPaused)
            {
                HotkeyReleased?.Invoke(this, new HotkeyReleasedEventArgs(
                    IHotkeyService.HotkeyType.Ptt, pttChannels));
            }
        }

        // Check squelch toggle bindings
        if (_squelchToggleBindings.TryGetValue(binding, out var squelchChannels))
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
        // Stop polling thread
        _pollingThread?.Join(1000);
        _pollingThread = null;

        lock (_joystickLock)
        {
            // Release and dispose devices
            foreach (var device in _joystickDevices)
            {
                try
                {
                    device.Unacquire();
                    device.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error releasing joystick device");
                }
            }

            _joystickDevices.Clear();
            _previousJoystickStates.Clear();
            _deviceNames.Clear();
        }

        // Dispose DirectInput
        _directInput?.Dispose();
        _directInput = null;

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

    public void UnregisterHotkey(IHotkeyService.HotkeyType type, HotkeyBinding binding, Guid channelId)
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

    public void UnregisterHotkeys(IHotkeyService.HotkeyType type)
    {
        var bindings = GetBindingsDictionary(type);
        var count = bindings.Count;
        bindings.Clear();

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
        // Save original bindings
        var originalPttBindings = new Dictionary<HotkeyBinding, List<Guid>>(_pttBindings, new HotkeyBindingComparer());
        var originalSquelchBindings =
            new Dictionary<HotkeyBinding, List<Guid>>(_squelchToggleBindings, new HotkeyBindingComparer());

        // Create a mapping of unique capture IDs to bindings
        var captureIdToBinding = new Dictionary<Guid, JoystickButtonBinding>();

        List<IDirectInputDevice8> captureDevicesSnapshot;
        lock (_joystickLock) { captureDevicesSnapshot = _joystickDevices.ToList(); }

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
#endif
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
        if (_pttBindings.TryGetValue(binding, out var pttChannels))
        {
            if (!PttKeysPaused)
            {
                _activeKeyBindings[e.Data.KeyCode] = binding;
                HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(
                    IHotkeyService.HotkeyType.Ptt, pttChannels));
            }
        }

        // Check squelch toggle bindings
        if (_squelchToggleBindings.TryGetValue(binding, out var squelchChannels))
        {
            _activeKeyBindings[e.Data.KeyCode] = binding;
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(
                IHotkeyService.HotkeyType.SquelchToggle, squelchChannels));
        }

        // Check overlay toggle bindings
        if (_toggleOverlayBindings.TryGetValue(binding, out var overlayChannels))
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
        if (_pttBindings.TryGetValue(binding, out var pttChannels))
        {
            if (!PttKeysPaused)
            {
                HotkeyReleased?.Invoke(this, new HotkeyReleasedEventArgs(
                    IHotkeyService.HotkeyType.Ptt, pttChannels));
            }
        }

        // Check squelch toggle bindings
        if (_squelchToggleBindings.TryGetValue(binding, out var squelchChannels))
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
