-- Offline harness for OpenFreqDCS.lua's HAVE QUICK / SINCGARS export (docs/jamming/HAVE_QUICK.md).
-- Loads the export script outside DCS with stubbed list_indication / GetDevice, then checks the ARC-210
-- ECCM page parsing, the CT-TD fix and the ARC-164 virtual HQ knob. Run: lua eccm_harness.lua <OpenFreqDCS.lua>
-- Prints "ECCM HARNESS OK" and exits 0 on success; prints FAIL lines and exits 1 otherwise.
-- Written for Lua 5.1 (DCS) and later.

local scriptPath = arg and arg[1]
if not scriptPath then
    print("usage: lua eccm_harness.lua <path to OpenFreqDCS.lua>")
    os.exit(2)
end

local indication = ""
function list_indication(id)
    if id == 18 then
        return indication
    end
    return ""
end

local args = {}
local panel = {
    get_argument_value = function(_, id)
        return args[id]
    end
}
function GetDevice(id)
    if id == 0 then
        return panel
    end
    return nil
end
function LoGetModelTime()
    return 0
end

dofile(scriptPath)
local E = OpenFreqDCS.eccm
assert(type(E) == "table", "OpenFreqDCS.eccm test hooks missing")

local failures = 0
local function check(condition, message)
    if not condition then
        failures = failures + 1
        print("FAIL: " .. message)
    end
end

