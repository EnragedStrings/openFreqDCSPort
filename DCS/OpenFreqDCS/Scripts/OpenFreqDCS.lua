local OpenFreqDCS = rawget(_G, "OpenFreqDCS") or {}
_G.OpenFreqDCS = OpenFreqDCS

if OpenFreqDCS.installed then
    return
end
OpenFreqDCS.installed = true

local lfs = rawget(_G, "lfs")
if not lfs then
    pcall(function() lfs = require("lfs") end)
end

local writeDir = ""
if lfs and type(lfs.writedir) == "function" then
    writeDir = lfs.writedir()
end

local function loadConfig()
    local path = writeDir .. [[Mods\Services\OpenFreqDCS\Scripts\OpenFreqDCSConfig.lua]]
    local ok, config = pcall(dofile, path)
    if ok and type(config) == "table" then
        return config
    end
    return rawget(_G, "OpenFreqDCSConfig") or {}
end

local config = loadConfig()
OpenFreqDCS.config = config

local function appendPackagePath(path)
    if package and package.path and not package.path:find(path, 1, true) then
        package.path = package.path .. ";" .. path
    end
end

appendPackagePath([[.\MissionEditor\?.lua]])
appendPackagePath([[.\Scripts\?.lua]])
appendPackagePath([[.\LuaSocket\?.lua]])

local socket
pcall(function() socket = require("socket") end)

local json
pcall(function() json = loadfile("Scripts\\JSON.lua")() end)

local terrain
pcall(function() terrain = require("terrain") end)

local function writeLog(message)
    if log and type(log.write) == "function" and log.INFO then
        pcall(log.write, "OpenFreqDCS", log.INFO, message)
    end

    if writeDir ~= "" then
        local file = io.open(writeDir .. [[Logs\OpenFreqDCS.log]], "ab")
        if file then
            file:write(os.date("!%Y-%m-%dT%H:%M:%SZ "), tostring(message), "\r\n")
            file:close()
        end
    end
end

local function writeDebug(message)
    if config.debugRadios == false then
        return
    end
    writeLog(message)
end

writeDebug(string.format(
    "loaded OpenFreqDCS export host=%s port=%s exportHz=%s socket=%s json=%s terrain=%s",
    tostring(config.host or "127.0.0.1"),
    tostring(config.port or 34321),
    tostring(config.exportHz or 10),
    tostring(socket ~= nil),
    tostring(json ~= nil),
    tostring(terrain ~= nil)
))

local function clamp(value, minValue, maxValue)
    if value < minValue then return minValue end
    if value > maxValue then return maxValue end
    return value
end

local function numberOr(value, fallback)
    if type(value) == "number" then return value end
    return fallback
end

local function boolOr(value, fallback)
    if type(value) == "boolean" then return value end
    return fallback
end

local function textOr(value)
    if value == nil then
        return "nil"
    end
    return tostring(value)
end

local function oneLine(value, maxLength)
    value = tostring(value or "")
    value = value:gsub("\r", " "):gsub("\n", " ")
    if #value > maxLength then
        return value:sub(1, maxLength) .. "..."
    end
    return value
end

local callGlobal

local function getListIndicatorValues(indicatorId)
    local raw = callGlobal("list_indication", "", indicatorId)
    if type(raw) ~= "string" or raw == "" then
        return nil, raw
    end

    local values = {}
    for key, value in raw:gmatch("-----------------------------------------%s*\n([^\n]+)\n([^\n]*)\n") do
        values[key] = value
    end

    if next(values) == nil then
        return nil, raw
    end

    return values, raw
end

local function escapeJsonString(value)
    value = tostring(value)
    value = value:gsub("\\", "\\\\")
    value = value:gsub("\"", "\\\"")
    value = value:gsub("\b", "\\b")
    value = value:gsub("\f", "\\f")
    value = value:gsub("\n", "\\n")
    value = value:gsub("\r", "\\r")
    value = value:gsub("\t", "\\t")
    value = value:gsub("[%z\1-\31]", function(c)
        return string.format("\\u%04x", string.byte(c))
    end)
    return "\"" .. value .. "\""
end

local function isArray(value)
    local maxIndex = 0
    local count = 0
    for key, _ in pairs(value) do
        if type(key) ~= "number" or key < 1 or key % 1 ~= 0 then
            return false
        end
        if key > maxIndex then maxIndex = key end
        count = count + 1
    end
    return maxIndex == count
end

