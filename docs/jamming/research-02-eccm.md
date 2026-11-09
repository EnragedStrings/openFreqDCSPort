# 02 - ECCM vs. Communications Jamming: HAVE QUICK, SINCGARS, UHF SATCOM, Link-16, other measures, and a simulator model

Scope: open literature only. Each claim carries a confidence tag:
- **[PUB]** stated in a public manual, standard summary, or textbook.
- **[DER]** derived here from public facts with standard theory.
- **[EST]** estimate for a classified or unpublished parameter. Use it as a tunable default and do not present it as fact.

---

## 0. Corrections to the brief

- **ARC-222** is a VHF radio (30-88 MHz FM with SINCGARS, plus 108-174 MHz AM). It is the F-16's VHF set, not a UHF one. [PUB]
- **ARC-186** is a single-channel VHF AM/FM radio and has no SINCGARS. The airborne SINCGARS radios are the ARC-201 (and ARC-201D/SIP), the ARC-210, and the ARC-222. [PUB] If OpenFreq models ARC-186 FM, keep it single-channel.
- **HAVE QUICK provides no encryption.** It is TRANSEC (anti-jam and anti-intercept by hopping), not COMSEC. It is often combined with KY-58 (VINSON) for voice encryption. [PUB: Wikipedia, Crypto Museum]

---

## 1. HAVE QUICK I / II

### 1.1 What is public

| Item | Value | Tag / source |
|---|---|---|
| Band | 225.000-399.975 MHz UHF AM, 25 kHz raster | PUB |
| Raster channel count | (400-225)/0.025 = **7000** channels | DER (arithmetic). No public source says HQ actually hops over all 7000. |
| Hop seed | WOD + TOD + NET → cryptographic PRNG controls the frequency sequence | PUB (Wikipedia; Army TM 1-1520-238-10) |
| WOD | 6 segments × 6 digits. It programs the "hopping rate and hopping pattern" (and dwell, per Wikipedia). | PUB |
| MWOD | Up to **6 WODs** stored at once, each tagged with a day, so the net rolls over automatically at day change. Entered and erased with the TONE switch in MWOD mode. | PUB (man.fas.org A2C2S tutorial; TM 1-1520-240-10) |
| TOD | Required. Normally from GPS, or sent over the air ("TOD send" / tone). Hopping fails without a valid WOD and TOD. | PUB |
| Net number | "A" plus 3 digits plus a 3-digit suffix (A000.000-A999.975 in HQ II format). The last two digits select the mode or hop table. | PUB (TM 1-1520-238-10/-240-10) |
| HQ II hopsets | Tactical: **NATO hopset (suffix .x25)** and **non-NATO hopset (.x50)**, **1000 nets each**. Training: HQ II training (.x25 range) has 16 nets. Basic HQ I training: A00.000-A00.400 (5 nets). FMT nets: A00.025-A01.525. Entering .x50/.x75 outside a valid range gives a pulsating warning tone. | PUB (TM search summaries; verify against your copy of the TM) |
| FMT | "Frequency Managed Training" nets. These use a **frequency list preloaded into radio memory**, and a training WOD must be entered first. | PUB (TM 1-1510-225-10) |
| Conferencing | In AJ mode the ARC-164 can **receive two simultaneous transmissions** on one net. This is enabled by digits of **WOD segment 2**, not by the net number. The second talker's radio **shifts its frequency by 25 kHz** when it hears the primary frequency in use. **Three simultaneous talkers garble.** Disabled in secure-speech (KY-58) mode. | PUB (TM 1-1520-240-10 p.137) |
| Coordination | ARFA (NATO, Brussels) coordinates HQ channels and hopsets. In peacetime only 4 net numbers may overlap per net-group. | PUB (Wikipedia) |
| Successor | SATURN (STANAG 4372), "Second generation Anti-jam Tactical UHF Radio for NATO". It is digital and much faster hopping, and has replaced HQ across NATO by about 2022. | PUB (Wikipedia) |
| HQ I vs II | HQ II (about 1985+) added more WOD content, MWOD, NATO/non-NATO hopsets, FMT, and conferencing. HQ I uses fewer WOD segments and the same basic principle. The two are not interoperable in AJ mode. | PUB (general); details EST |

