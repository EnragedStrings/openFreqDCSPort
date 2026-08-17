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

OpenFreqDCSConfig.los = OpenFreqDCSConfig.los or {}
OpenFreqDCSConfig.los.enabled = OpenFreqDCSConfig.los.enabled ~= false
OpenFreqDCSConfig.los.requestPort = OpenFreqDCSConfig.los.requestPort or 34322
OpenFreqDCSConfig.los.heightOffsetMeters = OpenFreqDCSConfig.los.heightOffsetMeters or 20
OpenFreqDCSConfig.los.maxHeightOffsetMeters = OpenFreqDCSConfig.los.maxHeightOffsetMeters or 200
OpenFreqDCSConfig.los.heightOffsetStepMeters = OpenFreqDCSConfig.los.heightOffsetStepMeters or 20

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
