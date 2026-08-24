using System;
using OpenFreqAudio.Satcom;

namespace OpenFreqAudio.Tests
{
    public class SatcomLpcTests
    {
        [Fact]
        public void SynthesisFilterNeverProducesNaNOrInfinity()
        {
            var filter = new LpcSynthesisFilter(10);
            var rng = new Random(1);
            // Deliberately pathological/near-unstable-adjacent coefficients (reflection
            // coefficients clamped to just under 1 -- the worst case ReflectionToLpc can produce).
            var reflection = new double[11];
            for (var i = 1; i <= 10; i++) reflection[i] = 0.99;
            var lpc = SatcomLpc.ReflectionToLpc(reflection, 10);

            for (var i = 0; i < 10000; i++)
            {
                var excitation = (rng.NextDouble() * 2 - 1) * 20000;
                var output = filter.Process(excitation, lpc);
                Assert.False(double.IsNaN(output));
                Assert.False(double.IsInfinity(output));
                // +/-8.0, matching LpcSynthesisFilter.Process's own safety-net clamp bound (a
                // normalized-domain value, not the PCM16-scale +/-32000 this used to assert --
                // see that clamp's own doc comment for why it changed).
                Assert.InRange(output, -8.0, 8.0);
            }
        }

        [Fact]
        public void SynthesisFilterImpulseResponseDecaysInsteadOfSaturating()
        {
            // Regression test for a sign bug in LpcSynthesisFilter.Process: y[n] must be
            // excitation[n] + sum(a[i]*y[n-i]) to match SatcomLpc.LevinsonDurbin/ReflectionToLpc's
            // own convention (Rabiner & Schafer's autocorrelation method). The previous "-="
            // feedback sign was self-consistently wrong -- individually-clamped |k|<1 reflection
            // coefficients only guarantee stability under the CORRECT sign; with the wrong sign,
            // even mild resonance diverged until the output clamp pinned it into a saturated
            // every-other-sample square wave, which is what "very high pitch/awful" SATCOM receive
            // audio traced back to.
            //
            // Reflection coefficients as SatcomVocoderEncoder.Analyze actually produced them for a
            // loud, clean 150 Hz tone (a near-worst-case, close to the |k|<0.999 clamp boundary on
            // k1) -- not an arbitrary synthetic array, so this ties directly to the real repro.
            var reflection = new[]
            {
                0.0, 0.99070, -0.73833, -0.41080, -0.27474, -0.19850, -0.14906, -0.11424, -0.08849, -0.06888, -0.05368
            };
            var lpc = SatcomLpc.ReflectionToLpc(reflection, 10);
            var filter = new LpcSynthesisFilter(10);

            var impulseResponse = new double[100];
            for (var n = 0; n < impulseResponse.Length; n++)
                impulseResponse[n] = filter.Process(n == 0 ? 1.0 : 0.0, lpc);

            // A genuinely stable resonator's impulse response rings and then settles back toward
            // zero; a filter driven unstable by a sign error instead grows without bound until
            // it's pinned at the output clamp and stays there. Comparing the tail to a modest
            // ceiling (not the clamp bound itself) catches that failure mode without pinning the
            // test to an exact decay curve.
            var tailMaxAbs = 0.0;
            for (var n = 70; n < impulseResponse.Length; n++)
                tailMaxAbs = Math.Max(tailMaxAbs, Math.Abs(impulseResponse[n]));

            Assert.True(tailMaxAbs < 20.0,
                $"Expected a unit impulse's response to have decayed close to zero by sample 70, got {tailMaxAbs}");
        }

        [Fact]
        public void ReflectionToLpcAlwaysProducesAStableFilterRegardlessOfInput()
        {
            // Any reflection coefficients clamped to (-1,1) must yield a stable all-pole filter
            // -- verify by running white noise through it and confirming bounded output.
            var rng = new Random(2);
            for (var trial = 0; trial < 20; trial++)
            {
                var reflection = new double[11];
                for (var i = 1; i <= 10; i++) reflection[i] = rng.NextDouble() * 1.998 - 0.999;
                var lpc = SatcomLpc.ReflectionToLpc(reflection, 10);
                var filter = new LpcSynthesisFilter(10);

                for (var n = 0; n < 1000; n++)
                {
                    var output = filter.Process(rng.NextDouble() * 2 - 1, lpc);
                    Assert.False(double.IsNaN(output) || double.IsInfinity(output));
                }
            }
        }