### 1.2 What is NOT public (and estimates)

- **Hop rate and dwell time.** Every open source says only "many times per second". STANAG 4246 (HQ I/II) is NATO RESTRICTED. The waveform carries **analog DSB-AM voice on every hop**, so each dwell must be long enough to hide the synthesizer and AGC settling transient. Phase-noise-limited synthesizers of 1980 also settle in roughly 0.1-1 ms. Both point to a "medium" hop rate, not a fast one.
  **[EST] Default 100 hops/s (10 ms dwell). Tunable range 20-500 hops/s.** Figures of "~1000 hops/s" appear on forums, but I found no verifiable source for them. The simulator outcome is fairly insensitive to the exact rate as long as it is above ~10 hops/s (see the intelligibility notes in §6.4). The rate matters mainly for follower jamming (§1.5).
- **Hopset size.** The NATO hopset removes frequencies reserved for civil and other users (e.g. 243.0 guard, ILS glideslope pairs 328.6-335.4, national allocations). **[EST] Tactical hopsets use about 1000-7000 channels. Default N = 2000 for NATO and 4000 for non-NATO.** **FMT and training nets hop over a small manually loaded list. [EST] ≤16 frequencies, so N ≈ 5-16.** Gameplay consequence: a training or FMT net has only about 7-12 dB of processing gain and can be jammed. Real crews know this too: training nets are not combat hopsets.
- **Sync tolerance.** [EST] TOD must agree to within a small fraction of a dwell (~1 ms) for clean audio. OpenFreq already models TOD match, so treat a mismatch as binary.

### 1.3 Processing gain against barrage and partial-band jamming [DER; Torrieri ch. 3; Simon et al. Vol. II ch. 2]

For FH, the processing gain against a jammer that must spread its power over the whole hopset is

`G_p = W_ss / B_ch = N` (in dB: 10·log10 N)

| Net | N | G_p |
|---|---|---|
| HQ, full 225-400 band | 7000 | 38.5 dB |
| HQ NATO hopset (EST) | 2000 | 33.0 dB |
| HQ FMT / training (EST) | 8-16 | 9-12 dB |
| SINCGARS full band | 2320 | 33.7 dB |
| SINCGARS with lockouts (EST typical) | 1000-2000 | 30-33 dB |
| Single-channel | 1 | 0 dB |

Meaning: a jammer that kills a single-channel net at J/S = +X dB needs **+X + G_p dB more total power** to produce the same per-hop J/S over the whole hopset. A 100 W barrage jammer is effectively a 50 mW spot jammer on each of 2000 channels.

**Partial-band (PBJ) logic** (Torrieri §3.2; Houston 1975; Viterbi & Jacobs 1975). A power-limited jammer gets more effect by concentrating its power on a fraction ρ of the hopset, so that the hops it hits are badly damaged:

- Per-channel jamming power at the receiver: `J_ch = J_total / (ρ·N)`, for channels inside the jammed band.
- Fraction of hops hit: `μ = ρ` for a static band and a uniform hopset.
- Worst-case ρ for uncoded noncoherent FSK: `ρ* ≈ 2/(Eb/NJ)`, which gives `Pb ≈ e^-1/(Eb/NJ)`. Pb then falls only inversely with power instead of exponentially, which is why FH data links need FEC and interleaving.
- For **analog AM voice** (HQ) the analogous rule is: put just enough power on each channel to wreck it (per-hop J/S above a "kill" threshold T_kill), and spread over as many channels as the power allows:
  `ρ* = min(1, (J_total/S) / (N · T_kill))` (all quantities linear, J_total/S referenced to the receiver).

### 1.4 Spot jamming an HQ net: what you hear [DER + psychoacoustics]

A spot jammer on k channels of an N-channel hopset hits a fraction `μ = k_inHopset / N` of hops. With N = 2000 and k = 1 that is **0.05% of hops**, about one 10 ms blip every 20 s at 100 hops/s, so it is effectively inaudible. **This is the gameplay payoff: the spot jammer that silences a single-channel UHF frequency does nothing useful against HQ.**

