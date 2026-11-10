namespace OpenFreqAudio.Jamming;

/// <summary>
/// The jamming technique, which decides the waveform synthesized into the receiver (see
/// docs/jamming/research-01-physics.md §3–4 and the FM 24-33 sound catalogue).
/// </summary>
public enum JammerTechnique
{
    /// <summary>Band-limited Gaussian noise (spot noise, or the in-passband slice of a barrage/partial-band).</summary>
    Noise,

    /// <summary>Unmodulated carrier (CW): heterodyne whistle on AM, silent capture on FM.</summary>
    Tone,

    /// <summary>Several simultaneous CW tones.</summary>
    MultiTone,

    /// <summary>Tones stepped through a sequence ("bagpipes").</summary>
    SteppedTones,

    /// <summary>A carrier swept back and forth across a span ("wobbler").</summary>
    Swept,

    /// <summary>Randomly timed noise pulses ("random pulse").</summary>
    RandomPulse,

    /// <summary>Short, regular, very sharp pulses ("spark").</summary>
    Spark,

    /// <summary>Rapidly rising-and-falling tone bursts ("gulls").</summary>
    Gulls,

    /// <summary>Babble / recorded-sound-like modulation (voice-band noise shaped like speech).</summary>
    Recorded
}

/// <summary>
/// Technique-specific waveform parameters. Unused fields are ignored by techniques that don't need them.
/// All values are at audio/baseband rates; frequencies are offsets from the jammer's centre in Hz.
/// </summary>
public sealed record JammerWaveformParams
{
    /// <summary>Tone offsets for MultiTone / SteppedTones, in Hz from the jammer centre.</summary>
    public IReadOnlyList<float> ToneOffsetsHz { get; init; } = [];

    /// <summary>Dwell per step for SteppedTones, in ms.</summary>
    public float StepIntervalMs { get; init; } = 150f;

    /// <summary>Peak-to-peak sweep span for Swept / Gulls, in Hz.</summary>
    public float SweepSpanHz { get; init; } = 2000f;

    /// <summary>Sweep repetition rate for Swept / Gulls, in Hz (sweeps per second).</summary>
    public float SweepRateHz { get; init; } = 4f;

    /// <summary>Mean pulse rate for RandomPulse / Spark, in pulses per second.</summary>
    public float PulseRateHz { get; init; } = 50f;

    /// <summary>Pulse on-fraction (0..1) for RandomPulse / Spark.</summary>
    public float PulseDuty { get; init; } = 0.1f;

    /// <summary>Look-through: the jammer goes silent for <see cref="LookThroughDuty"/> of every period, to listen.
    /// 0 disables look-through.</summary>
    public float LookThroughPeriodMs { get; init; }

    /// <summary>Fraction (0..1) of each look-through period the jammer is silent.</summary>
    public float LookThroughDuty { get; init; }

    /// <summary>Seed for the waveform's random processes, so a jammer sounds the same to everyone.</summary>
    public int Seed { get; init; }
}

/// <summary>
/// One jammer's contribution at one receiver slot, computed by the client at the physics tick and handed to
/// playback with <c>IPlaybackService.SetSlotInterference</c>. The DSP adds it to the slot's complex baseband
/// as one more signal, after noise and before envelope/AGC/squelch, in the receiver's unit-noise scale.
/// </summary>
/// <param name="JammerId">Stable id, so per-jammer DSP state (phase, PRNG) survives updates.</param>
/// <param name="JnrDb">In-passband jammer-to-noise ratio at this receiver, dB, after path loss, antenna gain and
/// bandwidth dilution. The DSP uses amplitude 10^(JnrDb/20) against unit-power noise.</param>
/// <param name="Technique">Waveform to synthesize.</param>
/// <param name="CarrierOffsetHz">Jammer centre relative to the receiver's tuned frequency, Hz (within the
/// passband; includes transmitter frequency tolerance).</param>
/// <param name="Params">Technique parameters.</param>
/// <param name="Responsive">True for responsive/preamble/follower-style jamming on a single channel: the jammer
/// only radiates while a wanted signal is present on the slot, after <paramref name="ReactionMs"/>.</param>
/// <param name="ReactionMs">Reaction delay for responsive jamming, ms.</param>
/// <param name="HopHitFraction">For hopping receivers (HAVE QUICK / SINCGARS): fraction of hops this jammer hits
/// (μ, or the follower's per-dwell fraction). 1 for single-channel receivers.</param>
/// <param name="HopFollower">A follower (responsive) jammer against a hopping receiver: instead of hitting a
/// fraction of hops, it hits every hop for the last <paramref name="HopHitFraction"/> of the dwell (it arrives
/// T_delay late on each new hop).</param>
public sealed record JammerContribution(
    string JammerId,
    float JnrDb,
    JammerTechnique Technique,
    float CarrierOffsetHz,
    JammerWaveformParams Params,
    bool Responsive = false,
    float ReactionMs = 0f,
    float HopHitFraction = 1f,
    bool HopFollower = false);

/// <summary>
/// Where a callback sits on a hopping slot's shared hop clock: hop index (with fraction) at its first sample, and
/// hops per sample. With the net's seed it fixes, for every listener on the net alike, which hops a jammer hits.
/// </summary>
internal readonly record struct HopTiming(ulong Seed, double HopsAtStart, double HopsPerSample);