        [Fact]
        public void LevinsonDurbinRecoversASingleSinusoidsDominantPole()
        {
            const int sampleRate = 8000;
            const double toneHz = 500;
            var frame = new double[400];
            for (var n = 0; n < frame.Length; n++)
                frame[n] = Math.Sin(2 * Math.PI * toneHz * n / sampleRate);

            var autocorr = SatcomLpc.Autocorrelate(frame, 10);
            var (lpc, reflection, error) = SatcomLpc.LevinsonDurbin(autocorr, 10);

            Assert.True(error >= 0);
            foreach (var k in reflection)
                Assert.InRange(k, -1.0, 1.0);
            Assert.Equal(11, lpc.Length);
        }
    }

    public class SatcomPitchEstimatorTests
    {
        [Fact]
        public void SilenceIsUnvoiced()
        {
            var estimator = new SatcomPitchEstimator(8000);
            var silence = new double[200];
            var (pitchHz, confidence) = estimator.Estimate(silence);
            Assert.Equal(0.0, pitchHz);
            Assert.Equal(0.0, confidence);
        }

        [Fact]
        public void PureToneIsDetectedNearItsTrueFrequency()
        {
            const int sampleRate = 8000;
            const double trueHz = 150.0;
            var estimator = new SatcomPitchEstimator(sampleRate);
            var frame = new double[400];
            for (var n = 0; n < frame.Length; n++)
                frame[n] = Math.Sin(2 * Math.PI * trueHz * n / sampleRate);

            (double pitchHz, double confidence) result = default;
            for (var i = 0; i < 3; i++) // let continuity filter settle
                result = estimator.Estimate(frame);

            Assert.True(result.pitchHz > 0, "Expected a voiced pitch estimate for a pure tone");
            Assert.InRange(result.pitchHz, trueHz * 0.85, trueHz * 1.15);
        }
    }

    public class SatcomChannelErrorModelTests
    {
        [Fact]
        public void ZeroFrameErrorRateProducesOnlyCleanFrames()
        {
            var model = new SatcomChannelErrorModel(42);
            for (var i = 0; i < 500; i++)
                Assert.Equal(SatcomFrameOutcome.Clean, model.NextOutcome(frameErrorRate: 0.0, burstSeverity: 0.0));
        }

        [Fact]
        public void CorruptNeverProducesUnsafeParameterValues()
        {
            var model = new SatcomChannelErrorModel(7);
            var reflection = new double[11];
            for (var i = 1; i <= 10; i++) reflection[i] = 0.5;

            for (var i = 0; i < 5000; i++)
            {
                var (pitchHz, energy, voicing, r) = model.Corrupt(200, 1.0, 0.5, reflection);
                Assert.True(pitchHz is 0 or (>= 60 and <= 400));
                Assert.InRange(energy, 0.0, 4.0);
                Assert.InRange(voicing, 0.0, 1.0);
                foreach (var k in r)
                    Assert.InRange(k, -0.999, 0.999);
            }
        }

        [Fact]
        public void SameSeedProducesDeterministicOutcomes()
        {
            var a = new SatcomChannelErrorModel(123);
            var b = new SatcomChannelErrorModel(123);
            for (var i = 0; i < 200; i++)
                Assert.Equal(a.NextOutcome(0.3, 0.5), b.NextOutcome(0.3, 0.5));
        }
    }

