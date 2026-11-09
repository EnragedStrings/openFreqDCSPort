# Configuring communications jammers (phase 1: scripted)

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
| `targeting.mode` | `Scripted` (phase 1). `Auto` is reserved for phase 2 and radiates nothing yet. |
| `schedule` | `startDelaySeconds`, then `onSeconds` on / `offSeconds` off, until `stopAfterSeconds`; counted from server start. |

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
Radios in SATCOM mode are not affected in phase 1.
