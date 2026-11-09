# Communications Jamming Physics for OpenFreq: Research Brief 01

Scope: VHF/UHF AM air band (118-137 MHz civil, 225-400 MHz military UHF AM) and VHF-FM (30-88 MHz). The target receiver has per-sample I/Q mixing, Gaussian noise, AGC, carrier-power squelch, a 300-3000 Hz audio band-pass, and free-space, two-ray and terrain path loss.

Confidence tags used below:
- **[S]** sourced (citation given).
- **[D]** derived here from first principles (the derivation is shown).
- **[E]** engineering estimate. It is defensible but not from a primary public source. Data in this area is thin or classified.

---

## 0. Key insight for the implementation (read first)

The receiver chain is already a per-sample complex-baseband simulation. So **the most realistic jamming model injects the jammer as one more complex-baseband waveform** into the same I/Q sum as the wanted signal and thermal noise, before AGC, squelch and the detector.

If it is injected there, almost every textbook effect *emerges by itself* and needs no hand-coded effect. These include the AM heterodyne whistle, AGC desensitization, AM "two voices plus squeal", FM capture, FM threshold clicks, squelch held open, and swept-tone "bagpipes".

What the simulator must still compute analytically (power budget, Section 6):
1. The jammer power arriving at the receiver: J, in dBm.
2. How much of it falls in the receiver IF passband, and at what frequency offset.
3. Out-of-channel effects that the narrow baseband simulation cannot represent: blocking/desense, reciprocal mixing, intermodulation and adjacent-channel leakage. These are applied as an extra in-band noise term or a gain reduction.
4. A scalar intelligibility estimate, if game logic (AI wingmen, ATC, scoring) needs to "know" whether a message got through.

---

## 1. The communications jamming equation

### 1.1 Three-node geometry: comm jamming attacks the *receiver*

A comm link has a transmitter T, a receiver R and a jammer J. Jamming is achieved **at R**. The jammer's distance to T does not matter. Its distance to R relative to the T→R distance does matter [S: Adamy, *EW 101*, ch. 9 "Communications Jamming"; USAFA ECE-495 EW notes, https://usafa-ece.github.io/ece-495-ew/block02/L17_ElectromagneticAttack_Reading.html; JED "Jamming efficacy of communications links", https://www.jedonline.com/2022/08/17/jamming-efficacy-of-communications-links/].

Consequences:
- **Asymmetry.** On a two-way net, each end has its own J/S. Example: a ground jammer near a SAM site jams the *aircraft* receivers overflying it effectively, while the distant AWACS/ATC receivers barely notice. Pilot A may be unable to hear ATC while ATC hears pilot A perfectly. Each (receiver, transmitter, jammer) triple needs its own computation.
- **Up-link vs down-link.** To jam aircraft-to-ground traffic, the jammer must reach the *ground station* receiver. It is often far away and behind terrain, so this is hard. To jam ground-to-air traffic, the jammer must reach the *aircraft*. Aircraft are at altitude and in line-of-sight (LOS) of everything, so this is easy. This is why airborne comm-jamming targets are typically aircraft receivers and radio relays.
- **Wingman burn-through.** A wingman 2 km away is almost unjammable compared with ATC 150 km away (Section 1.6).

### 1.2 Received powers (all dB quantities)

Received jammer power at R:

```
J [dBm] = ERP_J [dBm] − L_J [dB] + G_R(θ_J) [dBi] − L_pol,J [dB] − L_misc,J [dB]
```

Received signal power at R:

```
S [dBm] = ERP_T [dBm] − L_S [dB] + G_R(θ_T) [dBi] − L_pol,S [dB] − L_misc,S [dB]
```

- `ERP = P_tx[dBm] + G_tx(toward R)[dBi] − feeder loss`. Strictly this is EIRP; "ERP" in EW texts usually means EIRP.
- `L_J`, `L_S` are path losses (Section 1.3).
- `G_R(θ)` is the receiving-antenna gain toward each emitter. On aircraft this differs by direction because of airframe shadowing (Section 1.4).

The **jamming-to-signal ratio** is:

```
J/S [dB] = ERP_J − ERP_T − L_J + L_S + G_R(θ_J) − G_R(θ_T) − (L_pol,J − L_pol,S) + BW_term + DC_term      (1)
```

