namespace SatcomAudioTool;

/// <summary>Minimal 16-bit PCM WAV reader/writer -- no external dependency, just enough to
/// round-trip mono/stereo 16-bit PCM for this offline test tool.</summary>
public static class WavFile
{
    public sealed record PcmData(int SampleRate, int Channels, short[] Samples);

    public static PcmData Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var riff = new string(reader.ReadChars(4));
        if (riff != "RIFF") throw new InvalidDataException("Not a RIFF file");
        reader.ReadInt32(); // chunk size
        var wave = new string(reader.ReadChars(4));
        if (wave != "WAVE") throw new InvalidDataException("Not a WAVE file");

        int sampleRate = 0, channels = 0, bitsPerSample = 0;
        short[]? samples = null;

        while (stream.Position < stream.Length - 8)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();
            var chunkEnd = stream.Position + chunkSize;

            if (chunkId == "fmt ")
            {
                reader.ReadInt16(); // audio format (1 = PCM)
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadInt16(); // block align
                bitsPerSample = reader.ReadInt16();
            }
            else if (chunkId == "data")
            {
                if (bitsPerSample != 16)
                    throw new NotSupportedException($"Only 16-bit PCM WAV is supported (got {bitsPerSample}-bit)");
                var sampleCount = chunkSize / 2;
                samples = new short[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                    samples[i] = reader.ReadInt16();
            }

            stream.Position = chunkEnd + (chunkEnd % 2); // chunks are word-aligned
        }

        if (samples == null) throw new InvalidDataException("No data chunk found");
        return new PcmData(sampleRate, channels == 0 ? 1 : channels, samples);
    }

    public static void Write(string path, int sampleRate, short[] monoSamples)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        const int channels = 1;
        const int bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataSize = monoSamples.Length * 2;

        writer.Write("RIFF".ToCharArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE".ToCharArray());

        writer.Write("fmt ".ToCharArray());
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        writer.Write("data".ToCharArray());
        writer.Write(dataSize);
        foreach (var s in monoSamples)
            writer.Write(s);
    }

    /// <summary>Downmix stereo (or any multi-channel) interleaved PCM to mono by averaging
    /// channels.</summary>
    public static short[] ToMono(PcmData data)
    {
        if (data.Channels <= 1) return data.Samples;

        var frameCount = data.Samples.Length / data.Channels;
        var mono = new short[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            var sum = 0;
            for (var c = 0; c < data.Channels; c++)
                sum += data.Samples[i * data.Channels + c];
            mono[i] = (short)(sum / data.Channels);
        }
        return mono;
    }
}
