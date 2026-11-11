using System.Diagnostics;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using Microsoft.Extensions.Logging;
using NWaves.Filters.Butterworth;
using OpenFreqAudio.Jamming;
using OpenFreqAudio.Satcom;

// ReSharper disable InconsistentNaming

namespace OpenFreqAudio;

/// <summary>
/// RadioPlayback
/// - Push (WebRTC) streams use a per-stream circular float ring buffer.
/// - File-based streams are decoded by a background reader task that fills the same ring buffer,
/// - PushAudioData converts incoming bytes (16-bit PCM or 32-bit float) into floats and writes into the ring buffer.
/// </summary>
public class RadioPlayback : IDisposable
{
    public const double AgcAttack = 0.003f / 3;
    // Published AM radio figures put AGC decay at 0.1-0.3s; a third of that keeps the "quickly
    // attenuate as someone starts talking, hold level constant-ish through pauses" behavior
    // without the near-instant snap-back a much faster decay gave. BackgroundNoiseGenerator's
    // own noise-floor calibration (its unit-power doc comment) is written against this exact
    // 33ms value -- changing it independently would throw that calibration off.
    public const double AgcDecay = 0.1f / 3;

    // Squelch gates on this slower, dedicated power detector rather than on the fast AGC gain
    // itself -- impulsive VHF noise (see BackgroundNoiseGenerator) can swing the AGC across the
    // threshold for a sample or two between real transmissions, which used to be enough to pop
    // the gate open.
    public const double SquelchTau = 0.010;

    // Time constant of the second squelch detector, which only ever closes the gate
    // (upstream a4daedc).
    //
    // One detector and one threshold make the close time grow with signal strength.
    // The detector decays from the received power P down through the threshold, which
    // takes SquelchTau · ln((P - 1) / 3). That is 35 ms at 20 dB C/N but 171 ms at
    // 78 dB, and the link budget hands us 78 dB for an aircraft a kilometre away.
    // Every strong transmitter therefore leaves a long noise tail when it stops.
    //
    // A second, faster detector cuts that tail without letting impulses through,
    // because it can only close a gate that SquelchTau has already opened. Impulse
    // immunity stays entirely with the slow detector. Measured close times at
    // 20/40/60/78 dB C/N: 8/18/27/35 ms, against 35/82/128/171 ms from SquelchTau alone.
    public const double SquelchReleaseTau = 0.002;

    // Hysteresis: the gate closes at this fraction of the power that opens it, so 3 dB.
    //
    // One threshold for both directions chatters whenever the received power sits on
    // it. Measured at 4 dB C/N: 148 open/close cycles per second, with the gate open
    // 27% of the time. Real squelch circuits put hysteresis around the comparator for
    // exactly this reason.
    public const float SquelchCloseRatio = 0.5f;

    // Warn about stream gaps at most this often per stream, since drifting clocks
    // repeat the same gap on every cycle.
    private const double StreamGapLogIntervalSeconds = 5.0;

    // Real aircraft radios don't have 100% modulation.
    // A bunch of the standards are paywalled, but those I've found
    // suggest minimum specs are 85% modulation, with 90-95% being common.
    // https://www.etsi.org/deliver/etsi_i_ets/300600_300699/300676/01_20_91/ets_300676e01c.pdf
    // https://avweb.com/avionics/vhf-nav-comm-basics/
    public const double ModIndex = 0.95;

    private class RadioStream : ReceivedCarrier
    {
        private ILogger _logger;
        public string StreamId { get; set; } = "";
        public int FrequencyKHz { get; set; }
        public int BassStreamHandle { get; set; } // 0 for push streams
        public int BassMixerHandle { get; set; } // 0 if no mixer (push streams)
        public bool IsPush { get; set; } // true for WebRTC / pushed audio
        public required RadioEffect RadioEffect { get; set; }
        public required AudioParams CurrentParams { get; set; }

        // The carrier from CurrentParams, smoothed over time so it never steps (upstream fa6ab98).
        // Only touched on the DSP thread.
        public required CarrierSmoother CarrierSmoother { get; init; }

        // Audio to play is pushed here and pulled by playback.
        public SyncRope<float> Buffer { get; } = new();
        // Scratch space for decoding and FX application
        public float[] Scratch { get; set; } = [];
        public RadioStream(ILogger logger)
        {
            _logger = logger;
        }

        // For file-based streams we run a reader task that decodes and pushes into the ring
        public CancellationTokenSource? FileReaderCts { get; set; }
        public Task? FileReaderTask { get; set; }

        // Transmission state (separate from stream lifecycle)
        public AmbientNoiseType AmbientNoise { get; set; } = AmbientNoiseType.None;

        // Tracking for the mid-transmission gap warning. A callback that finds Buffer
        // empty drops this stream's carrier, which notches the envelope and can close
        // the squelch. See BeginStreamGap and EndStreamGap. DSP thread only.
        public long LastAudioTicks;
        public long GapStartTicks;
        public long LastGapLogTicks;

        public void StopFileReader()
        {
            try
            {
                FileReaderCts?.Cancel();
                FileReaderTask?.Wait(100);
            }
            catch
            {
                // we don't care for any errors here
            }
            finally
            {
                FileReaderCts = null;
                FileReaderTask = null;
            }
        }

        // Clear ring buffer (used for timeout/crash cleanup)
        public void ClearRingBuffer()
        {
            Buffer.Clear();
        }
    }

    /// <summary>
    /// Per-slot radio state: one instance per (frequency, slotId) pair.
    /// Each slot is its own receiver — it owns the full signal-processing chain
    /// (noise, AGC, band-pass filters, squelch) as well as its output params (volume, pan).
    /// </summary>
    internal class RadioConfig
    {
        // Which slot this is, for logs.
        public Guid SlotId { get; init; }

        // Jammers this slot currently receives (docs/jamming). Replaced wholesale by SetSlotInterference under
        // _lock; the DSP thread reads the reference from its slot snapshot.
        public IReadOnlyList<JammerContribution> Jammers { get; set; } = [];

        // Each jammer's waveform state (phase, PRNG, gates), keyed by JammerId so a new Jammers list with the
        // same ids carries on where the last one left off. DSP thread only (see SlotReceiver).
        public Dictionary<string, JammerWaveform> JammerStates { get; } = new();

        // HAVE QUICK / SINCGARS: the slot's hopping net (null = single channel), set by SetSlotHopNet. The DSP
        // reads the reference; a new state replaces it wholesale.
        public Eccm.HopSlotState? HopNet { get; set; }

        // The shared hop clock (net time, s, at the start of the next callback), advanced by samples and
        // re-anchored to the wall clock when it drifts (SlotReceiver.HopTimingFor). DSP thread only.
        public double HopClockSeconds;
        public bool HopClockValid;

        // True while this slot's receiver state is non-finite and being reset,
        // so that's logged once per run instead of every callback. DSP thread only.
        public bool WasNonFinite { get; set; }

        public bool IsTuned { get; set; }
        public float Volume { get; set; } = 1.0f;
        /// <summary>Pan position: -100 = full left, 0 = center (both), +100 = full right.</summary>
        public int Pan { get; set; } = 0;
        /// <summary>163rd vFS 3DAS static rendering: when true the slot is rendered binaurally at
        /// <see cref="Binaural"/>'s direction instead of with <see cref="Pan"/>.</summary>
        public bool SpatialOn { get; set; }
        public BinauralRenderer? Binaural { get; set; }
        /// <summary>Squelch threshold: 0 = always open (hear noise), 1 = normal gate (only signals break squelch).</summary>
        public float SquelchLevel { get; set; } = 1.0f;
        public bool WasSquelchOpen { get; set; }

        public BackgroundNoiseGenerator? NoiseGenerator { get; set; }

        public bool IsNoiseMuted { get; set; }

        // KY-58/COMSEC + HAVE QUICK configuration of this receiving slot, and the state needed to
        // render the COMSEC layer (see KySecureReceiveState for the TRANSEC/COMSEC layering).
        public bool Enc { get; set; }
        public int EncKey { get; set; }
        public bool HqOn { get; set; }
        public bool CryptoCapable { get; set; }
        public KySecureReceiveState KySecureState { get; } = new(SampleRate);
        public CipherTextNoiseGenerator CipherNoise { get; } = new(SampleRate);
        public CvsdColorationEffect CvsdEffect { get; } = new();
        public bool WasMatchedCipherActive { get; set; }
        public KySecureOutcome? LastLoggedComsecOutcome { get; set; }

        // Regardless of our sample rate, we want to band-pass our radio sound between a fixed
        // ~300 Hz low edge and a band-dependent high edge (see FastPathAudioSim.RadioBandConfig.
        // VoiceBandwidth_Hz) -- narrower bands (HF) sound audibly boxier/less clear than wider
        // ones (VHF/UHF), matching how real voice-radio channel bandwidth scales with band.
        // Chain two filters (a high-pass to remove low freqs & DC, then a low-pass).
        // This fits our needs better than alternatives:
        //
        // - A single-Butterworth bandpass either has to be relatively low-order
        //   with mediocre rolloff, or else it gets very unstable when we pass
        //   it only positive values from our envelope.
        //
        // - An equivalent FIR filter needs > 512 taps, adding delays of 5ms and up.
        public HighPassFilter HighPass { get; }
        public LowPassFilter LowPass { get; }

        // AM vs FM: drives the capture effect on co-channel transmitters and the noise
        // threshold/knee below (see the mixing loop), plus the noise generator's hiss
        // character. See FastPathAudioSim.bandConfigs for which real bands are which.
        public ModulationType Modulation { get; }

        // SATCOM routing can't be derived from frequency the way Modulation above is: DCS keeps
        // reporting the ARC-210's last-tuned dial frequency even once SATCOM (Channel 31 + PRST)
        // is selected, so GetBandConfig would never see it. Set explicitly via
        // RadioPlayback.SetSatcomState whenever the owning channel's SatcomAcquisitionState
        // changes (see ChannelCardListViewModel), independent of Modulation/frequency-band
        // classification. When true, the mixing loop takes an entirely different path (frame-
        // based vocoder processing) instead of the AM/FM per-sample envelope math.
        public bool IsSatcomActive { get; set; }
        public double SatcomFrameErrorRate { get; set; }
        public double SatcomBurstSeverity { get; set; }
        public SatcomVocoder? SatcomVocoderInstance { get; set; }

        // Vocoder output arrives frame-quantized (one full 8kHz analysis frame's worth of
        // samples at a time, upsampled back to SampleRate), while the DSP callback wants exactly
        // `samples` floats every cycle -- this smooths that mismatch. Same-thread only (drained
        // and filled entirely within the DSP callback), so no synchronization is needed.
        //
        // Doubles as the GEO propagation-delay buffer: the drain loop (ProcessSatcomSlot) only
        // starts dequeuing once this queue holds at least SatcomTargetLatencySamples, so real
        // (server-computed) one-way relay delay is actually heard, not just simulated in data --
        // and PTT release still drains whatever was already in flight, since the drain runs every
        // cycle regardless of whether a transmitter is currently active. Local sidetone never
        // touches this queue at all (it's mixed in the normal, undelayed path), so it stays immediate.
        public Queue<float> SatcomOutputQueue { get; } = new();

        /// <summary>Minimum buffered-sample depth before SatcomOutputQueue starts draining, set
        /// from the server's real geometry-computed SatcomLinkResult.PropagationLatencySeconds
        /// (see RadioPlayback.SetSatcomState) -- NOT vocoder/vocoder-buffering delay, which is
        /// separate and always present regardless of this value.</summary>
        public int SatcomTargetLatencySamples { get; set; }

        public RadioConfig(int frequencyKhz)
        {
            var bandConfig = FastPathAudioSim.GetBandConfig(frequencyKhz);
            HighPass = new HighPassFilter(300.0 / SampleRate, 3);
            LowPass = new LowPassFilter(bandConfig.VoiceBandwidth_Hz / SampleRate, 6);
            Modulation = bandConfig.Modulation;
        }

        // AGC gain; varies as a low-pass of the received signal
        // according to attack and decay params below.
        public AttackDecayFilter Agc = AttackDecayFilter.MakeAttackDecayFilter(AgcAttack, AgcDecay, SampleRate);

