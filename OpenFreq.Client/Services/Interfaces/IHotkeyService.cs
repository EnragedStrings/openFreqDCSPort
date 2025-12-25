using System;
using System.Threading;
using System.Threading.Tasks;
using SharpHook.Data;

namespace OpenFreqClient.Services.Interfaces;

/// <summary>
/// Service for managing global hotkey bindings and events
/// </summary>
public interface IHotkeyService : IDisposable
{
    // Events for hotkey press/release
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;
    event EventHandler<HotkeyReleasedEventArgs>? HotkeyReleased;
    
    // Lifecycle
    void Start();
    void Stop();
    
    // Binding management
    void RegisterHotkey(KeyCode key, Guid channelId);
    void UnregisterHotkey(KeyCode key, Guid channelId);
    
    // Capture
    Task<KeyCode> CaptureNextKeyAsync(CancellationToken cancellationToken = default);
}