Free-space form [S: Adamy EW 101; rfcafe J/S references https://rfcafe.com/references/electrical/ew-radar-handbook/jamming-to-signal-ratio-constant-power.htm]:

```
J/S = ERP_J − ERP_T − 20·log10(d_J) + 20·log10(d_S) + G_RJ − G_RS           (2)
```

Frequency cancels when the jammer is co-channel. One-way comm links scale as **20 log** of the range ratio in free space and **40 log** in two-ray (flat-earth far) propagation. Radar self-protection jamming differs: the R² vs R⁴ asymmetry does not exist in comms.

### 1.3 Path loss models (apply the *same* models already used for S)

**Free space** [S: ITU-R P.525]:

```
L_fs [dB] = 32.44 + 20·log10(d [km]) + 20·log10(f [MHz])
```

**Two-ray (plane earth)** far-field asymptote [S: Adamy EW 101 eq. for two-ray; Parsons, *The Mobile Radio Propagation Channel*]:

```
L_2ray [dB] = 120 + 40·log10(d [km]) − 20·log10(h_T [m]) − 20·log10(h_R [m])
```

Use it only beyond the Fresnel-zone distance:

```
FZ [km] = h_T [m] · h_R [m] · f [MHz] / 24 000        (Adamy's practical form)
```

- For d < FZ: use free space, or the full coherent two-ray sum with lobing, which OpenFreq already has.
- For d > FZ: use L_2ray.

Implications for aircraft:

| Geometry | h_T (m) | h_R (m) | f (MHz) | FZ (km) | Regime at 100 km |
|---|---|---|---|---|---|
| Ground jammer to aircraft at FL100 | 10 | 3000 | 250 | 312 | free space (+ lobing) |
| Ground jammer to helicopter at 50 m | 10 | 50 | 60 | 1.25 | two-ray, 40 log |
| Ground jammer to ground FM radio | 10 | 2 | 50 | 0.04 | two-ray, 40 log, + terrain |
| Air-to-air | 6000 | 6000 | 250 | 375 000 | free space |

**[D]** In practice, ground-to-air and air-to-air jamming is free-space limited until the radio horizon. Ground-to-ground VHF-FM jamming (DCS ground units, helicopters NOE) falls off at 40 dB/decade, which makes burn-through much more geometry sensitive.

**Radio horizon** (4/3 Earth) [S: standard; ITU-R P.528 for aeronautical curves]:

```
d_LOS [km] ≈ 4.12·(√h_T [m] + √h_R [m])
```

Example: jammer mast 10 m, aircraft at 3000 m gives 4.12·(3.16 + 54.8) ≈ 239 km. Beyond this point, diffraction loss rises tens of dB within a few km. ITU-R P.528 gives aeronautical basic transmission loss curves for 100 MHz - 15.5 GHz.

**Terrain / knife-edge diffraction** [S: ITU-R P.526]. The Fresnel–Kirchhoff parameter is:

```
v = h · sqrt( 2/λ · (1/d1 + 1/d2) )
```

The loss is:

```
J(v) [dB] = 6.9 + 20·log10( sqrt((v−0.1)² + 1) + v − 0.1 )      for v > −0.78, else 0
```

Use the *same* terrain routine for jammer→receiver as for transmitter→receiver. A jammer behind a ridge is simply less effective. This is the main geometric counter-tactic real crews use (masking).

### 1.4 Antenna gains and patterns

- **Aircraft VHF/UHF blades** are vertical monopole-like. They are roughly omnidirectional in azimuth (about 0 to +2 dBi on the horizon) with deep nulls toward zenith/nadir. Fuselage, wing and tail shadowing gives about **−5 to −20 dB** in some aspects. Example: a dorsal blade looking down past the wing at a ground jammer while the aircraft is banked. [E: typical published blade-antenna pattern measurements; exact values depend on airframe.] Recommendation: G_R(elevation, aspect) = 0 dBi nominal. Apply −10 dB when the emitter is on the far side of the fuselage relative to the antenna location (top/bottom blade), and −15 dB within about 20° of the antenna's axial null.
- **Jammer antennas:**
  - Ground comm jammers use log-periodic or Yagi arrays. Typical gain is **6-12 dBi** for directional systems with a 30-70° beamwidth, and about 2 dBi for omni whips/discones.
  - Airborne jammers (EC-130H Compass Call, EA-18G with ALQ-227/ALQ-99 low-band pods, NGJ-LB ALQ-249) use steerable arrays; their gains are not public **[E: 6-15 dBi]**.
- **Ground stations** (ATC, AWACS relay, FARP) usually have omni antennas at 0-6 dBi.

### 1.5 Polarization

- Air-band and VHF-FM tactical comms are **vertically polarized**. A co-polarized jammer has L_pol = 0 dB.
- A circularly polarized jammer against a linear receiver has L_pol = 3 dB.
- Theoretical cross-polarization loss is 20-30 dB. In practice, aircraft bank angle and multipath depolarization reduce this to about **10 dB** at most. Recommendation: L_pol = 20·log10(|cos Δψ|), clamped at −10 dB, where Δψ is the angle between the polarization vectors (roughly the aircraft bank angle projected onto the line of sight). [E]
- A banked aircraft also tilts its *own* link to ATC, so S and J both lose. Only the difference matters in J/S.

### 1.6 Bandwidth matching (power spreading loss)

Only the jammer power inside the receiver's predetection (IF) bandwidth B_R competes with the signal.

For a noise jammer of bandwidth B_J, centred at offset Δf:

```
J_in = J · (overlap(B_J centred at Δf, B_R centred at 0) / B_J)
     ≈ J − 10·log10(B_J / B_R)          when B_J ≥ B_R and fully overlapping      (3)
```

[S: Adamy EW 101; Poisel, *Modern Communications Jamming Principles and Techniques*, 2nd ed., ch. 1-2]

So the bandwidth term in (1) is `BW_term = −10·log10(B_J/B_R)` when B_J > B_R, and 0 otherwise. Narrowband CW is all in-band if |Δf| < B_R/2.

Typical receiver IF bandwidths:

| Receiver | Channel | Typical IF −6 dB BW | Source/notes |
|---|---|---|---|
| VHF AM 25 kHz (civil, 118-137) | 25 kHz | ±8 kHz (≈16 kHz) | ED-23C/DO-186B. Wide to accept offset-carrier ("climax") systems. **[S/E: from memory of EUROCAE ED-23C; verify]** |
| VHF AM 8.33 kHz | 8.33 kHz | ±2.78 kHz (≈5.6-7 kHz) | ED-23C 8.33 kHz receiver class **[S/E]** |
| UHF AM 225-400 (ARC-164/ARC-210) | 25 kHz | ≈16-20 kHz | **[E]** |
| VHF-FM 30-88 (SINCGARS SC mode, ARC-186 FM) | 25 kHz | ≈16-20 kHz; deviation about ±5-6.5 kHz | **[E]** |

Barrage example: a 1 kW (60 dBm) jammer spread over the whole 225-400 MHz band (175 MHz) delivers `60 − 10·log10(175e6/16e3) = 60 − 40.4 = 19.6 dBm` of *effective* ERP per 16 kHz channel. That is a 40 dB penalty compared with spot jamming. This is why real barrage jammers either cover a sub-band (a few MHz) or rely on very high power and directional arrays.

Power density: `PSD_J [dBm/Hz] = ERP_J − 10·log10(B_J)`. Jamming effectiveness is set by PSD_J × B_R, not by ERP_J.

### 1.7 Duty cycle

For a pulsed or look-through jammer with duty cycle D:
- Average power is `J_avg = J_peak + 10·log10(D)`.
- **Against analog voice**, intelligibility depends on the *time fraction masked*, not on the average power. Listeners "glimpse" speech in the gaps [S: Miller & Licklider 1950, "The intelligibility of interrupted speech", JASA 22:167]. AGC recovery stretches the effective masked fraction (Section 4.6).
- **Against digital links**, a jammer needs only to corrupt enough bits to defeat FEC or the vocoder. Adamy's often-quoted rule is that digital links are denied at **J/S ≈ 0 dB with 20-33% duty cycle**, while analog voice needs **about 100% duty** [S: Adamy EW 101/102 (recollection of the rule; verify exact wording)].

### 1.8 Burn-through range

Define `JS_req` as the J/S needed to deny the link (Section 2). Then solve (2) for the transmitter range at which jamming just fails.

Free space:

```
d_S,bt = d_J · 10^( (JS_req − ERP_J + ERP_T − G_RJ + G_RS − BW_term + L_pol...) / 20 )    (4)
```

Two-ray: replace /20 with /40, and include the antenna heights.

**Worked example** [D] (UHF AM, f = 250 MHz, free space, all receive gains 0 dBi):

| Item | Value |
|---|---|
| Ground spot jammer | 500 W into a 10 dBi Yagi gives ERP = 67 dBm. Range to aircraft 100 km: FSPL = 32.44 + 40 + 47.96 = 120.4 dB. **J = −53.4 dBm** |
| AWACS/ATC | 50 W into 3 dBi gives ERP = 50 dBm at 200 km: FSPL = 126.4 dB. **S = −76.4 dBm**. J/S = **+23 dB**, so the link is denied |
| Wingman | 10 W into 0 dBi gives ERP = 40 dBm at 5 km: FSPL = 94.4 dB. **S = −54.4 dBm**. J/S = **+1 dB**, which is marginal |
| Burn-through, JS_req = +3 dB (AM) | d_S,bt = 100 km · 10^((3 − 67 + 40)/20) = 100 · 10^(−1.2) = **6.3 km** |
| Thermal floor | −174 + 10·log10(16 000) + NF 8 dB = **−124 dBm**. The jammer is 70 dB above the noise, so thermal noise is irrelevant while the jammer is on |

The pilot would hear ATC disappear under the jammer at 200 km, while the wingman stays readable until about 6 km. This is exactly what real crews describe.

---

## 2. Required J/S by modulation

Note on what "J/S" means here. The *audio* outcome depends on the detector. All J/S values below are **predetection, in the receiver IF bandwidth, with jammer noise filling that bandwidth**, unless stated otherwise.

### 2.1 Mapping audio SNR to intelligibility (common to all analog)

Use an Articulation Index / Speech Intelligibility Index (SII) approach [S: ANSI S3.5-1997 (R2017), "Methods for Calculation of the Speech Intelligibility Index"; Kryter 1962 JASA 34:1689; French & Steinberg 1947].

Per band i (e.g., 6 octave bands or 18 one-third-octave bands restricted to 300-3000 Hz by the radio):

```
A_i = clip( (SNR_i [dB] + 15) / 30 , 0, 1 )          (band audibility; ANSI S3.5 uses a 30-dB speech dynamic range)
SII = Σ I_i · A_i                                  (I_i = band-importance weights, Σ I_i = 1 over the full band)
```

[S/E: the ±15 dB window is the ANSI S3.5-1997 form as I recall it. Some articulation-index implementations use −12 to +18 dB (e.g., https://metricgate.com/docs/articulation-index/). The difference is about 3 dB of calibration; pick one and tune by ear.]

**Radio band limiting.** Restricting speech to 300-3000 Hz loses about 0.25-0.3 of the importance weight, so a *perfectly clean* radio has SII_max ≈ 0.7-0.75. That is still very intelligible.

Simplified octave-band importance weights for "average speech" [S: ANSI S3.5 Table 3 (octave method), approximate]:

| Band (Hz) | 250 | 500 | 1000 | 2000 | 4000 | 8000 |
|---|---|---|---|---|---|---|
| I_i | 0.0617 | 0.1671 | 0.2373 | 0.2648 | 0.2142 | 0.0549 |

- Inside a 300-3000 Hz radio band, count about 0.5 × the 250 Hz weight (≈ 0.03), the full 500/1000/2000 weights, and about 0.5 × the 4000 Hz weight (≈ 0.11).
- SII_max ≈ 0.03 + 0.167 + 0.237 + 0.265 + 0.11 ≈ **0.81** before any noise. A clean radio is "good".

SII to intelligibility and operational acceptability [S: MIL-STD-1472 (Human Engineering), voice-communication intelligibility criteria]:

| Communication requirement | AI/SII | Modified Rhyme Test (MRT) | PB words |
|---|---|---|---|
| Exceptionally high (critical) | 0.7 | 97% | 90% |
| Normally acceptable | 0.5 | 91% | 75% |
| Minimally acceptable | 0.3 | 75% | 43% |
| Denied (practical) | < 0.1-0.15 | < 40% | < 15% |

With flat (white-in-band) noise, every band has SNR_i = SNR_audio, so `SII ≈ SII_max · clip((SNR_audio + 15)/30, 0, 1)`:

| SNR_audio (dB) | −15 | −10 | −6 | −3 | 0 | +3 | +6 | +10 | +15 |
|---|---|---|---|---|---|---|---|---|---|
| SII (×SII_max 0.81) | 0 | 0.14 | 0.24 | 0.32 | 0.41 | 0.49 | 0.57 | 0.68 | 0.81 |
| Practical result | nothing | odd word | fragments | minimal | usable w/ repeats | OK | good | very good | clean |

Cross-check: sentence speech-reception threshold (50% sentences) in steady speech-shaped noise is about **−5 dB SNR** for normal-hearing listeners [S: Plomp & Mimpen 1979, Audiology 18:43]. Sentences and standard phraseology (call signs, "cleared hot") are far more robust than isolated words. Aviation phraseology is highly predictable, so add a **+3 to +5 dB effective benefit** for expected messages and a penalty for unexpected ones (numbers, frequencies). [E]

### 2.2 AM voice (DSB-LC, envelope detector, AGC)

Post-detection SNR above threshold [D, standard result, e.g., Haykin *Communication Systems*; Taub & Schilling *Principles of Communication Systems*].

The received signal is `r(t) = A_c[1 + m·x(t)] + n(t)`, with `C = A_c²/2` the carrier power, one-sided noise PSD N₀ (W/Hz), IF bandwidth B, audio bandwidth W, and `P_x = <x²>` with peak |x| = 1.

```
SNR_audio = C·m²·P_x / (N₀·W) = CNR_IF · (B/W) · m²·P_x           (5)
```

Here `CNR_IF = C/(N₀B)`.

Typical numbers:
- ICAO requires modulation depth m ≥ 0.85. Speech peak-to-RMS is about 12-15 dB, so `m²P_x ≈ 0.72 × 0.04 ≈ −15 dB`, or about −12 dB with transmitter speech processing/clipping [E].
- With B = 16 kHz and W = 2.7 kHz, `B/W = +7.7 dB`.

**Rule:** `SNR_audio ≈ CNR_IF − 5 dB (±2 dB)` for noise filling the IF. [D]

Envelope-detector threshold: below CNR_IF ≈ 6-10 dB the envelope detector *suppresses the weak signal* (output signal ∝ CNR², noise-dominated envelope). Intelligibility then falls faster than (5) predicts. The I/Q simulation reproduces this automatically.

**Required J/S for AM (noise jamming filling the IF):**
- J/S = −5 dB gives SNR_audio ≈ 0 dB and SII ≈ 0.4: degraded but usable.
- J/S = 0 dB gives SNR_audio ≈ −5 dB and SII ≈ 0.27, below "minimally acceptable".
- J/S = +5 dB gives SNR_audio ≈ −10 dB, plus detector threshold suppression, so SII < 0.1: **denied**.
- J/S ≥ +10 dB: nothing gets through. In addition, AGC capture pushes the wanted voice down (Section 4.6).

**Engineering threshold for AM voice denial: J/S ≈ 0 to +6 dB in the IF, with 100% duty.** This matches the commonly quoted "analog: J/S ≈ 0-10 dB" figures [S: Poisel 2011 ch. 1; Adamy EW 101; rfessentials summary "analog FM J/S > 0-6 dB"].

**AM has no capture effect.** Any interference is linearly superimposed and audible at any level [S: https://rfessentials.com/resources/rf-glossary/capture-effect/]. Even J/S = −20 dB is audible as a whistle or noise; it just does not destroy intelligibility.

### 2.3 FM voice: capture effect and threshold

Above threshold, with no pre-emphasis [D; Taub & Schilling; Haykin]. With frequency deviation Δ_pk, β = Δ_pk/W, and γ = C/(N₀W):

```
SNR_audio = 3·β²·P_x·γ = 3·β²·P_x·(B/W)·CNR_IF                     (6)
```

With Δ_pk = 5 kHz, W = 3 kHz, β = 1.67: `3β² = 9.2 dB`. With `P_x(speech) ≈ −13 dB` and `B/W = +7.7 dB`: **SNR_audio ≈ CNR_IF + 4 dB**. Add about +3-6 dB if pre-/de-emphasis is used [E].

**FM threshold** (Rice click noise) occurs at CNR_IF ≈ 9-12 dB. Below it, output SNR collapses with "click/crackle" impulses. The Taub & Schilling approximation for tone modulation is:

```
SNR_audio = (3/2)·β²·γ / ( 1 + (12β/π)·γ·exp(−γ/(2(β+1))) )        (7)
```

[S: Taub & Schilling; Rice, "Noise in FM receivers", 1963.]

**Capture effect.** With two FM signals at amplitudes A_s > A_j, `ρ = A_j/A_s < 1`, and frequency offset Δf, the limiter/discriminator output is dominated by the stronger signal. The weaker contributes an interference term with phase about `ρ·sin ψ(t)`, where `ψ = 2πΔf·t + φ_j(t) − φ_s(t)`. So the frequency error is about `ρ·dψ/dt`, which is suppressed further by limiter action [S: Leentvaar & Flint, "The capture effect in FM receivers", IEEE Trans. Commun. 24(5), 1976].

**Capture ratio** (S/J needed to suppress the weaker by 30 dB):
- About 1-3 dB for good broadcast receivers.
- About 3-6 dB for generic receivers.
- Less than 1 dB for good professional land-mobile radios.

[S: https://rfessentials.com/resources/rf-glossary/capture-effect/; https://radiomuseum.org/forum/fm_capture_ratio.html]

Consequences for FM required J/S:
- **Noise jamming:** the link fails once the *noise* pushes CNR_IF below threshold. That happens at `S/J ≈ 10 dB`, i.e. **J/S ≈ −10 dB already causes severe degradation (clicks)**. J/S ≥ 0 dB gives complete denial (noise "captures" the limiter).
- **CW/tone jamming:** if `J/S < −(capture ratio)`, roughly J/S < −3 to −6 dB, the tone is largely suppressed and only a faint whistle is heard. If **J/S > +1 to +3 dB, the jammer captures the receiver** and the wanted voice vanishes. The transition is *abrupt*, about 3-6 dB wide. This is the defining FM behaviour.
- **Voice/music (imitative) jamming:** the stronger signal wins completely. The listener hears either the friendly or the jammer, with "popping" swaps when they are near equal.

**Engineering thresholds for FM voice:**
- Noise jammer: J/S ≈ −6 dB degrades; J/S ≥ 0 dB denies.
- Carrier/tone/voice jammer: J/S ≥ +3 dB denies, and silently so in the CW case.
- FM is *more robust* against weak jamming (below capture) and *less robust* than AM against strong jamming (all-or-nothing).

### 2.4 Digital / vocoded voice and FSK data

Noncoherent binary FSK in noise plus broadband noise jamming [S: Torrieri, *Principles of Spread-Spectrum Communication Systems*, 4th ed., ch. 2; Peterson, Ziemer & Borth]:

```
P_b = ½ · exp( −E_b / (2·(N₀ + J₀)) ),        J₀ = J_in / B_R  (W/Hz)
```

**Partial-band jamming** with fraction ρ of the hop band, of total bandwidth W_ss:
```
P_b(ρ) = (ρ/2)·exp(−ρ·E_b/(2J₀))
```
The worst-case ρ* = 2J₀/E_b gives:
```
P_b,worst ≈ e⁻¹ / (E_b/J₀)        for E_b/J₀ > 2
```
That is, inverse-linear instead of exponential. This is why partial-band jamming is optimal against uncoded FH/FSK [S: Torrieri; Simon, Omura, Scholtz & Levitt, *Spread Spectrum Communications Handbook*].

**Processing gain** (FH): `PG = 10·log10(W_ss / B_R)`. Example: SINCGARS 30-88 MHz (58 MHz) / 25 kHz gives about 33.6 dB [D]. A barrage jammer must overcome PG; a follower jammer bypasses it.

**Vocoder tolerance:**
- CVSD at 16 kbps (KY-57/58 VINSON-era) remains intelligible to roughly BER 1-5%.
- Modern LPC/MELP vocoders fail more abruptly at around 1-3% BER with FEC [E: from vocoder literature; exact thresholds depend on FEC/interleaving].
- Required J/S for a non-spread, uncoded digital voice link is about **0 dB at 20-33% duty** (Adamy rule). With a 10-15 dB margin for coded QPSK, the rfessentials summary gives "QPSK without FEC: J/S > 10-15 dB" for *continuous* denial.

**Follower (repeater) jammer vs FH** [S: Torrieri]. The jammer must hit the hop before the dwell time T_d ends:

```
(d_TJ + d_JR − d_TR)/c + T_proc ≤ η·T_d
```

η ≈ 0.5-0.8. For SINCGARS at about 100 hops/s, T_d ≈ 10 ms, so the extra path length can be up to roughly 1,500 km. **Geometry is not the constraint; processing time is.**

Have Quick (UHF AM FH) hop rate and pattern details are not authoritatively public **[flag]**. If modelled, treat it as FH with ~tens to hundreds of hops per second, PG ≈ 10·log10(175 MHz/25 kHz) ≈ 38 dB, and accept the uncertainty.

---

## 3. Jamming techniques and their physics

Doctrinal definitions:
- ATP 3-12.3 (Jan 2023) ch. 4, "spot and barrage jamming" [https://irp.fas.org/doddir/army/atp3-12-3.pdf].
- FM 34-45, ch. 6 [https://www.globalsecurity.org/intell/library/policy/army/fm/34-45/ch6.htm].
- FM 24-33 (1990) ch. 3 "obvious jamming signals" [https://irp.fas.org/doddir/army/fm24-33/fm243_4.htm].
- Poisel 2011; Adamy *EW 102* (comm jamming chapters); Schleher, *Electronic Warfare in the Information Age* (1999).

| Technique | Physics | Bandwidth & power density | Strengths / weaknesses | Effect on AM rx | Effect on FM rx |
|---|---|---|---|---|---|
| **Spot noise** | Gaussian noise (or noise-FM) on one channel | B_J ≈ B_R (10-25 kHz); full power per channel | Max J₀ on target. Defeated by a channel change; must know the freq | Hiss/roar; AGC drops voice; squelch held open | Above about −10 dB J/S gives clicks then full noise; noise squelch may *close* (see 4.5) |
| **Barrage** | Noise across many channels or a band | B_J = MHz to whole band; PSD = ERP − 10 log B_J | No target knowledge needed; −10 log(B_J/B_R) penalty; jams friendlies too | Same as spot but weaker per channel | Same |
| **Partial-band** | Barrage over fraction ρ of an FH band | Optimised ρ* | Best vs FH/FSK (Section 2.4) | Hops land in or out: periodic bursts | Bursts of noise/clicks at hop rate |
| **Swept spot / sweep** | Narrow noise or a tone swept across a band at rate R_s | Instantaneous narrow; time-average spread | High instantaneous J/S each pass; time-sharing over many channels | Periodic "zip/chirp" every sweep; heterodyne glide | Capture during the dwell then release: periodic "blips"/pops |
| **CW tone (single)** | Unmodulated carrier at offset Δf | All power in one line | Most power-efficient per Hz; weak masking of speech per watt | Steady heterodyne whistle at |Δf|; AGC capture; voice suppressed when J>S | J>S: silent capture ("subtle jamming", squelch capture); J<S: faint whistle |
| **Multi-tone / comb** | K tones, often one per channel (comb) | J/K per tone | Covers many channels like barrage, but with tones | Whistle(s); intermod among tones | Capture on hit channels |
| **Stepped tones ("bagpipes")** | Tone whose frequency steps up and down | — | Classic voice-net harassment, very annoying | Musical, bagpipe-like steps | Steps heard only if FM-modulated within the channel; otherwise capture + silence |
| **Gulls** | Variable freq: fast rise, slow fall | — | Nuisance, very effective vs voice | Seagull cry | Similar if within the channel deviation |
| **Wobbler** | Single carrier modulated by a low, slowly varying tone | — | Howl | Howl | Howl (if the modulation is FM within the channel) |
| **Spark** | Short, high-intensity bursts at a rapid rate | Wideband (short pulses) | Cheap, "one of the most effective"; hits all types | Buzz/rasp at the PRF; AGC pumping | Click train; capture during the pulse |
| **Random pulse** | Pulses of random amplitude, width and rate | Wideband | Vs data/TTY | Crackle/static bursts | Clicks |
| **Recorded sounds / voice / music** | Any audio modulated onto a carrier | Audio bandwidth | Distracting; deceptive | Heard *mixed* with the friendly + beat note | Stronger wins; friendly vanishes if J/S > CR |
| **Preamble jamming** | Transmits the radio's sync/preamble so radios lock in receive | Protocol-specific | Very effective vs secure-voice nets | n/a (crypto) | Radios held in RX / "lock-up" |
| **Responsive / follower** | Intercept, then jam the active channel | Spot | Efficient; delay T_react | First 50-500 ms of each transmission clean, then jammed | Same; capture after the delay |
| **Look-through** | Jammer pauses periodically to listen | Duty D = 0.8-0.95 | Lets the jammer track freq changes | Regular brief gaps where voice "punches through" | Same; FM gaps are clean |
| **Imitative deception / meaconing** | Fake ATC/AWACS voice; re-broadcast of the real signal | — | Requires language/procedure skill; countered by authentication | Both heard | Capture: jammer replaces friendly |
| **Smart / protocol-aware** | Targets sync, squelch tone, CTCSS, FH TOD, etc. | Very low power | Efficient; out of scope for analog voice | — | E.g., transmit a 150 Hz squelch tone to open squelches |

Descriptions of the obvious jamming signals are quoted from FM 24-33 ch. 3 [S]:
- **Random noise:** "synthetic radio noise. It is random in amplitude and frequency."
- **Stepped tones:** "tones transmitted in increasing and decreasing pitch. They resemble the sound of bagpipes."
- **Spark:** "bursts are of short duration and high intensity... repeated at a rapid rate."
- **Gulls:** "a quick rise and slow fall of a variable radio frequency."
- **Random pulse:** "pulses of varying amplitude, duration, and rate."
- **Wobbler:** "a single frequency which is modulated by a low and slowly varying tone" (howling).
- **Recorded sounds:** music, screams, laughter.
- **Preamble jamming:** locks radios in receive.

FM 24-33 also describes **"subtle jamming" = squelch capture**: a strong *unmodulated* carrier produces *no sound* and the operator may not notice that the net is blocked. Its recognition guidance is: "unmodulated jamming shows a lack of noise". This is exactly the FM-capture result in Section 2.3 and is a high-value realism feature.

Power-density trade-off summary: for fixed ERP, the jamming PSD scales as 1/B_J. Spot gives `J_in = J`. A barrage over N channels gives `J_in = J/N`. Sweep gives `J_in = J` for a fraction `B_R/B_sweep` of the time; whether it is effective depends on whether the revisit time `T_sweep < ~100-200 ms` (the speech syllable timescale) [E: speech syllabic rate about 4-5 Hz, so sweeps faster than about 10 Hz act like continuous interference, while slower sweeps leave usable gaps].

---

## 4. What jamming sounds like: precise DSP descriptions

Notation:
- Complex baseband at the receiver tuning frequency, sample rate f_s (must cover ±B_R/2; 48 kHz complex covers ±24 kHz, which is OK for 25 kHz channels).
- Wanted signal: `s[n]`.
- Thermal noise: `w[n]`.
- Jammer: `j[n] = A_j · e[n] · exp(j(2π·Δf[n]·n/f_s + φ₀))`, where `e[n]` is the technique's complex envelope with unit average power, and `A_j = sqrt(J_in,lin)` in the same amplitude units as s.
- Received: `r[n] = s[n] + j[n] + w[n]`. Then IF filter, AGC, squelch, detector, audio band-pass.

### 4.1 AM receiver with a strong interferer: the core physics

Wanted carrier with voice: `A_s(1+m x_s)`. Interferer at offset Δf with complex envelope a_j. The envelope detector outputs `|A_s(1+m x_s) + a_j e^{jψ}|`, where ψ = 2πΔf t + φ.

**Weak interferer (|a_j| ≪ A_s):**
```
|·| ≈ A_s(1+m x_s) + Re{a_j e^{jψ}}
```
This is a *linear superposition*:
- A CW jammer gives a pure **heterodyne whistle at |Δf|**, at a level of `20·log10(|a_j|/(A_s·m·x_rms))` relative to the voice.
- A noise jammer gives hiss.
- An AM-voice jammer gives the jammer's voice *plus* a whistle at Δf. This is the familiar "two aircraft transmitting at once" squeal [S: AM has no capture; envelope superposition].

**Strong interferer (|a_j| ≫ A_s):**
```
|·| ≈ |a_j| + A_s(1+m x_s)·cos(ψ − arg a_j)
```
- The *jammer's own* modulation dominates.
- The wanted voice becomes a **DSB-modulated version of itself on a |Δf| sub-carrier**. For |Δf| of a few hundred Hz to a few kHz, it is heard as garbled, frequency-shifted "monkey chatter" riding on the whistle.
- AGC normalises to |a_j|, so the wanted audio drops by about `20·log10(A_s/|a_j|)` = −J/S dB. This is **AGC capture/desensitization** [D].

**|Δf| below the 300 Hz audio HPF** (e.g., a comb tone a few tens of Hz off the true carrier):
- The beat itself is filtered out.
- It still modulates the *voice amplitude* at Δf ("wow-wow", slow fading or flutter).
- At Δf ≈ 0 it produces random-phase enhancement or cancellation of the carrier.

Realism note: aviation transmitters have frequency tolerance of order ±0.002% (25 kHz) to about ±1-5 ppm (8.33 kHz) [E/S: ICAO Annex 10 Vol III, values from memory; verify]. A jammer set to the nominal channel therefore beats with the transmitter at some tens of Hz to about 2 kHz, which is randomise-worthy.

### 4.2 FM receiver with a strong interferer: capture

Limiter + discriminator output: `y = d/dt arg(s + j + w)`.

- **ρ = |a_j|/A_s < 1:** the output is about the friendly audio plus `ρ·Δf·cos ψ`-type terms. That is a whistle at |Δf|, suppressed further by the limiter, with audio level about `20·log10(ρ·|Δf|/Δ_pk)` relative to full-deviation voice.
- **ρ > 1:** the output follows the jammer's instantaneous frequency.
  - Unmodulated CW gives **silence**, and the noise is "quieted" as well. This is the "subtle jamming" squelch capture.
  - Noise-FM gives roaring noise.
  - Voice-FM gives the jammer's voice cleanly.
- **ρ ≈ 1:** the phase wraps by 2π every time ψ passes π, producing **impulsive clicks at a rate of about |Δf|** and rapid swapping between the two programmes ("popping", "picket-fencing"). The I/Q simulation produces this if the discriminator is implemented as a phase-difference (`arg(r[n]·conj(r[n−1]))`), which it should be.

### 4.3 Sound catalogue with synthesis recipes (`e[n]`, `Δf[n]`)

Parameters marked [E] are plausible ranges chosen to match the doctrinal descriptions. Exact threat waveforms are not public.

| Name (doctrinal / slang) | Complex envelope `e[n]` | Offset `Δf[n]` | What the pilot hears (AM) | What the pilot hears (FM) |
|---|---|---|---|---|
| **Random noise (spot)** | complex Gaussian, band-limited to B_J (≤ B_R) | 0 (± jitter) | Loud hiss/roar, voice buried; constant level (AGC) | J/S < −10 dB: clean; −10 to 0: crackling clicks; > 0: roar, voice gone |
| **Barrage** | complex white Gaussian across the full simulated bandwidth, level J·B_R/B_J | n/a | Same as spot, weaker | Same |
| **CW / dead carrier** | 1 | constant 200 Hz-3 kHz, slow drift ±20 Hz/s [E] | Steady whistle; voice ducks; at high J/S voice reduced to garbled chatter | High J/S: **silence** (squelch open, no hiss): "subtle jamming" |
| **Stepped tones ("bagpipes")** | 1 | piecewise-constant from a set of 4-8 pitches in 400-2500 Hz, step every 100-300 ms, up/down pattern [E] | Musical stepped whistles (bagpipe) | If implemented as FM of a carrier by tones: bagpipes via the discriminator; if CW stepping: capture with faint steps |
| **Gulls** | 1 | asymmetric sawtooth: rise 300→2500 Hz in ~50-100 ms, fall over 300-600 ms, repeat [E] | Seagull cry | Similar (FM-modulated version) |
| **Wobbler** | carrier FM'd: Δf = f₀ + D·sin(2π f_m t) with f₀ ≈ 800-1500 Hz, D ≈ 200-600 Hz, f_m ≈ 0.5-5 Hz [E] | — | Howl/siren | Howl |
| **Swept spot** | narrow noise (1-5 kHz) or tone | linear sweep across ±B_sweep/2, sweep period T_sw = 20 ms-2 s [E]; in-channel only while |Δf| < B_R/2 | Fast sweep (T_sw < 100 ms): rasp/buzz at 1/T_sw; slow sweep: "zip" chirp every pass with clear gaps | Periodic pops/blips as capture occurs during passes |
| **Spark** | pulse train: rect pulses of width 50-500 µs at PRF 50-400 Hz, high peak, carrier or noise filling [E] | 0 | Harsh buzz/rasp at the PRF (motorboat when PRF < 30 Hz); AGC pumping | Click/buzz train |
| **Random pulse** | Poisson pulses, rate 20-500/s, width 0.1-5 ms, random amplitude (log-normal, σ = 6 dB) [E] | 0 | Crackle like heavy static (QRN) | Clicks |
| **Rotary / "bubble"** (radio-amateur and Soviet-jammer folklore) | CW AM-modulated at 5-20 Hz, or tone FM-swept periodically [E; weakly sourced] | — | Bubbling/warbling | Warble |
| **Music / voice / recorded** | AM: `1 + m_j·x_j[n]` with m_j ≈ 0.8, real; FM: `exp(j·2π·Δ_j·Σ x_j)` | AM: random small offset (beat note) | Both voices + squeal | Stronger wins completely |
| **Comb** | sum over channels; in the *simulated* baseband only the tone(s) within ±f_s/2 matter | channel spacing ± tolerance | Low beat note / flutter | Capture |
| **Look-through modifier** | multiply any `e[n]` by a gate: off for T_lt (2-20 ms) every T_cyc (50-500 ms), D = 0.85-0.97 [E] | — | Brief rhythmic "holes" | Same |
| **Responsive modifier** | jammer on after the friendly carrier is detected + T_react (50 ms-1 s) [E]; stays on for T_hang (0.5-3 s) after the friendly unkeys | — | First syllable clean, then jam; jam persists after the transmission | Same |

### 4.4 AGC interaction

AGC in AM receivers is driven by **total IF envelope power** (S + J + N). Model:

```
g[n+1] = g[n] + k_a·(P_ref − P_det[n])      fast attack (τ_a ≈ 1-10 ms)
                                            slow decay  (τ_d ≈ 100-1000 ms)   [E: typical aviation AM AGC]
```

Effects:
- **Continuous jammer:** gain is set by J. Wanted audio scales as about S/(S+J) in power. The radio "sounds alive" but the voice sinks into the jammer.
- **Pulsed jammer:** fast attack drops the gain during each pulse; slow decay keeps it low after the pulse. The voice is **ducked for τ_d after every pulse**, which is the "pumping" effect. Effective masked time fraction `D_eff = min(1, D + PRF·τ_d)`. With PRF = 10 Hz and τ_d = 200 ms, D_eff = 1. That is why low-duty pulse ("spark") jamming is effective against AM voice despite low average power. [D]
- **Swept jammer:** each pass punches the AGC, so the voice "dips" at the sweep rate.

### 4.5 Squelch interaction

- **AM carrier squelch (OpenFreq's model: squelch on carrier/IF power).** Any jammer with J_in > squelch threshold *holds the squelch open continuously*. The pilot hears the jammer all the time even when nobody is transmitting. This is correct for real aviation AM radios, many of which offer "squelch disable" for weak signals anyway.
- **FM noise squelch.** Real tactical FM radios (PRC-77/VRC-12/SINCGARS) gate on *the absence of high-frequency discriminator noise* (above ~3 kHz) or on a 150 Hz tone. So:
  - A **CW carrier quiets the discriminator**, which opens a noise squelch and gives silence ("squelch capture", FM 24-33).
  - A **noise jammer looks like "no signal"** and may *keep the squelch closed*, muting even the friendly signal.
  - **Tone squelch (150 Hz / CTCSS)** stays closed unless the jammer carries the tone. The operator then hears *nothing*, while the friendly message is lost because the friendly signal no longer captures.
  - Recommendation: if OpenFreq keeps power-based squelch for FM, document the difference. Optionally add a "noise squelch" mode that measures discriminator output power above 3.5 kHz before the audio LPF.
- **Squelch break / chatter.** A jammer near the threshold (swept or pulsed) causes the squelch to open and close rhythmically (squelch tail "bursts"). This emerges with hysteresis.

---

## 5. Receiver nonlinear effects at high J

These matter when the jammer is strong at the RF input but **off-channel**, or so strong that it overloads the front end. The baseband I/Q sim cannot represent off-channel energy, so model them analytically and inject the result as extra in-band noise or a gain loss.

Let `P_J,RF` be the jammer power at the antenna port (after preselector attenuation at offset Δf_off).

| Effect | Simple model | Typical airborne/tactical parameters [E unless noted] | When it matters |
|---|---|---|---|
| **Adjacent-channel leakage (selectivity)** | `J_in = P_J,RF − ACS(Δf_off)` | ACS ≈ 60 dB at ±25 kHz, 70-80 dB at ±50-100 kHz (ED-23C-class requirements are of this order; verify) | Strong jammer on the next channel; ground jammer close to the aircraft |
| **Reciprocal mixing** (LO phase noise) | `N_rm [dBm] = P_J,RF + L(Δf_off)[dBc/Hz] + 10·log10(B_R)` | L(25 kHz) ≈ −110 to −125 dBc/Hz; L(1 MHz) ≈ −135 to −145 dBc/Hz | A jammer a few channels away at −30 to 0 dBm raises the in-channel noise by tens of dB: "jammer in the next channel still kills my radio" |
| **Blocking / desensitization** (gain compression of the wanted signal) | `ΔG_dB ≈ 10·log10(1 + (P_J,RF/P_block)²)` (soft compression; simple form [E]) | Input P1dB ≈ −20 to −5 dBm; blocking spec: wanted signal degradation ≤ 3 dB with an unwanted signal of about −30 to −10 dBm at ±1 MHz or more | Jammer within a few km or co-located (e.g., ownship EW pods, or a jammer platform flying in formation) |
| **3rd-order intermodulation** | `P_IM3 = 3·P_in − 2·IIP3` (all dBm), at 2f₁−f₂ and 2f₂−f₁ | IIP3 ≈ −10 to +10 dBm | Multi-tone/comb jammers, or a jammer + strong friendly transmitter: produces *phantom in-channel tones* |
| **Front-end burnout / protection limiter** | above about +20 to +30 dBm at the port | — | Only for co-located emitters: practically never from jammers at range |
| **Image / spurious response** | `J_in = P_J,RF − IRR` | Image rejection ≈ 60-80 dB | Jammer at the image frequency (2·IF away) is a "smart" attack; generally ignore |

Example magnitude [D]: a 1 kW ERP (60 dBm) jammer at 10 km and 250 MHz gives `P_J,RF = 60 − 100.4 = −40 dBm` at the aircraft port.
- If it is 100 kHz off-channel, with L(100 kHz) ≈ −130 dBc/Hz and B_R = 16 kHz (42 dB): `N_rm = −40 − 130 + 42 = −128 dBm`. That is near the thermal floor, so negligible.
- At 2 km: `P_J,RF ≈ −26 dBm`, which gives `N_rm ≈ −114 dBm`. That is 10 dB above thermal, so weak friendlies are lost even off-channel.
- So off-channel effects matter mainly **within a few km** of strong jammers, or for co-site interference.

Implementation: sum all off-channel contributions into an **equivalent in-band white noise power `N_offch`** added to the thermal noise generator. Apply blocking as a multiplicative gain reduction on the wanted and in-band components **before** AGC. IM3 can be skipped unless multi-tone or comb jamming is added; if it is, inject IM3 tones as extra CW lines when they fall in-channel.

---

## 6. Recommended simulator model

### 6.1 Per (receiver R, jammer k) power budget, evaluated at the physics tick (e.g., 10-20 Hz, smoothed as for S)

```
for each receiver R tuned to f_R with IF bandwidth B_R, mode ∈ {AM, FM}:
    N_th   = -174 + 10log10(B_R) + NF_R                        [dBm]
    N_offch_lin = 0
    jam_list = []
    for each active jammer k:
        # 1. geometry and propagation (reuse signal code)
        L_k   = PathLoss(jammer_pos_k, R_pos, f_R, terrain)     # FSL / 2-ray / diffraction / horizon
        G_Rk  = RxPattern(R, direction_to_k)                    # airframe shadowing
        G_Jk  = JammerPattern(k, direction_to_R)                # beam steering / sector
        Lpol  = PolLoss(k, R)
        P_k   = Pout_k + G_Jk - L_k + G_Rk - Lpol               # total jammer power at R antenna port [dBm]

        # 2. spectral overlap with receiver passband (time-varying for sweep/hop)
        for each spectral component c of jammer k at time t (center f_c(t), bandwidth B_c, fraction-of-power p_c):
            Δf = f_c(t) - f_R
            frac_in = ∫ H_IF(f-f_R)·S_c(f) df  / ∫ S_c(f) df      # ≈ overlap(B_c,B_R)/B_c for flat noise
            if frac_in > 1e-6 and |Δf| < f_s/2:
                jam_list.append( (component waveform, Δf, P_k + 10log p_c + 10log frac_in) )   # in-band → synthesize as I/Q
            # out-of-channel effects
            N_offch_lin += lin(P_k + 10log p_c - ACS(|Δf|))               # selectivity leakage, if not already in-band
                         + lin(P_k + 10log p_c + Lphase(|Δf|) + 10log10(B_R))   # reciprocal mixing
            Pblock_total += lin(P_k + 10log p_c)
    G_block = -10log10(1 + (Pblock_total/P_block)^2)            # dB, applied to wanted + in-band
    N_total = N_th ⊕ N_offch                                    # ⊕ = power sum
```

### 6.2 Per-sample synthesis (audio thread)

```
r[n] = g_block · ( s[n] + Σ_k A_k · gate_k[n] · e_k[n] · exp(j·θ_k[n]) ) + w[n]·sqrt(N_total)
θ_k[n] = θ_k[n-1] + 2π·Δf_k[n]/f_s        (phase-continuous; Δf_k includes drift/sweep/step pattern)
A_k    = sqrt( lin(J_in,k) ) in the same amplitude reference as s[n] (S dBm → amplitude)
→ IF filter → AGC (driven by |r|²) → squelch (power, or optional noise-squelch for FM)
→ AM: envelope |r| (minus DC/carrier) ; FM: arg(r[n]·conj(r[n-1])) → de-emphasis
→ 300-3000 Hz band-pass → audio
```

Notes:
- Jammer amplitudes must use **the same dBm-to-amplitude reference** as the friendly signal. If they do, J/S in the sim equals the J/S from (1), and capture and AGC behaviour come out right with no fudge factors.
- **Smooth J_in** the same way S is smoothed, so the jammer level does not pop at tick boundaries. Sweeps and steps, however, should be computed *per sample* from `Δf_k[n]`, not per tick.
- **Barrage much wider than f_s:** inject white complex noise at level `J·B_R/B_J` instead of synthesising the whole band.
- **Frequency-hopping receiver** (if modelled): the in-band test changes each hop. A partial-band or barrage jammer "hits" a hop with probability ρ.
- **Determinism / network:** jammer parameters (seed, sweep phase, step pattern) should be sent as a compact state so all clients hear the same waveform. This is not physically necessary, but it is nice for multiplayer consistency. Random noise can be local.

### 6.3 Scalar intelligibility (for game logic, AI and UI)

```
SINR_IF   = S − (N_total ⊕ Σ_k J_in,k,eff)          [dB]

J_in,k,eff = J_in,k + M_tech                          (masking-efficiency correction, table below)
           + 10log10(D_eff,k)                         (time-masked fraction, incl. AGC recovery, look-through)

AM: SNR_audio = SINR_IF − 5 dB  ; if SINR_IF < 6 dB, subtract extra threshold loss 0.5·(6 − SINR_IF) dB [E]
FM: if any non-noise jammer has J_in,k > S + CR (CR = 3 dB): SNR_audio = −30 dB (captured)
    elif SINR_IF ≥ 10 dB: SNR_audio = SINR_IF + 4 dB (+ emphasis gain)
    else: SNR_audio = SINR_IF + 4 − 2.5·(10 − SINR_IF) dB   [E: piecewise fit to Eq. 7 below threshold]
SII ≈ SII_max · clip((SNR_audio + 15)/30, 0, 1),  SII_max ≈ 0.8 (300-3000 Hz)
Message received if SII ≥ 0.3 (minimally acceptable) — stochastic: P_word ≈ logistic((SII − 0.3)/0.06)
```

### 6.4 Parameter tables

**Masking-efficiency correction `M_tech`** (dB added to J_in for the *intelligibility scalar only*; the audio path stays physical) [E, grounded in critical-band masking and Miller & Licklider glimpsing]:

| Technique | M_tech (AM) | M_tech (FM) | Rationale |
|---|---|---|---|
| Noise (spot/barrage) | 0 | 0 | reference |
| CW single tone | −8 | 0 for capture test; −8 below capture | A tone masks only its critical band (~1/3 octave); the main AM damage is AGC capture |
| Stepped / gulls / wobbler | −4 | as CW | Moving tone covers more bands; adds annoyance |
| Swept noise, fast (T_sw < 100 ms) | 0 · D_eff | 0 · D_eff | Acts as continuous |
| Swept, slow (T_sw > 300 ms) | use D = B_R/B_sweep, D_eff via AGC | same | Listener glimpses between passes |
| Spark / pulse | use D_eff = min(1, D + PRF·τ_d) | D (no AGC in FM) | AGC pumping in AM |
| Voice / music | +2 | capture test | Informational masking: competing speech is worse than noise at equal SNR |

**Default jammer presets** [E. Public data is thin, these are generic classes, not specific systems. Specific Russian comm jammers (e.g., R-330 family, R-934B VHF/UHF air-comm jammer) and US systems (EC-130H Compass Call, ALQ-227, ALQ-249) have no authoritative public power figures.]:

| Preset | Freq coverage | P_out | Antenna gain | Typical B_J | Notes |
|---|---|---|---|---|---|
| Manpack / vehicle VHF-FM spot | 30-88 MHz | 50-500 W | 2-6 dBi | 25 kHz | Ground-to-ground; two-ray + terrain dominate |
| Ground VHF/UHF air-comm jammer | 100-400 MHz | 0.5-2 kW per channel, several channels | 8-12 dBi (sector) | 25 kHz spot / 1-10 MHz barrage | Targets aircraft receivers; LOS to ~200+ km |
| Airborne stand-off comm jammer | 20-1000 MHz | 1-10 kW aggregate | 6-15 dBi | spot / responsive | High altitude, so huge footprint |
| Escort pod low-band | 64 MHz-2 GHz (low-band) | kW-class ERP | array | spot/responsive | ERP not public |
| Expendable / UAV jammer | 30-1000 MHz | 1-20 W | 0-3 dBi | spot/barrage | Close-in; very high J at short range |

**Receiver presets:**

| Radio | Mode | B_R | NF | Squelch | AGC τ_a / τ_d | Capture ratio | Phase noise L(25 kHz) | ACS (±25 kHz) | P1dB in |
|---|---|---|---|---|---|---|---|---|---|
| VHF AM 25 kHz | AM | 16 kHz | 8 dB | carrier power | 5 ms / 300 ms | n/a | −115 dBc/Hz | 60 dB | −15 dBm |
| VHF AM 8.33 | AM | 6 kHz | 8 dB | carrier | 5 / 300 ms | n/a | −120 | 60 dB (at ±8.33) | −15 |
| UHF AM | AM | 16 kHz | 8 dB | carrier | 5 / 300 ms | n/a | −115 | 60 dB | −15 |
| VHF FM tactical | FM ±5-6.5 kHz | 16-20 kHz | 8 dB | noise or 150 Hz tone | (limiter) | 3 dB | −115 | 60 dB | −10 |

All [E], ED-23C / DO-186B / MIL-STD-188-242 are the governing standards. Exact receiver performance is radio-specific.

**Required J/S summary (predetection, in-channel, 100% duty unless stated):**

| Target | Noise jamming | CW / tone | Voice / music | Notes |
|---|---|---|---|---|
| AM voice | degrade −5 dB, deny 0 to +6 dB | audible at −20 dB; deny ≈ +6 to +10 dB (AGC capture + garbling) | audible at any level; deny ≈ +3 dB | No capture |
| FM voice | degrade −10 dB (threshold clicks), deny ≥ 0 dB | below −CR (−3 dB): faint whistle; **≥ +3 dB: total, silent capture** | ≥ +3 dB: jammer replaces friendly | All-or-nothing |
| Digital voice (uncoded) | ≈ 0 dB at 20-33% duty | same | n/a | Adamy rule |
| FH (SINCGARS/Have Quick) | J/S + PG (~34-38 dB) for barrage; partial-band optimum reduces this | — | follower: as non-FH if T_react < ~0.5·T_d | Torrieri |

---

## 7. Where public data is thin or classified

1. **Specific threat jammer ERP, gain, waveforms and look-through timing.** Not public. Use generic presets and expose them as mission-editor parameters.
2. **Have Quick / SINCGARS hopping details.** Hop rates and patterns are only loosely public. The PG approximation is adequate for gameplay.
3. **Aircraft antenna patterns.** Exist in proprietary EMC studies. Use a simple shadowing model.
4. **ED-23C / DO-186B receiver specs.** Paywalled. The values above are from memory and of the right order; verify before quoting as fact.
5. **Doctrinal "required J/S" values.** Public manuals give qualitative descriptions only. The numeric thresholds here are derived from modulation theory (Section 2) and match the textbook rules of thumb (Adamy, Poisel), but their uncertainty is about ±3-5 dB.
6. **The "rotary/bubble" sound name.** Folklore; not in FM 24-33's list. The FM 24-33 list (random noise, stepped tones, spark, gulls, random pulse, wobbler, recorded sounds, preamble) is the authoritative public taxonomy.

---

## References

Textbooks:
- D. Adamy, *EW 101: A First Course in Electronic Warfare* (Artech, 2001), ch. 9 "Communications Jamming"; *EW 102* (2004); *EW 103: Tactical Battlefield Communications EW* (2009). Comm J/S, two-ray, Fresnel distance, duty-cycle rules.
- R. Poisel, *Modern Communications Jamming Principles and Techniques*, 2nd ed. (Artech, 2011). Jamming effectiveness by modulation, partial-band, follower.
- D. Torrieri, *Principles of Spread-Spectrum Communication Systems*, 4th ed. (Springer, 2018). FSK under partial-band jamming, follower geometry.
- D. C. Schleher, *Electronic Warfare in the Information Age* (Artech, 1999).
- M. K. Simon, J. Omura, R. Scholtz, B. Levitt, *Spread Spectrum Communications Handbook* (McGraw-Hill).
- H. Taub & D. Schilling, *Principles of Communication Systems*. AM/FM noise performance, FM threshold formula.
- S. O. Rice, "Noise in FM receivers," in *Time Series Analysis*, 1963.
- K. Leentvaar & J. Flint, "The capture effect in FM receivers," *IEEE Trans. Commun.* 24(5):531-539, 1976.

Military publications:
- ATP 3-12.3 *Electromagnetic Warfare Techniques* (Jan 2023): https://irp.fas.org/doddir/army/atp3-12-3.pdf
- FM 24-33 *Communications Techniques: ECCM* (1990), ch. 3 jamming signals and recognition: https://irp.fas.org/doddir/army/fm24-33/fm243_4.htm
- FM 34-45 ch. 6 (spot, barrage, sweep definitions): https://www.globalsecurity.org/intell/library/policy/army/fm/34-45/ch6.htm
- MIL-STD-1472 (voice intelligibility criteria: AI 0.3/0.5/0.7 ↔ MRT 75/91/97%).

Propagation:
- ITU-R P.525 (free space); P.526 (diffraction); P.528 (aeronautical propagation curves).

Speech and hearing:
- ANSI S3.5-1997 (R2017) Speech Intelligibility Index; K. Kryter, JASA 34:1689 (1962); N. R. French & J. C. Steinberg, JASA 19:90 (1947).
- R. Plomp & A. Mimpen, *Audiology* 18:43 (1979).
- G. A. Miller & J. C. R. Licklider, JASA 22:167 (1950).

Aviation standards:
- ICAO Annex 10 Vol III; EUROCAE ED-23C / RTCA DO-186B.

Web summaries consulted:
- USAFA ECE-495 EW notes: https://usafa-ece.github.io/ece-495-ew/block02/L17_ElectromagneticAttack_Reading.html
- JED, "Jamming efficacy of communications links": https://www.jedonline.com/2022/08/17/jamming-efficacy-of-communications-links/
- RF Cafe J/S: https://rfcafe.com/references/electrical/ew-radar-handbook/jamming-to-signal-ratio-constant-power.htm
- rfessentials capture effect: https://rfessentials.com/resources/rf-glossary/capture-effect/
- Radiomuseum FM capture ratio: https://radiomuseum.org/forum/fm_capture_ratio.html
- Articulation index calculator: https://metricgate.com/docs/articulation-index/
