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

-- ARC-210 SATCOM detection. DCS itself has no dedicated "SATCOM selected" argument. The PRIMARY
-- signal (PROJECT_OBSERVED) is the ARC-210 cockpit display's own "active_channel" field, read via
-- list_indication on the same indicator already used for frequency/COMSEC -- an exact integer
-- channel number straight from the radio's own state, needing no per-install calibration. The
-- settings below (channel-select knob argument 552 landing on "Channel 31", tolerance-compared
-- since it's a continuous value not a stepped selector) are only a FALLBACK for the brief window
-- before that display indicator has been located (e.g. right after a DCS.exe restart), or if it's
-- ever unavailable for some other reason -- see OpenFreqDCS.lua's getArc210DisplayFrequencyHz/
-- buildA10C2Radios. PRST detection (secondary selector 553) has no known display-field equivalent
-- yet and always uses the argument-tolerance approach. See DCS/README.md and
-- docs/SATCOM_SIMULATION.md for how these values were observed and the acquisition behavior gated
-- on them.
OpenFreqDCSConfig.a10c2.satcom = OpenFreqDCSConfig.a10c2.satcom or {}
OpenFreqDCSConfig.a10c2.satcom.channelSelectorArgument = OpenFreqDCSConfig.a10c2.satcom.channelSelectorArgument or 552
OpenFreqDCSConfig.a10c2.satcom.channel31Value = OpenFreqDCSConfig.a10c2.satcom.channel31Value or 0.8499
OpenFreqDCSConfig.a10c2.satcom.secondarySelectorArgument = OpenFreqDCSConfig.a10c2.satcom.secondarySelectorArgument or 553
OpenFreqDCSConfig.a10c2.satcom.prstValue = OpenFreqDCSConfig.a10c2.satcom.prstValue or 0.2000
-- Small enough that neighboring channel/selector positions (which step in increments of
-- ~0.0333 for the channel knob, ~0.1 for the 5-position secondary selector) can't
-- false-trigger, large enough to absorb normal cockpit-argument floating-point jitter.
OpenFreqDCSConfig.a10c2.satcom.tolerance = OpenFreqDCSConfig.a10c2.satcom.tolerance or 0.01

-- Channels 31-50 on the ARC-210 channel-select knob are all DAMA ANDVT VOICE channels. Landing
-- anywhere in this band with PRST selected starts the login procedure (5 continuous seconds),
-- and SATCOM stays active as long as the knob remains anywhere in the band (and the radio stays
-- powered) -- moving outside it logs out and requires PRST selected again on any 31-50 channel to
-- log back in. It does NOT require dialing specifically to Channel 31. Normally this is checked
-- via the exact active_channel display field (see above) and channelBandMinValue/
-- channelBandMaxValue below never come into play; they only matter for the argument-552 fallback
-- path, which is ONLY calibrated for the 31-40 sub-band (channels 41-50 need new per-install
-- calibration data to detect via the fallback and currently won't). channelBandMinValue defaults
-- to channel31Value itself (id552's own reading at Channel 31); channelBandMaxValue defaults to
-- 0.9850, id552's PROJECT_OBSERVED reading at Channel 40 (user-calibrated by stepping the knob
-- through 31-40 and reading the "ARC210 SATCOM BAND" debug line in Logs\OpenFreqDCS.log). Override
-- either here, or per-install under Saved Games\...\Mods\Services\OpenFreqDCS\Scripts, if your
-- installation reads differently.
OpenFreqDCSConfig.a10c2.satcom.channelBandMinValue = OpenFreqDCSConfig.a10c2.satcom.channelBandMinValue
    or OpenFreqDCSConfig.a10c2.satcom.channel31Value
OpenFreqDCSConfig.a10c2.satcom.channelBandMaxValue = OpenFreqDCSConfig.a10c2.satcom.channelBandMaxValue
    or 0.9850

-- Channels 26-30 on the ARC-210 channel-select knob are half-duplex/dedicated SATCOM channels
-- (PROJECT_OBSERVED, user-reported): point-to-point, no PRST/DAMA login -- the operator just tunes
-- directly to an assigned transponder frequency the same way as any normal LOS channel. Detected
-- via the same active_channel display field as the 31-50 band (see OpenFreqDCS.lua's
-- arc210DedicatedSatcomActive); there's no argument-552 fallback range configured for this band
-- since it hasn't needed one -- unlike 31-50, if the display indicator isn't available yet this
-- band just reads as an ordinary LOS channel until it is, which is a safe default (never
-- misclassifies a real LOS channel as SATCOM).
--
-- NOTE: argument 561 (previously tracked here as a "SATCOM channel/net pushbutton") was a
-- misidentification -- it's actually the ARC-210's CT/CT-TD (FULL) COMSEC mode selection, already
-- covered by comsec_submode in the display data (see getArc210ComsecState in OpenFreqDCS.lua) --
-- and is not read separately here anymore.

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
