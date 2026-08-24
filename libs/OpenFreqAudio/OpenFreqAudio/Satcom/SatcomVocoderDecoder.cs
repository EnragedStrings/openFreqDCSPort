using System;

namespace OpenFreqAudio.Satcom;

/// <summary>
/// Speech-synthesis stage: given an (possibly lost/corrupted) encoded frame, reconstructs 8 kHz
/// PCM via mixed-excitation LPC synthesis, with stateful frame-loss concealment (see
/// SatcomDecoderState). One instance per active SATCOM receive slot -- carries continuous filter
/// history and concealment state across frames, so it must not be shared between simultaneous
/// SATCOM transmissions or reused after a stream ends without <see cref="Reset"/>.
/// </summary>
public sealed class SatcomVocoderDecoder
{
    private readonly LpcSynthesisFilter _synthesis;
    private readonly SatcomExcitationSynthesizer _excitation;
    private readonly SatcomDecoderState _state = new();
    private readonly int _sampleRate;
    private readonly int _order;

    public SatcomVocoderDecoder(int sampleRate, int seed, int lpcOrder = SatcomVocoderEncoder.LpcOrder)
    {
        _sampleRate = sampleRate;
        _order = lpcOrder;
        _synthesis = new LpcSynthesisFilter(lpcOrder);
        _excitation = new SatcomExcitationSynthesizer(seed);
    }

    public SatcomDecoderState State => _state;

    /// <summary>
    /// Synthesize one frame of PCM (length = <paramref name="outFrame"/>.Length, expected to be
    /// the codec's frame size at <see cref="SampleRate"/>) from an encoded frame and its channel
    /// outcome.
    /// </summary>
    public void Synthesize(SatcomEncodedFrame? encoded, SatcomFrameOutcome outcome, SatcomChannelErrorModel? errorModel,
        Span<double> outFrame)
    {
        double pitchHz, energy, voicing;
        double[] reflection;

        switch (outcome)
        {
            case SatcomFrameOutcome.Lost:
                (pitchHz, energy, voicing, reflection) = _state.Conceal();
                break;

            case SatcomFrameOutcome.Corrupted when encoded != null:
            {
                var (p, e, v, r) = SatcomFrameQuantizer.Dequantize(encoded, _order);
                if (errorModel != null)
                    (p, e, v, r) = errorModel.Corrupt(p, e, v, r);
                pitchHz = p; energy = e; voicing = v; reflection = r;
                // A corrupted-but-decoded frame still counts as "valid" for concealment purposes
                // -- it's real (if damaged) speech, not silence, so it becomes the new baseline.
                _state.CommitValidFrame(pitchHz, energy, voicing, reflection);
                break;
            }

            case SatcomFrameOutcome.Clean when encoded != null:
            {
                var (p, e, v, r) = SatcomFrameQuantizer.Dequantize(encoded, _order);
                pitchHz = p; energy = e; voicing = v; reflection = r;
                _state.CommitValidFrame(pitchHz, energy, voicing, reflection);
                break;
            }

            default:
                (pitchHz, energy, voicing, reflection) = _state.Conceal();
                break;
        }

        if (_state.SyncLost)
        {
            outFrame.Clear();
            return;
        }

        var lpc = SatcomLpc.ReflectionToLpc(reflection, _order);
        // Per-frame excitation-to-output gain, derived from THIS frame's actual reflection
        // coefficients (see SatcomLpc.ExcitationGain's own doc comment) rather than a fixed
        // order-only constant -- a resonant filter gets correspondingly less excitation energy,
        // canceling out its own amplification instead of compounding with it.
        var gain = SatcomLpc.ExcitationGain(energy, reflection, _order);

        for (var n = 0; n < outFrame.Length; n++)
        {
            var excitation = _excitation.NextSample(pitchHz, voicing, gain, _sampleRate);
            outFrame[n] = _synthesis.Process(excitation, lpc);
        }
    }

    public int SampleRate => _sampleRate;

    public void Reset()
    {
        _synthesis.Reset();
        _excitation.Reset();
        _state.Reset();
    }
}
