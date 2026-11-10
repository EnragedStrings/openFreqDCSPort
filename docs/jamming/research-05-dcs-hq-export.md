# Research 05: HAVE QUICK / SINCGARS state in DCS modules, and how Export.lua can read it

Status: research only (no code changed). Date: 2026-11-09.
Scope: which DCS aircraft simulate HAVE QUICK (HQ I/II) or SINCGARS, which cockpit arguments and
display indications show that state, where (if anywhere) DCS stores WOD/TOD/net data, and a
capture plan per module for OpenFreq.

## 0. Caveats about the evidence

- **DCS install inspected:** `D:\DCS World OpenBeta` (2.9.x). Modules present in
  `Mods\aircraft`: A-10A, A-10C, A-10C_2, AH-64D, AJS37, AV8BNA, Bf-109K-4, C130J, CH-47F, F-15C,
  F-15E, F-16C, F-4E, F-5E, F14, Ka-50, L-39C, MiG-21bis, MiG-29, P-51D, Su-25A/T, Su-27, Su-33,
  TF-51D, Uh-1H.
  **Not installed, so this document relies on public sources only for them:** F/A-18C (only its
  CoreMods DTC definitions are present), M-2000C, OH-58D, JF-17, C-101, Mi-8.
- **The A-10C II install has been modified locally.** Many A-10C_2 Lua files carry `-- 163rd vFS`
  comments: the A10TGPDonorSPI plugin v0.30, plus the "COMM 2" second ARC-210 (RT-2, device 78) and
  the UFC ECCM/COMSEC buttons. The parts that matter here:
  - `Mods\aircraft\A-10C_2\Cockpit\Scripts\ARC_210\Device\ARC_210_param.lua` lines 46-65. The
    `eccm_channels` table, `TOD_recieved = false`, `SG_time_recieved = false` and
    `GPS_receive_time` were written or edited by that plugin. The comments say: "ED defaults TOD to
    'received' when the key is absent", "Type strings (ED loader): HQI, HQII, SG, SG_CS, SG_CUE –
    plain HQ is NO FILL", and "the DTC [HQ] section (cartridge.dtc, ch1..ch27) overrides it".
  - `...\ARC_210\Indicator\arc_210_useful_definitions.lua` (AddRT, AddTextLabel parent) and
    `arc_210_page_HQ_MENU_WOD_LOAD_PAGE.lua` line 58 (WOD date shown at every step).
  - `clickabledata.lua` lines 721-726 (UFC ECCM/COMSEC/IDM hints), 1341 (RT SELECT).

  **I could not find a stock copy of these files** (the `D:\DCS Mods\GBU39_*` backups do not
  contain them). Element names that come from ED's page Lua (for example `Net_ID_left_part` and
  `active_eccm_channel_HQI_status`) are almost certainly stock. A stock install may lack the
  `eccm_channels` values and the TOD keys shown here. **Verify on a clean install before relying
  on them for users without the 163rd plugin.**
- The C++ ECCM logic lives in the module DLLs. String searches of `A-10C_2\bin\*.dll` found
  nothing useful (the binaries are probably packed). Behaviour described below is **inferred from
  the page Lua plus the param comments, not from the DLL.**

## 1. Matrix: real aircraft vs what DCS simulates

