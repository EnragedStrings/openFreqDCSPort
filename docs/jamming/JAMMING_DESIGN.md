# OpenFreq Communications Jamming — Design

Status: design agreed 2026-11-09, implementation not started.
Research behind every number here: `research-01-physics.md` (jamming equation, J/S thresholds, waveforms and
sounds), `research-02-eccm.md` (HAVE QUICK, SINCGARS, SATCOM, Link-16, hop model), `research-03-systems.md`
(real jammers, preset table, doctrine), `research-04-codebase-map.md` (integration points, file:line).
Figures there are tagged sourced / derived / estimated; presets built on estimates must say so in the UI/docs.

## Goals

- A mission maker places a jammer (server config, or a unit in the .miz) and every receiver in range experiences
  physically consistent jamming: by frequency, bandwidth, technique, power, geometry, terrain and line of sight.
- Jamming is heard, not scripted: the AM heterodyne whistle, "monkey chatter" under a strong jammer, AGC
  capture, squelch held open, FM silent capture, noise clicks, the Army FM 24-33 sound catalogue (random noise,
  stepped tones/"bagpipes", spark, gulls, random pulse, wobbler, recorded sounds, preamble jamming).
- ECCM matters: HAVE QUICK and SINCGARS frequency hopping defeat spot jammers, not barrage or follower jammers;
  training/FMT hopsets are weak; burn-through by closing range; changing frequency forces a reacquire; killing the
  jammer unit ends it.
- Same model for OpenFreq clients, SATCOM (link budget) and SRS users through the bridge.

## User decisions (2026-11-09)

| Topic | Decision |
|---|---|
| Placement | Server config first; then .miz units named `JAM:<preset>[...]` via a DCS server hook, tied to the unit being alive |
| Targeting | Scripted (explicit frequencies / bands) **and** an auto mode (detect active frequencies, spot-jam the busiest with look-through, protected list) |
| HAVE QUICK | Full WOD / TOD / net modelling end to end, not just the HqOn flag |
| v1 scope | AM/FM voice, HAVE QUICK + SINCGARS hop model, SATCOM, SRS bridge |

## Physical model (summary; equations in research-01 §1, §6 and research-02 §6)

Per (receiver r, jammer k), at the physics tick (10–20 Hz, smoothed like signals):

```
J_r,k [dBm]   = EIRP_k(θ) − L_path(k→r) + G_r − L_pol                     // same propagation as signals:
                                                                          // FastPathAudioSim (terrain/two-ray) or
                                                                          // FreeSpacePathModel + DCS LOS / horizon
J_in-band     = J_r,k − 10·log10(max(1, B_J / B_R)) · overlap(B_J, receiver passband)
JNR           = J_in-band − N_r            // N_r: receiver noise floor already used for SNR
technique gating: duty cycle, look-through, sweep position, pulse, follower f_hit = max(0, 1 − T_delay·R_h)
```

The jammer is injected into the receiver's complex baseband **as one more signal**, in the same unit-noise
scale (`amplitude = 10^(JNR/20)`), after the noise and before envelope/AGC/squelch
(RadioPlayback per-slot I/Q sum). FM capture weights and the FM threshold use SINR (signals + jammers).
Thresholds to sanity-check against (research-02 §6.4): T_kill AM ≈ 0 dB, FM ≈ +3 dB, digital ≈ 0 dB.

Hopping nets (research-02 §6.3–6.5): μ = |hopset ∩ jammed band| / N computed analytically; audio processed in
hop-sized blocks, hits drawn from a PRNG seeded by WOD/TOD/net so every listener hears the same hits; AM HQ →
noise bursts, SINCGARS CVSD → mute/garble; KY-58 preamble hit loses the transmission.

SATCOM (research-02 §3): jammer adds I0 to the uplink leg N0 (SatcomLinkEngineCore.EvaluateLeg); bent-pipe hard
limiting gives power robbing to every user on that channel; DAMA orderwire jamming blocks assignments.

## Architecture

