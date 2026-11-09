using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenFreqAudio;

/// <summary>
/// Feeds the capture mix to HUD Recorder (a local DCS gun-camera mod) so its HUD tapes carry the radio audio in sync.
/// Local shared memory only, no network. The layout and the write protocol are HUD Recorder's
/// <c>native/src/audio_feed.h</c> (the same feed the 455th SRS fork writes); any change bumps the version and the name
/// on both sides, and <c>HudTapeFeedTests</c> pins the numbers.
///
/// Layout: Header, RingHeader[2], then per ring Block[1024] and float[4 s * channels] (interleaved). Ring 0 "Heard" is
/// stereo, ring 1 "Voice" mono. OpenFreq writes only Heard: its capture mix already holds its own voice rendered
/// through the radio FX. Per block the single writer copies the samples to writeFrame % kFrames (wrapping), fills
/// Block[published % kBlocks], then stores writeFrame += frames and, last, published += 1. It never waits and never
/// allocates. A block's stamp is the QueryPerformanceCounter value (Stopwatch ticks on Windows) when its first sample
/// is heard.
/// </summary>
public sealed unsafe class HudTapeFeed : IDisposable
{
    public const string DefaultName = @"Local\HUD_GUNCAM_AUDIO_v1";
    public const uint Magic = 0x46414748; // "HGAF"
    public const uint Version = 1;
    public const int Rate = 48000;
    public const int Frames = 4 * Rate; // per ring
    public const int Blocks = 1024; // per ring
    public const int HeaderSize = 32, RingHeaderSize = 32, BlockSize = 24;
    public static readonly int[] Channels = [2, 1]; // Heard, Voice

    public static long RingHeaderOffset(int ring) => HeaderSize + ring * RingHeaderSize;

    public static long BlocksOffset(int ring) => ring == 0
        ? RingHeaderOffset(2)
        : BlocksOffset(ring - 1) + Blocks * BlockSize + (long)Frames * Channels[ring - 1] * 4;

    public static long SamplesOffset(int ring) => BlocksOffset(ring) + Blocks * BlockSize;
    public static long Size => SamplesOffset(1) + (long)Frames * Channels[1] * 4; // 2,353,248

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;

    private HudTapeFeed(MemoryMappedFile map, MemoryMappedViewAccessor view)
    {
        _map = map;
        _view = view;
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
    }

    /// <summary>
    /// Create the feed (or take over a stale one). Returns null with the reason when it can't: not Windows, or another
    /// live process (e.g. the 455th SRS fork) already writes it, which would corrupt both.
    /// </summary>
    public static HudTapeFeed? Open(out string reason, string name = DefaultName)
    {
        reason = "";
        if (!OperatingSystem.IsWindows())
        {
            reason = "the HUD Recorder feed is Windows only";
            return null;
        }

        MemoryMappedFile map;
        try
        {
            map = MemoryMappedFile.CreateOrOpen(name, Size, MemoryMappedFileAccess.ReadWrite);
        }
        catch (Exception ex)
        {
            reason = $"can't create {name}: {ex.Message}";
            return null;
        }

        // Everything after CreateOrOpen depends on another program's state (an existing map may be smaller: an older or
        // other-version writer made it), so no exception may leave here: startup and the settings toggle call this.
        MemoryMappedViewAccessor? view = null;
        try
        {
            try
            {
                view = map.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                reason = $"the existing {name} has a different size (another feed version?): {ex.Message}";
                map.Dispose();
                return null;
            }

            uint magic = view.ReadUInt32(0), version = view.ReadUInt32(4);
            uint owner = view.ReadUInt32(24); // the header's pid
            int self = Environment.ProcessId;
            if (magic == Magic && owner != 0 && owner != self && IsAlive((int)owner))
            {
                reason = version == Version
                    ? $"another program (pid {owner}, {ProcessName((int)owner)}) already feeds HUD Recorder"
                    : $"another program (pid {owner}, {ProcessName((int)owner)}) writes HUD Recorder feed version {version}, this OpenFreq writes {Version}";
                view.Dispose();
                map.Dispose();
                return null;
            }

            var feed = new HudTapeFeed(map, view);
            feed.Reset();
            return feed;
        }
        catch (Exception ex)
        {
            reason = $"can't use {name}: {ex.Message}";
            view?.Dispose();
            map.Dispose();
            return null;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return "?";
        }
    }

    // A fresh feed: rings empty (a reader that sees published go backwards starts over), header last.
    private void Reset()
    {
        new Span<byte>(_base, (int)Size).Clear();
        *(uint*)(_base + 0) = Magic;
        *(uint*)(_base + 4) = Version;
        *(uint*)(_base + 8) = Rate;
        // Header: magic 0, version 4, rate 8, reserved 12, qpcFrequency (int64) 16, pid 24, reserved2 28
        *(long*)(_base + 16) = Stopwatch.Frequency;
        *(uint*)(_base + 24) = (uint)Environment.ProcessId;
        Thread.MemoryBarrier();
    }

    /// <summary>
    /// Append one block of interleaved stereo float samples to the Heard ring, stamped with when its first sample is
    /// heard (<paramref name="heardQpc"/>, Stopwatch ticks). Single writer (the DSP thread); copies synchronously.
    /// </summary>
    public void WriteHeard(ReadOnlySpan<float> stereo, long heardQpc)
    {
        int frames = stereo.Length / 2;
        if (frames <= 0) return;
        if (frames > Rate) // a block is at most one second; never the case for a DSP callback
        {
            WriteHeard(stereo[..(Rate * 2)], heardQpc);
            WriteHeard(stereo[(Rate * 2)..], heardQpc + Stopwatch.Frequency);
            return;
        }

        long* rh = (long*)(_base + RingHeaderOffset(0));
        long published = rh[0], writeFrame = rh[1];
        float* ring = (float*)(_base + SamplesOffset(0));
        int at = (int)(writeFrame % Frames), first = Math.Min(frames, Frames - at);
        stereo[..(first * 2)].CopyTo(new Span<float>(ring + at * 2, first * 2));
        if (first < frames) stereo[(first * 2)..].CopyTo(new Span<float>(ring, (frames - first) * 2));

        byte* b = _base + BlocksOffset(0) + (published % Blocks) * BlockSize;
        *(long*)b = heardQpc;
        *(long*)(b + 8) = writeFrame;
        *(int*)(b + 16) = frames;
        *(int*)(b + 20) = 0;
        Volatile.Write(ref rh[1], writeFrame + frames);
        Volatile.Write(ref rh[0], published + 1);
    }

    /// <summary>
    /// Give up ownership (header pid 0, so another program may take the feed over) but stay mapped: for when a writer
    /// may still be running (RadioPlayback.Dispose with StopAll timed out). The OS unmaps it at process exit.
    /// </summary>
    public void Abandon()
    {
        if (!_disposed) *(uint*)(_base + 24) = 0;
    }

    private bool _disposed;

    /// <summary>Releases the shared memory. Only once nothing writes any more (RadioPlayback: after StopAll).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        *(uint*)(_base + 24) = 0; // no longer written: another program may take it over
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
    }
}
