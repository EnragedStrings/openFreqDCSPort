# HAVE QUICK and SINCGARS in OpenFreq

Frequency hopping makes a net hard to jam: a spot jammer that silences a single-channel frequency only hits the
odd hop of a HAVE QUICK net. OpenFreq simulates HAVE QUICK I/II (UHF AM, 225–400 MHz) and SINCGARS
(VHF FM, 30–88 MHz) end to end:

- WOD, MWOD, net numbers and TOD
- the TRANSEC match between radios
- the hopsets and how jammers hit them
- what the A-10C II cockpit exports, and manual entry for every other radio

The research behind each choice is in `research-02-eccm.md` §1, §2 and §6, and in `research-05-dcs-hq-export.md`.
The overall design is in `JAMMING_DESIGN.md`.

HAVE QUICK is TRANSEC, not encryption. KY-58 (COMSEC) works on top of it as before.

## How it works

### Who hears whom (the TRANSEC gate)

Two hopping radios hear each other only when all of these hold:

| Must match | HAVE QUICK | SINCGARS |
|---|---|---|
| Mode | HQ | SG |
| Net | A00.000–A99.975 | e.g. F001 |
| Fill | WOD for the operating day | TRANSEC key and lockouts |
| Time | both have a TOD; clocks within **±50 ms** [EST] | both have sync time; clocks within **±4 s** |

Anything that doesn't match hops on a different sequence and is **not heard**. The fill is compared by a one-way
fingerprint, and the TOD offsets are compared against the receiver's sync window.

- A hopping radio and a single-channel radio never hear each other. A hopper lands on a fixed frequency on about
  1/N of its hops, which is a 10 ms blip every ~20 s on a combat hopset, so this isn't modelled.
- Older clients and SRS only send the plain "HAVE QUICK on" flag. Radios that have only that flag still hear each
  other, as before. A radio with a full net identity doesn't match a flag-only radio.

### What goes on the wire

Each transmission carries an `eccm` object:

- `mode`
- `net`
- `key`: a 16-hex-digit fingerprint of the WOD for the day, or of the SINCGARS key and lockouts
- `todOk`
- `tod`: the radio's TOD offset in ms

**The WOD and the TRANSEC key never leave the machine they were entered on.** The server's comm plan is also sent
as fingerprints only.

### The net's channel

OpenFreq routes audio by frequency, so every radio on a net must meet on one channel:

- **HAVE QUICK:** net A12.325 uses 225 MHz + 12325/25 × 25 kHz = **237.325 MHz**.
- **SINCGARS:** the net name is hashed onto a 30–88 MHz channel.

When a radio goes into HQ or SG, its card retunes to that channel by itself. The gate keeps single-channel users of
the same frequency apart from the net. Jamming uses the net's hopset, not this channel.

### Hopsets [EST unless noted]

| Net | Hopset | N |
|---|---|---|
| HQ .x25 (NATO) | spread evenly over 225–400 MHz, excluding guard 243.0 ±0.1 and ILS 328.6–335.4 | 2000 |
| HQ .x50 (non-NATO) | same rule, different channels | 4000 |
| HQ .x00 (HQ I) | NATO hopset | 2000 |
| HQ A00.000–A00.400 (training), A00.025–A01.525 (FMT) | the comm plan's FMT list (default: 8 frequencies) | 5–16 |
| HQ .x75 outside FMT | invalid (`INVALID NET ID`) | — |
| SINCGARS | 30.000–87.975 MHz less the lockout bands (PUB: 2320 channels) | 2320 − lockouts |

- Hop rate: 100 hops/s [EST for HQ; ~100 PUB for SINCGARS], configurable.
- Hop sequence: a deterministic hash of (mode, net, fill fingerprint, hop index).
- Hop clock: net time (UTC) plus the radio's TOD offset. Every listener on a net therefore derives the same hop at
  the same moment.

### Jamming against hopping (μ and the follower)

For each hopping radio and each jammer, OpenFreq computes the hit fraction from the radio's actual hopset (FMT
list, lockouts):

```
μ = channels of the hopset the jammer's emissions overlap / N
per-hop J/N = the jammer's power in one 25 kHz channel it covers   (a spot: all of it; a barrage: B_ch / B_J)
```

