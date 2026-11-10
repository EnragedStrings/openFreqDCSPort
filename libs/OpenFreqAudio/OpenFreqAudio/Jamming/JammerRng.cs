namespace OpenFreqAudio.Jamming;

/// <summary>
/// A small deterministic PRNG for jammer waveforms (xorshift64*, seeded through SplitMix64), so a jammer
/// sounds the same on every machine and .NET version and nothing is allocated per draw. Mutable struct:
/// keep it in a field and never copy it.
/// </summary>
internal struct JammerRng
{
    private ulong _state;
    private double _spareGaussian;
    private bool _hasSpare;

    public JammerRng(ulong seed)
    {
        // SplitMix64 once, so nearby seeds give unrelated streams and a zero seed is still a valid state.
        ulong z = seed + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        _state = z == 0 ? 0x2545F4914F6CDD1DUL : z;
        _spareGaussian = 0;
        _hasSpare = false;
    }

    /// <summary>Seed from a jammer's <see cref="JammerWaveformParams.Seed"/>, its id and a stream number, so two
    /// jammers left at the default seed still don't produce identical (coherent) noise.</summary>
    public static JammerRng For(int seed, string jammerId, ulong stream)
    {
        // FNV-1a: string.GetHashCode is randomized per process, which would make clients disagree.
        ulong h = 0xCBF29CE484222325UL;
        foreach (char ch in jammerId)
        {
            h ^= ch;
            h *= 0x100000001B3UL;
        }
        return new JammerRng(h ^ ((ulong)(uint)seed << 32) ^ (stream * 0xD1B54A32D192ED03UL));
    }

    public ulong NextULong()
    {
        ulong x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>Uniform on [0, 1).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Standard normal (Marsaglia polar method; draws come in pairs).</summary>
    public double NextGaussian()
    {
        if (_hasSpare)
        {
            _hasSpare = false;
            return _spareGaussian;
        }

        double u, v, s;
        do
        {
            u = NextDouble() * 2.0 - 1.0;
            v = NextDouble() * 2.0 - 1.0;
            s = u * u + v * v;
        } while (s >= 1.0 || s == 0.0);

        double f = Math.Sqrt(-2.0 * Math.Log(s) / s);
        _spareGaussian = v * f;
        _hasSpare = true;
        return u * f;
    }
}
