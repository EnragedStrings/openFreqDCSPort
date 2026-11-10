using OpenFreqAudio.Jamming;

namespace OpenFreqAudio;

/// <summary>
/// One transmitter's carrier as a receiving slot sees it during one DSP callback: its drained audio,
/// smoothed level, phase and TRANSEC/COMSEC identity. <see cref="RadioPlayback"/>'s streams derive from
/// this; tests build it directly. Only ever touched on the DSP thread.
/// </summary>
internal class ReceivedCarrier
{
    // The carrier from CarrierSmoother at the start and end of this callback.
    // The mixer ramps linearly between them.
    // (We could step the smoother every audio sample, but this would be a lot more math
    // for negligible gain - given a physics update rate of 5-20 Hz, this approach
    // is only ever off by thousandths of a Hz in Doppler or dB in SNR.)
    public CarrierState CarrierStart;
    public CarrierState CarrierEnd;

    // Phase of this stream's carrier, relative to the channel frequency, in radians,
    // wrapped to [0, 2π) (upstream 49f2ede). Integrated per sample so a change in carrier
    // frequency (e.g. Doppler shift) doesn't cause an audible phase jump. Every slot tuned to
    // the channel starts its mix from this value, and it's advanced exactly once per callback
    // after all of them have used it.
    public double CarrierPhase;

    // How far CarrierPhase moves per sample at the start and end of this callback (from
    // CarrierStart / CarrierEnd); the step ramps linearly between them. Computed once when the
    // stream is drained, so every slot and the final advance agree even if physics swaps
    // CurrentParams mid-callback.
    public double CarrierPhaseStepStart;
    public double CarrierPhaseStepEnd;

    // This callback's voice samples. Can be shorter than the callback if the stream ran short,
    // in which case the carrier is held unmodulated for the rest.
    public Memory<float> Samples { get; set; }

    // KY-58/COMSEC + HAVE QUICK state of the transmitting radio, updated per-packet.
    public bool Enc { get; set; }
    public int EncKey { get; set; }
    public bool HqOn { get; set; }
}

/// <summary>
/// What <see cref="SlotReceiver.Process"/> did besides filling the output, for the caller to log or act on
/// (the receiver itself never logs or touches shared playback state).
/// </summary>
internal struct SlotReceiveResult
{
    /// <summary>This callback's COMSEC outcome, and whether it differs from the last one logged.</summary>
    public KySecureOutcome ComsecOutcome;
    public bool ComsecOutcomeChanged;
    public bool MatchedCipher;
    public bool WrongKey;
    public bool PassiveCiphertext;
    public ReceivedCarrier? Dominant;

    /// <summary>A decrypted secure transmission just started: play the RX cue.</summary>
    public bool TriggerRxTone;

    /// <summary>The slot went non-finite this callback for the first time and was reset. The values are
    /// the state before the reset, for the log.</summary>
    public bool WentNonFinite;
    public float NonFiniteAgc;
    public float NonFiniteSquelchDetector;
    public float NonFiniteLastOutput;
}

/// <summary>
/// The per-slot receiver: everything that turns the carriers on one channel into one slot's mono audio,
/// before pan/binaural mixing. TRANSEC gate, KY-58 classification, SATCOM vocoder branch, FM capture and
/// threshold, the complex-baseband (I/Q) sum with noise and jammers, envelope detection, AGC, squelch, CVSD
/// texture, band-pass and the NaN guard.
///
/// Jammers (RadioConfig.Jammers, docs/jamming) are added to the I/Q sum as more signals, after the noise and
/// before the envelope, in both the transmitter and the noise-only branch, so a jammer alone breaks squelch and
/// AM beats, AGC capture and squelch effects emerge. On FM they take part in capture and the threshold is taken
/// on SINR; see <see cref="ComputeFmJammerWeights"/>. SATCOM slots ignore them (their link budget handles
/// jamming). Jammers are not streams, so they can't trip SignalBlocked or the stream-gap logic.
///
/// It holds no BASS state, so it can be driven from tests with seeded noise. One instance per
/// <see cref="RadioPlayback"/>; it reuses its scratch buffers, so it is DSP-thread only and allocates
/// nothing per sample (and nothing per callback once its scratch has grown to the stream count).
/// </summary>
internal sealed class SlotReceiver
{
    private const int SampleRate = RadioPlayback.SampleRate;
    private const double ModIndex = RadioPlayback.ModIndex;

    // KY-58 wrong-variable acknowledgement beep. SIMULATION CHOICE: public documentation does not
    // establish an exact tone — chosen distinct from any documented HAVE QUICK TONE frequency
    // (1020/1667 Hz) so the two are never confused.
    internal const double WrongKeyBeepFrequencyHz = 900.0;
    internal const float WrongKeyBeepAmplitude = 0.6f;