| Jammer | HAVE QUICK NATO hopset | Single channel inside the band |
|---|---|---|
| Spot on one hopset channel | μ = 1/2000 = 0.05 % (inaudible; no badge) | killed |
| Spot off the hopset | nothing | (if on its frequency) killed |
| Partial-band, 10 % of 225–400 | μ ≈ 0.1: occasional ticks | killed |
| Barrage 225–400 | μ = 1, but ~38 dB weaker per channel | same |
| FMT net, 1 of 8 frequencies jammed | μ = 12.5 % | — |
| SINCGARS, 40–45 MHz jammed | μ ≈ 8.6 %; with a 40–45 MHz lockout, 0 | — |

**Follower.** A *responsive* jammer whose band reaches the hopset is treated as a follower. It re-tunes its whole
emission onto each hop it detects, `T_delay` late:

```
f_hit   = max(0, 1 − T_delay · R_h)
T_delay = reactionMs + 2 · d(jammer, receiver) / c
```

The transmitter's position isn't known to the client, so the extra path assumes the talker is next to the
receiver.

- Every hop is jammed for its last `f_hit` of the dwell.
- With 2 ms reaction at 100 hops/s, about 80 % of each dwell is jammed, so the net is heavily degraded.

**What you hear.** The hit hops carry the jammer's waveform (noise bursts on AM HQ), with 1.5 ms ramps. The hits
are drawn from the net's shared hop clock, so every listener on the net hears the same hits.

**Channel-card badge:**

| Hits | Badge |
|---|---|
| under 1 % of hops | none |
| 1–50 % of hops | at most INTERFERENCE (chopped but readable, Miller–Licklider) |
| from 50 % of hops | JAMMED, if the per-hop J/S kills |

## Comm plan (server)

The `eccm` section of `OpenFreq.Server.json` is the mission's fill. Clients receive it, as fingerprints, right
after they connect.

```json
{
  "eccm": {
    "missionDate": "2026-11-09",
    "hopRateHz": 100,
    "haveQuickSyncWindowMs": 50,
    "sincgarsSyncWindowMs": 4000,
    "natoChannels": 2000,
    "nonNatoChannels": 4000,
    "mwod": [
      { "date": "2026-11-09", "wod": "300.050 251.000 277.775 312.600 225.025 399.975", "label": "D1" },
      { "date": "2026-11-10", "segments": [ "300100", "251000", "277775", "312600", "225025", "399975" ] }
    ],
    "fmtFrequenciesKhz": [ 235000, 252500, 267750, 281250, 296000, 311500, 347250, 364750 ],
    "sincgars": {
      "transecKey": "BLUE-TEK-1",
      "lockouts": [ { "startKhz": 40000, "endKhz": 45000 } ]
    }
  }
}
```

Every field is optional:

| Field | Meaning |
|---|---|
| `enabled` | default `true` |
| `missionDate` | The HAVE QUICK operating day, which selects the MWOD entry. **Default: the server's UTC date.** DCS's export environment has no mission date, and reading one would need a DCS Hooks script (future). |
| `mwod` | Up to 6 dated WODs. Each WOD is 6 segments of 6 digits (`ddd.ddd` or `dddddd`). Invalid entries are logged and dropped. |
| `fmtFrequenciesKhz` | Training and FMT nets hop over this list. |
| `sincgars.transecKey`, `sincgars.lockouts` | Without a key, SINCGARS uses a built-in default key, so SINCGARS nets work out of the box. They are still separated by net and lockouts. |
| `hopRateHz`, `*SyncWindowMs`, `*Channels` | The estimates above. |

There is one plan for everybody. Per-coalition plans are future work.

## Per-aircraft support

