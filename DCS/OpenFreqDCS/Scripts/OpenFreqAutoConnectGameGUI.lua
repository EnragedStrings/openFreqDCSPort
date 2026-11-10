-- OpenFreqAutoConnectGameGUI.lua
--
-- OpenFreq client-side DCS auto-connect logic. Loaded by the thin hook
-- Saved Games\<DCS>\Scripts\Hooks\OpenFreq-ClientGameGUI.lua, which the OpenFreq app installs and
-- repairs. See docs/autoconnect/AUTOCONNECT_DESIGN.md (sections 5.2, 6, 7, 8, 9).
--
-- What it does, only while this DCS is a multiplayer CLIENT (not a server, not single player):
--   * tracks the DCS server session with onNetConnect / onNetDisconnect only (slot changes,
--     respawns, loading screens and mission rotation are deliberately ignored);
--   * accepts an "OPENFREQ/1 ..." chat line only from the joined server's own player id, parses
--     and validates it strictly, and forwards {host, port, sid, auth} as one JSON datagram to the
--     OpenFreq app on 127.0.0.1 (port and token come from the app-written client.json);
--   * if the app does not answer and the user enabled auto-launch IN THE APP, starts the exe
--     path the app itself recorded in client.json (nothing from the server reaches the command
--     line), at most once per DCS session.
--
-- Every callback returns nil so other hooks (DCS-SRS, DCSServerBot, ...) keep working.
-- No net.dostring_in, ever.

local M = {}

M.ADVERT_MAX_VERSION = 1
M.ADVERT_MAX_BYTES = 300
M.ACK_TIMEOUT_BEFORE_LAUNCH = 3   -- seconds without an ack before auto-launch is considered
M.RETRY_INTERVAL = 2              -- seconds between re-sends of an un-acked request
M.RETRY_BUDGET = 90               -- seconds a request is retried in total
M.POLL_INTERVAL = 0.25            -- onSimulationFrame work is throttled to ~4 Hz
M.EXE_NAME = "openfreq.client.exe"
M.LAUNCH_FLAG = "--launched-by-dcs"
M.MAX_LOG_LINES = 300

---------------------------------------------------------------------------------------------------
-- Pure helpers (exported for tools/LuaHookTests)
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

-- Lowercase IPv4 literal or RFC 1123 hostname with at least one dot. No IPv6, no '@', '/',
-- '%', ':', spaces or quotes (anything outside [a-z0-9.-] is rejected).
function M.isValidHost(host)
    if type(host) ~= "string" or #host < 1 or #host > 253 or host:find("[^a-z0-9%.%-]") then
        return false
    end
    if isIPv4(host) then
        return true
    end
    if not host:find(".", 1, true) then
        return false
    end
    local labels, lastLabel = 0, nil
    for label in (host .. "."):gmatch("([^%.]*)%.") do
        if #label < 1 or #label > 63 or label:sub(1, 1) == "-" or label:sub(-1) == "-" then
            return false
        end
        labels = labels + 1
        lastLabel = label
    end
    return labels >= 2 and not lastLabel:match("^%d+$")
end

function M.parsePort(text)
    if type(text) ~= "string" or not text:match("^%d%d?%d?%d?%d?$") then
        return nil
    end
    local n = tonumber(text)
    if n < 1024 or n > 65535 then
        return nil
    end
    return n
end

