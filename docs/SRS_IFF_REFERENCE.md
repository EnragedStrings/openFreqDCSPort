# SRS IFF/Transponder Reference

Research notes on how real [DCS-SRS](https://github.com/ciribob/DCS-SimpleRadioStandalone)
(`ciribob/DCS-SimpleRadioStandalone`, MIT-licensed) implements IFF/transponder simulation, gathered
so a future IFF implementation in OpenFreq doesn't have to re-research this from scratch. SRS's
source is **not vendored in this repo** (same policy as the rest of the SRS-derived code here --
see `OpenFreq.Client.Tests/DcsExportInstallerTests.cs`'s comment on not vendoring SRS's exact hook
text) -- everything below is quoted/paraphrased from the public MIT-licensed source for reference
purposes, fetched directly from GitHub while researching this.

OpenFreq currently has **no IFF implementation at all**. The wire-protocol field already exists
(`OpenFreq.Server/SrsBridge/SrsSyncProtocol.cs`'s `SrsTransponder` on `SrsPlayerRadioInfo.Iff`,
matching SRS's schema so bridged real SRS clients don't choke on a missing field) but it is never
assigned anywhere in the codebase -- it always serializes as the default (`Mode1=-1, Mode3=-1,
Mode4=false, Status=OFF`). No DCS Lua export code, client model, or UI exists for IFF today.

## 1. SRS's data model

`Common/Models/Player/Transponder.cs` (and `TransponderBase.cs`):

```
enum IFFControlMode { COCKPIT = 0, OVERLAY = 1, DISABLED = 2 }
enum IFFStatus       { OFF = 0, NORMAL = 1, IDENT = 2 }

control    IFFControlMode  // where the values below come from
expansion  bool            // IFF expansion module fitted
mode1      int             // -1 = off, else the code
mode2      int             // -1 = off, else the code
mode3      int             // -1 = off, else the code
mode4      bool            // on/off
status     IFFStatus
mic        int
```

`control` is the key field: `COCKPIT` means the values are read live from DCS Export.lua cockpit
arguments (real airframe simulation); `OVERLAY` means the player set them manually through SRS's
own client UI (used for aircraft with no simulated cockpit IFF panel); `DISABLED` means the
airframe has no IFF equipment modeled at all. Confusingly, `COCKPIT == 0` is also the default value
of an uninitialized int, so a module leaving `control` untouched at `0` still nominally claims
"cockpit" -- what actually matters per-aircraft is whether the export code *reads any real cockpit
argument* into `mode1`/`mode2`/`mode3`/`mode4`/`status`, not the literal `control` value alone.

## 2. Per-aircraft export modules

Real per-aircraft argument reads live in
`Scripts/DCS-SRS/Scripts/DCS-SRS-Modules/*.lua` (61 files total, one per airframe, the same
directory OpenFreq's own `buildXRadios()` functions in `DCS/OpenFreqDCS/Scripts/OpenFreqDCS.lua`
already mirror for radios -- see that file's per-aircraft comments citing "DCS-SRS's *.lua
module"). Checked directly against the live GitHub source:

### Aircraft with real cockpit-driven IFF

| Aircraft | Module | Cockpit reads |
|---|---|---|
| A-10A | `A10A.lua` | Same scheme as A-10C II below (older module, same panel) |
| A-10C II | `A10C2.lua` | Selector 200 = power (>=2 -> NORMAL), button 207 = IDENT. Mode 1: on/off button 202, digits buttons 209/210 (x100 + x10). Mode 3: on/off button 204, digits buttons 211-214 (x10000/x1000/x100/x10). Mode 4: on/off button 208. Mode 2 not read (stays -1). |
| F-16C | `F16C.lua` | Device 539 selector = power/emergency. Button 125 = IDENT. Mode 1: buttons 546/548/553. Mode 3: buttons 546/548/550/552 + device 539 (EMERG -> hardcoded 7700). Mode 4: buttons 541/543. |
| F-4E Phantom | `F4.lua` | `IFF_device = GetDevice(4)`; `get_mode1()`, `get_mode2()`, `get_mode3()`, `get_mode4_is_on()` -- the only module with a real Mode 2 read. |
| F-14 Tomcat | `F14.lua` | `_iffDevice:hasPower()` -> status NORMAL, `:isIdentActive()` -> status IDENT, `:isModeActive(3)` + `:getModeCode(3)` -> mode3, `:isModeActive(4)` -> mode4. Mode 1/2 hardcoded (-1/unset). |
| F/A-18C Hornet | `FA18C.lua` | No raw button IDs -- parses rendered UFC text/cueing instead: Mode 1 from `UFC_OptionDisplay1` pattern match, Mode 2/3 from `UFC_ScratchPadNumberDisplay` on scratchpad update, Mode 4 from `UFC_OptionCueing4 == ":"`. IDENT: button/device 99, latches status=IDENT for a hardcoded 18s (`LoGetModelTime() + 18`) then reverts to NORMAL. |
| Mirage 2000C | `M2000C.lua` | `_iffDevice = GetDevice(42)`; `:hasPower()` -> NORMAL, `:isIdentActive()` -> IDENT, else status=-1 (no power). `:isModeActive(3)` + `:getModeCode(3)` -> mode3, `:isModeActive(4)` -> mode4. Mode 1/2 mostly static. |
| SA342 Gazelle | `SA342.lua` | Button 246 = power, button 240 = IDENT. Mode 1: `SR.getSelectorPosition(234,0.1)*10 + SR.getSelectorPosition(235,0.1)`. Mode 2 disabled (-1). Mode 3/4 not confirmed read (only mode1/status verified). |
| AH-64D Apache | `AH64.lua` | `GetDevice(0):get_argument_value(404)` = PLT emergency panel XPNDR. EUFD text parsing: `_eufdDevice["Transponder_MC"] == "NORM"` gates status (NORMAL/IDENT via a separate ident-button read), `_eufdDevice["Transponder_MODE_3A"]` -> mode3, `_eufdDevice["XPNDR_MODE_4"] ~= nil` -> mode4. Mode 1 persisted from operator entry (`_ah64Mode1Persist`), not read live. Mode 2 hardcoded -1. |
| Viggen (AJS37) | `AJS37.lua` | `_iffDevice:hasPower()` -> status, `:isModeActive(3)` + `:getModeCode(3)` -> mode3, `:isModeActive(4)` -> mode4. Mode 1/2 hardcoded. |

Two distinct patterns emerge: **raw button/selector polling** (A-10A/A-10C II/F-16C/SA342, all
`SR.getButtonPosition`/`SR.getSelectorPosition` against specific numeric argument IDs -- the same
style OpenFreq's own DCS export already uses for radios) vs. **device-object method calls**
(F-4/F-14/M2000C/AJS37 via a `GetDevice(id):hasPower()/isModeActive()/getModeCode()` style
abstraction, and F/A-18C/AH-64 via parsing the rendered cockpit display text). Which pattern a
given aircraft uses depends on what DCS's own Export API exposes for that module -- newer/more
complex avionics (glass cockpits with a rendered IFF/transponder page) tend to need text-parsing;
simpler physical panels use direct button reads.

### Aircraft with IFF explicitly disabled or unimplemented

Confirmed via `_data.capabilities = { dcsIFF = false, ... }` (or, for C-130J-30, a literal
`-- TODO: Implement IFF capability` comment) -- these leave `iff` either fully static or absent
entirely, no cockpit reads at all:

A-10C (original, non-II), F-15C, F-15EX ("EagleII"), AV-8B N/A, JF-17, Su-27, Su-25, MiG-29 (both
`MiG29.lua` and `MiG29Fulcrum.lua`), MiG-21bis, MiG-19P, UH-60L, CH-47F, Mi-8, Mi-24P, OH-58D,
SH-60B (no `iff` table at all), C-130J-30.

The remaining ~30 modules (WWII fighters -- P-51, P-47, Bf109, FW190, La-7, Yak-52, Spitfire family
-- and trainers -- L-39, Hawk, MB-339A, T-45, C-101, SK-60) were not individually checked, but the
pattern is unambiguous enough (no IFF concept applies to that era, and DCS doesn't model
transponders for those modules) that they almost certainly have none either.

## 3. Relevance to OpenFreq

The two aircraft OpenFreq already has DCS radio support for (A-10C II, F-16C -- see
`DCS/OpenFreqDCS/Scripts/OpenFreqDCS.lua`'s `buildA10C2Radios`/`buildF16Radios`) both have
ready-to-port real cockpit argument IDs above, using the exact same `getSelectorPosition`/
`getButtonPosition`-against-numeric-argument-ID style OpenFreq's own radio code already uses --
this is a straightforward port, not new research, when the time comes.

If OpenFreq ever adds DCS radio support for F-4, F-14, F/A-18, Mirage 2000C, SA342, AH-64, or
Viggen, their IFF wiring is proven out in SRS too, though F/A-18C and AH-64 need UFC/EUFD
text-parsing rather than plain button reads, and F-4/F-14/M2000C/AJS37 need the `GetDevice(id)`
method-call style rather than raw argument polling.

A future implementation would need, end to end:

1. **DCS Lua export** (`OpenFreqDCS.lua`): read the cockpit arguments above per aircraft, add an
   `iff` table to the exported unit data (mirroring the radio tables already built per aircraft).
2. **Client-side DCS state model** (`OpenFreq.Client/Models/Dcs/DcsRadioState.cs` or a sibling
   type): parse the new Lua fields, same as radio state is parsed today.
3. **Wire it to the server**: populate `OpenFreq.Server/SrsBridge/SrsSyncProtocol.cs`'s
   `SrsTransponder` (currently always left at its default) from that client-reported IFF state, so
   bridged real SRS clients actually see it.
4. **UI**: something to display/configure IFF status on the OpenFreq client, if OpenFreq's own
   clients should also be able to see/set it (SRS's own client has a dedicated IFF panel window for
   the `OVERLAY` control mode -- not researched here since it's a different question from cockpit
   reads).