    /// <summary>What replaces the transmitters' voice in the mix (see the COMSEC switch).</summary>
    private enum SampleOverride { None, CipherNoise, Beep }

    private readonly List<ReceivedCarrier> _transec = new();
    private readonly List<ReceivedCarrier> _dominantOnly = new(1);
    private readonly List<float> _relativePowers = new();
    private readonly List<float> _captured = new();
    private float[] _captureWeights = [];
    private double[] _phaseStepStarts = [];
    private double[] _phaseStepSlopes = [];
    private double[] _amplitudeStarts = [];
    private double[] _amplitudeSlopes = [];
    private double[] _phases = [];
    private short[] _satcomPcmScratch = [];

    /// <summary>FM capture with jammers is re-decided every this many samples (2 ms), so look-through gaps,
    /// pulses and hop hits capture and release the receiver as they happen.</summary>
    internal const int CaptureBlockSamples = RadioPlayback.SampleRate / 500;

    // Jammer scratch, shared by every slot (they run one after another). Per jammer: I, Q and per-capture-block
    // power; then the weighted sum, and per-sample FM weights for the transmitters and noise.
    private float[][] _jamI = [];
    private float[][] _jamQ = [];
    private float[][] _jamBlockPower = [];
    private float[][] _jamBlockWeight = [];
    private bool[] _jamNoiseLike = [];
    private float[] _jamSumI = [];
    private float[] _jamSumQ = [];
    private float[] _fmNoiseScales = [];
    private float[] _fmTxWeights = [];
    private float[] _blockNoiseScale = [];
    private float[] _blockTxWeight = [];
    private long _frame;