| Module | Real radios with HQ / SINCGARS | What DCS simulates (evidence) | Readable from Export |
|---|---|---|---|
| **A-10C II** | ARC-210 (HQ I/II and SINCGARS), ARC-164 (HQ II), ARC-186 VHF-FM (no SINCGARS) | **ARC-210 ECCM is simulated**: an ECCM page with HQI/HQII/SG channel types, net ID, a fill/time status line, SG cold start/cue, plus HQ menus (OPER DATE, VERIFY WOD/MWOD, WOD LOAD, WOD ERASE, HQ II FMT), GPS time load and SG hopset send/recv/load time. ARC-164: the 100 MHz knob has an "A" position, but the manual documents no HQ function (T-Tone: "No function"). | ARC-210: **yes, via `list_indication(18)` while the ECCM page is shown.** ARC-164: only the knob position. |
| A-10C (legacy) | ARC-164 (HQ II), ARC-186 | No ARC-210. ARC-164 is identical to the A-10C II's (args 161-173, 735; "EGI HQ TOD" switch arg 708). No HQ logic is documented. | Knob position only |
| F-16C | ARC-164 (HQ), ARC-222 (VHF) | The DED COM1 page has "UHF"/" HQ" and "TOD" text, but **without controllers they are static labels** (`F-16C\Cockpit\Scripts\Displays\Common\indicator\DED\Pages\COM\COM_1.lua` lines 18 and 32). The manual says the ARC-164 is "capable of HAVE QUICK" but describes no HQ procedure. The backup panel 100 MHz knob (arg 411) has 3 positions. | No (knob only) |
| F-15E | ARC-164 (R1), ARC-210 (R2) with HQ, AJ | Manual v1.7 §5.7: "HQ (PB 3): Have Quick system. Not modelled in DCS." "HQ TOD (PB 8) … Not modelled." "AJ PROGRAM … Not modelled." | No |
| AH-64D | ARC-164 HQ I/II plus KY-58; 2× ARC-201D SINCGARS | Manual: "TUNE – FH. Not implemented", "TUNE – FH/M. Not implemented", "HQ NET> … (N/I)", "HOPSET … (N/I)", "MWOD. Not implemented", UHF page "(N/I)". `MPD_COM_UHF_WOD.lua` is a placeholder ("COMM UHF WOD PAGE"). | No (only single-channel presets) |
| CH-47F | ARC-164 HQ (U2), ARC-201D SINCGARS (F1/F2) | CDU pages exist: `CDU\indicator\Pages\COMM\CDU_PAGE_COMM_U2_HQ_PRESETS_1..4.lua`, `..._U2_HQll_SETUP.lua`, `..._U2_MWOD_DAY.lua` (static "MWOD DAY 01"), `CDU_PAGE_F1_SINCGARS.lua` (empty apart from its title). HQ presets show callsign and frequency controllers. The Early Access guide does not document HQ or FH. The main COMM page (`CDU_PAGE_COMM.lua` lines 13-52) shows only P/C COMSEC and frequency. **Treat as not simulated (shell pages).** | Not usefully |
| C-130J (3rd party) | ARC-210 | Ships a copy of ED's ARC-210 page set including ECCM and HQ menus (`C130J\Cockpit\Scripts\ARC_210\init.lua` lines 24, 53-57, 137-140). Whether the backend works is unknown. | Possibly the same as the A-10C II. **Untested.** |
| AV-8B | ARC-210 ×2 (HQ/SINCGARS capable in service) | Neither the manual nor the Lua mentions HQ, ECCM or SINCGARS. | No |
| F-14A/B | ARC-159 UHF, ARC-182 V/UHF | No HQ strings anywhere. Heatblur's DTC (`CoreMods\aircraft\F14\DTC`) has no HQ fields. | No |
| F-4E | ARC-164 | No HQ strings in `F-4E\Cockpit\Scripts\UHF_Radio`. | No |
| F-5E | ARC-164 | The 100 MHz knob (arg 327) has 4 positions (T?/2/3/A; the manual text drops the first glyph, which suggests "T"). No HQ logic is documented. | Knob position only |
| UH-1H | ARC-51BX, ARC-131 FM | No HQ or SINCGARS | n/a |
| F/A-18C (not installed) | ARC-210 ×2 | Public sources and the community view: no HQ or ECCM. CoreMods DTC `FA-18C\DTC\COMM\*` has no HQ fields. | No |
| M-2000C, OH-58D, JF-17, C-101 (not installed) | OH-58D: ARC-201D SINCGARS, ARC-164 HQ | No public evidence of HQ or SINCGARS simulation. **Unverified.** | Assume no |
| Ka-50, Mi-8, L-39, MiG/Su | none | n/a | n/a |

The DCS DTC system (`MissionEditor\modules\me_DTC.lua`, `CoreMods\aircraft\{AH-64D,CH-47F,F-16C,F14,FA-18C}\DTC\COMM\*`) has **no HQ, WOD, TOD, hopset or net fields**. The only "HQ" hits there are the HQ-7 SAM entries in the threat tables. The ME `panelRadio` for the A-10C II (`CoreMods\aircraft\A-10\A-10C_2.lua` lines 773ff) defines plain frequency presets only, and `AddPropAircraft` has no ECCM properties. **No .miz key carries HQ data.** The only source of HQ fill is the module's own `ARC_210_param.lua` `eccm_channels` table, and in this install the 163rd plugin's `cartridge.dtc [HQ]` section.

## 2. A-10C II ARC-210 in detail (the only real target)