When μ grows (partial-band or wide barrage), each jammed hop yields one of the following, depending on the receiver and squelch behaviour:
- **Noise burst:** an AM detector passes the jammer noise through the audio for that dwell.
- **Mute or dropout:** the radio's squelch or hop-sync logic suppresses the hop.
- **"Tick" or "zipper" texture:** a fast series of short gaps or bursts.

Intelligibility of randomly interrupted speech follows **Miller & Licklider (1950)**, "The Intelligibility of Interrupted Speech", JASA 22, 167. At interruption rates above about 10/s, with **50% of the speech removed, word intelligibility stays high (~80-90%)**. Intelligibility collapses toward the 2-4 Hz trough or as the on-fraction drops toward 25%. Noise-filled gaps sound more continuous because of phonemic restoration (Warren 1970), but they are more fatiguing. Hop interruption at ≥50 hops/s is in the "fast" regime. The resulting voice-quality map is in §6.4.

Rule-of-thumb cross-check (Adamy, *EW 101/102*): a digital link is reliably jammed with **J/S ≈ 0 dB on about 1/3 of the time or bits**. Analog voice is more forgiving: it needs a higher μ to deny the net, but it degrades earlier in perceived quality.

### 1.5 Follower (repeater / responsive) jamming [DER; Torrieri §3.6; Poisel *Modern Communications Jamming* ch. 9]

The follower must detect the new hop, measure its frequency, tune, and radiate before the dwell ends. Geometry adds the excess path delay:

```
T_delay = T_detect + T_tune + (d_TJ + d_JR - d_TR) / c
fraction of dwell jammed  f_hit = max(0, 1 - T_delay / T_dwell)
jammer must also have per-hop J/S > T_kill at the receiver
```

- At **100 hops/s** (T_dwell = 10 ms), even 1 ms of processing leaves 9 ms, which is **2700 km** of excess path. A modern digital-receiver follower is therefore **geometrically and technically feasible against HQ at any plausible rate up to a few hundred hops/s**. The practical limits are discriminating the target net among many emitters, and the fact that the jammer starts T_delay late on each hop.
- **SINCGARS** (~100 hops/s) is in the same regime. This is the well-known open critique of slow hoppers.
- **Link-16** (13 µs pulse interval, 6.4 µs pulses) leaves an excess-path budget below about 2 km even with zero processing time. **Follower jamming is impractical.**

**[EST] Simulator stance:** treat the follower as an advanced Red capability. Set T_detect + T_tune = 1-3 ms for a "modern" system and 5-20 ms for an "old" one. Each hop is jammed for the fraction f_hit. The net outcome is "partially jammed audio", not "killed". The only defenses are higher hop rate (not available on HQ) or reducing J/S (geometry, power, terrain).

### 1.6 Barrage over the whole band

Every channel, hopping or not, gets `J_ch = J_total · B_ch / W_J`. HQ gains nothing relative to a single-channel net hit by the same barrage, apart from the G_p dilution already contained in J_ch. A barrage strong enough to exceed T_kill everywhere kills everybody, but it needs about 33-38 dB more power than a spot jammer, or a much closer standoff (burn-through, §5). Only power, geometry, and moving off the band defeat it.

---

## 2. SINCGARS (ARC-201, ARC-210, ARC-222)

| Item | Value | Tag / source |
|---|---|---|
| Band / raster | 30.000-87.975 MHz, 25 kHz → **2320 channels** | PUB (TM 11-5820-890 series; Wikipedia) |
| Hop rate | **~100 hops/s** ("over 100 frequencies per second") | PUB (Army TM) |
| Dwell | ~10 ms. Part of each dwell carries sync/overhead. | DER/EST |
| Voice | Cipher text (ICOM/VINSON-compatible): **CVSD 16 kbps**. Plain-text FH voice also exists. | PUB |
| Data | Up to 16 kbps. With FEC: 4.8 kbps (ICOM), 9.6 kbps (SIP). | PUB |
| Hopset definition | Fill (loadset) = TRANSEC key + hopset + **lockout sets** (bands excluded from hopping) + Net ID + SYNC TIME | PUB (TM; FM 11-32 era) |
| Sync | Net members' clocks must agree within a few seconds. Late-net-entry via cue / passive late entry. | PUB (EST on exact window: ±4 s commonly cited) |