        // The gate runs two detectors on the same mean power, both separate from Agc, which
        // still does its normal job normalizing output level. Only SquelchDetector (slow, see
        // SquelchTau) opens it, so an impulse too short to move that slow detector can never
        // break squelch. Only SquelchReleaseDetector (fast, see SquelchReleaseTau) closes it,
        // so the tail after a transmitter stops doesn't grow with how strong that transmitter was.
        public FirstOrderFilter SquelchDetector =
            FirstOrderFilter.MakeFirstOrderFilter(SquelchTau, SampleRate, 1.0);

        public FirstOrderFilter SquelchReleaseDetector =
            FirstOrderFilter.MakeFirstOrderFilter(SquelchReleaseTau, SampleRate, 1.0);

        /// <summary>
        /// Whether the gate is open. This has to survive across callbacks, since the
        /// hysteresis means each threshold only applies in one direction. Stays false on a
        /// SATCOM slot, whose vocoder decides what's audible instead. DSP thread only.
        /// </summary>
        public bool SquelchOpen;

        /// <summary>
        /// The mean powers this slot's gate opens and closes at.
        /// Typical squelch is at +6 dB, which is a factor of 2x on the envelope.
        /// The detectors track mean power, so square that.
        /// </summary>
        public (float Open, float Close) SquelchPowers()
        {
            float threshold = SquelchLevel * 2.0f;
            float open = threshold * threshold;
            return (open, open * SquelchCloseRatio);
        }

        /// <summary>
        /// Advance both detectors with one sample's mean power, then return whether the
        /// gate is open. The slow detector only opens the gate and the fast one only
        /// closes it, so an impulse can cut a tail short but can never break squelch.
        /// A NaN loses both comparisons and so closes the gate, which is the safe way
        /// round: an open gate is how a NaN would reach the shared mix.
        /// </summary>
        public bool StepSquelch(float power, float openPower, float closePower)
        {
            SquelchDetector.Apply(power);
            SquelchReleaseDetector.Apply(power);
            SquelchOpen = SquelchOpen
                ? SquelchReleaseDetector.D1 >= closePower
                : SquelchDetector.D1 >= openPower;
            return SquelchOpen;
        }

        /// <summary>
        /// False if the detectors or the band-pass have gone NaN or infinite (upstream a12b86a).
        /// Every sample passes through all of them, and they all latch a bad value,
        /// so checking once per callback catches one anywhere in it.
        /// </summary>
        /// <param name="output">This callback's output. The band-pass state isn't
        /// visible, but a bad value in it shows up in the last sample.</param>
        public bool IsFinite(ReadOnlySpan<float> output) =>
            float.IsFinite(Agc.D1) && float.IsFinite(SquelchDetector.D1) &&
            float.IsFinite(SquelchReleaseDetector.D1) &&
            (output.IsEmpty || float.IsFinite(output[^1]));

        public void ResetReceiver()
        {
            Agc.D1 = 1;
            SquelchDetector.D1 = 1;
            SquelchReleaseDetector.D1 = 1;
            SquelchOpen = false;
            HighPass.Reset();
            LowPass.Reset();
        }

        // AGC attack and decay are exponential functions -
        // for a time constant tau, if Fs is our sample rate,
        // AGC ramps down each sample at e^(-1/tau * Fs).
        // This means we ramp about 95% of the way in 3 tau,
        // 99% of the way in 4.6 tau, etc.
        // See: https://en.wikipedia.org/wiki/RC_circuit
        //
        // Radio specifications I found suggest AGC should attack
        // (ramp up) in about ~3ms, and decay (ramp down) in ~100ms,
        // but ramping down faster (say 10-20ms) gives us quick clicks
        // even when in close formation, and produces cool-sounding
        // distortions when barely coming through.
        // Pick time constants about a third of those values
        // to get the intended effect. (Feel free to tune these by ear!)
        // Values live on RadioPlayback.AgcAttack/AgcDecay.
    }


    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RadioPlayback> _logger;

    // All incoming streams
    private readonly Dictionary<string, RadioStream> _streams = new();

    // Per-slot radio state (full signal-processing chain + output params), keyed by (frequency, slotId)
    private readonly Dictionary<(int freq, Guid slotId), RadioConfig> _slots = new();

    // Frequencies were we are currently transmitting and which are therefore muted
    private readonly HashSet<int> _transmittingFrequencies = new();

    private readonly object _lock = new();

    private int _masterStream;
    private DSPProcedure? _dspProc;

    /// <summary>
    /// Fired on errors that are meaningful to the user (device switch failures, playback loss).
    /// Message is already human-readable; no stack trace. Raised on the calling thread.
    /// Subscribe in OpenFreqService; do not subscribe from UI layers directly.
    /// </summary>
    public event Action<string>? UserFacingError;

    private void RaiseUserFacingError(string message)
    {
        UserFacingError?.Invoke(message);
        _logger.LogError("{UserMessage}", message);
    }

    // True after Initialize() — used to distinguish "no master stream yet" from
    // "master stream died after a failed device switch". ChangeOutputDevice always
    // attempts to (re)start the master stream when this is true, even if _masterStream == 0.
    private bool _isInitialized;

    // The BASS device index the current (or last successfully started) master stream ran on.
    // Populated in Initialize() and updated on each successful ChangeOutputDevice.
    // Used as the fallback target when a new device switch fails.
    private int _currentDeviceIndex = -1;

    private const int MaxBufferSize = 24576;
    private float[] _dspScratch = new float[MaxBufferSize];
    // The per-slot receive chain (noise, I/Q mix, AGC, squelch, band-pass); DSP thread only.
    private readonly SlotReceiver _slotReceiver = new();
    private float[] _stereoBuffer = new float[MaxBufferSize * 2];

    // Sample counter for the KY-58 wrong-key beep only, so its tone stays continuous across
    // callbacks. Radio carriers integrate their own phase instead (RadioStream.CarrierPhase).
    // Wrapped every second: the 900 Hz beep is a whole number of cycles per second, so the
    // wrap is seamless and the counter never overflows.
    private int _sampleNum = 0;

    private const double TwoPi = 2.0 * Math.PI;

    // Alias of AudioFormat.SampleRate, which is where the rate lives now (upstream a4daedc);
    // kept because a lot of our code (3DAS, SATCOM, HUD feed, tests) reads it from here.
    public const int SampleRate = AudioFormat.SampleRate;

    private const int NoiseFadeSamples = 2400;

    private static readonly Lock _bassInitLock = new();

    private int _masterDspProcHandle;

    // Sidetone (own-voice loopback): mic samples pushed here from the recording callback,
    // drained and mixed in the DSP callback with both sides on separate BASS audio threads,
    // so we use a lock-free SPSC ring buffer instead of SyncRope to avoid any lock on the hot path.
    private readonly SidetoneSpscBuffer _sidetoneBuffer = new(8192); // 8192 floats ≈ 170ms @ 48kHz
    private float[] _sidetoneScratch = [];
    public bool SidetoneEnabled { get; set; }
    public float SidetoneVolume { get; set; } = 0.4f;
    public float MasterVolume { get; set; } = 1.0f;

    public void PushSidetone(ReadOnlySpan<float> samples) => _sidetoneBuffer.Write(samples);
    public void ClearSidetone() => _sidetoneBuffer.Clear();

    // --- Ownship mic click (see MicClickPlayer): pick-up at key-down, put-down at key-up. Headset and capture
    // (HUD tape / session recording), never transmitted. Rendered once per callback, before the capture frame. ---
    private readonly MicClickPlayer _micClick = new();
    private float[] _micClickScratch = [];
    private int _micClickFrames; // DSP thread: samples of _micClickScratch carrying the click this callback

    /// <summary>Ownship mic click on/off.</summary>
    public bool MicClickEnabled { get; set; } = true;

    /// <summary>Mic click level, 1 = the samples as recorded.</summary>
    public float MicClickVolume { get; set; } = 1.0f;

    /// <summary>The pick-up (key-down) and put-down (key-up) click WAVs: mono 16-bit PCM at 48 kHz. Null disables one.</summary>
    public void SetMicClickAssets(byte[]? keyDownWavBytes, byte[]? keyUpWavBytes) =>
        _micClick.SetSamples(keyDownWavBytes != null ? DecodePcm16MonoWav(keyDownWavBytes) : null,
            keyUpWavBytes != null ? DecodePcm16MonoWav(keyUpWavBytes) : null);

    /// <summary>Play the mic click for a PTT edge: <paramref name="keyDown"/> true at key-down, false at key-up.</summary>
    public void PlayMicClick(bool keyDown)
    {
        if (MicClickEnabled) _micClick.Trigger(keyDown);
    }

    // --- Encryption PTT tones (KY_58_TX / KY_58_RX): short one-shot cues, decoded once at load
    // time into float buffers and mixed directly into the master output (see MixOneShotTone). ---
    private float[]? _txToneSamples;
    private float[]? _rxToneSamples;
    private int _txTonePosition = -1; // -1 = not playing
    private int _rxTonePosition = -1;

    /// <summary>
    /// Provide the WAV bytes for the encrypted-PTT tones. Pass null for either to disable it.
    /// Expected format: mono 16-bit PCM at <see cref="SampleRate"/> (48 kHz) — the same format
    /// used everywhere else in this engine, so no resampling is performed.
    /// </summary>
    public void SetEncryptionToneAssets(byte[]? txWavBytes, byte[]? rxWavBytes)
    {
        lock (_lock)
        {
            _txToneSamples = txWavBytes != null ? DecodePcm16MonoWav(txWavBytes) : null;
            _rxToneSamples = rxWavBytes != null ? DecodePcm16MonoWav(rxWavBytes) : null;
        }
    }

    /// <summary>Play the local "keying an encrypted radio" PTT tone. Call at PTT keydown.</summary>
    public void PlayTxTone()
    {
        lock (_lock)
        {
            if (_txToneSamples != null) _txTonePosition = 0;
        }
    }

    /// <summary>Play the "now receiving a decrypted secure transmission" tone. DSP thread only.</summary>
    private void TriggerRxTone()
    {
        lock (_lock)
        {
            if (_rxToneSamples != null) _rxTonePosition = 0;
        }
    }

    /// <summary>Mix up to <paramref name="frames"/> samples of a one-shot mono tone, centered, into
    /// <see cref="_stereoBuffer"/> at <paramref name="gain"/> (the master volume), advancing
    /// <paramref name="position"/> (-1 when finished).</summary>
    private void MixOneShotTone(ref int position, float[]? toneSamples, int frames, float gain)
    {
        if (position < 0 || toneSamples == null) return;

        int remaining = toneSamples.Length - position;
        int toMix = Math.Min(remaining, frames);
        for (int i = 0; i < toMix; ++i)
        {
            float s = toneSamples[position + i] * gain;
            _stereoBuffer[i * 2] += s;
            _stereoBuffer[i * 2 + 1] += s;
        }

        position += toMix;
        if (position >= toneSamples.Length) position = -1;
    }

    /// <summary>Minimal canonical-WAV parser: mono 16-bit PCM only (matches this engine's native
    /// format), used solely for the small bundled encryption tone assets.</summary>
    private static float[] DecodePcm16MonoWav(byte[] wavBytes)
    {
        const int headerSearchStart = 12; // past "RIFF"+size+"WAVE"
        int pos = headerSearchStart;
        while (pos + 8 <= wavBytes.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wavBytes, pos, 4);
            int chunkSize = BitConverter.ToInt32(wavBytes, pos + 4);
            int chunkDataStart = pos + 8;

            if (chunkId == "data")
            {
                int sampleCount = chunkSize / 2;
                var result = new float[sampleCount];
                for (int i = 0; i < sampleCount; ++i)
                {
                    short pcm = BitConverter.ToInt16(wavBytes, chunkDataStart + i * 2);
                    result[i] = pcm / (float)short.MaxValue;
                }
                return result;
            }

            pos = chunkDataStart + chunkSize + (chunkSize % 2); // chunks are word-aligned
        }

