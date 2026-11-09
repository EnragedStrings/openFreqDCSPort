using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Xunit;

namespace OpenFreqAudio.Tests
{
    // The HUD Recorder feed (HudTapeFeed): its numbers must match HUD Recorder's native/src/audio_feed.h exactly, and a
    // reader following that file's protocol must get back what was written, stamps included.
    public class HudTapeFeedTests
    {
        private static string TestName() => $@"Local\HUD_GUNCAM_AUDIO_test_{Guid.NewGuid():N}";

        [Fact]
        public void Layout_MatchesHudRecorder()
        {
            Assert.Equal(@"Local\HUD_GUNCAM_AUDIO_v1", HudTapeFeed.DefaultName);
            Assert.Equal(0x46414748u, HudTapeFeed.Magic);
            Assert.Equal(1u, HudTapeFeed.Version);
            Assert.Equal(48000, HudTapeFeed.Rate);
            Assert.Equal(4 * 48000, HudTapeFeed.Frames);
            Assert.Equal(1024, HudTapeFeed.Blocks);
            Assert.Equal(32, HudTapeFeed.RingHeaderOffset(0));
            Assert.Equal(96, HudTapeFeed.BlocksOffset(0)); // header + 2 ring headers
            Assert.Equal(96 + 1024 * 24, HudTapeFeed.SamplesOffset(0));
            Assert.Equal(96 + 1024 * 24 + 4 * 48000 * 2 * 4, HudTapeFeed.BlocksOffset(1));
            Assert.Equal(2353248, HudTapeFeed.Size); // audio_feed.h: static_assert(kSize == 2353248)
        }

        [Fact]
        public void Header_IsWrittenOnOpen()
        {
            if (!OperatingSystem.IsWindows()) return;
            string name = TestName();
            using var feed = HudTapeFeed.Open(out var reason, name);
            Assert.True(feed != null, reason);
            using var map = MemoryMappedFile.OpenExisting(name);
            using var v = map.CreateViewAccessor(0, HudTapeFeed.Size, MemoryMappedFileAccess.Read);
            Assert.Equal(HudTapeFeed.Magic, v.ReadUInt32(0));
            Assert.Equal(HudTapeFeed.Version, v.ReadUInt32(4));
            Assert.Equal((uint)HudTapeFeed.Rate, v.ReadUInt32(8));
            Assert.Equal(Stopwatch.Frequency, v.ReadInt64(16)); // QueryPerformanceFrequency on Windows
            Assert.Equal((uint)Environment.ProcessId, v.ReadUInt32(24));
            Assert.Equal(0L, v.ReadInt64(HudTapeFeed.RingHeaderOffset(0))); // published
            Assert.Equal(0L, v.ReadInt64(HudTapeFeed.RingHeaderOffset(0) + 8)); // writeFrame
        }

        [Fact]
        public void Blocks_ReadBackAsWritten_AcrossTheRingWrap()
        {
            if (!OperatingSystem.IsWindows()) return;
            string name = TestName();
            using var feed = HudTapeFeed.Open(out var reason, name);
            Assert.True(feed != null, reason);
            using var map = MemoryMappedFile.OpenExisting(name);
            using var v = map.CreateViewAccessor(0, HudTapeFeed.Size, MemoryMappedFileAccess.Read);

            // variable block sizes (as BASS DSP callbacks), at least 4.5 s and 1030 blocks: both the sample ring (4 s) and
            // the block ring (1024) wrap
            int[] sizes = [240, 480, 441, 1000, 960];
            long frame = 0, qpc = 1_000_000_000;
            int n = 0;
            var buf = new float[2000];
            while (frame < 4 * 48000 + 24000 || n < HudTapeFeed.Blocks + 6)
            {
                int f = sizes[n % sizes.Length];
                for (int i = 0; i < f; i++)
                {
                    buf[2 * i] = Sample(frame + i, 0);
                    buf[2 * i + 1] = Sample(frame + i, 1);
                }

                feed!.WriteHeard(buf.AsSpan(0, 2 * f), qpc + n);
                // reader side, as audio.cpp: the block just published, then its samples
                long rh = HudTapeFeed.RingHeaderOffset(0);
                long published = v.ReadInt64(rh), writeFrame = v.ReadInt64(rh + 8);
                Assert.Equal(n + 1, published);
                Assert.Equal(frame + f, writeFrame);
                long b = HudTapeFeed.BlocksOffset(0) + (n % HudTapeFeed.Blocks) * 24L;
                Assert.Equal(qpc + n, v.ReadInt64(b));
                Assert.Equal(frame, v.ReadInt64(b + 8));
                Assert.Equal(f, v.ReadInt32(b + 16));
                for (int i = 0; i < f; i += 97)
                {
                    long at = (frame + i) % HudTapeFeed.Frames;
                    long s = HudTapeFeed.SamplesOffset(0) + at * 2 * 4;
                    Assert.Equal(Sample(frame + i, 0), v.ReadSingle(s));
                    Assert.Equal(Sample(frame + i, 1), v.ReadSingle(s + 4));
                }

                frame += f;
                n++;
            }

            Assert.True(n > HudTapeFeed.Blocks, "the block ring should have wrapped too");
            // the mono Voice ring is never written by OpenFreq
            Assert.Equal(0L, v.ReadInt64(HudTapeFeed.RingHeaderOffset(1)));
        }

        [Fact]
        public void AnotherLiveWriter_IsRefused_AndAStaleOneTakenOver()
        {
            if (!OperatingSystem.IsWindows()) return;
            string name = TestName();
            using var map = MemoryMappedFile.CreateNew(name, HudTapeFeed.Size);
            using var v = map.CreateViewAccessor(0, HudTapeFeed.Size);
            using var other = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
                { CreateNoWindow = true, UseShellExecute = false })!;
            try
            {
                v.Write(0, HudTapeFeed.Magic);
                v.Write(24, (uint)other.Id);
                Assert.Null(HudTapeFeed.Open(out var reason, name));
                Assert.Contains($"pid {other.Id}", reason);
            }
            finally
            {
                other.Kill();
                other.WaitForExit();
            }

            using var feed = HudTapeFeed.Open(out var reason2, name);
            Assert.True(feed != null, reason2);
            Assert.Equal((uint)Environment.ProcessId, v.ReadUInt32(24));
            feed!.Dispose();
            Assert.Equal(0u, v.ReadUInt32(24)); // released: another program may take it over
        }

        private static float Sample(long frame, int ch) => (float)Math.Sin(frame * 0.01 + ch) * 0.5f;
    }
}