-- list_indication text as DCS formats it: a dashed separator, the element name, its value.
local function page(entries)
    local parts = {}
    for _, kv in ipairs(entries) do
        parts[#parts + 1] = "-----------------------------------------\n" .. kv[1] .. "\n" .. kv[2] .. "\n"
    end
    return table.concat(parts)
end

local function show(entries)
    indication = page(entries)
    return E.getListIndicatorValues(18)
end

-- 1. CT-TD is the KY-58 time-delay mode: encrypted, and not HAVE QUICK any more.
do
    local values = show({ { "comsec_submode", "CT-TD" }, { "ky_submode_label", "3" }, { "freq_label_mhz", "251" },
        { "freq_label_khz", "000" } })
    local enc, key, third = E.getArc210ComsecState(values)
    check(enc == true, "CT-TD should be encrypted")
    check(key == 3, "CT-TD key should be 3, got " .. tostring(key))
    check(third == nil, "getArc210ComsecState must not return an HQ flag")
    check(E.getArc210EccmState(values) == nil, "a frequency page is not HAVE QUICK")

    local pt = show({ { "comsec_submode", "PT" }, { "ky_submode_label", "1" } })
    check(E.getArc210ComsecState(pt) == false, "PT is plain")
    local ct = show({ { "comsec_submode", "CT" }, { "ky_submode_label", "2" } })
    check(E.getArc210ComsecState(ct) == true, "CT is encrypted")
end

-- 2. ECCM page, HQ II net A12.300, ready.
do
    local values = show({
        { "active_eccm_channel_index", "1" },
        { "active_eccm_channel_type", "HQII" },
        { "active_eccm_channel_HQI_status", "" },
        { "Net_ID_left_part", "A12" },
        { "Net_ID_right_part", "300" },
        { "AM", "AM" },
        { "comsec_submode", "PT" }
    })
    local eccm = E.parseArc210EccmPage(values)
    check(eccm ~= nil, "ECCM page not recognised")
    if eccm then
        check(eccm.mode == "hq2", "mode hq2, got " .. tostring(eccm.mode))
        check(eccm.net == "A12.300", "net A12.300, got " .. tostring(eccm.net))
        check(eccm.status == "", "ready status should be empty, got '" .. tostring(eccm.status) .. "'")
        check(eccm.eccmChannel == 1, "eccm channel 1")
        check(eccm.source == "cockpit", "source cockpit")
    end
end

-- 3. Readiness strings: NO TIME on HQ I, NO WOD, INVALID NET ID.
do
    local function status(kind, text)
        return E.parseArc210EccmPage(show({
            { "active_eccm_channel_type", kind },
            { "active_eccm_channel_HQI_status", text },
            { "Net_ID_left_part", "A20" },
            { "Net_ID_right_part", "400" }
        }))
    end
    local t = status("HQI", "NO TIME")
    check(t.mode == "hq1" and t.status == "NO TIME", "HQI NO TIME, got " .. tostring(t.mode) .. "/" .. tostring(t.status))
    check(status("HQII", "NO WOD").status == "NO WOD", "NO WOD")
    check(status("HQII", "NO MWOD").status == "NO MWOD", "NO MWOD")
    check(status("HQII", "NO OPER DAY").status == "NO OPER DAY", "NO OPER DAY")
    check(status("HQII", "INVALID NET ID").status == "INVALID NET ID", "INVALID NET ID")
end

-- 4. SINCGARS (" SG" with a leading space), late entry; cold start.
do
    local sg = E.parseArc210EccmPage(show({
        { "active_eccm_channel_type", " SG" },
        { "SG_data", "F001" },
        { "top_line_label", "LE" }
    }))
    check(sg ~= nil and sg.mode == "sg", "SG mode")
    check(sg ~= nil and sg.net == "F001", "SG net F001")
    check(sg ~= nil and sg.sgLateEntry == "LE", "SG late entry")
    local cs = E.parseArc210EccmPage(show({
        { "active_eccm_channel_type", " SG" },
        { "SG_data", "F002" },
        { "COLD ST", "" },
        { "SG_CS_label_mhz", "45" }
    }))
    check(cs ~= nil and cs.mode == "sg_cs", "SG cold start")
end

-- 5. NO FILL channel: ECCM page, nothing loaded.
do
    local off = E.parseArc210EccmPage(show({ { "active_eccm_channel_type", "" }, { "NO FILL", "" } }))
    check(off ~= nil and off.mode == "off" and off.status == "NO FILL", "NO FILL is off")
end

-- 6. Sticky across pages: an HQ net survives a menu page and ends on a frequency page.
do
    local hq = E.getArc210EccmState(show({
        { "active_eccm_channel_type", "HQII" }, { "Net_ID_left_part", "A12" }, { "Net_ID_right_part", "325" },
        { "active_eccm_channel_HQI_status", "" }
    }))
    check(hq ~= nil and hq.net == "A12.325", "HQ read")
    local menu = E.getArc210EccmState(show({ { "menu_title", "HQ MENU" } }))
    check(menu ~= nil and menu.net == "A12.325", "HQ kept on a menu page")
    local single = E.getArc210EccmState(show({ { "freq_label_mhz", "251" }, { "freq_label_khz", "000" } }))
    check(single == nil, "frequency page ends HQ")
end

-- 7. ARC-164 virtual HQ: MNL mode, 100 MHz knob on "A", digits 1 2 . 3 25 -> A12.325.
do
    args[167] = 0.0
    args[162] = 0.2
    args[163] = 0.1
    args[164] = 0.2
    args[165] = 0.3
    args[166] = 0.1
    local v = E.getArc164VirtualHq(panel)
    check(v ~= nil and v.net == "A12.325", "ARC-164 A12.325, got " .. tostring(v and v.net))
    check(v ~= nil and v.mode == "hq2" and v.source == "knob", "ARC-164 virtual mode/source")
    args[166] = 0.3
    check(E.getArc164VirtualHq(panel).net == "A12.375", "ARC-164 .x75 step")
    args[162] = 0.0
    check(E.getArc164VirtualHq(panel) == nil, "knob on 2: single channel")
    args[162] = 0.2
    args[167] = 0.1
    check(E.getArc164VirtualHq(panel) == nil, "preset mode: no virtual HQ")
    args[167] = 0.0
    OpenFreqDCS.config.a10c2 = OpenFreqDCS.config.a10c2 or {}
    OpenFreqDCS.config.a10c2.arc164VirtualHq = { aIndex = 3 }
    check(E.getArc164VirtualHq(panel) == nil, "configured A index respected")
    OpenFreqDCS.config.a10c2.arc164VirtualHq = { enabled = false }
    check(E.getArc164VirtualHq(panel) == nil, "virtual HQ can be disabled")
end

if failures > 0 then
    print(string.format("ECCM HARNESS FAILED (%d)", failures))
    os.exit(1)
end
print("ECCM HARNESS OK")
os.exit(0)
