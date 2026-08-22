OpenFreqDCSConfig = OpenFreqDCSConfig or {}

OpenFreqDCSConfig.host = OpenFreqDCSConfig.host or "127.0.0.1"
OpenFreqDCSConfig.port = OpenFreqDCSConfig.port or 34321
OpenFreqDCSConfig.exportHz = OpenFreqDCSConfig.exportHz or 10
OpenFreqDCSConfig.debugRadios = OpenFreqDCSConfig.debugRadios ~= false
OpenFreqDCSConfig.debugSeconds = OpenFreqDCSConfig.debugSeconds or 1

OpenFreqDCSConfig.a10c2 = OpenFreqDCSConfig.a10c2 or {}
OpenFreqDCSConfig.a10c2.enabled = OpenFreqDCSConfig.a10c2.enabled ~= false
OpenFreqDCSConfig.a10c2.exportGuardFrequencies = OpenFreqDCSConfig.a10c2.exportGuardFrequencies ~= false
OpenFreqDCSConfig.a10c2.trustDevicePower = OpenFreqDCSConfig.a10c2.trustDevicePower == true
OpenFreqDCSConfig.a10c2.arc210IndicatorIds = OpenFreqDCSConfig.a10c2.arc210IndicatorIds or { 18 }
OpenFreqDCSConfig.a10c2.arc210IndicatorScanMax = OpenFreqDCSConfig.a10c2.arc210IndicatorScanMax or 100

-- ARC-210 SATCOM detection. DCS itself has no dedicated "SATCOM selected" argument -- the
-- observed proxy is the channel-select knob (552) landing on "Channel 31" and the secondary
-- selector (553) landing on "PRST". Both are continuous cockpit-argument values (not stepped
-- selectors), so they're compared with a tolerance rather than exact equality. See
-- DCS/README.md and docs/SATCOM_SIMULATION.md for how these values were observed and the
-- 3-second acquisition behavior gated on them.
OpenFreqDCSConfig.a10c2.satcom = OpenFreqDCSConfig.a10c2.satcom or {}
OpenFreqDCSConfig.a10c2.satcom.channelSelectorArgument = OpenFreqDCSConfig.a10c2.satcom.channelSelectorArgument or 552
OpenFreqDCSConfig.a10c2.satcom.channel31Value = OpenFreqDCSConfig.a10c2.satcom.channel31Value or 0.8499
OpenFreqDCSConfig.a10c2.satcom.secondarySelectorArgument = OpenFreqDCSConfig.a10c2.satcom.secondarySelectorArgument or 553
OpenFreqDCSConfig.a10c2.satcom.prstValue = OpenFreqDCSConfig.a10c2.satcom.prstValue or 0.2000
-- Small enough that neighboring channel/selector positions (which step in increments of
-- ~0.0333 for the channel knob, ~0.1 for the 5-position secondary selector) can't
-- false-trigger, large enough to absorb normal cockpit-argument floating-point jitter.
OpenFreqDCSConfig.a10c2.satcom.tolerance = OpenFreqDCSConfig.a10c2.satcom.tolerance or 0.01

-- Channels 31-40 on the ARC-210 channel-select knob are all DAMA ANDVT VOICE channels, but only
-- Channel 31 runs the actual PRST login procedure. Once logged in (5 continuous seconds on
-- Channel 31 + PRST), SATCOM should stay active as long as the knob remains anywhere in this
-- band (and the radio stays powered) -- moving outside it logs out and requires returning to
-- Channel 31 + PRST to log back in. channelBandMinValue defaults to channel31Value itself (id552's
-- own reading at Channel 31); channelBandMaxValue defaults to 0.9850, id552's PROJECT_OBSERVED
-- reading at Channel 40 (user-calibrated by stepping the knob through 31-40 and reading the
-- "ARC210 SATCOM BAND" debug line in Logs\OpenFreqDCS.log). Override either here, or per-install
-- under Saved Games\...\Mods\Services\OpenFreqDCS\Scripts, if your installation reads differently.
OpenFreqDCSConfig.a10c2.satcom.channelBandMinValue = OpenFreqDCSConfig.a10c2.satcom.channelBandMinValue
    or OpenFreqDCSConfig.a10c2.satcom.channel31Value
OpenFreqDCSConfig.a10c2.satcom.channelBandMaxValue = OpenFreqDCSConfig.a10c2.satcom.channelBandMaxValue
    or 0.9850

-- SATCOM channel/net pushbutton (PROJECT_OBSERVED, separate from the 31-40 knob above): argument
-- 561 reads ~1.000 while pressed, ~0 at rest -- a momentary pushbutton, not a rotary. Both this
-- AND the 31-40 band above have to be satisfied, and matching, for two stations to talk over
-- SATCOM: the knob puts the radio in the DAMA/ANDVT band at all, this pushbutton picks which of 6
-- virtual channels/nets within it. Each press (rising edge, tracked in OpenFreqDCS.lua) advances
-- the channel 1 -> 6 then wraps back to 1; the aircraft always starts on channel 1.
OpenFreqDCSConfig.a10c2.satcom.channelSelectorPushButtonArgument = OpenFreqDCSConfig.a10c2.satcom.channelSelectorPushButtonArgument
    or 561
