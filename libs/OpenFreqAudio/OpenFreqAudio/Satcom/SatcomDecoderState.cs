using System;

namespace OpenFreqAudio.Satcom;

/// <summary>
/// Decoder-side state carried across frames -- last valid parameters, consecutive-loss tracking,
/// and sync state -- so lost/corrupted frames are concealed with continuity rather than each
/// frame being synthesized as an independent isolated sound effect (see design brief's "Frame
/// Loss Concealment"/"Decoder State" requirements). One instance per active SATCOM receive slot.
/// </summary>
public sealed class SatcomDecoderState
{
    public double LastPitchHz { get; private set; }
    public double LastEnergy { get; private set; }
    public double LastVoicing { get; private set; }
    public double[] LastReflection { get; private set; } = [];
    public int ConsecutiveLossCount { get; private set; }

    /// <summary>True once concealment has run out of confidence (several consecutive lost
    /// frames) -- decoder should output silence and wait for network resync rather than keep
    /// extrapolating stale parameters indefinitely.</summary>
    public bool SyncLost { get; private set; }

    private const int MaxConcealedFrames = 8;

    /// <summary>Conceal one lost frame: hold the last valid parameters with progressive
    /// attenuation (~25% per consecutive loss) rather than silence or a click. Call once per lost
    /// frame; does not mutate Last* (those stay pinned to the last genuinely valid frame so
    /// concealment doesn't compound decay from its own previous concealed output).</summary>
    public (double PitchHz, double Energy, double Voicing, double[] Reflection) Conceal()
    {
        ConsecutiveLossCount++;
        if (ConsecutiveLossCount > MaxConcealedFrames)
            SyncLost = true;

        var decay = Math.Pow(0.75, ConsecutiveLossCount - 1);
        return (LastPitchHz, LastEnergy * decay, LastVoicing, LastReflection);
    }

    /// <summary>Record a genuinely decoded (clean or corrupted-but-usable) frame as the new
    /// concealment baseline, and clear loss/sync state -- the "recovery crossfade" back into
    /// valid audio happens implicitly since the LPC synthesis filter's own history already
    /// provides continuity; no separate crossfade buffer is needed.</summary>
    public void CommitValidFrame(double pitchHz, double energy, double voicing, double[] reflection)
    {
        LastPitchHz = pitchHz;
        LastEnergy = energy;
        LastVoicing = voicing;
        LastReflection = reflection;
        ConsecutiveLossCount = 0;
        SyncLost = false;
    }

    public void Reset()
    {
        LastPitchHz = 0;
        LastEnergy = 0;
        LastVoicing = 0;
        LastReflection = [];
        ConsecutiveLossCount = 0;
        SyncLost = false;
    }
}
