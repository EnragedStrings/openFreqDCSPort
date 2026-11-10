using OpenFreqAudio.Jamming;

namespace OpenFreqAudio.Tests;

/// <summary>
/// Renders listening samples of every jamming technique through the real receiver (SlotReceiver), so the sound can
/// be judged by ear. Does nothing unless OPENFREQ_RENDER_JAMMING is set to an output folder that contains a
/// voice.wav (16-bit mono, any length; 48 kHz). Each file: 1.5 s of the jammer alone (nobody talking), the voice
/// transmission with the jammer, then 1.5 s of the jammer alone again.
/// </summary>
public class JammingSampleRenderer
{
    private const int Fs = RadioPlayback.SampleRate;
    private const int UhfAmKhz = 251_000;
    private const int VhfFmKhz = 40_000;
    private const double SignalSnrDb = 25; // a clear, strong talker

    [Fact]
    public void RenderListeningSamples()
    {
        var dir = Environment.GetEnvironmentVariable("OPENFREQ_RENDER_JAMMING");
        if (string.IsNullOrEmpty(dir)) return;
        var voice = ReadMonoWav(Path.Combine(dir, "voice.wav"));

        static JammerContribution J(float jsDb, JammerTechnique t, float offsetHz = 0f, JammerWaveformParams? p = null,
            float hop = 1f, bool responsive = false) =>
            new("jam", (float)SignalSnrDb + jsDb, t, offsetHz, p ?? new JammerWaveformParams { Seed = 7 },
                responsive, responsive ? 150f : 0f, hop);

        var lookThrough = new JammerWaveformParams { Seed = 7, LookThroughPeriodMs = 1000, LookThroughDuty = 0.15f };
        var tones = new JammerWaveformParams { Seed = 7, ToneOffsetsHz = [-1200f, 400f, 1700f] };

        var scenarios = new (string Name, int Khz, JammerContribution? Jammer)[]
        {
            ("00_am_clean", UhfAmKhz, null),
            ("01_am_noise_js-10_interference", UhfAmKhz, J(-10, JammerTechnique.Noise)),
            ("02_am_noise_js0_kill", UhfAmKhz, J(0, JammerTechnique.Noise)),
            ("03_am_noise_js+10_denied", UhfAmKhz, J(10, JammerTechnique.Noise)),
            ("04_am_tone_1khz_js-10_whistle", UhfAmKhz, J(-10, JammerTechnique.Tone, 1000f)),
            ("05_am_tone_1khz_js+10_monkey_chatter", UhfAmKhz, J(10, JammerTechnique.Tone, 1000f)),
            ("06_am_multitone_js0", UhfAmKhz, J(0, JammerTechnique.MultiTone, 0f, tones)),
            ("07_am_stepped_tones_bagpipes_js0", UhfAmKhz, J(0, JammerTechnique.SteppedTones)),
            ("08_am_swept_wobbler_js0", UhfAmKhz, J(0, JammerTechnique.Swept)),
            ("09_am_gulls_js0", UhfAmKhz, J(0, JammerTechnique.Gulls)),
            ("10_am_random_pulse_js0", UhfAmKhz, J(0, JammerTechnique.RandomPulse)),
            ("11_am_spark_js0", UhfAmKhz, J(0, JammerTechnique.Spark)),
            ("12_am_recorded_babble_js0", UhfAmKhz, J(0, JammerTechnique.Recorded)),
            ("13_am_noise_lookthrough_js+10", UhfAmKhz, J(10, JammerTechnique.Noise, 0f, lookThrough)),
            ("14_am_responsive_noise_js+10", UhfAmKhz, J(10, JammerTechnique.Noise, responsive: true)),
            ("15_haveqick_noise_js+10_10pct_hops", UhfAmKhz, J(10, JammerTechnique.Noise, hop: 0.10f)),
            ("16_haveqick_noise_js+10_50pct_hops", UhfAmKhz, J(10, JammerTechnique.Noise, hop: 0.50f)),
            ("20_fm_clean", VhfFmKhz, null),
            ("21_fm_cw_js-6_suppressed", VhfFmKhz, J(-6, JammerTechnique.Tone, 800f)),
            ("22_fm_cw_js+6_silent_capture", VhfFmKhz, J(6, JammerTechnique.Tone, 800f)),
            ("23_fm_noise_js-6", VhfFmKhz, J(-6, JammerTechnique.Noise)),
            ("24_fm_noise_js+3_denied", VhfFmKhz, J(3, JammerTechnique.Noise)),
        };

        foreach (var (name, khz, jammer) in scenarios)
        {
            var output = Render(voice, khz, jammer);
            WriteMonoWav(Path.Combine(dir, name + ".wav"), output);
        }
    }

    private static float[] Render(float[] voice, int khz, JammerContribution? jammer)
    {
        int lead = (int)(1.5 * Fs), tail = (int)(1.5 * Fs);
        int total = lead + voice.Length + tail;
        var h = new SlotHarness(khz, noiseSeed: 99, squelchLevel: 1f);
        var tx = h.Add(SignalSnrDb, voice: n => n >= lead && n < lead + voice.Length ? voice[n - lead] : 0f);
        if (jammer != null) h.Slot.Jammers = [jammer];

        const int block = 480; // 10 ms, as BASS calls the DSP
        var output = new float[total];
        for (int at = 0; at < total; at += block)
        {
            int len = Math.Min(block, total - at);
            tx.Active = at + len > lead && at < lead + voice.Length; // the carrier is up only while talking
            h.Step(output.AsSpan(at, len));
        }

        return output;
    }

    private static float[] ReadMonoWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int pos = 12, channels = 1, bits = 16;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, pos + 10);
                bits = BitConverter.ToInt16(bytes, pos + 22);
            }
            else if (id == "data")
            {
                if (bits != 16) throw new NotSupportedException("16-bit PCM only");
                int frames = size / 2 / channels;
                var x = new float[frames];
                float peak = 1e-6f;
                for (int i = 0; i < frames; ++i)
                {
                    x[i] = BitConverter.ToInt16(bytes, pos + 8 + i * 2 * channels) / 32768f;
                    peak = Math.Max(peak, Math.Abs(x[i]));
                }

                // Like a normalized mic: peaks near full modulation.
                for (int i = 0; i < frames; ++i) x[i] *= 0.9f / peak;
                return x;
            }

            pos += 8 + size + (size & 1);
        }

        throw new InvalidDataException("no data chunk");
    }

    private static void WriteMonoWav(string path, float[] x)
    {
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = x.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in x) w.Write((short)Math.Clamp(s * 0.6f * 32767f, -32767f, 32767f));
    }
}
