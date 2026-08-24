# ARC-210 UHF SATCOM Simulation

Server-authoritative UHF SATCOM for the A-10C II's ARC-210: cockpit-argument detection and a
5-second local acquisition animation, real orbital ephemeris (SGP4 via a vetted library, sourced
from CelesTrak or from admin-configured static GEO slots), a bent-pipe two-leg link budget with a
proper C/N0 -> Eb/N0 -> BER chain, a server-run DAMA network controller with distinct 5 kHz/25 kHz
schedulers, and a custom MELP/LPC-inspired digital vocoder whose frame-level Clean/Corrected/
Corrupted/Erased decisions are made server-side from the real link physics -- wired into the live
real-time audio pipeline (including actual GEO propagation delay), distinct from the existing
terrestrial AM/FM propagation model.

Every claim below is tagged with its provenance:

- **SOURCE_EXACT** -- an exact, publicly documented value (e.g. the 8.96s 5-kHz DAMA frame,
  WGS84 constants, the speed of light).
- **SOURCE_DERIVED** -- derived via a published formula/method from exact or public inputs (e.g.
  GMST, FSPL, C/N0 combining).
- **PHYSICAL_CALCULATION** -- a first-principles physical calculation with no tunable "art" (e.g.
  Boltzmann-constant noise density, propagation delay from range/c).
- **CALIBRATED_APPROXIMATION** -- tuned to be plausible because the real value isn't public at
  this fidelity (e.g. antenna gain pattern, modulation choice, satellite EIRP stand-in).
- **GAMEPLAY_CONFIG** -- a deliberate gameplay/UX choice with no claim to realism (e.g. default
  hold durations, debug-flag gating).
- **PROJECT_OBSERVED** -- measured directly from this project's own DCS cockpit inspection (e.g.
  the device=0 id=552/553 SATCOM mode argument IDs).

Nothing here claims bitstream compatibility with a real MELP/MELPe/CELP codec, reproduces any
controlled/classified network-control procedure or encryption algorithm, or implements SATURN
(the ARC-210's separate frequency-hopping anti-jam LOS waveform) or MUOS's WCDMA network waveform
-- legacy Dedicated/DAMA UHF SATCOM is what's modeled. Where the real standards (MIL-STD-188-
181/183/185, MIL-STD-3005, FED-STD-1016) are referenced, it's for the publicly-describable
*concepts* they define, not their exact coding tables or classified signaling procedures.

## 1. DCS SATCOM detection (PROJECT_OBSERVED)

DCS doesn't expose a "SATCOM selected" flag. The observed proxy: the ARC-210's channel-select
knob (cockpit argument 552) reads **Channel 31** (~0.8499) and the secondary selector (argument
553) reads **PRST** (~0.2000), simultaneously, compared with a tolerance (not exact equality) to
absorb ordinary cockpit-argument float jitter:

```lua
-- DCS/OpenFreqDCS/Scripts/OpenFreqDCSConfig.lua
OpenFreqDCSConfig.a10c2.satcom = {
    channelSelectorArgument = 552,
    channel31Value = 0.8499,
    secondarySelectorArgument = 553,
    prstValue = 0.2000,
    tolerance = 0.01,
}
```

Computed once per DCS export frame in `buildA10C2Radios()`
(`DCS/OpenFreqDCS/Scripts/OpenFreqDCS.lua`), exported as `satcomSelected`, received client-side as
`DcsRadioState.SatcomSelected`.

## 2. Local acquisition: login vs. band-sustain (PROJECT_OBSERVED + GAMEPLAY_CONFIG)

DCS shows a login/acquisition animation on the ARC-210 when SATCOM is selected. Modeled as a
plain timestamp-based state machine, client-local (this is a radio-mode-transition UI/timing
concern with no network dimension, distinct from DAMA network access, \S6). Two distinct signals,
matching real ARC-210 DAMA ANDVT VOICE channel-plan behavior: Channels 31-40 are all DAMA ANDVT
VOICE channels, but only Channel 31 runs the PRST login procedure -- once logged in, staying
anywhere in that band (with power) keeps SATCOM active without re-running login:

```
NORMAL --[Channel 31 + PRST (loginTrigger)]--> ACQUIRING --[5.0 continuous seconds]--> READY
   ^                                               |
   +---------[loginTrigger drops]------------------+   (cancels, resets timer, back to NORMAL)

READY --[stays in Channel 31-40 band + powered (bandActive)]--> READY  (no re-login needed)
READY --[leaves the band, or loses power]--> NORMAL   (logout -- must return to Channel 31 + PRST)
```

