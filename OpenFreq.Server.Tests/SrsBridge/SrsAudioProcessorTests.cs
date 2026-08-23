using Microsoft.Extensions.Logging.Abstractions;
using OpenFreqAudio;
using OpenFreqServer.SrsBridge;

namespace OpenFreq.Server.Tests.SrsBridge;

/// <summary>
/// Covers the headless OpenFreq->SRS DSP chain: the encryption/COMSEC path, and that a blocked
/// signal produces silence regardless of encryption state. Tests here pass a fixed "clear as day"
/// AudioParams so they isolate the COMSEC chain from SrsSignalQuality's own distance/horizon math
/// (covered separately in SrsSignalQualityTests) -- see SrsAudioProcessor's own doc comment.
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

    // "Clear as day" -- DropoutRate/DeepFadeRate 0, SignalBlocked false -- so these tests exercise
    // only the COMSEC/encryption chain, not SrsSignalQuality (covered separately).
    private static AudioParams ClearAudioParams() => FastPathAudioSim.GetDefaultAudioParams(0);

    [Fact]
    public void ClearTransmission_PassesThroughAudibleSignal()
    {
        var processor = CreateProcessor();
        var input = ToneBuffer(960);

        var output = processor.Process(input, slotEnc: false, slotEncKey: 0, streamEnc: false, streamEncKey: 0,
            AmbientNoiseType.None, ClearAudioParams());

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
            AmbientNoiseType.None, ClearAudioParams());

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
            AmbientNoiseType.None, ClearAudioParams());

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
                AmbientNoiseType.None, ClearAudioParams());
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
                AmbientNoiseType.None, ClearAudioParams());

        // A new PTT starts (server detects a new talk spurt and resets before processing this
        // buffer) with a totally different, clear transmission on the same peer.
        processor.ResetForNewTalkSpurt();
        var output = processor.Process(input, slotEnc: false, slotEncKey: 0, streamEnc: false, streamEncKey: 0,
            AmbientNoiseType.None, ClearAudioParams());

        Assert.Contains(output, s => Math.Abs(s) > 1000);
    }

    [Fact]
    public void AmbientNoiseType_DoesNotThrow()
    {
        var processor = CreateProcessor();
        var input = ToneBuffer(960);

        var output = processor.Process(input, slotEnc: false, slotEncKey: 0, streamEnc: false, streamEncKey: 0,
            AmbientNoiseType.AirF16, ClearAudioParams());

        Assert.Equal(input.Length, output.Length);
    }

    [Fact]
    public void SignalBlocked_ProducesSilenceRegardlessOfEncryptionState()
    {
        var processor = CreateProcessor();
        var input = ToneBuffer(960);

        var blocked = ClearAudioParams();
        blocked.SignalBlocked = true;

        var output = processor.Process(input, slotEnc: false, slotEncKey: 0, streamEnc: false, streamEncKey: 0,
            AmbientNoiseType.None, blocked);

        Assert.Equal(input.Length, output.Length);
        Assert.All(output, s => Assert.Equal(0, s));
    }
}
