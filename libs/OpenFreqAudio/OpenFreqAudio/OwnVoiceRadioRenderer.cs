using Microsoft.Extensions.Logging;
using NWaves.Filters.Butterworth;

// ReSharper disable InconsistentNaming

namespace OpenFreqAudio;

/// <summary>
/// Renders our own microphone audio for the session recording (and the HUD Recorder feed) with
/// the same radio tone as the incoming audio, but without a simulated receiver (upstream 102796d):
///   1. RadioEffect: transmitter-side ambient SFX (cockpit, mask, …)
///   2. The band-pass of each receiver in <see cref="RadioPlayback"/>: ~300 Hz up to the
///      band-dependent high edge of the frequency we transmit on
///      (<see cref="FastPathAudioSim.RadioBandConfig.VoiceBandwidth_Hz"/>).
///
/// There is no AM envelope, background noise, AGC or squelch. This models a tape recording in our
/// own cockpit: at zero distance a receiver adds no audible noise to our voice. It only added a
/// loud pop at key-down (while the AGC caught the carrier) and a noise burst at key-up (before the
/// squelch closed).
///
/// Driven once per DSP buffer for the WHOLE recording (even when we are not transmitting).
/// The ambient SFX run continuously, so the recording has the cockpit sound between
/// transmissions too. <see cref="Process"/> is DSP thread only; the properties may be set
/// from any thread.
/// </summary>
public sealed class OwnVoiceRadioRenderer
{
    private readonly RadioEffect _effect;
    private readonly HighPassFilter _highPass;
    private LowPassFilter _lowPass;
    private readonly int _sampleRate;

    // The low-pass cutoff the filter was built for, and the one FrequencyKhz asks for. The filter is
    // only rebuilt on the DSP thread (in Process), so setting the frequency never races its state.
    private double _lowPassHz;
    private volatile int _frequencyKhz;

    public OwnVoiceRadioRenderer(int sampleRate, ILogger logger)
    {
        _sampleRate = sampleRate;
        _effect = new RadioEffect(sampleRate, 1, logger);
        _highPass = new HighPassFilter(300.0 / sampleRate, 3);
        _lowPassHz = 3000.0;
        _lowPass = new LowPassFilter(_lowPassHz / sampleRate, 6);
    }

    /// <summary>Transmitter-acoustics SFX layer (cockpit, mask, …). Thread-safe.</summary>
    public AmbientNoiseType AmbientNoise
    {
        get => _effect.AmbientNoise;
        set => _effect.AmbientNoise = value;
    }

    /// <summary>
    /// The frequency we transmit on, which sets the band-pass high edge the same way it is set for
    /// a receiver tuned there. 0 (unknown) keeps the current one. Thread-safe.
    /// </summary>
    public int FrequencyKhz
    {
        get => _frequencyKhz;
        set => _frequencyKhz = value;
    }

    /// <summary>
    /// Render one DSP buffer of <paramref name="frames"/> samples of own-voice audio (zero where
    /// we have none) into <paramref name="buffer"/> in place.
    /// <paramref name="sfxVolume"/> (0..1) wet/dry-blends the ambient SFX. The band-pass always
    /// runs, so own voice keeps the radio tone even with no SFX.
    /// </summary>
    public void Process(float[] buffer, int frames, float sfxVolume)
    {
        int khz = _frequencyKhz;
        if (khz > 0)
        {
            double wantHz = FastPathAudioSim.GetBandConfig(khz).VoiceBandwidth_Hz;
            if (wantHz != _lowPassHz)
            {
                _lowPassHz = wantHz;
                _lowPass = new LowPassFilter(wantHz / _sampleRate, 6);
            }
        }

        // Transmitter acoustics (ambient SFX)
        _effect.Process(buffer, 0, frames, sfxVolume);

        // Band-pass for the radio tone (run the filters every sample for continuity).
        for (int i = 0; i < frames; i++)
        {
            buffer[i] = _lowPass.Process(_highPass.Process(buffer[i]));
        }
    }
}