-- Parses one advert line. Returns {version, host, port, sid, auth} or nil, reason.
-- Unknown keys are ignored (forward compatibility); "pw" is never read.
function M.parseAdvert(line)
    if type(line) ~= "string" then
        return nil, "not a string"
    end
    if #line > M.ADVERT_MAX_BYTES then
        return nil, "too long"
    end
    if line:find("[%c]") then
        return nil, "control characters"
    end
    local version, rest = line:match("^OPENFREQ/(%d+) (.+)$")
    if not version then
        return nil, "no OPENFREQ/<v> prefix"
    end
    version = tonumber(version)
    if version < 1 or version > M.ADVERT_MAX_VERSION then
        return nil, "unsupported version " .. tostring(version)
    end
    local human = rest:find(" -- ", 1, true)
    if human then
        rest = rest:sub(1, human - 1)
    end
    local fields = {}
    for token in rest:gmatch("%S+") do
        local key, value = token:match("^([a-z]+)=(%S+)$")
        if not key then
            return nil, "malformed token"
        end
        if fields[key] ~= nil then
            return nil, "duplicate key " .. key
        end
        fields[key] = value
    end
    local host = fields.host
    if not M.isValidHost(host) then
        return nil, "invalid host"
    end
    local port = M.parsePort(fields.port)
    if not port then
        return nil, "invalid port"
    end
    local sid = fields.sid
    if sid ~= nil and (#sid > 32 or sid:find("[^A-Za-z0-9%._%-]")) then
        return nil, "invalid sid"
    end
    local auth = 0
    if fields.auth ~= nil then
        if fields.auth ~= "0" and fields.auth ~= "1" then
            return nil, "invalid auth"
        end
        auth = tonumber(fields.auth)
    end
    return { version = version, host = host, port = port, sid = sid, auth = auth }
end

-- Validates the app-recorded exe path before it is ever handed to the shell.
function M.isLaunchableExePath(exe)
    if type(exe) ~= "string" or #exe < 4 or #exe > 260 then
        return false, "missing"
    end
    if not exe:match("^%a:[\\/]") then
        return false, "not an absolute drive path"
    end
    if exe:find('["%%%^&|<>!\r\n]') then
        return false, "forbidden character"
    end
    if exe:find("[\128-\255]") then
        return false, "non-ASCII path (not supported by os.execute)"
    end
    local name = exe:match("([^\\/]+)$")
    if not name or name:lower() ~= M.EXE_NAME then
        return false, "file name is not OpenFreq.Client.exe"
    end
    return true
end

-- Minimal deterministic JSON encoder for the request datagrams (objects with sorted keys,
-- strings, finite numbers, booleans).
local function encodeString(s)
    return '"' .. s:gsub('[%c"\\]', function(c)
        if c == '"' then return '\\"' end
        if c == "\\" then return "\\\\" end
        if c == "\n" then return "\\n" end
        if c == "\r" then return "\\r" end
        if c == "\t" then return "\\t" end
        return string.format("\\u%04x", c:byte())
    end) .. '"'
end

function M.encodeJson(value)
    local t = type(value)
    if t == "string" then
        return encodeString(value)
    elseif t == "number" then
        if value ~= value or value == math.huge or value == -math.huge then
            return "null"
        end
        if value == math.floor(value) and math.abs(value) < 2 ^ 53 then
            return string.format("%d", value)
        end
        return string.format("%.17g", value)
    elseif t == "boolean" then
        return value and "true" or "false"
    elseif t == "table" then
        local keys = {}
        for k, _ in pairs(value) do
            keys[#keys + 1] = tostring(k)
        end
        table.sort(keys)
        local parts = {}
        for _, k in ipairs(keys) do
            parts[#parts + 1] = encodeString(k) .. ":" .. M.encodeJson(value[k])
        end
        return "{" .. table.concat(parts, ",") .. "}"
    end
    return "null"
end

---------------------------------------------------------------------------------------------------
-- Hook instance
---------------------------------------------------------------------------------------------------

-- deps: { DCS, net, lfs, socket, now = function() seconds end, execute = function(cmd) end,
--         writedir = "...\\" (optional override) }. Missing deps fall back to DCS globals.
function M.create(deps)
    deps = deps or {}
    local DCS = deps.DCS or rawget(_G, "DCS")
    local net = deps.net or rawget(_G, "net")
    local lfs = deps.lfs or rawget(_G, "lfs")
    local execute = deps.execute or os.execute

    local function now()
        if deps.now then
            return deps.now()
        end
        if DCS and type(DCS.getRealTime) == "function" then
            local ok, t = pcall(DCS.getRealTime)
            if ok and type(t) == "number" then
                return t
            end
        end
        return os.clock()
    end

    local writeDir = deps.writedir
    if not writeDir and lfs and type(lfs.writedir) == "function" then
        local ok, dir = pcall(lfs.writedir)
        if ok and type(dir) == "string" then
            writeDir = dir
        end
    end

    local logLines = 0
    local function log(message)
        if logLines >= M.MAX_LOG_LINES or not writeDir then
            return
        end
        logLines = logLines + 1
        local file = io.open(writeDir .. "Logs\\OpenFreqDCS.log", "ab")
        if file then
            file:write(os.date("!%Y-%m-%dT%H:%M:%SZ "), "autoconnect: ", tostring(message), "\r\n")
            if logLines == M.MAX_LOG_LINES then
                file:write(os.date("!%Y-%m-%dT%H:%M:%SZ "), "autoconnect: log limit reached\r\n")
            end
            file:close()
        end
    end

    local socket = deps.socket
    local function getSocketLib()
        if socket then
            return socket
        end
        pcall(function()
            if package and package.path and not package.path:find([[.\LuaSocket\?.lua]], 1, true) then
                package.path = package.path .. [[;.\LuaSocket\?.lua]]
            end
            if package and package.cpath and not package.cpath:find([[.\LuaSocket\?.dll]], 1, true) then
                package.cpath = package.cpath .. [[;.\LuaSocket\?.dll]]
            end
            socket = require("socket")
        end)
        return socket
    end

    local function call(fn, ...)
        if type(fn) ~= "function" then
            return nil
        end
        local ok, result = pcall(fn, ...)
        if ok then
            return result
        end
        return nil
    end

    local function isMultiplayerClient()
        return DCS ~= nil and call(DCS.isMultiplayer) == true and call(DCS.isServer) ~= true
    end

    -- client.json: written by the OpenFreq app only. Re-read on every send attempt so an app
    -- restart (new token) or a settings change applies without restarting DCS.
    local function readClientConfig()
        if not writeDir then
            return nil, "no write dir"
        end
        local file = io.open(writeDir .. "Mods\\Services\\OpenFreqDCS\\AutoConnect\\client.json", "rb")
        if not file then
            return nil, "client.json missing (start OpenFreq once)"
        end
        local text = file:read("*a")
        file:close()
        if type(text) ~= "string" or #text == 0 or #text > 8192 then
            return nil, "client.json empty or oversized"
        end
        local decoded = net and call(net.json2lua, text)
        if type(decoded) ~= "table" or decoded.v ~= 1 then
            return nil, "client.json invalid"
        end
        local port = tonumber(decoded.port)
        if not port or port ~= math.floor(port) or port < 1024 or port > 65535 then
            return nil, "client.json port invalid"
        end
        if type(decoded.token) ~= "string" or not decoded.token:match("^[0-9a-f]+$")
            or #decoded.token < 32 or #decoded.token > 128 then
            return nil, "client.json token invalid"
        end
        return {
            port = math.floor(port),
            token = decoded.token,
            exe = type(decoded.exe) == "string" and decoded.exe or nil,
            autoConnect = decoded.autoConnect == true,
            autoLaunch = decoded.autoLaunch == true,
        }
    end

    local udp
    local function getUdp()
        if udp then
            return udp
        end
        local lib = getSocketLib()
        if not lib or type(lib.udp) ~= "function" then
            return nil
        end
        local s = call(lib.udp)
        if not s then
            return nil
        end
        local ok = pcall(function()
            s:setsockname("127.0.0.1", 0)
            s:settimeout(0)
        end)
        if not ok then
            pcall(function() s:close() end)
            return nil
        end
        udp = s
        return udp
    end

    local session = nil      -- { id, dcsServer, since, seq, seen = {}, launched, hinted }
    local pending = nil      -- { message, seq, firstSent, lastSent, line }
    local lastPoll = nil
    local counter = 0

    local function newSessionId()
        counter = counter + 1
        return string.format("%08x%04x%04x", os.time() % 4294967296, math.random(0, 65535), counter % 65536)
    end

    local function serverHost()
        local host = net and call(net.get_server_host)
        if type(host) == "string" and #host <= 255 then
            return host
        end
        return ""
    end

    local function sendMessage(message, cfg)
        local s = getUdp()
        if not s then
            log("LuaSocket unavailable; cannot reach the OpenFreq app")
            return false
        end
        message.token = cfg.token
        local ok = pcall(function()
            s:sendto(M.encodeJson(message), "127.0.0.1", cfg.port)
        end)
        message.token = nil
        return ok
    end

    local function nextSeq()
        session.seq = session.seq + 1
        return session.seq
    end

    local function sessionInfo()
        if session.dcsServer == "" then
            session.dcsServer = serverHost()
        end
        return { id = session.id, dcsServer = session.dcsServer, since = session.since }
    end

    local function tryLaunch(cfg)
        if session.launched then
            return
        end
        session.launched = true
        if not (cfg.autoConnect and cfg.autoLaunch) then
            log("app not answering; auto-launch is off in OpenFreq")
            return
        end
        local ok, reason = M.isLaunchableExePath(cfg.exe)
        if not ok then
            log("not launching: exe path rejected (" .. tostring(reason) .. ")")
            return
        end
        local file = io.open(cfg.exe, "rb")
        if not file then
            log("not launching: exe not found at recorded path")
            return
        end
        file:close()
        log("launching OpenFreq from the path recorded by the app")
        -- `start` returns immediately, so DCS is not blocked while the app starts. The command
        -- starts with `start`, not a quote, so cmd /c keeps the quoting as written.
        local okExec, err = pcall(execute, 'start "" "' .. cfg.exe .. '" ' .. M.LAUNCH_FLAG)
        if not okExec then
            log("launch failed: " .. tostring(err))
        end
    end

    local function showLocalHint(text)
        if net and type(net.recv_chat) == "function" then
            pcall(net.recv_chat, text)
        end
    end

    local function handleAck(data)
        local decoded = net and call(net.json2lua, data)
        if type(decoded) ~= "table" or decoded.type ~= "openfreq.dcs.ack" or not pending then
            return
        end
        if tonumber(decoded.seq) ~= pending.seq then
            return
        end
        local state = decoded.state
        log("app acknowledged request " .. tostring(pending.seq) .. ": " .. tostring(state))
        if state == "pending-consent" and session and not session.hinted then
            session.hinted = true
            showLocalHint("OpenFreq: switch to the OpenFreq window to approve voice auto-connect.")
        end
        pending = nil
    end

    local function pollAcks(cfg)
        if not udp then
            return
        end
        for _ = 1, 8 do
            local data, ip, port
            local ok = pcall(function()
                data, ip, port = udp:receivefrom()
            end)
            if not ok or not data then
                return
            end
            if ip == "127.0.0.1" and tonumber(port) == cfg.port and #data <= 2048 then
                handleAck(data)
            end
        end
    end

    local function pump(force)
        if not pending or not session then
            return
        end
        local t = now()
        local cfg, reason = readClientConfig()
        if not cfg then
            if not pending.configWarned then
                pending.configWarned = true
                log("cannot forward advert: " .. tostring(reason))
            end
            if t - pending.firstSent > M.RETRY_BUDGET then
                pending = nil
            end
            return
        end
        pollAcks(cfg)
        if not pending then
            return
        end
        if t - pending.firstSent > M.RETRY_BUDGET then
            log("giving up on request " .. tostring(pending.seq) .. ": OpenFreq did not answer")
            if session.seen then
                session.seen[pending.line] = nil   -- a later identical advert may try again
            end
            pending = nil
            return
        end
        if t - pending.firstSent >= M.ACK_TIMEOUT_BEFORE_LAUNCH then
            tryLaunch(cfg)
        end
        if force or t - pending.lastSent >= M.RETRY_INTERVAL then
            pending.lastSent = t
            sendMessage(pending.message, cfg)
        end
    end

    local cb = {}

    function cb.onNetConnect(localPlayerID)
        if not isMultiplayerClient() then
            return
        end
        session = { id = newSessionId(), dcsServer = serverHost(), since = os.time(), seq = 0, seen = {} }
        pending = nil
        log("joined DCS server " .. session.dcsServer .. " (session " .. session.id .. ")")
    end

    function cb.onNetDisconnect(reason, code)
        if not session then
            return
        end
        local leaving = session
        pending = nil
        local cfg = readClientConfig()
        if cfg then
            local message = {
                type = "openfreq.dcs.autoconnect", v = 1, seq = nextSeq(), action = "leave",
                session = { id = leaving.id, dcsServer = leaving.dcsServer, since = leaving.since },
                reason = tostring(reason or ""):sub(1, 100),
                code = tonumber(code) or 0,
            }
            -- Loopback UDP; sent twice (same seq, the app de-duplicates) for robustness.
            sendMessage(message, cfg)
            sendMessage(message, cfg)
        end
        log("left DCS server (session " .. leaving.id .. ")")
        session = nil
    end

    function cb.onChatMessage(message, from)
        if not session or not isMultiplayerClient() then
            return
        end
        if type(message) ~= "string" or message:sub(1, 9) ~= "OPENFREQ/" then
            return
        end
        local serverId = net and call(net.get_server_id)
        if type(serverId) ~= "number" or from ~= serverId then
            log("ignored advert-like line from player id " .. tostring(from))
            return
        end
        if session.seen[message] then
            return
        end
        local advert, reason = M.parseAdvert(message)
        if not advert then
            log("rejected advert: " .. tostring(reason))
            return
        end
        session.seen[message] = true
        pending = {
            line = message,
            seq = nextSeq(),
            firstSent = now(),
            lastSent = now(),
        }
        pending.message = {
            type = "openfreq.dcs.autoconnect", v = 1, seq = pending.seq, action = "connect",
            session = sessionInfo(),
            target = { host = advert.host, port = advert.port, sid = advert.sid, auth = advert.auth },
        }
        log("advert accepted: " .. advert.host .. ":" .. tostring(advert.port))
        pump(true)
    end

    function cb.onSimulationFrame()
        if not pending then
            return
        end
        local t = now()
        if lastPoll and t - lastPoll < M.POLL_INTERVAL then
            return
        end
        lastPoll = t
        pump(false)
    end

    -- Wrap every callback: never raise into DCS, always return nil.
    local callbacks = {}
    for name, fn in pairs(cb) do
        callbacks[name] = function(...)
            local ok, err = pcall(fn, ...)
            if not ok then
                log(name .. " error: " .. tostring(err))
            end
            return nil
        end
    end

    -- Test/diagnostic accessors (not used by DCS).
    local api = {
        getSession = function() return session end,
        getPending = function() return pending end,
    }
    return callbacks, api
end

return M