### Common (`OpenFreq.Common/Jamming/`)
- `JammerDefinition` — id, name, preset id, coalition, position (lat/lon/alt **and/or** DCS-local x/y/z),
  alive, transmitter power W, antenna gain dBi + pattern (omni | sector with azimuth/beamwidth), feeder loss,
  technique, technique parameters (sweep span/rate, tone list/step, pulse PRF/duty, follower reaction ms,
  look-through period/duty), targeting (`scripted`: spot frequencies + bands; `auto`: band limits, max
  simultaneous spots, dwell, reacquire delay, protected list), on/off schedule.
- `JammerPresets` — the research-03 §4 table (r934b, r934um2, r330b, r378, pelena6, leer2, palantin, tirada_uhf,
  il22pp, mi8ppa, ec130h, ea37b, ea6b_usq113, ea18g_lb, quickfix, tlq17a, ulq19, mlq34, generic_manpack) with an
  `Estimated` flag per figure.
- `JammingMath` — pure functions: in-band overlap/dilution, EIRP with pattern, JNR, burn-through range,
  follower hit fraction, hop fraction μ, intelligibility (SII / Miller–Licklider tables). Fully unit-tested.
- Signaling: `JammerCatalogUpdate` (definitions + live state: alive, current target frequencies per jammer,
  on/off), broadcast on change and every 5 s, modelled on `SatelliteEphemerisUpdate`. Four-edit pattern:
  SignalingMessageTypes, SignalingMessages, SignalingMessageFactory, OpenFreqJsonContext.

### Server
- `ServerConfig.Jamming` (`jamming: { enabled, jammers: [...] }`) following the `SatcomServerConfig` catalog pattern.
- `JammingCoordinator` — owns the jammer set (config + DCS-hook jammers), runs the auto-targeting brain,
  publishes `JammerCatalogUpdate`.
  - Auto brain: candidate frequencies = frequencies with transmissions whose transmitter is within the jammer's
    detection range (presence positions + LOS oracle when available); score by activity; pick up to N spots;
    dwell with look-through; when a target net changes frequency, reacquire after a delay; never the protected
    list (guard 121.5/243.0 by default). Deterministic for tests (injectable clock/random).
- SATCOM: jammers flagged for SATCOM uplink add I0 to the affected channel's uplink leg.
- SRS bridge: per SRS listener, compute J with FreeSpacePathModel and degrade `SrsAudioProcessor` output
  (same waveform generators, run server-side).
- DCS hook intake (phase 6): authenticated message from the DCS server hook carrying mission jammers.

### Audio library (`libs/OpenFreqAudio`)
- Prerequisite refactor: extract the per-slot receive mix from the BASS DSP lambda into an internal, testable
  `SlotReceiver.Process(...)` with no behaviour change (pinned by golden tests on seeded noise).