    /// <summary>
    /// Run one slot for one callback.
    /// </summary>
    /// <param name="slot">The receiving slot; its receiver state advances.</param>
    /// <param name="transmitting">The carriers with audio on this slot's channel this callback. Not
    /// modified; their CarrierPhase is the start phase, which the caller advances once per callback.</param>
    /// <param name="output">Filled with the slot's mono audio; its length is the callback length.</param>
    /// <param name="sampleNum">The wrong-key beep's running sample counter (RadioPlayback._sampleNum).</param>
    public SlotReceiveResult Process(RadioPlayback.RadioConfig slot, IReadOnlyList<ReceivedCarrier> transmitting,
        Span<float> output, int sampleNum)
    {
        int samples = output.Length;
        var result = new SlotReceiveResult();

        // TRANSEC gate: HAVE QUICK on/off must match for this slot to see a
        // transmitter's RF at all. DCS only exposes a binary "HQ selected"
        // flag (no real WOD/TOD), so this is a straight match/no-match gate.
        _transec.Clear();
        for (int k = 0; k < transmitting.Count; ++k)
            if (transmitting[k].HqOn == slot.HqOn)
                _transec.Add(transmitting[k]);
        var dominant = _transec.Count > 0 ? _transec[0] : null;
        bool carrierPresent = dominant != null;

        var (matchedCipher, wrongKey, passiveCiphertext) = carrierPresent
            ? KySecureReceiveState.Classify(slot.Enc, slot.EncKey, slot.CryptoCapable,
                dominant!.Enc, dominant.EncKey)
            : (false, false, false);
        var comsecOutcome = slot.KySecureState.Update(carrierPresent, matchedCipher, wrongKey,
            passiveCiphertext, samples);

        result.ComsecOutcome = comsecOutcome;
        result.MatchedCipher = matchedCipher;
        result.WrongKey = wrongKey;
        result.PassiveCiphertext = passiveCiphertext;
        result.Dominant = dominant;
        if (comsecOutcome != slot.LastLoggedComsecOutcome)
        {
            result.ComsecOutcomeChanged = true;
            slot.LastLoggedComsecOutcome = comsecOutcome;
        }

        // "Modified PTT tone": a short cue at the moment this slot starts
        // successfully receiving a decrypted secure transmission.
        bool matchedCipherNow = comsecOutcome == KySecureOutcome.Pass && matchedCipher;
        if (matchedCipherNow && !slot.WasMatchedCipherActive)
            result.TriggerRxTone = true;
        slot.WasMatchedCipherActive = matchedCipherNow;

        // What carries this buffer, and whether its content is real voice or
        // something synthesized in its place. A single-element list is used for
        // the synthesized cases (noise burst / passive ciphertext / wrong-key
        // beep) purely to reuse the existing multi-transmitter beat-frequency math
        // below with k=0 and no beats.
        List<ReceivedCarrier> transmittingStreams;
        var sampleOverride = SampleOverride.None;
        _dominantOnly.Clear();
        if (dominant != null) _dominantOnly.Add(dominant);
        switch (comsecOutcome)
        {
            case KySecureOutcome.Pass:
                transmittingStreams = _transec;
                break;
            case KySecureOutcome.NoiseBurst:
            case KySecureOutcome.PassiveCiphertext:
                transmittingStreams = _dominantOnly;
                sampleOverride = SampleOverride.CipherNoise;
                break;
            case KySecureOutcome.Beep:
                transmittingStreams = _dominantOnly;
                sampleOverride = SampleOverride.Beep;
                break;
            case KySecureOutcome.Muted:
            case KySecureOutcome.Idle:
            default:
                _dominantOnly.Clear();
                transmittingStreams = _dominantOnly;
                break;
        }

        LastMixed = transmittingStreams;
        var numStreams = transmittingStreams.Count;
        EnsureCapacity(numStreams);
        // Each carrier's level as of the end of this callback, which the FM capture
        // and noise-cliff decisions below are made on. The mix itself ramps each
        // carrier from CarrierStart to CarrierEnd (see CarrierSmoother), scaled by
        // captureWeights.
        var relativePowers = _relativePowers;
        relativePowers.Clear();
        var captureWeights = _captureWeights;
        // Each carrier's phase step per sample (ramping from start to end of the
        // callback), relative to the channel frequency, and its running phase for
        // this slot. Every slot starts from the stream's stored CarrierPhase and
        // doesn't write it back; it's advanced once for the whole channel after
        // the slot loop.
        var phaseStepStarts = _phaseStepStarts;
        var phaseStepSlopes = _phaseStepSlopes;
        var amplitudeStarts = _amplitudeStarts;
        var amplitudeSlopes = _amplitudeSlopes;
        var phases = _phases;
        for (int k = 0; k < numStreams; ++k)
        {
            var tx = transmittingStreams[k];
            // The smoothed SNR is already converted from dB to linear amplitude.
            relativePowers.Add((float)tx.CarrierEnd.Amplitude);
            captureWeights[k] = 1f;
            amplitudeStarts[k] = tx.CarrierStart.Amplitude;
            amplitudeSlopes[k] = (tx.CarrierEnd.Amplitude - tx.CarrierStart.Amplitude) / samples;
            // Relative to the channel frequency, not the first transmitter: that one
            // can change between callbacks, which would jump every phase now that
            // they're integrated. The envelope doesn't care about a rotation applied
            // to all carriers at once, so any stable reference works.
            phaseStepStarts[k] = tx.CarrierPhaseStepStart;
            phaseStepSlopes[k] = (tx.CarrierPhaseStepEnd - tx.CarrierPhaseStepStart) / samples;
            phases[k] = tx.CarrierPhase;
        }

        // Jammers: render this callback's waveforms (SATCOM slots skip them). Responsive jammers key on
        // any wanted transmission that got through TRANSEC, whatever COMSEC then makes of it.
        int numJammers = slot.IsSatcomActive ? 0 : RenderJammers(slot, samples, _transec.Count > 0);
        bool fmJammed = numJammers > 0 && slot.Modulation == ModulationType.FM;

        if (slot.IsSatcomActive)
        {
            // The squelch gate stays shut for SATCOM slots (see ProcessSatcom):
            // the vocoder's own frame-loss/concealment model, not AGC-driven
            // squelch, decides what's audible.
            slot.SquelchOpen = false;

            // Entirely different path: frame-based digital vocoder processing
            // instead of the AM/FM per-sample envelope/AGC/squelch/bandpass math
            // below. Fills output just like the AM/FM branch does, so the shared
            // pan/volume mix downstream still applies unchanged.
            ProcessSatcom(slot, transmittingStreams, sampleOverride, sampleNum, output);
        }
        else
        {
            // Asymmetric open/close with hysteresis (upstream a4daedc); see StepSquelch.
            var (squelchOpenPower, squelchClosePower) = slot.SquelchPowers();

            // Noise is always there!
            // The question is just "how loud compared to the signal?"
            // (What's the SNR?)
            // We draw independent I and Q noise per sample below - if we used
            // I_noise[n] = Q_noise[n], we wouldn't have random noise,
            // we'd have a single signal with a fixed phase (45 deg).
            var noiseGen = slot.NoiseGenerator;

            // FM vs AM (constant for this whole chunk, computed once from the
            // per-transmitter powers above): FM receivers capture onto the strongest
            // co-channel signal instead of mixing weaker ones in (ApplyFmCaptureEffect's
            // per-transmitter weights go in captureWeights), and FM is quieter than AM above a
            // threshold SNR but noisier below it (the classic FM "cliff") instead of
            // AM's smooth linear noise-vs-SNR curve. AM (fmNoiseScale stays 1) is
            // completely unaffected by any of this.
            float fmNoiseScale = 1f;
            if (slot.Modulation == ModulationType.FM && numStreams > 0)
            {
                float maxPower = 0f;
                for (int k = 0; k < relativePowers.Count; ++k)
                    maxPower = Math.Max(maxPower, relativePowers[k]);

                if (numStreams > 1)
                {
                    var captured = _captured;
                    captured.Clear();
                    captured.AddRange(relativePowers);
                    ApplyFmCaptureEffect(captured, maxPower);
                    for (int k = 0; k < numStreams; ++k)
                        captureWeights[k] = relativePowers[k] > 0f ? captured[k] / relativePowers[k] : 0f;
                }

                if (maxPower > 0f)
                    fmNoiseScale = FmNoiseScale(20f * MathF.Log10(maxPower));
            }

            if (numJammers > 0)
                MixJammers(numJammers, numStreams, samples, fmJammed);

            if (numStreams > 0)
            {
                // Calculate E[n] for each sample n.
                for (int n = 0; n < samples; ++n)
                {
                    // Start with our noise.
                    // I and Q must come from independent draws; see BackgroundNoiseGenerator.
                    float ni0 = 0f, nq0 = 0f;
                    noiseGen?.Next(out ni0, out nq0);
                    float noiseScale = fmJammed ? _fmNoiseScales[n] : fmNoiseScale;
                    double i = ni0 * noiseScale;
                    double q = nq0 * noiseScale;
                    // Jammers are just more signals in the same I/Q sum (already FM-weighted).
                    if (numJammers > 0)
                    {
                        i += _jamSumI[n];
                        q += _jamSumQ[n];
                    }
                    for (int k = 0; k < numStreams; ++k)
                    {
                        // θ_k is the phasor that rotates around at each beat frequency k.
                        double theta = phases[k];
                        // Sum IQ components _before_ taking the length of the vector,
                        // as that's a nonlinear operation.
                        // A stream can be shorter than `samples` if it came up
                        // short on buffered audio this callback (see RadioPlayback's drain
                        // loop) -- hold its carrier with no voice modulation for
                        // the remainder rather than indexing past its samples.
                        var txSpan = transmittingStreams[k].Samples.Span;
                        float samp = sampleOverride switch
                        {
                            SampleOverride.CipherNoise => slot.CipherNoise.NextSample(),
                            SampleOverride.Beep => BeepSample(n, sampleNum),
                            _ => n < txSpan.Length ? txSpan[n] : 0f,
                        };
                        // n + 1 here and for the phase step, so the last sample lands on
                        // CarrierEnd, which is where the next callback starts.
                        double carrier = (amplitudeStarts[k] + amplitudeSlopes[k] * (n + 1)) *
                                         (fmJammed ? _fmTxWeights[k * samples + n] : captureWeights[k]);
                        double amplitude = carrier * (1 + samp * ModIndex);
                        i += amplitude * Math.Cos(theta);
                        q += amplitude * Math.Sin(theta);
                        phases[k] = RadioPlayback.WrapPhase(theta + phaseStepStarts[k] + phaseStepSlopes[k] * (n + 1));
                    }

                    // Take the envelope.
                    output[n] = (float)Math.Sqrt(i * i + q * q);

                    // Update the AGC, which sets the playback level...
                    slot.Agc.Apply(output[n]);

                    // ...and the squelch detectors, which decide whether we hear
                    // anything at all. Squelch reads its own power detectors, not the
                    // AGC gain -- see SquelchTau: impulsive noise can swing the AGC
                    // across threshold for a sample or two between real transmissions.
                    //
                    // NB: Handle squelch per sample, before the band-pass smooths the edges!
                    // We don't want to gate the whole buffer (or not!) based on a single value.
                    if (slot.StepSquelch(output[n] * output[n],
                            squelchOpenPower, squelchClosePower))
                    {
                        output[n] = output[n] / slot.Agc.D1;
                        // SIM_VINSON: successfully-decrypted secure voice gets a CVSD-like
                        // texture instead of sounding identical to clear analog AM.
                        if (comsecOutcome == KySecureOutcome.Pass && matchedCipher)
                            output[n] = slot.CvsdEffect.Process(output[n]);
                    }
                    else
                    {
                        output[n] = 0;
                    }
                }
            }
            // Nothing is transmitting except noise, decay AGC back to unity.
            else
            {
                for (int n = 0; n < samples; ++n)
                {
                    float ni1 = 0f, nq1 = 0f;
                    noiseGen?.Next(out ni1, out nq1);
                    double i = ni1;
                    double q = nq1;
                    // A jammer alone is heard (and breaks squelch) just like a carrier would be.
                    if (numJammers > 0)
                    {
                        if (fmJammed)
                        {
                            i *= _fmNoiseScales[n];
                            q *= _fmNoiseScales[n];
                        }
                        i += _jamSumI[n];
                        q += _jamSumQ[n];
                    }
                    output[n] = (float)Math.Sqrt(i * i + q * q);
                    slot.Agc.Apply(output[n]);

                    // See above.
                    if (slot.StepSquelch(output[n] * output[n],
                            squelchOpenPower, squelchClosePower))
                    {
                        output[n] = output[n] / slot.Agc.D1;
                    }
                    else
                    {
                        output[n] = 0;
                    }
                }
            }

            for (int n = 0; n < samples; ++n)
            {
                // Bandpass the signal, which removes the DC component and centers us around 0
                output[n] = slot.LowPass.Process(
                    slot.HighPass.Process(output[n]));
            }
        }

        // Catch a NaN or infinity before it reaches the shared mix (and the 3DAS binaural
        // state, recording and HUD feed), where it would take out every radio. Silence this
        // slot for this callback and start its receiver over (upstream a12b86a). ResetReceiver
        // also closes the gate, so a NaN can't leave it latched open.
        if (!slot.IsFinite(output) || !JammersFinite(slot))
        {
            if (!slot.WasNonFinite)
            {
                result.WentNonFinite = true;
                result.NonFiniteAgc = slot.Agc.D1;
                result.NonFiniteSquelchDetector = slot.SquelchDetector.D1;
                result.NonFiniteLastOutput = output.IsEmpty ? 0f : output[^1];
            }
            slot.WasNonFinite = true;
            slot.ResetReceiver();
            // Jammer state (phases, filters) starts over too; it's re-seeded, so it stays deterministic.
            slot.JammerStates.Clear();
            output.Clear();
        }
        else
        {
            slot.WasNonFinite = false;
        }

        return result;
    }

