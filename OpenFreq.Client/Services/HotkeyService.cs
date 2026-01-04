using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenFreqClient.Services.Interfaces;
using SharpHook;
using SharpHook.Data;

namespace OpenFreqClient.Services;

public class HotkeyService : IHotkeyService
{
    private TaskPoolGlobalHook? _hook;
    private CancellationTokenSource? _cts;
    private Task? _hookTask;
    private bool _paused;

    private readonly Dictionary<KeyCode, List<Guid>> _bindings = new();
    private readonly HashSet<KeyCode> _pressedKeys = new();

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;
    public event EventHandler<HotkeyReleasedEventArgs>? HotkeyReleased;

    public void Start()
    {
        if (_hook != null) return;

        _cts = new CancellationTokenSource();
        _hook = new TaskPoolGlobalHook();
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;

        // Store the task so we can properly stop it
        _hookTask = _hook.RunAsync();
    }

    public void Stop()
    {
        if (_hook == null) return;

        // Cancel the hook
        _cts?.Cancel();

        // Unsubscribe from events
        _hook.KeyPressed -= OnKeyPressed;
        _hook.KeyReleased -= OnKeyReleased;

        // Dispose the hook (this stops it)
        _hook.Dispose();
        _hook = null;

        // Wait for task to complete (with timeout)
        try
        {
            _hookTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Task was cancelled, this is expected
        }

        _hookTask = null;
        _cts?.Dispose();
        _cts = null;

        _pressedKeys.Clear();
    }

    public void Pause()
    {
        _paused = true;
    }

    public void Resume()
    {
        _paused = false;
    }

    public void RegisterHotkey(KeyCode key, Guid channelId)
    {
        if (_bindings.TryGetValue(key, out var bindings))
        {
            if (bindings.Contains(channelId))
            {
                return;
            }

            bindings.Add(channelId);
        }
        else
        {
            _bindings[key] = new List<Guid> { channelId };
        }
    }

    public void UnregisterHotkey(KeyCode key, Guid channelId)
    {
        if (_bindings.TryGetValue(key, out var bindings))
        {
            bindings.Remove(channelId);
        }
    }

    public async Task<KeyCode> CaptureNextKeyAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<KeyCode>();

        void OnKeyCaptured(object? sender, KeyboardHookEventArgs e)
        {
            if (e.Data.KeyCode == KeyCode.VcEscape)
            {
                tcs.TrySetResult(KeyCode.VcUndefined);
            }
            tcs.TrySetResult(e.Data.KeyCode);
        }

        try
        {
            if (_hook == null)
            {
                throw new InvalidOperationException("Hotkey service is not started");
            }

            _hook.KeyPressed += OnKeyCaptured;

            // Wait for cancellation or key press
            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            if (_hook != null)
            {
                _hook.KeyPressed -= OnKeyCaptured;
            }
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (_paused) return;
        
        if (_pressedKeys.Contains(e.Data.KeyCode))
            return; // Already pressed

        _pressedKeys.Add(e.Data.KeyCode);

        if (_bindings.TryGetValue(e.Data.KeyCode, out var bindings))
        {
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(bindings));
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        _pressedKeys.Remove(e.Data.KeyCode);

        if (_bindings.TryGetValue(e.Data.KeyCode, out var binding))
        {
            HotkeyReleased?.Invoke(this, new HotkeyReleasedEventArgs(binding));
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

public class HotkeyBinding
{
    public KeyCode Key { get; }
    public Guid ChannelCard { get; }

    public HotkeyBinding(KeyCode key, Guid channelCard)
    {
        Key = key;
        ChannelCard = channelCard;
    }
}

public class HotkeyPressedEventArgs(List<Guid> channelIds) : EventArgs
{
    public List<Guid> ChannelIds { get; } = channelIds;
}

public class HotkeyReleasedEventArgs(List<Guid> channelIds) : EventArgs
{
    public List<Guid> ChannelIds { get; } = channelIds;
}