### 2.1 Devices, arguments and indicator
- Devices (`A-10C_2\Cockpit\Scripts\devices.lua`): EGI = 18, UHF_RADIO (ARC-164) = 54, VHF_AM_RADIO (ARC-210 RT-1) = 55, VHF_FM_RADIO (ARC-186) = 56, INTERCOM = 58, KY_58 = 69, ARC_210_RT2 = 78 (163rd mod only).
- ARC-210 RCU args (`clickabledata.lua` lines 1323-1350):
  - 551: master switch, 7 positions (OFF/TR+G/TR/ADF/CHG PRST/TEST/ZERO; OpenFreqDCS.lua line 803).
  - 552: channel knob.
  - **553: secondary switch, 7 positions, step 0.1.** OpenFreq already treats 0.2 as PRST (OpenFreqDCS.lua lines 704-710). By the real RCU layout the positions are probably 0.0 = ECCM MASTER, 0.1 = ECCM, 0.2 = PRST, 0.3 = MAN, 0.4 = MAR, 0.5 = 243, 0.6 = 121. **Verify with the arg scanner.**
  - 554-558: frequency selectors.
  - 564/565/566: TOD SEND / TOD RCV / GPS buttons. Their stock hints say "No function", but the GPS button drives the `GPS_TIME` → `GPS_LOADING_TIME` → `GPS_TIME_RECIEVED` pages, and the 163rd comments say TOD comes from "the GPS button (EGI HQ TD) or TOD RCV".
  - 561-563: FSKs. 569: MENU.
- ARC-164 (`clickabledata.lua` lines 1264-1281):
  - 161: channel.
  - **162: 100 MHz knob, 3 positions: index 0 = "2", 1 = "3", 2 = "A".**
  - 163-166: 10 / 1 / 0.1 / 0.025 MHz knobs.
  - 167: freq mode (MNL/PRESET/GRD). 168: function. 169: T-Tone (spring, -1/0/+1). 170: squelch. 171: volume. 172: test. 173: status. 735: load.
  - **708: "EGI HQ TOD" switch** on the antenna panel (device UHF_RADIO, Button_17).
- Display: `list_indication(18)` is the ARC-210 RCU, as already used by `getArc210DisplayFrequencyHz` (OpenFreqDCS.lua lines 469-510). Each text element appears as `name\nvalue`, with only the current page's elements.

### 2.2 ECCM page fields (`ARC_210\Indicator\arc_210_page_ECCM.lua`)

`ARC_210_Init.lua` line 262 and the `pages[]` list: page `PAGE_ARC_210_ECCM` = subset `ARC_210_ECCM` only. **The KY / CUR_FREQ_MOD subsets are not on this page, so `freq_label_mhz/khz` disappear in ECCM.**

| list_indication key | Values | Meaning |
|---|---|---|
| `active_eccm_channel_index` | `%d` | ECCM channel number (1-27; param index + 1 is likely) |
| `active_eccm_channel_type` | `""`, `HQI`, `HQII`, ` SG` (with a leading space), ` SG`, ` SG` | HQ I / HQ II / SINCGARS (SG, SG cold start, SG cue) |
| `NO FILL` | present when type index 0 | No ECCM data in this channel |
| `active_eccm_channel_HQI_status` | `""`, `NO OPER DAY`, `NO TIME`, `NO WOD`, `NO MWOD`, `NO FMT`, `INVALID NET ID` | **HQ readiness.** Empty means ready (date, TOD, WOD/MWOD and FMT valid). `NO TIME` means TOD not synced. |
| `Net_ID_left_part` / `Net_ID_right_part` | `A%02d` / `%03d` (e.g. `A12` / `300`) | **Active HQ net number** (A12.300) |
| `AM` | label under the net ID | HQ is AM |
| `SG_data` | `F%03d` | SINCGARS net/hopset ID (F001) |
| `top_line_label` | `LE` / `LF%d` | SINCGARS late-entry state (likely sync status) |
| `SG_CS_label_mhz/khz`, `COLD ST` | freq | SG cold-start cue frequency |
| `SG_CUE_top_line_label` | `CUE ON` / `CUE` | SG cue state |
| `comsec_mode`, `comsec_submode`, `ky_submode_label` | `KY-58 VOICE`…, `PT`/`CT`/`CT-TD`, `%d` | Embedded COMSEC (also on the ECCM page) |

