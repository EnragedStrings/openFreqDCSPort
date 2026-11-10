-- OpenFreq-ServerGameGUI.lua
--
-- OpenFreq server-side DCS GameGUI hook. Install on the DCS *server* only:
--   Saved Games\<instance>\Scripts\Hooks\OpenFreq-ServerGameGUI.lua
-- and configure it with:
--   Saved Games\<instance>\Config\OpenFreq.json
-- See docs/autoconnect/SERVER_ADMIN.md.
--
-- Module "autoConnect": when a player joins, sends that player ONE chat line that the OpenFreq
-- client hook on the player's PC understands, e.g.
--   OPENFREQ/1 host=voice.example.com port=9987 sid=example-main auth=1 -- OpenFreq voice available
-- The line never contains a password.
--
-- Module "jamming" (mission jammers, docs/jamming/MISSION_JAMMERS.md): finds mission units whose
-- unit, static or group name starts with a tag (default "JAM:") and sends their position and alive
-- state to the OpenFreq server as HMAC-signed UDP datagrams. It reads the "jamming" section of the
-- same OpenFreq.json.
--
-- Rules this hook follows so it coexists with DCS-SRS, DCSServerBot and other hooks:
--   * every callback returns nil, except onPlayerTrySendChat, which returns "" only to drop a
--     player's chat line that imitates an OpenFreq advert;
--   * no os.execute, ever. autoConnect does no net.dostring_in and no network I/O. jamming (only
--     when its section is valid and enabled) uses net.dostring_in("server", ...) to read units
--     from the mission scripting environment and one non-blocking LuaSocket UDP socket to send;
--   * work happens only while this DCS instance is a multiplayer server
--     (DCS.isMultiplayer() and DCS.isServer()), checked inside each callback;
--   * a missing or invalid OpenFreq.json (or section) disables that module (fail closed);
--   * nothing blocks a frame and no error propagates into DCS (everything runs in pcall).

local ADVERT_VERSION = 1
local ADVERT_PREFIX = "OPENFREQ/"
local ADVERT_SUFFIX = " -- OpenFreq voice available"
local COMMAND_COOLDOWN_SECONDS = 10
local MAX_LOG_LINES = 200

local OpenFreqServer = { modules = {} }

-- DCS globals, looked up at call time so a test harness (tools/LuaHookTests) can stub them.
local function G(name)
    return rawget(_G, name)
end

local function safeCall(fn, ...)
    if type(fn) ~= "function" then
        return false, nil
    end
    return pcall(fn, ...)
end

local logLines = 0

-- tag is the module name that prefixes the line ("autoconnect", "jamming").
local function writeTaggedLog(tag, message)
    if logLines >= MAX_LOG_LINES then
        return
    end
    logLines = logLines + 1
    local lfs = G("lfs")
    local ok, dir = safeCall(lfs and lfs.writedir)
    if not ok or type(dir) ~= "string" then
        return
    end
    local file = io.open(dir .. "Logs\\OpenFreqServer.log", "ab")
    if file then
        file:write(os.date("!%Y-%m-%dT%H:%M:%SZ "), tag, ": ", tostring(message), "\r\n")
        if logLines == MAX_LOG_LINES then
            file:write(os.date("!%Y-%m-%dT%H:%M:%SZ "), tag, ": log limit reached, further lines suppressed\r\n")
        end
        file:close()
    end
end

local function writeLog(message)
    writeTaggedLog("autoconnect", message)
end

local function isMultiplayerServer()
    local DCS = G("DCS")
    if type(DCS) ~= "table" then
        return false
    end
    local okMp, mp = safeCall(DCS.isMultiplayer)
    local okSrv, srv = safeCall(DCS.isServer)
    return okMp and okSrv and mp == true and srv == true
end

local function serverPlayerId()
    local net = G("net")
    local ok, id = safeCall(net and net.get_server_id)
    if ok and type(id) == "number" then
        return id
    end
    return 1
end

---------------------------------------------------------------------------------------------------
-- Validation (same rules as the client hook and the OpenFreq app)
---------------------------------------------------------------------------------------------------

local function isIPv4(host)
    local a, b, c, d = host:match("^(%d+)%.(%d+)%.(%d+)%.(%d+)$")
    if not a then
        return false
    end
    for _, part in ipairs({ a, b, c, d }) do
        if #part > 3 or (#part > 1 and part:sub(1, 1) == "0") or tonumber(part) > 255 then
            return false
        end
    end
    return true
end

local function isValidHost(host)
    if type(host) ~= "string" then
        return false
    end
    if #host < 1 or #host > 253 or host:find("[^a-z0-9%.%-]") then
        return false
    end
    if isIPv4(host) then
        return true
    end
    if not host:find(".", 1, true) then
        return false
    end
    local labels = 0
    local lastLabel
    for label in (host .. "."):gmatch("([^%.]*)%.") do
        if #label < 1 or #label > 63 or label:sub(1, 1) == "-" or label:sub(-1) == "-" then
            return false
        end
        labels = labels + 1
        lastLabel = label
    end
    -- "1.2.3" or "10.0.0.256" style: numeric-only top label is never a real hostname.
    return labels >= 2 and not lastLabel:match("^%d+$")
end

local function toPort(value)
    local n = tonumber(value)
    if not n or n ~= math.floor(n) or n < 1024 or n > 65535 then
        return nil
    end
    return math.floor(n)
end

local function isValidSid(sid)
    return type(sid) == "string" and #sid >= 1 and #sid <= 32 and not sid:find("[^A-Za-z0-9%._%-]")
