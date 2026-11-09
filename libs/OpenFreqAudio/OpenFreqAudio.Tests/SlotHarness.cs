namespace OpenFreqAudio.Tests;

/// <summary>
/// Drives <see cref="SlotReceiver"/> the way RadioPlayback's DSP callback does, without BASS: one slot on one
/// channel, a set of transmitters with fixed SNR and tune offset, deterministic voice and seeded noise.
/// </summary>
internal sealed class SlotHarness
{
    public const int Fs = RadioPlayback.SampleRate;

    public sealed class Transmitter
    {
        public required int TxKhz { get; init; }
        public required double SnrDb { get; set; }
        public double TuneOffsetPpm { get; init; }
        /// <summary>Voice sample at absolute sample index n (null: unmodulated carrier).</summary>
        public Func<long, float>? Voice { get; init; }
        /// <summary>Only this many samples of each callback carry audio (null: all), to mimic a short stream.</summary>
        public int? ShortBy { get; init; }
        public bool Active { get; set; } = true;
        public ReceivedCarrier Carrier { get; } = new();
        public float[] Buffer = [];
    }

    public RadioPlayback.RadioConfig Slot { get; }
    public int FrequencyKhz { get; }
    public List<Transmitter> Transmitters { get; } = new();
    public SlotReceiver Receiver { get; } = new();
    public long SampleIndex { get; private set; }
    public int SampleNum { get; private set; }

    private readonly List<ReceivedCarrier> _active = new();

    public SlotHarness(int frequencyKhz, int noiseSeed = 1234, float squelchLevel = 1f)
    {
        FrequencyKhz = frequencyKhz;
        Slot = new RadioPlayback.RadioConfig(frequencyKhz)
        {
            NoiseGenerator = new BackgroundNoiseGenerator(Fs, frequencyKhz, noiseSeed),
            SquelchLevel = squelchLevel,
            IsTuned = true,
        };
    }

    public Transmitter Add(double snrDb, int txKhz = 0, double ppm = 0, Func<long, float>? voice = null)
    {
        var tx = new Transmitter { TxKhz = txKhz == 0 ? FrequencyKhz : txKhz, SnrDb = snrDb, TuneOffsetPpm = ppm, Voice = voice };
        Transmitters.Add(tx);
        return tx;
    }

    /// <summary>Run one callback of <paramref name="samples"/> into <paramref name="output"/>.</summary>
    public SlotReceiveResult Step(Span<float> output)
    {
        int samples = output.Length;
        _active.Clear();
        foreach (var tx in Transmitters)
        {
            if (!tx.Active) continue;
            int len = Math.Max(0, samples - (tx.ShortBy ?? 0));
            if (len == 0) continue;
            if (tx.Buffer.Length < len) tx.Buffer = new float[len];
            for (int n = 0; n < len; ++n)
                tx.Buffer[n] = tx.Voice?.Invoke(SampleIndex + n) ?? 0f;
            var c = tx.Carrier;
            c.Samples = tx.Buffer.AsMemory(0, len);
            var state = new CarrierState(Math.Pow(10, tx.SnrDb / 20.0), tx.TuneOffsetPpm);
            c.CarrierStart = state;
            c.CarrierEnd = state;
            c.CarrierPhaseStepStart = RadioPlayback.PhaseStep(tx.TxKhz, FrequencyKhz, tx.TuneOffsetPpm);
            c.CarrierPhaseStepEnd = c.CarrierPhaseStepStart;
            _active.Add(c);
        }

        var result = Receiver.Process(Slot, _active, output, SampleNum);
        Advance(samples);
        return result;
    }

    /// <summary>The carriers to hand a legacy implementation for the same callback (same setup as Step).</summary>
    public List<ReceivedCarrier> Prepare(int samples)
    {
        var list = new List<ReceivedCarrier>();
        foreach (var tx in Transmitters)
        {
            if (!tx.Active) continue;
            int len = Math.Max(0, samples - (tx.ShortBy ?? 0));
            if (len == 0) continue;
            var buf = new float[len];
            for (int n = 0; n < len; ++n)
                buf[n] = tx.Voice?.Invoke(SampleIndex + n) ?? 0f;
            var c = tx.Carrier;
            c.Samples = buf;
            var state = new CarrierState(Math.Pow(10, tx.SnrDb / 20.0), tx.TuneOffsetPpm);
            c.CarrierStart = state;
            c.CarrierEnd = state;
            c.CarrierPhaseStepStart = RadioPlayback.PhaseStep(tx.TxKhz, FrequencyKhz, tx.TuneOffsetPpm);
            c.CarrierPhaseStepEnd = c.CarrierPhaseStepStart;
            list.Add(c);
        }
        return list;
    }