- `Jamming/JammerWaveform` generators producing complex baseband per sample: band-limited noise (spot/barrage
  slice), CW/multi-tone, stepped tones, swept/wobbler (chirp phase), pulse/spark/gulls (gated), recorded/voice
  (looped sample through the jammer's modulation), with carrier offset (randomised transmitter tolerance).
- `RadioConfig.Jammers` set through `IPlaybackService.SetSlotInterference(freq, slot, contributions)`.
- Hop engine for HQ/SINCGARS slots: hop-block gating seeded from WOD/TOD/net.
- NaN guard covers jammer state; jammers never trip `SignalBlocked` or stream-gap logic.

### Client
- `JammingService` — subscribes to `JammerCatalogUpdate`; at the physics tick, for every tuned slot computes each
  jammer's contribution (ISignalCalculator / FreeSpacePathModel + DCS LOS with key `jam:{id}`, receiver
  sensitivity) and pushes them to playback. Lat/lon jammers on a DCS client are converted to DCS-local once via
  a new Lua `coord.LLtoLO` request (there is no C# projection).
- UI: "JAMMED" / "INTERFERENCE" badge on the channel card (SignalBlockedText pattern), optional J/S readout in
  debug view, and in GCI mode a jammer list.

### HAVE QUICK (full) and SINCGARS
- Model: WOD (6 segments × 6 digits; segment 1 and 2 semantics incl. conferencing per research-02 §1.1), MWOD (up
  to 6 dated WODs), TOD (with a sync state: received / not received; drift), net number (A00.000–A99.975 with
  NATO .x25 / non-NATO .x50 / training / FMT nets), hopset selection (NATO ≈2000, non-NATO ≈4000, FMT 5–16 from
  a frequency list), hop rate default 100 hops/s (all hidden values are estimates and configurable).
- TRANSEC gate: two radios communicate only if WOD, net and TOD match within the sync window; otherwise the
  listener hears nothing (or hop-collision noise). Replaces the `HqOn` equality at RadioPlayback's gate.
- Wire: `FrequencyTransmission` gains HQ/SINCGARS net identity (net, WOD hash — never raw WOD — and TOD epoch).
- DCS export: per-module investigation of what each radio exposes (ARC-164 in A-10C II / F-16C / F-15E / F-5E,
  ARC-210 in A-10C II / F/A-18C / AV-8B / F-15E, SINCGARS-capable radios); where a module doesn't simulate HQ,
  the OpenFreq channel card provides manual WOD/net entry and a "TOD from GPS / from net" action.
- SINCGARS: net ID, hopset with lockout bands, sync time; same hop engine.

### DCS mission jammers (phase 6)
- `Scripts/Hooks/OpenFreqJamming.lua` (GameGUI hook) on the **DCS server**: scans mission groups/units whose name
  matches `JAM:<preset>[;key=value...]` (e.g. `JAM:r934b;mode=auto;band=225-400`), tracks alive/position,
  sends them to the OpenFreq server (configured endpoint + server password). Unit death or removal turns the
  jammer off. Installer adds the hook alongside the existing export script.

## Phases

| # | Phase | Delivers | Depends on |
|---|---|---|---|
| 0 | Refactor | `SlotReceiver` extracted + golden tests, no behaviour change | — |
| 1 | Core RF jamming | Common model + presets + math; server config + catalog broadcast; client JammingService; DSP injection with all waveforms; FM capture/threshold on SINR; UI badge; scripted targeting | 0 |
| 2 | Auto targeting | Server jammer brain (detect, select, look-through, reacquire, protected list) | 1 |
| 3 | HAVE QUICK + SINCGARS | Full WOD/TOD/net model, TRANSEC gate, wire fields, DCS export per module, manual entry UI, hop engine + jamming μ/follower | 1 |
| 4 | SATCOM | Uplink I0, power robbing, DAMA orderwire jamming | 1 |
| 5 | SRS bridge | Server-side jamming of bridged SRS listeners | 1 |
| 6 | DCS mission jammers | GameGUI hook, server intake, alive tracking, installer | 1, 2 |

Every phase ends with: build, full tests, and the relevant sanity checks below as automated tests where possible.

## Acceptance sanity checks (research-02 §6.7, research-01 §1.8)

1. Spot jammer 50 W at 50 nm vs a single-channel UHF net with the talker at 100 nm: net denied. Same jammer vs
   an HQ net (combat hopset): ≈0.05% hops hit, unaffected.
2. Barrage 225–400 MHz, 1 kW: ≈38–40 dB weaker per 25 kHz channel than spot; degrades all nets at range,
   kills inside burn-through; HQ gains nothing.
3. Partial-band on 10% of the band: HQ μ ≈ 0.1 (occasional ticks); single-channel nets inside the band killed.
4. Follower vs HQ at 100 hops/s with 2 ms reaction: ≈80% of each dwell jammed → heavily degraded.
5. One uplink jammer on a UHF SATCOM channel: every user on that channel degraded.
6. Ground R-934B 100 km from a fighter: GCI/AWACS→fighter J/S ≈ +10…+19 dB (denied); wingman at 5 km
   ≈ −6 dB (gets through).
7. FM receiver: carrier jammer ≥ +3 dB above the voice captures it silently; noise jammer gives clicks then denial.
8. Killing the jammer (unit dead / config off) restores clean audio within one catalog update.