**Behaviour under PBJ [DER]:**
- **CVSD voice** has no strong FEC. A jammed hop corrupts about 160 bits (10 ms at 16 kbps). CVSD is tolerant of bit errors (intelligible at ~1% BER, poor beyond ~5-10%), and a hop error is a dense 10 ms burst. **Use the same interrupted-speech mapping as HQ**, with "garble/hiss" as the burst sound instead of AM noise. Speech under CVSD at moderate hit rates sounds "bubbly".
- **COMSEC sync:** VINSON-type crypto resynchronizes on the preamble of each transmission. Dense hits at the start of a key-up can cause a missed preamble, which loses the entire transmission. [EST] Model P(lose whole over) = μ for the first ~100-200 ms of preamble.
- **FEC data** (interleaved across hops) survives μ up to roughly 10-25% with near-zero residual errors, then falls off a cliff. [EST]
- **Lockouts** shrink the hopset and therefore G_p. They can also be used defensively, to lock out a known jammed sub-band, which restores per-hop SINR at the cost of 10·log10 of the remaining fraction. **This is a nice player-ECCM action:** "lock out 40-45 MHz" gives μ → 0 for a static partial-band jammer.

---

## 3. UHF SATCOM (5/25 kHz, DAMA and non-DAMA; MUOS)

**Public facts:**
- FLTSATCOM/UFO channels: UFO has **17 × 25 kHz + 21 × 5 kHz** channels, in roughly the 240-320 MHz band (APL Technical Digest, Gunduzhan). [PUB]
- The commonly cited split is uplink ~292-318 MHz and downlink ~243-270 MHz. [PUB in channel plans; not re-verified here]
- Waveforms:
  - **MIL-STD-188-181** (dedicated / non-DAMA, 5 and 25 kHz)
  - **-182** (5 kHz DAMA)
  - **-183** (25 kHz TDMA DAMA, and later the Integrated Waveform)
  [PUB]
- The transponders are **non-regenerative bent pipes**. UHF channels are usually hard-limited.

**Why UHF SATCOM is fragile [DER; MITRE 10-2505; Jones 1963 limiter theory]:**
1. **Uplink jamming hits every user on the channel, anywhere in the footprint.** An earth-coverage antenna hears a jammer anywhere on roughly a third of the Earth. A bent pipe retransmits whatever arrives. A **hard limiter shares fixed downlink power in proportion to input powers ("power robbing")**, and a strong signal additionally suppresses a weak one by up to ~6 dB (≈1 dB at moderate ratios):
   `P_user_down ≈ P_sat · S_up / (S_up + J_up + N_up)`, minus up to 6 dB of small-signal suppression when J_up ≫ S_up.
   Unauthorized users (the "Brazilian pirates" on FLTSAT/UFO; HamSCI 2026 paper) show that a cheap ground transmitter can occupy a military UHF channel. [PUB]
2. **No processing gain.** The legacy waveforms are narrowband FM/PSK, so G_p = 0 dB.
3. **Downlink jamming** must be local to the victim, because the satellite downlink is weak. A ground jammer near the receiver wins easily against a ground terminal. An aircraft with a top-mounted antenna gets some fuselage shielding from ground jammers. [EST] Apply 10-15 dB extra jammer path loss when the jammer is below the aircraft's horizon plane.
4. **DAMA:** jamming the **control/orderwire channel or the TDMA frame sync** denies channel assignment for the whole DAMA network on that channel. That is a single point of failure. [DER]

**MUOS:** WCDMA (3GPP-based). 5 MHz carriers at 3.84 Mcps give ~26 dB despreading against narrowband jammers for 9.6 kbps services [DER]. It is not described as protected/AJ SATCOM. MUOS also carries a legacy UHF payload that is just as vulnerable as UFO.