local encodeJson
encodeJson = function(value)
    local valueType = type(value)
    if valueType == "nil" then
        return "null"
    elseif valueType == "boolean" then
        return value and "true" or "false"
    elseif valueType == "number" then
        if value ~= value or value == math.huge or value == -math.huge then
            return "0"
        end
        return string.format("%.10g", value)
    elseif valueType == "string" then
        return escapeJsonString(value)
    elseif valueType == "table" then
        local parts = {}
        if isArray(value) then
            for i = 1, #value do
                parts[#parts + 1] = encodeJson(value[i])
            end
            return "[" .. table.concat(parts, ",") .. "]"
        end

        for key, item in pairs(value) do
            if type(key) == "string" and type(item) ~= "function" then
                parts[#parts + 1] = escapeJsonString(key) .. ":" .. encodeJson(item)
            end
        end
        return "{" .. table.concat(parts, ",") .. "}"
    end

    return "null"
end

function callGlobal(name, fallback, ...)
    local fn = rawget(_G, name)
    if type(fn) ~= "function" then
        return fallback
    end
    local ok, result = pcall(fn, ...)
    if ok then
        return result
    end
    return fallback
end

local function callMethod(target, name, fallback, ...)
    if not target or type(target[name]) ~= "function" then
        return fallback
    end
    local ok, result = pcall(target[name], target, ...)
    if ok then
        return result
    end
    return fallback
end

local function getMainPanel()
    return callGlobal("GetDevice", nil, 0)
end

local function getArgument(mainPanel, id, fallback)
    local value = callMethod(mainPanel, "get_argument_value", nil, id)
    if type(value) == "number" then
        return value
    end
    return fallback
end

local function getSelectorIndex(mainPanel, id, step)
    local value = getArgument(mainPanel, id, nil)
    if type(value) ~= "number" then
        return nil, nil
    end

    step = numberOr(step, 0.1)
    if step <= 0 then
        return nil, value
    end

    return math.floor((value / step) + 0.5), value
end

local function isIndexInRange(index, minValue, maxValue)
    return type(index) == "number" and index >= minValue and index <= maxValue
end

local function formatArguments(mainPanel, ids)
    local parts = {}
    for _, id in ipairs(ids) do
        local value = getArgument(mainPanel, id, nil)
        if type(value) == "number" then
            parts[#parts + 1] = tostring(id) .. "=" .. string.format("%.3f", value)
        else
            parts[#parts + 1] = tostring(id) .. "=nil"
        end
    end
    return table.concat(parts, ",")
end

local function getDevice(id)
    return callGlobal("GetDevice", nil, id)
end

local function roundToStep(value, step)
    if type(step) ~= "number" or step <= 0 then
        return value
    end
    return math.floor((value + step / 2) / step) * step
end

local function getDeviceOn(device)
    local isOn = callMethod(device, "is_on", nil)
    if type(isOn) == "boolean" then
        return isOn
    end
    return nil
end

local function getDeviceFrequencyHz(device, roundStep, requirePower)
    local isOn = getDeviceOn(device)
    local frequency = callMethod(device, "get_frequency", 0)
    if type(frequency) ~= "number" or frequency < 0 then
        return 0, frequency, isOn
    end
    if requirePower and isOn == false then
        return 0, frequency, isOn
    end
    return math.floor(roundToStep(frequency, roundStep or 5000) + 0.5), frequency, isOn
end

local quarterKhzByIndex = {
    [0] = 0,
    [1] = 25,
    [2] = 50,
    [3] = 75
}

local function getArc210DialFrequencyHz(mainPanel)
    local hundredsIndex = getSelectorIndex(mainPanel, 554, 0.1)
    local tens = getSelectorIndex(mainPanel, 555, 0.1)
    local ones = getSelectorIndex(mainPanel, 556, 0.1)
    local hundredKhz = getSelectorIndex(mainPanel, 557, 0.1)
    local quarterIndex = getSelectorIndex(mainPanel, 558, 0.1)

    if not isIndexInRange(hundredsIndex, 0, 3)
        or not isIndexInRange(tens, 0, 9)
        or not isIndexInRange(ones, 0, 9)
        or not isIndexInRange(hundredKhz, 0, 9)
        or not isIndexInRange(quarterIndex, 0, 3) then
        return nil
    end

    local quarterKhz = quarterKhzByIndex[quarterIndex]
    if quarterKhz == nil then
        return nil
    end

    local mhz = (hundredsIndex * 100) + (tens * 10) + ones
    local hz = (mhz * 1000000) + ((hundredKhz * 100 + quarterKhz) * 1000)
    if hz < 30000000 or hz > 399975000 then
        return nil
    end
    return hz
end

local function getArc164DialFrequencyHz(mainPanel)
    local mode = getSelectorIndex(mainPanel, 167, 0.1)
    if mode ~= nil and mode ~= 0 then
        return nil
    end

    local hundredsIndex = getSelectorIndex(mainPanel, 162, 0.1)
    local tens = getSelectorIndex(mainPanel, 163, 0.1)
    local ones = getSelectorIndex(mainPanel, 164, 0.1)
    local tenthMhz = getSelectorIndex(mainPanel, 165, 0.1)
    local quarterIndex = getSelectorIndex(mainPanel, 166, 0.1)

    if not isIndexInRange(hundredsIndex, 0, 1)
        or not isIndexInRange(tens, 0, 9)
        or not isIndexInRange(ones, 0, 9)
        or not isIndexInRange(tenthMhz, 0, 9)
        or not isIndexInRange(quarterIndex, 0, 3) then
        return nil
    end

    local quarterKhz = quarterKhzByIndex[quarterIndex]
    if quarterKhz == nil then
        return nil
    end

    local mhz = ((hundredsIndex + 2) * 100) + (tens * 10) + ones
    local hz = (mhz * 1000000) + (tenthMhz * 100000) + (quarterKhz * 1000)
    if hz < 200000000 or hz > 399975000 then
        return nil
    end
    return hz
end

local function tryGetArc210DisplayFrequencyFromIndicator(indicatorId)
    local values, raw = getListIndicatorValues(indicatorId)
    if not values then
        return nil, raw, indicatorId, nil
    end

    local mhz = tonumber(values.freq_label_mhz)
    local khz = tonumber(values.freq_label_khz)
    if not mhz or not khz then
        return nil, raw, indicatorId, values
    end

    local frequency = (mhz * 1000000) + (khz * 1000)
    if frequency <= 1000 then
        return nil, raw, indicatorId, values
    end

    return frequency, raw, indicatorId, values
end

-- KY-58/COMSEC state for the ARC-210, read from the same list_indication values used for its
-- frequency display. comsec_submode: "PT" = plain text, "CT" = cipher text, "CT-TD" = cipher
-- text with Have Quick (time data) frequency hopping. Mirrors DCS-SRS's A10C2.lua interpretation
-- of indicator 18.
local function getArc210ComsecState(values)
    if not values or not values.comsec_submode then
        return false, nil, false
    end

    local submode = values.comsec_submode
    local encKey = tonumber(values.ky_submode_label)

    if submode == "CT" then
        return true, encKey, false
    elseif submode == "CT-TD" then
        return true, encKey, true
    end

    -- "PT" (plain text) or any other/unrecognized submode
    return false, encKey, false
end

local function scanArc210DisplayIndicator()
    local scanMax = numberOr(config.a10c2 and config.a10c2.arc210IndicatorScanMax, 100)
    local candidates = {}

    for indicatorId = 0, scanMax do
        local frequency, raw = tryGetArc210DisplayFrequencyFromIndicator(indicatorId)
        if frequency then
            OpenFreqDCS.arc210IndicatorId = indicatorId
            OpenFreqDCS.arc210IndicatorScanSummary = "found:" .. tostring(indicatorId)
            return frequency, raw, indicatorId
        end

        if type(raw) == "string" and raw ~= "" then
            local flat = oneLine(raw, 120)
            local lower = flat:lower()
            if lower:find("freq", 1, true) or lower:find("arc", 1, true) or lower:find("rt", 1, true) then
                candidates[#candidates + 1] = tostring(indicatorId) .. ":" .. flat
                if #candidates >= 4 then
                    break
                end
            end
        end
    end

    OpenFreqDCS.arc210IndicatorScanSummary = #candidates > 0 and table.concat(candidates, " || ") or "none"
    return nil, nil, nil
end

local function getArc210DisplayFrequencyHz()
    local configuredIds = config.a10c2 and config.a10c2.arc210IndicatorIds or { 18 }
    local tried = {}

    if OpenFreqDCS.arc210IndicatorId then
        local frequency, raw, indicatorId, values = tryGetArc210DisplayFrequencyFromIndicator(OpenFreqDCS.arc210IndicatorId)
        if frequency then
            return frequency, raw, indicatorId, values
        end
        tried[OpenFreqDCS.arc210IndicatorId] = true
    end

    if type(configuredIds) == "table" then
        for _, indicatorId in ipairs(configuredIds) do
            if type(indicatorId) == "number" and not tried[indicatorId] then
                local frequency, raw, foundIndicatorId, values = tryGetArc210DisplayFrequencyFromIndicator(indicatorId)
                if frequency then
                    OpenFreqDCS.arc210IndicatorId = foundIndicatorId
                    OpenFreqDCS.arc210IndicatorScanSummary = "configured:" .. tostring(foundIndicatorId)
                    return frequency, raw, foundIndicatorId, values
                end
                tried[indicatorId] = true
            end
        end
    end

    local now = callGlobal("LoGetModelTime", 0)
    if OpenFreqDCS.nextArc210IndicatorScanTime == nil or now >= OpenFreqDCS.nextArc210IndicatorScanTime then
        OpenFreqDCS.nextArc210IndicatorScanTime = now + 2
        return scanArc210DisplayIndicator()
    end

    return nil, nil, nil, nil
end

local function getDeviceModulation(device, fallback)
    local modulation = callMethod(device, "get_modulation", fallback)
    if type(modulation) == "number" then
        return math.floor(modulation)
    end
    return fallback
end

local function getDevicePower(device, frequencyHz, knownIsOn)
    if not (config.a10c2 and config.a10c2.trustDevicePower == true) then
        return frequencyHz > 1000
    end

    local isOn = knownIsOn
    if isOn == nil then
        isOn = getDeviceOn(device)
    end
    if type(isOn) == "boolean" then
        return isOn and frequencyHz > 0
    end
    return frequencyHz > 0
end

local function getVolume(mainPanel, ...)
    local ids = { ... }
    local haveAny = false
    local volume = 1.0
    for _, id in ipairs(ids) do
        local value = getArgument(mainPanel, id, nil)
        if type(value) == "number" then
            haveAny = true
            volume = volume * clamp(value, 0.0, 1.0)
        end
    end
    if not haveAny then
        return 1.0
    end
    return clamp(volume, 0.0, 1.0)
end

-- Only the ARC-210 has a true simultaneous-guard mode (TR+G: monitor 243.0 in addition to the
-- tuned frequency, without retuning away from it). The ARC-164's GRD selector position (167,
-- index 2) instead exclusively retunes its primary frequency to guard -- getArc164DialFrequencyHz
-- already reflects that via the device's own get_frequency(), so it needs no separate guard field.
-- The ARC-186 has no guard capability at all.
local function getArc210GuardHz(mainPanel)
    if config.a10c2 and config.a10c2.exportGuardFrequencies == false then
        return 0
    end
    local modeIndex = getSelectorIndex(mainPanel, 551, 0.1)
    if modeIndex == 1 then -- TR+G
        return 243000000
    end
    return 0
end

local function getPtt(mainPanel)
    local micHorizontal = getArgument(mainPanel, 752, 0)
    local micVertical = getArgument(mainPanel, 751, 0)
    return {
        arc210 = micHorizontal > 0.5,
        arc164 = micVertical < -0.5,
        arc186 = micHorizontal < -0.5
    }
end

local function buildA10C2Radios()
    local mainPanel = getMainPanel()
    local ptt = getPtt(mainPanel)

    local arc210 = getDevice(55)
    local arc164 = getDevice(54)
    local arc186 = getDevice(56)

    local requireDevicePower = config.a10c2 and config.a10c2.trustDevicePower == true
    local arc210Frequency, arc210RawFrequency, arc210IsOn = getDeviceFrequencyHz(arc210, 5000, requireDevicePower)
    local arc164Frequency, arc164RawFrequency, arc164IsOn = getDeviceFrequencyHz(arc164, 5000, requireDevicePower)
    local arc186Frequency, arc186RawFrequency, arc186IsOn = getDeviceFrequencyHz(arc186, 5000, requireDevicePower)
    local arc210DisplayFrequency, arc210DisplayRaw, arc210DisplayIndicator, arc210DisplayValues = getArc210DisplayFrequencyHz()
    local arc210DialFrequency = getArc210DialFrequencyHz(mainPanel)
    local arc164DialFrequency = getArc164DialFrequencyHz(mainPanel)
    if arc210DisplayFrequency then
        arc210Frequency = arc210DisplayFrequency
    elseif arc210DialFrequency then
        arc210Frequency = arc210DialFrequency
    end
    if arc164DialFrequency then
        arc164Frequency = arc164DialFrequency
    end

    -- ARC-210 built-in COMSEC (independent of the shared external KY-58 unit below)
    local arc210Enc, arc210EncKey, arc210HqOn = getArc210ComsecState(arc210DisplayValues)

    -- KY-58 Radio Encryption: shared external unit, switchable between the ARC-164 (UHF) and
    -- ARC-186 (VHF-FM). Mirrors DCS-SRS's A10C2.lua cockpit argument mapping (784 power/OP,
    -- 783 mode/zeroize, 781 crad selector, 782 key channel, 149 EMER selector, 167 UHF freq
    -- mode selector) rather than reverse-engineering new argument IDs.
    local uhfFreqModeSelector = getSelectorIndex(mainPanel, 167, 0.1)
    local ky58Power = getArgument(mainPanel, 784, 0)
    local ky58Mode = getArgument(mainPanel, 783, 0)
    local arc164Enc, arc164EncKey = false, nil
    local arc186Enc, arc186EncKey = false, nil

    if ky58Power and ky58Power > 0.5 and ky58Mode == 0 then
        -- Mode switch set to OP and powered on
        local cradSelector = getArgument(mainPanel, 781, 0)
        local emerSelector = getSelectorIndex(mainPanel, 149, 0.1)
        local channel = getSelectorIndex(mainPanel, 782, 0.1)
        channel = channel and (channel + 1) or nil

        -- crad/2 (VHF-FM): matches DCS-SRS's condition verbatim, including its own comment
        -- ("encryption disabled when EMER AM/FM selected") even though the >= 2 check reads as
        -- the opposite at a glance — this is the proven-working cockpit argument behavior.
        local targetsArc186 = roundToStep(cradSelector or 0, 0.1) == 0.2 and emerSelector ~= nil and emerSelector >= 2
        -- crad/1 (UHF): disabled when the UHF frequency-mode selector is set to GRD (index 2)
        local targetsArc164 = cradSelector == 0 and uhfFreqModeSelector ~= 2

        if targetsArc186 and channel then
            arc186Enc = true
            arc186EncKey = channel
        elseif targetsArc164 and channel then
            arc164Enc = true
            arc164EncKey = channel
        end
    end

    -- Manual squelch-open switches. Rest position (0) is normal/closed squelch; flipping the
    -- switch opens (disables) squelch to help pick out weak/garbled signals -- mirrors the
    -- client's existing "open squelch" feature. ARC-186 is a 3-position switch (SQUELCH -1 /
    -- center 0 / momentary TONE +1); only the SQUELCH position opens squelch, TONE is unrelated.
    local arc210SquelchOn = getArgument(mainPanel, 568, 0) > 0.5
    local arc164SquelchOn = getArgument(mainPanel, 170, 0) > 0.5
    local arc186SquelchOn = getArgument(mainPanel, 148, 0) < -0.5
    -- Same switch (148), momentary TONE position (+1): keys the ARC-186 and sends an attention
    -- tone instead of mic audio. Spring-loaded back to center on release, already handled by DCS.
    local arc186ToneOn = getArgument(mainPanel, 148, 0) > 0.5

    -- ARC-210 power knob (551): 0 = OFF, 0.1 = TR+G, 0.2 = TR, 0.3 = ADF, 0.4 = CHG PRST,
    -- 0.5 = TEST, 0.6 = ZERO (PULL). The default power heuristic below (frequency > 1000 Hz)
    -- can't see this since the dial keeps its tuned frequency even when powered off, so gate on
    -- the knob explicitly whenever it reads OFF.
    local arc210PowerKnobOff = getSelectorIndex(mainPanel, 551, 0.1) == 0
    -- ARC-164 power knob (168): 0 = OFF, 0.1 = MAIN, 0.2 = BOTH, 0.3 = ADF. Same dial-keeps-its-
    -- frequency issue as ARC-210, found via debugArgScan.
    local arc164PowerKnobOff = getSelectorIndex(mainPanel, 168, 0.1) == 0
    -- ARC-186 power knob (152): 0 = OFF, 0.1 = TR, 0.2 = DF. Same rationale, found via debugArgScan.
    local arc186PowerKnobOff = getSelectorIndex(mainPanel, 152, 0.1) == 0

    local now = callGlobal("LoGetModelTime", 0)
    local debugSeconds = numberOr(config.debugSeconds, 1)
    if config.debugRadios ~= false and (OpenFreqDCS.nextRadioDebugTime == nil or now >= OpenFreqDCS.nextRadioDebugTime) then
        OpenFreqDCS.nextRadioDebugTime = now + debugSeconds
        local arc210Display = oneLine(arc210DisplayRaw, 220)
        writeDebug(string.format(
            "A-10C_2 radios: ARC210 freq=%s displayFreq=%s dialFreq=%s displayIndicator=%s raw=%s on=%s modeArg551=%s args554-558=%s vol=%s sq=%s pwrOff=%s displayRaw=%s displayScan=%s | ARC164 freq=%s dialFreq=%s raw=%s on=%s modeArg168=%s selector167=%s channel161=%s args162-166=%s vol=%s sq=%s pwrOff=%s | ARC186 freq=%s raw=%s on=%s modeArg149=%s vol=%s sq=%s tone=%s pwrOff=%s | ptt arc210=%s arc164=%s arc186=%s mic751=%s mic752=%s",
            textOr(arc210Frequency), textOr(arc210DisplayFrequency), textOr(arc210DialFrequency), textOr(arc210DisplayIndicator), textOr(arc210RawFrequency), textOr(arc210IsOn), textOr(getArgument(mainPanel, 551, nil)), formatArguments(mainPanel, { 554, 555, 556, 557, 558 }), textOr(getVolume(mainPanel, 238, 225, 226)), textOr(arc210SquelchOn), textOr(arc210PowerKnobOff), arc210Display, textOr(OpenFreqDCS.arc210IndicatorScanSummary),
            textOr(arc164Frequency), textOr(arc164DialFrequency), textOr(arc164RawFrequency), textOr(arc164IsOn), textOr(getArgument(mainPanel, 168, nil)), textOr(getArgument(mainPanel, 167, nil)), textOr(getArgument(mainPanel, 161, nil)), formatArguments(mainPanel, { 162, 163, 164, 165, 166 }), textOr(getVolume(mainPanel, 171, 238, 227, 228)), textOr(arc164SquelchOn), textOr(arc164PowerKnobOff),
            textOr(arc186Frequency), textOr(arc186RawFrequency), textOr(arc186IsOn), textOr(getArgument(mainPanel, 149, nil)), textOr(getVolume(mainPanel, 147, 238, 223, 224)), textOr(arc186SquelchOn), textOr(arc186ToneOn), textOr(arc186PowerKnobOff),
            textOr(ptt.arc210), textOr(ptt.arc164), textOr(ptt.arc186), textOr(getArgument(mainPanel, 751, nil)), textOr(getArgument(mainPanel, 752, nil))
        ))
    end

    local arc210Modulation = getDeviceModulation(arc210, 0)
    if arc210HqOn then
        arc210Modulation = 4 -- Have Quick
    end

    return {
        {
            slot = 1,
            name = "ARC-210",
            frequencyHz = arc210Frequency,
            secondaryFrequencyHz = getArc210GuardHz(mainPanel),
            modulation = arc210Modulation,
            volume = getVolume(mainPanel, 238, 225, 226),
            isOn = not arc210PowerKnobOff and getDevicePower(arc210, arc210Frequency, arc210IsOn),
            ptt = ptt.arc210,
            enc = arc210Enc,
            encKey = arc210EncKey,
            hqOn = arc210HqOn,
            squelchOn = arc210SquelchOn,
            toneOn = false
        },
        {
            slot = 2,
            name = "ARC-164 UHF",
            frequencyHz = arc164Frequency,
            secondaryFrequencyHz = 0, -- GRD retunes the primary frequency instead; see getArc210GuardHz comment
            modulation = getDeviceModulation(arc164, 0),
            volume = getVolume(mainPanel, 171, 238, 227, 228),
            isOn = not arc164PowerKnobOff and getDevicePower(arc164, arc164Frequency, arc164IsOn),
            ptt = ptt.arc164,
            enc = arc164Enc,
            encKey = arc164EncKey,
            hqOn = false,
            squelchOn = arc164SquelchOn,
            toneOn = false
        },
        {
            slot = 3,
            name = "ARC-186 VHF FM",
            frequencyHz = arc186Frequency,
            secondaryFrequencyHz = 0,
            modulation = getDeviceModulation(arc186, 1),
            volume = getVolume(mainPanel, 147, 238, 223, 224),
            isOn = not arc186PowerKnobOff and getDevicePower(arc186, arc186Frequency, arc186IsOn),
            ptt = ptt.arc186,
            enc = arc186Enc,
            encKey = arc186EncKey,
            hqOn = false,
            squelchOn = arc186SquelchOn,
            toneOn = arc186ToneOn
        }
    }
end

local function detectTheater()
    local ok, theater = pcall(function()
        if env and env.mission and env.mission.theatre then
            return env.mission.theatre
        end
        return nil
    end)
    if ok and type(theater) == "string" and theater ~= "" then
        return theater
    end
    return "DCS"
end

local function safeFileName(value)
    value = tostring(value or "DCS")
    value = value:gsub("[^%w%._%-]", "_")
    if value == "" then
        return "DCS"
    end
    return value
end

local function mkdir(path)
    if lfs and type(lfs.mkdir) == "function" then
        pcall(lfs.mkdir, path)
    end
end

local function ensureHeightmapDirectory()
    local root = writeDir .. [[Mods\Services\OpenFreqDCS]]
    mkdir(writeDir .. [[Mods]])
    mkdir(writeDir .. [[Mods\Services]])
    mkdir(root)
    mkdir(root .. [[\Heightmaps]])
    return root .. [[\Heightmaps]]
end

local function int16le(value)
    value = math.floor(value + 0.5)
    value = clamp(value, -32768, 32767)
    if value < 0 then
        value = 65536 + value
    end
    return string.char(value % 256, math.floor(value / 256) % 256)
end

local function writeHeightmapMetadata(state)
    if not state or not state.metadataPath then
        return
    end

    local file = io.open(state.metadataPath, "wb")
    if not file then
        return
    end

    file:write(encodeJson({
        schema = "openfreq.dcs.heightmap",
        version = 1,
        theater = state.theater,
        rawPath = state.rawPath,
        metadataPath = state.metadataPath,
        originX = state.originX,
        originZ = state.originZ,
        widthMeters = state.widthMeters,
        heightMeters = state.heightMeters,
        samplesX = state.samplesX,
        samplesZ = state.samplesZ,
        ready = state.ready == true
    }))
    file:close()
end

local function initHeightmap(theater)
    local hmConfig = config.heightmap or {}
    if hmConfig.enabled ~= true or not land or type(land.getHeight) ~= "function" then
        OpenFreqDCS.heightmapState = nil
        return nil
    end

    local samplesX = math.max(32, math.floor(numberOr(hmConfig.samplesX, 4096)))
    local samplesZ = math.max(32, math.floor(numberOr(hmConfig.samplesZ, 4096)))
    local originX = numberOr(hmConfig.originX, -500000)
    local originZ = numberOr(hmConfig.originZ, -500000)
    local widthMeters = numberOr(hmConfig.widthMeters, 1000000)
    local heightMeters = numberOr(hmConfig.heightMeters, 1000000)

    local directory = ensureHeightmapDirectory()
    local baseName = string.format("%s_%d_%d_%d_%d_%d_%d",
        safeFileName(theater), originX, originZ, widthMeters, heightMeters, samplesX, samplesZ)

    local state = {
        theater = theater,
        originX = originX,
        originZ = originZ,
        widthMeters = widthMeters,
        heightMeters = heightMeters,
        samplesX = samplesX,
        samplesZ = samplesZ,
        rowsPerTick = math.max(1, math.floor(numberOr(hmConfig.rowsPerTick, 1))),
        row = 0,
        ready = false,
        rawPath = directory .. "\\" .. baseName .. ".raw",
        metadataPath = directory .. "\\" .. baseName .. ".json",
        file = nil
    }

    if lfs and type(lfs.attributes) == "function" and lfs.attributes(state.rawPath, "size") == (samplesX * samplesZ * 2) then
        state.ready = true
        writeHeightmapMetadata(state)
    else
        state.file = io.open(state.rawPath, "wb")
    end

    OpenFreqDCS.heightmapState = state
    return state
end

local function pumpHeightmap(theater)
    local hmConfig = config.heightmap or {}
    if hmConfig.enabled ~= true then
        return nil
    end

    local state = OpenFreqDCS.heightmapState
    if not state or state.theater ~= theater then
        state = initHeightmap(theater)
    end
    if not state then
        return nil
    end

    if state.ready then
        return state
    end

    if not state.file then
        state.file = io.open(state.rawPath, "ab")
    end
    if not state.file then
        return state
    end

    local rowsThisTick = state.rowsPerTick
    local feetPerMeter = 3.280839895
    while rowsThisTick > 0 and state.row < state.samplesZ do
        local row = state.row
        local z = state.originZ
        if state.samplesZ > 1 then
            z = state.originZ + (state.heightMeters * row / (state.samplesZ - 1))
        end

        for col = 0, state.samplesX - 1 do
            local x = state.originX
            if state.samplesX > 1 then
                x = state.originX + (state.widthMeters * col / (state.samplesX - 1))
            end

            local ok, heightMetersAtPoint = pcall(land.getHeight, { x = x, y = z })
            if not ok or type(heightMetersAtPoint) ~= "number" then
                heightMetersAtPoint = 0
            end
            state.file:write(int16le(heightMetersAtPoint * feetPerMeter))
        end

        state.row = state.row + 1
        rowsThisTick = rowsThisTick - 1
    end

    if state.row >= state.samplesZ then
        state.file:close()
        state.file = nil
        state.ready = true
        writeHeightmapMetadata(state)
    end

    return state
end

local function heightmapPacket(state)
    if not state then
        return nil
    end
    return {
        theater = state.theater,
        rawPath = state.rawPath,
        metadataPath = state.metadataPath,
        originX = state.originX,
        originZ = state.originZ,
        widthMeters = state.widthMeters,
        heightMeters = state.heightMeters,
        samplesX = state.samplesX,
        samplesZ = state.samplesZ,
        ready = state.ready == true
    }
end

local function terrainVisible(ax, ay, az, bx, by, bz)
    if not terrain or type(terrain.isVisible) ~= "function" then
        return false
    end

    local ok, visible = pcall(terrain.isVisible, ax, ay, az, bx, by, bz)
    return ok and visible == true
end

local function calculateLosLoss(localPosition, remotePosition)
    local losConfig = config.los or {}
    local baseOffset = numberOr(losConfig.heightOffsetMeters, 20)
    local maxOffset = numberOr(losConfig.maxHeightOffsetMeters, 200)
    local step = numberOr(losConfig.heightOffsetStepMeters, 20)

    if step <= 0 then step = 20 end
    if maxOffset < baseOffset then maxOffset = baseOffset end

    if terrainVisible(
        localPosition.x, localPosition.y + baseOffset, localPosition.z,
        remotePosition.x, remotePosition.y + baseOffset, remotePosition.z) then
        return 0.0, true
    end

    local offset = baseOffset + step
    while offset < maxOffset do
        if terrainVisible(
            localPosition.x, localPosition.y + offset, localPosition.z,
            remotePosition.x, remotePosition.y + baseOffset, remotePosition.z) then
            return offset / maxOffset, true
        end
        offset = offset + step
    end

    if terrainVisible(
        localPosition.x, localPosition.y + maxOffset, localPosition.z,
        remotePosition.x, remotePosition.y + baseOffset, remotePosition.z) then
        return 0.99, true
    end

    return 1.0, false
end

local function buildLosResponse(request)
    local selfData = callGlobal("LoGetSelfData", nil)
    local localPosition = selfData and selfData.Position or nil
    local terrainAvailable = terrain ~= nil and type(terrain.isVisible) == "function"
    local response = {
        schema = "openfreq.dcs.los.response",
        version = 1,
        requestId = request.requestId or "",
        terrainAvailable = terrainAvailable,
        results = {}
    }

    if not terrainAvailable or not localPosition or type(request.checks) ~= "table" then
        return response
    end

    for _, check in pairs(request.checks) do
        local position = check.position or {}
        if type(check.id) == "string" and
            type(position.x) == "number" and
            type(position.y) == "number" and
            type(position.z) == "number" then
            local loss, visible = calculateLosLoss(localPosition, position)
            response.results[#response.results + 1] = {
                id = check.id,
                loss = loss,
                visible = visible == true
            }
        end
    end

    return response
end

function OpenFreqDCS.startLos()
    local losConfig = config.los or {}
    if losConfig.enabled == false or OpenFreqDCS.losReceiveSocket or not socket then
        return
    end

    local udp = socket.udp()
    if not udp then
        return
    end

    local port = numberOr(losConfig.requestPort, 34322)
    local ok, err = pcall(function()
        udp:setsockname("127.0.0.1", port)
        udp:settimeout(0)
    end)

    if ok then
        OpenFreqDCS.losReceiveSocket = udp
        writeDebug("DCS LOS listener started on UDP " .. tostring(port))
    else
        pcall(function() udp:close() end)
        writeDebug("DCS LOS listener failed on UDP " .. tostring(port) .. ": " .. tostring(err))
    end
end

function OpenFreqDCS.processLosRequests()
    if not OpenFreqDCS.losReceiveSocket or not json or not OpenFreqDCS.udp then
        return
    end

    for _ = 1, 32 do
        local received = OpenFreqDCS.losReceiveSocket:receive()
        if not received then
            return
        end

        local ok, request = pcall(function() return json:decode(received) end)
        if ok and type(request) == "table" and request.schema == "openfreq.dcs.los.request" then
            local response = buildLosResponse(request)
            local payload = encodeJson(response)
            pcall(function() OpenFreqDCS.udp:send(payload) end)
        end
    end
end

local function buildPacket(modelTime)
    local selfData = callGlobal("LoGetSelfData", nil)
    local theater = detectTheater()
    local isA10C2 = boolOr(config.a10c2 and config.a10c2.enabled, true)
        and selfData ~= nil
        and selfData.Name == "A-10C_2"

    local latLongAlt = selfData and selfData.LatLongAlt or {}
    local position = selfData and selfData.Position or nil
    local velocity = callGlobal("LoGetVectorVelocity", nil)
    local heading = selfData and selfData.Heading or nil

    local radios = {}
    if isA10C2 then
        radios = buildA10C2Radios()
    end

    local heightmap = pumpHeightmap(theater)

    return {
        schema = "openfreq.dcs.export",
        version = 1,
        modelTime = modelTime or callGlobal("LoGetModelTime", 0),
        theater = theater,
        unit = selfData and selfData.Name or "",
        unitName = selfData and selfData.UnitName or "",
        playerName = callGlobal("LoGetPilotName", ""),
        isInAircraft = isA10C2,
        isInGame = isA10C2 and position ~= nil,
        latitude = numberOr(latLongAlt.Lat, 0),
        longitude = numberOr(latLongAlt.Long, 0),
        altitudeMsl = numberOr(latLongAlt.Alt, 0),
        headingRadians = type(heading) == "number" and heading or nil,
        position = position and { x = numberOr(position.x, 0), y = numberOr(position.y, 0), z = numberOr(position.z, 0) } or nil,
        velocity = velocity and { x = numberOr(velocity.x, 0), y = numberOr(velocity.y, 0), z = numberOr(velocity.z, 0) } or nil,
        radios = radios,
        heightmap = heightmapPacket(heightmap)
    }
end

function OpenFreqDCS.start()
    if not socket then
        return
    end

    OpenFreqDCS.startLos()

    if OpenFreqDCS.udp then
        return
    end

    local udp = socket.udp()
    if not udp then
        return
    end

    udp:settimeout(0)
    local ok = pcall(function()
        udp:setpeername(config.host or "127.0.0.1", numberOr(config.port, 34321))
    end)
    if ok then
        OpenFreqDCS.udp = udp
    else
        pcall(function() udp:close() end)
    end
end

function OpenFreqDCS.stop()
    if OpenFreqDCS.heightmapState and OpenFreqDCS.heightmapState.file then
        pcall(function() OpenFreqDCS.heightmapState.file:close() end)
    end
    if OpenFreqDCS.udp then
        pcall(function() OpenFreqDCS.udp:close() end)
    end
    if OpenFreqDCS.losReceiveSocket then
        pcall(function() OpenFreqDCS.losReceiveSocket:close() end)
    end
    OpenFreqDCS.udp = nil
    OpenFreqDCS.losReceiveSocket = nil
end

function OpenFreqDCS.export(modelTime)
    local hz = numberOr(config.exportHz, 10)
    if hz < 1 then hz = 1 end

    local now = modelTime or callGlobal("LoGetModelTime", 0)
    if OpenFreqDCS.nextExportTime and now < OpenFreqDCS.nextExportTime then
        return OpenFreqDCS.nextExportTime
    end

    OpenFreqDCS.nextExportTime = now + (1 / hz)

    OpenFreqDCS.start()
    OpenFreqDCS.processLosRequests()
    if OpenFreqDCS.udp then
        local ok, payload = pcall(function()
            return encodeJson(buildPacket(now))
        end)
        if ok and payload then
            pcall(function() OpenFreqDCS.udp:send(payload) end)
        elseif not ok then
            if OpenFreqDCS.nextExportErrorLogTime == nil or now >= OpenFreqDCS.nextExportErrorLogTime then
                OpenFreqDCS.nextExportErrorLogTime = now + 1
                writeDebug("export error: " .. tostring(payload))
            end
        end
    end

    return OpenFreqDCS.nextExportTime
end

-- Debug tool for discovering cockpit argument IDs (e.g. squelch switches) that have no known
-- ID yet. Enable via config.debugArgScan.enabled, then flip one switch at a time in the cockpit
-- and watch Logs\OpenFreqDCS.ArgScan.log for "changed" lines -- the id that changes right as you
-- flip the switch is the argument to use. Disable again once done; it's a perf cost. Kept in its
-- own file (not OpenFreqDCS.log or dcs.log) since it can be high-volume.
local function writeArgScanLog(message)
    if writeDir ~= "" then
        local file = io.open(writeDir .. [[Logs\OpenFreqDCS.ArgScan.log]], "ab")
        if file then
            file:write(os.date("!%Y-%m-%dT%H:%M:%SZ "), tostring(message), "\r\n")
            file:close()
        end
    end
end

local argScanPrevValues = {}

local function scanArgumentsOnce()
    local scanConfig = config.debugArgScan

    if not OpenFreqDCS.argScanConfigLogged then
        OpenFreqDCS.argScanConfigLogged = true
        writeLog(string.format(
            "argScan config seen at load: present=%s enabled=%s minId=%s maxId=%s deviceIds=%s ignoreIds=%s",
            tostring(scanConfig ~= nil),
            tostring(scanConfig and scanConfig.enabled),
            tostring(scanConfig and scanConfig.minId),
            tostring(scanConfig and scanConfig.maxId),
            scanConfig and type(scanConfig.deviceIds) == "table" and table.concat(scanConfig.deviceIds, ",") or "nil",
            scanConfig and type(scanConfig.ignoreIds) == "table" and table.concat(scanConfig.ignoreIds, ",") or "nil"))
    end

    if not scanConfig or scanConfig.enabled ~= true then
        return
    end

    local now = callGlobal("LoGetModelTime", 0)
    local hz = numberOr(scanConfig.scanHz, 20)
    if hz < 1 then hz = 1 end
    if OpenFreqDCS.nextArgScanTime and now < OpenFreqDCS.nextArgScanTime then
        return
    end
    OpenFreqDCS.nextArgScanTime = now + (1 / hz)

    local minId = math.floor(numberOr(scanConfig.minId, 0))
    local maxId = math.floor(numberOr(scanConfig.maxId, 900))
    local deviceIds = scanConfig.deviceIds or { 0 }

    local ignoreIds = {}
    if type(scanConfig.ignoreIds) == "table" then
        for _, ignoreId in ipairs(scanConfig.ignoreIds) do
            ignoreIds[ignoreId] = true
        end
    end

    for _, deviceId in ipairs(deviceIds) do
        local device = getDevice(deviceId)
        if device then
            for id = minId, maxId do
                if not ignoreIds[id] then
                    local value = getArgument(device, id, nil)
                    if type(value) == "number" then
                        local key = deviceId .. ":" .. id
                        local previous = argScanPrevValues[key]
                        if previous ~= nil and math.abs(previous - value) > 0.0005 then
                            writeArgScanLog(string.format(
                                "device=%d id=%d changed %.4f -> %.4f",
                                deviceId, id, previous, value))
                        end
                        argScanPrevValues[key] = value
                    end
                end
            end
        end
    end
end

local previousStart = LuaExportStart
local previousStop = LuaExportStop
local previousAfterNextFrame = LuaExportAfterNextFrame
local previousActivityNextEvent = LuaExportActivityNextEvent

function LuaExportStart()
    if type(previousStart) == "function" then
        pcall(previousStart)
    end
    OpenFreqDCS.start()
end

function LuaExportStop()
    OpenFreqDCS.stop()
    if type(previousStop) == "function" then
        pcall(previousStop)
    end
end

function LuaExportAfterNextFrame()
    if type(previousAfterNextFrame) == "function" then
        pcall(previousAfterNextFrame)
    end
    scanArgumentsOnce()
    OpenFreqDCS.export()
end

function LuaExportActivityNextEvent(t)
    local previousNext = nil
    if type(previousActivityNextEvent) == "function" then
        local ok, result = pcall(previousActivityNextEvent, t)
        if ok and type(result) == "number" then
            previousNext = result
        end
    end

    local ourNext = OpenFreqDCS.export(t)
    if type(previousNext) == "number" and previousNext < ourNext then
        return previousNext
    end
    return ourNext
end