end

local function isValidCommand(cmd)
    return type(cmd) == "string" and #cmd >= 2 and #cmd <= 32 and cmd:match("^%-?[%w_]+$") ~= nil
end

---------------------------------------------------------------------------------------------------
-- Config: Saved Games\<instance>\Config\OpenFreq.json
---------------------------------------------------------------------------------------------------

local function readConfigFile()
    local lfs, net = G("lfs"), G("net")
    local okDir, dir = safeCall(lfs and lfs.writedir)
    if not okDir or type(dir) ~= "string" then
        return nil, "lfs.writedir unavailable"
    end
    local path = dir .. "Config\\OpenFreq.json"
    local file = io.open(path, "rb")
    if not file then
        return nil, "no " .. path
    end
    local text = file:read("*a")
    file:close()
    if type(text) ~= "string" or #text == 0 or #text > 65536 then
        return nil, "empty or oversized " .. path
    end
    local ok, decoded = safeCall(net and net.json2lua, text)
    if not ok or type(decoded) ~= "table" then
        return nil, "invalid JSON in " .. path
    end
    return decoded
end

---------------------------------------------------------------------------------------------------
-- Module: autoConnect
---------------------------------------------------------------------------------------------------

local autoConnect = {
    line = nil,          -- the advert line for the current config, nil when disabled/invalid
    command = "-openfreq",
    sentByKey = {},      -- player key -> advert line already sent to that player
    keyById = {},        -- player id -> player key (ucid when available)
    pendingById = {},    -- player id -> true between onPlayerConnect and the first send
    lastCommandById = {},
    sending = false,
}

