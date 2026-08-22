using System.Text.Json.Serialization;

namespace OpenFreq.Common.Satcom;

/// <summary>How a satellite's position is determined. Two first-class modes (not a
/// live-with-fallback relationship) -- see docs/SATCOM_SIMULATION.md.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SatcomEphemerisMode>))]
public enum SatcomEphemerisMode
{
    /// <summary>Fixed longitude/altitude, no time dependence. Zero network dependency; the
    /// default so SATCOM works out of the box and so historical/offline DCS missions aren't
    /// forced onto today's real satellite positions.</summary>
    StaticGeo,

    /// <summary>Real orbital position via CelesTrak-sourced elements and SGP4 propagation.
    /// Admin-supplied NORAD ID required. Falls back to cache, then to a configured StaticGeo
    /// definition, if the network/elements are unavailable.</summary>
    LiveTle
}

/// <summary>How a client's serving satellite is chosen for a given net.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SatelliteSelectionMode>))]
public enum SatelliteSelectionMode
{
    /// <summary>Fixed satellite per net, from admin config. The realistic default: real UHF
    /// SATCOM terminals are net-assigned, not "nearest satellite wins".</summary>
    Assigned,

    /// <summary>Operator/mission-pinned satellite id, distinct from Assigned only in provenance
    /// (e.g. set via a scripted mission trigger rather than the static net table).</summary>
    Manual,

    /// <summary>Optional server mode: ranks visible, capacity-available satellites by link
    /// margin/elevation, with hysteresis (minimum margin delta + minimum hold duration) before
    /// handover -- never simply "closest satellite this tick".</summary>
    AutoBestVisible
}

/// <summary>Legacy UHF SATCOM waveform family. 5 kHz and 25 kHz DAMA are distinct protocols with
/// their own framing, not one scheduler parameterized by bandwidth. IW/MUOS are deliberately not
/// modeled here -- legacy Dedicated/DAMA is what an A-10C II ARC-210 actually exposes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SatcomWaveform>))]
public enum SatcomWaveform
{
    /// <summary>Point-to-point assigned channel, 5 kHz. No DAMA network access required.</summary>
    Dedicated5k,

    /// <summary>Point-to-point assigned channel, 25 kHz.</summary>
    Dedicated25k,

    /// <summary>Demand-assigned, 5 kHz. ~8.96 s FOW/ROW/COM frame (SOURCE_EXACT, FM 6-02.90).</summary>
    Dama5k,

    /// <summary>Demand-assigned, 25 kHz. Distinct Automatic/Distributed-Control framing from
    /// 5 kHz DAMA (SOURCE_DERIVED from FM 6-02.90's 25 kHz DAMA description).</summary>
    Dama25k
}

/// <summary>Whether a waveform is DAMA (shared, network-access-controlled) or Dedicated (always
/// available once tuned, no network login).</summary>
public static class SatcomWaveformExtensions
{
    public static bool IsDama(this SatcomWaveform waveform) =>
        waveform is SatcomWaveform.Dama5k or SatcomWaveform.Dama25k;

    public static bool Is25kHz(this SatcomWaveform waveform) =>
        waveform is SatcomWaveform.Dedicated25k or SatcomWaveform.Dama25k;
}

/// <summary>Representative modulation family used for the Eb/N0-&gt;BER curve. CALIBRATED_APPROXIMATION:
/// the exact modulation used by a given MIL-STD-188-181/183 legacy waveform variant isn't public at
/// circuit-implementation fidelity; noncoherent FSK is a reasonable stand-in for legacy UHF SATCOM
/// (it's what most legacy UHF tactical/DAMA-era equipment of this class used) and is the default.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SatcomModulation>))]
public enum SatcomModulation
{
    NoncoherentFsk,
    Dpsk,
    Bpsk
}

/// <summary>Per-frame outcome of the simulated digital channel, decided server-side from the real
/// computed BER/FER -- richer than a plain Clean/Lost binary so FEC's actual job (recovering some
/// errored frames) is visible as its own state rather than folded into "clean".</summary>
public enum SatcomFrameDisposition
{
    /// <summary>No bit errors survived to the frame level.</summary>
    Clean,

    /// <summary>Bit errors occurred but FEC fully recovered them -- audibly identical to Clean,
    /// tracked separately for diagnostics/testing.</summary>
    Corrected,

    /// <summary>FEC could not fully recover the frame, but enough of it survived (or the codec's
    /// concealment can extrapolate from it) to still be represented, degraded.</summary>
    Corrupted,

    /// <summary>Frame did not survive at all -- concealment/silence, same idea as the previous
    /// "Lost" outcome.</summary>
    Erased
}

/// <summary>Link-quality state with acquisition-vs-tracking hysteresis: a higher Eb/N0 bar is
/// required to move toward Good, a lower one to fall away from it, and a Holdover grace period
/// sits between Degraded and Lost so a momentary bad sample doesn't instantly drop the call.</summary>
public enum SatcomLinkQualityState
{
    Good,
    Marginal,
    Degraded,
    Holdover,
    Lost
}

/// <summary>Why a client currently has no usable SATCOM service, surfaced to the UI/debug panel
/// instead of a bare "unavailable" boolean.</summary>
public enum SatcomAcquisitionFailureReason
{
    None,
    RadioNotReady,
    NoNetConfig,
    NoSatAssignment,
    EphemerisStale,
    BelowHorizon,
    TerrainBlocked,
    InsufficientAcquisitionMargin,
    OrderwireNotAcquired,
    RangingFailed,
    LoginTimeout,
    NoServiceCapacity
}

/// <summary>Provenance tag for every modeled parameter (spec's classification requirement),
/// surfaced in docs/SATCOM_SIMULATION.md and debug telemetry so it's always clear which numbers
/// are real physics/public standards vs. tuned approximations vs. pure gameplay config.</summary>
public enum SatcomValueSource
{
    /// <summary>A publicly documented, exact value (e.g. the ~8.96s 5kHz DAMA frame, WGS84
    /// constants, speed of light).</summary>
    SourceExact,

    /// <summary>Derived via a published formula/method from exact or public inputs (e.g. GMST,
    /// FSPL, C/N0 combining).</summary>
    SourceDerived,

    /// <summary>A first-principles physical calculation with no tunable "art" in it (e.g.
    /// Boltzmann-constant noise density, propagation delay from range/c).</summary>
    PhysicalCalculation,

    /// <summary>Tuned to be plausible/well-behaved because the real value isn't public at this
    /// fidelity (e.g. antenna gain pattern, modulation choice, satellite EIRP stand-in).</summary>
    CalibratedApproximation,

    /// <summary>A deliberate gameplay/UX choice with no claim to realism (e.g. default hold
    /// durations, debug-flag gating).</summary>
    GameplayConfig,

    /// <summary>Measured directly from this project's own DCS cockpit inspection (e.g. the
    /// device=0 id=552/553 SATCOM mode argument IDs, dial-decode ranges).</summary>
    ProjectObserved
}