Implementation: `OpenFreq.Client/Services/Satcom/SatcomAcquisitionStateMachine.cs`
(`OpenFreq.Client.Tests/SatcomAcquisitionStateMachineTests.cs`, 12 tests). `Update(loginTrigger,
bandActive, nowMs)` -- `loginTrigger` only ever matters in `NORMAL`/`ACQUIRING` (exact Channel 31
+ PRST); `bandActive` only ever matters once `READY` (anywhere in the configured band). Driven
once per DCS export frame from `ChannelCardListViewModel.SyncDcsRadioOnUiThread`, gated on the
radio being named "ARC-210", with radio power (`DcsRadioState.IsOn`) folded into both signals at
the call site so either one fails safe the instant the radio loses power. The 5.0-second
acquisition duration is **GAMEPLAY_CONFIG** (an earlier pass shortened this from 3.0s on explicit
request, to give more headroom for PTT/connection to settle).

**Band configuration** (`DCS/OpenFreqDCS/Scripts/OpenFreqDCSConfig.lua`,
`a10c2.satcom.channelBandMinValue`/`channelBandMaxValue`): the channel-knob argument-value range
covering Channels 31-40. Defaults to exactly `channel31Value` (i.e. "must stay on Channel 31",
matching the pre-band-sustain behavior) until tuned -- this project doesn't have live DCS cockpit
access to determine the real min/max id552 values across that range itself. To tune: get airborne
with `debugArgScan`/`debugRadios` enabled, step the channel knob through 31-40, read id552's value
at each position from the `ARC210 SATCOM BAND` debug line in `Logs\OpenFreqDCS.log`, then set
`channelBandMinValue`/`channelBandMaxValue` to the observed low/high values.

### 2a. "SATCOM VOICE" frequency display override (PROJECT_OBSERVED)

DCS's export keeps reporting the ARC-210's last-tuned dial frequency even once SATCOM is selected.
`ChannelCardViewModel.FrequencyDisplayText` overrides the readout to show **"SATCOM VOICE"** for
as long as `SatcomAcquisitionState != Normal`. UI label only -- `FrequencyKhz` (and therefore
which network channel is joined for audio routing) is untouched.

## 3. Server-authoritative architecture

Unlike the existing terrestrial AM/FM propagation model (which runs entirely client-side, with
the server acting only as a blind relay), SATCOM's satellite selection, ephemeris, link budget,
BER/frame-disposition decisions, and DAMA network access are **computed and decided by the
server**. This was a deliberate architectural correction: a client cannot self-report perfect
SATCOM quality, and satellite/DAMA state can't be spoofed by a modified client.

```
Client (per DCS export tick):
  - own lat/lon/alt/heading/pitch/bank (real DCS telemetry)
  - ARC-210 login-ready state, PTT, radio-powered
  - local DCS terrain-LOS result toward the last-known assigned satellite
    (via the existing land.isVisible-backed RequestLineOfSight mechanism -- a single BOUNDED
    point ~50km out along the satellite's az/el direction, with a rise/floor that clears any
    real-world terrain, never the full ~35,786km to the satellite itself)
        |
        v  SatcomGeometryUpdateMessage
     [ Server: SatcomServerCoordinator ]
        - SatcomEphemerisService: resolves each catalog satellite's position (StaticGeo or
          SGP4-propagated LiveTle)
        - SatcomSatelliteSelector: Assigned (default) / Manual / AutoBestVisible
        - SatcomLinkEngine: independent uplink (active transmitter -> satellite) and downlink
          (satellite -> this receiver) legs, Earth-ellipsoid occlusion, antenna/footprint gain,
          FSPL, C/N0, linear-domain leg combining, Eb/N0, modulation BER, FEC, frame error rate,
          acquisition/tracking hysteresis, terrain-LOS anti-cheat sanity check
        - DamaNetworkController: server-run per-terminal SatcomDamaStateMachine instances under
          one authoritative capacity/slot-assignment owner (Dama5kHzScheduler/Dama25kHzScheduler)
        - SatcomFrameDispositionModel: seeded, deterministic Clean/Corrected/Corrupted/Erased
          sequence for the next few encoded voice frames, with scheduled arrival timestamps
        |
        v  SatcomLinkStateMessage (+ low-rate SatelliteEphemerisUpdateMessage broadcast)
  Client: displays satellite/quality/DAMA state, feeds frame-error-rate + propagation latency
  into RadioPlayback's existing (already frame-based, already-correct) vocoder/channel-error
  pipeline, and gates playback timing on the real propagation delay (\S9).
```

