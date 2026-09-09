namespace OpenFreq.BotClient;

/// <summary>Minimal 16-bit PCM WAV reader -- no external dependency, adapted from
/// tools/SatcomAudioTool/WavFile.cs. Also resamples to an arbitrary target rate (linear
/// interpolation) since a bot operator's response clip won't always already be at
/// OpenFreqRtcClient.SAMPLE_RATE.</summary>
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

    /// <summary>Simple linear-interpolation resample -- good enough for a bot's spoken response
    /// clip, not intended for anything quality-sensitive (see SatcomDownsampler elsewhere in the
    /// codebase for the precise integer-ratio resampler used ahead of Whisper).</summary>
    public static short[] Resample(short[] monoSamples, int fromRate, int toRate)
    {
        if (fromRate == toRate || monoSamples.Length == 0) return monoSamples;

        var outLength = (int)((long)monoSamples.Length * toRate / fromRate);
        var output = new short[outLength];
        for (var i = 0; i < outLength; i++)
        {
            var srcPos = (double)i * fromRate / toRate;
            var srcIndex = (int)srcPos;
            var frac = srcPos - srcIndex;
            var a = monoSamples[Math.Min(srcIndex, monoSamples.Length - 1)];
            var b = monoSamples[Math.Min(srcIndex + 1, monoSamples.Length - 1)];
            output[i] = (short)(a + (b - a) * frac);
        }
        return output;
    }
}
