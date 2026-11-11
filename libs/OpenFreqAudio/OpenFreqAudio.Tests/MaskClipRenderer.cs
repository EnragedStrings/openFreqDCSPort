using ManagedBass;

namespace OpenFreqAudio.Tests;

/// <summary>
/// Renders mask listening clips from a voice recording so the mask can be judged by ear. Does nothing unless
/// OPENFREQ_RENDER_MASK is set to an output folder and OPENFREQ_MASK_INPUT to an audio file BASS can decode (WAV,
/// MP3, ...). The input is mixed to mono, resampled to 48 kHz and levelled to -20 dBFS RMS (like the client's mic
/// normalizer), then rendered through the app's own A-10C hot-mic chain with each mask profile.
/// </summary>
public class MaskClipRenderer
{
    private const int Fs = RadioPlayback.SampleRate;

    [Fact]
    public void RenderMaskClips()
    {
        var dir = Environment.GetEnvironmentVariable("OPENFREQ_RENDER_MASK");
        var input = Environment.GetEnvironmentVariable("OPENFREQ_MASK_INPUT");
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(input)) return;
        Directory.CreateDirectory(dir);

        var voice = Level(Decode(input), -20f);
        // 1.5 s of silence after the voice, so the idle hot mic can be heard.
        voice = [.. voice, .. new float[Fs * 3 / 2]];

        float[] MicChain(MaskProfile profile)
        {
            var b = (float[])voice.Clone();
            AmbientNoiseEffectFactory.Create(AmbientNoiseType.AirA10, Fs, mask: profile).ApplyMicOnly(b, 0, b.Length, 1f);
            return b;
        }

        Write(dir, "00_dry_reference.wav", voice);
        Write(dir, "01_A10_profile2_classic.wav", MicChain(MaskProfile.Classic));
        Write(dir, "02_A10_profile1_measured.wav", MicChain(MaskProfile.Measured));
    }

    /// <summary>Decodes OPENFREQ_DECODE_INPUT to 48 kHz mono 16-bit WAV at OPENFREQ_DECODE_OUTPUT (for analysis).</summary>
    [Fact]
    public void DecodeToWav()
    {
        var input = Environment.GetEnvironmentVariable("OPENFREQ_DECODE_INPUT");
        var output = Environment.GetEnvironmentVariable("OPENFREQ_DECODE_OUTPUT");
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) return;
        Write(Path.GetDirectoryName(output)!, Path.GetFileName(output), Decode(input));
    }

    /// <summary>Writes 20 s of the A-10C cockpit ambience bed (ApplyPreFade on silence) to OPENFREQ_BED_OUTPUT.</summary>
    [Fact]
    public void RenderA10Bed()
    {
        var output = Environment.GetEnvironmentVariable("OPENFREQ_BED_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        var bed = new float[Fs * 20];
        AmbientNoiseEffectFactory.Create(AmbientNoiseType.AirA10, Fs).ApplyPreFade(bed, 0, bed.Length, 1f);
        Write(Path.GetDirectoryName(output)!, Path.GetFileName(output), bed);
    }

    /// <summary>Renders OPENFREQ_APP_MASK_INPUT (48 kHz mono WAV) through the app's A-10C mic chain with mask profile 1
    /// (ApplyMicOnly, the HUD-tape hot mic path) to OPENFREQ_APP_MASK_OUTPUT, to check it against the chosen clip.</summary>
    [Fact]
    public void RenderAppProfile1()
    {
        var input = Environment.GetEnvironmentVariable("OPENFREQ_APP_MASK_INPUT");
        var output = Environment.GetEnvironmentVariable("OPENFREQ_APP_MASK_OUTPUT");
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) return;
        var x = Decode(input);
        AmbientNoiseEffectFactory.Create(AmbientNoiseType.AirA10, Fs, mask: MaskProfile.Measured).ApplyMicOnly(x, 0, x.Length, 1f);
        Write(Path.GetDirectoryName(output)!, Path.GetFileName(output), x);
    }

    [Fact]
    public void DumpMeasuredMaskImpulse()
    {
        var output = Environment.GetEnvironmentVariable("OPENFREQ_IMPULSE_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        var f = new MeasuredMaskFilter(Fs);
        var y = new float[4096];
        for (int i = 0; i < y.Length; i++) y[i] = f.Process(i == 0 ? 1f : 0f);
        File.WriteAllLines(output, y.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static float[] Decode(string path)
    {
        Bass.Init(0, Fs); // the "no sound" device: decoding only
        int h = Bass.CreateStream(path, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Mono);
        Assert.True(h != 0, $"BASS could not decode {path}: {Bass.LastError}");
        var info = Bass.ChannelGetInfo(h);
        var samples = new List<float>();
        var chunk = new float[16384];
        while (true)
        {
            int got = Bass.ChannelGetData(h, chunk, chunk.Length * 4 | (int)DataFlags.Float);
            if (got <= 0) break;
            samples.AddRange(chunk.AsSpan(0, got / 4).ToArray());
        }
        Bass.StreamFree(h);
        return Resample(samples.ToArray(), info.Frequency, Fs);
    }

    private static float[] Resample(float[] x, int from, int to)
    {
        if (from == to) return x;
        // Windowed-sinc, cut at the lower Nyquist.
        double ratio = (double)to / from, cutoff = Math.Min(1.0, ratio);
        int half = 32, n = (int)(x.Length * ratio);
        var y = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / ratio;
            int c = (int)t;
            double sum = 0;
            for (int k = c - half + 1; k <= c + half; k++)
            {
                if (k < 0 || k >= x.Length) continue;
                double d = t - k, arg = Math.PI * d * cutoff;
                double sinc = d == 0 ? 1 : Math.Sin(arg) / arg;
                double win = 0.5 + 0.5 * Math.Cos(Math.PI * d / half);
                sum += x[k] * cutoff * sinc * win;
            }
            y[i] = (float)sum;
        }
        return y;
    }

    private static double Rms(float[] s) => Math.Sqrt(s.Sum(v => (double)v * v) / Math.Max(1, s.Length));

    private static float[] Level(float[] s, float dbfs)
    {
        double g = Math.Pow(10, dbfs / 20) / Math.Max(1e-9, Rms(s));
        return s.Select(v => (float)(v * g)).ToArray();
    }

    private static float[] Match(float[] s, double targetRms)
    {
        double g = targetRms / Math.Max(1e-9, Rms(s));
        return s.Select(v => MathF.Tanh((float)(v * g))).ToArray();
    }

    private static void Write(string dir, string name, float[] s)
    {
        using var f = File.Create(Path.Combine(dir, name));
        using var w = new BinaryWriter(f);
        w.Write("RIFF"u8); w.Write(36 + s.Length * 2); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(s.Length * 2);
        foreach (var v in s) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
    }
}