    /// <summary>The carriers the last <see cref="Process"/> call mixed (after TRANSEC/COMSEC), for logs.
    /// Valid until the next call.</summary>
    public IReadOnlyList<ReceivedCarrier> LastMixed { get; private set; } = [];

    private static float BeepSample(int n, int sampleNum) =>
        (float)Math.Sin(2.0 * Math.PI * WrongKeyBeepFrequencyHz * (n + sampleNum) / SampleRate)
        * WrongKeyBeepAmplitude;

    /// <summary>
    /// The FM threshold ("cliff"): how much the receiver noise is scaled for a captured carrier at
    /// <paramref name="snrDb"/> over it. ~0.15x well above threshold, ~2.5x well below.
    /// </summary>
    internal static float FmNoiseScale(float snrDb)
    {
        const float thresholdDb = 8f;  // knee position
        const float steepness = 0.35f; // dB^-1, controls how sharp the cliff is
        float t = 1f / (1f + MathF.Exp(-(thresholdDb - snrDb) * steepness));
        return 0.15f + t * 2.35f;
    }

    /// <summary>
    /// FM receivers (frequency discriminators) "capture" onto the strongest co-channel signal
    /// and largely suppress weaker ones, unlike AM's envelope detector which mixes/beats them
    /// together. Modeled as a soft power-ratio
    /// gate rather than a hard winner-take-all: transmitters within ~1 dB of the strongest are
    /// only lightly suppressed (matching the real "picket fencing"/motorboating you hear when
    /// two FM signals are nearly equal strength); anything much weaker fades out sharply.
    /// Mutates <paramref name="relativePowers"/> in place; <paramref name="maxPower"/> must
    /// already be the max of that list (callers compute it anyway for the noise-threshold calc).
    /// </summary>
    internal static void ApplyFmCaptureEffect(List<float> relativePowers, float maxPower)
    {
        if (maxPower <= 0f) return;

        for (int k = 0; k < relativePowers.Count; ++k)
            relativePowers[k] *= FmCaptureWeight(relativePowers[k], maxPower);
    }

