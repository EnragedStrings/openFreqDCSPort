using System;

namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Tests for the integrated carrier phase the AM mixer uses (upstream 49f2ede).
    /// </summary>
    public class CarrierPhaseTests
    {
        private const double TwoPi = 2 * Math.PI;

        [Fact]
        public void PhaseStep_IsRelativeToTheChannel()
        {
            // On channel with no tune offset, the carrier doesn't rotate.
            Assert.Equal(0, RadioPlayback.PhaseStep(251000, 251000, 0));

            // 1 kHz off channel turns 2π · 1000 / 48000 per sample.
            Assert.Equal(TwoPi * 1000 / RadioPlayback.SampleRate,
                RadioPlayback.PhaseStep(251001, 251000, 0), 12);
        }

        [Fact]
        public void PhaseStep_ResolvesSubHertzOffsetsAtUhf()
        {
            // In float32 a 250 MHz carrier only resolved to ~16 Hz, so a few-Hz beat vanished
            // or snapped to a multiple of that. 0.004 ppm at 250 MHz is 1 Hz.
            double oneHz = RadioPlayback.PhaseStep(250000, 250000, 0.004);
            Assert.Equal(TwoPi * 1.0 / RadioPlayback.SampleRate, oneHz, 12);

            double twoHz = RadioPlayback.PhaseStep(250000, 250000, 0.008);
            Assert.Equal(2 * oneHz, twoHz, 12);
        }

        [Fact]
        public void AdvancePhase_MatchesPerSampleIntegration()
        {
            double step = RadioPlayback.PhaseStep(127000, 127000, 31.7);
            double theta = 1.0;
            for (int n = 0; n < 480; ++n)
                theta = RadioPlayback.WrapPhase(theta + step);

            Assert.Equal(theta, RadioPlayback.AdvancePhase(1.0, step, 480), 9);
        }

        [Theory]
        [InlineData(1.0, -0.5, 480)]
        [InlineData(6.0, 0.4, 1_000_000)]
        public void AdvancePhase_StaysWrapped(double theta, double step, int samples)
        {
            double next = RadioPlayback.AdvancePhase(theta, step, samples);
            Assert.InRange(next, 0, TwoPi);
            Assert.True(next < TwoPi);
        }

        [Fact]
        public void AdvancePhase_RecoversFromNonFinite()
        {
            Assert.Equal(0, RadioPlayback.AdvancePhase(double.NaN, 0.1, 480));
            Assert.Equal(0, RadioPlayback.AdvancePhase(1.0, double.PositiveInfinity, 480));
        }

        [Fact]
        public void ChangingFrequency_DoesNotJumpThePhase()
        {
            // The old θ = 2π·f·n/Fs teleported the phase whenever f changed (a Doppler update);
            // integrated, the phase just carries on from where it was.
            double slow = RadioPlayback.PhaseStep(127000, 127000, 1.0);
            double fast = RadioPlayback.PhaseStep(127000, 127000, 1.5);
            double theta = RadioPlayback.AdvancePhase(0, slow, 4800);
            double afterOneSample = RadioPlayback.AdvancePhase(theta, fast, 1);
            double delta = Math.Abs(afterOneSample - theta);
            delta = Math.Min(delta, TwoPi - delta);
            Assert.Equal(fast, delta, 9);
        }
    }
}