**Simulator model:** a SATCOM channel is a single shared resource. Compute uplink `SINR_up` once per channel per tick, using jammer ERP and the sat-to-jammer slant range against each user's uplink. The worst-case downlink to every listener is then degraded by the power-robbing term above. An effective uplink jammer (J_up > S_up + 3 dB) kills that channel for all players worldwide in the footprint. DAMA: if the orderwire channel is jammed, no new assignments can be made, and existing circuits drop at the next frame resync. [EST] Model this as a drop within 1-2 frames (seconds).

---

## 4. Link-16 / MIDS (brief)

- **Band and hopping:** 960-1215 MHz. **51 frequencies, 969-1206 MHz, 3 MHz spacing.** Notches around 1030/1090 MHz (IFF/TACAN protection). [PUB]
- **Pulse timing:** pulses are 6.4 µs (plus 6.6 µs dead time) in a 13 µs interval. The frequency changes every pulse: **~76,923 hops/s**. [PUB]
- **Coding:** 32-chip CCSK at 5 Mchip/s MSK, **RS(31,15)** coding (corrects 8 symbol errors or 16 erasures), interleaving, and an optional double-pulse format. Time-slot TDMA uses 7.8125 ms slots. [PUB: NPS theses ADA488647/ADA461555; sigidwiki]
- **Jam resistance:**
  - The combination of coding and interleaving means messages survive until a large fraction of pulses is jammed. [EST] There is a cliff at about 30-50% of pulses hit (double-pulse mode tolerates more).
  - Follower jamming is impractical (see §1.5).
  - An effective barrage must cover the full ~240 MHz band, which puts the ~49 dB band/symbol ratio against the jammer, and must also respect IFF notches.
  - Effective jamming realistically needs proximity (short range) or high-power barrage.
- **Simulator:** compute `μ = fraction of the 51 frequencies with J/S > T (≈0 dB)`. Then:
  - Message success = 1 if μ < 0.3.
  - Linear falloff to 0 at μ = 0.6 (single pulse) or 0.7 (double pulse).
  - Outputs are track update rate and voice (if Link-16 voice is modeled; 2.4/16 kbps).
  [EST]

---

## 5. Other ECCM that pilots and nets actually use (and how to model them)

| ECCM | Real practice | Model hook |
|---|---|---|
| **Burn-through / power** | Raise transmit power, close range. In free space, J/S scales as (ERP_J/ERP_S)·(d_S/d_J)². | `d_burnthrough = d_J · sqrt(ERP_S / (ERP_J · T_kill))`. Show players "you will burn through inside X nm". |
| **Alternate / pre-planned frequencies** | Comm cards list a primary and an alternate/"Fox" frequency. Brevity "**CHATTERMARK**" means switch to the briefed jam plan. (Brevity: MULTISERVICE BREVITY CODES, ATP 1-02.1.) | A single-channel jammer must re-find the net (search time). Model a jammer "reacquire" delay of seconds to tens of seconds, driven by look-through and scan. |
| **Hopping (HQ/SINCGARS)** | WOD/TOD/NET. | §6 |
| **Reduced range / terrain masking** | Fly low behind terrain from the jammer, keep the jammer off LOS. | Use the existing propagation and LOS for the jammer path as well (the same model for J as for S). |
| **Relay** | AWACS or another aircraft relays a weak link. | Shorter d_S per hop. |
| **Directional antennas** | Ground and ship terminals, SATCOM dishes. Rare on fighters (blade antennas, omni). | Antenna gain toward J vs S. Usually 0 dB for aircraft. Nulling antennas belong to protected SATCOM, not UHF. |
| **EMCON** | Minimize transmissions to deny DF and cueing of follower or spot jammers. | Jammer cueing needs detected emissions. Fewer transmissions mean slower jammer reacquisition. |
| **Authentication / brevity vs imitative deception** | Challenge-reply (authentication tables), code words. COMSEC (KY-58) makes intruder voice impossible on a cipher net. HQ alone does **not** authenticate, but an intruder needs WOD + TOD + NET to talk on the net. | Plain single-channel nets: an enemy player can transmit. HQ nets: only with the matching triplet. KY-58 nets: only with the matching key. |
| **Look-through / jammer awareness** | Operators report "MUSIC" (brevity for radar jamming); communications jamming is reported, then the net goes to the jam plan. | UX cue only. |
| **HF ALE** (MIL-STD-188-141B/C; 3G ALE; 188-110 modems with long interleavers) | ALE scans a channel list, uses link-quality analysis to pick the best channel, and re-links automatically around jamming. | Treat each ALE channel as single-channel. The link picks the best SINR channel after a re-link delay (~10-30 s for 2G). |