-- Build the advert from the "autoConnect" section. Returns line or nil, reason.
function autoConnect.buildLine(section)
    if type(section) ~= "table" then
        return nil, "no autoConnect section"
    end
    if section.enabled ~= true then
        return nil, "autoConnect.enabled is not true"
    end
    local host = type(section.host) == "string" and section.host:lower() or nil
    if not isValidHost(host) then
        return nil, "invalid autoConnect.host"
    end
    local port = toPort(section.port)
    if not port then
        return nil, "invalid autoConnect.port (1024-65535)"
    end
    local parts = { ADVERT_PREFIX .. ADVERT_VERSION, "host=" .. host, "port=" .. string.format("%d", port) }
    if section.sid ~= nil then
        if not isValidSid(section.sid) then
            return nil, "invalid autoConnect.sid ([A-Za-z0-9._-]{1,32})"
        end
        parts[#parts + 1] = "sid=" .. section.sid
    end
    parts[#parts + 1] = "auth=" .. ((section.auth == true) and "1" or "0")
    -- Deliberately no password key: passwords are never advertised (design decision 4).
    return table.concat(parts, " ") .. ADVERT_SUFFIX
end

function autoConnect.configure(config)
    local section = type(config) == "table" and (config.autoConnect or config.autoconnect) or nil
    local line, reason = autoConnect.buildLine(section)
    local command = "-openfreq"
    if type(section) == "table" and section.chat_command ~= nil then
        if isValidCommand(section.chat_command) then
            command = section.chat_command:lower()
        else
            writeLog("ignoring invalid chat_command")
        end
    end
    autoConnect.command = command
    local changed = line ~= autoConnect.line
    autoConnect.line = line
    if line then
        writeLog("advert: " .. line)
    else
        writeLog("disabled: " .. tostring(reason))
    end
    return changed
end

local function playerKey(id)
    local net = G("net")
    local ok, ucid = safeCall(net and net.get_player_info, id, "ucid")
    if ok and type(ucid) == "string" and #ucid > 0 then
        return "ucid:" .. ucid
    end
    return "id:" .. tostring(id)
end

local function sendTo(id, line)
    local net = G("net")
    autoConnect.sending = true
    local ok, err = safeCall(net and net.send_chat_to, line, id)
    autoConnect.sending = false
    if not ok then
        writeLog("send_chat_to failed for player " .. tostring(id) .. ": " .. tostring(err))
    end
    return ok
end

-- Sends at most once per player per advert line; force=true is the explicit chat command.
function autoConnect.maybeSend(id, force)
    local line = autoConnect.line
    if not line or id == serverPlayerId() then
        return
    end
    local key = autoConnect.keyById[id] or playerKey(id)
    autoConnect.keyById[id] = key
    if not force and autoConnect.sentByKey[key] == line then
        autoConnect.pendingById[id] = nil
        return
    end
    if sendTo(id, line) then
        autoConnect.sentByKey[key] = line
        autoConnect.pendingById[id] = nil
    end
end

function autoConnect.onMissionLoadEnd(config)
    local changed = autoConnect.configure(config)
    if changed and autoConnect.line then
        -- New or changed advert: send it once to players who are already connected.
        for id, _ in pairs(autoConnect.keyById) do
            autoConnect.maybeSend(id, false)
        end
    end
end

function autoConnect.onPlayerConnect(id)
    if id == serverPlayerId() then
        return
    end
    autoConnect.keyById[id] = playerKey(id)
    autoConnect.pendingById[id] = true
end

function autoConnect.onPlayerStart(id)
    autoConnect.maybeSend(id, false)
end

function autoConnect.onPlayerChangeSlot(id)
    -- Fallback when onPlayerStart has not fired yet for this connection. Later slot changes,
    -- respawns and deaths never re-send.
    if autoConnect.pendingById[id] then
        autoConnect.maybeSend(id, false)
    end
end

function autoConnect.onPlayerDisconnect(id)
    local key = autoConnect.keyById[id]
    if key then
        autoConnect.sentByKey[key] = nil
    end
    autoConnect.keyById[id] = nil
    autoConnect.pendingById[id] = nil
    autoConnect.lastCommandById[id] = nil
end

-- Returns "" to drop the message, nil to let it through unchanged.
function autoConnect.onPlayerTrySendChat(playerID, msg)
    if autoConnect.sending or type(msg) ~= "string" then
        return nil
    end
    local trimmed = msg:gsub("^%s+", "")
    if trimmed:sub(1, #ADVERT_PREFIX):upper() == ADVERT_PREFIX then
        writeLog("dropped advert look-alike from player " .. tostring(playerID))
        return ""
    end
    if autoConnect.line and trimmed:gsub("%s+$", ""):lower() == autoConnect.command then
        local now = os.time()
        local last = autoConnect.lastCommandById[playerID]
        if not last or now - last >= COMMAND_COOLDOWN_SECONDS then
            autoConnect.lastCommandById[playerID] = now
            autoConnect.maybeSend(playerID, true)
        end
    end
    return nil
end

OpenFreqServer.modules.autoConnect = autoConnect

---------------------------------------------------------------------------------------------------
-- Module: jamming (mission jammers; docs/jamming/MISSION_JAMMERS.md)
---------------------------------------------------------------------------------------------------

local JAM_WIRE_MAGIC = "OFJ1"
local JAM_QUERY_MAGIC = "OFJQ1"
local JAM_MAX_UNITS = 200
local JAM_MAX_DATAGRAM = 60000
local JAM_ERROR_LOG_SECONDS = 300
local WORD = 4294967296 -- 2^32

local function jamLog(message)
    writeTaggedLog("jamming", message)
end

-- SHA-256 / HMAC-SHA256 in plain Lua 5.1 arithmetic (no bit library needed; DCS GameGUI is Lua
-- 5.1). Words are numbers in [0, 2^32). Only AND needs a helper: xor(a, b) = a + b - 2*and(a, b),
-- rotations and shifts are exact double arithmetic. AND uses a 256x256 byte table built on first
-- use, or a native AND (bit32, Lua 5.3 '&', LuaJIT bit) when one is present and self-tests OK.

local AND8

local function buildAnd8()
    local nib = {}
    for a = 0, 15 do
        for b = 0, 15 do
            local r, bitv, x, y = 0, 1, a, b
            for _ = 1, 4 do
                if x % 2 == 1 and y % 2 == 1 then
                    r = r + bitv
                end
                x = (x - x % 2) / 2
                y = (y - y % 2) / 2
                bitv = bitv * 2
            end
            nib[a * 16 + b] = r
        end
    end
    local t = {}
    for a = 0, 255 do
        local al = a % 16
        local ah = (a - al) / 16
        for b = 0, 255 do
            local bl = b % 16
            local bh = (b - bl) / 16
            t[a * 256 + b + 1] = nib[ah * 16 + bh] * 16 + nib[al * 16 + bl]
        end
    end
    return t
end

local function bandTable(a, b)
    local t = AND8
    local a0, b0 = a % 256, b % 256
    a, b = (a - a0) / 256, (b - b0) / 256
    local a1, b1 = a % 256, b % 256
    a, b = (a - a1) / 256, (b - b1) / 256
    local a2, b2 = a % 256, b % 256
    local a3, b3 = (a - a2) / 256, (b - b2) / 256
    return t[a0 * 256 + b0 + 1] + t[a1 * 256 + b1 + 1] * 256
        + t[a2 * 256 + b2 + 1] * 65536 + t[a3 * 256 + b3 + 1] * 16777216
end

local SHA_K = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
}

local function rotr(x, n)
    local p = 2 ^ n
    local lo = x % p
    return (x - lo) / p + lo * (WORD / p)
end

local function shr(x, n)
    local p = 2 ^ n
    return (x - x % p) / p
end

local function sha256Words(msg, band)
    local function xor(a, b)
        return a + b - 2 * band(a, b)
    end
    local len = #msg
    local bits = len * 8
    local tail = { msg, "\128", string.rep("\0", (55 - len) % 64) }
    for i = 7, 0, -1 do
        local p = 256 ^ i
        local v = (bits - bits % p) / p
        tail[#tail + 1] = string.char(v % 256)
    end
    msg = table.concat(tail)
    local H = { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 }
    local w = {}
    for block = 1, #msg, 64 do
        for i = 1, 16 do
            local p = block + (i - 1) * 4
            local b1, b2, b3, b4 = msg:byte(p, p + 3)
            w[i] = ((b1 * 256 + b2) * 256 + b3) * 256 + b4
        end
        for i = 17, 64 do
            local x, y = w[i - 15], w[i - 2]
            local s0 = xor(xor(rotr(x, 7), rotr(x, 18)), shr(x, 3))
            local s1 = xor(xor(rotr(y, 17), rotr(y, 19)), shr(y, 10))
            w[i] = (w[i - 16] + s0 + w[i - 7] + s1) % WORD
        end
        local a, b, c, d, e, f, g, h = H[1], H[2], H[3], H[4], H[5], H[6], H[7], H[8]
        for i = 1, 64 do
            local S1 = xor(xor(rotr(e, 6), rotr(e, 11)), rotr(e, 25))
            local ch = band(e, f) + band(WORD - 1 - e, g) -- disjoint bits: + is xor
            local t1 = (h + S1 + ch + SHA_K[i] + w[i]) % WORD
            local S0 = xor(xor(rotr(a, 2), rotr(a, 13)), rotr(a, 22))
            local ab = band(a, b)
            local maj = ab + band(c, a + b - 2 * ab) -- (a&b) | (c&(a^b)), disjoint bits
            local t2 = (S0 + maj) % WORD
            h, g, f, e, d, c, b, a = g, f, e, (d + t1) % WORD, c, b, a, (t1 + t2) % WORD
        end
        H[1], H[2], H[3], H[4] = (H[1] + a) % WORD, (H[2] + b) % WORD, (H[3] + c) % WORD, (H[4] + d) % WORD
        H[5], H[6], H[7], H[8] = (H[5] + e) % WORD, (H[6] + f) % WORD, (H[7] + g) % WORD, (H[8] + h) % WORD
    end
    return H
end

local function wordsToBin(H)
    local out = {}
    for i = 1, 8 do
        local v = H[i]
        local b4 = v % 256; v = (v - b4) / 256
        local b3 = v % 256; v = (v - b3) / 256
        local b2 = v % 256
        local b1 = (v - b2) / 256
        out[i] = string.char(b1, b2, b3, b4)
    end
    return table.concat(out)
end

local function wordsToHex(H)
    local out = {}
    for i = 1, 8 do
        -- Two 16-bit halves: Lua 5.1's %x goes through a C long (32-bit signed on Windows).
        local lo = H[i] % 65536
        out[i] = string.format("%04x%04x", (H[i] - lo) / 65536, lo)
    end
    return table.concat(out)
end

local SHA_ABC = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
local bandImpl, bandName

local function selectBand(name)
    local function useTable()
        AND8 = AND8 or buildAnd8()
        bandImpl, bandName = bandTable, "table"
    end
    if name == "table" then
        useTable()
        return bandName
    end
    local candidates = {}
    local b32 = rawget(_G, "bit32")
    if type(b32) == "table" and type(b32.band) == "function" then
        candidates[#candidates + 1] = { "bit32", b32.band }
    end
    local loader = rawget(_G, "loadstring") or rawget(_G, "load")
    local okL, chunk = pcall(loader, "return function(a, b) return a & b end")
    if okL and type(chunk) == "function" then
        local okC, fn = pcall(chunk)
        if okC and type(fn) == "function" then
            candidates[#candidates + 1] = { "native", fn }
        end
    end
    local bitlib = rawget(_G, "bit")
    if type(bitlib) == "table" and type(bitlib.band) == "function" then
        local bb = bitlib.band
        candidates[#candidates + 1] = { "bit", function(a, b) return bb(a, b) % WORD end }
    end
    for _, c in ipairs(candidates) do
        local ok, hex = pcall(function() return wordsToHex(sha256Words("abc", c[2])) end)
        if ok and hex == SHA_ABC then
            bandImpl, bandName = c[2], c[1]
            return bandName
        end
    end
    useTable()
    return bandName
end

local function sha256Bin(msg)
    if not bandImpl then selectBand() end
    return wordsToBin(sha256Words(msg, bandImpl))
end

local function sha256Hex(msg)
    if not bandImpl then selectBand() end
    return wordsToHex(sha256Words(msg, bandImpl))
end

local function xorBytes(s, pad)
    local out = {}
    for i = 1, #s do
        local c = s:byte(i)
        out[i] = string.char(c + pad - 2 * bandImpl(c, pad))
    end
    return table.concat(out)
end

local function hmacSha256Hex(key, msg)
    if not bandImpl then selectBand() end
    if #key > 64 then
        key = sha256Bin(key)
    end
    key = key .. string.rep("\0", 64 - #key)
    local inner = sha256Bin(xorBytes(key, 0x36) .. msg)
    return wordsToHex(sha256Words(xorBytes(key, 0x5c) .. inner, bandImpl))
end

-- JSON encoding (own encoder rather than net.lua2json: fixed key order, plain numbers and an
-- empty "units" that is always [] -- the HMAC covers these exact bytes).

local function utf8Clean(s)
    if not s:find("[\128-\255]") then
        return s
    end
    local out, i, n = {}, 1, #s
    while i <= n do
        local c = s:byte(i)
        local len, lo, hi = 0, 0x80, 0xBF
        if c < 0x80 then
            len = 1
        elseif c >= 0xC2 and c <= 0xDF then
            len = 2
        elseif c >= 0xE0 and c <= 0xEF then
            len = 3
            if c == 0xE0 then lo = 0xA0 elseif c == 0xED then hi = 0x9F end
        elseif c >= 0xF0 and c <= 0xF4 then
            len = 4
            if c == 0xF0 then lo = 0x90 elseif c == 0xF4 then hi = 0x8F end
        end
        local ok = len > 0 and i + len - 1 <= n
        if ok and len > 1 then
            local b = s:byte(i + 1)
            ok = b >= lo and b <= hi
            for j = i + 2, i + len - 1 do
                b = s:byte(j)
                if b < 0x80 or b > 0xBF then ok = false end
            end
        end
        if ok then
            out[#out + 1] = s:sub(i, i + len - 1)
            i = i + len
        else
            out[#out + 1] = "?"
            i = i + 1
        end
    end
    return table.concat(out)
end

local JSON_ESCAPES = { ['"'] = '\\"', ["\\"] = "\\\\", ["\n"] = "\\n", ["\r"] = "\\r", ["\t"] = "\\t" }

local function jsonString(s)
    s = utf8Clean(tostring(s))
    return '"' .. s:gsub('[%c"\\]', function(c)
        return JSON_ESCAPES[c] or string.format("\\u%04x", c:byte())
    end) .. '"'
end

local function jsonNumber(n)
    if n == math.floor(n) and math.abs(n) < 1e15 then
        return string.format("%.0f", n)
    end
    return string.format("%.10g", n)
end

local function isFiniteNumber(v)
    return type(v) == "number" and v == v and v > -1e15 and v < 1e15
end

-- Mission-environment query, run with net.dostring_in("server", code). The chunk returns one
-- string: a header line "OFJQ1", then one line per unit with 14 '|'-separated fields:
--   U|name|group|kind|side|type|alive|lat|lon|alt|x|y|z|src
-- kind = air|ground|ship|static (empty when the object does not exist), alive = 1|0, position
-- fields empty when unknown, src = u (unit tagged by its own name), g (first unit of a tagged
-- group), s (static). In text fields '\' -> '\\', '|' -> '\p', LF -> '\n', CR -> '\r'.
-- Prepended by buildQuery: PREFIX (string), SCAN (boolean), NAMES ({ {name, src}, ... }).
local QUERY_BODY = [==[
local out, count, seen = { "OFJQ1" }, 0, {}
local function esc(s)
    return (tostring(s):gsub("[\\|\n\r]", function(c)
        if c == "\\" then return "\\\\" elseif c == "|" then return "\\p" elseif c == "\n" then return "\\n" end
        return "\\r"
    end))
end
local function num(v)
    if type(v) == "number" and v == v and v > -1e15 and v < 1e15 then return string.format("%.17g", v) end
    return ""
end
local function m(obj, name, a)
    if obj == nil then return nil end
    local ok, r = pcall(function()
        if a == nil then return obj[name](obj) end
        return obj[name](obj, a)
    end)
    if ok then return r end
    return nil
end
local function tagged(s)
    return type(s) == "string" and s:sub(1, #PREFIX) == PREFIX
end
local function byName(src, name)
    local ok, obj = pcall(function()
        if src == "s" then return StaticObject.getByName(name) end
        return Unit.getByName(name)
    end)
    if ok then return obj end
    return nil
end
local function add(obj, src, name, group)
    if count >= 400 or type(name) ~= "string" or seen[name] then return end
    seen[name] = true
    count = count + 1
    local f = { "U", esc(name), type(group) == "string" and esc(group) or "", "", "", "", "0", "", "", "", "", "", "", src }
    if obj ~= nil and m(obj, "isExist") == true then
        local static = src == "s"
        if static then
            f[4] = "static"
        else
            local desc = m(obj, "getDesc")
            local cat = type(desc) == "table" and desc.category or nil
            local grp = m(obj, "getGroup")
            if type(cat) ~= "number" then cat = m(grp, "getCategory") end
            if cat == 0 or cat == 1 then f[4] = "air" elseif cat == 3 then f[4] = "ship" else f[4] = "ground" end
            if f[3] == "" then
                local gn = m(grp, "getName")
                if type(gn) == "string" then f[3] = esc(gn) end
            end
        end
        f[5] = num(m(obj, "getCoalition"))
        local tn = m(obj, "getTypeName")
        if type(tn) == "string" then f[6] = esc(tn) end
        local active = static or m(obj, "isActive") ~= false
        local life = m(obj, "getLife")
        if active and type(life) == "number" and life > 0 then f[7] = "1" end
        local p = m(obj, "getPoint")
        if type(p) == "table" and type(p.x) == "number" and type(p.y) == "number" and type(p.z) == "number" then
            local ok, lat, lon = pcall(function() return coord.LOtoLL(p) end)
            if ok and type(lat) == "number" and type(lon) == "number" then
                f[8], f[9] = num(lat), num(lon)
            end
            f[10], f[11], f[12], f[13] = num(p.y), num(p.x), num(p.y), num(p.z)
        end
    end
    out[#out + 1] = table.concat(f, "|")
end
if SCAN then
    for side = 0, 2 do
        local okG, groups = pcall(function() return coalition.getGroups(side) end)
        if okG and type(groups) == "table" then
            for _, g in ipairs(groups) do
                local gname = m(g, "getName")
                local units = m(g, "getUnits")
                if type(units) == "table" then
                    for _, u in ipairs(units) do
                        local uname = m(u, "getName")
                        if tagged(uname) then add(u, "u", uname, gname) end
                    end
                end
                if tagged(gname) then
                    local u = m(g, "getUnit", 1)
                    add(u, "g", m(u, "getName"), gname)
                end
            end
        end
        local okS, statics = pcall(function() return coalition.getStaticObjects(side) end)
        if okS and type(statics) == "table" then
            for _, s in ipairs(statics) do
                local sname = m(s, "getName")
                if tagged(sname) then add(s, "s", sname, "") end
            end
        end
    end
end
for _, e in ipairs(NAMES) do
    if not seen[e[1]] then add(byName(e[2], e[1]), e[2], e[1], nil) end
end
return table.concat(out, "\n")
]==]

local QUERY_UNESCAPE = { ["\\"] = "\\", p = "|", n = "\n", r = "\r" }

local jamming = {
    config = nil,       -- validated "jamming" section, nil when disabled
    active = false,     -- true while a mission is running and the module may send
    socketModule = nil, -- LuaSocket, loaded on first enabled mission
    udp = nil,
    ip = nil,
    boot = nil,
    seq = 0,
    mission = "",
    lastMissionId = 0,
    units = {},         -- known tagged units in discovery order: { name, src, group, kind, ... }
    byName = {},
    groups = {},        -- tagged group names that already own a jammer unit
    lastTickAt = nil,
    lastScanAt = nil,
    lastErrorAt = {},
    capLogged = false,
    trimLogged = false,
}

jamming.sha256Hex = sha256Hex
jamming.hmacSha256Hex = hmacSha256Hex
jamming.selectBand = selectBand -- tests: selectBand("table") forces the pure-arithmetic AND
jamming.bandName = function() return bandName end
jamming.queryBody = QUERY_BODY

local function jamLogLimited(key, message)
    local now = os.time()
    local last = jamming.lastErrorAt[key]
    if last and now - last < JAM_ERROR_LOG_SECONDS then
        return
    end
    jamming.lastErrorAt[key] = now
    jamLog(message)
end

local function newBootId()
    local seed = math.floor((os.time() * 1000 + os.clock() * 1000003) % 2147483647)
    pcall(math.randomseed, seed)
    for _ = 1, 3 do
        math.random()
    end
    local hex = {}
    for i = 1, 8 do
        hex[i] = string.format("%x", math.random(0, 15))
    end
    return table.concat(hex)
end

local function clampSeconds(value, default, lo, hi)
    if value == nil then
        return default
    end
    if not isFiniteNumber(value) then
        return nil
    end
    if value < lo then return lo end
    if value > hi then return hi end
    return value
end

-- Validates the "jamming" section. Returns config or nil, reason. Never puts the secret in reason.
function jamming.validate(section)
    if type(section) ~= "table" then
        return nil, "no jamming section"
    end
    if section.enabled ~= true then
        return nil, "jamming.enabled is not true"
    end
    local host = section.host
    if host == nil then
        host = "127.0.0.1"
    end
    host = type(host) == "string" and host:lower() or nil
    if not isValidHost(host) then
        return nil, "invalid jamming.host"
    end
    local port = 9989
    if section.port ~= nil then
        port = toPort(section.port)
        if not port then
            return nil, "invalid jamming.port (1024-65535)"
        end
    end
    local secret = section.secret
    if type(secret) ~= "string" or #secret < 16 or #secret > 1024 then
        return nil, "jamming.secret missing or shorter than 16 characters"
    end
    if secret:upper():find("CHANGE-ME", 1, true) then
        return nil, "jamming.secret is still the example placeholder"
    end
    local interval = clampSeconds(section.intervalSeconds, 2, 1, 30)
    if not interval then
        return nil, "invalid jamming.intervalSeconds"
    end
    local rescan = clampSeconds(section.rescanSeconds, 30, 5, 600)
    if not rescan then
        return nil, "invalid jamming.rescanSeconds"
    end
    local prefix = section.prefix
    if prefix == nil then
        prefix = "JAM:"
    end
    if type(prefix) ~= "string" or #prefix < 1 or #prefix > 16 or prefix:find("[^\32-\126]") then
        return nil, "invalid jamming.prefix (1-16 printable ASCII characters)"
    end
    return {
        host = host, port = port, secret = secret, intervalSeconds = interval,
        rescanSeconds = rescan, prefix = prefix,
    }
end

local function loadSocket()
    if jamming.socketModule then
        return jamming.socketModule
    end
    local ok, mod = pcall(function()
        local pkg = rawget(_G, "package")
        if type(pkg) == "table" then
            if type(pkg.path) == "string" and not pkg.path:find(".\\LuaSocket\\?.lua", 1, true) then
                pkg.path = pkg.path .. ";.\\LuaSocket\\?.lua"
            end
            if type(pkg.cpath) == "string" and not pkg.cpath:find(".\\LuaSocket\\?.dll", 1, true) then
                pkg.cpath = pkg.cpath .. ";.\\LuaSocket\\?.dll"
            end
        end
        return require("socket")
    end)
    if ok and type(mod) == "table" and type(mod.udp) == "function" then
        jamming.socketModule = mod
        return mod
    end
    return nil, tostring(mod)
end

local function resolveHost(socketModule, host)
    if isIPv4(host) then
        return host
    end
    local ok, ip = pcall(function() return socketModule.dns.toip(host) end)
    if ok and type(ip) == "string" and isIPv4(ip) then
        return ip
    end
    return nil
end

function jamming.buildQuery(prefix, scan, units)
    local parts = { "local PREFIX = ", string.format("%q", prefix), "\nlocal SCAN = ", scan and "true" or "false",
        "\nlocal NAMES = {\n" }
    for _, u in ipairs(units) do
        parts[#parts + 1] = string.format("{%q, %q},\n", u.name, u.src)
    end
    parts[#parts + 1] = "}\n"
    parts[#parts + 1] = QUERY_BODY
    return table.concat(parts)
end

-- Parses the query result. Returns a list of records or nil, reason.
function jamming.parseQueryResult(text)
    if type(text) ~= "string" then
        return nil, "no result"
    end
    if text ~= JAM_QUERY_MAGIC and text:sub(1, #JAM_QUERY_MAGIC + 1) ~= JAM_QUERY_MAGIC .. "\n" then
        return nil, "unexpected result: " .. text:sub(1, 200)
    end
    local records = {}
    local first = true
    for line in (text .. "\n"):gmatch("([^\n]*)\n") do
        if first then
            first = false
        elseif line ~= "" then
            local f = {}
            for field in (line .. "|"):gmatch("([^|]*)|") do
                f[#f + 1] = field:gsub("\\(.)", QUERY_UNESCAPE)
            end
            if #f == 14 and f[1] == "U" and f[2] ~= "" and (f[14] == "u" or f[14] == "g" or f[14] == "s") then
                records[#records + 1] = {
                    name = f[2], group = f[3], kind = f[4], side = tonumber(f[5]), type = f[6],
                    alive = f[7] == "1", lat = tonumber(f[8]), lon = tonumber(f[9]), alt = tonumber(f[10]),
                    x = tonumber(f[11]), y = tonumber(f[12]), z = tonumber(f[13]), src = f[14],
                }
            end
        end
    end
    return records
end

local function isTagged(name)
    local prefix = jamming.config and jamming.config.prefix
    return type(name) == "string" and prefix ~= nil and name:sub(1, #prefix) == prefix
end

local function mergeRecord(r)
    local u = jamming.byName[r.name]
    if not u then
        -- A tagged group names one jammer: the unit first found for it. When that unit dies the
        -- next unit of the group does not take over (kill the jammer, end the jamming).
        if r.src == "g" and jamming.groups[r.group] then
            return
        end
        if #jamming.units >= JAM_MAX_UNITS then
            if not jamming.capLogged then
                jamming.capLogged = true
                jamLog("more than " .. JAM_MAX_UNITS .. " tagged units; ignoring the rest")
            end
            return
        end
        u = { name = r.name, src = r.src, group = "" }
        jamming.units[#jamming.units + 1] = u
        jamming.byName[r.name] = u
        if isTagged(r.group) then
            jamming.groups[r.group] = true
        end
    end
    if r.group ~= "" then u.group = r.group end
    if r.kind ~= "" then u.kind = r.kind end
    if r.side then u.side = r.side end
    if r.type ~= "" then u.type = r.type end
    u.alive = r.alive
    u.lat, u.lon, u.alt = r.lat, r.lon, r.alt
    u.x, u.y, u.z = r.x, r.y, r.z
end

function jamming.query(scan)
    local net = G("net")
    local code = jamming.buildQuery(jamming.config.prefix, scan, jamming.units)
    local ok, result = safeCall(net and net.dostring_in, "server", code)
    if not ok then
        jamLogLimited("dostring", "net.dostring_in failed: " .. tostring(result))
        return false
    end
    local records, reason = jamming.parseQueryResult(result)
    if not records then
        jamLogLimited("query", "mission query failed: " .. tostring(reason))
        return false
    end
    for _, r in ipairs(records) do
        mergeRecord(r)
    end
    return true
end

local function unitJson(u)
    local hasPos = isFiniteNumber(u.lat) and isFiniteNumber(u.lon) and isFiniteNumber(u.alt)
    local alive = u.alive == true and hasPos -- a jammer needs a position
    local kind = u.kind or (u.src == "s" and "static" or "ground")
    local parts = {
        '"name":' .. jsonString(u.name),
        '"group":' .. jsonString(u.group or ""),
        '"kind":' .. jsonString(kind),
        '"side":' .. jsonNumber(isFiniteNumber(u.side) and u.side or 0),
        '"type":' .. jsonString(u.type or ""),
        '"alive":' .. (alive and "true" or "false"),
    }
    if hasPos then
        parts[#parts + 1] = '"lat":' .. jsonNumber(u.lat)
        parts[#parts + 1] = '"lon":' .. jsonNumber(u.lon)
        parts[#parts + 1] = '"alt":' .. jsonNumber(u.alt)
        if isFiniteNumber(u.x) and isFiniteNumber(u.y) and isFiniteNumber(u.z) then
            parts[#parts + 1] = '"x":' .. jsonNumber(u.x)
            parts[#parts + 1] = '"y":' .. jsonNumber(u.y)
            parts[#parts + 1] = '"z":' .. jsonNumber(u.z)
        end
    end
    return "{" .. table.concat(parts, ",") .. "}"
end

-- Builds the signed datagram for mission/units. Returns datagram, json.
function jamming.buildDatagram(secret, mission, units)
    jamming.seq = jamming.seq + 1
    local head = '{"v":1,"boot":' .. jsonString(jamming.boot) .. ',"seq":' .. jsonNumber(jamming.seq)
        .. ',"ts":' .. jsonNumber(os.time()) .. ',"mission":' .. jsonString(mission) .. ',"units":['
    local budget = JAM_MAX_DATAGRAM - (#JAM_WIRE_MAGIC + 66) - #head - 2
    local items, used = {}, 0
    for i = 1, math.min(#units, JAM_MAX_UNITS) do
        local item = unitJson(units[i])
        local cost = #item + (i > 1 and 1 or 0)
        if used + cost > budget then
            if not jamming.trimLogged then
                jamming.trimLogged = true
                jamLog("datagram over " .. JAM_MAX_DATAGRAM .. " bytes; sending the first " .. (i - 1) .. " units only")
            end
            break
        end
        items[#items + 1] = item
        used = used + cost
    end
    local json = head .. table.concat(items, ",") .. "]}"
    return JAM_WIRE_MAGIC .. " " .. hmacSha256Hex(secret, json) .. " " .. json, json
end

function jamming.send(mission, units)
    local cfg = jamming.config
    if not cfg or not jamming.udp or not jamming.ip then
        return false
    end
    local datagram = jamming.buildDatagram(cfg.secret, mission, units)
    local ok, sent, err = pcall(function() return jamming.udp:sendto(datagram, jamming.ip, cfg.port) end)
    if not ok or not sent then
        jamLogLimited("send", "UDP send to " .. jamming.ip .. ":" .. cfg.port .. " failed: " .. tostring(ok and err or sent))
        return false
    end
    return true
end

local function realTime()
    local DCS = G("DCS")
    local ok, t = safeCall(DCS and DCS.getRealTime)
    if ok and isFiniteNumber(t) then
        return t
    end
    return os.clock()
end

function jamming.deactivate()
    jamming.active = false
end

-- One update: lookup (or full scan when due), then send. Sends nothing when the query fails, so
-- the OpenFreq server's staleAfterSeconds switches the jammers off (fail closed).
function jamming.tick(now)
    jamming.lastTickAt = now
    local scan = jamming.lastScanAt == nil or now - jamming.lastScanAt >= jamming.config.rescanSeconds
    if not jamming.query(scan) then
        return
    end
    if scan then
        jamming.lastScanAt = now
    end
    jamming.send(jamming.mission, jamming.units)
end

function jamming.onMissionLoadEnd(config)
    jamming.active = false
    local section = type(config) == "table" and config.jamming or nil
    local cfg, reason = jamming.validate(section)
    jamming.config = cfg
    if not cfg then
        jamLog("disabled: " .. tostring(reason))
        return
    end
    local socketModule, err = loadSocket()
    if not socketModule then
        jamLog("disabled: LuaSocket did not load: " .. tostring(err))
        return
    end
    local ip = resolveHost(socketModule, cfg.host)
    if not ip then
        jamLog("disabled: could not resolve " .. cfg.host)
        return
    end
    if not jamming.udp then
        local ok, udp = pcall(socketModule.udp)
        if not ok or udp == nil then
            jamLog("disabled: could not create a UDP socket: " .. tostring(udp))
            return
        end
        pcall(function() udp:settimeout(0) end)
        jamming.udp = udp
    end
    jamming.ip = ip
    jamming.boot = jamming.boot or newBootId()
    local DCS = G("DCS")
    local okName, name = safeCall(DCS and DCS.getMissionName)
    if not okName or type(name) ~= "string" then
        name = ""
    end
    local id = math.max(os.time(), jamming.lastMissionId + 1)
    jamming.lastMissionId = id
    jamming.mission = name .. "|" .. jsonNumber(id)
    jamming.units, jamming.byName, jamming.groups = {}, {}, {}
    jamming.lastScanAt, jamming.lastTickAt = nil, nil
    jamming.capLogged, jamming.trimLogged = false, false
    jamming.active = true
    jamLog(string.format("sending to %s:%d every %gs (rescan %gs, prefix %s)", ip, cfg.port,
        cfg.intervalSeconds, cfg.rescanSeconds, cfg.prefix))
    jamming.tick(realTime())
end

function jamming.onSimulationFrame()
    if not jamming.active then
        return
    end
    local now = realTime()
    if jamming.lastTickAt and now - jamming.lastTickAt < jamming.config.intervalSeconds then
        return
    end
    if not isMultiplayerServer() then
        jamming.lastTickAt = now
        return
    end
    jamming.tick(now)
end

-- The mission is over: one datagram with mission "" switches every mission jammer off.
function jamming.onSimulationStop()
    if not jamming.active then
        return
    end
    jamming.active = false
    jamming.send("", {})
end

OpenFreqServer.modules.jamming = jamming

---------------------------------------------------------------------------------------------------
-- DCS callbacks
---------------------------------------------------------------------------------------------------

local callbacks = {}

local function reloadConfig()
    local config, reason = readConfigFile()
    if not config then
        writeLog("config not loaded: " .. tostring(reason))
    end
    return config
end

function callbacks.onMissionLoadEnd()
    if not isMultiplayerServer() then
        pcall(jamming.deactivate)
        return
    end
    local config = reloadConfig()
    pcall(autoConnect.onMissionLoadEnd, config)
    pcall(jamming.onMissionLoadEnd, config)
end

function callbacks.onSimulationFrame()
    pcall(jamming.onSimulationFrame)
end

function callbacks.onSimulationStop()
    pcall(jamming.onSimulationStop)
end

function callbacks.onPlayerConnect(id)
    if not isMultiplayerServer() then
        return
    end
    pcall(autoConnect.onPlayerConnect, id)
end

function callbacks.onPlayerStart(id)
    if not isMultiplayerServer() then
        return
    end
    pcall(autoConnect.onPlayerStart, id)
end

function callbacks.onPlayerChangeSlot(id)
    if not isMultiplayerServer() then
        return
    end
    pcall(autoConnect.onPlayerChangeSlot, id)
end

function callbacks.onPlayerDisconnect(id)
    pcall(autoConnect.onPlayerDisconnect, id)
end

function callbacks.onPlayerTrySendChat(playerID, msg, to)
    if not isMultiplayerServer() then
        return nil
    end
    local ok, result = pcall(autoConnect.onPlayerTrySendChat, playerID, msg)
    if ok and result == "" then
        return ""
    end
    return nil
end

OpenFreqServer.callbacks = callbacks

-- Initial read at hook load; re-read on every onMissionLoadEnd so a config change applies on the
-- next mission without restarting DCS.
pcall(autoConnect.configure, reloadConfig())

local DCS = G("DCS")
if type(DCS) == "table" and type(DCS.setUserCallbacks) == "function" then
    DCS.setUserCallbacks(callbacks)
end

return OpenFreqServer