Shared math (`OpenFreq.Common/Satcom/`) -- geodesy, antenna model, link budget, orbit math, the
DAMA state machine itself, and the frame-disposition model -- is used by both sides so there is
exactly one implementation of the physics, not a client copy and a server copy that could drift.

## 4. Satellite ephemeris (SOURCE_EXACT/SOURCE_DERIVED formulas, CALIBRATED_APPROXIMATION catalog)

`OpenFreq.Server/Satcom/SatcomEphemerisService.cs`. Two first-class, independently-selectable
modes per satellite (`SatcomEphemerisMode`) -- not "live with a fallback":

- **StaticGeo**: fixed longitude/altitude, no time dependence, zero network dependency. No
  network-outage risk, and doesn't force historical/offline DCS missions onto today's real
  satellite positions -- switch any catalog entry to this mode for a guaranteed-available
  fallback.
- **LiveTle** (default catalog): real orbital position, fetched from CelesTrak
  (`https://celestrak.org/NORAD/elements/gp.php?CATNR=<id>&FORMAT=TLE`) roughly every 2 hours
  (matching CelesTrak's own published GP/TLE update cadence -- SOURCE_DERIVED, not arbitrary) by
  `SGPdotNET.TLE.CachingRemoteTleProvider` (from the **SGP.NET** NuGet package -- a vetted,
  independently-maintained SGP4 implementation, not a hand-rolled propagator), which also owns
  on-disk caching and max-age refresh. Local propagation runs at ~1 Hz via `SGPdotNET.Propagation.
  Sgp4`. The TEME -> geodetic conversion uses SGP.NET's own `EciCoordinate.ToGeodetic()` (which
  is sidereal-time/nutation-aware), not this project's own `SatcomOrbitMath` -- that class exists
  as a tested, documented utility (Vallado's GMST polynomial, TEME<->ECEF rotation,
  `OpenFreq.Common.Tests/Satcom/SatcomOrbitMathTests.cs`) but the vetted library is preferred for
  the actual live-ephemeris path.
  The out-of-the-box catalog (`SatcomServerConfig.Default`) ships the real 11-satellite UHF
  Follow-On (UFO) constellation (`ufo-1`..`ufo-11`) in this mode -- their NORAD catalog numbers
  are PROJECT_OBSERVED (looked up against CelesTrak's own live GP query and cross-checked against
  independent sources, 2026-08-22); nothing about their current operational status/coverage is
  asserted (several are decades old and may be retired or have drifted from their original
  station-kept longitude) -- their live position, at whatever it actually is right now, always
  comes from a fresh fetch/SGP4 propagation, never a value baked into this project.
  Never crashes or hard-fails without internet: fetch/propagation failure marks the last-known
  position stale (kept, not snapped) for up to 24h, then falls back to the satellite definition's
  own configured Static longitude/altitude (0 deg by default for the UFO entries, since there's no
  verified current longitude to assert per bird -- if that fallback is ever actually hit, treat it
  as "no real position known" rather than a real one).

Real UHF MILSATCOM constellations (MUOS, legacy FLTSATCOM/UFO) have specific longitude/transponder
assignments that aren't publicly catalogued in the operational detail this project could model
even with a real NORAD id -- **CALIBRATED_APPROXIMATION** applies to the footprint/health/
transponder-capability fields regardless of ephemeris mode.

## 5. Satellite selection (SatelliteSelectionMode)

`OpenFreq.Server/Satcom/SatcomSatelliteSelector.cs`. Corrects the earlier "always connect to
whichever satellite has the best link margin" behavior, which is not how real net-assigned UHF
SATCOM terminals work:

- **Assigned**: fixed satellite per net, from admin config
  (`SatcomNetDefinition.AssignedSatelliteId`). Never auto-picked by margin/distance. If the
  assigned satellite is below horizon/unserviceable, the radio reports no service and waits --
  it does not secretly pick another satellite.
- **Manual**: same behavior as Assigned; distinguished only by provenance (e.g. a mission-scripted
  override vs. the static net table) -- this pass doesn't yet differentiate them operationally.