    /// <summary>The capture weight (0..1) of a signal at amplitude <paramref name="amplitude"/> against the
    /// strongest at <paramref name="maxAmplitude"/>; see <see cref="ApplyFmCaptureEffect"/>.</summary>
    internal static float FmCaptureWeight(float amplitude, float maxAmplitude)
    {
        const float captureRatioDb = 1.0f; // half-suppression point below the strongest signal
        const float steepnessPerDb = 2.5f;

        float deltaDb = 20f * MathF.Log10(Math.Max(amplitude, 1e-6f) / maxAmplitude); // <= 0
        return 1f / (1f + MathF.Exp(-(deltaDb + captureRatioDb) * steepnessPerDb));
    }

    /// <summary>
    /// SATCOM's per-slot processing: routes the dominant transmitter's audio through
    /// SatcomVocoder instead of the AM/FM envelope-detector math, filling
    /// <paramref name="output"/> just like the AM/FM branch
    /// does so the shared pan/volume mix downstream is unaffected. Runs synchronously on the
    /// audio callback thread rather than a background worker -- a conscious tradeoff (see
    /// docs/SATCOM_SIMULATION.md): the vocoder's per-frame cost (10th-order LPC + autocorrelation
    /// pitch search over a ~22ms/180-sample-at-8kHz frame, amortized over ~1000+ samples of
    /// output at 48kHz) is small next to genuinely expensive real-time-unsafe work (disk/network
    /// IO, large allocations), and doing this inline avoids introducing a new cross-thread
    /// synchronization surface into a live audio system that can't be listened to and verified
    /// mid-development. Revisit if profiling ever shows this actually matters.
    ///
    /// Does not apply the AM/FM beat-frequency multi-transmitter math -- SATCOM is a digital
    /// point-to-point-per-transponder link, not two analog carriers beating together; if the
    /// KY-58/COMSEC gate above already narrowed <paramref name="transmittingStreams"/> to a
    /// single dominant stream (or a synthesized noise-burst/beep via
    /// <paramref name="sampleOverride"/>), that's exactly what gets vocoded, so SATCOM traffic
    /// still honors the existing COMSEC match/mismatch behavior for free.
    /// </summary>
    private void ProcessSatcom(RadioPlayback.RadioConfig slot, List<ReceivedCarrier> transmittingStreams,
        SampleOverride sampleOverride, int sampleNum, Span<float> output)
    {
        int samples = output.Length;
        if (transmittingStreams.Count > 0 && slot.SatcomVocoderInstance != null)
        {
            var span = transmittingStreams[0].Samples.Span;
            if (span.Length >= samples)
            {
                if (_satcomPcmScratch.Length < samples)
                    _satcomPcmScratch = new short[samples];
                for (int n = 0; n < samples; ++n)
                {
                    float samp = sampleOverride switch
                    {
                        SampleOverride.CipherNoise => slot.CipherNoise.NextSample(),
                        SampleOverride.Beep => BeepSample(n, sampleNum),
                        _ => span[n],
                    };
                    _satcomPcmScratch[n] = (short)Math.Clamp(samp * short.MaxValue, short.MinValue, short.MaxValue);
                }

                var processed = slot.SatcomVocoderInstance.ProcessBuffer(
                    _satcomPcmScratch.AsSpan(0, samples), slot.SatcomFrameErrorRate, slot.SatcomBurstSeverity);
                foreach (var s in processed)
                    slot.SatcomOutputQueue.Enqueue(s / (float)short.MaxValue);

                // Bound the queue so a sustained cadence mismatch between input chunk size and
                // completed-frame output size can't grow latency unboundedly -- drop the oldest
                // buffered audio, favoring low latency over never losing a sample (1s cap, well
                // above what normal operation should ever accumulate).
                while (slot.SatcomOutputQueue.Count > SampleRate)
                    slot.SatcomOutputQueue.Dequeue();
            }
        }
        // No carrier (or vocoder not yet constructed this cycle): SATCOM has no meaningful idle
        // "static" the way analog AM/FM does -- a digital link is either receiving a real signal
        // or it isn't -- so just drain whatever's still buffered rather than synthesizing noise.
        //
        // Below its target depth, the queue is still filling the real (server-computed) GEO
        // propagation delay -- output silence, not whatever partial audio has arrived so far.
        // Once full, it drains one sample per cycle same as before; this naturally covers both
        // "audio starts ~250ms after the far end keys up" and "audio keeps arriving for ~250ms
        // after the far end un-keys" (drain always runs, whether or not a transmitter is active
        // this cycle) without needing separate onset/release logic.
        for (int n = 0; n < samples; ++n)
            output[n] = slot.SatcomOutputQueue.Count > slot.SatcomTargetLatencySamples
                ? slot.SatcomOutputQueue.Dequeue()
                : 0f;
    }

