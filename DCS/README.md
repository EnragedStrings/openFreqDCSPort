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

## Finding unknown cockpit argument IDs (e.g. squelch)

Radio state (frequency, volume, encryption, PTT) is read via
`GetDevice(0):get_argument_value(id)`, where `id` is the cockpit's 3D-model
animation argument number for a given switch/knob. There's no documentation
shipped with the mod for these IDs, so unmapped switches (e.g. squelch on the
ARC-210/ARC-164/ARC-186) need to be found by correlating a physical switch
flip with the argument that changes.

To do that, set `debugArgScan.enabled = true` in `Scripts/OpenFreqDCSConfig.lua`
(edit the copy under your `Saved Games\DCS...\Mods\Services\OpenFreqDCS`
install), then in the cockpit flip **one switch at a time**, pausing a second
or two between each. Every changed argument gets logged to its own file
(kept separate from the radio debug log since it can be high-volume):

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
