using System;
using Xunit;

namespace OpenFreqAudio.Tests
{
    public class BinauralRendererTests
    {
        private const int Rate = 48000;

        // Deterministic white noise (a tone would make the correlation lag ambiguous), 0.2 s; returns RMS and the
        // left-minus-right arrival lag in samples, from the cross-correlation peak.
        private static (double left, double right, int lagLeftBehind) Render(float az, float el)
        {
            var r = new BinauralRenderer(Rate);
            r.SetDirection(az, el);
            const int frames = 480;
            var mono = new float[frames];
            var stereo = new float[frames * 2];
            var all = new float[frames * 2 * 20];
            var rng = new Random(1234);
            for (int block = 0; block < 20; block++)
            {
                for (int n = 0; n < frames; n++)
                    mono[n] = (float)(rng.NextDouble() * 2 - 1);
                Array.Clear(stereo);
                r.Process(mono, stereo, frames, 1f);
                Array.Copy(stereo, 0, all, block * frames * 2, stereo.Length);
            }

            int start = frames * 2 * 4; // skip the filter / delay warm-up
            int count = (all.Length - start) / 2;
            double sl = 0, sr = 0;
            for (int i = 0; i < count; i++)
            {
                sl += all[start + 2 * i] * all[start + 2 * i];
                sr += all[start + 2 * i + 1] * all[start + 2 * i + 1];
            }

            int bestLag = 0;
            double best = double.MinValue;
            for (int lag = -40; lag <= 40; lag++)
            {
                double c = 0;
                for (int i = 50; i < count - 50; i++)
                    c += all[start + 2 * (i + lag)] * all[start + 2 * i + 1];
                if (c > best) { best = c; bestLag = lag; }
            }
            return (Math.Sqrt(sl / count), Math.Sqrt(sr / count), bestLag);
        }

        [Fact]
        public void Ahead_IsBalanced()
        {
            var (l, r, lag) = Render(0, 0);
            Assert.InRange(l / r, 0.98, 1.02);
            Assert.Equal(0, lag);
        }

        [Fact]
        public void Right_IsLouderAndEarlierInRightEar()
        {
            var (l, r, lag) = Render(90, 0);
            Assert.True(r > l * 1.5, $"right {r} left {l}");
            Assert.InRange(lag, 25, 35); // ~0.66 ms around the head at 48 kHz: left ear hears it later
        }

        [Fact]
        public void Left_MirrorsRight()
        {
            var (l, r, lag) = Render(-90, 0);
            Assert.True(l > r * 1.5, $"left {l} right {r}");
            Assert.InRange(lag, -35, -25);
        }

        // High-frequency share of the left ear: RMS of the first difference over RMS (rises with brightness).
        private static double Brightness(float az, float el)
        {
            var r = new BinauralRenderer(Rate);
            r.SetDirection(az, el);
            const int frames = 480;
            var mono = new float[frames];
            var stereo = new float[frames * 2];
            var rng = new Random(99);
            double e = 0, d = 0;
            float prev = 0;
            for (int block = 0; block < 20; block++)
            {
                for (int n = 0; n < frames; n++)
                    mono[n] = (float)(rng.NextDouble() * 2 - 1);
                Array.Clear(stereo);
                r.Process(mono, stereo, frames, 1f);
                for (int n = 0; n < frames; n++)
                {
                    float x = stereo[2 * n];
                    if (block >= 4) { e += x * x; d += (x - prev) * (x - prev); }
                    prev = x;
                }
            }
            return Math.Sqrt(d / e);
        }

        [Fact]
        public void Elevation_SeparatesRadiosAhead()
        {
            // 3DAS AMDF CAFG402B: RT1 (-15 deg) and LARS (+45 deg) are both ahead; only elevation separates them.
            double lars = Brightness(0, 45), level = Brightness(0, 0), rt1 = Brightness(0, -15);
            Assert.True(lars > level * 1.05, $"+45 {lars} 0 {level}");
            Assert.True(rt1 < level * 0.99, $"-15 {rt1} 0 {level}");
            var (l, r, lag) = Render(0, 45);
            Assert.InRange(l / r, 0.98, 1.02);  // still centred
            Assert.Equal(0, lag);
        }

        [Fact]
        public void SmallAzimuth_GivesSmallLead()
        {
            var (l, r, lag) = Render(-15, 0);
            Assert.True(l > r, $"left {l} right {r}");
            Assert.InRange(lag, -12, -2);
        }
    }
}
