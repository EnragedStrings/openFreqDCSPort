namespace OpenFreqAudio;

/// <summary>
/// A carrier's amplitude and tune offset at one instant, from its <see cref="CarrierSmoother"/>.
/// </summary>
internal readonly record struct CarrierState(double Amplitude, double TuneOffsetPpm);

/// <summary>
/// Smooths a transmitter's carrier between physics results (<see cref="AudioParams"/>),
/// so amplitude and frequency changes are continuous instead of steps.
/// </summary>
/// <remarks>
/// Physics updates run every ~60 ms, but just replacing the previous values with the new ones
/// causes sudden jumps in SNR and Doppler shift, which can cause popping and AGC warbling.
/// Close formation is the worst case, since relatively small changes in distance or direction
/// can cause relatively large changes in SNR and radial velocity.
///
/// Each value goes through a second-order low-pass, so the output and its slope are both continuous,
/// and the output never overshoots the target.
///
/// Only what the mixer uses is smoothed: <see cref="AudioParams.ReceivedSnrDb"/> sets the carrier's
/// amplitude, and <see cref="AudioParams.TuneOffsetPPM"/> sets its frequency.
/// The rest of <see cref="AudioParams"/> is a link budget for logs, so it stays as physics computed it.
/// </remarks>
internal class CarrierSmoother
{
    /// <summary>
    /// The time constant of each stage.
    /// After a step of S dB, the steepest slope is S / (e · τ). For a 9 dB step that is 41 dB/s,
    /// or 1.4 dB in one AGC decay time constant (<see cref="RadioPlayback.AgcDecay"/>).
    /// The cost is about 2τ of delay on real changes in level.
    /// </summary>
    public const double TimeConstantSeconds = 0.08;

    private TwoStage _receivedSnrDb;
    private TwoStage _tuneOffsetPpm;

    public CarrierSmoother(AudioParams initial) => Reset(initial);

    public float ReceivedSnrDb => _receivedSnrDb.Value;
    public float TuneOffsetPPM => _tuneOffsetPpm.Value;

    /// <summary>
    /// The carrier at the current smoothed values.
    /// The SNR is a power ratio in dB over the receiver's unit-power noise,
    /// so the carrier amplitude is its square root.
    /// </summary>
    public CarrierState Carrier => new(Math.Pow(10, ReceivedSnrDb / 20.0), TuneOffsetPPM);

    /// <summary>
    /// Go straight to <paramref name="target"/>, for example when a new transmission starts.
    /// </summary>
    public void Reset(AudioParams target)
    {
        _receivedSnrDb.Reset(target.ReceivedSnrDb);
        _tuneOffsetPpm.Reset(target.TuneOffsetPPM);
    }

    /// <summary>
    /// Move toward <paramref name="target"/> over <paramref name="dtSeconds"/>,
    /// with the target held constant for that time.
    /// </summary>
    public void Step(AudioParams target, double dtSeconds)
    {
        float alpha = (float)(1 - Math.Exp(-dtSeconds / TimeConstantSeconds));
        _receivedSnrDb.Step(target.ReceivedSnrDb, alpha);
        _tuneOffsetPpm.Step(target.TuneOffsetPPM, alpha);
    }

    public override string ToString() => $"ReceivedSnrDb: {ReceivedSnrDb}, TuneOffsetPPM: {TuneOffsetPPM}";

    private struct TwoStage
    {
        private float _first;
        private float _second;

        public readonly float Value => _second;

        public void Reset(float x)
        {
            _first = x;
            _second = x;
        }

        public void Step(float target, float alpha)
        {
            // A non-finite value would latch the filter forever. That is how a single NaN
            // SNR silenced a radio until reconnect in 1.1.0. Hold the last good value instead,
            // and start over from the next good target if a reset brought one in.
            if (!float.IsFinite(target)) return;
            if (!float.IsFinite(_first) || !float.IsFinite(_second))
            {
                Reset(target);
                return;
            }

            _first += alpha * (target - _first);
            _second += alpha * (_first - _second);
        }
    }
}