---

## 6. Recommended simulator model

### 6.1 Entities

**Jammer**
- `f_c` (MHz), `W_J` (MHz, 0.025 for spot)
- `technique` ∈ {spot, barrage, partial_band (set of sub-bands), swept, follower, pulse, deceptive}
- `P_J` (W), antenna gain, position
- swept: `sweep_rate` (MHz/s)
- follower: `T_react` (s)
- pulse: `duty` (fraction), `PRF` (Hz)
- reacquire/scan time (for chasing single-channel nets)

**Net**
- `mode` ∈ {single, HQ, SINCGARS, SATCOM, Link16}
- hopset: list of channels, or band + exclusions; N
- `R_h` (hops/s)
- `B_ch` = 25 kHz (5 kHz for SATCOM 5k)
- modulation ∈ {AM, FM, CVSD-digital, data-FEC}
- COMSEC flag

### 6.2 Per-channel jamming power at receiver r

```
J_r(ch) = P_J · G_J · G_r / L_path(J→r) · Overlap(ch)        // same propagation model as signals
Overlap(ch) = min(B_ch, |[f_ch±B_ch/2] ∩ [f_c±W_J/2]|) / W_J  // barrage/partial-band dilution
S_r = signal power from the talker (existing model)
SINR_hop(ch) = S_r / (N_r + J_r(ch))
```

Additional notes:
- **Spot:** W_J = B_ch, so Overlap = 1 on the target channel and 0 elsewhere. Add a receiver adjacent-channel rejection term (EST −60 dB) if desired.
- **Swept:** time-varying f_c(t). A channel is hit while it lies within the jammer's instantaneous bandwidth. The single-channel duty is `B_ch / W_sweep` per sweep period, which gives periodic noise bursts. Hopping nets see `μ ≈ W_inst / W_ss` plus a dwell-overlap term.
- **Pulse:** multiply J by `1/duty` (peak power) during the on-fraction, then apply time-gating.
- **Follower:** each hop is hit for `f_hit = max(0, 1 − T_delay·R_h)`, where T_delay includes excess path/c.

### 6.3 Fraction of hops jammed

```
μ = (1/N) · Σ_{ch∈hopset} H(ch),  H(ch) = 1 if J_r(ch)/S_r ≥ T_kill(mod) else partial (see 6.4)
```

For performance, there is no need to iterate 7000 channels per tick. Compute the hopset's overlap with each jammer band analytically: `μ = |hopset ∩ jammed band| / N`, with a single per-channel J for that jammer. For single-channel nets, μ ∈ {0, 1}.

**HQ net-specific:** if the net is using an FMT/training list (small N), μ jumps in large steps. One spot jammer on one of 8 frequencies gives μ = 12.5%.

### 6.4 From per-hop SINR to voice

Per-hop SINR thresholds, as an analog AM/FM voice "articulation" mapping. All values [EST], tuned to typical AI/STI curves and Adamy rules of thumb.

| Per-hop SINR | AM voice hop outcome | FM (capture) | CVSD digital |
|---|---|---|---|
| > 15 dB | clean | clean | clean |
| 6-15 dB | audible hiss/whine under voice | clean (capture) | clean |
| 0-6 dB | heavy noise, partly intelligible | sudden capture loss: noise/jammer audio | BER rising: "bubbly" |
| < 0 dB (`T_kill` ≈ 0 dB AM, +3 dB FM, ~0 dB digital) | jammer dominates that dwell: noise burst / tone | jammer captures | hop lost (burst garble or mute) |

