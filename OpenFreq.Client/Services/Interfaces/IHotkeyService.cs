using System;
using System.Threading;
using System.Threading.Tasks;
using OpenFreq.Client.Services.Interfaces;
using SharpHook.Data;

namespace OpenFreqClient.Services.Interfaces;

/// <summary>
/// Service for managing global hotkey bindings and events
/// </summary>
public interface IHotkeyService : IDisposable, ILifecycleService
{
    // Events for hotkey press/release
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;
    event EventHandler<HotkeyReleasedEventArgs>? HotkeyReleased;
    
    void Pause();
    void Resume();
    
    // Binding management
    void RegisterHotkey(KeyCode key, Guid channelId);
    void UnregisterHotkey(KeyCode key, Guid channelId);
    
    // Capture
    Task<KeyCode> CaptureNextKeyAsync(CancellationToken cancellationToken = default);
}