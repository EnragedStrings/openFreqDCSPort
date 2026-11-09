namespace OpenFreqAudio.Tests
{
    public class PeakLimiterTests
    {
        private const int Fs = RadioPlayback.SampleRate;
        private const float Ceiling = 0.891f; // -1 dBFS
        // Allow only for float rounding.
        private const float MaxOutput = Ceiling * 1.0001f;

        private static float Sine(int n, float amplitude) => amplitude * MathF.Sin(2 * MathF.PI * 1000 * n / Fs);

        [Fact]
        public void QuietInput_PassesUnchanged_AfterLatency()
        {
            var limiter = new PeakLimiter(Fs);
            int latency = limiter.LatencySamples;
            Assert.Equal(Fs / 500, latency); // 2 ms

            var input = Enumerable.Range(0, Fs / 10).Select(n => Sine(n, 0.5f)).ToArray();
            var output = input.Select(limiter.Process).ToArray();

            for (int n = 0; n < latency; n++) Assert.Equal(0f, output[n]);
            for (int n = latency; n < output.Length; n++) Assert.Equal(input[n - latency], output[n]);
            Assert.Equal(0, limiter.LimitedSamples);
        }

        [Fact]
        public void SuddenLoudInput_StaysBelowCeiling()
        {
            var limiter = new PeakLimiter(Fs);
            float maxOut = 0;

            // Silence, then a sine 6 dB over the ceiling with no warning.
            for (int n = 0; n < Fs / 10; n++) limiter.Process(0);
            for (int n = 0; n < Fs / 10; n++)
                maxOut = MathF.Max(maxOut, MathF.Abs(limiter.Process(Sine(n, 2 * Ceiling))));

            Assert.InRange(maxOut, 0.95f * Ceiling, MaxOutput);
            Assert.True(limiter.LimitedSamples > 0);
            Assert.InRange(limiter.MaxReductionDb, 5.5f, 6.5f);
        }

        [Fact]
        public void Reset_EmptiesTheDelayLine()
        {
            var limiter = new PeakLimiter(Fs);
            for (int n = 0; n < Fs / 10; n++) limiter.Process(Sine(n, 2 * Ceiling));

            limiter.Reset();

            Assert.Equal(0, limiter.LimitedSamples);
            Assert.Equal(0f, limiter.MaxReductionDb);
            for (int n = 0; n < limiter.LatencySamples * 2; n++) Assert.Equal(0f, limiter.Process(0));
        }

        [Fact]
        public void Stereo_LoudestChannelSetsOneGainForBoth()
        {
            var limiter = new PeakLimiter(Fs, channels: 2);
            int latency = limiter.LatencySamples;
            Assert.Equal(Fs / 500, latency); // still 2 ms, counted in frames

            // Left 6 dB over the ceiling, right well under it.
            int frames = Fs / 10;
            var input = new float[frames * 2];
            for (int n = 0; n < frames; n++)
            {
                input[2 * n] = Sine(n, 2 * Ceiling);
                input[2 * n + 1] = Sine(n, 0.25f);
            }

            var output = (float[])input.Clone();
            limiter.Process(output);

            for (int n = latency; n < frames; n++)
            {
                float inLeft = input[2 * (n - latency)];
                float inRight = input[2 * (n - latency) + 1];
                float outLeft = output[2 * n];
                float outRight = output[2 * n + 1];

                Assert.True(MathF.Abs(outLeft) <= MaxOutput);
                // The right channel gets the left's gain, so the balance between them stays.
                if (MathF.Abs(inLeft) > 0.1f)
                    Assert.Equal(inRight * (outLeft / inLeft), outRight, 1e-5);
            }

            Assert.True(limiter.LimitedSamples > 0);
        }

        [Fact]
        public void Stereo_RejectsPartialFrames()
        {
            var limiter = new PeakLimiter(Fs, channels: 2);
            Assert.Throws<ArgumentException>(() => limiter.Process(new float[3]));
        }
    }
}