    /// <summary>Advance carrier phases and clocks once per callback, as RadioPlayback does after its slot loop.</summary>
    public void Advance(int samples)
    {
        foreach (var tx in Transmitters)
        {
            var c = tx.Carrier;
            c.CarrierPhase = RadioPlayback.AdvancePhase(c.CarrierPhase, c.CarrierPhaseStepStart, c.CarrierPhaseStepEnd, samples);
        }
        SampleIndex += samples;
        SampleNum = (SampleNum + samples) % Fs;
    }

    /// <summary>Run <paramref name="totalSamples"/> in callbacks of <paramref name="block"/> and return the output.</summary>
    public float[] Run(int totalSamples, int block = 480)
    {
        var output = new float[totalSamples];
        for (int at = 0; at < totalSamples; at += block)
            Step(output.AsSpan(at, Math.Min(block, totalSamples - at)));
        return output;
    }

    /// <summary>A crude band-limited "voice": five tones in 300–3000 Hz under a 4 Hz syllabic envelope.</summary>
    public static float Voice(long n)
    {
        double t = n / (double)Fs;
        double syllable = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 4.0 * t);
        double v = 0.30 * Math.Sin(2 * Math.PI * 410 * t)
                 + 0.22 * Math.Sin(2 * Math.PI * 730 * t + 0.4)
                 + 0.16 * Math.Sin(2 * Math.PI * 1130 * t + 1.1)
                 + 0.10 * Math.Sin(2 * Math.PI * 1710 * t + 2.0)
                 + 0.07 * Math.Sin(2 * Math.PI * 2390 * t + 0.3);
        return (float)(v * syllable);
    }

    /// <summary>Voice through the receiver's own band-pass, as the reference for correlation.</summary>
    public static float[] FilteredVoice(int frequencyKhz, int totalSamples)
    {
        var filters = new RadioPlayback.RadioConfig(frequencyKhz);
        var r = new float[totalSamples];
        for (int n = 0; n < totalSamples; ++n)
            r[n] = filters.LowPass.Process(filters.HighPass.Process(Voice(n)));
        return r;
    }

    public static double Rms(ReadOnlySpan<float> x)
    {
        if (x.IsEmpty) return 0;
        double s = 0;
        foreach (var v in x) s += (double)v * v;
        return Math.Sqrt(s / x.Length);
    }

    /// <summary>Correlation coefficient of <paramref name="x"/> with <paramref name="reference"/>.</summary>
    public static double Correlation(ReadOnlySpan<float> x, ReadOnlySpan<float> reference)
    {
        double xy = 0, xx = 0, yy = 0;
        for (int n = 0; n < x.Length; ++n)
        {
            xy += (double)x[n] * reference[n];
            xx += (double)x[n] * x[n];
            yy += (double)reference[n] * reference[n];
        }
        return xx > 0 && yy > 0 ? xy / Math.Sqrt(xx * yy) : 0;
    }

    /// <summary>RMS of the part of <paramref name="x"/> that is the reference (its projection onto it).</summary>
    public static double VoiceLevel(ReadOnlySpan<float> x, ReadOnlySpan<float> reference)
    {
        double xy = 0, yy = 0;
        for (int n = 0; n < x.Length; ++n)
        {
            xy += (double)x[n] * reference[n];
            yy += (double)reference[n] * reference[n];
        }
        return yy > 0 ? Math.Abs(xy) / Math.Sqrt(yy * x.Length) : 0;
    }

    /// <summary>Amplitude of the <paramref name="hz"/> component of <paramref name="x"/> (single-bin DFT).</summary>
    public static double ToneAmplitude(ReadOnlySpan<float> x, double hz)
    {
        double re = 0, im = 0;
        for (int n = 0; n < x.Length; ++n)
        {
            double w = 2 * Math.PI * hz * n / Fs;
            re += x[n] * Math.Cos(w);
            im -= x[n] * Math.Sin(w);
        }
        return 2 * Math.Sqrt(re * re + im * im) / x.Length;
    }
}
