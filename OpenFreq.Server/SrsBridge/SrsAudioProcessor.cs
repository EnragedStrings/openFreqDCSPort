using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreqAudio;

namespace OpenFreqServer.SrsBridge;

/// <summary>
/// Headless per-(SRS receiver, source OpenFreq peer) DSP pipeline for the OpenFreq->SRS leg: runs
/// decoded PCM through the same pure DSP primitives (RadioEffect fading/ambient,
/// KySecureReceiveState's KY-58 COMSEC state machine, ciphertext texture) a real OpenFreq client
/// already applies locally on receive. A stock SRS client can't run any of that itself, so the
/// server bakes it into the waveform before sending -- see docs/SRS_BRIDGE.md.
///
/// One instance per (receiving SRS client, transmitting OpenFreq peer) pair, kept for the life of
/// that pairing so RadioEffect's fading timers and KySecureReceiveState's phase machine carry
/// continuous state across packets, matching how a real receiving slot's DSP chain persists for
/// as long as it's tuned rather than resetting every buffer.
///
/// Fading is currently always "clear" (RadioEffect.Params is seeded from
/// FastPathAudioSim.GetDefaultAudioParams, whose DropoutRate/DeepFadeRate are both 0, so
/// RadioEffect.Process's fading stage never actually triggers). Real geometric distance-based
/// signal quality is a later pass (SrsSignalQuality.cs) -- only the encryption/COMSEC chain is
/// fully live here, since unlike fading it doesn't depend on signal quality at all. Once that
/// later pass lands, updating RadioEffect.Params with real values is the only change needed here;
/// the chain itself is already wired correctly.
/// </summary>
public sealed class SrsAudioProcessor
{
    private const int SampleRate = OpenFreqRtcClient.SAMPLE_RATE;

    // Matches RadioPlayback's own KY-58 wrong-key beep tone exactly (RadioPlayback.cs) so this
    // sounds like the same radio, not a different approximation.
    private const double WrongKeyBeepFrequencyHz = 900.0;
    private const float WrongKeyBeepAmplitude = 0.6f;

    private readonly RadioEffect _radioEffect;
    private KySecureReceiveState _kySecureState;
    private readonly CipherTextNoiseGenerator _cipherNoise;
    private readonly CvsdColorationEffect _cvsdColoration;
    private readonly ILogger _logger;
    private long _sampleClock;
    private KySecureOutcome? _lastLoggedOutcome;

    public SrsAudioProcessor(ILogger logger)
    {
        _radioEffect = new RadioEffect(SampleRate, 1, FastPathAudioSim.GetDefaultAudioParams(0), logger);
        _kySecureState = new KySecureReceiveState(SampleRate);
        _cipherNoise = new CipherTextNoiseGenerator(SampleRate);
        _cvsdColoration = new CvsdColorationEffect();
        _logger = logger;
    }

    /// <summary>Reset the KY-58/COMSEC phase machine at the start of a new talk-spurt. This
    /// processor is only driven when a packet actually arrives, and Process always passes
    /// carrierPresent: true -- it never gets a "carrier dropped" tick between separate PTT
    /// transmissions to reset itself on. Without this, a transmission that ended mid wrong-key
    /// beep/noise-burst/mute sequence left Phase.Muted latched forever, silencing every later
    /// transmission from this peer -- including perfectly clear ones -- while ambient
    /// noise/radio effects kept playing normally (voice gone, everything else still audible).
    /// RadioEffect's own fading/ambient state deliberately does NOT reset here -- see the class
    /// doc comment on why that one is meant to persist for the life of the pairing.</summary>
    public void ResetForNewTalkSpurt()
    {
        _kySecureState = new KySecureReceiveState(SampleRate);
        _lastLoggedOutcome = null;
    }

    /// <summary>Processes one buffer of received PCM for this (receiver, source) pairing.
    /// slotEnc/slotEncKey describe the SRS receiver's own radio (which radio it's tuned to
    /// determines this); streamEnc/streamEncKey the transmitting OpenFreq peer's. ambientNoiseType
    /// is the transmitter's own cockpit acoustic environment, already carried on the wire with no
    /// signal-quality dependency, so it's wired in now rather than deferred.</summary>
    public short[] Process(ReadOnlySpan<short> pcmIn, bool slotEnc, int slotEncKey, bool streamEnc,
        int streamEncKey, AmbientNoiseType ambientNoiseType)
    {
        var (matchedCipher, wrongKey, passiveCiphertext) = KySecureReceiveState.Classify(
            slotEnc, slotEncKey, slotCryptoCapable: true, streamEnc, streamEncKey);

        var outcome = _kySecureState.Update(carrierPresent: true, matchedCipher, wrongKey, passiveCiphertext,
            pcmIn.Length);

        if (outcome != _lastLoggedOutcome)
        {
            _logger.LogInformation(
                "SrsAudioProcessor COMSEC: outcome={Outcome} slotEnc={SlotEnc} slotKey={SlotKey} streamEnc={StreamEnc} streamKey={StreamKey} matched={Matched} wrongKey={WrongKey} passiveCiphertext={Passive}",
                outcome, slotEnc, slotEncKey, streamEnc, streamEncKey, matchedCipher, wrongKey, passiveCiphertext);
            _lastLoggedOutcome = outcome;
        }

        var buffer = new float[pcmIn.Length];
        switch (outcome)
        {
            case KySecureOutcome.Pass:
                for (var i = 0; i < pcmIn.Length; i++) buffer[i] = pcmIn[i] / 32768f;
                if (matchedCipher)
                    for (var i = 0; i < buffer.Length; i++) buffer[i] = _cvsdColoration.Process(buffer[i]);
                break;

            case KySecureOutcome.NoiseBurst:
            case KySecureOutcome.PassiveCiphertext:
                for (var i = 0; i < buffer.Length; i++) buffer[i] = _cipherNoise.NextSample();
                break;

            case KySecureOutcome.Beep:
                for (var i = 0; i < buffer.Length; i++)
                    buffer[i] = (float)Math.Sin(2.0 * Math.PI * WrongKeyBeepFrequencyHz * (i + _sampleClock) / SampleRate)
                        * WrongKeyBeepAmplitude;
                break;

            case KySecureOutcome.Muted:
            case KySecureOutcome.Idle:
            default:
                // buffer stays zero-initialized -- silence.
                break;
        }

        _sampleClock += buffer.Length;

        _radioEffect.AmbientNoise = ambientNoiseType;
        _radioEffect.Process(buffer, 0, buffer.Length, ambientNoiseVolume: 1.0f);

        var outPcm = new short[buffer.Length];
        for (var i = 0; i < outPcm.Length; i++)
            outPcm[i] = (short)Math.Clamp(buffer[i] * 32768f, short.MinValue, short.MaxValue);
        return outPcm;
    }
}
