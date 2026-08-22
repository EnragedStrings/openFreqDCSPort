using System;
using System.Collections.Generic;

namespace OpenFreqAudio.Satcom;

/// <summary>
/// Top-level SATCOM digital-voice pipeline: resample -> analyze -> quantize -> channel-corrupt ->
/// decode -> resample back. This is the class both the real-time TX/RX audio path and the
/// offline test harness (see SatcomOfflineTestHarness) use -- one implementation, not a
/// parallel "test-only" code path, so tuning done via the offline tool reflects what players
/// actually hear.
///
/// Runs entirely in frame-sized batches (not truly per-sample), matching this library's existing
/// pattern of doing frame-based work (Opus encode/decode) off the real-time audio callback --
/// see RadioPlayback's DSP callback vs. RTPAudioSender's dedicated send thread. Not intended to
/// be called from the BASS DSP callback itself.
/// </summary>
public sealed class SatcomVocoder
{
    public const int InternalSampleRate = 8000;

    private readonly SatcomVocoderEncoder _encoder;
    private readonly SatcomVocoderDecoder _decoder;
    private readonly SatcomChannelErrorModel _channelModel;
    private readonly SatcomDownsampler _downsampler;
    private readonly SatcomUpsampler _upsampler;
    private readonly int _frameSizeSamples8K;
    private readonly double[] _analysisBuffer;
    private int _analysisFill;

    public SatcomVocoder(int networkSampleRate, double frameDurationSeconds, int seed)
    {
        _encoder = new SatcomVocoderEncoder(InternalSampleRate);
        _decoder = new SatcomVocoderDecoder(InternalSampleRate, seed);
        _channelModel = new SatcomChannelErrorModel(seed);
        _downsampler = new SatcomDownsampler(networkSampleRate, InternalSampleRate);
        _upsampler = new SatcomUpsampler(InternalSampleRate, networkSampleRate);
        _frameSizeSamples8K = Math.Max(1, (int)Math.Round(InternalSampleRate * frameDurationSeconds));
        _analysisBuffer = new double[_frameSizeSamples8K];
    }

    public SatcomDecoderState DecoderState => _decoder.State;

    /// <summary>
    /// Process one buffer of network-rate (e.g. 48 kHz) 16-bit PCM microphone audio through the
    /// full SATCOM pipeline, returning the resulting network-rate PCM as heard by the receiver.
    /// </summary>
    /// <param name="frameErrorRate">0..1, from SatcomLinkBudget.FrameErrorRate for the current
    /// link margin.</param>
    /// <param name="burstSeverity">0..1, how far below threshold the link is (drives correlated
    /// burst loss length) -- see SatcomChannelErrorModel.</param>
    public short[] ProcessBuffer(ReadOnlySpan<short> pcmIn, double frameErrorRate, double burstSeverity)
    {
        var output = new List<short>(pcmIn.Length + 256);
        var upsampled = new double[_upsampler.Factor];

        foreach (var sample in pcmIn)
        {
            if (!_downsampler.Process(sample, out var down8K))
                continue;

            _analysisBuffer[_analysisFill++] = down8K;
            if (_analysisFill < _frameSizeSamples8K)
                continue;

            _analysisFill = 0;
            var frame = _encoder.Analyze(_analysisBuffer);
            var encoded = SatcomFrameQuantizer.Quantize(frame, SatcomVocoderEncoder.LpcOrder);
            var frameBits = EstimateFrameBits(SatcomVocoderEncoder.LpcOrder);
            var fer = frameErrorRate; // already a per-frame probability, not re-derived from bits here
            var outcome = _channelModel.NextOutcome(fer, burstSeverity);

            var synthesized = new double[_frameSizeSamples8K];
            _decoder.Synthesize(encoded, outcome, _channelModel, synthesized);

            foreach (var s8K in synthesized)
            {
                _upsampler.Process(s8K, upsampled);
                foreach (var s in upsampled)
                    output.Add(ClampToInt16(s));
            }
            _ = frameBits; // reserved: available for callers that want bits/frame telemetry
        }

        return output.ToArray();
    }

    /// <summary>Rough encoded-frame size in bits (pitch + gain + voicing + spectral indices, at
    /// SatcomFrameQuantizer's fixed quantization levels) -- informational, not currently fed back
    /// into the FER calculation (the caller already computes FER from the profile's configured
    /// bit rate; this is exposed for telemetry/debugging parity with the design brief's
    /// EncodedVoiceFrame concept).</summary>
    private static int EstimateFrameBits(int order) => 5 /*pitch*/ + 4 /*gain*/ + 3 /*voicing*/ + order * 5 /*spectral*/;

    private static short ClampToInt16(double sample)
    {
        if (double.IsNaN(sample) || double.IsInfinity(sample)) return 0;
        return (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
    }

    public void Reset()
    {
        _encoder.Reset();
        _decoder.Reset();
        _downsampler.Reset();
        _analysisFill = 0;
    }
}