    private static bool JammersFinite(RadioPlayback.RadioConfig slot)
    {
        foreach (var w in slot.JammerStates.Values)
            if (!w.IsFinite) return false;
        return true;
    }

    /// <summary>
    /// Render every jammer on the slot into _jamI/_jamQ (one row each) and its capture-block powers. Creates
    /// state for new ids and drops state for ids no longer listed. Returns how many rows were rendered.
    /// </summary>
    private int RenderJammers(RadioPlayback.RadioConfig slot, int samples, bool wantedPresent)
    {
        var jammers = slot.Jammers;
        var states = slot.JammerStates;
        if (jammers.Count == 0)
        {
            if (states.Count > 0) states.Clear();
            return 0;
        }

        _frame++;
        int blocks = (samples + CaptureBlockSamples - 1) / CaptureBlockSamples;
        EnsureJammerCapacity(jammers.Count, samples, blocks);

        int count = 0;
        for (int j = 0; j < jammers.Count; ++j)
        {
            var c = jammers[j];
            if (c?.JammerId == null || c.Params == null) continue;
            if (!states.TryGetValue(c.JammerId, out var waveform))
            {
                waveform = new JammerWaveform(c.JammerId, c.Params.Seed);
                states[c.JammerId] = waveform;
            }
            // The same id twice in one list is one jammer.
            if (waveform.LastFrame == _frame) continue;
            waveform.LastFrame = _frame;

            waveform.Render(c, wantedPresent, _jamI[count].AsSpan(0, samples), _jamQ[count].AsSpan(0, samples),
                _jamBlockPower[count].AsSpan(0, blocks), CaptureBlockSamples);
            _jamNoiseLike[count] = JammerWaveform.IsNoiseLike(c.Technique);
            count++;
        }

        // Forget jammers that left the list (Remove during enumeration is allowed on Dictionary).
        if (states.Count > count)
        {
            foreach (var (id, waveform) in states)
                if (waveform.LastFrame != _frame)
                    states.Remove(id);
        }

        return count;
    }

