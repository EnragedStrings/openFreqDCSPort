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
-- The line never contains a password. A future "jamming" module lives in this same file and
-- reads its own section of the same OpenFreq.json.
--
-- Rules this hook follows so it coexists with DCS-SRS, DCSServerBot and other hooks:
--   * every callback returns nil, except onPlayerTrySendChat, which returns "" only to drop a
--     player's chat line that imitates an OpenFreq advert;
--   * no net.dostring_in, no os.execute, no network I/O;
--   * work happens only while this DCS instance is a multiplayer server
--     (DCS.isMultiplayer() and DCS.isServer()), checked inside each callback;
--   * a missing or invalid OpenFreq.json disables everything (fail closed).

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

local function writeLog(message)
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
        file:write(os.date("!%Y-%m-%dT%H:%M:%SZ "), "autoconnect: ", tostring(message), "\r\n")
        if logLines == MAX_LOG_LINES then
            file:write(os.date("!%Y-%m-%dT%H:%M:%SZ "), "autoconnect: log limit reached, further lines suppressed\r\n")
        end
        file:close()
    end
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
-- OpenFreqServer.modules.jamming = ... (reserved: reads the "jamming" section of OpenFreq.json)

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
        return
    end
    pcall(autoConnect.onMissionLoadEnd, reloadConfig())
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