- **AutoBestVisible** (default net): ranks visible, capacity-available satellites by link margin
  (downlink C/N0 from the terminal's own position), but only switches with **hysteresis** -- a
  minimum margin improvement (`AutoSelectMarginHysteresisDb`, GAMEPLAY_CONFIG) *and* a minimum
  hold duration on the current satellite (`AutoSelectMinHoldSeconds`, GAMEPLAY_CONFIG) before a
  handover is even considered. Never simply "closest/best satellite this exact tick". Selection is
  per (client, net) session, not shared across a whole net: each receiving client independently
  picks whichever catalog satellite it can best see from its own position, and
  `SatcomLinkEngine.Evaluate` then checks the active transmitter's uplink leg against THAT
  specific satellite. Two stations can only hear each other when both can actually close a link to
  a shared bird -- there's no separate "force everyone onto one satellite" step, it falls out of
  each receiver's own per-leg LOS evaluation.

## 6. Link budget (SOURCE_EXACT/PHYSICAL_CALCULATION formulas + two CALIBRATED_APPROXIMATION inputs)

`OpenFreq.Common/Satcom/SatcomLinkBudget.cs` + `SatcomLinkEngineCore.cs` +
`OpenFreq.Server/Satcom/SatcomLinkEngine.cs`. The full chain, per leg, per transmission:

```
FSPL(range, freq)  [PHYSICAL_CALCULATION]
  -> received carrier power (TX power, TX/RX antenna gain, path loss)
  -> C/N0 = carrier - Boltzmann-constant noise density(system noise temp)  [PHYSICAL_CALCULATION]
combine uplink C/N0 and downlink C/N0 in the LINEAR domain (bent-pipe transponder rule:
  1/(C/No)_total = 1/(C/No)_up + 1/(C/No)_down)  [SOURCE_DERIVED]
  -> Eb/N0 = combined C/N0 - 10log10(bit rate)  [PHYSICAL_CALCULATION]
  -> modulation-specific raw BER (noncoherent FSK / DPSK / BPSK textbook curve)  [CALIBRATED_APPROXIMATION
     modulation choice -- the real MIL-STD-188-181/183 waveform's exact modulation isn't public at
     circuit fidelity; noncoherent FSK is the default, representative of legacy UHF tactical gear]
  -> FEC coding-gain shift applied to Eb/N0 before the BER curve, then post-FEC BER
  -> frame error rate = 1-(1-BER)^frameBits  [PHYSICAL_CALCULATION given the independent-bit
     assumption]
```

**Independent per-leg evaluation, not a single "distance between the two aircraft" shortcut**:
one uplink evaluation (active transmitter -> satellite) is computed once per transmission; one
downlink evaluation (satellite -> receiver) is computed independently for every recipient. A
receiver never needs direct line-of-sight to the transmitter, only to the satellite (true
bent-pipe relay modeling) -- verified by
`OpenFreq.Common.Tests/Satcom/SatcomLinkEngineCoreTests.TerminalToTerminalSeparationAloneDoesNotDetermineQuality`.

**Two occlusion checks, not one**:

- **Earth-ellipsoid horizon occlusion** (`SatcomGeodesy.EllipsoidOccludes`, PHYSICAL_CALCULATION
  line-segment/WGS84-ellipsoid intersection): is the satellite geometrically below the curvature
  of the Earth from here. Computed server-side from real geometry.
- **Local DCS terrain LOS**: is there a mountain in the way locally. Only the client has DCS
  terrain access (`land.isVisible`), so the client reports this for a single bounded point along
  the satellite's direction (\S3) -- the server treats a "clear" claim as trustworthy only when
  its own geometry agrees the satellite is above the horizon in the first place (a below-horizon
  "clear" claim is not something a legitimate client-side ray could ever produce), and treats a
  "blocked" claim as authoritative even over an otherwise RF-usable leg.

**Acquisition-vs-tracking hysteresis** (`SatcomLinkQualityTracker`, GAMEPLAY_CONFIG thresholds):
a higher Eb/N0 bar is required to reach `Good` from `Lost` than to remain locked once there, and a
`Holdover` grace period sits between `Degraded` and `Lost` so one bad sample doesn't instantly drop
the call. States: `Good` / `Marginal` / `Degraded` / `Holdover` / `Lost`.

**Two remaining CALIBRATED_APPROXIMATION inputs** (the biggest approximations in the whole model,
now applied per-leg instead of per-aircraft-only): satellite EIRP/antenna-gain advantage
(`SatcomNetDefinition.SatelliteEirpAdjustDb`) and airframe antenna pattern (`SatcomAntennaModel`
-- see its own doc comments).

### 6a. Upper/lower SATCOM antenna diversity (A-10C II)

**CALIBRATED_APPROXIMATION**, same caveat as the rest of `SatcomAntennaModel` -- no public
ARC-210/airframe antenna pattern exists at this fidelity for either physical antenna. Real
diversity-capable airframes (currently just the A-10C II) have two antennas -- a spine-mounted
upper antenna (best gain near zenith) and a belly-mounted lower antenna (best gain near the local
horizon) -- and a pilot-operated selector switch choosing which one is actually connected to the
radio. Which antenna's curve `SatcomAntennaModel.TerminalAntennaGainDb` evaluates against is
`SatcomTerminalState.AntennaSelection` (`SatcomAntennaSelection.Upper`/`.Lower`); non-diversity
airframes never set this and are pinned to `Upper` (single-antenna behavior, unchanged).

**Switch detection** (PROJECT_OBSERVED, user-reported): cockpit argument 707 -- 1.0 = upper, 0.0 =
lower, 0.5 = mid-travel/not a third state. Exported raw by `buildA10C2Radios`
(`DCS/OpenFreqDCS/Scripts/OpenFreqDCS.lua`, config in `OpenFreqDCSConfig.a10c2.satcomAntenna`) as
`DcsRadioState.SatcomAntennaSelectorRaw`; interpreted client-side by
`SatcomAntennaSelectorStateMachine` (`OpenFreq.Client/Services/Satcom/`), a plain latch (not a
debounce/timer machine like `SatcomAcquisitionStateMachine`) that locks onto a clean 1.0/0.0 read
and leaves the prior selection intact on 0.5 or a null/unavailable read. Defaults to **Lower**
before the first clean read -- an explicit product decision, not a physical fact.

**Crossover calibration**: PROJECT_OBSERVED (user-reported), the real switch's SOP is satellite
look angle at/below 30 degrees -> lower antenna, above 30 degrees -> upper antenna. The two curves
in `SatcomAntennaModel.TerminalAntennaGainDb` are calibrated so their own full-gain zones meet
exactly at that boundary (upper: elevation >= 30 degrees full gain; lower: |elevation| <= 30
degrees full gain), each rolling off/airframe-shadowing beyond it -- flying with the switch in the
wrong position for the actual satellite elevation now genuinely costs gain, which the
single-antenna model could never represent at all.

Tilt (bank/pitch) is applied as an **additive penalty on off-boresight angle** for whichever
antenna is selected, not a pre-shift of elevation before computing off-boresight -- those are NOT
equivalent once an antenna's boresight isn't at zenith (elevation 90), and the pre-shift form was
caught producing a directionally-wrong result (more tilt could *improve* the lower antenna's gain,
by mathematical accident of the pre-shift crossing back through its own boresight) by
`SatcomAntennaModelTests.MoreTiltNeverImprovesGainForAFixedSatellite`. The additive form guarantees
more tilt, in either direction, can never improve gain for a fixed satellite, for either antenna --
see `SatcomAntennaModel`'s own doc comments for the exact reasoning.

## 7. DAMA network access (server-authoritative)

`OpenFreq.Server/Satcom/DamaNetworkController.cs` + `DamaFrameScheduler.cs`. A single
authoritative controller owns shared capacity and slot assignment across all terminals on a net;
per-terminal state is the existing, already-tested `SatcomDamaStateMachine`
(`OpenFreq.Common/Satcom/SatcomDamaStateMachine.cs`, moved from client-local, unchanged logic --
`OpenFreq.Common.Tests/Satcom/SatcomDamaStateMachineTests.cs`, 10 tests) -- **now server-run per
(client, net) session**, fed the server's real computed `linkAvailable`/`Eb/N0` instead of a
client's self-only geometry:

```
OFFLINE --[login Ready]--> SEARCHING --[link visible, 1.0s]--> SYNCHRONIZING --[1.5s]--> READY
                                                                                    |
                    +-------------------[PTT]---------------------------------------+
                    v
              REQUESTING --[margin OK AND capacity available]--> ASSIGNED --[PTT held]--> TX
                    |
                    +--[margin too poor, OR net at capacity]--> SERVICE_DENIED --> READY

READY --[receiving]--> RX --[carrier stops]--> READY
Any on-network state --[link lost]--> LOST_SYNC --[link recovers]--> SEARCHING
Any state --[login no longer Ready]--> OFFLINE
```

**5 kHz and 25 kHz DAMA are separate scheduler implementations**
(`Dama5kHzScheduler`/`Dama25kHzScheduler`), not one class parameterized by bandwidth:

- **Dama5kHzScheduler**: the sourced ~8.96-second FOW/ROW/COM frame (**SOURCE_EXACT**, FM 6-02.90).
- **Dama25kHzScheduler**: a distinct, shorter nominal access cycle and higher slot count
  (**CALIBRATED_APPROXIMATION** -- the exact public-domain 25 kHz Automatic/Distributed-Control
  timing isn't available at circuit-implementation fidelity).

**Capacity + generic priority** (`GAMEPLAY_CONFIG`, explicitly *not* a model of real classified
precedence values): each net has a finite `CapacityPerFrame` slot count. A request beyond
capacity is denied unless its priority is strictly higher than the lowest-priority currently-held
slot, in which case that holder is preempted (and must re-request from scratch) -- capacity is
never exceeded. `Dedicated5k`/`Dedicated25k` waveforms skip network access/contention entirely
(effectively unlimited capacity), matching real point-to-point assigned-channel behavior.

Tests: `OpenFreq.Server.Tests/Satcom/DamaFrameSchedulerTests.cs`,
`DamaNetworkControllerTests.cs` (capacity exhaustion, priority preemption, slot release) using a
virtual clock (no real 8.96s waits).

## 8. Rate-limited data service

`OpenFreq.Server/Satcom/SatcomDataService.cs`. Goodput derives from the net's information rate,
FEC/framing overhead, this client's fair share of the net's shared slot capacity, and a
packet-error-driven ARQ retransmission expectation (`1/(1-packetErrorRate)`) -- not a flat
bitrate-minus-nominal-overhead number. Poor RF genuinely costs transfer time (and, at extreme
error rates, genuinely fails rather than claiming instant/guaranteed delivery). Minimal
server+shared-library capability for this pass, not a polished end-user "send file" feature --
see `OpenFreq.Server.Tests/Satcom/SatcomDataServiceTests.cs`.

## 9. Vocoder + frame disposition (the core audio deliverable)

**Chosen approach: custom MELP/LPC-inspired perceptual vocoder** -- explicitly a
**CALIBRATED_APPROXIMATION** of low-rate tactical digital speech, not a bit-exact MELP/MELPe/CELP
implementation (MELPe is patent-encumbered with export/distribution-restricted reference source).
Pipeline (`libs/OpenFreqAudio/OpenFreqAudio/Satcom/`) is unchanged from the prior pass -- LPC
analysis/synthesis via reflection coefficients (guaranteed-stable synthesis filter), pitch/voicing
estimation, mixed excitation, coarse parameter quantization, and per-parameter corruption/
concealment. Fully described (with its own test list) in the class-level doc comments of that
directory; see also `libs/OpenFreqAudio/OpenFreqAudio.Tests/SatcomVocoderTests.cs` (13 tests).

**What changed**: which frames are Clean vs. corrupted, and how, is now decided **server-side**.
`OpenFreq.Common/Satcom/SatcomFrameDispositionModel.cs` extends the original 3-state
(Clean/Corrupted/Lost) outcome to 4 states -- `Clean` / `Corrected` (FEC fully recovered raw bit
errors -- audibly identical to Clean, tracked separately for diagnostics) / `Corrupted` /
`Erased` -- driven by the server's real raw (pre-FEC) and post-FEC frame error rates, with the
same burst-correlation behavior as before. The server precomputes a short lookahead batch
(`SatcomLinkStateMessage.FrameDispositions`, ~8 frames) with scheduled arrival timestamps and
pushes it to the client ahead of playback time.

**Current integration point**: the client currently still drives its local
`OpenFreqAudio.Satcom.SatcomChannelErrorModel` (the original 3-state model) via the server-pushed
`FrameErrorRate` + a quality-state-derived burst-severity proxy
(`ChannelCardListViewModel.OnSatcomLinkStateReceived`) -- i.e. the *rate* is now server-authoritative
real physics, but the literal per-frame dice roll for *which* frames are affected still executes
client-side rather than consuming the server's precomputed `FrameDispositions` batch directly.
Wiring `RadioPlayback.ProcessSatcomSlot` to consume that batch instead (matching frame index to
scheduled arrival timestamp) is a documented follow-on, not yet done in this pass.

## 10. Live audio wiring, including propagation delay

**Still synchronous on the audio callback thread** (`ProcessSatcomSlot`), for the same reasoning
as before (small, bounded per-frame DSP cost; no new cross-thread synchronization surface). The
real-time audio path never does HTTP/SGP4/JSON/file-IO/DB work or blocks on the network -- it only
ever reads the small precomputed FER/latency values `SetSatcomState` was last called with, exactly
the same shape of call as before.

**GEO propagation delay is now actually heard, not just computed.** `RadioConfig.SatcomOutputQueue`
(the existing queue that already smooths the vocoder's frame-quantized output against the audio
callback's fixed per-cycle sample count) now doubles as the propagation-delay buffer: its drain
loop only starts dequeuing once the queue holds at least `SatcomTargetLatencySamples`
(set from the server's real `SatcomLinkResult.PropagationLatencySeconds`, capped at 0.9s as a
safety bound) worth of buffered audio. This means:

- Audio genuinely starts ~250ms-class (real geometry-computed, not hardcoded) after the far end
  keys up, not instantly.
- Already-buffered/in-flight audio keeps draining out for that same duration *after* the far end
  releases PTT, since the drain loop runs every cycle regardless of whether a transmitter is
  currently active -- no more instant cutoff.
- **Local sidetone never touches this queue** (it's mixed through the normal, undelayed
  self-monitoring path) and so stays immediate, exactly as real PTT sidetone should.

**TX side is still not separately implemented** -- the *receiving* client's `ProcessSatcomSlot`
still runs encode-corrupt-decode on whatever clean audio arrives, using that receiver's own real
link quality (now server-computed). `OwnVoiceRadioRenderer` (self-monitoring) is unaffected.

**Still not done** (documented, not silent, follow-on work): wiring `ProcessSatcomSlot` to consume
the server's precomputed `FrameDispositions` batch directly instead of re-deriving FER locally
(\S9); a polished end-user data-transfer UI (\S8 exists as a server+library capability only); the
full 8-scenario in-game multiplayer acceptance matrix (see \S12 -- these need to be flown, not
just implemented); a full statistical audio-intelligibility test corpus (deterministic
frame-error/threshold tests exist instead, plus the offline tool below for listening).

## 11. Offline SATCOM audio test tool

`tools/SatcomAudioTool/` -- unchanged from the prior pass; a standalone console app rendering test
WAVs through the exact same `SatcomVocoder` class the live path uses. See its own `--help` output.

## 12. Testing SATCOM in DCS

1. Get airborne in the A-10C II. Set the ARC-210's channel knob to Channel 31, secondary selector
   to PRST. Watch `Logs\OpenFreqDCS.log` for the SATCOM-selected debug line.
2. `ChannelCardViewModel.SatcomStatusText` should show `SATCOM ACQ x.x/5` then `SATCOM`; the
   frequency readout switches to `SATCOM VOICE`. `DamaStatusText` should progress
   `DAMA SEARCHING` -> `DAMA SYNC` -> `DAMA RDY` once the server confirms link availability.
3. `ChannelCardViewModel.SatcomLinkStatusText` shows the assigned satellite name and quality
   state (or the failure reason if unavailable) once the server's first `SatcomLinkStateMessage`
   arrives.
4. Enable `SettingsViewModel.DebugMode` (and confirm the server's `SatcomServerConfig.
   DebugTelemetryEnabled` is on) to see `ChannelCardViewModel.SatcomDebugText`'s detailed
   readout -- C/N0/Eb/N0/BER/DAMA-frame/slot, own heading/pitch/bank, which antenna is selected,
   the assigned satellite's own lat/lon/alt, and per-leg elevation/azimuth/range/tilt/off-boresight/
   footprint-gain/terminal-gain -- rendered on the channel card itself (`ChannelCardView.axaml`,
   below the frequency), never shown to normal users by default. DEBUG-ONLY, added specifically to
   troubleshoot the antenna-gain model live rather than by re-deriving the math by hand; candidate
   for trimming or a stricter gate once that model is trusted.
5. To test the antenna selector switch specifically: step the ARC-210 SATCOM antenna selector
   through upper (1.0) / mid-travel (0.5) / lower (0.0) and confirm via `SatcomDebugText`'s
   `antenna=` field that it latches Upper/Lower on a clean read and holds its prior value through
   0.5, and that the debug readout's terminal gain for the wrong antenna at a given satellite
   elevation is visibly worse than for the correct one.
6. With two players/clients on the same net: confirm remote audio starts audibly after a short
   (real geometry-driven) delay rather than instantly, and keeps playing briefly after the far end
   releases PTT. Fly one aircraft behind terrain relative to the satellite and confirm its uplink
   degrades/fails for the other party even if the two aircraft are otherwise close together (this
   is the acceptance-scenario matrix from the design brief -- exercising it in a live DCS
   multiplayer session is a manual step this project can't perform on its own).

## 13. Configuration

- `DCS/OpenFreqDCS/Scripts/OpenFreqDCSConfig.lua`: `a10c2.satcom.*` (detection tolerance/values).
- `OpenFreq.Server/ServerConfig.cs` -> `satcom` (`SatcomServerConfig`, JSON):

```json
{
  "satcomEnabled": true,
  "satcom": {
    "ephemerisCacheDirectory": "satcom_cache",
    "ephemerisFetchIntervalHours": 2.0,
    "propagationHz": 1.0,
    "debugTelemetryEnabled": true,
    "satellites": [
      { "id": "ufo-1", "displayName": "UFO 1 (USA 98)", "ephemerisMode": "LiveTle", "noradId": 22563 },
      { "id": "ufo-2", "displayName": "UFO 2 (USA 95)", "ephemerisMode": "LiveTle", "noradId": 22787 },
      { "id": "ufo-3", "displayName": "UFO 3 (USA 104)", "ephemerisMode": "LiveTle", "noradId": 23132 },
      { "id": "ufo-4", "displayName": "UFO 4 (USA 108)", "ephemerisMode": "LiveTle", "noradId": 23467 },
      { "id": "ufo-5", "displayName": "UFO 5 (USA 111)", "ephemerisMode": "LiveTle", "noradId": 23589 },
      { "id": "ufo-6", "displayName": "UFO 6 (USA 114)", "ephemerisMode": "LiveTle", "noradId": 23696 },
      { "id": "ufo-7", "displayName": "UFO 7 (USA 127)", "ephemerisMode": "LiveTle", "noradId": 23967 },
      { "id": "ufo-8", "displayName": "UFO 8 (USA 138)", "ephemerisMode": "LiveTle", "noradId": 25258 },
      { "id": "ufo-9", "displayName": "UFO 9 (USA 140)", "ephemerisMode": "LiveTle", "noradId": 25501 },
      { "id": "ufo-10", "displayName": "UFO 10 (USA 146)", "ephemerisMode": "LiveTle", "noradId": 25967 },
      { "id": "ufo-11", "displayName": "UFO 11 (USA 174)", "ephemerisMode": "LiveTle", "noradId": 28117 },
      { "id": "satcom-static-example", "displayName": "Offline/fallback example",
        "ephemerisMode": "StaticGeo", "staticLongitudeDeg": 100.0 }
    ],
    "nets": [
      { "netId": "a10-arc210-satcom", "displayName": "A-10C II ARC-210 UHF SATCOM (default net)",
        "selectionMode": "AutoBestVisible",
        "waveform": "Dama5k", "bandwidthHz": 5000,
        "uplinkHz": 300000000, "downlinkHz": 260000000 }
    ]
  }
}
```

Every `LiveTle` `noradId` must be an admin-supplied, currently-valid NORAD catalog number,
re-verified against current CelesTrak data before use -- this project ships no example that
claims a real military satellite currently carries a specific operational channel.

## References

- MIL-STD-188-181 -- Interoperability Standard for Access to 5-kHz and 25-kHz UHF SATCOM Channels.
  <https://quicksearch.dla.mil/qsDocDetails.aspx?ident_number=107856>
- MIL-STD-188-183 -- Interoperability Standard for Multiple-Access 5-kHz/25-kHz UHF SATCOM Channels.
  <https://quicksearch.dla.mil/qsDocDetails.aspx?ident_number=107858>
- MIL-STD-188-185 -- Interoperability Standard for UHF MILSATCOM DAMA Control System.
  <https://quicksearch.dla.mil/qsDocDetails.aspx?ident_number=115324>
- FM 6-02.90/MCRP 3-40.3G/NTTP 6-02.9/AFTTP(I) 3-2.53 -- UHF TACSAT and DAMA Operations.
  <https://www.globalsecurity.org/military/library/policy/army/fm/6-02-90/fm6-02-90.pdf>
- MIL-STD-3005 -- 2400 bit/s Mixed Excitation Linear Prediction (MELP).
  <https://melpe.org/wp-content/uploads/2019/02/MIL-STD-3005-MELP.pdf>
- RFC 8130 -- RTP Payload Format for MELPe. <https://datatracker.ietf.org/doc/html/rfc8130>
- RFC 8817 -- RTP Payload Format for TSVCIS. <https://www.rfc-editor.org/info/rfc8817/>
- FED-STD-1016 -- 4800 bit/s Code Excited Linear Prediction (CELP).
  <https://everyspec.com/FED-STD/FED-STD-1016_23395/>
- NAVAIR AN/ARC-210(V) Radio Communications System (public product page).
  <https://www.navair.navy.mil/product/ANARC-210V-Radio-Communications-System>
- Vallado, D. *Fundamentals of Astrodynamics and Applications* -- WGS84/ECEF/topocentric geodesy,
  GMST/TEME<->ECEF conversion (general reference, not a SATCOM-specific source).
- CelesTrak (<https://celestrak.org>) -- public GP/TLE orbital element data source.
- SGP.NET (<https://github.com/parzivail/SGP.NET>) -- the SGP4 propagation library used for
  LiveTle-mode ephemeris.
- Pratt, Bostian & Allnutt, *Satellite Communications* -- bent-pipe transponder C/N0 combining
  rule (linear-domain leg combination).