    /// <summary>
    /// Sum the rendered jammers into _jamSumI/_jamSumQ. AM: as they are (an envelope detector has no capture;
    /// AGC and the beats do the rest). FM: weighted per capture block, see <see cref="ComputeFmJammerWeights"/>,
    /// which also fills the per-sample transmitter weights and noise scale the mix uses instead of the
    /// per-callback ones.
    /// </summary>
    private void MixJammers(int numJammers, int numStreams, int samples, bool fm)
    {
        var sumI = _jamSumI.AsSpan(0, samples);
        var sumQ = _jamSumQ.AsSpan(0, samples);
        sumI.Clear();
        sumQ.Clear();

        if (!fm)
        {
            for (int j = 0; j < numJammers; ++j)
            {
                var ji = _jamI[j];
                var jq = _jamQ[j];
                for (int n = 0; n < samples; ++n)
                {
                    sumI[n] += ji[n];
                    sumQ[n] += jq[n];
                }
            }
            return;
        }

        int blocks = (samples + CaptureBlockSamples - 1) / CaptureBlockSamples;
        EnsureFmTxCapacity(numStreams, samples, blocks);
        ComputeFmJammerWeights(numJammers, numStreams, blocks);

        // Ramp every weight across each block from the previous block's value, so capture and release are
        // smooth (the first block of a callback holds its own value).
        for (int b = 0; b < blocks; ++b)
        {
            int start = b * CaptureBlockSamples;
            int len = Math.Min(CaptureBlockSamples, samples - start);
            int prev = b == 0 ? 0 : b - 1;
            for (int n = 0; n < len; ++n)
            {
                float f = (n + 1) / (float)len;
                int at = start + n;
                _fmNoiseScales[at] = Lerp(_blockNoiseScale[prev], _blockNoiseScale[b], f);
                for (int k = 0; k < numStreams; ++k)
                    _fmTxWeights[k * samples + at] = Lerp(_blockTxWeight[k * blocks + prev], _blockTxWeight[k * blocks + b], f);
                for (int j = 0; j < numJammers; ++j)
                {
                    float w = Lerp(_jamBlockWeight[j][prev], _jamBlockWeight[j][b], f);
                    sumI[at] += _jamI[j][at] * w;
                    sumQ[at] += _jamQ[j][at] * w;
                }
            }
        }
    }

    private static float Lerp(float a, float b, float f) => a + (b - a) * f;

