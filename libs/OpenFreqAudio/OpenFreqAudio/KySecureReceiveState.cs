namespace OpenFreqAudio;

/// <summary>What a receiving slot should render this DSP buffer, per the KY-58 COMSEC layer.</summary>
public enum KySecureOutcome
{
    /// <summary>No relevant transmission right now — nothing extra to render.</summary>
    Idle,

    /// <summary>Clear voice, or successfully decrypted cipher voice (colored via <see cref="CvsdColorationEffect"/> when cipher).</summary>
    Pass,

    /// <summary>KY-58 wrong-variable: brief acknowledgement beep.</summary>
    Beep,

    /// <summary>KY-58 wrong-variable: noise-burst phase (rendered via <see cref="CipherTextNoiseGenerator"/>).</summary>
    NoiseBurst,

    /// <summary>KY-58 wrong-variable: muted for the remainder of this transmission.</summary>
    Muted,

    /// <summary>Receiver has no crypto capability and is hearing an encrypted transmitter:
    /// continuous raw ciphertext texture for the whole transmission — no beep/mute phases, this
    /// is simply what an ordinary AM receiver would pick up.</summary>
    PassiveCiphertext
}

/// <summary>
/// Per-slot KY-58 receive state machine.
///
/// <c>Idle -&gt; Syncing -&gt; Beep -&gt; NoiseBurst -&gt; Muted -&gt; (reset when carrier drops)</c> for a
/// wrong crypto variable, or straight to <c>Valid</c> (renders as <see cref="KySecureOutcome.Pass"/>)
/// when the variable matches. This mirrors the documented KY-58 wrong-variable behavior (detect
/// RF, attempt sync, fail, indicate, mute) rather than an instant mute or continuous "encrypted
/// static." Phase durations are SIMULATION CHOICE — public documentation does not establish exact
/// KY-58 wrong-key timing.
/// </summary>
public sealed class KySecureReceiveState
{
    private enum Phase { Idle, Syncing, Valid, Beep, NoiseBurst, Muted, PassiveCiphertext }

    private readonly int _sampleRate;
    private Phase _phase = Phase.Idle;
    private int _samplesRemainingInPhase;

    public KySecureReceiveState(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    /// <summary>
    /// Classify a (receiving slot, transmitting stream) pair into the three COMSEC outcomes the
    /// state machine cares about. TX-clear/RX-clear is intentionally excluded here — the caller
    /// only invokes this once it already knows at least one side is encrypted.
    /// </summary>
    public static (bool matchedCipher, bool wrongKey, bool passiveCiphertext) Classify(
        bool slotEnc, int slotEncKey, bool slotCryptoCapable, bool streamEnc, int streamEncKey)
    {
        if (!streamEnc)
        {
            // TX clear, RX set to cipher: no documented distinction from the wrong-key case.
            return slotEnc ? (false, true, false) : (false, false, false);
        }

        if (!slotCryptoCapable)
            return (false, false, true);

        if (!slotEnc)
            return (false, true, false);

        return slotEncKey == streamEncKey ? (true, false, false) : (false, true, false);
    }

    /// <summary>Advance the state machine by one DSP buffer of <paramref name="frames"/> samples.</summary>
    public KySecureOutcome Update(bool carrierPresent, bool matchedCipher, bool wrongKey, bool passiveCiphertext,
        int frames)
    {
        if (!carrierPresent)
        {
            _phase = Phase.Idle;
            return KySecureOutcome.Idle;
        }

        switch (_phase)
        {
            case Phase.Idle:
                if (passiveCiphertext)
                {
                    _phase = Phase.PassiveCiphertext;
                    return KySecureOutcome.PassiveCiphertext;
                }
                if (wrongKey)
                {
                    _phase = Phase.Syncing;
                    _samplesRemainingInPhase = (int)(_sampleRate * 0.15); // SIMULATION CHOICE
                    return KySecureOutcome.Idle;
                }
                _phase = Phase.Valid;
                return KySecureOutcome.Pass;

            case Phase.Syncing:
                _samplesRemainingInPhase -= frames;
                if (_samplesRemainingInPhase > 0) return KySecureOutcome.Idle;
                _phase = Phase.Beep;
                _samplesRemainingInPhase = (int)(_sampleRate * 0.2); // SIMULATION CHOICE
                return KySecureOutcome.Beep;

            case Phase.Beep:
                _samplesRemainingInPhase -= frames;
                if (_samplesRemainingInPhase > 0) return KySecureOutcome.Beep;
                _phase = Phase.NoiseBurst;
                _samplesRemainingInPhase = (int)(_sampleRate * 0.3); // SIMULATION CHOICE
                return KySecureOutcome.NoiseBurst;

            case Phase.NoiseBurst:
                _samplesRemainingInPhase -= frames;
                if (_samplesRemainingInPhase > 0) return KySecureOutcome.NoiseBurst;
                _phase = Phase.Muted;
                return KySecureOutcome.Muted;

            case Phase.Muted:
                return KySecureOutcome.Muted;

            case Phase.PassiveCiphertext:
                if (passiveCiphertext) return KySecureOutcome.PassiveCiphertext;
                _phase = Phase.Idle;
                return KySecureOutcome.Idle;

            case Phase.Valid:
                if (!wrongKey && !passiveCiphertext) return KySecureOutcome.Pass;
                _phase = Phase.Idle;
                return KySecureOutcome.Idle;

            default:
                return KySecureOutcome.Idle;
        }
    }
}