    public class SatcomVocoderDecoderTests
    {
        [Fact]
        public void ConcealmentOfManyConsecutiveLossesNeverProducesNaNAndEventuallyGoesSilent()
        {
            var decoder = new SatcomVocoderDecoder(8000, seed: 5);
            // Seed the decoder with one valid frame first.
            var encoder = new SatcomVocoderEncoder(8000);
            var frame = new double[180];
            for (var n = 0; n < frame.Length; n++) frame[n] = Math.Sin(2 * Math.PI * 150 * n / 8000.0);
            var analyzed = encoder.Analyze(frame);
            var encoded = SatcomFrameQuantizer.Quantize(analyzed, SatcomVocoderEncoder.LpcOrder);

            Span<double> outFrame = new double[180];
            decoder.Synthesize(encoded, SatcomFrameOutcome.Clean, null, outFrame);

            for (var i = 0; i < 20; i++)
            {
                decoder.Synthesize(null, SatcomFrameOutcome.Lost, null, outFrame);
                foreach (var s in outFrame)
                {
                    Assert.False(double.IsNaN(s));
                    Assert.False(double.IsInfinity(s));
                }
            }

            Assert.True(decoder.State.SyncLost, "Expected sync-lost after many consecutive concealed frames");
        }

        [Fact]
        public void CleanFrameProducesNonZeroOutputForVoicedSpeech()
        {
            var encoder = new SatcomVocoderEncoder(8000);
            var decoder = new SatcomVocoderDecoder(8000, seed: 9);
            var frame = new double[180];
            for (var n = 0; n < frame.Length; n++) frame[n] = 0.5 * Math.Sin(2 * Math.PI * 150 * n / 8000.0);
            var analyzed = encoder.Analyze(frame);
            var encoded = SatcomFrameQuantizer.Quantize(analyzed, SatcomVocoderEncoder.LpcOrder);

            Span<double> outFrame = new double[180];
            decoder.Synthesize(encoded, SatcomFrameOutcome.Clean, null, outFrame);

            var hasEnergy = false;
            foreach (var s in outFrame)
                if (Math.Abs(s) > 1e-6) hasEnergy = true;
            Assert.True(hasEnergy, "Expected non-silent output for clearly voiced input");
        }
    }

    public class SatcomVocoderEndToEndTests
    {
        private static short[] MakeToneWav(int sampleRate, double durationSeconds, double toneHz)
        {
            var n = (int)(sampleRate * durationSeconds);
            var buf = new short[n];
            for (var i = 0; i < n; i++)
                buf[i] = (short)(8000 * Math.Sin(2 * Math.PI * toneHz * i / sampleRate));
            return buf;
        }

        private static int CountZeroCrossings(short[] buf, int start, int len)
        {
            var crossings = 0;
            for (var i = start + 1; i < start + len; i++)
                if (buf[i - 1] != 0 && Math.Sign(buf[i]) != Math.Sign(buf[i - 1]))
                    crossings++;
            return crossings;
        }

        [Fact]
        public void OutputStaysNearInputPitchInsteadOfNyquistBuzz()
        {
            // Regression test for the LpcSynthesisFilter sign bug (see SatcomLpcTests) at the full
            // ProcessBuffer level: a runaway synthesis filter alternates every sample, which is a
            // tone at Nyquist (4 kHz at the vocoder's 8 kHz internal rate) regardless of the actual
            // input pitch -- audible as a harsh high-pitched buzz instead of voice.
            const int sr = 48000;
            const double toneHz = 150;
            var vocoder = new SatcomVocoder(networkSampleRate: sr, frameDurationSeconds: 0.0225, seed: 1);
            var n = (int)(sr * 1.5);
            var input = new short[n];
            for (var i = 0; i < n; i++)
                input[i] = (short)(8000 * Math.Sin(2 * Math.PI * toneHz * i / sr));

            var output = vocoder.ProcessBuffer(input, frameErrorRate: 0.0, burstSeverity: 0.0);

            // Measure the last 0.5s via zero-crossing rate, giving the pitch tracker/decoder time
            // to settle.
            var tailLen = Math.Min(output.Length, sr / 2);
            var tailStart = output.Length - tailLen;
            var crossings = CountZeroCrossings(output, tailStart, tailLen);
            var estimatedHz = crossings / 2.0 / (tailLen / (double)sr);

            // A healthy vocoder's output won't exactly match the input pitch (it's a synthetic
            // excitation shaped by LPC formants, not a pass-through), but it must stay in a
            // plausible voice-range ballpark -- nowhere near the ~4000 Hz Nyquist buzz the sign bug
            // produced.
            Assert.True(estimatedHz < 800.0,
                $"Expected output pitch well under the vocal range ceiling, got ~{estimatedHz:F0} Hz (Nyquist-buzz symptom)");
        }

