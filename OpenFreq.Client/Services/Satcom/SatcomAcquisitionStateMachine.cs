using System;

namespace OpenFreq.Client.Services.Satcom;

public enum SatcomState
{
    /// <summary>ARC-210 is not logged into SATCOM: either never logged in, or logged in and then
    /// left the Channel 31-40 DAMA ANDVT VOICE band (or lost power). SATCOM unavailable.</summary>
    Normal,

    /// <summary>Controls are in the Channel 31 + PRST login configuration but the acquisition/
    /// login delay (modeling DCS's ARC-210 SATCOM login animation, plus time for the network/DAMA
    /// layer to come up) hasn't elapsed yet. SATCOM unavailable -- PTT must not route through
    /// SATCOM in this state.</summary>
    Acquiring,

    /// <summary>Logged in: held the Channel 31 + PRST login configuration continuously for at
    /// least <see cref="SatcomAcquisitionStateMachine.AcquisitionSeconds"/>, and has stayed
    /// somewhere in the Channel 31-40 band (with power) ever since. SATCOM available.</summary>
    Ready
}

/// <summary>
/// Tracks the ARC-210's SATCOM login/logout lifecycle. DCS OBSERVED BEHAVIOR: selecting Channel 31
/// + PRST doesn't make SATCOM usable instantly -- the cockpit shows a login/acquisition animation
/// first. Real ARC-210 DAMA ANDVT VOICE channel plan behavior (per the design brief this state
/// machine was corrected against): Channels 31-40 are all DAMA ANDVT VOICE channels, but only
/// Channel 31 runs the actual PRST login procedure -- once logged in, the radio stays active as
/// long as the knob remains anywhere in that 31-40 band (and stays powered), not just exactly on
/// Channel 31. Leaving the band (or losing power) logs out and requires a fresh Channel 31 + PRST
/// login to get back in.
///
/// Modeled as a plain timestamp comparison (no sleep/blocking of any kind), fed two DCS-reported
/// cockpit-argument signals once per DCS export frame: the exact login trigger
/// (<see cref="Models.Dcs.DcsRadioState.SatcomSelected"/>) and the broader band-sustain signal
/// (<see cref="Models.Dcs.DcsRadioState.SatcomBandActive"/>, combined with radio power at the
/// call site -- see ChannelCardListViewModel.SyncDcsRadioOnUiThread).
///
/// Deliberately takes "now" as a parameter rather than reading a clock itself, so it's fully
/// deterministic and unit-testable without fakes/mocks -- callers pass
/// <c>Environment.TickCount64</c> (or any other monotonic millisecond source).
///
/// SIMULATION APPROXIMATION: DCS doesn't expose an actual "SATCOM acquired" signal, so the exact
/// duration is chosen to give a plausible login/network-acquisition delay, not a documented spec
/// value. 5.0s (rather than the cockpit login animation's shorter apparent length) deliberately
/// leaves headroom for the DAMA layer (see SatcomDamaStateMachine) to reach READY within the same
/// window, so PTT becomes available roughly when both the ARC-210 login and network access are
/// actually done.
/// </summary>
public sealed class SatcomAcquisitionStateMachine
{
    public const double AcquisitionSeconds = 5.0;

    public SatcomState State { get; private set; } = SatcomState.Normal;

    /// <summary>Seconds continuously spent in the Channel 31 + PRST login configuration so far
    /// this acquisition attempt. 0 in <see cref="SatcomState.Normal"/>, clamped to
    /// <see cref="AcquisitionSeconds"/> once <see cref="SatcomState.Ready"/>.</summary>
    public double AcquisitionElapsedSeconds { get; private set; }

    private long? _acquisitionStartedAtMs;

    /// <summary>
    /// Advance the state machine with the latest DCS-observed cockpit state.
    /// </summary>
    /// <param name="loginTrigger">DcsRadioState.SatcomSelected for the ARC-210 this frame -- true
    /// only when both the channel knob reads exactly Channel 31 and the secondary selector reads
    /// PRST, within tolerance (already computed in OpenFreqDCS.lua). Only ever starts/continues an
    /// acquisition attempt from <see cref="SatcomState.Normal"/>/<see cref="SatcomState.Acquiring"/>
    /// -- once <see cref="SatcomState.Ready"/>, this signal is ignored in favor of
    /// <paramref name="bandActive"/>.</param>
    /// <param name="bandActive">True when the channel knob is anywhere in the configured Channel
    /// 31-40 DAMA ANDVT VOICE band AND the radio is powered (DcsRadioState.SatcomBandActive
    /// combined with DcsRadioState.IsOn at the call site) -- the sustain/logout signal once
    /// <see cref="SatcomState.Ready"/>.</param>
    /// <param name="nowMs">Current monotonic time in milliseconds (e.g.
    /// <c>Environment.TickCount64</c>). Must be non-decreasing across calls.</param>
    /// <returns>The resulting state after this update.</returns>
    public SatcomState Update(bool loginTrigger, bool bandActive, long nowMs)
    {
        switch (State)
        {
            case SatcomState.Normal:
                if (loginTrigger)
                {
                    State = SatcomState.Acquiring;
                    _acquisitionStartedAtMs = nowMs;
                    AcquisitionElapsedSeconds = 0.0;
                }
                break;

            case SatcomState.Acquiring:
                if (!loginTrigger)
                {
                    // Left Channel 31 + PRST before finishing login -- cancel, no partial credit.
                    // Re-entering requires a full new acquisition.
                    State = SatcomState.Normal;
                    _acquisitionStartedAtMs = null;
                    AcquisitionElapsedSeconds = 0.0;
                    break;
                }

                var startedAt = _acquisitionStartedAtMs ?? nowMs;
                var elapsedSeconds = Math.Max(0.0, (nowMs - startedAt) / 1000.0);
                AcquisitionElapsedSeconds = Math.Min(elapsedSeconds, AcquisitionSeconds);
                if (elapsedSeconds >= AcquisitionSeconds)
                {
                    State = SatcomState.Ready;
                    AcquisitionElapsedSeconds = AcquisitionSeconds;
                }
                break;

            case SatcomState.Ready:
                if (!bandActive)
                {
                    // Left the whole Channel 31-40 band (or lost power) -- log out. Getting back
                    // in requires a fresh Channel 31 + PRST login, not just returning to the band.
                    State = SatcomState.Normal;
                    _acquisitionStartedAtMs = null;
                    AcquisitionElapsedSeconds = 0.0;
                }
                else
                {
                    // Roaming within 31-40 (including off the exact login channel/selector) keeps
                    // SATCOM active without re-running the login sequence.
                    AcquisitionElapsedSeconds = AcquisitionSeconds;
                }
                break;
        }

        return State;
    }
}