Aggregate intelligibility (word-level) for hopping voice with a random fraction μ of hops killed. This uses the Miller & Licklider shape for interruption rates ≥ 10/s, with linear interpolation and the [EST] values below:

| μ (hops lost) | 0 | 0.1 | 0.25 | 0.5 | 0.75 | 0.9 |
|---|---|---|---|---|---|---|
| word intelligibility | 1.0 | 0.97 | 0.92 | 0.80 | 0.45 | 0.15 |
| audible character | normal | occasional ticks | "zipper"/static texture | heavily chopped, readable | mostly noise, fragments | denied |

Implementation for audio:
1. Run the audio in hop-sized blocks (block = 1/R_h, e.g. 10 ms).
2. For each block, draw `hit ~ Bernoulli(μ_eff)`, or deterministically from a PRNG seeded by WOD/TOD/NET, so all listeners hear consistent hits.
3. On a hit, either replace the block with jammer audio (noise or tone at level by SINR) or **mute it**, depending on the radio's squelch. [EST] Use AM HQ → noise burst; SINCGARS CT → mute/garble.
4. Crossfade 1-2 ms at block edges to avoid artificial clicks. Real HQ hop transitions are believed to be muted briefly; a faint tick is optional.
5. Partially jammed hops (0 < SINR < 15 dB) mix noise at the computed SNR instead of the full burst.

For single-channel AM under the same jammer, μ = 1. Voice gets continuous noise at the computed SINR, which matches the existing static/noise model with an added J term.

### 6.5 Mode-specific outcome rules

- **Single-channel UHF/VHF AM:** continuous SINR. Spot jamming kills it when J/S ≥ T_kill. The player ECCM is to change frequency; the jammer then needs reacquire time.
- **HQ I/II:** μ from §6.3 with the HQ hopset. Follower → f_hit per hop. Conferencing: the second talker is offset by 25 kHz, which is irrelevant to jamming. KY-58 over HQ: apply the CVSD/COMSEC preamble-loss rule from §2.
- **SINCGARS:** same as HQ with N = 2320 minus lockouts. The player may set a lockout band, which removes those channels from the hopset (μ ↓, N ↓).
- **SATCOM:** shared uplink SINR with power robbing (§3). No processing gain. One jammer can affect every user on that channel. DAMA orderwire jamming → assignment loss.
- **Link-16:** μ over 51 frequencies → message success cliff (§4).
- **HF ALE:** best-channel re-link after a delay.

### 6.6 Default parameter table (EST unless noted)

| Parameter | Default | Range | Status |
|---|---|---|---|
| HQ hop rate | 100 hops/s | 20-500 | EST (classified) |
| HQ NATO hopset N | 2000 | 1000-7000 | EST |
| HQ non-NATO hopset N | 4000 | 1000-7000 | EST |
| HQ FMT/training N | 8 | 5-16 | EST |
| SINCGARS hop rate | 100 hops/s | — | PUB (~100) |
| SINCGARS N | 2320 − lockouts | — | PUB |
| Link-16 | 51 freq, 76,923 pulses/s | — | PUB |
| T_kill AM | 0 dB | −3...+3 | EST (Adamy-style) |
| T_kill FM | +3 dB | 0...+6 | EST |
| Follower reaction | 2 ms modern / 10 ms legacy | 0.5-20 ms | EST |
| Jammer reacquire (single-channel net after QSY) | 10 s | 2-60 s | EST |
| Limiter small-signal suppression | 1-6 dB | — | PUB theory (Jones 1963) |
| Aircraft below-horizon shielding for SATCOM downlink jam | 10 dB | 5-15 | EST |

### 6.7 Gameplay sanity checks the model should reproduce