        [Fact]
        public void ModerateAmplitudeInputProducesAudibleNotSilentOutput()
        {
            // Regression test for a PCM16-vs-normalized scale mismatch: SatcomVocoderEncoder.Analyze
            // computed Energy directly from raw PCM16-scale samples (thousands), but
            // SatcomFrameQuantizer's GainMinDb/GainMaxDb range and the decoder's excitation gain are
            // calibrated for a normalized (0 dBFS = amplitude 1.0) signal. Every realistic mic input
            // clamped to the quantizer's top gain bin, and the decoder's un-rescaled gain then
            // produced automatically near-silent output (approx -66 dBFS peak) regardless of how
            // loud the actual input was.
            const int sr = 48000;
            var vocoder = new SatcomVocoder(networkSampleRate: sr, frameDurationSeconds: 0.0225, seed: 4);
            var n = (int)(sr * 1.0);
            var input = new short[n];
            for (var i = 0; i < n; i++)
                input[i] = (short)(6000 * Math.Sin(2 * Math.PI * 150 * i / sr));

            var output = vocoder.ProcessBuffer(input, frameErrorRate: 0.0, burstSeverity: 0.0);

            var tailStart = Math.Max(0, output.Length - sr / 4);
            var maxAbs = 0;
            for (var i = tailStart; i < output.Length; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs((int)output[i]));

            Assert.True(maxAbs > 500,
                $"Expected clearly audible output (a few hundred+ out of a 32767 range) for a moderately loud input, got peak {maxAbs}");
        }

        [Fact]
        public void PerfectLinkProducesNoFrameLossAndOutputExists()
        {
            var vocoder = new SatcomVocoder(networkSampleRate: 48000, frameDurationSeconds: 0.0225, seed: 1);
            var input = MakeToneWav(48000, 1.0, 150);

            var output = vocoder.ProcessBuffer(input, frameErrorRate: 0.0, burstSeverity: 0.0);

            Assert.True(output.Length > 0);
            Assert.False(vocoder.DecoderState.SyncLost);
            var hasEnergy = false;
            foreach (var s in output)
                if (Math.Abs((int)s) > 100) hasEnergy = true;
            Assert.True(hasEnergy, "Expected audible output for a perfect link");
        }

        [Fact]
        public void ZeroPercentQualityProducesNoIntelligibleOutput()
        {
            var vocoder = new SatcomVocoder(networkSampleRate: 48000, frameDurationSeconds: 0.0225, seed: 2);
            var input = MakeToneWav(48000, 2.0, 150);

            // frameErrorRate=1.0 -> every frame lost -> concealment decays to silence and
            // eventually declares sync lost, per SatcomDecoderState.
            var output = vocoder.ProcessBuffer(input, frameErrorRate: 1.0, burstSeverity: 1.0);

            Assert.True(vocoder.DecoderState.SyncLost);
            // Tail of the output (after concealment has had time to decay/lose sync) should be
            // at/near silence, not full-amplitude tone.
            var tailStart = output.Length - 4800; // last ~100ms
            var maxTail = 0;
            for (var i = Math.Max(0, tailStart); i < output.Length; i++)
                maxTail = Math.Max(maxTail, Math.Abs((int)output[i]));
            Assert.True(maxTail < 4000, $"Expected near-silence after sustained total loss, got peak {maxTail}");
        }

