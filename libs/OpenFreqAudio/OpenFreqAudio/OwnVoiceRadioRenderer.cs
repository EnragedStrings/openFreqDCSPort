using Microsoft.Extensions.Logging;

// ReSharper disable InconsistentNaming

namespace OpenFreqAudio;

/// <summary>
/// Renders our own microphone audio for the session recording and the HUD Recorder feed: the pilot's own voice in
/// the cockpit, through the transmitter-side acoustics of <see cref="RadioEffect"/> (cockpit ambience, gain stage,
/// oxygen mask / headset / room) and nothing else. No radio effects: no band-pass, AM envelope, background noise, AGC
/// or squelch -- this is the pilot's own mask on the tape, not the radio. (It used to add each receiver's band-pass,
/// upstream 102796d; removed so own voice and the HUD-tape hot mic sound like the same mask.)
///
/// Driven once per DSP buffer for the WHOLE recording (even when we are not transmitting).
/// The ambient SFX run continuously, so the recording has the cockpit sound between
/// transmissions too. <see cref="Process"/> is DSP thread only; the properties may be set
/// from any thread.
/// </summary>
public sealed class OwnVoiceRadioRenderer
{
    private readonly RadioEffect _effect;

    public OwnVoiceRadioRenderer(int sampleRate, ILogger logger)
    {
        _effect = new RadioEffect(sampleRate, 1, logger);
    }

    /// <summary>Transmitter-acoustics SFX layer (cockpit, mask, …). Thread-safe.</summary>
    public AmbientNoiseType AmbientNoise
    {
        get => _effect.AmbientNoise;
        set => _effect.AmbientNoise = value;
    }

    /// <summary>
    /// Render one DSP buffer of <paramref name="frames"/> samples of own-voice audio (zero where
    /// we have none) into <paramref name="buffer"/> in place.
    /// <paramref name="sfxVolume"/> (0..1) wet/dry-blends the ambient SFX.
    /// </summary>
    public void Process(float[] buffer, int frames, float sfxVolume)
    {
        _effect.Process(buffer, 0, frames, sfxVolume);
    }
}
