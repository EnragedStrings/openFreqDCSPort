namespace OpenFreqAudio.Tests
{
    public class EncryptionDspTests
    {
        [Fact]
        public void CipherTextNoiseGenerator_SamplesStayInRange()
        {
            var gen = new CipherTextNoiseGenerator(48000);
            for (int i = 0; i < 48000; i++)
            {
                var s = gen.NextSample();
                Assert.InRange(s, -1f, 1f);
            }
        }

        [Fact]
        public void CipherTextNoiseGenerator_IsNotConstant()
        {
            // A texture generator that never changes level would just be a DC offset, not noise.
            var gen = new CipherTextNoiseGenerator(48000);
            var first = gen.NextSample();
            bool sawDifferent = false;
            for (int i = 0; i < 1000; i++)
            {
                if (gen.NextSample() != first) { sawDifferent = true; break; }
            }
            Assert.True(sawDifferent);
        }

        [Fact]
        public void CvsdColorationEffect_TracksASteadyInput()
        {
            var effect = new CvsdColorationEffect();
            float last = 0f;
            for (int i = 0; i < 2000; i++)
                last = effect.Process(0.5f);

            // After enough samples the adaptive quantizer should have converged close to the input.
            Assert.InRange(last, 0.4f, 0.6f);
        }

        [Fact]
        public void CvsdColorationEffect_OutputStaysBounded()
        {
            var effect = new CvsdColorationEffect();
            var rng = new Random(1234);
            for (int i = 0; i < 5000; i++)
            {
                var input = (float)(rng.NextDouble() * 2.0 - 1.0);
                var output = effect.Process(input);
                Assert.InRange(output, -1.5f, 1.5f);
            }
        }
    }
}