HQ menu pages (shown only while the pilot is in MENU):
- `arc_210_page_HQ_MENU_OPER_DATE_PAGE.lua`: `active_oper_date` (`%d`, operational day of month).
- `arc_210_page_HQ_MENU_WOD_LOAD_PAGE.lua`: `WOD_segment` (`%d`), `WOD_segment_data_mhz` / `WOD_segment_data_khz` (the six WOD elements as frequencies), `WOD_segment_date` (`%02d`).
- `arc_210_page_HQ_MENU_VERIFY_WOD_PAGE.lua`: `active_channel` (bound to `oper_date`).
- `arc_210_page_HQ_MENU_HQ_II_FMT_PAGE.lua`: `HQII_segment`, `HQII_segment_data_mhz/khz`.
- `arc_210_page_ECCM_LOAD_TIME.lua`: `SG_time` (`%02d.%02d`).
- `arc_210_page_GPS_TIME_RECIEVED.lua`: label "GPS TIME RECIEVED".

**WOD contents can therefore be read only while the pilot is paging through WOD LOAD or VERIFY.** They cannot be captured continuously. The readiness flag (`active_eccm_channel_HQI_status`) is the practical signal.

### 2.3 Correction to the current script

`getArc210ComsecState` (OpenFreqDCS.lua lines 419-439) sets `hqOn = true` when `comsec_submode == "CT-TD"`, copying DCS-SRS. In the page Lua, `comsec_submode` is a **KY/COMSEC** setting with formats `PT`/`CT`/`CT-TD` (`arc_210_page_KY.lua` lines 16-22). CT-TD is the KY-58 cipher-text *time-delay* (long preamble) mode, not HAVE QUICK. **This is a misread.** HQ is on when the RCU is on the ECCM page with `active_eccm_channel_type` in {`HQI`,`HQII`}. My reading of TD is moderately confident and should be checked against an ARC-210 operator manual. Either way, the ECCM page fields are the authoritative signal.

### 2.4 Configured fill (param file)
- `eccm_channels[0..26]` (`ARC_210_param.lua` lines 53-65) holds `{type, left_part, right_part}` for HQ and `{type="SG", SG_data=n}` for SINCGARS. In this install:
  - [0] HQI A12.300, [1] A12.325, [2] A12.350, [3] A20.400, [4] A21.500, [5] A30.600.
  - [6] HQII A00.025, [7] HQII A00.050.
  - [8] SG F001, [25] SG_CS, [26] SG_CUE.
- `TOD_recieved` and `SG_time_recieved` set whether time is pre-synced at spawn. The comments say ED treats a missing key as received.
- The export environment has `io`/`lfs`. OpenFreq *could* read this file as text and parse `eccm_channels` to show the configured net plan. This is the static fill, not live state, and in stock installs (and in IC-protected multiplayer) the file may differ per client. **Recommend not depending on it.** Use it at most as a UI hint.

### 2.5 Things to probe in-sim (unknowns)
1. Does `GetDevice(55):get_modulation()` change in ECCM? The current code maps HQ to modulation 4 by hand.
2. Does `GetDevice(55):get_frequency()` return the net-entry pseudo-frequency or the last single-channel frequency while in ECCM?
3. Enumerate device methods once: `for k in pairs(getmetatable(GetDevice(55)).__index) do ... end`. ED sometimes exposes extra getters.
4. Confirm the arg 553 positions and the arg 162 = "A" index with the existing arg scanner (`scanArgumentsOnce`, line 1859).
5. Check whether parent-only `ceSimple` elements (`HQ_symb`, `net_ID_symbology`, `SG_symb`) appear as keys. If they do, they are clean booleans.

## 3. Capture strategy per module

