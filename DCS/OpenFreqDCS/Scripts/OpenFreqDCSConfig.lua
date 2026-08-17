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

-- Diagnostic tool for finding unknown cockpit argument IDs (e.g. squelch switches). Off by
-- default -- flip on, flip one switch at a time in the cockpit, and read the changed id out of
-- Logs\OpenFreqDCS.log. See DCS/README.md for details.
OpenFreqDCSConfig.debugArgScan = OpenFreqDCSConfig.debugArgScan or {}
OpenFreqDCSConfig.debugArgScan.enabled = OpenFreqDCSConfig.debugArgScan.enabled == true
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
