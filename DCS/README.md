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
