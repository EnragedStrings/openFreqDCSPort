# Configuring communications jammers (scripted and auto)

Jamming is off by default. Turn it on in `OpenFreq.Server.json` with a `jamming` section. The server
broadcasts the jammers to every client (on change, every `broadcastIntervalSeconds`, and on connect);
each client works out what its own radios hear from each jammer, using the same propagation as normal
signals (terrain, horizon, DCS line of sight). Design: `JAMMING_DESIGN.md`.

```json
{
  "jamming": {
    "enabled": true,
    "broadcastIntervalSeconds": 5,
    "jammers": [
      {
        "id": "sinitsa-1",
        "presetId": "r934b",
        "coalition": "red",
        "latitude": 42.18,
        "longitude": 42.48,
        "pattern": "Sector",
        "azimuthDeg": 270,
        "technique": "Noise",
        "targeting": {
          "spotFrequenciesKhz": [ 251000, 264000 ],
          "bands": [ { "startKhz": 225000, "endKhz": 235000 } ]
        },
        "schedule": { "startDelaySeconds": 60, "onSeconds": 300, "offSeconds": 120 }
      },
      {
        "id": "fm-barrage",
        "presetId": "r330b",
        "dcsPosition": { "x": -281000, "y": 30, "z": 647000 },
        "txPowerW": 500,
        "technique": "SteppedTones",
        "toneOffsetsHz": [ -1500, -500, 500, 1500 ],
        "targeting": { "bands": [ { "startKhz": 30000, "endKhz": 40000 } ] }
      }
    ]
  }
}
```

## Fields

Every physical field is optional when `presetId` is set: what you leave out comes from the preset.

