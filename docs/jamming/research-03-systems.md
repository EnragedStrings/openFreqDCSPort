# 03 — Real-world communications jammers: systems survey and simulator presets

Scope: open-source survey of communications jammers relevant to the OpenFreq bands
(HF 2-30 MHz, VHF-FM 30-88 MHz, VHF-AM 118-156 MHz, UHF-AM 225-400 MHz, UHF SATCOM ~240-320 MHz),
plus a preset table for direct use and notes on doctrinal employment for a flight-sim context.

Confidence tags used throughout:
- **[S]** sourced: figure appears in a manufacturer page, official/army publication, or a reasonably primary reference (cited).
- **[S2]** secondary: figure appears only in a secondary database/press report (armyrecognition, armsnet, COGINT report, StrategyPage, Wikipedia without a primary ref). Plausible but unverified.
- **[E]** engineering estimate by the author (derived from typical antenna gains, chassis power, physics). Use as a sim default, not as fact.

Bottom line up front: **power, ERP, channel counts and antenna gains for almost every comms jammer are not public.**
The only hard numbers in open sources are a handful of transmitter powers (R-330B 1 kW, R-934U >=500 W,
Pelena-6 >=60 W, AN/ULQ-19 250 W, AN/TLQ-17A 550 W, AN/ALQ-151(V)2 1 kW PEP) and frequency ranges.
Everything ERP-related in the preset table is therefore [E], anchored to those powers.

---

## 1. Russian / Soviet-lineage systems