        return [];
    }

    // --- Session capture (one combined stereo mix: incoming as heard with pan + own voice centered,
    //     through the mask / cockpit of OwnVoiceRadioRenderer, no radio effects). The mix can be written to an Ogg Opus file
    //     OR streamed to a separate playback device (e.g. a virtual cable). Exclusive in practice,
    //     but both sinks are supported independently here. ---
    private bool _recording;        // file sink active
    private OggOpusRecorder? _recorder;
    private bool _monitoring;       // device sink active
    private int _monitorStream;     // push stream on the monitor output device
    private OwnVoiceRadioRenderer? _ownVoiceRenderer;
    private float[] _recordStereo = [];
    private float[] _ownVoiceScratch = [];
    // Own mic samples pushed here from the recording callback (producer), drained on the
    // DSP thread (consumer). Same SPSC buffer used for sidetone.
    private readonly SidetoneSpscBuffer _recordOwnVoiceBuffer = new(8192);

    // Keeps the capture mix (recording, monitor, HUD feed) under full scale (upstream 74b46f0). Reset when a capture
    // starts, so it doesn't start with the last 2 ms of the previous one. Both are used only on the DSP thread.
    private readonly PeakLimiter _captureLimiter = new(SampleRate, channels: 2);
    private bool _wasCapturing;
    private bool _feeding;          // HUD Recorder feed active (local shared memory, see HudTapeFeed)
    // Hot mic (HUD feed only): the pilot's mic outside transmissions, crossfaded at PTT by the mic callback
    // (HotMicCrossfader). Mixed into the feed's own copy of the capture frame, under its own limiter, so the
    // session recording and monitor never get it.
    private readonly SidetoneSpscBuffer _hotMicBuffer = new(8192);
    private readonly PeakLimiter _feedLimiter = new(SampleRate, channels: 2);
    private float[] _feedStereo = [];
    private float[] _hotMicScratch = [];
    private bool _wasFeedingHotMic;
    // The hot mic's own mask: the pilot's mic through the same gain stage and mask as own voice for OwnVoiceAmbient,
    // without the cockpit ambience (the own-voice renderer already puts that on the tape continuously). DSP thread
    // only; rebuilt when the ambient type changes.
    private IAmbientNoiseEffect? _hotMicMask;
    private AmbientNoiseType _hotMicMaskType;
    private MaskProfile _hotMicMaskProfile;
    private HudTapeFeed? _hudFeed;  // created on the first start, kept until Dispose (the DSP thread may hold it)
    private long _outputLatencyTicks; // DSP callback -> heard, Stopwatch ticks (re-read at every master stream start)

    // Any sink active: the DSP thread builds the capture frame and owns an own-voice renderer.
    private bool Capturing => _recording || _monitoring || _feeding;

    /// <summary>Hot mic on the HUD Recorder feed (only while the feed runs). On by default.</summary>
    public bool HotMicEnabled { get; set; } = true;

    /// <summary>The feed runs with the hot mic on: the mic callback should push <see cref="PushHotMicForFeed"/>.</summary>
    public bool WantsHotMic => _feeding && HotMicEnabled;

    /// <summary>Feed hot-mic samples (mono, 48 kHz, [-1,1], already crossfaded) for the HUD Recorder feed only.</summary>
    public void PushHotMicForFeed(ReadOnlySpan<float> samples)
    {
        if (WantsHotMic) _hotMicBuffer.Write(samples);
    }

    /// <summary>Feed own mic samples (mono, 48kHz, [-1,1]) for the outgoing side of the capture.</summary>
    public void PushOwnVoiceForRecording(ReadOnlySpan<float> samples)
    {
        if (Capturing) _recordOwnVoiceBuffer.Write(samples);
    }

    /// <summary>
    /// Transmitter ambient SFX for own voice in the capture (upstream 102796d). The SFX run for the
    /// whole capture, so set this before it starts. A capture that starts later picks up the current value.
    /// </summary>
    public AmbientNoiseType OwnVoiceAmbient
    {
        get;
        set
        {
            // Under the lock, so a renderer that EnsureCaptureRenderer creates at the same time
            // can't miss the new value.
            lock (_lock)
            {
                field = value;
                if (_ownVoiceRenderer != null) _ownVoiceRenderer.AmbientNoise = value;
            }
        }
    }

    /// <summary>
    /// Mask sound for the jet types on our own voice in the capture and on the HUD-tape hot mic. Local only: what we
    /// transmit, and every incoming voice, keeps the classic mask. Default: profile 1, the measured mask.
    /// </summary>
    public MaskProfile OwnVoiceMaskProfile
    {
        get;
        set
        {
            lock (_lock)
            {
                field = value;
                if (_ownVoiceRenderer != null) _ownVoiceRenderer.MaskProfile = value;
            }
        }
    } = MaskProfile.Measured;

    /// <summary>
    /// The frequency we transmit on. Informational: own voice in the capture is no longer band-limited
    /// (no radio effects on our own voice; see OwnVoiceRadioRenderer).
    /// </summary>
    public int OwnVoiceFrequencyKhz { get; set; }

    public bool IsRecording => _recording;
    public bool IsMonitoring => _monitoring;
    /// <summary>The user's capture (file or monitor device) is running. The HUD Recorder feed is not counted.</summary>
    public bool IsCapturing => _recording || _monitoring;
    public bool IsFeedingHudTape => _feeding;
    /// <summary>Any sink wants own voice (PushOwnVoiceForRecording), the HUD Recorder feed included.</summary>
    public bool WantsOwnVoice => Capturing;

    /// <summary>
    /// Wet/dry blend (0..1) of the ambient SFX (cockpit, gain stage, mask) on own voice in the capture, and of the
    /// mask on the HUD-tape hot mic. 0 = the dry mic.
    /// </summary>
    public float OwnVoiceSfxVolume { get; set; } = 1.0f;

    // Wet/dry blend (0..1) for the transmitter-side ambient noise layer.
    // 0 bypasses ambient SFX entirely; 1 applies them at full strength.
    public float AmbientNoiseVolume { get; set; } = 1.0f;

    public RadioPlayback(ILoggerFactory loggerFactory, int playbackDeviceIndex = -1)
    {
        _logger = loggerFactory.CreateLogger<RadioPlayback>();
        _loggerFactory = loggerFactory;
        lock (_bassInitLock)
        {
            // Configure BASS for low-latency operation. Must precede Bass.Init: the device buffer
            // length is only read when a device is initialized.
            Bass.Configure(Configuration.UpdatePeriod, 5);
            Bass.Configure(Configuration.PlaybackBufferLength, 10);
            Bass.Configure(Configuration.DeviceBufferLength, 10);
            Bass.Configure(Configuration.UpdateThreads, 1);

            // Just use the default device - BASS doesnt like any checks on that
            if (playbackDeviceIndex == -1)
            {
                Bass.Init(playbackDeviceIndex);
            }
            else
            {
                var deviceInfo = Bass.GetDeviceInfo(playbackDeviceIndex);
                if (!deviceInfo.IsInitialized)
                {
                    // Initialize the new device
                    if (!Bass.Init(playbackDeviceIndex, SampleRate, DeviceInitFlags.Default, IntPtr.Zero))
                    {
                        _logger.LogError($"Failed to initialize device {playbackDeviceIndex}: {Bass.LastError}");
                        return;
                    }
                }

                Bass.CurrentDevice = playbackDeviceIndex;
            }

            // Capture the actual BASS device index after init so we know the fallback target.
            // For default device (-1), Bass.CurrentDevice resolves to the real index after init.
            _currentDeviceIndex = Bass.CurrentDevice;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use explicit path on non-Windows to avoid strange .NET lib*.so wrangling issues
                // We don't need to free it explicitly, this is covered by BASS
                NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libbassmix.so"));
            }
        }
    }

    public void StartStream(string streamId, string filePath, AudioParams audioParams,
        AmbientNoiseType ambientNoise = AmbientNoiseType.None)
    {
        _logger.LogInformation($"Starting stream '{streamId} with Ambient {ambientNoise}");
        int bassStream;
        ChannelInfo info;

        lock (_lock)
        {
            if (_streams.ContainsKey(streamId)) StopStreamInternal(streamId);

            // Create BASS decode stream (float)
            bassStream = Bass.CreateStream(filePath, 0, 0, BassFlags.Loop | BassFlags.Float | BassFlags.Decode);
            if (bassStream == 0) throw new Exception($"BASS error creating stream '{streamId}': {Bass.LastError}");

            info = Bass.ChannelGetInfo(bassStream);

            _logger.LogInformation("File info: NativeRate={NativeRate}, Channels={Channels}, ResampledTo={InputRate}",
                info.Frequency, info.Channels, SampleRate);
        }

        lock (_lock)
        {
            // Create a BassMix mixer to resample from native rate to SampleRate
            int mixer = BassMix.CreateMixerStream(SampleRate, info.Channels,
                BassFlags.Decode | BassFlags.Float);
            if (mixer == 0)
                throw new Exception($"BASS error creating mixer for '{streamId}': {Bass.LastError}");
            if (!BassMix.MixerAddChannel(mixer, bassStream, BassFlags.Default))
                throw new Exception($"BASS error adding channel to mixer for '{streamId}': {Bass.LastError}");
            if (info.Channels != 1)
                throw new Exception($"BASS error: expected mono, got {info.Channels} channels");

            var stream = new RadioStream(_logger)
            {
                StreamId = streamId,
                FrequencyKHz = audioParams.RadioFrequencyKHz,
                BassStreamHandle = bassStream,
                BassMixerHandle = mixer,
                IsPush = false,
                RadioEffect = new RadioEffect(SampleRate, info.Channels,
                        _loggerFactory.CreateLogger<RadioEffect>())
                    { AmbientNoise = ambientNoise },
                CurrentParams = audioParams,
                CarrierSmoother = new CarrierSmoother(audioParams),
                AmbientNoise = ambientNoise
            };

            // Start a background task that pulls resampled floats from the mixer and pushes them into the ring buffer.
            stream.FileReaderCts = new CancellationTokenSource();
            var token = stream.FileReaderCts.Token;
            int streamSampleRate = SampleRate;
            stream.FileReaderTask = Task.Run(() =>
            {
                try
                {
                    // Read in larger chunks for better throughput (8192 frames = ~185ms at 44.1kHz)
                    int chunkFrames = 8192;
                    int chunkSamples = chunkFrames;
                    int consecutiveNoData = 0;
                    int totalFramesRead = 0;

                    while (!token.IsCancellationRequested)
                    {
                        float[] readBuffer = new float[chunkSamples];
                        int bytesRequested = chunkSamples * sizeof(float);
                        int bytesRead = Bass.ChannelGetData(mixer, readBuffer, bytesRequested);

                        if (bytesRead <= 0)
                        {
                            // If we consistently get no data, sleep briefly to avoid CPU spin
                            consecutiveNoData++;
                            if (consecutiveNoData > 10)
                            {
                                Thread.Sleep(1);
                            }

                            if (consecutiveNoData == 1)
                            {
                                _logger.LogDebug(
                                    "Reached end of stream after reading {TotalFrames} total frames ({Duration:F2}s) at {Time:HH:mm:ss.fff} (StreamId: {StreamId})",
                                    totalFramesRead, (float)totalFramesRead / streamSampleRate, DateTime.Now, streamId);
                            }

                            continue;
                        }

                        consecutiveNoData = 0;

                        int samplesRead = bytesRead / sizeof(float);
                        totalFramesRead += samplesRead;

                        if (samplesRead > 0)
                        {
                            stream.Buffer.Fill(readBuffer.AsMemory()[..samplesRead]);

                            // Throttle reading to prevent flooding the ring buffer
                            // Dynamically adjust sleep time based on how full the buffer is

                            var currentFill = stream.Buffer.Available;
                            // Fill up to a second
                            var fillPercent = (float)currentFill / streamSampleRate;

                            // Calculate base sleep time (80% of audio duration)
                            int baseSleepMs = (int)((float)currentFill / streamSampleRate * 1000 * 0.8);

                            // Adjust sleep based on buffer fill level:
                            // - If buffer is >70% full, sleep extra to let DSP catch up
                            // - If buffer is <30% full, skip sleep to fill faster
                            // - Otherwise, use base sleep time
                            int adjustedSleepMs;
                            if (fillPercent > 0.70f)
                            {
                                // Buffer getting full - sleep longer (up to 2x base)
                                float multiplier = 1.0f + (fillPercent - 0.70f) / 0.30f; // 1.0 to 2.0
                                adjustedSleepMs = (int)(baseSleepMs * multiplier);
                            }
                            else if (fillPercent < 0.30f)
                            {
                                // Buffer running low - sleep less (down to 50% of base)
                                float multiplier = 0.5f + (fillPercent / 0.30f) * 0.5f; // 0.5 to 1.0
                                adjustedSleepMs = (int)(baseSleepMs * multiplier);
                            }
                            else
                            {
                                // Buffer in good range - use base sleep
                                adjustedSleepMs = baseSleepMs;
                            }

                            if (adjustedSleepMs > 0)
                            {
                                Thread.Sleep(adjustedSleepMs);
                            }
                        }
                    }

                    _logger.LogDebug("Task cancelled, exiting (StreamId: {StreamId})", streamId);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Task cancelled (StreamId: {StreamId})", streamId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception in FileReader for stream {StreamId}", streamId);
                }
            }, token);

            _streams.Add(streamId, stream);

            _logger.LogInformation(
                "Added file stream {StreamId}: Freq={Frequency:F3}MHz, Power={Power}dBm, Channels={Channels}, NativeRate={NativeRate}Hz, ResampledTo={InputRate}Hz",
                streamId, audioParams.RadioFrequencyKHz / 1000.0, audioParams.ReceivedDb, info.Channels, info.Frequency,
                SampleRate);
        }
    }

    public void StartPushStream(string streamId, int sampleRate, int channels, AudioParams audioParams)
    {
        if (sampleRate != SampleRate)
        {
            throw new ArgumentException($"Push stream had sample rate of {sampleRate}, expected {SampleRate}");
        }
        if (channels != 1)
        {
            throw new ArgumentException($"Expected mono, got {channels} channels");
        }

        // Build the stream object outside the lock
        var newStream = new RadioStream(_loggerFactory.CreateLogger<RadioStream>())
        {
            StreamId = streamId,
            FrequencyKHz = audioParams.RadioFrequencyKHz,
            BassStreamHandle = 0,
            IsPush = true,
            RadioEffect = new RadioEffect(sampleRate, channels,
                _loggerFactory.CreateLogger<RadioEffect>()),
            CurrentParams = audioParams,
            CarrierSmoother = new CarrierSmoother(audioParams),
        };

        lock (_lock)
        {
            // Re-check under lock — another thread may have added the same stream while
            // we were constructing newStream outside the lock.
            if (_streams.ContainsKey(streamId)) return;

            int ringFrames = (sampleRate * 150) / 1000; // 150ms
            int ringCapacity = ringFrames * Math.Max(1, channels);

            _logger.LogInformation("Stream '{StreamId}' on {Frequency:F3} MHz", streamId,
                audioParams.RadioFrequencyKHz / 1000.0);
            _logger.LogInformation("  SampleRate={SampleRate}, Channels={Channels}", sampleRate, channels);
            _logger.LogInformation(
                "  RingBuffer: {RingFrames} frames × {Channels} ch = {RingCapacity} samples ({Duration:F1}s)",
                ringFrames, Math.Max(1, channels), ringCapacity, (float)ringFrames / sampleRate);

            _streams.Add(streamId, newStream);
        }
    }

    // Accepts raw PCM bytes from WebRTC.
    // ambientNoise describes the acoustic environment of the transmitting platform and
    // is forwarded to RadioEffect so the correct SFX layer is applied post-demodulation.
    // enc/encKey/hqOn describe the transmitting radio's KY-58/COMSEC and HAVE QUICK state and
    // are consumed per-slot in the DSP loop (see KySecureReceiveState).
    public bool PushAudioData(string streamId, Memory<short> audioData, AmbientNoiseType ambientNoise = AmbientNoiseType.None,
        bool enc = false, int encKey = 0, bool hqOn = false, Eccm.HopNetIdentity? eccm = null)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                _logger.LogWarning("Stream '{StreamId}' not found", streamId);
                return false;
            }

            if (!stream.IsPush)
            {
                _logger.LogWarning("Stream '{StreamId}' is not a push stream", streamId);
                return false;
            }

            // Propagate ambient noise type to the effect processor so the correct
            // SFX layer (Air / Ground / Stationary) is applied per packet.
            if (stream.AmbientNoise != ambientNoise)
            {
                stream.AmbientNoise = ambientNoise;
                stream.RadioEffect.AmbientNoise = ambientNoise;
            }

            stream.Enc = enc;
            stream.EncKey = encKey;
            stream.HqOn = hqOn;
            stream.Eccm = eccm;

            if (audioData.IsEmpty) return false;

            float[] floatFrames = new float[audioData.Length];

            // 16-bit PCM little-endian
            for (int i = 0; i < audioData.Length; ++i)
            {
                float pcm16 = audioData.Span[i];
                floatFrames[i] = pcm16 / short.MaxValue;
            }

            // Push into ring buffer
            stream.Buffer.Fill(new Memory<float>(floatFrames));
            return true;
        }
    }

    public async Task StopStream(string streamId)
    {
        // The master stream keeps running with nothing playing (upstream 9586ce7): recording, the monitor
        // sink, the HUD Recorder feed and the KY-58 tones all run in its DSP callback, and nothing that
        // tunes a slot or adds a stream would start it again.
        await Task.Run(() =>
        {
            lock (_lock)
            {
                StopStreamInternal(streamId);
            }
        });
    }

    private void StopStreamInternal(string streamId)
    {
        if (!_streams.TryGetValue(streamId, out var stream)) return;

        // Stop file reader if any
        if (!stream.IsPush)
        {
            stream.StopFileReader();
        }

        if (!stream.IsPush && stream.BassMixerHandle != 0)
        {
            Bass.StreamFree(stream.BassMixerHandle);
        }

        if (!stream.IsPush && stream.BassStreamHandle != 0)
        {
            Bass.StreamFree(stream.BassStreamHandle);
        }

        _streams.Remove(streamId);
    }

    public void UpdateStreamParams(string streamId, AudioParams newParams)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream)) return;
            stream.CurrentParams = newParams;
        }
    }

    /// <summary>Discard any buffered-but-unplayed audio for a stream (e.g. when the caller has
    /// determined the signal is blocked and doesn't want stale audio bleeding through once it
    /// clears).</summary>
    public void ClearStreamBuffer(string streamId)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(streamId, out var stream))
                stream.ClearRingBuffer();
        }
    }

    public void Initialize()
    {
        // Always start master stream during initialization
        // This eliminates race conditions when adding streams later
        // The stream will just output silence until streams are added
        StartMasterStream();
        _isInitialized = true;
    }

    /// <summary>
    /// Restart the master stream if it is not running. No-op if already playing.
    /// Guards against edge cases where the stream was stopped (e.g. all slots were
    /// untuned during a shutdown) and the same RadioPlayback instance is reused on reconnect,
    /// and against a stream whose device output stopped (state PausedDevice/Stopped) — the
    /// silent-until-the-user-reselects-the-device case. Cheap enough to call on every RX start.
    /// </summary>
    public void EnsureMasterStreamRunning()
    {
        if (!_isInitialized) return;

        if (_masterStream == 0)
        {
            _logger.LogWarning("Master stream not running — restarting");
            StartMasterStream();
            return;
        }

        var state = Bass.ChannelIsActive(_masterStream);
        if (state == PlaybackState.Playing || _currentDeviceIndex < 0) return;

        // Same rebuild as reselecting the output device in Settings.
        _logger.LogWarning("Master stream on device {Device} is {State} — restarting output", _currentDeviceIndex, state);
        ChangeOutputDevice(_currentDeviceIndex);
    }

    // Create the own-voice renderer if no capture sink owns one yet. Call under _lock.
    private void EnsureCaptureRenderer()
    {
        if (_ownVoiceRenderer != null) return;
        _ownVoiceRenderer = new OwnVoiceRadioRenderer(SampleRate,
            _loggerFactory.CreateLogger<OwnVoiceRadioRenderer>())
        {
            MaskProfile = OwnVoiceMaskProfile, // before AmbientNoise, so the effect is built once with it
            AmbientNoise = OwnVoiceAmbient,
        };
        _recordOwnVoiceBuffer.Clear();
    }

    // Drop the renderer once no capture sink is active anymore. Call under _lock.
    private void ClearCaptureRendererIfIdle()
    {
        if (Capturing) return;
        _ownVoiceRenderer = null;
        _recordOwnVoiceBuffer.Clear();
    }

    /// <summary>
    /// Start recording the session to a combined stereo Ogg Opus file at <paramref name="filePath"/>.
    /// Captures incoming audio (as heard, post-FX, with pan) plus our own voice in the cockpit and mask, no radio
    /// effects (see <see cref="OwnVoiceRadioRenderer"/>), panned centre. No-op if already recording. Encoding runs on the
    /// recorder's own thread.
    /// </summary>
    public void StartRecording(string filePath)
    {
        lock (_lock)
        {
            if (_recording) return;

            try
            {
                _recorder = new OggOpusRecorder(filePath, _logger);
            }
            catch (Exception ex)
            {
                _recorder = null;
                RaiseUserFacingError($"Recording: failed to start Opus encoder: {ex.Message}");
                return;
            }

            EnsureCaptureRenderer();
            _recording = true;
            _logger.LogInformation("Recording started: {Path}", filePath);
        }
    }

    /// <summary>Stop and finalize the session recording. No-op if not recording.</summary>
    public void StopRecording()
    {
        OggOpusRecorder? recorder;
        lock (_lock)
        {
            if (!_recording) return;
            _recording = false;
            recorder = _recorder;
            _recorder = null;
            ClearCaptureRendererIfIdle();
        }

        // Dispose outside the lock — it drains the queue and joins the encode thread.
        recorder?.Dispose();
        _logger.LogInformation("Recording stopped");
    }

    /// <summary>
    /// Start streaming the combined capture mix to a separate playback device (BASS device index
    /// <paramref name="deviceIndex"/>), e.g. a virtual audio cable. No-op if already monitoring.
    /// The monitor device runs on its own clock, independent of the master output device, so a
    /// generous push-stream buffer is used to absorb drift.
    /// </summary>
    public void StartMonitor(int deviceIndex)
    {
        lock (_lock)
        {
            if (_monitoring) return;

            int stream = 0;
            try
            {
                var info = Bass.GetDeviceInfo(deviceIndex);
                if (!info.IsInitialized &&
                    !Bass.Init(deviceIndex, SampleRate, DeviceInitFlags.Default, IntPtr.Zero))
                {
                    RaiseUserFacingError($"Monitor: failed to initialize device {deviceIndex}: {Bass.LastError}");
                    return;
                }

                Bass.CurrentDevice = deviceIndex;

                // Generous buffer to absorb clock drift vs the master device. Restore afterwards
                // so other stream creation keeps the low-latency setting.
                int prevBuf = Bass.GetConfig(Configuration.PlaybackBufferLength);
                Bass.Configure(Configuration.PlaybackBufferLength, 200);
                stream = Bass.CreateStream(SampleRate, 2, BassFlags.Float, StreamProcedureType.Push);
                Bass.Configure(Configuration.PlaybackBufferLength, prevBuf);

                if (stream == 0)
                {
                    RaiseUserFacingError($"Monitor: failed to create stream on device {deviceIndex}: {Bass.LastError}");
                    return;
                }

                Bass.ChannelPlay(stream);
            }
            finally
            {
                // Keep the master device current for the rest of the pipeline on this thread.
                if (_currentDeviceIndex >= 0) Bass.CurrentDevice = _currentDeviceIndex;
            }

            _monitorStream = stream;
            EnsureCaptureRenderer();
            _monitoring = true;
            _logger.LogInformation("Monitor stream started on device {Device}", deviceIndex);
        }
    }

    /// <summary>Stop streaming the capture mix to the monitor device. No-op if not monitoring.</summary>
    public void StopMonitor()
    {
        int stream;
        lock (_lock)
        {
            if (!_monitoring) return;
            _monitoring = false;
            stream = _monitorStream;
            _monitorStream = 0;
            ClearCaptureRendererIfIdle();
        }

        if (stream != 0)
        {
            Bass.ChannelStop(stream);
            Bass.StreamFree(stream);
        }
        _logger.LogInformation("Monitor stream stopped");
    }

    /// <summary>
    /// Start feeding the capture mix (incoming as heard + own voice through the radio FX, as recorded) to HUD Recorder's
    /// local shared memory, so its DCS HUD tapes carry the radios in sync. Windows only; no-op if already feeding.
    /// Fails with a user-facing error if another program (e.g. the 455th SRS fork) already feeds it.
    /// </summary>
    public void StartHudTapeFeed()
    {
        lock (_lock)
        {
            if (_feeding) return;
            if (_hudFeed == null)
            {
                _hudFeed = HudTapeFeed.Open(out var reason);
                if (_hudFeed == null)
                {
                    RaiseUserFacingError($"HUD Recorder feed: {reason}");
                    return;
                }
            }

            EnsureCaptureRenderer();
            _feeding = true;
            _logger.LogInformation("HUD Recorder feed started (output latency {Ms:F1} ms)",
                _outputLatencyTicks * 1000.0 / Stopwatch.Frequency);
        }
    }

    /// <summary>Stop feeding HUD Recorder (the shared memory stays mapped until Dispose). No-op if not feeding.</summary>
    public void StopHudTapeFeed()
    {
        lock (_lock)
        {
            if (!_feeding) return;
            _feeding = false;
            ClearCaptureRendererIfIdle();
        }

        _logger.LogInformation("HUD Recorder feed stopped");
    }

    public void TuneFrequency(int frequencyKHz, Guid slotId)
    {
        lock (_lock)
        {
            var key = (frequencyKHz, slotId);
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new RadioConfig(frequencyKHz) { SlotId = slotId };
                _slots[key] = slot;
            }

            if (slot.NoiseGenerator == null)
            {
                slot.NoiseGenerator = new BackgroundNoiseGenerator(SampleRate, frequencyKHz);
                _logger.LogInformation("TuneFrequency {Frequency:F3} MHz", frequencyKHz / 1000.0);
            }

            slot.IsTuned = true;
        }
    }

    public void UntuneFrequency(int frequencyKHz, Guid slotId)
    {
        // Leaves the master stream running; see StopStream.
        lock (_lock)
        {
            var key = (frequencyKHz, slotId);
            if (_slots.TryGetValue(key, out var slot))
                slot.IsTuned = false;
        }
    }

    public void SetSquelchLevel(int frequencyKHz, Guid slotId, float squelchLevel)
    {
        lock (_lock)
        {
            var key = (frequencyKHz, slotId);
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new RadioConfig(frequencyKHz) { SlotId = slotId };
                _slots[key] = slot;
            }
            slot.SquelchLevel = squelchLevel;
        }
    }

    /// <summary>
    /// Route a receiving slot through the SATCOM digital vocoder pipeline (OpenFreqAudio.Satcom)
    /// instead of the normal AM/FM envelope-detector path, or back to normal when
    /// <paramref name="isActive"/> is false. Called from the client whenever the owning channel's
    /// SatcomAcquisitionState changes to/from Ready (see ChannelCardListViewModel) -- deliberately
    /// separate from Modulation/frequency-band classification (see RadioConfig.IsSatcomActive)
    /// since DCS keeps reporting the ARC-210's misleading dial frequency in SATCOM mode.
    /// <paramref name="frameErrorRate"/>/<paramref name="burstSeverity"/> should be refreshed
    /// periodically (e.g. once per link-quality update) via repeated calls while active --
    /// cheap to call, does not reset the vocoder's decoder state or output buffer.
    /// </summary>
    /// <summary>
    /// Replaces the jammers this slot receives. Called by the client at the physics tick; an empty list clears
    /// them. See docs/jamming/JAMMING_DESIGN.md.
    /// </summary>
    public void SetSlotInterference(int frequencyKHz, Guid slotId, IReadOnlyList<JammerContribution> contributions)
    {
        lock (_lock)
        {
            var key = (frequencyKHz, slotId);
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new RadioConfig(frequencyKHz) { SlotId = slotId };
                _slots[key] = slot;
            }

            // The DSP thread reads this reference without the lock, so it must be a list nobody mutates later
            // (each call passes a new one). Per-jammer waveform state survives by JammerId.
            slot.Jammers = contributions ?? [];
        }
    }

    /// <summary>Sets (or clears, with null) a slot's HAVE QUICK / SINCGARS net: the TRANSEC gate matches it
    /// against each transmitter's identity, and jammer hop hits follow its shared hop clock.</summary>
    public void SetSlotHopNet(int frequencyKHz, Guid slotId, Eccm.HopSlotState? hopNet)
    {
        lock (_lock)
        {
            var key = (frequencyKHz, slotId);
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new RadioConfig(frequencyKHz) { SlotId = slotId };
                _slots[key] = slot;
            }
            slot.HopNet = hopNet;
        }
    }

    public void SetSatcomState(int frequencyKHz, Guid slotId, bool isActive, double frameErrorRate,
        double burstSeverity, double frameDurationSeconds = 0.0225, double propagationLatencySeconds = 0.0)
    {
        lock (_lock)
        {
            var key = (frequencyKHz, slotId);
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new RadioConfig(frequencyKHz) { SlotId = slotId };
                _slots[key] = slot;
            }

            slot.IsSatcomActive = isActive;
            slot.SatcomFrameErrorRate = frameErrorRate;
            slot.SatcomBurstSeverity = burstSeverity;
            // Cap well under the output queue's own 1s safety bound (ProcessSatcomSlot) so a bad/
            // stale server value can't itself create runaway latency.
            var cappedLatencySeconds = Math.Clamp(propagationLatencySeconds, 0.0, 0.9);
            slot.SatcomTargetLatencySamples = (int)(cappedLatencySeconds * SampleRate);

            if (!isActive)
            {
                // Drop the vocoder instance/queue on deactivation rather than leaving it idle --
                // re-entering SATCOM later (a fresh acquisition) should start clean, not resume
                // mid-stream with stale decoder concealment state.
                slot.SatcomVocoderInstance = null;
                slot.SatcomOutputQueue.Clear();
            }
            else
            {
                slot.SatcomVocoderInstance ??= new SatcomVocoder(SampleRate, frameDurationSeconds, slotId.GetHashCode());
            }
        }
    }

    /// <summary>Configure KY-58/COMSEC + HAVE QUICK state for a receiving slot.</summary>
    public void SetSlotEncryption(int frequencyKHz, Guid slotId, bool enc, int encKey, bool hqOn, bool cryptoCapable)
    {
        lock (_lock)
        {
            var key = (frequencyKHz, slotId);
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new RadioConfig(frequencyKHz) { SlotId = slotId };
                _slots[key] = slot;
            }
            slot.Enc = enc;
            slot.EncKey = encKey;
            slot.HqOn = hqOn;
            slot.CryptoCapable = cryptoCapable;
        }
    }

    public void SetFrequencyVolume(int frequencyKHz, Guid slotId, float volume)
    {
        lock (_lock)
        {
            var key = (frequencyKHz, slotId);
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new RadioConfig(frequencyKHz) { SlotId = slotId };
                _slots[key] = slot;
            }
            // No headroom cap any more (upstream 8d37b34): the output and capture peak limiters keep
            // a loud slot from clipping. A non-finite volume would still poison the whole mix
            // (and the 3DAS binaural state), so that's the one value we refuse.
            if (float.IsFinite(volume))
                slot.Volume = volume;
        }
    }

    public float GetFrequencyVolume(int frequencyKHz, Guid slotId)
    {
        lock (_lock)
        {
            return _slots.TryGetValue((frequencyKHz, slotId), out var slot) ? slot.Volume : 0f;
        }
    }

    public void SetFrequencyPan(int frequencyKHz, Guid slotId, int pan)
    {
        lock (_lock)
        {
            var key = (frequencyKHz, slotId);
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new RadioConfig(frequencyKHz) { SlotId = slotId };
                _slots[key] = slot;
            }
            slot.Pan = Math.Clamp(pan, -100, 100);
        }
    }

    /// <summary>163rd vFS 3DAS: render this slot binaurally at a fixed direction around the head
    /// (<paramref name="enabled"/> true), or go back to the plain <see cref="SetFrequencyPan"/> pan.</summary>
    public void SetFrequencySpatial(int frequencyKHz, Guid slotId, bool enabled, float azimuthDeg, float elevationDeg)
    {
        lock (_lock)
        {
            var key = (frequencyKHz, slotId);
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new RadioConfig(frequencyKHz) { SlotId = slotId };
                _slots[key] = slot;
            }
            if (enabled)
            {
                if (slot.Binaural == null) slot.Binaural = new BinauralRenderer(SampleRate);
                else if (!slot.SpatialOn) slot.Binaural.Reset();
                slot.Binaural.SetDirection(azimuthDeg, elevationDeg);
            }
            slot.SpatialOn = enabled && slot.Binaural != null;
        }
    }

    public void AddTransmittingFrequencies(IEnumerable<int> frequencies)
    {
        lock (_lock)
        {
            foreach (var frequency in frequencies)
            {
                _transmittingFrequencies.Add(frequency);
            }

            // Log changes
            _logger.LogDebug(_transmittingFrequencies.Count > 0
                    ? "Now blocking: {Frequencies}"
                    : "Not blocking any frequencies",
                string.Join(", ", _transmittingFrequencies.Select(f => $"{f / 1000.0:F3} MHz")));
        }
    }

    public void RemoveTransmittingFrequencies(IEnumerable<int> frequencies)
    {
        lock (_lock)
        {
            foreach (var frequency in frequencies)
            {
                _transmittingFrequencies.Remove(frequency);
            }

            // Log changes
            _logger.LogDebug(_transmittingFrequencies.Count > 0
                    ? "Now blocking: {Frequencies}"
                    : "Not blocking any frequencies",
                string.Join(", ", _transmittingFrequencies.Select(f => $"{f / 1000.0:F3} MHz")));
        }
    }

    public void ClearTransmittingFrequencies()
    {
        lock (_lock)
        {
            _transmittingFrequencies.Clear();
        }
    }

    private void StartMasterStream()
    {
        StreamProcedure streamProc = (_, buffer, length, _) =>
        {
            if (buffer != IntPtr.Zero)
            {
                unsafe
                {
                    float* ptr = (float*)buffer;
                    int samples = length / sizeof(float);
                    for (int i = 0; i < samples; i++) ptr[i] = 0f;
                }
            }

            return length;
        };

        _masterStream = Bass.CreateStream(SampleRate, 2, BassFlags.Float, streamProc, IntPtr.Zero);
        if (_masterStream == 0) throw new Exception($"BASS error creating master stream: {Bass.LastError}");
        SetupDSPAndPlay();
        _outputLatencyTicks = MeasureOutputLatencyTicks();
    }

    // How far the DSP callback runs ahead of the speaker: the stream's playback buffer plus the device's own latency
    // (BASS reports it when known; otherwise its device buffer). Only stamps the HUD Recorder feed.
    private static long MeasureOutputLatencyTicks()
    {
        double ms = Bass.GetConfig(Configuration.PlaybackBufferLength);
        try
        {
            var info = Bass.Info;
            ms += info.Latency > 0 ? info.Latency : Bass.GetConfig(Configuration.DeviceBufferLength);
        }
        catch
        {
            ms += Bass.GetConfig(Configuration.DeviceBufferLength);
        }

        return (long)(Math.Clamp(ms, 0, 500) / 1000.0 * Stopwatch.Frequency);
    }

    private void StopMasterStream(string reason)
    {
        int streamToStop = 0;
        int dspHandleToRemove = 0;
        DSPProcedure? procToRemove = null;

        // Capture what we need to do while holding the lock
        lock (_lock)
        {
            streamToStop = _masterStream;
            dspHandleToRemove = _masterDspProcHandle;
            procToRemove = _dspProc;

            // Clear state immediately so other threads know we're stopping
            _masterStream = 0;
            _dspProc = null;
            _masterDspProcHandle = 0;
        }


        if (streamToStop != 0)
        {
            if (procToRemove != null)
            {
                Bass.ChannelRemoveDSP(streamToStop, dspHandleToRemove);
            }

            Bass.ChannelStop(streamToStop);
            Bass.StreamFree(streamToStop);

            _logger.LogInformation("Master stream stopped ({Reason})", reason);
        }
    }

    private void SetupDSPAndPlay()
    {
        var outputLimiter = new PeakLimiter(SampleRate, channels: 2);

        // This callback is fired continuously - to avoid stutters,
        // we need to provide length bytes of stereo audio samples.
        _dspProc = (_, _, bufferPtr, length, _) =>
        {
            int stereoOutputSamples = length / sizeof(float);
            int samples = stereoOutputSamples / 2;

            // Ensure buffers are large enough
            if (samples > MaxBufferSize)
            {
                lock (_lock)
                {
                    _dspScratch = new float[samples];
                    _stereoBuffer = new float[stereoOutputSamples];
                }
            }

            // Snapshot current streams and slots
            List<RadioStream> streams;
            Dictionary<int, List<RadioConfig>> tunedSlotsByFreq;
            // What should we mute because we're talking on it?
            HashSet<int> transmittingFrequencies;
            // Capture state snapshot (encoder / monitor handles stay valid for this callback).
            bool capturing;
            OggOpusRecorder? recorder;
            int monitorStream;
            OwnVoiceRadioRenderer? ownVoiceRenderer;
            HudTapeFeed? hudFeed;
            bool hotMic;
            long callbackQpc = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                transmittingFrequencies = new(_transmittingFrequencies);
                streams = _streams.Values.ToList();

                capturing = Capturing;
                recorder = _recorder;
                monitorStream = _monitorStream;
                ownVoiceRenderer = _ownVoiceRenderer;
                hudFeed = _feeding ? _hudFeed : null;
                hotMic = _feeding && HotMicEnabled;

                // Build freq → tuned-slots index from _slots snapshot
                tunedSlotsByFreq = new Dictionary<int, List<RadioConfig>>();
                foreach (var kvp in _slots)
                {
                    if (!kvp.Value.IsTuned) continue;
                    int f = kvp.Key.freq;
                    if (!tunedSlotsByFreq.TryGetValue(f, out var list))
                    {
                        list = new List<RadioConfig>();
                        tunedSlotsByFreq[f] = list;
                    }
                    list.Add(kvp.Value);
                }
            }

            // Group streams by frequency - we're treating each as its own radio,
            // with its own AGC, squelch, etc.
            var streamsByFrequency = new Dictionary<int, List<RadioStream>>();
            foreach (var stream in streams)
            {
                int freq = stream.FrequencyKHz;
                if (!streamsByFrequency.TryGetValue(freq, out var list))
                {
                    list = new List<RadioStream>();
                    streamsByFrequency[freq] = list;
                }

                list.Add(stream);
            }

            Array.Clear(_stereoBuffer, 0, stereoOutputSamples);

            // Drain each stream independently, up to `samples` or however much it has ready --
            // never less than that for every OTHER stream too. This used to instead take the
            // minimum availability across every stream in the whole system (not just this
            // frequency) and shrink the entire callback's `samples` to match, so one transmitter
            // whose jitter buffer ran momentarily thin throttled every frequency, every slot, for
            // that whole callback -- the output fell behind by that shortfall every time it
            // happened, ratcheting latency up over a session. A stream that comes up short now
            // just holds its carrier with no voice modulation for the remainder of this callback
            // (see the bounds check on Samples.Span below) instead of shrinking everyone's buffer.
            long callbackTicks = Stopwatch.GetTimestamp();

            foreach (var stream in streams)
            {
                int take = Math.Min(samples, stream.Buffer.Available);
                if (take <= 0)
                {
                    BeginStreamGap(stream, callbackTicks);
                    stream.Samples = new Memory<float>();
                    continue;
                }

                bool firstAudio = stream.LastAudioTicks == 0;
                double gapSeconds = EndStreamGap(stream, callbackTicks);

                // Smooth the physics over this callback, continuing right where the previous
                // one left off (upstream fa6ab98), so neither the carrier's level nor its Doppler
                // shift steps every time physics (including our DCS LOS/horizon losses) lands a
                // new result -- that stepping was AGC warble and pops in close formation.
                var target = stream.CurrentParams;
                // A new transmission (first audio, or a silence longer than a PTT release) starts
                // at the latest physics result instead of ramping from the last transmission.
                // So does a blocked signal: a hard LOS/horizon cutoff should cut at once, not
                // fade out over ~200 ms. (The client normally clears the stream's buffer when it
                // blocks, so this only matters for audio already buffered.)
                if (firstAudio || gapSeconds * 1000 > AudioFormat.TalkspurtGapMs || target.SignalBlocked)
                {
                    stream.CarrierSmoother.Reset(target);
                }
                stream.CarrierStart = stream.CarrierSmoother.Carrier;
                stream.CarrierSmoother.Step(target, (double)samples / SampleRate);
                stream.CarrierEnd = stream.CarrierSmoother.Carrier;

                if (stream.Scratch.Length < take)
                {
                    stream.Scratch = new float[take];
                }
                int drained = stream.Buffer.DrainTo(stream.Scratch.AsSpan()[..take])!.Value;
                if (drained > 0 && drained != take)
                {
                    throw new Exception($"Expected {take} samples, got {drained}");
                }

                stream.CarrierPhaseStepStart = PhaseStep(target.RadioFrequencyKHz, stream.FrequencyKHz,
                    stream.CarrierStart.TuneOffsetPpm);
                stream.CarrierPhaseStepEnd = PhaseStep(target.RadioFrequencyKHz, stream.FrequencyKHz,
                    stream.CarrierEnd.TuneOffsetPpm);

                // Apply radio effects
                stream.RadioEffect.Process(stream.Scratch, 0, drained, AmbientNoiseVolume);

                stream.Samples = stream.Scratch.AsMemory()[..drained];
            }

            // 2: Process each tuned slot (noise + envelope + AGC + squelch + band-pass + mix).
            //    Each slot is its own radio/receiver, with its own AGC, squelch, filters, and noise.
            foreach (var (freq, tunedSlots) in tunedSlotsByFreq)
            {
                // Skip frequencies we're transmitting on — we mute our own TX.
                if (transmittingFrequencies.Contains(freq))
                {
                    continue;
                }

                // Get pre-grouped streams for this frequency
                // Don't bail early if these are empty;
                // still want to apply squelch sound and other FX.
                var freqStreams = streamsByFrequency.GetValueOrDefault(freq, []);

                // Mix transmitting streams
                {
                    // Streams are drained independently now (see above), so two transmitters on
                    // the same frequency can legitimately have different amounts of audio ready
                    // this callback -- the per-sample mixing loop below bounds-checks each
                    // stream's Samples.Span itself rather than assuming they're all `samples` long.
                    var transmittingStreamsAll = freqStreams.Where(s => s.Samples.Length > 0).ToList();

                    // The per-slot math lives in SlotReceiver.Process; what follows is why it works.

                    // AM demodulators are envelope detectors
                    // (https://en.wikipedia.org/wiki/Envelope_detector)
                    // They pull out the modulated voice by extracting
                    // the shape (envelope) of the signal.
                    // This has some nice advantages:
                    //
                    // 1. Receivers don't have to perfectly match the channel frequency
                    //    of the transmitter - as long as a TX is in the passband of an RX,
                    //    we can recover the transmitted voice without any frequency errors
                    //    that would make it sound too high or too low.
                    //    (This is especially nice for fast aircraft, since the Doppler effect
                    //    means the frequencies are changing all the time!)
                    //
                    // 2. The electronics for an envelope detector are pretty cheap and simple.
                    //
                    // All is well when a single transmitter is sending on a frequency,
                    // but when *multiple* transmitters send at once, trouble starts.
                    // IRL radios are never tuned to the exact same frequency, since
                    // making two oscillators moving at several million cycles per second
                    // match perfectly is very hard - and so the carrier frequencies
                    // create beats (https://en.wikipedia.org/wiki/Beat_(acoustics)).
                    // Along with people talking over each other,
                    // the receiver hears some nasty effects:
                    //
                    // 1. The receiver hears tones at each of the beat frequencies.
                    //
                    // 2. The beat frequencies ring modulate the weaker voice -
                    //    each frequency f turns into two: f + beat and f - beat.
                    //    (Find some videos of guitar pedals and vocoders that apply
                    //    ring modulation for an example of what this sounds like.)
                    //
                    // 3. The louder voice amplitude-modulates the weaker one.
                    //
                    // 4. Low beat frequencies that fall inside the AGC's passband
                    //    can cause the volume to "pump" up and down.
                    //
                    // Instead of trying to simulate all this, we can calculate the real thing!
                    // For some signal s[n], its envelope is E[n] = sqrt(I[n]^2 + Q[n]^2)
                    // where (I + jQ) is the representation of the signal as a complex number
                    // (see https://en.wikipedia.org/wiki/In-phase_and_quadrature_components).
                    //
                    // If k people are talking on the same channel,
                    // then each will have some signal modulated on a carrier frequency Fc_k
                    // relative to our channel frequency. Then for each k,
                    // I_k = cos(θ_k[n])
                    // Q_k = sin(θ_k[n])
                    // where θ_k[n] = θ_k[n-1] + 2π · Fc_k / F_s
                    //   and F_s is the sample rate.
                    // The phases θ_k must be *integrated* instead of calculated as
                    // θ_k[n] = 2π · Fc_k · n / F_s,
                    // since Fc_k changes on the fly with Doppler shift (upstream 49f2ede).
                    // We'll multiply those terms by each AM signal A_k + v_k[n],
                    // where A_k is the received power of the carrier and v_k[n] is the modulated voice.
                    // All together, we get
                    //
                    // I[n] = Σ_k (A_k + v_k[n]) · cos(θ_k[n])
                    // Q[n] = Σ_k (A_k + v_k[n]) · sin(θ_k[n])
                    // E[n] = sqrt(I[n]² + Q[n]²)
                    //
                    // which gives us an envelope between 0 and 2.
                    // Shift that back to [-1, 1] and we have ourselves the envelope.

                    // Run the full effects chain per slot — each slot is its own receiver
                    // with its own noise, AGC, squelch, band-pass filters, and (independently)
                    // its own TRANSEC (HAVE QUICK) and COMSEC (KY-58) state. See
                    // KySecureReceiveState for the layering: TRANSEC decides whether a slot can
                    // see a transmitter's RF at all; COMSEC then decides what that RF means.
                    foreach (var slot in tunedSlots)
                    {
                        if (slot.IsNoiseMuted) continue;

                        var output = _dspScratch.AsSpan(0, samples);
                        var received = _slotReceiver.Process(slot, transmittingStreamsAll, output, _sampleNum);

                        if (received.ComsecOutcomeChanged)
                        {
                            var dominant = received.Dominant;
                            _logger.LogWarning(
                                "RadioPlayback COMSEC: freq={FreqKhz} outcome={Outcome} slotEnc={SlotEnc} slotKey={SlotKey} slotCryptoCapable={SlotCryptoCapable} streamEnc={StreamEnc} streamKey={StreamKey} matched={Matched} wrongKey={WrongKey} passiveCiphertext={Passive}",
                                freq, received.ComsecOutcome, slot.Enc, slot.EncKey, slot.CryptoCapable,
                                dominant?.Enc ?? false, dominant?.EncKey ?? 0, received.MatchedCipher,
                                received.WrongKey, received.PassiveCiphertext);
                        }

                        if (received.TriggerRxTone)
                            TriggerRxTone();

                        if (received.WentNonFinite)
                            LogNonFiniteSlot(freq, slot, received, _slotReceiver.LastMixed);

                        if (slot.SquelchOpen != slot.WasSquelchOpen)
                        {
#if DEBUG
                            // The detector is mean power against a unit-power noise floor,
                            // so this is the carrier-to-noise ratio in dB.
                            _logger.LogDebug(
                                "SQUELCH {State} (Freq: {Frequency}, Carrier SNR={CarrierToNoise:F1}dB)",
                                slot.SquelchOpen ? "OPEN" : "CLOSED", freq,
                                10.0 * Math.Log10(Math.Max(slot.SquelchDetector.D1, 1e-12f)));
#endif
                            slot.WasSquelchOpen = slot.SquelchOpen;
                        }

                        // Mix this slot's mono signal into the stereo output with its pan and volume.
                        float mv = MasterVolume;
                        var binaural = slot.Binaural;
                        if (slot.SpatialOn && binaural != null)
                        {
                            // 163rd vFS 3DAS static rendering: binaural at the slot's helmet-fixed direction.
                            binaural.Process(_dspScratch, _stereoBuffer, samples, mv * slot.Volume);
                            continue;
                        }
                        float panAngle  = (slot.Pan + 100) / 200f * MathF.PI / 2f;
                        float leftGain  = mv * slot.Volume * MathF.Cos(panAngle);
                        float rightGain = mv * slot.Volume * MathF.Sin(panAngle);
                        for (int frame = 0; frame < samples; frame++)
                        {
                            int leftIdx = frame * 2;
                            _stereoBuffer[leftIdx]     += leftGain  * _dspScratch[frame];
                            _stereoBuffer[leftIdx + 1] += rightGain * _dspScratch[frame];
                        }
                    }

                    // Every slot above started from the same stored phases. Now move each
                    // carrier on exactly once for this callback, so the next one continues
                    // where this one left off however many slots (if any) heard it.
                    foreach (var stream in transmittingStreamsAll)
                        stream.CarrierPhase = AdvancePhase(stream.CarrierPhase,
                            stream.CarrierPhaseStepStart, stream.CarrierPhaseStepEnd, samples);
                }
            }

            // Capture for the session recording/monitor BEFORE sidetone is mixed in, so the
            // incoming side is "as heard" minus our own raw mic loopback. Our own voice
            // is added back rendered through the radio FX (CaptureRecordingFrame).
            // The capture limiter delays the capture by as much as the output limiter delays playback, so the HUD
            // feed's "heard" stamp needs no correction.
            // Ownship mic click: this callback's slice, for the capture (below) and the headset (with the tones).
            if (_micClickScratch.Length < samples) _micClickScratch = new float[samples];
            if (MicClickEnabled)
            {
                _micClickFrames = _micClick.Render(_micClickScratch.AsSpan(0, samples), MicClickVolume);
            }
            else
            {
                _micClick.Stop();
                _micClickFrames = 0;
            }

            if (capturing && !_wasCapturing)
                _captureLimiter.Reset();
            _wasCapturing = capturing;
            if (capturing)
                CaptureRecordingFrame(samples, recorder, monitorStream, ownVoiceRenderer, hudFeed, hotMic,
                    callbackQpc + _outputLatencyTicks);

            // Several radios at once and high volume settings can add up past full scale. Clipping there makes loud,
            // broadband pops far outside our 300-3000 Hz passband. A 2 ms look-ahead limiter (upstream 74b46f0)
            // replaces scaling the whole buffer by its loudest sample, which pumped and made the output quieter every
            // time another speaker came in. Sidetone and the KY-58 tones go in after it, so they don't get its delay.
            outputLimiter.Process(_stereoBuffer.AsSpan(0, stereoOutputSamples));

            // Sidetone: mix own voice (mono) into stereo output
            // No AGC/ALC
            if (SidetoneEnabled)
            {
                int sidetoneAvail = _sidetoneBuffer.Available;
                if (sidetoneAvail > 0)
                {
                    int sidetoneFrames = Math.Min(sidetoneAvail, samples);
                    if (_sidetoneScratch.Length < sidetoneFrames)
                        _sidetoneScratch = new float[sidetoneFrames];
                    int drained = _sidetoneBuffer.Read(_sidetoneScratch.AsSpan()[..sidetoneFrames]);
                    float vol = SidetoneVolume;
                    for (int i = 0; i < drained; ++i)
                    {
                        float s = _sidetoneScratch[i] * vol;
                        _stereoBuffer[i * 2]     += s;
                        _stereoBuffer[i * 2 + 1] += s;
                    }
                }
            }

            // Encryption PTT tones (KY_58_TX / KY_58_RX): short one-shot cues mixed in centered, at the master
            // volume, after the output limiter (so no added delay) and before the final clamp.
            // Locked because PlayTxTone() can be called concurrently from another thread.
            lock (_lock)
            {
                MixOneShotTone(ref _txTonePosition, _txToneSamples, samples, MasterVolume);
                MixOneShotTone(ref _rxTonePosition, _rxToneSamples, samples, MasterVolume);
            }

            // Ownship mic click in the headset: centred, like the tones, at MicClickVolume (in the render) x master.
            float headsetClickGain = MasterVolume;
            for (int i = 0; i < _micClickFrames; ++i)
            {
                float s = _micClickScratch[i] * headsetClickGain;
                _stereoBuffer[i * 2] += s;
                _stereoBuffer[i * 2 + 1] += s;
            }

            // Sidetone and tones can't clip alone, but can add to loud receive audio. A clamp touches only the samples
            // that overflow, and adds no delay.
            foreach (ref float s in _stereoBuffer.AsSpan(0, stereoOutputSamples))
                s = Math.Clamp(s, -1f, 1f);

            Marshal.Copy(_stereoBuffer, 0, bufferPtr, stereoOutputSamples);
            // Keep the wrong-key beep phase-coherent across callbacks (see _sampleNum).
            _sampleNum = (_sampleNum + samples) % SampleRate;
        };

        _masterDspProcHandle = Bass.ChannelSetDSP(_masterStream, _dspProc, IntPtr.Zero);
        if (_masterDspProcHandle == 0)
            throw new Exception($"BASS error attaching DSP proc to master stream: {Bass.LastError}. All playback would be silent.");

        // Ensure device output is running. On a device that was disconnected and reconnected,
        // BASS keeps IsInitialized = true but stops the output (BASS_ERROR_START on ChannelPlay).
        // Bass.Start() is idempotent — no-op if output is already running.
        if (!Bass.Start())
            _logger.LogWarning(
                "Bass.Start() on device {Device} returned false: {Error}. ChannelPlay may fail.",
                Bass.CurrentDevice, Bass.LastError);

        if (!Bass.ChannelPlay(_masterStream))
            throw new Exception($"BASS error starting master stream playback: {Bass.LastError}");
    }


    /// <summary>
    /// This stream had no samples this callback, so its carrier drops. Start timing
    /// the gap. A stream that has never delivered audio isn't in a gap - it is idle.
    /// </summary>
    private static void BeginStreamGap(RadioStream stream, long now)
    {
        if (stream.LastAudioTicks != 0 && stream.GapStartTicks == 0)
            stream.GapStartTicks = now;
    }

    /// <summary>
    /// This stream has samples again, so close out any gap we were timing (upstream a4daedc).
    /// A gap shorter than one Opus frame means whatever fills this stream kept going
    /// and we drained ahead of it, so the carrier dropped with audio still to come.
    /// Warn about that. A longer gap means the producer itself stopped, which is how a
    /// transmission ends, so say nothing.
    /// </summary>
    /// <returns>How long the gap lasted in seconds, or 0 if there was none.</returns>
    private double EndStreamGap(RadioStream stream, long now)
    {
        double gapSeconds = 0;
        if (stream.GapStartTicks != 0)
        {
            gapSeconds = (double)(now - stream.GapStartTicks) / Stopwatch.Frequency;
            // Everything upstream paces audio one Opus frame at a time, so a shorter gap
            // than that is drift between the producer and the BASS callbacks.
            if (gapSeconds < (double)AudioFormat.OpusSamplesPerFrame / SampleRate)
            {
                double sinceLog = (double)(now - stream.LastGapLogTicks) / Stopwatch.Frequency;
                if (stream.LastGapLogTicks == 0 || sinceLog >= StreamGapLogIntervalSeconds)
                {
                    stream.LastGapLogTicks = now;
                    LogStreamGap(stream, gapSeconds);
                }
            }

            stream.GapStartTicks = 0;
        }

        stream.LastAudioTicks = now;
        return gapSeconds;
    }

    /// <summary>
    /// A stream ran dry mid-transmission. Called on the DSP thread, so this takes a
    /// snapshot and logs it from the thread pool.
    /// </summary>
    private void LogStreamGap(RadioStream stream, double gapSeconds)
    {
        var streamId = stream.StreamId;
        int freqKhz = stream.FrequencyKHz;

        LogOffDspThread(logger => logger.LogWarning(
            "Stream {StreamId} on {Frequency:F3} MHz had no samples for {GapMs:F1} ms",
            streamId, freqKhz / 1000.0, gapSeconds * 1000));
    }

    /// <summary>
    /// How far a carrier's phase moves in one sample, relative to the channel frequency
    /// (upstream 49f2ede / fa6ab98). In double: in float32 the carrier frequency only resolved
    /// to ~16 Hz at 250 MHz, which quantized beat frequencies.
    /// </summary>
    internal static double PhaseStep(int txKhz, int channelKhz, double tuneOffsetPpm)
    {
        var offsetHz = (txKhz - channelKhz) * 1e3 + txKhz * 1e3 * tuneOffsetPpm * 1e-6;
        return (TwoPi * offsetHz / SampleRate) % TwoPi;
    }

    /// <summary>Wrap a phase that's at most one turn out of range back into [0, 2π).</summary>
    internal static double WrapPhase(double theta)
    {
        if (theta >= TwoPi) theta -= TwoPi;
        else if (theta < 0) theta += TwoPi;
        return theta;
    }

    /// <summary>
    /// The phase <paramref name="samples"/> samples on from <paramref name="theta"/> at
    /// <paramref name="step"/> radians per sample, wrapped to [0, 2π).
    /// </summary>
    internal static double AdvancePhase(double theta, double step, int samples) =>
        AdvancePhase(theta, step, step, samples);

    /// <summary>
    /// The phase <paramref name="samples"/> samples on from <paramref name="theta"/>, with the
    /// step ramping linearly from <paramref name="stepStart"/> to <paramref name="stepEnd"/>
    /// the way the mixer ramps it (sample n advances by stepStart + slope · (n + 1)), wrapped to [0, 2π).
    /// </summary>
    internal static double AdvancePhase(double theta, double stepStart, double stepEnd, int samples)
    {
        if (!double.IsFinite(theta) || !double.IsFinite(stepStart) || !double.IsFinite(stepEnd)) return 0;
        double slope = (stepEnd - stepStart) / samples;
        double total = stepStart * samples + slope * samples * (samples + 1) / 2.0;
        theta = (theta + total) % TwoPi;
        return theta < 0 ? theta + TwoPi : theta;
    }

    /// <summary>
    /// A slot's detectors or band-pass went NaN or infinite, and are about to be reset.
    /// Called on the DSP thread, so this takes a snapshot and logs it from the thread pool.
    /// </summary>
    private void LogNonFiniteSlot(int freqKhz, RadioConfig slot, in SlotReceiveResult received,
        IReadOnlyList<ReceivedCarrier> transmittingStreams)
    {
        var slotId = slot.SlotId;
        float agc = received.NonFiniteAgc;
        float squelchDetector = received.NonFiniteSquelchDetector;
        float lastOutput = received.NonFiniteLastOutput;
        // This thread keeps changing the smoother, so format it before the log runs on another thread.
        var transmitters = transmittingStreams.OfType<RadioStream>()
            .Select(s => (s.StreamId, s.CurrentParams, Smoothed: s.CarrierSmoother.ToString()))
            .ToList();

        LogOffDspThread(logger => logger.LogError(
            "Reset receiver on {Frequency:F3} MHz (slot {SlotId}) after it went non-finite: " +
            "AGC {Agc}, squelch detector {SquelchDetector}, last output sample {LastOutput}. " +
            "Transmitters: {Transmitters}",
            freqKhz / 1000.0, slotId, agc, squelchDetector, lastOutput,
            transmitters.Count == 0
                ? "none"
                : string.Join("; ", transmitters.Select(t => $"{t.StreamId}: {t.CurrentParams}, smoothed {t.Smoothed}"))));
    }

    /// <summary>
    /// Log from the thread pool, so the DSP thread never waits on a log sink.
    /// Each call queues work, so keep this to rare events.
    /// </summary>
    private void LogOffDspThread(Action<ILogger> log) =>
        ThreadPool.QueueUserWorkItem(_ => log(_logger));

    /// <summary>
    /// Build one stereo recording frame (incoming as heard, with pan, + own voice with the
    /// radio tone, panned centre) and feed it to the encoder. Runs on
    /// the DSP thread; the Opus encode itself happens on the recorder's own thread.
    /// </summary>
    private void CaptureRecordingFrame(int samples, OggOpusRecorder? recorder, int monitorStream,
        OwnVoiceRadioRenderer? renderer, HudTapeFeed? hudFeed, bool hotMic, long heardQpc)
    {
        int stereo = samples * 2;
        if (_recordStereo.Length < stereo) _recordStereo = new float[stereo];

        // Incoming, as heard (pre-sidetone) — pan / volume already applied in the fan-out.
        Array.Copy(_stereoBuffer, _recordStereo, stereo);

        // Own voice, with the radio tone and ambient SFX, mixed in centre. The renderer runs EVERY
        // frame (not just when voice is present) so its ambient SFX are continuous.
        if (renderer != null)
        {
            if (_ownVoiceScratch.Length < samples) _ownVoiceScratch = new float[samples];
            int voiceAvail = Math.Min(_recordOwnVoiceBuffer.Available, samples);
            int drained = voiceAvail > 0
                ? _recordOwnVoiceBuffer.Read(_ownVoiceScratch.AsSpan()[..voiceAvail])
                : 0;
            // The rest of the buffer is silence (key released, or the mic is behind).
            for (int i = drained; i < samples; i++) _ownVoiceScratch[i] = 0f;

            renderer.Process(_ownVoiceScratch, samples, OwnVoiceSfxVolume);

            // Centre pan: each channel gets cos45° = sin45° ≈ 0.707, scaled by MasterVolume —
            // matches the gain staging a centre-panned, unity-volume incoming stream receives.
            float ownGain = 0.70710678f * MasterVolume;
            for (int i = 0; i < samples; i++)
            {
                float s = ownGain * _ownVoiceScratch[i];
                _recordStereo[i * 2] += s;
                _recordStereo[i * 2 + 1] += s;
            }
        }

        // Ownship mic click: centred at own voice's gain, so it sits with our voice on the tape / recording.
        if (_micClickFrames > 0)
        {
            float clickGain = 0.70710678f * MasterVolume;
            for (int i = 0; i < _micClickFrames; i++)
            {
                float s = clickGain * _micClickScratch[i];
                _recordStereo[i * 2] += s;
                _recordStereo[i * 2 + 1] += s;
            }
        }

        // HUD feed with the hot mic: its own copy of the frame, the hot mic mixed in centre at own voice's gain
        // BEFORE a limiter of its own (same lookahead as the capture limiter, so the "heard" stamp still holds).
        bool feedHotMic = hudFeed != null && hotMic;
        if (feedHotMic)
        {
            if (!_wasFeedingHotMic) _feedLimiter.Reset();
            if (_hotMicScratch.Length < samples) _hotMicScratch = new float[samples];
            if (_feedStereo.Length < stereo) _feedStereo = new float[stereo];
            int hotAvail = Math.Min(_hotMicBuffer.Available, samples);
            int hotRead = hotAvail > 0 ? _hotMicBuffer.Read(_hotMicScratch.AsSpan()[..hotAvail]) : 0;
            for (int i = hotRead; i < samples; i++) _hotMicScratch[i] = 0f;

            // Same mask (and gain stage) as own voice, so idle mic and transmissions are the same person in the same
            // mask; no ambience (already on the tape) and no radio effects (it is the pilot's mask, not the radio).
            var maskType = OwnVoiceAmbient;
            var maskProfile = OwnVoiceMaskProfile;
            if (_hotMicMask == null || maskType != _hotMicMaskType || maskProfile != _hotMicMaskProfile)
            {
                _hotMicMask = AmbientNoiseEffectFactory.Create(maskType, SampleRate, mask: maskProfile);
                _hotMicMaskType = maskType;
                _hotMicMaskProfile = maskProfile;
            }
            _hotMicMask.ApplyMicOnly(_hotMicScratch, 0, samples, OwnVoiceSfxVolume);
            HudFeedMix.Build(_recordStereo.AsSpan(0, stereo), _hotMicScratch.AsSpan(0, samples),
                0.70710678f * MasterVolume, _feedLimiter, _feedStereo.AsSpan(0, stereo));
        }
        else if (_wasFeedingHotMic)
        {
            _hotMicBuffer.Clear();
        }

        _wasFeedingHotMic = feedHotMic;

        // In case multiple RX channels plus our own voice make us clip (upstream b4e67d7 / 74b46f0).
        _captureLimiter.Process(_recordStereo.AsSpan(0, stereo));

        // File sink: queued to the recorder's own encode thread.
        recorder?.Write(_recordStereo.AsSpan(0, stereo));
        // Device sink: push to the monitor output stream (its device pulls at its own rate).
        if (monitorStream != 0)
            Bass.StreamPutData(monitorStream, _recordStereo, stereo * sizeof(float));
        // HUD Recorder feed: copied into its shared memory now, stamped with when this block is heard.
        hudFeed?.WriteHeard(feedHotMic ? _feedStereo.AsSpan(0, stereo) : _recordStereo.AsSpan(0, stereo), heardQpc);
    }

    public async Task StopAll()
    {
        _logger.LogInformation("Stopping all streams and cleaning up resources...");

        // Finalize any active capture before tearing down BASS.
        StopRecording();
        StopMonitor();

        // 2. Stop all streams
        List<string> ids;
        lock (_lock)
        {
            ids = _streams.Keys.ToList();
        }

        foreach (var id in ids)
        {
            await StopStream(id);
        }

        // 3. Stop master stream
        StopMasterStream("StopAll");

        // 4. Clear slot configs
        lock (_lock)
        {
            _slots.Clear();
        }

        ClearTransmittingFrequencies();

        // 5. Free BASS device
        try
        {
            var currentDevice = Bass.CurrentDevice;
            if (currentDevice != -1)
            {
                Bass.Free();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error freeing BASS device");
        }
    }

    public List<string> GetActiveStreams()
    {
        lock (_lock) return _streams.Keys.ToList();
    }

    public List<string> GetStreamsOnFrequency(int frequencyKHz)
    {
        lock (_lock)
            return _streams.Values.Where(s => s.FrequencyKHz == frequencyKHz).Select(s => s.StreamId).ToList();
    }

    public bool IsStreamActive(string id)
    {
        lock (_lock) return _streams.TryGetValue(id, out _);
    }

    public List<int> GetActiveFrequencies()
    {
        lock (_lock) return _streams.Values.Select(s => s.FrequencyKHz).Distinct().OrderBy(x => x).ToList();
    }

    public AudioParams? GetStreamParams(string id)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(id, out var s)) return s.CurrentParams;
            return null;
        }
    }

    public int? GetStreamFrequency(string id)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(id, out var s)) return s.FrequencyKHz;
            return null;
        }
    }

    public void ChangeOutputDevice(int newDeviceIndex)
    {
        // Capture previous device index BEFORE stopping — needed for fallback if switch fails.
        // Do NOT use _masterStream != 0 to decide whether to restart: after a failed previous
        // switch, _masterStream may be 0 even though _isInitialized is true and we need a stream.
        int previousDeviceIndex;
        lock (_lock)
        {
            previousDeviceIndex = _currentDeviceIndex;
        }

        _logger.LogInformation(
            "Switching BASS playback device: {Old} → {New}",
            previousDeviceIndex, newDeviceIndex);

        // Stop the current master stream regardless — StopMasterStream is a no-op if none running.
        StopMasterStream("switching output device");

        // Initialize the new device if BASS hasn't seen it yet.
        // NOTE: a device that was disconnected and reconnected still shows IsInitialized = true —
        // BASS does not auto-deinit on disconnect. Its output is stopped, not its init state.
        // Bass.Start() inside SetupDSPAndPlay handles that case.
        var deviceInfo = Bass.GetDeviceInfo(newDeviceIndex);
        if (!deviceInfo.IsInitialized)
        {
            _logger.LogInformation("BASS device {Index} not yet initialized — calling Bass.Init", newDeviceIndex);
            if (!Bass.Init(newDeviceIndex, SampleRate, DeviceInitFlags.Default, IntPtr.Zero))
            {
                RaiseUserFacingError(
                    $"Audio device (index {newDeviceIndex}) failed to initialize: {Bass.LastError}. Restoring previous device.");
                TryRestorePreviousDevice(previousDeviceIndex);
                return;
            }
        }

        Bass.CurrentDevice = newDeviceIndex;

        if (_isInitialized)
        {
            try
            {
                StartMasterStream();
                lock (_lock) { _currentDeviceIndex = newDeviceIndex; }
                _logger.LogInformation("BASS playback started on device {Index}", newDeviceIndex);
            }
            catch (Exception ex)
            {
                // First attempt failed (e.g. reconnected device with stale BASS state).
                // Try force-reinit: free device in BASS and re-initialize from scratch.
                _logger.LogWarning(ex,
                    "First start attempt failed on device {Index} — trying force-reinit (Bass.Free + Bass.Init).",
                    newDeviceIndex);
                try
                {
                    Bass.CurrentDevice = newDeviceIndex;
                    Bass.Free(); // deinit this specific device in BASS
                    if (Bass.Init(newDeviceIndex, SampleRate, DeviceInitFlags.Default, IntPtr.Zero))
                    {
                        Bass.CurrentDevice = newDeviceIndex;
                        StartMasterStream();
                        lock (_lock) { _currentDeviceIndex = newDeviceIndex; }
                        _logger.LogInformation("BASS playback started on device {Index} after force-reinit", newDeviceIndex);
                        return;
                    }
                    _logger.LogError("Force-reinit of BASS device {Index} failed: {Error}", newDeviceIndex, Bass.LastError);
                }
                catch (Exception reinitEx)
                {
                    _logger.LogWarning(reinitEx, "Force-reinit of BASS device {Index} threw", newDeviceIndex);
                }

                RaiseUserFacingError(
                    $"Playback failed to start on audio device (index {newDeviceIndex}). Restoring previous device.");
                TryRestorePreviousDevice(previousDeviceIndex);
            }
        }
    }

    /// <summary>
    /// Attempt to restart the master stream on <paramref name="deviceIndex"/>.
    /// Called on the error path of <see cref="ChangeOutputDevice"/> so audio is not
    /// permanently lost when the target device fails to initialise or play.
    /// </summary>
    private void TryRestorePreviousDevice(int deviceIndex)
    {
        if (deviceIndex < 0 || !_isInitialized)
        {
            RaiseUserFacingError(
                "Audio playback lost — no valid previous device to fall back to. Please select a device in Settings.");
            return;
        }

        _logger.LogWarning("Attempting to restore BASS playback on previous device {Index}", deviceIndex);
        Bass.CurrentDevice = deviceIndex;

        try
        {
            StartMasterStream();
            lock (_lock) { _currentDeviceIndex = deviceIndex; }
            _logger.LogWarning("Restored BASS playback on device {Index}", deviceIndex);
        }
        catch (Exception ex)
        {
            RaiseUserFacingError(
                "Audio playback lost — failed to restore previous output device. Please select a device in Settings.");
            _logger.LogError(ex, "Failed to restore BASS playback on device {Index}", deviceIndex);
        }
    }


    /// <summary>
    /// Called when WebSocket signals PTT press (optional - RTP markers are primary)
    /// </summary>
    public void OnWebSocketPTTPress(string streamId)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var stream)) return;

            // WebSocket arrives before RTP packets typically
            // Just log for validation - RTP start marker will trigger actual transmission start
            _logger.LogDebug("PTT pressed - expecting RTP start marker (StreamId: {StreamId})", streamId);
        }
    }

    public void Dispose()
    {
        bool stopped = false;
        try
        {
            stopped = StopAll().Wait(500);
        }
        finally
        {
            HudTapeFeed? feed;
            lock (_lock)
            {
                _feeding = false;
                feed = _hudFeed;
                _hudFeed = null;
            }

            // Unmap only once StopAll completed (master stream and its DSP callback gone): a callback still running
            // would write through a raw pointer into an unmapped view and kill the process. Otherwise just give up
            // ownership; the OS unmaps it at exit.
            if (stopped) feed?.Dispose();
            else feed?.Abandon();
        }
    }

    // Lock-free SPSC ring buffer for sidetone.
    // Producer = BASS recording thread. Consumer = BASS DSP thread.
    // Uses Volatile.Write (store-release) on the write index so the consumer sees
    // all buffer writes before the updated index, and Volatile.Read (load-acquire)
    // so each side observes the other's latest index across CPU cores / ARM reordering.
    private sealed class SidetoneSpscBuffer(int capacity)
    {
        private readonly float[] _buf = new float[NextPow2(capacity)];
        private readonly int _mask = NextPow2(capacity) - 1;
        private int _writeIdx;
        private int _readIdx;

        private static int NextPow2(int n) { int p = 1; while (p < n) p <<= 1; return p; }

        public int Available => Volatile.Read(ref _writeIdx) - Volatile.Read(ref _readIdx);

        // Called only from the recording thread.
        public void Write(ReadOnlySpan<float> src)
        {
            int wp = _writeIdx; // only writer touches _writeIdx — no Volatile.Read needed
            int free = _buf.Length - (wp - Volatile.Read(ref _readIdx));
            int n = Math.Min(src.Length, free);
            for (int i = 0; i < n; i++)
                _buf[(wp + i) & _mask] = src[i];
            Volatile.Write(ref _writeIdx, wp + n); // release: buffer writes visible before index update
        }

        // Called only from the DSP thread.
        public int Read(Span<float> dst)
        {
            int rp = _readIdx; // only reader touches _readIdx
            int n = Math.Min(dst.Length, Volatile.Read(ref _writeIdx) - rp); // acquire
            for (int i = 0; i < n; i++)
                dst[i] = _buf[(rp + i) & _mask];
            Volatile.Write(ref _readIdx, rp + n);
            return n;
        }

        // Safe to call from any thread — single store, worst case DSP sees stale index once.
        public void Clear() => Volatile.Write(ref _readIdx, Volatile.Read(ref _writeIdx));
    }
}
