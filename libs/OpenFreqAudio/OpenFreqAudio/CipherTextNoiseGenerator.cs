namespace OpenFreqAudio;

/// <summary>
/// Procedurally approximates the demodulated appearance of an encrypted digital voice waveform
/// (SIM_VINSON ciphertext) — a pseudorandom binary sequence at a nominal data rate, not white
/// noise. Used both for a non-crypto-capable receiver listening to an encrypted transmitter, and
/// for the noise-burst phase of the KY-58 wrong-key sequence in <see cref="KySecureReceiveState"/>.
///
/// SIMULATION CHOICE: the exact ciphertext spectral character is not publicly documented for
/// real VINSON/SAVILLE traffic; this is a perceptual approximation only (Method B from the design
/// brief — a pseudorandom bit stream at a nominal data rate, not literal white noise).
/// </summary>
public sealed class CipherTextNoiseGenerator
{
    // Nominal "bit rate" of the synthesized digital waveform. Real VINSON/CVSD runs at 16 kb/s;
    // once this passes through the existing AM envelope + 300-3000 Hz band-pass chain, a rate
    // anywhere near that just reads as broadband hiss. This lower rate is chosen purely so the
    // result is audibly "digital data" rather than accuracy to the real bit rate.
    private const double BitsPerSecond = 1800.0;

    private static readonly Random SeedRng = new();

    private uint _lfsr;
    private readonly double _samplesPerBit;
    private double _samplesUntilNextBit;
    private float _currentLevel;

    public CipherTextNoiseGenerator(int sampleRate)
    {
        _lfsr = (uint)SeedRng.Next(1, int.MaxValue);
        _samplesPerBit = sampleRate / BitsPerSecond;
        _samplesUntilNextBit = 0;
        _currentLevel = NextBitLevel();
    }

    private float NextBitLevel()
    {
        // 16-bit maximal-length Fibonacci LFSR (taps 16,14,13,11) — cheap, deterministic-per-seed
        // pseudorandom bit source, good enough for a perceptual "digital data" texture.
        uint bit = (_lfsr ^ (_lfsr >> 2) ^ (_lfsr >> 3) ^ (_lfsr >> 5)) & 1u;
        _lfsr = (_lfsr >> 1) | (bit << 15);
        return (_lfsr & 1u) != 0 ? 1f : -1f;
    }

    /// <summary>Next raw sample of the synthesized ciphertext waveform, in [-1, 1].</summary>
    public float NextSample()
    {
        if (_samplesUntilNextBit <= 0)
        {
            _currentLevel = NextBitLevel();
            _samplesUntilNextBit += _samplesPerBit;
        }

        _samplesUntilNextBit -= 1;
        return _currentLevel;
    }
}