### 1.1 R-934B / R-934BMV / R-934U "Sinitsa" — VHF/UHF aviation-band jammer (the key system)
- **Role:** automated jamming of air-ground and air-air voice/data links in the aviation VHF/UHF bands, plus ground VHF/UHF links. Ukrainian reporting describes R-934B as jamming "aviation VHF radio communications, tactical aircraft guidance systems, and ground-based fixed and mobile radio links" [S2, militarnyi.com]. Military-Review describes the main task as "disrupting guidance of enemy strike or fighter aircraft" [S2]. The R-934U variant is now marketed/used largely against UAV command and telemetry links [S2, www1.ru].
- **Platform:** R-934B on Ural-4320-31 6x6 truck with K1-4320 shelter or MT-LBu tracked chassis; 16 kW towed generator [S, ausairpower APA-REB-Systems]. R-934BMV is the MT-LBu station within Borisoglebsk-2 [S, militarnyi; armsnet S2].
- **Frequency:** R-934U/"Sinitsa" receiver 100-400 MHz; transmitter in three sub-bands 100-150, 150-220, 220-400 MHz; 1 kHz synthesiser step; ±4 kHz carrier accuracy [S2, www1.ru 2024/2025 TTX listings]. That covers both VHF-AM 118-156 and UHF-AM 225-400 (and UHF SATCOM 240-320). Rosoboronexport's export "R-934B Configuration 28" page gives 30-3000 MHz [S, roe.ru via search extract] — this is an export/modernised configuration and should not be taken as the Soviet/legacy R-934B band. Belarus' derivative R-934UM2 "Groza-6": 30-3000 MHz (up to 6000 MHz in some configs), up to 28 fixed-frequency links jammed simultaneously [S2, armyrecognition / vpk.gov.by].
- **Output power:** >=500 W [S2, R-934U TTX]. Antenna gain unknown; a 3-sub-band transmitter suggests three PA/antenna chains [E]. Antenna: mast-mounted, 360° coverage claimed [S2]; assume log-periodic / biconical array 3-8 dBi on 10-15 m mast [E].
- **Range:** "up to 250 km" [S2] — consistent with radio horizon to aircraft at altitude (10 m mast vs 10 km altitude gives ~425 km 4/3-earth horizon). This is a line-of-sight claim against airborne receivers, not a guarantee of effect.
- **Frequency hoppers:** Rosoboronexport says R-934B Config 28 handles fixed-frequency and FH emitters "up to 1000 hops/s" [S via search extract of roe.ru; could not open page directly — TLS error]. Treat as a marketing claim.
- **Simultaneous targets:** not published for R-934B [E: 3 true simultaneous, one per sub-band; up to ~6-8 quasi-simultaneous by time-sharing, analogous to R-330B's 6].

### 1.2 R-330B / R-330BM / R-330BMV "Mandat-B" — VHF (tactical FM) jammer
- **Role:** detection, DF and jamming of tactical VHF (combat-net) radio. Part of R-330M "Mandat" and Borisoglebsk-2.
- **Platform:** MT-LBu (B/BM/BMV), Ural/KamAZ (T variant) [S, APA].
- **Frequency:** 30-100 MHz (B/T); R-330U 30-60 MHz jamming [S, APA; ru.wikipedia].
- **Power:** 1.0 kW transmitter [S, APA; ru.wikipedia].
- **Channels:** "up to 6 targets quasi-simultaneously" [S, APA quoting Russian TTX].
- **Deployment:** 40-70 min [S]. Remote control to ~12 km (whip) / ~30 km (directional) via R-415V relays [S, ru.wikipedia].
- **Relevance:** VHF-FM 30-88 MHz band (helicopter/ground FM nets, JTAC FM). [E] mast 10-16 m, LPDA ~6 dBi.

### 1.3 R-378A / R-378B / R-378BMV — HF jammer
- **Role:** automated HF comms jamming station (Mandat / Borisoglebsk-2). R-378AM "automated HF communications jammer (upgraded)" [S2].
- **Frequency:** HF, commonly given as 1.5-30 MHz [S2/E — consistent with all HF jammer naming; no numeric primary source found].
- **Power:** not found. [E] ~1 kW per transmitter, 1-4 transmitters (Soviet HF jammers of this class were kW-scale).
- **Relevance:** HF only (long-range air reporting, strategic nets).

### 1.4 R-325U / R-325UMV
- Listed as a jamming station in Borisoglebsk-2 (2 per complex) [S2, armsnet; militarnyi]. **No band or power found in open sources.** Some Ukrainian descriptions say the complex jams "tactical command and control including satellite" [S2]. Low confidence: treat as a VHF/UHF station complementing R-934BMV. Recommend no dedicated preset, or alias to R-934BMV with reduced power.

### 1.5 Borisoglebsk-2 (complex)
- Composition: 1x R-330KMV C2, 2x R-378BMV (HF), 2x R-330BMV (VHF), 2x R-934BMV (VHF/UHF), 2x R-325UMV [S2, armsnet; militarnyi]. Signal analysis 3 MHz-3 GHz; emitter detection >=30 km [S2]. Claimed FH capability [S2]. All MT-LBu.
- **Sim note:** a Borisoglebsk-2 "battery" = one HF + one VHF-FM + one VHF/UHF-AM emitter each with its own presets, controlled as a group.

### 1.6 R-330Zh Zhitel — NOT an air-band jammer (verified)
- Recon 100-2000 MHz [S, Wikipedia + roe.ru TTX quoted in multiple sources]. **Jamming bands: 800-960 MHz (GSM), 1227.6 / 1575.42 MHz (GPS L2/L1), 1500-1700 and 1700-1900 MHz (Inmarsat/Iridium/Thuraya, DCS-1800)** [S2, Russian TTX extracts]. Range 20-25 km vs ground users, >=50 km vs airborne users (i.e., GNSS receivers in aircraft) [S2]. Ural-43203 / KamAZ-43114 truck + antenna trailer [S].
- **Sim relevance:** none for the OpenFreq voice bands; relevant only if GNSS degradation is ever modelled.

### 1.7 Pelena-6 — light comms jammer
- 20-1000 MHz, >=60 W output, <=450 W draw, BTR-70/BTR-80 chassis [S, APA]. Good model for a small vehicle jammer covering all OpenFreq VHF/UHF bands at low power.

### 1.8 Leer-2 / Leer-3
- **Leer-3 (RB-341V):** Orlan-10 UAV-borne GSM jammer/IMSI catcher; 900-1800 MHz class (sources disagree: 0.935-1.785 GHz COGINT vs 900-2500 MHz Military-Review) [S2]. Range claims 6 km to 100+ km [S2, inconsistent]. Not a voice-band jammer for OpenFreq.
- **Leer-2:** Tigr-M based ("85Ya6"?), 20-2700 MHz per a single COGINT entry [S2, single source]. Power unknown [E: 100-500 W]. Could serve as a light wideband preset.

### 1.9 Palantin
- Operational-tactical network-centric EW system; HF 3-30 MHz and VHF 30-300 MHz; "radio-communication suppression range 1000 km" [S2, armsnet citing COGINT June 2023]. The 1000 km figure is only plausible for HF skywave. Janes: "discretely target RF objects, conduct bandwidth and spot jamming" [S2 via Janes summary].

### 1.10 Tirada-2 (and -2S)
- Satellite uplink jammer; COGINT lists 3-14 GHz [S2]. The Space Review notes variants specialised for "decimetre" (UHF) vs "centimetre" (SHF) bands [S2]. **A UHF Tirada variant would be the Russian analogue of a UHF SATCOM jammer.** Uplink jamming of a bent-pipe UHF transponder denies the channel across the whole satellite footprint, which is why UHF SATCOM is notoriously vulnerable (cf. well-documented pirate use of US UFO UHF transponders).
- **Sim note:** model UHF SATCOM jamming as a *channel-wide* (not geometry-based) effect gated on the jammer being in the satellite footprint.

### 1.11 Others (not comms jammers in our bands — note only)
- **Krasukha-2/-4:** radar jammers (S-band vs AWACS, X/Ku vs airborne/SAR radars). Not comms.
- **Moskva-1:** passive radar-recon and EW C2 node; not a comms jammer.
- **Pole-21, Svet-KU, Tobol:** GNSS/navigation (Svet-KU also described as cellular recon) — not voice comms.
- **Rychag-AV (Mi-8MTPR-1):** L187A, 5.1-11 GHz radar jamming, DRFM [S2, TWZ quoting KNIRTI]. Not comms.
- **Khibiny / SAP-518:** airborne self-protection radar jammers. Not comms.
- **SPN-2/-4/-30/-40:** ground jammers vs airborne radars (X/Ku). Not comms.
- **Topol-E, Pelena-1:** ground jammers vs AEW&C radar. Not comms — but note doctrinal emphasis on attacking AWACS.
- **Il-22PP "Porubshchik":** airborne stand-off jammer targeting "air-defence radars, AEW aircraft, airborne command posts and ground communication centres"; claims strict frequency selectivity so own comms are unaffected [S2]. Bands/power not public. Il-18 based (not in DCS). [E] comms subsystem HF-UHF, multi-kW.
- **R-834 / other Soviet airborne comms jammers:** no reliable open-source data located; the Soviet front-level EW brigade equipment used for aviation comms was the R-934 family (ground) and airborne jammer variants on An-12PP/Mi-8PPA (the latter mainly radar) [E/S2].

---

## 2. Western systems

### 2.1 EC-130H Compass Call → EA-37B Compass Call
- **Role:** the DoD's long-range stand-off airborne C2/communications jammer; also EW/acquisition radar and navigation [S, af.mil fact sheet: Block 35 adds "higher ERP, extended frequency range, DSP"].
- **Frequency:** "open sources note VHF and UHF, 30 MHz-3 GHz" (SPEAR / mission system) [S2, airforce-technology]. Baseline-2 details classified [S].
- **Power / channels / range:** not public. [E] multiple high-power (kW-class) transmitters into directional arrays; per-spot EIRP 40-47 dBW; 8-16 simultaneous spots; stand-off 50-150 km behind FLOT; orbit FL200-250 (EC-130H) / FL400+ (EA-37B, "flies higher and faster" [S2]).
- Final EC-130H flight Sept 2026 alongside EA-37B [S2, Wikipedia].

### 2.2 EA-6B Prowler (ALQ-99 + AN/USQ-113)
- ALQ-99 bands: Band 1 64-150 MHz, Band 2 150-270 MHz, Band 3 270-500 MHz (added ICAP I), up to Band 10 (20 GHz) [S, Wikipedia]. Max power 10.8 kW (older) / 6.8 kW per pod generation [S2, Wikipedia — figure ambiguous].
- AN/USQ-113: stand-alone comms jammer; (V)4 dual-frequency (two simultaneous targets) for ICAP III Block 5 [S, DOT&E reports via globalsecurity]. Band not published [E: ~20-500 MHz using ALQ-99 low-band transmitters/USQ-113 own antennas].

### 2.3 EA-18G Growler (ALQ-99 low band + ALQ-227 CCS; NGJ)
- ALQ-227 Communications Countermeasures Set provides comms receiving/jamming using ALQ-99 low-band pod transmitters [S2]. NGJ-Mid Band is AN/ALQ-249 (2-6 GHz) [S, NAVAIR/JED]; **NGJ-Low Band is a separate pod (no public band, "lower frequency bands"), EOC ~2029** [S2]. Correct the brief's "ALQ-249 NGJ-LB" — ALQ-249 is mid-band.
- [E] Per-spot EIRP low band 35-40 dBW, 2-4 simultaneous; NGJ-LB AESA likely higher.

### 2.4 AN/ALQ-151(V)2 "Quick Fix" (EH-60A) / AN/TLQ-17A(V)2
- Helicopter-mounted comms intercept/DF/jam; **1.5-80 MHz, 1 kW PEP** [S, US Army TB 43-0133]. Retired. Good analogue for helicopter-borne VHF-FM/HF jammer.

### 2.5 AN/TLQ-17A "Traffic Jam" (and (V)4 "Sandcrab")
- Modular ground/air comms jammer; **1.5-20 MHz at 550 W max; 20-80 MHz** (power not captured) [S, TB 43-0133]; up to 255 preset frequencies [S2, militaryperiscope]. (V)3 in S-250 shelter on HMMWV [S, FAS].

### 2.6 AN/ULQ-19(V) (RACJAM)
- Jeep/vehicle VHF jammer; **20-80 MHz, 250 W** [S, TB 43-0133]; 16 pre-selected target channels, reacts within ~1 s [S2, militaryperiscope]. RACJAM family 100 W-2 kW [S2].

### 2.7 AN/MLQ-34 TACJAM
- Divisional high-power VHF jammer on M1015 tracked carrier; retired [S2]. Band commonly given as VHF 20-80 MHz [E/S2]; power commonly cited as multi-kW (three transmitters) [E, unverified].

### 2.8 Modern US Army: Prophet (SIGINT only), TLS-BCT / TLS-EAB (Terrestrial Layer System, vehicle EA+SIGINT), MFEW-Air Large (pod on MQ-1C) — specs classified. [E] use generic "modern vehicle EA" preset.

### 2.9 Chinese / Iranian (brief)
- **China:** Y-8G / Y-9G (GX-series) stand-off comms/radar jamming aircraft, J-16D escort jammer, ground "comms countermeasure" brigades with truck-mounted VHF/UHF jammers — no public specs [E: use Compass Call-like and R-934-like presets].
- **Iran:** "Cobra V8" ground EW system, claimed 200-250 km range against aircraft/radar [S2, IRGC press claims]; treat as R-934-class preset.

---

## 3. DCS World / Falcon BMS context

### 3.1 Existing comm-jamming in the ecosystem
- **DCS-SRS does not implement jamming** (no jammer entity, no noise injection; only distance/LOS/terrain options). No mainstream mod implements *voice* jamming.
- DCS mission scripts that do "jamming" are all **radar/SAM** effects: "Electronic War Jamming Script v2.0" (MIST, `EWJamming("Prowler1")`, emulates defensive/offensive jamming by setting SAM ROE/alarm state), "EA-18G-style Electronic Warfare Script" (spot jamming, capacity system, F10 menu; MOOSE variant adding SRS TTS narration) [S, forum.dcs.world threads 151381, 378351, 381375]. None model receivers, J/S or comms.
- MOOSE `Sound.SRS` / `MSRS` can push audio/TTS onto SRS frequencies — technically could inject noise on a frequency but no one has published a jamming implementation [S, MOOSE docs].
- Falcon BMS: has radar ECM modelling but, to my knowledge, no comms-jamming of its own IVC/voice system [E — verify with BMS docs].
- Forum thread "Current or planned state of DCS World electromagnetic warfare" (242525) documents community demand; ED has not shipped comms EW.

### 3.2 DCS units usable as jammer proxies (verify exact type names against the DCS unit DB)
No dedicated EW/jammer vehicles exist in core DCS. Plausible chassis proxies:

| Real system | Real chassis | DCS proxy (type name, verify) |
|---|---|---|
| R-934BMV / R-330BMV / R-378BMV / R-325UMV (Borisoglebsk-2) | MT-LBu | `MTLB` (MT-LB) |
| R-934B (truck) / R-330T | Ural-4320-31 / KamAZ-43101 | `Ural-4320-31`, `Ural-4320T`, `KAMAZ Truck` |
| R-330Zh Zhitel | Ural-43203 / KamAZ-43114 | `KAMAZ Truck`, `Ural-4320T` |
| Pelena-6 | BTR-70/80 | `BTR-80` |
| Leer-2 | Tigr-M | `Tigr_233036` |
| Mandat C2 / generic Russian EW command post | — | `Ural-375 PBU`, `SKP-11` (ATC mobile command post), `ZIL-131 KUNG` |
| Mi-8MTPR-1 / Mi-8PPA | Mi-8 | `Mi-8MT` |
| Il-22PP | Il-18 | (none; `An-26B`, `Il-76MD`, or `A-50` as stand-ins) |
| EC-130H | C-130 | `C-130` |
| EA-37B | G550 | (none in core; tag any AI transport) |
| EA-6B / EA-18G | — | community mods only (EA-6B mod; `FA-18C_hornet` tagged) |
| AN/TLQ-17A(V)3 | HMMWV + S-250 | `M1025 HMMWV`, `M1043 HMMWV Armament` |
| EH-60A Quick Fix | UH-60A | `UH-60A` |
| AN/MLQ-34 | M1015 (M548 family) | `M-113` (closest) |
| US/NATO generic C2 | — | `Predator GCS`, `Predator TrojanSpirit`, `HEMTT TFFT` |

Recommendation: match jammers by a **name/tag convention** (e.g. unit or group name contains `JAM:R-934B` or a config file keyed by unit name), not by type, since every proxy chassis also exists as an ordinary vehicle. Tie jammer activity to unit alive/dead state so SEAD/strikes on EW stations matter (as in Ukraine, where Borisoglebsk-2/R-934 stations are high-value targets).

---

## 4. Preset table (for direct simulator use)

Conventions: EIRP in dBW (dB relative to 1 W, referenced to isotropic). Per-spot = P_tx(dBW) + G_ant(dBi) − 2 dB feeder loss, when the transmitter is dedicated to one target. When N spots share one PA, per-spot EIRP drops by 10·log10(N). Barrage "total" = the same PA spreading power across the stated bandwidth; effective in-channel jamming density = total − 10·log10(barrage BW / channel BW) (25 kHz AM, 25 kHz FM, 25/5 kHz SATCOM). All EIRP values are **[E]** unless the transmitter power is [S].

| Preset ID | System | Platform | Band(s) covered (MHz) | Tx power (per PA) | Spot EIRP (dBW) | Barrage total EIRP (dBW) | Max simultaneous spots | Techniques | Antenna height / alt | Typical employment |
|---|---|---|---|---|---|---|---|---|---|---|
| `r934b` | R-934B/BMV "Sinitsa" | Ground, MT-LBu/Ural | 100-400 (VHF-AM, UHF-AM, UHF SATCOM downlink) | 500 W [S2] ×3 sub-band PAs [E] | 33 (500 W + 8 dBi − 2) | 33 per sub-band (spread over sub-band) | 3 true, 6-8 time-shared [E] | Noise (AM/FM noise), tone/sweep, spot, partial-band, follower vs FH (claim ≤1000 hops/s) | 12 m mast | 20-60 km behind FLOT; targets aircraft receivers (GCI/AWACS→fighter, FAC→CAS, tower/approach); LOS to 250 km vs air |
| `r934um2` | R-934UM2 "Groza-6" (modern derivative) | Ground, 6x6 | 30-3000 | 500 W ×4 [E] | 34 | 34 | 28 links quasi-sim [S2] | as above + digital/smart | 15 m | as above, more channels |
| `r330b` | R-330B/BMV "Mandat-B" | Ground, MT-LBu | 30-100 (VHF-FM) | 1 kW [S] | 34 (30 + 6 − 2) | 34 over 30-100 | 1 true, 6 quasi-sim [S] | Noise FM, tone, spot, look-through | 12 m mast | 10-30 km behind FLOT; ground FM nets, helo/JTAC FM; LOS vs low-flying helos |
| `r378` | R-378A/BMV | Ground, MT-LBu/truck | 1.5-30 (HF) | 1 kW [E] | 30 (30 + 2 − 2) | 30 | 2-4 [E] | Noise, tone, spot | whip/dipole, 10 m | HF nets; groundwave ~30-50 km, skywave long range |
| `pelena6` | Pelena-6 | Ground, BTR-80 | 20-1000 | 60 W [S] | 18 (17.8 + 2 − 2) | 18 | 1-2 [E] | Noise, spot | 3 m vehicle whip | Short-range local denial (≤15-20 km vs ground, LOS vs air) |
| `leer2` | Leer-2 | Ground, Tigr-M | 20-2700 [S2 single] | 200 W [E] | 24 | 24 | 2 [E] | Noise, spot, cellular | 5 m | Tactical, mobile |
| `palantin` | Palantin | Ground, truck(s) | 3-300 [S2] | 2 kW [E] | 36 | 36 | 8+ [E] | Network-centric spot + barrage | 15-20 m | Operational depth; HF long range, VHF LOS |
| `tirada_uhf` | Tirada-2 (hypothetical UHF variant) | Ground, fixed/truck | 290-320 uplink (SATCOM) [E] | 1 kW [E] | 50 (30 + 22 dBi dish − 2) | n/a | 1-2 transponders | Uplink noise on transponder | n/a (satellite) | Denies UHF SATCOM channel across footprint |
| `il22pp` | Il-22PP Porubshchik | Air, Il-18 | 30-400 comms (+radar) [E] | 1 kW per spot [E] | 37 | 37 | 4-8 [E] | Spot, barrage, follower | 6-8 km alt | Stand-off 50-150 km; vs AWACS/ground C2 and fighter nets |
| `mi8ppa` | Mi-8 EW variant (generic comms) | Air, Mi-8 | 30-400 [E] | 500 W [E] | 30 | 30 | 2 [E] | Spot, noise | 1-3 km alt | Escort/local, 20-50 km |
| `ec130h` | EC-130H Compass Call | Air, C-130 | 30-3000 [S2] | 1-2 kW per spot [E] | 43 (high-gain arrays) | 45 | 8-16 [E] | Spot, barrage, follower, smart/protocol-aware | FL200-250 | Stand-off 50-150 km behind friendly lines; vs enemy C2, IADS voice, GCI |
| `ea37b` | EA-37B Compass Call | Air, G550 | 30-3000 [E] | 1-2 kW per spot [E] | 45 | 47 | 12-16 [E] | as above | FL400+ | Same, greater stand-off (100-250 km LOS) |
| `ea6b_usq113` | EA-6B (USQ-113 + ALQ-99 B1-3) | Air, EA-6B | 20-500 (ALQ-99 B1-3 64-500 [S]) | ~1 kW per transmitter [E] | 38 | 38 | 2 (USQ-113(V)4 dual) [S] | Spot, follower | FL200-300 | Escort/stand-off 30-100 km |
| `ea18g_lb` | EA-18G (ALQ-99 LB + ALQ-227) | Air, F/A-18F | 64-500 [S for ALQ-99 bands] | ~1 kW [E] | 38 | 38 | 2-4 [E] | Spot, follower, look-through | FL200-350 | Escort / modified escort; 30-100 km |
| `quickfix` | AN/ALQ-151(V)2 Quick Fix | Air, EH-60A | 1.5-80 [S] | 1 kW PEP [S] | 28 (30 + 0 − 2) | 28 | 1-2 [E] | Spot | 0.1-1 km AGL | Div. level, 10-30 km behind FLOT, vs enemy FM/HF nets |
| `tlq17a` | AN/TLQ-17A Traffic Jam | Ground, HMMWV | 1.5-80 [S] | 550 W [S] | 27 | 27 | 1 (255 presets) [S2] | Spot, look-through | 10 m mast | Brigade/div. tactical |
| `ulq19` | AN/ULQ-19 RACJAM | Ground, jeep | 20-80 [S] | 250 W [S] | 24 (24 + 2 − 2) | 24 | 1 (16 presets, ~1 s reaction) [S2] | Spot, auto look-through | 3-5 m | Bde tactical |
| `mlq34` | AN/MLQ-34 TACJAM | Ground, M1015 | 20-80 [S2/E] | 2 kW ×3 [E] | 39 | 39 | 3 [E] | Spot, noise | 10 m | Div. high power |
| `generic_manpack` | Generic man-portable comms jammer | Ground, dismount | 20-500 [E] | 20-50 W [E] | 13-17 | 15 | 1-2 | Barrage/spot | 2 m | Very local (few km vs ground; LOS vs low air) |

Uncertainty notes for the table:
- Antenna gain is the largest unknown. Ground VHF/UHF jammers almost certainly use directional LPDAs or phased sectors (6-10 dBi) for spot work and omnis (0-2 dBi) for 360° barrage — so a single "360° coverage" claim (R-934U) and high gain are somewhat in tension. A simulator could model a 360° omni mode (gain 2 dBi) vs a directional spot mode (gain 8 dBi, ±30° beam).
- Airborne figures (Compass Call, Il-22PP, Growler) are fully estimated. Do not present them to users as "real" numbers.
- Barrage: in practice barrage noise over 100-400 MHz with 500 W gives in-channel density ~27 + 6 − 10·log10(300 MHz/25 kHz) ≈ −8 dBW per 25 kHz channel — about 40 dB weaker than spot. This is why real jammers are spot/follower and why barrage is only effective close-in.

---

## 5. Physics sanity check (why ground→air comms jamming is so effective)

Free-space LOS, equal receiver: J/S (dB) = EIRP_j − EIRP_s − 20·log10(D_j / D_s) (+ receiver antenna pattern and AM/FM processing).
- **GCI/ground controller → fighter:** ground site 100 W into 3 dBi ≈ 23 dBW at D_s = 150 km; R-934B 33 dBW at D_j = 150 km → J/S ≈ +10 dB → AM voice unusable. Jammer still achieves J/S ≈ 0 dB at D_j ≈ 3·D_s.
- **AWACS → fighter:** AWACS radio ~ 20 dBW at 200 km; ground jammer 33 dBW at 100 km → J/S ≈ +19 dB.
- **Wingman ↔ wingman (air-to-air, 5 km apart, 10 W = 10 dBW):** ground jammer 33 dBW at 150 km → J/S = 23 − 29.5 ≈ −6.5 dB → degraded but intelligible. Close formation comms are hard to jam; **burn-through** works.
- **Aircraft → ground controller (uplink):** the ground receiver usually has terrain/horizon shielding from a ground jammer, and the jammer is far away → hard to jam with a ground jammer; airborne jammers (Compass Call) are what attack ground receivers.
- Required J/S (rule of thumb, open literature e.g. Adamy *EW 101/102*, Poisel *Modern Communications Jamming*): AM voice ~0 dB for effective denial, −10 dB already annoying; FM voice needs ~0 to +6 dB due to capture effect, then a sharp cliff; digital/FH needs ~0 dB on a fraction of hops (partial-band, ~10-30% hops lost breaks digital voice).

---

## 6. Doctrinal employment relevant to a flight-sim scenario

**Russian (R-330/Borisoglebsk-2/R-934 family):**
- EW companies/battalions in each brigade/army; stations deployed 10-40 km behind the line of contact in tactical depth, often dispersed and remotely controlled from R-330KMV (up to ~30 km links). Deployment 40-70 min; relocate after emitting due to counter-battery/SEAD threat (proven in Ukraine — many R-934B/BMV stations destroyed by HIMARS/FPV).
- Target selection: R-934 specifically targets the **aviation control loop** — air-ground VHF/UHF between enemy controllers (GCI, FAC/JTAC, airfield ATC) and aircraft, and inter-aircraft data links. Doctrine favours jamming during the critical phases: strike ingress, CAS coordination, intercept vectoring. Own channels are protected with "prohibited frequency lists" (R-934U explicitly builds such lists [S2]).
- Combined with radar jamming (Krasukha, Pelena/Topol vs AWACS) to blind and isolate fighters from GCI/AWACS.
- Typical behaviour: recon first (DF + emitter list), then targeted spot jamming on detected active frequencies, look-through to see if the net moved, then follow.

**US/NATO (Compass Call, Growler, Army EA):**
- Compass Call orbits stand-off behind friendly lines (and increasingly modified escort in permissive airspace), jamming enemy C2 voice/data, IADS reporting nets, and early-warning chatter; it coordinates with strike packages so jamming starts at push and stops when no longer needed (to avoid fratricide and preserve intel).
- Growler: escort/modified escort jamming, comms jamming of IADS C2 during SEAD windows.
- Army ground/heli EA (Quick Fix, TLQ-17, ULQ-19, TLS): division/brigade level vs enemy FM/HF tactical nets.
- Deconfliction: Joint Restricted Frequency List (JRFL) — TABOO / PROTECTED / GUARDED frequencies that friendly jammers must not jam (e.g., guard 121.5/243.0, own GCI). **Simulator implication:** jammer presets should support a "protected frequencies" list so jammers don't hit their own side, and an optional "guard is taboo" flag.

**Scenario patterns for OpenFreq mission makers:**
1. Red ground R-934B 30-50 km behind FLOT jamming Blue's AWACS/GCI and CAS/JTAC frequencies → Blue must switch frequency, use Have Quick/SINCGARS hopping (partial resistance), get closer to the controller (burn-through), or kill the jammer.
2. Blue EC-130H/EA-37B stand-off jamming Red GCI→fighter UHF/VHF control nets → Red fighters lose vectors.
3. Borisoglebsk-2 battery jamming VHF-FM helicopter/JTAC nets near the front.
4. UHF SATCOM uplink jammer denying a SATCOM channel theatre-wide.
5. Recommended jammer state machine: Search (listen) → Detect active frequency (needs target transmission above sensitivity) → React (reaction time 0.5-2 s legacy, <100 ms modern) → Jam with periodic look-through (e.g. 10% duty) → if target silent/moved, return to search. Frequency-hopping radios: follower effectiveness = fraction of dwell jammed = max(0, 1 − (reaction + propagation-geometry delay)/dwell).

---

## 7. Sources consulted
- Air Power Australia, "Russian/Soviet REB systems" — https://ausairpower.net/APA-REB-Systems.html (R-330B/T/U, R-934B platform, Pelena-6, SPN, Topol-E)
- Rosoboronexport R-934B Config 28 / R-330Zh pages — https://roe.ru/... (via search extracts; direct fetch failed on TLS)
- www1.ru, "Sinitsa" TTX — https://www1.ru/en/articles/2025/01/18/sinica-v-umelyx-rukax-na-cto-sposobna-rossiiskaia-stanciia-reb.html
- Militarnyi reports on R-934B/BMV and Borisoglebsk-2 — https://militarnyi.com/en/news/himars-struck-r-934b-sinitsa-automated-jamming-station-in-the-kupiansk-direction/
- Army Recognition, R-934UM2 Groza-6 — https://armyrecognition.com/news/army-news/army-news-2024/belarus-strengthens-electronic-warfare-capacities-with-new-locally-made-r-934um2-vehicles
- armsnet database entries (Borisoglebsk-2 composition, Palantin) — https://armsnet.duckdns.org/equipment/...
- Wikipedia: R-330Zh Zhitel; AN/ALQ-99; EC-130H; ru.wikipedia Borisoglebsk-2
- The War Zone, Mi-8MTPR-1 Rychag-AV — https://twz.com/this-is-russias-electronic-warfare-helicopter-jamming-in-ukraine
- The Space Review (Tirada-2) — https://thespacereview.com/article/4056/1 ; GlobalSecurity Tirada — https://www.globalsecurity.org/space/world/russia/tirade.htm
- US Army TB 43-0133 (via tpub) — AN/TLQ-17A, AN/ULQ-19, AN/ALQ-151 parameters — https://radiosets.tpub.com/TB-43-0133/
- FAS AN/TLQ-17, AN/USQ-113 — https://man.fas.org/dod-101/sys/land/an-tlq-17.htm , https://man.fas.org/dod-101/sys/ac/equip/an-usq-113.htm
- DOT&E EA-6B annual reports (USQ-113(V)4 dual-frequency) — globalsecurity.org mirrors
- USAF EC-130H fact sheet — https://www.af.mil/About-Us/Fact-Sheets/Display/Article/104550/ec-130h-compass-call/
- NAVAIR NGJ — https://www.navair.navy.mil/product/Next-Generation-Jammer
- Military Periscope (ULQ-19/RACJAM, TLQ-17A, MLQ-34) overview snippets
- DCS forums: threads 151381 (EW jamming script v2.0), 378351 and 381375 (EA-18G EW scripts), 242525 (state of EW in DCS); MOOSE Sound.SRS docs
- General theory: D. Adamy, *EW 101/102/103*; R. Poisel, *Modern Communications Jamming Principles and Techniques* (standard references for J/S requirements)

Unable to access: COGINT Russian EW report PDF (403), roe.ru pages (TLS), BSUIR scanned textbook (image-only), Janes (paywall), Military Periscope full entries (paywall).
