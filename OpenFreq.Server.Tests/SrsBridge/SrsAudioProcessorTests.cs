using Microsoft.Extensions.Logging.Abstractions;
using OpenFreqAudio;
using OpenFreqServer.SrsBridge;

namespace OpenFreq.Server.Tests.SrsBridge;

/// <summary>
/// Covers the headless OpenFreq->SRS DSP chain (Phase 3): the encryption/COMSEC path, which is
/// fully live in this pass, and that fading stays a no-op absent real signal-quality input (that's
/// a later pass). See SrsAudioProcessor's own doc comment for the phasing rationale.
/// </summary>
public class SrsAudioProcessorTests
{
    private static short[] ToneBuffer(int samples, double freqHz = 440, int sampleRate = 48000)
    {
        var buf = new short[samples];
        for (var i = 0; i < samples; i++)
            buf[i] = (short)(Math.Sin(2 * Math.PI * freqHz * i / sampleRate) * 10000);
        return buf;
    }

    private static SrsAudioProcessor CreateProcessor() => new(NullLogger.Instance);

    [Fact]
    public void ClearTransmission_PassesThroughAudibleSignal()
    {
        var processor = CreateProcessor();
        var input = ToneBuffer(960);

        var output = processor.Process(input, slotEnc: false, slotEncKey: 0, streamEnc: false, streamEncKey: 0,
            AmbientNoiseType.None);

        Assert.Equal(input.Length, output.Length);
        // No fading (default AudioParams has DropoutRate/DeepFadeRate = 0) and no encryption
        // involved -- the tone should survive essentially intact, not be silenced or replaced.
        Assert.Contains(output, s => Math.Abs(s) > 1000);
    }

    [Fact]
    public void MatchedEncryption_ProducesColoredButAudibleSignal()
    {
        var processor = CreateProcessor();
        var input = ToneBuffer(960);

        var output = processor.Process(input, slotEnc: true, slotEncKey: 3, streamEnc: true, streamEncKey: 3,
            AmbientNoiseType.None);

        Assert.Equal(input.Length, output.Length);
        // CVSD coloration modifies the waveform but it's still meant to be intelligible voice, not
        // silence or noise.
        Assert.Contains(output, s => Math.Abs(s) > 500);
    }

    [Fact]
    public void ReceiverNotCryptoEngaged_HearsPassiveCiphertextTexture_NotOriginalTone()
    {
        var processor = CreateProcessor();
        var input = ToneBuffer(960, freqHz: 440);

        // slotEnc=false: this SRS receiver's radio isn't in cipher mode at all, but the
        // transmitter is encrypted -- real KY-58 hardware in this state just passes raw
        // ciphertext texture through continuously (PassiveCiphertext), not the original tone.
        var output = processor.Process(input, slotEnc: false, slotEncKey: 0, streamEnc: true, streamEncKey: 3,
            AmbientNoiseType.None);

        Assert.Equal(input.Length, output.Length);
        Assert.NotEqual(input, output);
    }

    [Fact]
    public void WrongKey_EventuallyMutes()
    {
        var processor = CreateProcessor();
        // Buffer sizes small enough, and enough of them, to walk the KY-58 state machine
        // (Idle -> Syncing[~0.15s] -> Beep[~0.2s] -> NoiseBurst[~0.3s] -> Muted) all the way
        // through within a bounded number of calls.
        var input = ToneBuffer(960); // 20ms per call
        short[] lastOutput = [];

        for (var i = 0; i < 60; i++) // 60 * 20ms = 1.2s, comfortably past the ~0.65s to reach Muted
        {
            lastOutput = processor.Process(input, slotEnc: true, slotEncKey: 1, streamEnc: true, streamEncKey: 2,
                AmbientNoiseType.None);
        }

        Assert.All(lastOutput, s => Assert.Equal(0, s));
    }

    [Fact]
    public void ResetForNewTalkSpurt_RecoversFromLatchedMute()
    {
        var processor = CreateProcessor();
        var input = ToneBuffer(960);

        // Drive a mismatched-key transmission all the way to Muted, matching WrongKey_EventuallyMutes.
        // Process always passes carrierPresent: true and is only invoked while a transmission is
        // actually arriving, so nothing here would ever un-mute this processor on its own -- that's
        // the bug: a transmission that ends mid wrong-key sequence leaves Muted latched forever,
        // silencing every later transmission from this peer including clear ones.
        for (var i = 0; i < 60; i++)
            processor.Process(input, slotEnc: true, slotEncKey: 1, streamEnc: true, streamEncKey: 2,
                AmbientNoiseType.None);

        // A new PTT starts (server detects a new talk spurt and resets before processing this
        // buffer) with a totally different, clear transmission on the same peer.
        processor.ResetForNewTalkSpurt();
        var output = processor.Process(input, slotEnc: false, slotEncKey: 0, streamEnc: false, streamEncKey: 0,
            AmbientNoiseType.None);

        Assert.Contains(output, s => Math.Abs(s) > 1000);
    }

    [Fact]
    public void AmbientNoiseType_DoesNotThrow()
    {
        var processor = CreateProcessor();
        var input = ToneBuffer(960);

        var output = processor.Process(input, slotEnc: false, slotEncKey: 0, streamEnc: false, streamEncKey: 0,
            AmbientNoiseType.AirF16);

        Assert.Equal(input.Length, output.Length);
    }
}