OpenFreqDCSConfig.a10c2.satcom.channelPushButtonPressedValue = OpenFreqDCSConfig.a10c2.satcom.channelPushButtonPressedValue
    or 1.000

OpenFreqDCSConfig.f16c = OpenFreqDCSConfig.f16c or {}
OpenFreqDCSConfig.f16c.enabled = OpenFreqDCSConfig.f16c.enabled ~= false

OpenFreqDCSConfig.c130j = OpenFreqDCSConfig.c130j or {}
OpenFreqDCSConfig.c130j.enabled = OpenFreqDCSConfig.c130j.enabled ~= false

OpenFreqDCSConfig.uh60l = OpenFreqDCSConfig.uh60l or {}
OpenFreqDCSConfig.uh60l.enabled = OpenFreqDCSConfig.uh60l.enabled ~= false

OpenFreqDCSConfig.los = OpenFreqDCSConfig.los or {}
OpenFreqDCSConfig.los.enabled = OpenFreqDCSConfig.los.enabled ~= false
OpenFreqDCSConfig.los.requestPort = OpenFreqDCSConfig.los.requestPort or 34322
OpenFreqDCSConfig.los.heightOffsetMeters = OpenFreqDCSConfig.los.heightOffsetMeters or 20
OpenFreqDCSConfig.los.maxHeightOffsetMeters = OpenFreqDCSConfig.los.maxHeightOffsetMeters or 200
OpenFreqDCSConfig.los.heightOffsetStepMeters = OpenFreqDCSConfig.los.heightOffsetStepMeters or 20

-- Diagnostic tool for finding unknown cockpit argument IDs (e.g. squelch switches). On by
-- default for now -- this repo is private with only developers using it, so it's more useful
-- for everyone to have live arg-scan data (Logs\OpenFreqDCS.ArgScan.log) than to keep it opt-in.
-- Flip to false here (or per-install, under Saved Games\...\Mods\Services\OpenFreqDCS\Scripts)
-- once the module roster settles down and this stops being worth the per-frame scan cost for
-- everyone. See DCS/README.md for details.
OpenFreqDCSConfig.debugArgScan = OpenFreqDCSConfig.debugArgScan or {}
OpenFreqDCSConfig.debugArgScan.enabled = OpenFreqDCSConfig.debugArgScan.enabled ~= false
OpenFreqDCSConfig.debugArgScan.scanHz = OpenFreqDCSConfig.debugArgScan.scanHz or 20
OpenFreqDCSConfig.debugArgScan.minId = OpenFreqDCSConfig.debugArgScan.minId or 0
OpenFreqDCSConfig.debugArgScan.maxId = OpenFreqDCSConfig.debugArgScan.maxId or 900
OpenFreqDCSConfig.debugArgScan.deviceIds = OpenFreqDCSConfig.debugArgScan.deviceIds or { 0 }
-- Ids that report a "changed" reading unrelated to any switch (observed noisy on 10 and 600) --
-- excluded so real switch flips aren't buried in the log.
OpenFreqDCSConfig.debugArgScan.ignoreIds = OpenFreqDCSConfig.debugArgScan.ignoreIds or { 600, 10 }

OpenFreqDCSConfig.heightmap = OpenFreqDCSConfig.heightmap or {}
OpenFreqDCSConfig.heightmap.enabled = OpenFreqDCSConfig.heightmap.enabled == true

-- DCS does not expose a cheap, universal terrain-bounds call to Export.lua.
-- These defaults cover a one-million-meter square centered on the DCS local origin.
-- Set these per theater before enabling the sampler if a map uses wider coordinates.
OpenFreqDCSConfig.heightmap.originX = OpenFreqDCSConfig.heightmap.originX or -500000
OpenFreqDCSConfig.heightmap.originZ = OpenFreqDCSConfig.heightmap.originZ or -500000
OpenFreqDCSConfig.heightmap.widthMeters = OpenFreqDCSConfig.heightmap.widthMeters or 1000000
OpenFreqDCSConfig.heightmap.heightMeters = OpenFreqDCSConfig.heightmap.heightMeters or 1000000
OpenFreqDCSConfig.heightmap.samplesX = OpenFreqDCSConfig.heightmap.samplesX or 4096
OpenFreqDCSConfig.heightmap.samplesZ = OpenFreqDCSConfig.heightmap.samplesZ or 4096
OpenFreqDCSConfig.heightmap.rowsPerTick = OpenFreqDCSConfig.heightmap.rowsPerTick or 1

return OpenFreqDCSConfig