| Module | HQ on | Net number | WOD / MWOD | TOD sync | SINCGARS | Approach |
|---|---|---|---|---|---|---|
| **A-10C II ARC-210** | `list_indication(18)` has `Net_ID_left_part` or `active_eccm_channel_type ∈ {HQI,HQII}`; arg 553 ∈ {ECCM, ECCM MSTR} as a cross-check | `Net_ID_left_part..'.'..Net_ID_right_part` → e.g. `A12.300`; also `active_eccm_channel_index` | Readiness only: `active_eccm_channel_HQI_status` not in {`NO WOD`,`NO MWOD`,`NO FMT`,`NO OPER DAY`}. WOD value: **OpenFreq UI / server-side net plan**, keyed by mission date. | `NO TIME` → unsynced; else synced | type ` SG` → `SG_data` F-number, `top_line_label` LE/LFn; cold start / cue → SG_CS freq | Cockpit read + OpenFreq-held keys |
| A-10C II / A-10C ARC-164 | arg 162 index 2 ("A") **as pilot intent only** (DCS does nothing with it) | args 163-166 → `Axx.xxx` (A + 10 MHz/1 MHz digits + .1 MHz + .025 step) in MNL mode | OpenFreq UI | Assume synced if the EGI HQ TOD switch (arg 708) is ON and the aircraft has GPS; else manual | n/a | Optional "virtual HQ": the knob selects a net, OpenFreq supplies the WOD |
| F-5E ARC-164 | arg 327 position "A" (4-position knob: confirm index order) | args 328-331 | UI | UI / assume | n/a | Same virtual-HQ option |
| F-16C, F-4E | Backup UHF panel 100 MHz knob (F-16 arg 411, 3 positions; check for "A") | knob digits | UI | UI | n/a | Virtual HQ via knob if "A" exists, else UI toggle |
| F-15E, AH-64D, CH-47F, AV-8B, F/A-18C, F-14, others | Not simulated | — | — | — | — | **OpenFreq UI only**: per-radio "ECCM" toggle, net number, SG net |
| C-130J | Try the same `list_indication` parse as the A-10C II (find the indicator id by scanning) | same | same | same | same | Experimental |

### TOD modelling recommendation
- HQ needs a common time base, not real UTC. Use **DCS mission time**: absolute sim time = mission start time (`LoGetMissionStartTime()`) + `LoGetModelTime()`. The multiplayer server keeps it common across clients, which mirrors GPS-derived TOD.
- Export.lua has no mission *date*. To get the MWOD day, add a Hooks script (`Saved Games\DCS\Scripts\Hooks`) that reads `DCS.getCurrentMission().mission.date` (Day/Month/Year) and passes it to OpenFreq. Alternatively, OpenFreq can ask the user or server.
- Treat TOD as a per-radio boolean `todValid`:
  - A-10C II: from the `NO TIME` status.
  - Other HQ-capable aircraft: assume `true` when the aircraft has GPS (the A-10C EGI HQ TOD switch arg 708 can gate it), otherwise UI-controlled.
  - Optionally add a configurable TOD offset to model a lost or emergency-started clock (the real ARC-164 "T" position starts the TOD clock locally). Two radios hop together only if their WOD, net and TOD (within tolerance) match.
- SINCGARS sync time (`SG_time`, `SG_time_recieved`) can be treated the same way. ARC-210 `top_line_label` LE/LFn indicates late-entry state.

## 4. Suggested packet fields
Per radio: `eccm = {mode = "off"|"hq1"|"hq2"|"sg"|"sg_cs"|"sg_cue", eccmChannel, hqNet = "A12.300", hqStatus = ""|"NO TIME"|…, todValid, sgNet = "F001", sgLateEntry = "LE"|"LF3", source = "cockpit"|"knob"|"ui"}`. The existing `hqOn` can be derived from `mode`. WOD/MWOD material never comes from DCS. It belongs in the OpenFreq server's net plan, keyed by mission date.

## 5. Sources
- Local: the module Lua files cited above; manuals in `Mods\aircraft\*\Doc` (A-10C II Flight Manual EN §ARC-164 pp.168-170; F-15E Manual v1.7 §5.7; AH-64D Flight Manual EN COM chapter and DTU section; CH-47F EA Guide; F-16C EA Guide §AN/ARC-164; F-5E Flight Manual §UHF).
- HAVE QUICK background: https://en.wikipedia.org/wiki/Have_Quick
- ARC-164 HQ II operator procedures (WOD in presets 20-15, MWOD load at 220.025, the "T" position = emergency TOD start, A-3-2-T switch): TM 1-1520-238-10, https://apachehelicopter.tpub.com/TM-1-1520-238-10/css/TM-1-1520-238-10_166.htm (pages 162-174); TM 1-1510-223-10, https://aviationandaccessories.tpub.com/TM-1-1510-223-10/css/TM-1-1510-223-10_111.htm
- DCS changelog (ARC-210 named presets in the ME, 2.8.5): https://www.digitalcombatsimulator.com/en/news/changelog/openbeta/2.8.5.40170/
- No ED forum or changelog post documenting ARC-210 ECCM or HQ fill was found in a quick search. **This gap should be closed before shipping**, especially to confirm the stock `eccm_channels` content.
