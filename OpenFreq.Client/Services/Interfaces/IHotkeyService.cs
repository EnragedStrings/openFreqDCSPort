using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenFreq.Client.Models;
using OpenFreq.Client.Services.Interfaces;

namespace OpenFreqClient.Services.Interfaces;

/// <summary>
/// Service for managing global hotkey bindings and events
/// Supports both keyboard (cross-platform) and joystick (Windows-only) bindings
/// </summary>
public interface IHotkeyService : IDisposable, ILifecycleService
{
    /// <summary>Sentinel "channel id" used to register the global PTT keybind (transmits on
    /// whichever channel is currently selected -- see LocationViewModel.SelectedChannel/
    /// SelectChannel) instead of one specific channel. Never a real ChannelCardViewModel.Id.</summary>
    public static readonly Guid GlobalPttChannelId = new("11111111-1111-1111-1111-111111111111");

    // Events for hotkey press/release
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;
    event EventHandler<HotkeyReleasedEventArgs>? HotkeyReleased;

    void PausePttKeys();
    void ResumePttKeys();

    bool PttKeysPaused { get; }

    void RegisterHotkey(HotkeyType type, HotkeyBinding binding, Guid channelId);
    void UnregisterHotkey(HotkeyType type, HotkeyBinding binding, Guid channelId);
    void UnregisterHotkeys(HotkeyType type);

    Task<HotkeyBinding?> CaptureNextHotkeyAsync(CancellationToken cancellationToken = default);

    /// <summary>Forces an immediate, full re-enumeration of joystick/HOTAS devices instead of
    /// waiting on the background poll (see HotkeyService.PollJoysticks' 5s TryAcquireNewDevices
    /// cadence) -- a manual escape hatch for when a device doesn't get picked back up on its own
    /// (e.g. after being unplugged/replugged, or acquired by another app and released), the same
    /// role SRS's device refresh serves. No-op returning 0 on non-Windows, where there's no
    /// joystick support at all. Returns the number of joysticks acquired after the rescan.</summary>
    int RescanInputDevices();

#if WINDOWS
    // Joystick-specific methods (Windows only)
    List<JoystickDeviceInfo> GetAvailableJoysticks();
    bool IsJoystickConnected(Guid deviceInstanceGuid);

    /// <summary>Rebinds DirectInput's cooperative-level window to <paramref name="windowHandle"/>,
    /// re-acquiring every currently-held device against it. Start() runs before the app's
    /// MainWindow exists, so it has to fall back to a placeholder handle; call this once the real
    /// window is available (see App.axaml.cs) so devices aren't left anchored to that fallback for
    /// the life of the process.</summary>
    void AttachWindow(IntPtr windowHandle);
#endif

    public enum HotkeyType
    {
        Ptt, // used for PTT
        SquelchToggle, // toggle squelch on/off
        ToggleOverlay // show/hide the radio overlay window (app-scoped, not per-channel)
    }
}