        [Fact]
        public void OutputNeverClips()
        {
            var vocoder = new SatcomVocoder(networkSampleRate: 48000, frameDurationSeconds: 0.0225, seed: 3);
            var input = MakeToneWav(48000, 1.0, 150);
            // Loud input, marginal link -- the stress case for clamping.
            for (var i = 0; i < input.Length; i++)
                input[i] = (short)Math.Clamp(input[i] * 3, short.MinValue, short.MaxValue);

            var output = vocoder.ProcessBuffer(input, frameErrorRate: 0.4, burstSeverity: 0.6);

            // "Never clips" means never exceeds the 16-bit PCM representable range (what
            // ClampToInt16 actually guarantees) -- not an arbitrary internal headroom margin.
            foreach (var s in output)
                Assert.InRange(s, short.MinValue, short.MaxValue);

            // The above is true trivially (ClampToInt16 guarantees it by construction) and does
            // NOT by itself detect actual saturation/distortion -- it passed unchanged throughout
            // the whole lifetime of the gain bug below. Assert on the fraction of samples actually
            // pinned at/near full-scale instead, which is what real clipping distortion looks like.
            var pinnedCount = output.Count(s => Math.Abs((int)s) >= 32000);
            Assert.True(pinnedCount / (double)output.Length < 0.02,
                $"Expected only rare full-scale samples, got {pinnedCount}/{output.Length} pinned at/near +/-32767");
        }

        [Fact]
        public void PerfectLinkOutputRmsStaysWithinAFewTimesTheInputRms()
        {
            // Regression test for the excitation-gain bug (see SatcomLpc.ExcitationGain's own doc
            // comment): a fixed order-only gain constant ignored how much a given frame's specific
            // reflection coefficients would resonate, and produced synthesis output measured at
            // 5-19x the intended level for realistic voiced speech -- 57.8% of samples pinned at
            // full-scale on a zero-channel-error ("perfect link") test render, i.e. severe clipping
            // distortion even with nothing wrong with the link. A healthy vocoder colors/compresses
            // the signal (MELP-class output is not a transparent pass-through) but shouldn't
            // multiply its RMS level by many times over.
            const int sr = 48000;
            var vocoder = new SatcomVocoder(networkSampleRate: sr, frameDurationSeconds: 0.0225, seed: 11);
            var input = MakeToneWav(sr, 1.5, 150);

            var output = vocoder.ProcessBuffer(input, frameErrorRate: 0.0, burstSeverity: 0.0);

            double InputRms(short[] s) => Math.Sqrt(s.Select(v => (double)v * v).Average());
            var inputRms = InputRms(input);
            var outputRms = InputRms(output);

            Assert.True(outputRms < inputRms * 3.0,
                $"Expected output RMS within a few times the input RMS, got input={inputRms:F0} output={outputRms:F0} " +
                $"({outputRms / inputRms:F1}x)");
        }
    }

    public class SatcomLpcExcitationGainTests
    {
        [Fact]
        public void FlatFilterLeavesGainUnchanged()
        {
            // All reflection coefficients 0 -> an all-pass filter that neither amplifies nor
            // attenuates -- ExcitationGain should return the target RMS exactly.
            var flat = new double[11];
            var gain = SatcomLpc.ExcitationGain(targetOutputRms: 0.2, flat, order: 10);
            Assert.Equal(0.2, gain, precision: 9);
        }

        [Fact]
        public void ResonantFilterProducesSmallerGainThanFlatFilter()
        {
            // A strongly resonant filter (reflection coefficients near the |k|<1 boundary) will
            // amplify a fixed-amplitude excitation far more than a flat one -- ExcitationGain must
            // correspondingly reduce the excitation for that filter, or the resulting synthesis
            // output blows past the intended level (exactly the bug this function fixes).
            var flat = new double[11];
            var resonant = new double[11];
            for (var i = 1; i <= 10; i++) resonant[i] = 0.9;

            var flatGain = SatcomLpc.ExcitationGain(0.2, flat, 10);
            var resonantGain = SatcomLpc.ExcitationGain(0.2, resonant, 10);

            Assert.True(resonantGain < flatGain,
                $"Expected a resonant filter to reduce excitation gain below the flat-filter case, got flat={flatGain:F4} resonant={resonantGain:F4}");
        }

        [Fact]
        public void NeverReturnsNegativeOrNaN()
        {
            var rng = new Random(3);
            for (var trial = 0; trial < 200; trial++)
            {
                var reflection = new double[11];
                for (var i = 1; i <= 10; i++) reflection[i] = rng.NextDouble() * 1.998 - 0.999;
                var gain = SatcomLpc.ExcitationGain(rng.NextDouble(), reflection, 10);
                Assert.False(double.IsNaN(gain) || double.IsInfinity(gain));
                Assert.True(gain >= 0.0);
            }
        }
    }
}
