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
/// threshold, the complex-baseband (I/Q) sum with noise, envelope detection, AGC, squelch, CVSD texture,
/// band-pass and the NaN guard.
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

            if (numStreams > 0)
            {
                // Calculate E[n] for each sample n.
                for (int n = 0; n < samples; ++n)
                {
                    // Start with our noise.
                    // I and Q must come from independent draws; see BackgroundNoiseGenerator.
                    float ni0 = 0f, nq0 = 0f;
                    noiseGen?.Next(out ni0, out nq0);
                    double i = ni0 * fmNoiseScale;
                    double q = nq0 * fmNoiseScale;
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
                        double carrier = (amplitudeStarts[k] + amplitudeSlopes[k] * (n + 1)) * captureWeights[k];
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
        if (!slot.IsFinite(output))
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