    /// <summary>
    /// FM capture and threshold with jammers, per capture block. Every transmitter and jammer competes in the
    /// same capture decision as transmitters do among themselves (<see cref="FmCaptureWeight"/> against the
    /// strongest), so a carrier-like jammer ~3 dB over the voice captures the receiver and the voice vanishes,
    /// and one ~6 dB under it is suppressed. The threshold noise scale is then taken on SINR rather than SNR:
    /// the strongest carrier (transmitter or carrier-like jammer) against unit noise plus noise-like jammers
    /// plus whatever weaker carrier-like jammers survive capture. Noise-like jammers aren't a carrier to capture
    /// onto: they're noise to the limiter, so they scale with that same threshold factor. A CW jammer that
    /// captures leaves near-silence (FM 24-33's "subtle jamming"); a noise jammer pushes the voice down the
    /// cliff (more noise at −10…−6 dB J/S, denial from 0 dB).
    /// </summary>
    private void ComputeFmJammerWeights(int numJammers, int numStreams, int blocks)
    {
        for (int b = 0; b < blocks; ++b)
        {
            float maxAll = 0f, maxCarrier = 0f;
            int participants = numStreams;
            for (int k = 0; k < numStreams; ++k)
            {
                maxAll = Math.Max(maxAll, _relativePowers[k]);
                maxCarrier = Math.Max(maxCarrier, _relativePowers[k]);
            }
            for (int j = 0; j < numJammers; ++j)
            {
                float a = MathF.Sqrt(Math.Max(0f, _jamBlockPower[j][b]));
                if (!float.IsFinite(a)) a = 0f;
                if (a > 1e-3f) participants++;
                maxAll = Math.Max(maxAll, a);
                if (!_jamNoiseLike[j]) maxCarrier = Math.Max(maxCarrier, a);
            }

            bool compete = participants > 1 && maxAll > 0f;
            for (int k = 0; k < numStreams; ++k)
            {
                float a = _relativePowers[k];
                _blockTxWeight[k * blocks + b] = compete ? FmCaptureWeight(a, maxAll) : (a > 0f || maxAll <= 0f ? 1f : 0f);
            }

            double interference = 1.0;
            for (int j = 0; j < numJammers; ++j)
            {
                float a = MathF.Sqrt(Math.Max(0f, _jamBlockPower[j][b]));
                if (!float.IsFinite(a)) a = 0f;
                if (_jamNoiseLike[j])
                {
                    interference += (double)a * a;
                    continue;
                }
                float w = compete ? FmCaptureWeight(a, maxAll) : 1f;
                _jamBlockWeight[j][b] = w;
                if (a < maxCarrier) interference += (double)a * w * a * w;
            }

            float noiseScale = maxCarrier > 0f
                ? FmNoiseScale((float)(10.0 * Math.Log10((double)maxCarrier * maxCarrier / interference)))
                : 1f;
            _blockNoiseScale[b] = noiseScale;
            for (int j = 0; j < numJammers; ++j)
                if (_jamNoiseLike[j]) _jamBlockWeight[j][b] = noiseScale;
        }
    }

    private void EnsureJammerCapacity(int jammers, int samples, int blocks)
    {
        if (_jamI.Length < jammers)
        {
            int size = Math.Max(jammers, 4);
            Array.Resize(ref _jamI, size);
            Array.Resize(ref _jamQ, size);
            Array.Resize(ref _jamBlockPower, size);
            Array.Resize(ref _jamBlockWeight, size);
            _jamNoiseLike = new bool[size];
        }
        for (int j = 0; j < _jamI.Length; ++j)
        {
            if (_jamI[j] == null || _jamI[j].Length < samples)
            {
                _jamI[j] = new float[samples];
                _jamQ[j] = new float[samples];
            }
            if (_jamBlockPower[j] == null || _jamBlockPower[j].Length < blocks)
            {
                _jamBlockPower[j] = new float[blocks];
                _jamBlockWeight[j] = new float[blocks];
            }
        }
        if (_jamSumI.Length < samples)
        {
            _jamSumI = new float[samples];
            _jamSumQ = new float[samples];
            _fmNoiseScales = new float[samples];
        }
        if (_blockNoiseScale.Length < blocks) _blockNoiseScale = new float[blocks];
    }

    private void EnsureFmTxCapacity(int numStreams, int samples, int blocks)
    {
        if (_fmTxWeights.Length < numStreams * samples) _fmTxWeights = new float[numStreams * samples];
        if (_blockTxWeight.Length < numStreams * blocks) _blockTxWeight = new float[numStreams * blocks];
    }

    private void EnsureCapacity(int count)
    {
        if (_captureWeights.Length >= count) return;
        int size = Math.Max(count, 4);
        _captureWeights = new float[size];
        _phaseStepStarts = new double[size];
        _phaseStepSlopes = new double[size];
        _amplitudeStarts = new double[size];
        _amplitudeSlopes = new double[size];
        _phases = new double[size];
    }
}
