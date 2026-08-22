# OpenFreq DCS Export

This is a clean OpenFreq-owned DCS export path. It sends JSON UDP packets to the
OpenFreq client on `127.0.0.1:34321`.

Install `DCS/OpenFreqDCS` into:

`%USERPROFILE%\Saved Games\DCS\Mods\Services\OpenFreqDCS`

or:

`%USERPROFILE%\Saved Games\DCS.openbeta\Mods\Services\OpenFreqDCS`

Then add this line to `Saved Games\...\Scripts\Export.lua`:

```lua
pcall(function() local lfs = require("lfs"); dofile(lfs.writedir() .. [[Mods\Services\OpenFreqDCS\Scripts\OpenFreqDCS.lua]]); end)
```

The Windows client installer runs `installer/windows/Install-DcsExport.ps1` to do
that automatically.

Heightmap sampling is disabled by default in `Scripts/OpenFreqDCSConfig.lua`.
Enable it only when you want DCS to generate an OpenFreq-compatible raw terrain
file. The sampler uses `land.getHeight`, writes signed little-endian int16 feet,
and reports the completed raw file to the client so OpenFreq can load it through
the same terrain pipeline used for BMS.

## Supported aircraft

`OpenFreqDCS.lua` dispatches on the DCS internal unit name (`selfData.Name`) to decide which
radios to export. Each aircraft can be toggled independently in `Scripts/OpenFreqDCSConfig.lua`
(`a10c2.enabled`, `f16c.enabled`, `c130j.enabled`, `uh60l.enabled` — all default on).

| Aircraft | Internal name(s) | Radios exported | Notes |
|---|---|---|---|
| A-10C II | `A-10C_2` | ARC-210, ARC-164 UHF, ARC-186 VHF FM | KY-58 encryption, HAVE QUICK, ARC-210 TR+G guard |
| F-16C Viper | `F-16C_50` (+ `F-16D_*`/Barak/F-16I variants) | ARC-164 UHF, ARC-222 VHF | KY-58 encryption. No cockpit PTT argument is known (DCS-SRS doesn't have one either) — bind a PTT hotkey in the client instead of relying on cockpit auto-detect. UHF guard is only read via the backup panel, not the UFC. |
| C-130J-30 | `C-130J-30` (Airplane Simulation Company module) | UHF1, UHF2, VHF1, VHF2, HF1, HF2, SAT (ARC-210) | Reads the pilot/left-seat volume+PTT panel only — DCS's export API can't tell which crew seat you're in, so right-seat/jump-seat players will see the wrong volume/PTT state. SAT/ARC-210 is best-effort (DCS-SRS itself flags that device as unreliable on this module). |
| UH-60L Black Hawk | `UH-60L` (+ `UH-60L_DAP`, `MH-60R`) | ARC-201 FM (pilot), ARC-164 UHF, ARC-186 VHF, ARC-201 FM (copilot) | ARC-220 HF isn't exported (no cockpit-readable frequency). Gated on DC bus/battery power. |

Argument IDs for the F-16C, C-130J-30, and UH-60L were ported from
[DCS-SRS](https://github.com/ciribob/DCS-SimpleRadioStandalone)'s per-aircraft export modules
(`Scripts/DCS-SRS/Scripts/DCS-SRS-Modules/*.lua`) — the same approach the original A-10C II
implementation used. They haven't all been flown and verified in-game yet; if a radio looks wrong,
use the arg-scan tool below to confirm/correct the id, and check `OpenFreqDCS.log`'s per-aircraft
debug line (`debugRadios`, on by default) for the raw values it's reading.

### Adding another aircraft

1. Find the DCS internal unit name: fastest way is to check DCS-SRS's `DCS-SRS-Modules` folder for
   a matching `.lua` file and read its `SR.exporters["..."] = ...` registration, or just log
   `selfData.Name` from `OpenFreqDCS.lua`'s `buildPacket` while sitting in the aircraft.
2. Port a starting set of device/argument IDs from that same DCS-SRS module as a `buildXRadios()`
   function (see `buildF16Radios`/`buildC130Radios`/`buildUH60Radios` for the pattern) — reuse the
   existing generic helpers (`getDevice`, `getArgument`, `getSelectorIndex`, `getDeviceFrequencyHz`,
   `getDeviceModulation`, `getVolume`, `getParam`) rather than writing new low-level plumbing.
3. Add one entry to the `aircraftBuilders` dispatch table and one `enabled` config block in
   `OpenFreqDCSConfig.lua`, mirroring the existing aircraft.
4. Verify in-game with `debugArgScan` (below) for anything not already covered by the ported IDs,
   and check the new debug log line for sane values before calling it done.

## Finding unknown cockpit argument IDs (e.g. squelch)

Radio state (frequency, volume, encryption, PTT) is read via
`GetDevice(0):get_argument_value(id)`, where `id` is the cockpit's 3D-model
animation argument number for a given switch/knob. There's no documentation
shipped with the mod for these IDs, so unmapped switches (e.g. squelch on the
ARC-210/ARC-164/ARC-186) need to be found by correlating a physical switch
flip with the argument that changes.

`debugArgScan.enabled` defaults to `true` in the repo config (this repo is currently private
with only developers on it, so it's more useful for everyone to have live arg-scan data than to
keep it opt-in — flip it to `false` in `Scripts/OpenFreqDCSConfig.lua`, or per-install under
`Saved Games\DCS...\Mods\Services\OpenFreqDCS\Scripts`, once that stops being worth the per-frame
scan cost). With it on, flip **one switch at a time** in the cockpit, pausing a second or two
between each. Every changed argument gets logged to its own file (kept separate from the radio
debug log since it can be high-volume):

`%USERPROFILE%\Saved Games\DCS\Logs\OpenFreqDCS.ArgScan.log`

(or `Saved Games\DCS.openbeta\Logs\OpenFreqDCS.ArgScan.log`, matching
whichever install you're using), as lines like:

```
device=0 id=781 changed 0.0000 -> 0.2000
```

The `id` on the line logged right as you flip a given switch is that switch's
argument. `minId`/`maxId` (default `0`-`900`) bound the scan range, and
`scanHz` (default `20`) throttles how often it samples — scanning is a real
per-frame cost, so turn `debugArgScan.enabled` back off once you've found what
you need.