| Aircraft / radio | Source | Mode and net | Fill (WOD) | TOD |
|---|---|---|---|---|
| **A-10C II ARC-210** | Cockpit: ECCM page (`list_indication(18)`) | `active_eccm_channel_type` HQI/HQII/SG, `Net_ID_left_part`.`Net_ID_right_part`, `SG_data` | Comm plan MWOD for the day. If OpenFreq has no WOD but the cockpit says ready, a shared default. | The cockpit's `NO TIME`; the other readiness lines are passed through (`NO WOD`, `NO MWOD`, `NO OPER DAY`, `INVALID NET ID`). |
| **A-10C / A-10C II ARC-164** | "Virtual HQ": 100 MHz knob (arg 162) on **A** in MNL | A + digit knobs, e.g. A12.325 | comm plan / manual | assumed GPS-timed (manual toggle) |
| F-16C, F-15E, AH-64D, CH-47F, AV-8B, F/A-18C, F-14, others | not simulated by DCS | manual (AJ flyout on the card) | comm plan / manual | manual |
| GCI / stationary / BMS cards | manual | manual | comm plan / manual | manual |

**Not verified in DCS yet:**

- The ARC-210 ECCM field values. They were read from the page Lua of an install with 163rd vFS modifications, not
  a stock install.
- The arg 162 "A" index. OpenFreqDCSConfig.lua `a10c2.arc164VirtualHq.aIndex` defaults to 2, and every raw value is
  logged as `ARC164 100MHz knob`.
- Whether the ECCM page fields persist while other pages are shown. The export keeps the last ECCM state through
  menu pages and drops it on a frequency page.

`OpenFreqDCS.log` logs `ECCM: ARC210 …` on every change. The offline check is `lua DCS/tests/eccm_harness.lua
DCS/OpenFreqDCS/Scripts/OpenFreqDCS.lua`, which a client test runs when `lua` is on PATH.

**Fixed:**

- The export used to read the ARC-210 KY submode `CT-TD` as HAVE QUICK (copied from DCS-SRS). CT-TD is the KY-58
  time-delay mode. It is now just "encrypted", and HAVE QUICK comes only from the ECCM page.

## Player procedures

1. **Load the fill.** With a comm plan, there is nothing to do: the MWOD for the mission date is loaded. Without
   one, open the card's **AJ** flyout and type the briefed WOD (6 segments) or SINCGARS key.
   - The ARC-210 in the A-10C II loads its own WOD in the cockpit. OpenFreq still needs the same WOD from the comm
     plan or the flyout for the net to match other aircraft.
2. **TOD.** GPS-timed radios are synced (**TOD** checked).
   - After an emergency TOD start, enter the clock's offset in seconds. Radios whose clocks differ by more than the
     sync window can't talk.
   - **TOD RCV** puts the clock back on net time.
   - In the A-10C II, `NO TIME` on the ECCM page means the radio can't hop until it gets GPS time or receives TOD.
3. **Select the net.**
   - ARC-210: select an ECCM channel.
   - ARC-164: set the 100 MHz knob to A and dial the net, e.g. A12.325.
   - Other radios: pick HAVE QUICK or SINCGARS in the flyout and type the net.

   The card shows `HQ A12.325`, the hopset (`NATO N=2000`) and the fill source. Anything that stops the radio
   hopping is shown in red: `NO WOD`, `NO MWOD`, `NO TIME`, `INVALID NET ID`.
4. **Under jamming.**
   - Combat hopsets shrug off spot jammers.
   - Training and FMT nets don't.
   - For SINCGARS, lock out a jammed sub-band in the comm plan.
   - A follower can only be beaten by range or terrain.

## Estimates and simplifications

- **Hopset tables.** HQ hopset contents and sizes are not public. OpenFreq spreads the channels evenly, so μ is
  proportional to the jammed width.
- **Hop rate and sync window.** The 100 hops/s rate and the 50 ms HQ window are estimates. SINCGARS ±4 s is the
  commonly cited figure.
- **Jamming draw.** Hits are drawn per hop with probability μ, deterministically from the net's hop clock. The
  hit/miss statistics match the hopset exactly, but individual hops are not tied to individual channel numbers.
- **Not modelled:** WOD segment semantics, HQ conferencing, the difference between HQ I and HQ II in AJ mode, and
  per-coalition plans.
- **KY-58 over HQ.** Losing a transmission whose preamble is hit (research-02 §2) is **future work**. Today the
  hit hops only add jammer audio.
- **Net time.** Net time is the UTC wall clock of each client. Clients whose clocks differ by tens of ms hear the
  same hits slightly shifted.
