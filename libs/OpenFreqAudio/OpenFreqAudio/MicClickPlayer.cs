using System;

namespace OpenFreqAudio;

/// <summary>
/// Ownship mic click: a short one-shot sample at PTT key-down (pick-up) and key-up (put-down), heard in the
/// pilot's own headset and recorded on the capture (HUD tape / session recording). Not transmitted: receivers
/// already make their own key-up/down clicks (AGC catching the carrier, squelch closing).
/// <see cref="Trigger"/> may be called from any thread; <see cref="Render"/> runs on the DSP thread.
/// A new trigger restarts from the beginning of that edge's sample (a quick release cuts a pick-up short).
/// </summary>
public sealed class MicClickPlayer
{
    private readonly object _lock = new();
    private float[]? _keyDown;
    private float[]? _keyUp;
    private float[]? _playing;
    private int _position;

    /// <summary>Mono samples at the engine rate, [-1, 1]; null disables that edge.</summary>
    public void SetSamples(float[]? keyDown, float[]? keyUp)
    {
        lock (_lock)
        {
            _keyDown = keyDown;
            _keyUp = keyUp;
            _playing = null;
            _position = 0;
        }
    }

    /// <summary>A click is still being played out.</summary>
    public bool IsPlaying
    {
        get { lock (_lock) return _playing != null; }
    }

    /// <summary>Start the pick-up (<paramref name="keyDown"/> true) or put-down click.</summary>
    public void Trigger(bool keyDown)
    {
        lock (_lock)
        {
            _playing = keyDown ? _keyDown : _keyUp;
            _position = 0;
        }
    }

    /// <summary>Stop any click in progress (feature switched off).</summary>
    public void Stop()
    {
        lock (_lock) _playing = null;
    }

    /// <summary>
    /// Writes the next <c>destination.Length</c> samples of the click times <paramref name="gain"/> into
    /// <paramref name="destination"/> (zeros after the click ends). Returns how many samples carry the click,
    /// 0 when nothing is playing.
    /// </summary>
    public int Render(Span<float> destination, float gain)
    {
        lock (_lock)
        {
            if (_playing == null)
            {
                destination.Clear();
                return 0;
            }

            int n = Math.Min(_playing.Length - _position, destination.Length);
            for (int i = 0; i < n; i++) destination[i] = _playing[_position + i] * gain;
            destination[n..].Clear();
            _position += n;
            if (_position >= _playing.Length) _playing = null;
            return n;
        }
    }
}