| Field | Meaning |
|---|---|
| `id` | Unique id (also seeds the jammer's sound, so everyone hears the same thing). |
| `presetId` | One of the presets below. Unknown or missing → a generic 100 W, 2 dBi jammer. |
| `enabled`, `alive` | `false` silences the jammer (it stays in the catalog). |
| `latitude`, `longitude`, `altitudeMeters` | Geodetic position. Without `altitudeMeters` the antenna is `heightAglMeters` above the ground (preset mast height). DCS clients convert it to map coordinates through their own DCS (`coord.LLtoLO`). |
| `dcsPosition` `{x, y, z}` | DCS mission coordinates (x north, y altitude MSL, z east, metres). Used directly by DCS clients. |
| `bmsPosition` `{x, y, z}` | Falcon BMS theater coordinates, feet (as in BMS shared memory). |
| `txPowerW`, `powerAmplifiers` | Power per PA. Up to `powerAmplifiers` targets each get full power; more share it. |
| `antennaGainDbi`, `feederLossDb` | EIRP = P + G − feeder loss. |
| `pattern` | `Omni` or `Sector` (`azimuthDeg` boresight, `beamwidthDeg` −3 dB width, default 60, `frontToBackDb` default 20). |
| `minFrequencyKhz`, `maxFrequencyKhz`, `maxSimultaneousSpots` | Tuning range and spot capacity; targets outside are dropped. |
| `technique` | `Noise`, `Tone`, `MultiTone`, `SteppedTones`, `Swept`, `RandomPulse`, `Spark`, `Gulls`, `Recorded`. |
| `toneOffsetsHz`, `stepIntervalMs`, `sweepSpanHz`, `sweepRateHz`, `pulseRateHz`, `pulseDuty` | Waveform parameters. |
| `lookThroughPeriodMs`, `lookThroughDuty` | Periodic silent windows. |
| `responsive`, `reactionMs` | Radiate only while someone transmits on the channel, after the reaction delay. |
| `carrierToleranceHz` | ± transmitter offset (default 300 Hz): the pitch of the AM whistle. |
| `targeting.spotFrequenciesKhz` | Channels to jam; each gets `spotBandwidthKhz` (default 25). |
| `targeting.bands` | Barrage / partial-band ranges; power is spread across the band (≈ −38 dB per channel for 225–400 MHz). |
| `targeting.protectGuard` | Default `true`: 121.5 and 243.0 MHz are notched out of bands. A spot listed on guard is still jammed. |
| `targeting.protectedFrequenciesKhz` | Own nets: notched out of bands and never spot-jammed. |
| `targeting.mode` | `Scripted` (default) or `Auto` (see [Auto targeting](#auto-targeting)). Scripted spots and bands are radiated in both modes. |
| `targeting.autoBands` | Auto: only pick frequencies inside these ranges (default: the whole tuning range). |
| `targeting.autoMaxSpots` | Auto: most auto spots at once (default: the preset's spots, less the scripted spots). |
| `targeting.detectionSensitivityDbm` | Auto: weakest transmission the intercept receiver hears (preset: −110; Compass Call −120). |
| `targeting.activityHalfLifeSeconds` | Auto: half-life of the activity score (default 60). |
| `targeting.dropAfterSeconds` | Auto: silence after which a target is dropped (default 30). |
| `targeting.reacquireDelaySeconds` | Auto: search/DF/set-on time before a newly heard frequency is jammed (preset: 10; Compass Call 5). |
| `schedule` | `startDelaySeconds`, then `onSeconds` on / `offSeconds` off, until `stopAfterSeconds`; counted from server start. |

## Auto targeting

An auto jammer behaves like a real one (research-03 §6): it listens, locates, spot-jams the busiest
frequencies it hears, pauses to listen, and goes looking again when a net goes quiet or moves.

```json
{
  "id": "sinitsa-auto",
  "presetId": "r934b",
  "latitude": 42.18,
  "longitude": 42.48,
  "lookThroughPeriodMs": 1000,
  "lookThroughDuty": 0.1,
  "targeting": {
    "mode": "Auto",
    "autoBands": [ { "startKhz": 225000, "endKhz": 400000 } ],
    "autoMaxSpots": 2,
    "protectedFrequenciesKhz": [ 262000, 270500 ],
    "reacquireDelaySeconds": 10,
    "dropAfterSeconds": 30
  }
}
```

**It jams any frequency it hears, whichever side is talking.** Radio can't tell friend from foe, so the
jammer doesn't either. List your own side's frequencies in `protectedFrequenciesKhz`. Guard (121.5 / 243.0)
is protected unless `protectGuard` is `false`.

How it works (on the server; the catalog shows each auto jammer's emissions, plus `autoState`
`Searching`/`Jamming` and `autoTargetsKhz`; changes go out within 1 s):

1. **Detect.** It hears a transmission when the transmitter's position is known, the frequency is inside
   the tuning range and `autoBands` and isn't protected, and the link budget clears the sensitivity:
   `P_tx − FSPL(d, f) ≥ detectionSensitivityDbm`. P_tx is assumed to be 10 W VHF/UHF (100 W HF), with
   0 dBi antennas. Beyond the 4/3-earth radio horizon of the two antenna heights it hears nothing. When a
   DCS client can check terrain line of sight, a blocked path is not heard. When nobody can check, only
   the horizon applies. Transmitter positions come from the position sent with the transmission, the
   client's DCS presence, or the SRS player's position for bridged SRS users. If no position is known,
   the transmitter can't be detected. The jammer itself needs `latitude`/`longitude`.
2. **Score.** Each frequency gets keyed seconds plus 1 per key-down, decaying with `activityHalfLifeSeconds`.
3. **Reacquire delay.** A frequency becomes a target only `reacquireDelaySeconds` after it was first heard.
4. **Select.** Free spots go to the highest scores. When all spots are full, a waiting frequency takes a
   spot from a target in two cases. Either the target has been silent for the reacquire delay and the
   new frequency was heard more recently (the net moved), or the new frequency scores 1.25× the weakest
   target. Auto and scripted spots share the power amplifiers (power ÷ targets beyond the PA count).
5. **Look-through.** While it jams a frequency, it only hears that frequency during the silent part of
   each `lookThroughPeriodMs`. With a period of 0, a separate receiver listens all the time. A target with
   no detected activity for `dropAfterSeconds` is dropped, and it must be found again (step 3) before the
   jammer comes back to it.

**Counter-measures for the players:**
- **Brevity.** Short, rare calls keep the score low. With look-through, a short call can fall entirely
  inside a jamming period and not be heard.
- **QSY / CHATTERMARK.** Changing frequency costs the jammer at least the reacquire delay (10 s by default)
  before it finds the new net. Changing again, or alternating between briefed frequencies, keeps it
  behind you.
- **Burn-through.** Close the range to the other station, or kill the jammer.
- **Make yourself hard to hear.** Terrain masking, or low altitude, which shortens your radio horizon to
  the jammer.
- **HAVE QUICK / SINCGARS** frequency hopping beats a spot jammer outright (a spot on a 2000-channel combat
  hopset hits 0.05 % of hops). Partial-band, barrage and follower jammers still hurt, and training / FMT nets are
  weak. The comm plan (`eccm` section) and the details are in [HAVE_QUICK.md](HAVE_QUICK.md).

## Presets

From `research-03-systems.md` §4. **All figures marked [E] are estimates**: antenna gain always is, and
for most airborne systems power and capacity are too. Spot EIRP = P + G − 2 dB.

| id | System | MHz | Power | Gain | Spot EIRP | Spots | Estimated |
|---|---|---|---|---|---|---|---|
| `r934b` | R-934B Sinitsa | 100–400 | 500 W × 3 PA | 8 | 33 dBW | 3 | gain, PAs, spots |
| `r934um2` | R-934UM2 Groza-6 | 30–3000 | 500 W × 4 | 9 | 34 | 28 | power, gain |
| `r330b` | R-330B Mandat-B | 30–100 | 1 kW | 6 | 34 | 6 | gain |
| `r378` | R-378A | 1.5–30 | 1 kW | 2 | 30 | 4 | power, gain |
| `pelena6` | Pelena-6 | 20–1000 | 60 W | 2 | 18 | 2 | gain |
| `leer2` | Leer-2 | 20–2700 | 200 W | 3 | 24 | 2 | power, gain |
| `palantin` | Palantin | 3–300 | 2 kW | 5 | 36 | 8 | power, gain |
| `tirada_uhf` | Tirada-2 (hypothetical UHF) | 290–320 | 1 kW | 22 | 50 | 2 | all |
| `il22pp` | Il-22PP | 30–400 | 1 kW | 9 | 37 | 8 | all |
| `mi8ppa` | Mi-8 EW | 30–400 | 500 W | 5 | 30 | 2 | all |
| `ec130h` | EC-130H Compass Call | 30–3000 | 1.5 kW | 13 | 43 | 16 | power, gain |
| `ea37b` | EA-37B Compass Call | 30–3000 | 1.5 kW | 15 | 45 | 16 | all |
| `ea6b_usq113` | EA-6B Prowler | 20–500 | 1 kW × 2 | 10 | 38 | 2 | power, gain |
| `ea18g_lb` | EA-18G Growler | 64–500 | 1 kW | 10 | 38 | 4 | power, gain |
| `quickfix` | AN/ALQ-151 Quick Fix | 1.5–80 | 1 kW | 0 | 28 | 2 | gain |
| `tlq17a` | AN/TLQ-17A Traffic Jam | 1.5–80 | 550 W | 1.6 | 27 | 1 | gain |
| `ulq19` | AN/ULQ-19 RACJAM | 20–80 | 250 W | 2 | 24 | 1 | gain |
| `mlq34` | AN/MLQ-34 TACJAM | 20–80 | 2 kW × 3 | 8 | 39 | 3 | power, gain |
| `generic_manpack` | Generic manpack | 20–500 | 35 W | 1.6 | 15 | 2 | all |

## What clients compute

For each tuned radio and each active jammer emission, at about 10 Hz:

```
J      = EIRP(toward receiver, pattern) × power share − path loss   (terrain model when a heightmap is loaded,
                                                                      else free space + horizon; DCS LOS on top)
J_in   = J + 10·log10(overlap(emission, receiver IF) / emission width)   IF: 16 kHz (AM 25 kHz, FM), 6 kHz (8.33)
JNR    = J_in − receiver sensitivity                                  (the same reference as the radio's SNR)
```

The channel card shows **JAMMED** when the jammer is at or above the signal it would cover
(J/S ≥ 0 dB AM, +3 dB FM; or JNR ≥ 20 dB with no signal), and **INTERFERENCE** when it is audible.
Radios in SATCOM mode are handled by the server instead (next section).

## SATCOM (phase 4)

Legacy UHF SATCOM is a hard-limited bent pipe with no processing gain, so it is jammed through the satellite, not at
each receiver. The server works it out in the SATCOM link evaluation (every 500 ms per user); the client's own
jamming computation skips SATCOM radios. No jammer setting is needed: any jammer **with a `latitude`/`longitude`**
whose emissions overlap a SATCOM channel takes part. Jammers placed only by `dcsPosition`/`bmsPosition` cannot be
placed relative to a satellite and are ignored for SATCOM.

| Path | Who is hit | Model |
|---|---|---|
| **Uplink** (emission overlaps the net's `uplinkHz` ± bandwidth/2) | **Every user on that channel**, anywhere in the footprint | Jammer power at the satellite J = EIRP(sky) − FSPL(slant range) + footprint gain + the satellite's receive advantage (`satelliteEirpAdjustDb`), the same scale as a user's uplink. I0 = J/B is added to the uplink N0. The jammer must see the satellite (above its horizon, not Earth-occluded, inside the footprint). |
| **Power robbing** | Same | The hard limiter gives the wanted signal S/(S+J+N) of the downlink power instead of S/(S+N), less a further 0–1 dB (noise) or 0–6 dB (tone, sweep, multi-tone) of weak-signal suppression. |
| **Downlink** (emission overlaps the net's downlink) | Receivers within the jammer's radio horizon | J at the receiver in free space, 0 dBi receive antenna, −10 dB for an aircraft above the jammer (top-mounted antenna). The satellite downlink is weak, so this works out to long range with line of sight. Terrain is not modelled. |
| **DAMA orderwire** (emission overlaps `orderwireUplinkHz`, default the net's uplink) | The whole DAMA network | Jammed when J at the satellite ≥ the control station's orderwire (`orderwireEirpDbw`, default 30 dBW) + `orderwireKillJsDb` (default 0). Channel requests are denied at once; after 1.5 frames (13.4 s on 5 kHz DAMA) terminals lose sync and must re-acquire once it clears. |

The jammer's gain toward the satellite is `satcomAntennaGainDbi`. It defaults to 0 dBi for every preset except
`tirada_uhf` (22 dBi dish): tactical comms jammers point their gain at the horizon. **Estimate.**

What it means in practice (default net: 20 W terminal, 5 kHz, 2400 bit/s):

| Jammer | Effect |
|---|---|
| `tirada_uhf` spot on the uplink | J/S ≈ +30 dB at the satellite: channel denied for everyone |
| `r934b` spot (25 kHz) on the uplink | J/S ≈ +7 dB: channel denied for everyone (the "UHF pirates" case) |
| `r934b` barrage 225–400 MHz | ≈ −30 dB at the satellite: about 1 dB lost; but it also covers the downlink, so aircraft inside its radio horizon (240–330 km at 3,000–6,000 m) lose the link |

On the card: **JAMMED** when jamming took the link to Degraded or worse (or the DAMA orderwire is jammed),
**INTERFERENCE** when it cost at least 1 dB. A link lost to jamming reports the failure reason `Jammed`; a DAMA
network that lost sync to orderwire jamming reports `OrderwireNotAcquired`. With debug telemetry on, the SATCOM debug
line shows uplink J/S, robbing loss and downlink J/N. The voice degrades through the existing SATCOM frame-error
model (vocoder dropouts, then muting), with no extra audio.

Net fields (in `satcom.nets[]`):

| Field | Default | Meaning |
|---|---|---|
| `orderwireUplinkHz` | the net's `uplinkHz` | DAMA control channel uplink frequency |
| `orderwireEirpDbw` | 30 | Control station orderwire EIRP, dBW (estimate) |
| `orderwireKillJsDb` | 0 | J/S at the satellite that takes the orderwire (estimate) |

**Hopping radios (HAVE QUICK / SINCGARS).** The client hands playback the hop hit fraction instead of a constant
jammer. It is μ = (channels of the radio's hopset the jammer covers) / N. A responsive jammer becomes a follower,
which jams the last 1 − (reaction + 2·d/c)·R_h of every dwell. The per-hop J/N is the jammer's power in one channel.
All listeners on a net hear the same hops hit. The badge stays blank under 1 % of hops and shows at most
INTERFERENCE under 50 %. See [HAVE_QUICK.md](HAVE_QUICK.md).
