namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Tests for <see cref="CarrierSmoother"/>, which keeps a transmitter's carrier level
    /// and frequency continuous when physics hands <see cref="RadioPlayback"/> a new result in a step.
    /// </summary>
    public class CarrierSmootherTests
    {
        // One 10 ms DSP callback.
        private const double Dt = 0.01;

        private static AudioParams Params(float snrDb, float tuneOffsetPpm = 1f) => new()
        {
            ReceivedSnrDb = snrDb,
            RadioFrequencyKHz = 141450,
            TuneOffsetPPM = tuneOffsetPpm,
        };

        [Fact]
        public void StartsAtTheInitialParams()
        {
            var smoother = new CarrierSmoother(Params(20f));

            Assert.Equal(20f, smoother.ReceivedSnrDb);
            Assert.Equal(1f, smoother.TuneOffsetPPM);

            // 20 dB over unit-power noise is an amplitude of 10.
            Assert.Equal(10.0, smoother.Carrier.Amplitude, 1e-9);
            Assert.Equal(1.0, smoother.Carrier.TuneOffsetPpm);
        }

        [Fact]
        public void SpreadsAStepOutWithoutOvershoot()
        {
            // What close formation did: from one physics result to the next,
            // the range jumped from about 10 m to about 30 m.
            var smoother = new CarrierSmoother(Params(100f));
            var target = Params(91f);

            float last = smoother.ReceivedSnrDb;
            float largestChange = 0f;
            for (int i = 0; i < 100; ++i) // 1 s
            {
                smoother.Step(target, Dt);
                float now = smoother.ReceivedSnrDb;
                Assert.InRange(now, 91f, last);
                largestChange = Math.Max(largestChange, last - now);
                last = now;
            }

            // The steepest slope is 9 dB / (e · 80 ms) = 41 dB/s, so about 0.4 dB per callback.
            // Before the smoother, the whole 9 dB landed in one callback.
            Assert.True(largestChange < 0.5f, $"Largest change in one callback was {largestChange} dB");
            Assert.Equal(91f, smoother.ReceivedSnrDb, 0.01f);
        }

        [Fact]
        public void SmoothsTheTuneOffset()
        {
            var smoother = new CarrierSmoother(Params(100f));
            // A 1 ppm change in Doppler is a 300 m/s change in closing speed.
            var target = Params(100f, tuneOffsetPpm: 2f);

            smoother.Step(target, Dt);

            // It has started to move, but has not jumped to the target.
            Assert.InRange(smoother.TuneOffsetPPM, 1.001f, 1.5f);
            Assert.Equal(smoother.TuneOffsetPPM, smoother.Carrier.TuneOffsetPpm);

            for (int i = 0; i < 100; ++i) smoother.Step(target, Dt);

            Assert.Equal(2f, smoother.TuneOffsetPPM, 0.001f);
        }

        [Fact]
        public void ResetGoesStraightToTheTarget()
        {
            // A new transmission keys the carrier up from nothing,
            // so it should start at the latest result instead of ramping from the last transmission.
            var smoother = new CarrierSmoother(Params(20f));
            smoother.Reset(Params(100f, tuneOffsetPpm: 2f));
            Assert.Equal(100f, smoother.ReceivedSnrDb);
            Assert.Equal(2f, smoother.TuneOffsetPPM);
        }

        [Fact]
        public void HoldsTheLastValueThroughANonFiniteTarget()
        {
            var smoother = new CarrierSmoother(Params(100f));
            smoother.Step(Params(float.NaN), Dt);
            Assert.Equal(100f, smoother.ReceivedSnrDb);

            smoother.Step(Params(float.PositiveInfinity), Dt);
            Assert.Equal(100f, smoother.ReceivedSnrDb);

            smoother.Step(Params(90f), Dt);
            Assert.InRange(smoother.ReceivedSnrDb, 90f, 100f - 0.01f);
        }

        [Fact]
        public void RecoversFromANonFiniteReset()
        {
            // In 1.1.0 a single NaN SNR latched a radio silent until reconnect.
            var smoother = new CarrierSmoother(Params(float.NaN));
            Assert.True(float.IsNaN(smoother.ReceivedSnrDb));

            smoother.Step(Params(90f), Dt);
            Assert.Equal(90f, smoother.ReceivedSnrDb);
        }
    }
}