1. **Spot jammer, 50 W, 50 nm from a single-channel UHF net with S at 100 nm:** net denied. **Same jammer against HQ (N = 2000):** μ = 0.05%, unaffected. ✔
2. **Barrage 225-400 MHz, 1 kW:** J per 25 kHz channel ≈ 0.14 W, about 38 dB weaker than a 1 kW spot. It degrades everyone equally at long range and kills everyone inside the burn-through range. HQ gets no advantage. ✔
3. **Partial-band on 10% of the band at the same total power:** HQ μ ≈ 0.1, which gives occasional ticks. Single-channel nets inside the band are killed and those outside are fine. ✔
4. **Follower vs HQ at 100 hops/s, T_react 2 ms:** about 80% of each dwell jammed (if J/S is sufficient), so the net is heavily degraded. That is a realistic "HQ is not invulnerable" outcome. ✔
5. **One ground jammer on the UFO uplink:** every SATCOM user on that channel is degraded, worldwide in the footprint. ✔

---

## Sources

- Have Quick (Wikipedia): https://en.wikipedia.org/wiki/Have_Quick
- Crypto Museum, HAVE QUICK: https://cryptomuseum.com/radio/havequick
- TM 1-1520-238-10 (AH-64), HQ net numbers/FMT: https://apachehelicopter.tpub.com/TM-1-1520-238-10/css/TM-1-1520-238-10_170.htm
- TM 1-1520-240-10 (CH-47), HQ net types, conferencing, MWOD: https://ch-47helicopters.tpub.com/TM-1-1520-240-10/css/TM-1-1520-240-10_136.htm and https://ch-47helicopters.tpub.com/TM-1-1520-240-10/css/TM-1-1520-240-10_137.htm
- TM 1-1510-225-10, FMT list loading: https://aviationandaccessories.tpub.com/TM-1-1510-225-10/css/TM-1-1510-225-10_399.htm
- A2C2S tutorial (ARC-164, WOD/MWOD): https://man.fas.org/dod-101/sys/ac/equip/docs/A2C2S_Tutorial_RevA1a/tsld024.htm
- SINCGARS (Wikipedia): https://en.wikipedia.org/wiki/SINCGARS ; Army lesson plan: https://irp.fas.org/doddir/army/miobc/lp-sincg.htm ; FM 11-43: https://www.globalsecurity.org/military/library/policy/army/fm/11-43/c4b.htm ; Euro-SD critique of slow hopping: https://euro-sd.com/2024/06/articles/38296/breaking-and-entering/
- UFO channelization, APL Tech Digest (Gunduzhan): https://www.jhuapl.edu/sites/default/files/2024-09/33-01-Gunduzhan.pdf ; UFO channel groups: https://electronicstechnician.tpub.com/14088/css/Uhf-Follow-On-Satellite-76.htm
- Unauthorized use of UHF military satellites (HamSCI 2026): https://hamsci.org/bibcite/reference/584
- MITRE bent-pipe AJ analysis: https://www.mitre.org/sites/default/files/pdf/10_2505.pdf
- MUOS legacy/WCDMA test: https://www.globalsecurity.org/space/library/news/2009/space-091019-lockheed-martin01.htm
- Link-16: https://sigidwiki.com/wiki/Link_16 ; NPS theses https://apps.dtic.mil/sti/pdfs/ADA488647.pdf , https://apps.dtic.mil/sti/pdfs/ADA461555.pdf ; https://liotier.github.io/Ersatz-MIL-STD-6016/link16-reference.html
- Miller & Licklider (1950), "The Intelligibility of Interrupted Speech", JASA 22:167: https://typeset.io/pdf/the-intelligibility-of-interrupted-speech-2odkpjsawv.pdf
- Textbooks, used for the theory and not re-fetched here:
  - Torrieri, *Principles of Spread-Spectrum Communication Systems* (Springer), FH, partial-band and follower jamming chapters
  - Simon, Omura, Scholtz & Levitt, *Spread Spectrum Communications Handbook*, Vol. II (FH in PBJ)
  - Poisel, *Modern Communications Jamming Principles and Techniques*
  - Adamy, *EW 101/102* (J/S rules of thumb, burn-through)
  - Jones (1963), hard-limiter signal suppression
  - Warren (1970), phonemic restoration

**Classified or thin areas, flagged:**
- HQ hop rate, dwell, hopset sizes, sync tolerance (STANAG 4246 is restricted)
- SINCGARS exact sync window and FEC details
- Link-16 jamming margins
- Exact receiver squelch behaviour on jammed hops

All of these are marked [EST] above.
