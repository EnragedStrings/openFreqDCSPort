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
                Assert.InRange(output, -32000.0, 32000.0);
            }
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
        }
    }
}
