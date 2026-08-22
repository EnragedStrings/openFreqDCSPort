using System;

namespace OpenFreq.Common.Satcom;

/// <summary>
/// Server-side, seeded/deterministic per-frame disposition sequencing: decides Clean/Corrected/
/// Corrupted/Erased per encoded SATCOM voice frame from the real computed raw (pre-FEC) and
/// post-FEC frame error rates, with burst-correlated runs (a real antenna-shadowing event lasts
/// multiple frames, not one independent coin-flip at a time). The server decides WHICH frames are
/// impacted and how -- the actual DSP-parameter corruption for a Corrupted frame still happens
/// client-side in OpenFreqAudio's SatcomChannelErrorModel.Corrupt() (only the receiving client has
/// the decoded frame's parameters to perturb), but that call now applies a disposition already
/// decided here rather than the client rolling its own dice for whether corruption happens at all.
/// </summary>
public sealed class SatcomFrameDispositionModel
{
    private readonly Random _rng;
    private int _burstFramesRemaining;

    public SatcomFrameDispositionModel(int seed) => _rng = new Random(seed);

    /// <param name="rawFrameErrorRate">0..1, pre-FEC (from SatcomLinkBudget.RawBer via FrameErrorRate).</param>
    /// <param name="postFecFrameErrorRate">0..1, post-FEC (from SatcomLinkBudget.PostFecBer via
    /// FrameErrorRate) -- always &lt;= rawFrameErrorRate (FEC only ever helps).</param>
    /// <param name="burstSeverity">0..1, how far below threshold the link is -- higher values
    /// produce longer correlated burst impairments.</param>
    public SatcomFrameDisposition NextDisposition(double rawFrameErrorRate, double postFecFrameErrorRate,
        double burstSeverity)
    {
        var rawFer = Math.Clamp(rawFrameErrorRate, 0.0, 1.0);
        var postFecFer = Math.Clamp(Math.Min(postFecFrameErrorRate, rawFer), 0.0, 1.0);
        burstSeverity = Math.Clamp(burstSeverity, 0.0, 1.0);

        if (_burstFramesRemaining > 0)
        {
            _burstFramesRemaining--;
            return PickCorruptedOrErased(postFecFer, burstSeverity);
        }

        if (_rng.NextDouble() >= rawFer)
            return SatcomFrameDisposition.Clean; // no raw bit errors this frame at all

        // Raw errors occurred this frame -- did FEC fully correct them?
        var recoveryProbability = rawFer > 1e-9 ? Math.Clamp(1.0 - postFecFer / rawFer, 0.0, 1.0) : 1.0;
        if (_rng.NextDouble() < recoveryProbability)
            return SatcomFrameDisposition.Corrected;

        if (burstSeverity > 0.3 && _rng.NextDouble() < burstSeverity)
            _burstFramesRemaining = 1 + (int)(burstSeverity * 6);

        return PickCorruptedOrErased(postFecFer, burstSeverity);
    }

    /// <summary>Mirrors the original 3-state model's Lost-vs-Corrupted split (see
    /// OpenFreqAudio.Satcom.SatcomChannelErrorModel): near threshold, mild corruption dominates;
    /// well below threshold, total erasure dominates and becomes certain as postFecFer/burstSeverity
    /// approach 1.</summary>
    private SatcomFrameDisposition PickCorruptedOrErased(double postFecFer, double burstSeverity)
    {
        var erasedProbability = Math.Clamp(postFecFer * 1.5 + burstSeverity * 0.3, 0.0, 1.0);
        return _rng.NextDouble() < erasedProbability ? SatcomFrameDisposition.Erased : SatcomFrameDisposition.Corrupted;
    }
}
