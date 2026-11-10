-- Lua test harness for the OpenFreq DCS GameGUI hooks (auto-connect, mission jammers).
--
--   lua tools/LuaHookTests/run.lua [repoRoot]
--
-- Runs with plain Lua 5.1-5.4 and no DCS: DCS, net, lfs, LuaSocket, io.open (a virtual file
-- system), os.execute, the clock and the DCS mission scripting environment (what
-- net.dostring_in("server", ...) runs in) are all stubbed. Exit code 0 = all tests passed.
-- OpenFreq.Client.Tests/LuaHookHarnessTests.cs runs this when a `lua` executable is on PATH.

local repoRoot = arg and arg[1] or "."
repoRoot = repoRoot:gsub("[\\/]$", "")

local SERVER_HOOK = repoRoot .. "/DCS/Server/OpenFreq-ServerGameGUI.lua"
local CLIENT_MODULE = repoRoot .. "/DCS/OpenFreqDCS/Scripts/OpenFreqAutoConnectGameGUI.lua"
local CLIENT_LOADER = repoRoot .. "/DCS/OpenFreqDCS/Hooks/OpenFreq-ClientGameGUI.lua"

---------------------------------------------------------------------------------------------------
-- Tiny JSON decoder (stub for net.json2lua)
---------------------------------------------------------------------------------------------------

local function jsonDecode(text)
    local pos = 1
    local function ws()
        pos = text:find("[^ \t\r\n]", pos) or (#text + 1)
    end
    local value
    local function str()
        pos = pos + 1
        local out = {}
        while true do
            local c = text:sub(pos, pos)
            if c == "" then error("unterminated string") end
            if c == '"' then pos = pos + 1; break end
            if c == "\\" then
                local e = text:sub(pos + 1, pos + 1)
                local map = { ['"'] = '"', ["\\"] = "\\", ["/"] = "/", n = "\n", r = "\r", t = "\t", b = "\b", f = "\f" }
                if e == "u" then
                    out[#out + 1] = string.char(tonumber(text:sub(pos + 2, pos + 5), 16) % 256)
                    pos = pos + 6
                else
                    out[#out + 1] = map[e] or e
                    pos = pos + 2
                end
            else
                out[#out + 1] = c
                pos = pos + 1
            end
        end
        return table.concat(out)
    end
    function value()
        ws()
        local c = text:sub(pos, pos)
        if c == "{" then
            pos = pos + 1
            local obj = {}
            ws()
            if text:sub(pos, pos) == "}" then pos = pos + 1; return obj end
            while true do
                ws()
                local k = str()
                ws()
                assert(text:sub(pos, pos) == ":", "expected :")
                pos = pos + 1
                obj[k] = value()
                ws()
                local d = text:sub(pos, pos)
                pos = pos + 1
                if d == "}" then return obj end
                assert(d == ",", "expected ,")
            end
        elseif c == "[" then
            pos = pos + 1
            local arr = {}
            ws()
            if text:sub(pos, pos) == "]" then pos = pos + 1; return arr end
            while true do
                arr[#arr + 1] = value()
                ws()
                local d = text:sub(pos, pos)
                pos = pos + 1
                if d == "]" then return arr end
                assert(d == ",", "expected ,")
            end
        elseif c == '"' then
            return str()
        elseif text:sub(pos, pos + 3) == "true" then
            pos = pos + 4; return true
        elseif text:sub(pos, pos + 4) == "false" then
            pos = pos + 5; return false
        elseif text:sub(pos, pos + 3) == "null" then
            pos = pos + 4; return nil
        else
            local num = text:match("^-?%d+%.?%d*[eE]?[-+]?%d*", pos)
            assert(num and #num > 0, "bad JSON at " .. pos)
            pos = pos + #num
            return tonumber(num)
        end
    end
    local result = value()
    ws()
    assert(pos > #text, "trailing data")
    return result
end

---------------------------------------------------------------------------------------------------
-- Virtual file system (io.open stub) for paths under the fake Saved Games folder
---------------------------------------------------------------------------------------------------

local WRITEDIR = "C:\\Users\\Pilot\\Saved Games\\DCS\\"
local realOpen = io.open
local vfs = {}

local function fakeOpen(path, mode)
    if type(path) == "string" and (path:sub(1, #WRITEDIR) == WRITEDIR or path:match("^%a:\\")) then
        mode = mode or "r"
        if mode:find("r") then
            local content = vfs[path]
            if content == nil then return nil, "not found" end
            local consumed = false
            return {
                read = function() if consumed then return nil end consumed = true return content end,
                close = function() end,
            }
        end
        local buffer = {}
        if mode:find("a") and vfs[path] then buffer[1] = vfs[path] end
        return {
            write = function(self, ...)
                for _, v in ipairs({ ... }) do buffer[#buffer + 1] = tostring(v) end
                return self
            end,
            close = function() vfs[path] = table.concat(buffer) end,
        }
    end
    return realOpen(path, mode)
end

---------------------------------------------------------------------------------------------------
-- Test runner
---------------------------------------------------------------------------------------------------

local tests, failures = {}, 0
local function test(name, fn) tests[#tests + 1] = { name = name, fn = fn } end

local function eq(actual, expected, what)
    if actual ~= expected then
        error((what or "value") .. ": expected " .. tostring(expected) .. ", got " .. tostring(actual), 2)
    end
end
local function truthy(v, what) if not v then error((what or "condition") .. " was false", 2) end end
local function falsy(v, what) if v then error((what or "condition") .. " was true: " .. tostring(v), 2) end end

---------------------------------------------------------------------------------------------------
-- DCS stubs
---------------------------------------------------------------------------------------------------

local env -- current stub environment

local function newEnv(opts)
    opts = opts or {}
    vfs = {}
    local e = {
        mp = opts.mp ~= false,
        server = opts.server == true,
        serverId = 1,
        chats = {},       -- server: {line, to}
        localChats = {},  -- client: net.recv_chat
        sent = {},        -- client: UDP datagrams {data, host, port}
        inbox = {},       -- client: datagrams to receive {data, ip, port}
        commands = {},    -- client: os.execute
        clock = 1000,
        registered = nil,
        ucids = {},
    }
    e.DCS = {
        isMultiplayer = function() return e.mp end,
        isServer = function() return e.server end,
        setUserCallbacks = function(cb) e.registered = cb end,
        getRealTime = function() return e.clock end,
    }
    e.net = {
        get_server_id = function() return e.serverId end,
        get_my_player_id = function() return e.server and 1 or 7 end,
        get_server_host = function() return "203.0.113.5:10308" end,
        get_player_info = function(id, attr) if attr == "ucid" then return e.ucids[id] end end,
        json2lua = jsonDecode,
        send_chat_to = function(line, to)
            e.chats[#e.chats + 1] = { line = line, to = to }
            if e.onSend then e.onSend(line, to) end
        end,
        recv_chat = function(text) e.localChats[#e.localChats + 1] = text end,
    }
    e.lfs = { writedir = function() return WRITEDIR end }
    e.udpCreated = 0
    e.dnsNames = {}   -- host name -> IPv4 for socket.dns.toip
    e.socket = {
        dns = { toip = function(host) return e.dnsNames[host] end },
        udp = function()
            e.udpCreated = e.udpCreated + 1
            return {
                setsockname = function() return 1 end,
                settimeout = function(_, t) e.udpTimeout = t end,
                sendto = function(_, data, host, port)
                    if e.udpFail then return nil, "connection refused" end
                    e.sent[#e.sent + 1] = { data = data, host = host, port = port }
                    return #data
                end,
                receivefrom = function()
                    local item = table.remove(e.inbox, 1)
                    if not item then return nil, "timeout" end
                    return item.data, item.ip, item.port
                end,
                close = function() end,
            }
        end,
    }
    e.execute = function(cmd) e.commands[#e.commands + 1] = cmd return 0 end
    rawset(_G, "DCS", e.DCS)
    rawset(_G, "net", e.net)
    rawset(_G, "lfs", e.lfs)
    io.open = fakeOpen
    env = e
    return e
end

local function restoreGlobals()
    io.open = realOpen
    rawset(_G, "DCS", nil)
    rawset(_G, "net", nil)
    rawset(_G, "lfs", nil)
    package.loaded.socket = nil
end

---------------------------------------------------------------------------------------------------
-- Client helpers
---------------------------------------------------------------------------------------------------

local CLIENT_JSON = WRITEDIR .. "Mods\\Services\\OpenFreqDCS\\AutoConnect\\client.json"
local EXE = "C:\\Users\\Pilot\\AppData\\Local\\OpenFreq DCS Client\\OpenFreq.Client.exe"
local TOKEN = "0123456789abcdef0123456789abcdef"
local ADVERT = "OPENFREQ/1 host=voice.example.com port=9937 sid=example-main auth=1 -- OpenFreq voice available"

local M = dofile(CLIENT_MODULE)

local function writeClientJson(fields)
    local cfg = { v = 1, port = 34323, token = TOKEN, exe = EXE, autoConnect = true, autoLaunch = true, profile = "" }
    for k, v in pairs(fields or {}) do cfg[k] = v end
    for k, v in pairs(fields or {}) do if v == "nil" then cfg[k] = nil end end
    vfs[CLIENT_JSON] = M.encodeJson(cfg)
end

local function newClient(opts)
    local e = newEnv(opts)
    writeClientJson(opts and opts.clientJson)
    vfs[EXE] = "MZ"
    local cb = M.create({
        DCS = e.DCS, net = e.net, lfs = e.lfs, socket = e.socket,
        now = function() return e.clock end, execute = e.execute,
    })
    return e, cb
end

local function lastSent(e)
    local item = e.sent[#e.sent]
    return item and jsonDecode(item.data), item
end

local function frames(e, cb, seconds)
    local steps = math.floor(seconds / 0.25 + 0.5)
    for _ = 1, steps do
        e.clock = e.clock + 0.25
        eq(cb.onSimulationFrame(), nil, "onSimulationFrame return")
    end
end

local function ack(e, seq, state, port)
    e.inbox[#e.inbox + 1] = {
        data = M.encodeJson({ type = "openfreq.dcs.ack", v = 1, seq = seq, state = state or "connected" }),
        ip = "127.0.0.1", port = port or 34323,
    }
end

---------------------------------------------------------------------------------------------------
-- Client: parsing and validation
---------------------------------------------------------------------------------------------------

test("client parse: valid advert with human suffix", function()
    local a = M.parseAdvert(ADVERT)
    truthy(a, "parsed")
    eq(a.host, "voice.example.com", "host"); eq(a.port, 9937, "port"); eq(a.sid, "example-main", "sid"); eq(a.auth, 1, "auth")
end)

test("client parse: unknown keys ignored, pw never surfaced", function()
    local a = M.parseAdvert("OPENFREQ/1 host=1.2.3.4 port=9987 future=x pw=secret")
    truthy(a, "parsed")
    eq(a.pw, nil, "pw"); eq(a.future, nil, "future"); eq(a.auth, 0, "auth default")
end)

test("client parse: rejects malformed adverts", function()
    local bad = {
        "openfreq/1 host=a.com port=9987",
        "xx OPENFREQ/1 host=a.com port=9987",
        " OPENFREQ/1 host=a.com port=9987",
        "OPENFREQ/2 host=a.com port=9987",
        "OPENFREQ/0 host=a.com port=9987",
        "OPENFREQ/1",
        "OPENFREQ/1 port=9987",
        "OPENFREQ/1 host=a.com",
        "OPENFREQ/1 host=Voice.Example.com port=9987",
        "OPENFREQ/1 host=localhost port=9987",
        "OPENFREQ/1 host=::1 port=9987",
        "OPENFREQ/1 host=[::1] port=9987",
        "OPENFREQ/1 host=user@a.com port=9987",
        "OPENFREQ/1 host=a.com/x port=9987",
        "OPENFREQ/1 host=a.com:9987 port=9987",
        "OPENFREQ/1 host=a%20b.com port=9987",
        "OPENFREQ/1 host=\"a.com\" port=9987",
        "OPENFREQ/1 host=-a.com port=9987",
        "OPENFREQ/1 host=a-.com port=9987",
        "OPENFREQ/1 host=a..com port=9987",
        "OPENFREQ/1 host=1.2.3.256 port=9987",
        "OPENFREQ/1 host=01.2.3.4 port=9987",
        "OPENFREQ/1 host=1.2.3 port=9987",
        "OPENFREQ/1 host=a.com port=80",
        "OPENFREQ/1 host=a.com port=70000",
        "OPENFREQ/1 host=a.com port=99a",
        "OPENFREQ/1 host=a.com port=-9987",
        "OPENFREQ/1 host=a.com port=9987 auth=2",
        "OPENFREQ/1 host=a.com port=9987 sid=" .. string.rep("x", 33),
        "OPENFREQ/1 host=a.com port=9987 sid=a;b",
        "OPENFREQ/1 host=a.com host=b.com port=9987",
        "OPENFREQ/1 host=a.com port=9987 junk",
        "OPENFREQ/1 host=a.com port=9987\nOPENFREQ/1 host=b.com port=9987",
        "OPENFREQ/1 host=a.com port=9987 -- " .. string.rep("x", 300),
        "OPENFREQ/1 host=" .. string.rep("a", 64) .. ".com port=9987",
    }
    for _, line in ipairs(bad) do
        local a = M.parseAdvert(line)
        if a then error("accepted bad advert: " .. line) end
    end
end)

test("client exe path validation", function()
    truthy(M.isLaunchableExePath(EXE), "installer path with spaces")
    truthy(M.isLaunchableExePath("D:\\Games\\OpenFreq\\openfreq.client.exe"), "portable path, case-insensitive name")
    local bad = {
        nil, "", "OpenFreq.Client.exe", "\\\\server\\share\\OpenFreq.Client.exe",
        "C:\\x\\evil.exe", "C:\\x\\OpenFreq.Client.exe.bat", "C:\\x\"y\\OpenFreq.Client.exe",
        "C:\\x&calc\\OpenFreq.Client.exe", "C:\\%TEMP%\\OpenFreq.Client.exe", "C:\\x|y\\OpenFreq.Client.exe",
        "C:\\x^y\\OpenFreq.Client.exe", "C:\\x<y\\OpenFreq.Client.exe", "C:\\x!y\\OpenFreq.Client.exe",
        "C:\\caf\195\169\\OpenFreq.Client.exe",
    }
    for i = 1, 14 do
        if M.isLaunchableExePath(bad[i]) then error("accepted exe path #" .. i .. ": " .. tostring(bad[i])) end
    end
end)

---------------------------------------------------------------------------------------------------
-- Client: sessions, sender check, forwarding
---------------------------------------------------------------------------------------------------

test("client: valid advert from server is forwarded once with token", function()
    local e, cb = newClient()
    eq(cb.onNetConnect(7), nil, "onNetConnect return")
    eq(cb.onChatMessage(ADVERT, 1), nil, "onChatMessage return")
    eq(#e.sent, 1, "datagrams")
    local msg, raw = lastSent(e)
    eq(raw.host, "127.0.0.1", "dest host"); eq(raw.port, 34323, "dest port")
    eq(msg.type, "openfreq.dcs.autoconnect", "type"); eq(msg.v, 1, "v"); eq(msg.token, TOKEN, "token")
    eq(msg.action, "connect", "action"); eq(msg.seq, 1, "seq")
    eq(msg.target.host, "voice.example.com", "host"); eq(msg.target.port, 9937, "port")
    eq(msg.target.sid, "example-main", "sid"); eq(msg.target.auth, 1, "auth"); eq(msg.target.pw, nil, "pw")
    eq(msg.session.dcsServer, "203.0.113.5:10308", "dcsServer")
    truthy(type(msg.session.id) == "string" and #msg.session.id >= 8, "session id")
    restoreGlobals()
end)

test("client: lines from other players, before joining, as host or in single player are ignored", function()
    local e, cb = newClient()
    cb.onNetConnect(7)
    cb.onChatMessage(ADVERT, 5)
    cb.onChatMessage(ADVERT, 7)
    cb.onChatMessage(ADVERT, nil)
    eq(#e.sent, 0, "datagrams from non-server senders")

    e, cb = newClient()
    cb.onChatMessage(ADVERT, 1) -- no onNetConnect yet
    eq(#e.sent, 0, "no session")

    e, cb = newClient({ server = true })
    cb.onNetConnect(1)
    cb.onChatMessage(ADVERT, 1)
    eq(#e.sent, 0, "hosting player")

    e, cb = newClient({ mp = false })
    cb.onNetConnect(1)
    cb.onChatMessage(ADVERT, 1)
    eq(#e.sent, 0, "single player")
    restoreGlobals()
end)

test("client: invalid advert from server is rejected", function()
    local e, cb = newClient()
    cb.onNetConnect(7)
    cb.onChatMessage("OPENFREQ/1 host=192.168.1.1&calc port=9987", 1)
    cb.onChatMessage("OPENFREQ/1 host=a.com port=22", 1)
    eq(#e.sent, 0, "datagrams")
    restoreGlobals()
end)

test("client: ack stops retries, identical advert is de-duplicated, no launch", function()
    local e, cb = newClient()
    cb.onNetConnect(7)
    cb.onChatMessage(ADVERT, 1)
    ack(e, 1, "connected")
    frames(e, cb, 10)
    eq(#e.sent, 1, "datagrams after ack")
    eq(#e.commands, 0, "launches")
    cb.onChatMessage(ADVERT, 1)
    eq(#e.sent, 1, "duplicate advert forwarded")
    restoreGlobals()
end)

test("client: ack from wrong port or wrong seq is ignored", function()
    local e, cb = newClient({ clientJson = { autoLaunch = false } })
    cb.onNetConnect(7)
    cb.onChatMessage(ADVERT, 1)
    ack(e, 1, "connected", 40000)
    ack(e, 99, "connected")
    frames(e, cb, 2.25)
    truthy(cb ~= nil and #e.sent >= 2, "kept retrying")
    restoreGlobals()
end)

test("client: un-acked request retries every 2 s with the same seq, gives up after 90 s", function()
    local e, cb = newClient({ clientJson = { autoLaunch = false } })
    cb.onNetConnect(7)
    cb.onChatMessage(ADVERT, 1)
    frames(e, cb, 6)
    eq(#e.sent, 4, "initial + 3 retries")
    for _, item in ipairs(e.sent) do eq(jsonDecode(item.data).seq, 1, "seq") end
    frames(e, cb, 100)
    local count = #e.sent
    frames(e, cb, 10)
    eq(#e.sent, count, "no sends after giving up")
    -- After giving up, the same advert (e.g. "-openfreq") may try again.
    cb.onChatMessage(ADVERT, 1)
    eq(#e.sent, count + 1, "retry after give-up")
    restoreGlobals()
end)

test("client: auto-launch runs the recorded exe once, with no advert data", function()
    local e, cb = newClient()
    cb.onNetConnect(7)
    cb.onChatMessage("OPENFREQ/1 host=evil.example.com port=9987", 1)
    frames(e, cb, 2.5)
    eq(#e.commands, 0, "no launch before 3 s")
    frames(e, cb, 1)
    eq(#e.commands, 1, "one launch")
    eq(e.commands[1], 'start "" "' .. EXE .. '" --launched-by-dcs', "command")
    falsy(e.commands[1]:find("evil", 1, true), "advert data in command")
    frames(e, cb, 30)
    eq(#e.commands, 1, "still one launch")
    -- Leaving and joining a new session allows one more launch.
    cb.onNetDisconnect("bye", 0)
    cb.onNetConnect(7)
    cb.onChatMessage(ADVERT, 1)
    frames(e, cb, 4)
    eq(#e.commands, 2, "one launch per session")
    restoreGlobals()
end)

test("client: launch gating (consent flags, exe checks, missing client.json)", function()
    local cases = {
        { clientJson = { autoLaunch = false } },
        { clientJson = { autoConnect = false } },
        { clientJson = { exe = "C:\\Tools\\evil.exe" } },
        { clientJson = { exe = "C:\\Missing\\OpenFreq.Client.exe" } },
        { clientJson = { exe = "nil" } },
    }
    for i, opts in ipairs(cases) do
        local e, cb = newClient(opts)
        cb.onNetConnect(7)
        cb.onChatMessage(ADVERT, 1)
        frames(e, cb, 10)
        eq(#e.commands, 0, "launches in case " .. i)
        truthy(#e.sent > 0, "request still sent in case " .. i)
    end
    local e, cb = newClient()
    vfs[CLIENT_JSON] = nil
    cb.onNetConnect(7)
    cb.onChatMessage(ADVERT, 1)
    frames(e, cb, 10)
    eq(#e.sent, 0, "no datagram without client.json")
    eq(#e.commands, 0, "no launch without client.json")
    restoreGlobals()
end)

test("client: token and port are re-read from client.json (app restart)", function()
    local e, cb = newClient({ clientJson = { autoLaunch = false } })
    cb.onNetConnect(7)
    cb.onChatMessage(ADVERT, 1)
    writeClientJson({ token = "ffffffffffffffffffffffffffffffff", autoLaunch = false })
    frames(e, cb, 2)
    eq(lastSent(e).token, "ffffffffffffffffffffffffffffffff", "new token")
    restoreGlobals()
end)

test("client: pending-consent ack shows one local hint", function()
    local e, cb = newClient()
    cb.onNetConnect(7)
    cb.onChatMessage(ADVERT, 1)
    ack(e, 1, "pending-consent")
    frames(e, cb, 1)
    eq(#e.localChats, 1, "local hints")
    eq(#e.commands, 0, "no launch once acked")
    restoreGlobals()
end)

test("client: leave is sent on onNetDisconnect only", function()
    local e, cb = newClient()
    cb.onNetConnect(7)
    local sessionId
    cb.onChatMessage(ADVERT, 1)
    ack(e, 1)
    frames(e, cb, 1)
    sessionId = jsonDecode(e.sent[1].data).session.id
    -- Slot change, respawn, loading screens, mission rotation: nothing is sent, session survives.
    eq(cb.onPlayerChangeSlot, nil, "onPlayerChangeSlot is not hooked")
    eq(cb.onSimulationStop, nil, "onSimulationStop is not hooked")
    eq(cb.onSimulationStart, nil, "onSimulationStart is not hooked")
    eq(cb.onNetMissionChanged, nil, "onNetMissionChanged is not hooked")
    eq(cb.onMissionLoadBegin, nil, "onMissionLoadBegin is not hooked")
    eq(cb.onGameEvent, nil, "onGameEvent is not hooked")
    cb.onChatMessage(ADVERT, 1) -- server re-sends the same line after rotation
    eq(#e.sent, 1, "no new datagrams across rotation")
    eq(cb.onNetDisconnect("Disconnected", 0), nil, "onNetDisconnect return")
    eq(#e.sent, 3, "leave sent (twice, same seq)")
    local leave = lastSent(e)
    eq(leave.action, "leave", "action"); eq(leave.session.id, sessionId, "session id"); eq(leave.token, TOKEN, "token")
    eq(jsonDecode(e.sent[2].data).seq, leave.seq, "same seq")
    cb.onNetDisconnect("again", 0)
    eq(#e.sent, 3, "no second leave")
    restoreGlobals()
end)

test("client: session starts even if isMultiplayer() is not yet true at onNetConnect", function()
    local e, cb = newClient({ mp = false })
    cb.onNetConnect(7)
    cb.onChatMessage(ADVERT, 1)
    eq(#e.sent, 0, "not forwarded while isMultiplayer() is false")
    e.mp = true
    cb.onChatMessage(ADVERT, 1)
    eq(#e.sent, 1, "forwarded once multiplayer")
    restoreGlobals()
end)

test("client: rejoin creates a new session id", function()
    local e, cb = newClient()
    cb.onNetConnect(7)
    cb.onChatMessage(ADVERT, 1)
    local first = jsonDecode(e.sent[1].data).session.id
    cb.onNetDisconnect("x", 0)
    cb.onNetConnect(7)
    cb.onChatMessage(ADVERT, 1)
    local second = lastSent(e).session.id
    truthy(first ~= second, "different session ids")
    restoreGlobals()
end)

test("client: loader registers callbacks only when the module is installed", function()
    local e = newEnv()
    local f = realOpen(CLIENT_MODULE, "rb")
    vfs[WRITEDIR .. "Mods\\Services\\OpenFreqDCS\\Scripts\\OpenFreqAutoConnectGameGUI.lua"] = "return dofile([[" .. CLIENT_MODULE .. "]])"
    f:close()
    local realDofile = dofile
    dofile = function(path)
        if path:sub(1, #WRITEDIR) == WRITEDIR then return realDofile(CLIENT_MODULE) end
        return realDofile(path)
    end
    realDofile(CLIENT_LOADER)
    truthy(type(e.registered) == "table" and type(e.registered.onChatMessage) == "function", "registered")

    e = newEnv()
    realDofile(CLIENT_LOADER)
    eq(e.registered, nil, "nothing registered without the module")
    dofile = realDofile
    restoreGlobals()
end)

---------------------------------------------------------------------------------------------------
-- Server hook
---------------------------------------------------------------------------------------------------

local SERVER_JSON = WRITEDIR .. "Config\\OpenFreq.json"

local function serverConfig(section)
    local base = { enabled = true, host = "voice.example.com", port = 9937, sid = "example-main", auth = true }
    for k, v in pairs(section or {}) do base[k] = v end
    return M.encodeJson({ v = 1, autoConnect = base })
end

---------------------------------------------------------------------------------------------------
-- Fake DCS mission scripting environment (what net.dostring_in("server", code) runs in)
---------------------------------------------------------------------------------------------------

-- world = { groups = { {name, side, category, units = { {name, type, life, active, point,
--           removed, exists, category, failPoint} }} }, statics = { {name, side, type, life,
--           point, removed, exists} } }
local function newMissionEnv(world)
    local unitByName, staticByName = {}, {}
    local function unitObject(d, g)
        local o = {}
        function o:isExist() return d.exists ~= false and not d.removed end
        function o:getName() return d.name end
        function o:getLife() return d.life or 1 end
        function o:isActive() return d.active ~= false end
        function o:getPoint()
            if d.failPoint then error("getPoint failed") end
            local p = d.point or { x = 1000, y = 50, z = 2000 }
            return { x = p.x, y = p.y, z = p.z }
        end
        function o:getTypeName() return d.type or "Ural-375" end
        function o:getCoalition() return g and g.side or d.side or 0 end
        if g then
            function o:getDesc() return { category = d.category or g.category or 2 } end
            function o:getGroup() return g.object end
        end
        return o
    end
    local function existingUnits(g)
        local list = {}
        for _, d in ipairs(g.units) do
            if d.exists ~= false and not d.removed then list[#list + 1] = unitObject(d, g) end
        end
        return list
    end
    for _, g in ipairs(world.groups) do
        g.object = {
            getName = function() return g.name end,
            getUnits = function() return existingUnits(g) end,
            getUnit = function(_, i) return existingUnits(g)[i] end,
            getCategory = function() return g.category or 2 end,
        }
    end
    local function find(name)
        for _, g in ipairs(world.groups) do
            for _, d in ipairs(g.units) do
                if d.name == name then return d, g end
            end
        end
    end
    local function findStatic(name)
        for _, d in ipairs(world.statics) do
            if d.name == name then return d end
        end
    end
    local mission = {
        coalition = {
            getGroups = function(side)
                local list = {}
                for _, g in ipairs(world.groups) do
                    if g.side == side and #existingUnits(g) > 0 then list[#list + 1] = g.object end
                end
                return list
            end,
            getStaticObjects = function(side)
                local list = {}
                for _, d in ipairs(world.statics) do
                    if d.side == side and not d.removed then list[#list + 1] = unitObject(d, nil) end
                end
                return list
            end,
        },
        Unit = { getByName = function(name)
            local d, g = find(name)
            if d and not d.removed then return unitObject(d, g) end
        end },
        StaticObject = { getByName = function(name)
            local d = findStatic(name)
            if d and not d.removed then return unitObject(d, nil) end
        end },
        coord = { LOtoLL = function(p) return 40 + p.x / 100000, 40 + p.z / 100000, p.y end },
        pcall = pcall, type = type, tostring = tostring, tonumber = tonumber, ipairs = ipairs, pairs = pairs,
        string = string, table = table, math = math, error = error,
    }
    return mission
end

local function runInMission(world, code)
    local mission = newMissionEnv(world)
    local fn, err
    if rawget(_G, "setfenv") and rawget(_G, "loadstring") then
        fn, err = loadstring(code, "mission")
        if fn then setfenv(fn, mission) end
    else
        fn, err = load(code, "mission", "t", mission)
    end
    if not fn then return "[string] " .. tostring(err) end
    local ok, result = pcall(fn)
    if not ok then return tostring(result) end
    return tostring(result)
end

local JAM_SECRET = "0123456789abcdef-test-secret"

local function newServer(opts)
    opts = opts or {}
    local e = newEnv({ mp = opts.mp, server = opts.server ~= false })
    if opts.config ~= false then vfs[SERVER_JSON] = opts.config or serverConfig() end
    e.ucids = { [2] = "ucid-two", [3] = "ucid-three" }
    e.world = opts.world or { groups = {}, statics = {} }
    e.queries = {}
    e.missionName = "Op Test"
    e.DCS.getMissionName = function() return e.missionName end
    e.net.dostring_in = function(state, code)
        e.queries[#e.queries + 1] = code
        if e.queryOverride then return e.queryOverride(state, code) end
        if state ~= "server" then return "" end
        return runInMission(e.world, code)
    end
    package.loaded.socket = e.socket
    e.hook = dofile(SERVER_HOOK)
    local cb = e.registered
    truthy(type(cb) == "table", "server callbacks registered")
    cb.onMissionLoadEnd()
    return e, cb
end

local EXPECTED_LINE = "OPENFREQ/1 host=voice.example.com port=9937 sid=example-main auth=1 -- OpenFreq voice available"

test("server: one advert per player on join, never on slot change or respawn", function()
    local e, cb = newServer()
    eq(cb.onPlayerConnect(2), nil, "return")
    eq(#e.chats, 0, "nothing before onPlayerStart")
    eq(cb.onPlayerStart(2), nil, "return")
    eq(#e.chats, 1, "sent")
    eq(e.chats[1].line, EXPECTED_LINE, "line"); eq(e.chats[1].to, 2, "to")
    cb.onPlayerChangeSlot(2); cb.onPlayerStart(2); cb.onPlayerChangeSlot(2)
    eq(#e.chats, 1, "no re-send")
    cb.onPlayerConnect(3); cb.onPlayerStart(3)
    eq(#e.chats, 2, "second player")
    eq(e.chats[2].to, 3, "to second")
    restoreGlobals()
end)

test("server: advert line parses in the client hook", function()
    local a = M.parseAdvert(EXPECTED_LINE)
    truthy(a and a.host == "voice.example.com" and a.port == 9937 and a.auth == 1, "round trip")
end)

test("server: first slot change is the fallback when onPlayerStart has not fired", function()
    local e, cb = newServer()
    cb.onPlayerConnect(2)
    cb.onPlayerChangeSlot(2)
    eq(#e.chats, 1, "sent on slot change")
    cb.onPlayerStart(2)
    eq(#e.chats, 1, "not again on start")
    restoreGlobals()
end)

test("server: mission rotation with unchanged config re-sends nothing", function()
    local e, cb = newServer()
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    cb.onMissionLoadEnd()
    cb.onPlayerConnect(2); cb.onPlayerStart(2) -- even if DCS re-reports the player
    eq(#e.chats, 1, "sends")
    restoreGlobals()
end)

test("server: changed config on mission load re-sends once to connected players", function()
    local e, cb = newServer()
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    cb.onPlayerConnect(3); cb.onPlayerStart(3)
    vfs[SERVER_JSON] = serverConfig({ port = 9938 })
    cb.onMissionLoadEnd()
    eq(#e.chats, 4, "re-sent to both")
    truthy(e.chats[3].line:find("port=9938", 1, true), "new port")
    cb.onMissionLoadEnd()
    eq(#e.chats, 4, "unchanged config sends nothing more")
    restoreGlobals()
end)

test("server: reconnecting player gets the advert again", function()
    local e, cb = newServer()
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    cb.onPlayerDisconnect(2)
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    eq(#e.chats, 2, "sends")
    restoreGlobals()
end)

test("server: disabled, missing, invalid config, single player and client role send nothing", function()
    local configs = {
        serverConfig({ enabled = false }),
        serverConfig({ host = "localhost" }),
        serverConfig({ host = "a.com\" port=1" }),
        serverConfig({ port = 80 }),
        serverConfig({ sid = "bad sid" }),
        "not json",
        M.encodeJson({ v = 1 }),
    }
    for i, cfg in ipairs(configs) do
        local e, cb = newServer({ config = cfg })
        cb.onPlayerConnect(2); cb.onPlayerStart(2)
        eq(#e.chats, 0, "sends with config #" .. i)
    end
    local e, cb = newServer({ config = false })
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    eq(#e.chats, 0, "sends with no config file")
    e, cb = newServer({ mp = false })
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    eq(#e.chats, 0, "sends in single player")
    e, cb = newServer({ server = false })
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    eq(#e.chats, 0, "sends as a client")
    restoreGlobals()
end)

test("server: password is never advertised", function()
    local e, cb = newServer({ config = serverConfig({ advertise_password = true, password = "hunter2", pw = "x" }) })
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    eq(#e.chats, 1, "sent")
    falsy(e.chats[1].line:find("hunter2", 1, true), "password in line")
    falsy(e.chats[1].line:find("pw=", 1, true), "pw key in line")
    restoreGlobals()
end)

test("server: lowercases host and accepts the lowercase autoconnect section name", function()
    local e, cb = newServer({ config = M.encodeJson({ v = 1, autoconnect = { enabled = true, host = "Voice.Example.COM", port = 9987 } }) })
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    eq(e.chats[1].line, "OPENFREQ/1 host=voice.example.com port=9987 auth=0 -- OpenFreq voice available", "line")
    restoreGlobals()
end)

test("server: player look-alike adverts are dropped, other chat passes (nil)", function()
    local e, cb = newServer()
    eq(cb.onPlayerTrySendChat(2, "OPENFREQ/1 host=evil.com port=9987", -1), "", "exact")
    eq(cb.onPlayerTrySendChat(2, "  openfreq/1 host=evil.com port=9987", -1), "", "case/space variant")
    eq(cb.onPlayerTrySendChat(1, "OPENFREQ/1 host=evil.com port=9987", -1), "", "host player")
    eq(cb.onPlayerTrySendChat(2, "hello OPENFREQ/1", -1), nil, "ordinary chat")
    eq(cb.onPlayerTrySendChat(2, "hello", -1), nil, "ordinary chat 2")
    eq(#e.chats, 0, "no sends")
    restoreGlobals()
end)

test("server: our own send is not dropped even if DCS routes it through onPlayerTrySendChat", function()
    local e, cb = newServer()
    local dropped
    e.onSend = function(line, to) dropped = cb.onPlayerTrySendChat(1, line, to) end
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    eq(dropped, nil, "own advert result")
    restoreGlobals()
end)

test("server: chat command re-sends to that player, rate-limited", function()
    local e, cb = newServer()
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    eq(cb.onPlayerTrySendChat(2, "-openfreq", -1), nil, "command passes through")
    eq(#e.chats, 2, "re-sent on request")
    cb.onPlayerTrySendChat(2, "-OpenFreq ", -1)
    eq(#e.chats, 2, "rate limited")
    restoreGlobals()
end)

test("server: coexists in a callback chain with another hook", function()
    local e, cb = newServer()
    -- A stand-in for another hook (e.g. SRS) registered after ours: DCS calls every hook's
    -- callback until one returns non-nil. Ours must return nil for everything it does not own.
    local otherCalls = 0
    local other = { onPlayerTrySendChat = function() otherCalls = otherCalls + 1 return nil end,
                    onPlayerConnect = function() otherCalls = otherCalls + 1 end }
    local function chain(name, ...)
        for _, hook in ipairs({ cb, other }) do
            local fn = hook[name]
            if fn then
                local r = fn(...)
                if r ~= nil then return r end
            end
        end
        return nil
    end
    eq(chain("onPlayerTrySendChat", 2, "hi", -1), nil, "chain result")
    eq(chain("onPlayerConnect", 2), nil, "connect chain")
    eq(otherCalls, 2, "other hook reached")
    restoreGlobals()
end)

---------------------------------------------------------------------------------------------------
-- Server hook: jamming module (mission jammers, docs/jamming/MISSION_JAMMERS.md)
---------------------------------------------------------------------------------------------------

local function jamConfig(section, noAutoConnect)
    local jam = { enabled = true, secret = JAM_SECRET }
    for k, v in pairs(section or {}) do jam[k] = v end
    for k, v in pairs(section or {}) do if v == "nil" then jam[k] = nil end end
    local cfg = { v = 1, jamming = jam }
    if not noAutoConnect then
        cfg.autoConnect = { enabled = true, host = "voice.example.com", port = 9937, sid = "example-main", auth = true }
    end
    return M.encodeJson(cfg)
end

local function demoWorld()
    return {
        groups = {
            { name = "Blue SAM", side = 2, category = 2, units = {
                { name = "JAM:r934b;mode=auto #001", type = "Ural-375", point = { x = -281000, y = 512, z = 647000 } },
                { name = "Plain truck" },
            } },
            { name = "JAM:@sinitsa", side = 1, category = 2, units = {
                { name = "Ural 1", point = { x = 100, y = 20, z = 200 } },
                { name = "Ural 2" },
            } },
            { name = "Untagged flight", side = 2, category = 0, units = { { name = "Viper 1-1", type = "F-16C_50" } } },
            { name = "Jammer flight", side = 2, category = 0, units = { { name = "JAM:ej;tech=Noise", type = "EA-6B", point = { x = 5000, y = 7000, z = 6000 } } } },
            { name = "Helo", side = 1, category = 1, units = { { name = "JAM:helo", type = "Mi-8MT" } } },
            { name = "Navy", side = 2, category = 3, units = { { name = "JAM:ship", type = "CVN_71" } } },
        },
        statics = {
            { name = "JAM:spark", side = 0, type = "Comms tower M", point = { x = 10, y = 300, z = 20 } },
            { name = "Tent", side = 1, type = "FARP Tent" },
        },
    }
end

-- The hook's own sends only (client tests use the same capture list).
local function jamSent(e)
    local list = {}
    for _, item in ipairs(e.sent) do
        if item.data:sub(1, 5) == "OFJ1 " then list[#list + 1] = item end
    end
    return list
end

local function decodeDatagram(e, item, secret)
    local J = e.hook.modules.jamming
    local mac, json = item.data:match("^OFJ1 (%x+) (.*)$")
    truthy(mac, "datagram shape")
    eq(#mac, 64, "hmac length")
    falsy(mac:find("[A-F]"), "uppercase hex")
    eq(J.hmacSha256Hex(secret or JAM_SECRET, json), mac, "hmac over the json part")
    truthy(#item.data <= 60000, "datagram size")
    return jsonDecode(json), json
end

local function lastJam(e)
    local list = jamSent(e)
    local item = list[#list]
    truthy(item, "a jamming datagram was sent")
    return decodeDatagram(e, item), item
end

local function unitsByName(msg)
    local map, count = {}, 0
    for _, u in ipairs(msg.units) do map[u.name] = u; count = count + 1 end
    return map, count
end

local function serverFrames(e, cb, seconds)
    local steps = math.floor(seconds / 0.25 + 0.5)
    for _ = 1, steps do
        e.clock = e.clock + 0.25
        eq(cb.onSimulationFrame(), nil, "onSimulationFrame return")
    end
end

local function readLog()
    return vfs[WRITEDIR .. "Logs\\OpenFreqServer.log"] or ""
end

test("jamming: SHA-256 and HMAC-SHA256 test vectors (native and pure-arithmetic AND)", function()
    local J = dofile(SERVER_HOOK).modules.jamming
    local vectors = {
        { string.rep("\11", 20), "Hi There", "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7" },
        { "Jefe", "what do ya want for nothing?", "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843" },
        { string.rep("\170", 131), "Test Using Larger Than Block-Size Key - Hash Key First",
          "60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54" },
    }
    local long = string.rep("0123456789abcdef\200\255", 300)
    local reference
    for _, backend in ipairs({ "auto", "table" }) do
        J.selectBand(backend == "table" and "table" or nil)
        eq(J.sha256Hex(""), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", backend .. " sha256('')")
        eq(J.sha256Hex("abc"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", backend .. " sha256('abc')")
        eq(J.sha256Hex("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"),
            "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1", backend .. " sha256 two blocks")
        for i, v in ipairs(vectors) do
            eq(J.hmacSha256Hex(v[1], v[2]), v[3], backend .. " RFC 4231 vector #" .. i)
        end
        local h = J.hmacSha256Hex("k", long)
        reference = reference or h
        eq(h, reference, backend .. " long message agrees across backends")
    end
end)

test("jamming: disabled without a valid section; no socket, no query, no send, secret never logged", function()
    local cases = {
        M.encodeJson({ v = 1, autoConnect = { enabled = true, host = "voice.example.com", port = 9937 } }),
        jamConfig({ enabled = false }),
        jamConfig({ secret = "short" }),
        jamConfig({ secret = "nil" }),
        jamConfig({ secret = "CHANGE-ME-to-a-long-random-secret" }),
        jamConfig({ port = 80 }),
        jamConfig({ port = 70000 }),
        jamConfig({ port = "x" }),
        jamConfig({ host = "localhost" }),
        jamConfig({ host = "a.com;rm" }),
        jamConfig({ prefix = "" }),
        jamConfig({ prefix = string.rep("J", 17) }),
        jamConfig({ prefix = "JAM\n" }),
        jamConfig({ intervalSeconds = "2" }),
        jamConfig({ rescanSeconds = false }),
        "not json",
    }
    for i, cfg in ipairs(cases) do
        local e, cb = newServer({ config = cfg, world = demoWorld() })
        serverFrames(e, cb, 5)
        cb.onSimulationStop()
        eq(e.udpCreated, 0, "sockets with config #" .. i)
        eq(#e.queries, 0, "mission queries with config #" .. i)
        eq(#jamSent(e), 0, "datagrams with config #" .. i)
        falsy(readLog():find(JAM_SECRET, 1, true), "secret in log #" .. i)
        if i > 1 and i < #cases then
            truthy(readLog():find("jamming: disabled: ", 1, true), "disabled reason logged #" .. i)
        end
    end
    -- autoConnect still works when the jamming section is invalid.
    local e, cb = newServer({ config = jamConfig({ secret = "short" }) })
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    eq(#e.chats, 1, "advert still sent")
    restoreGlobals()
end)

test("jamming: nothing is sent when not a multiplayer server", function()
    for _, opts in ipairs({ { mp = false }, { server = false } }) do
        opts.config = jamConfig(); opts.world = demoWorld()
        local e, cb = newServer(opts)
        serverFrames(e, cb, 10)
        cb.onSimulationStop()
        eq(#e.queries, 0, "queries")
        eq(#jamSent(e), 0, "datagrams")
    end
    restoreGlobals()
end)

test("jamming: full scan on mission load finds unit, static and group tags; datagram format", function()
    local e, cb = newServer({ config = jamConfig(), world = demoWorld() })
    local sent = jamSent(e)
    eq(#sent, 1, "one datagram on mission load")
    eq(sent[1].host, "127.0.0.1", "default host"); eq(sent[1].port, 9989, "default port")
    eq(e.udpTimeout, 0, "non-blocking socket")
    local msg = lastJam(e)
    eq(msg.v, 1, "v"); eq(msg.seq, 1, "seq")
    truthy(type(msg.boot) == "string" and msg.boot:match("^%x%x%x%x%x%x%x%x$"), "boot is 8 hex chars")
    truthy(math.abs(msg.ts - os.time()) <= 5, "ts")
    truthy(msg.mission:match("^Op Test|%d+$"), "mission identity: " .. tostring(msg.mission))
    local units, count = unitsByName(msg)
    eq(count, 6, "tagged units")
    falsy(units["Plain truck"] or units["Viper 1-1"] or units["Tent"] or units["Ural 2"], "untagged unit listed")
    local sam = units["JAM:r934b;mode=auto #001"]
    eq(sam.group, "Blue SAM", "group"); eq(sam.kind, "ground", "kind"); eq(sam.side, 2, "side")
    eq(sam.type, "Ural-375", "type"); eq(sam.alive, true, "alive")
    eq(sam.x, -281000, "x"); eq(sam.y, 512, "y"); eq(sam.z, 647000, "z"); eq(sam.alt, 512, "alt")
    truthy(math.abs(sam.lat - 37.19) < 1e-9, "lat " .. tostring(sam.lat))
    truthy(math.abs(sam.lon - 46.47) < 1e-9, "lon " .. tostring(sam.lon))
    local grp = units["Ural 1"]
    truthy(grp, "group tag puts the jammer on the first unit")
    eq(grp.group, "JAM:@sinitsa", "group name carries the tag"); eq(grp.side, 1, "red")
    eq(units["JAM:spark"].kind, "static", "static kind"); eq(units["JAM:spark"].group, "", "static group")
    eq(units["JAM:spark"].alive, true, "static alive"); eq(units["JAM:spark"].side, 0, "neutral")
    eq(units["JAM:ej;tech=Noise"].kind, "air", "airplane"); eq(units["JAM:ej;tech=Noise"].alt, 7000, "air alt")
    eq(units["JAM:helo"].kind, "air", "helicopter"); eq(units["JAM:ship"].kind, "ship", "ship")
    truthy(readLog():find("jamming: sending to 127.0.0.1:9989", 1, true), "jamming log prefix")
    restoreGlobals()
end)

test("jamming: names with '|', newline, backslash and quotes survive the round trip", function()
    local odd = "JAM:a|b\nc\\d\"e\r|"
    local world = { groups = { { name = "G|1\\", side = 2, category = 2, units = { { name = odd } } } }, statics = {} }
    local e, cb = newServer({ config = jamConfig(), world = world })
    local units = unitsByName(lastJam(e))
    truthy(units[odd], "odd name present")
    eq(units[odd].group, "G|1\\", "odd group")
    serverFrames(e, cb, 2) -- lookup by the known (unescaped) name
    units = unitsByName(lastJam(e))
    eq(units[odd] and units[odd].alive, true, "found again by lookup")
    -- Parser alone.
    local J = e.hook.modules.jamming
    local recs = J.parseQueryResult("OFJQ1\nU|JAM:x\\py\\nz\\\\|g||||0|||||||u")
    eq(#recs, 1, "records"); eq(recs[1].name, "JAM:x|y\nz\\", "unescaped"); eq(recs[1].alive, false, "dead")
    falsy(J.parseQueryResult("[string \"x\"]:1: attempt to call nil"), "error text rejected")
    restoreGlobals()
end)

test("jamming: custom prefix, host name resolution and port", function()
    local world = demoWorld()
    world.groups[1].units[2].name = "EW-1 truck"
    local e, cb = newServer({ world = world, config = jamConfig({ prefix = "EW-", host = "jam.example.com", port = 9990 }) })
    eq(#jamSent(e), 0, "unresolvable host: nothing sent")
    truthy(readLog():find("could not resolve jam.example.com", 1, true), "resolve failure logged")
    e.dnsNames["jam.example.com"] = "10.1.2.3"
    cb.onMissionLoadEnd()
    local sent = jamSent(e)
    eq(sent[#sent].host, "10.1.2.3", "resolved ip"); eq(sent[#sent].port, 9990, "port")
    local units, count = unitsByName(lastJam(e))
    eq(count, 1, "only the EW- unit"); truthy(units["EW-1 truck"], "custom prefix")
    restoreGlobals()
end)

test("jamming: throttled to intervalSeconds, seq increments, boot stays", function()
    local e, cb = newServer({ config = jamConfig(), world = demoWorld() })
    serverFrames(e, cb, 1.75)
    eq(#jamSent(e), 1, "no send before intervalSeconds")
    serverFrames(e, cb, 0.25)
    eq(#jamSent(e), 2, "send at intervalSeconds")
    serverFrames(e, cb, 6)
    local sent = jamSent(e)
    eq(#sent, 5, "every 2 s")
    local first = decodeDatagram(e, sent[1])
    for i, item in ipairs(sent) do
        local msg = decodeDatagram(e, item)
        eq(msg.seq, i, "seq " .. i); eq(msg.boot, first.boot, "boot"); eq(msg.mission, first.mission, "mission")
    end
    -- Clamp: intervalSeconds below 1 becomes 1.
    e, cb = newServer({ config = jamConfig({ intervalSeconds = 0.1, rescanSeconds = 1 }), world = demoWorld() })
    serverFrames(e, cb, 3)
    eq(#jamSent(e), 4, "1 s minimum interval")
    restoreGlobals()
end)

test("jamming: dead and removed units are reported alive=false and stay listed", function()
    local world = demoWorld()
    local e, cb = newServer({ config = jamConfig(), world = world })
    world.groups[1].units[1].life = 0          -- killed (still exists, life 0)
    world.statics[1].removed = true            -- destroyed static: getByName returns nil
    world.groups[2].units[1].removed = true    -- group-tag unit gone: Ural 2 must NOT take over
    world.groups[6].units[1].exists = false    -- ship sunk: isExist false
    serverFrames(e, cb, 2)
    local units, count = unitsByName(lastJam(e))
    eq(count, 6, "still six")
    eq(units["JAM:r934b;mode=auto #001"].alive, false, "killed unit")
    eq(units["JAM:spark"].alive, false, "removed static"); eq(units["JAM:spark"].lat, nil, "no position when gone")
    eq(units["Ural 1"].alive, false, "removed group unit"); eq(units["Ural 1"].group, "JAM:@sinitsa", "group kept")
    eq(units["JAM:ship"].alive, false, "sunk ship"); eq(units["JAM:ship"].kind, "ship", "kind kept")
    serverFrames(e, cb, 30) -- across a rescan
    units, count = unitsByName(lastJam(e))
    eq(count, 6, "still six after rescan")
    falsy(units["Ural 2"], "next group unit does not inherit the jammer")
    eq(units["Ural 1"].alive, false, "still dead after rescan")
    restoreGlobals()
end)

test("jamming: late spawn appears after rescanSeconds, late activation is not alive", function()
    local world = demoWorld()
    world.groups[#world.groups + 1] = { name = "Late", side = 1, category = 2,
        units = { { name = "JAM:late", active = false, point = { x = 1, y = 2, z = 3 } } } }
    local e, cb = newServer({ config = jamConfig(), world = world })
    local units = unitsByName(lastJam(e))
    eq(units["JAM:late"].alive, false, "inactive = not alive")
    eq(units["JAM:late"].x, 1, "position still included for an inactive unit")
    world.groups[#world.groups].units[1].active = true
    world.groups[#world.groups + 1] = { name = "Spawned", side = 2, category = 2, units = { { name = "JAM:spawned" } } }
    serverFrames(e, cb, 2)
    units = unitsByName(lastJam(e))
    eq(units["JAM:late"].alive, true, "activated")
    falsy(units["JAM:spawned"], "not before the rescan")
    serverFrames(e, cb, 26)
    falsy(unitsByName(lastJam(e))["JAM:spawned"], "not at 28 s")
    serverFrames(e, cb, 2)
    truthy(unitsByName(lastJam(e))["JAM:spawned"], "found by the 30 s rescan")
    restoreGlobals()
end)

test("jamming: onSimulationStop sends an empty mission once, then nothing", function()
    local e, cb = newServer({ config = jamConfig(), world = demoWorld() })
    eq(cb.onSimulationStop(), nil, "return")
    local msg, item = lastJam(e)
    eq(msg.mission, "", "empty mission"); eq(#msg.units, 0, "no units")
    truthy(item.data:find('"units":[]', 1, true), "units is an empty JSON array")
    eq(msg.seq, 2, "seq continues")
    local count = #jamSent(e)
    serverFrames(e, cb, 10)
    cb.onSimulationStop()
    eq(#jamSent(e), count, "nothing after stop")
    -- Next mission: new identity, same boot, seq continues, units rescanned.
    e.missionName = "Op Next"
    cb.onMissionLoadEnd()
    local next = lastJam(e)
    truthy(next.mission:match("^Op Next|%d+$"), "new mission")
    eq(next.boot, msg.boot, "same boot"); eq(next.seq, 3, "seq")
    local stamp = tonumber(next.mission:match("|(%d+)$"))
    cb.onMissionLoadEnd()
    truthy(tonumber(lastJam(e).mission:match("|(%d+)$")) > stamp, "every load gets a new id")
    restoreGlobals()
end)

test("jamming: failed mission query sends nothing; errors in the mission never escape", function()
    local e, cb = newServer({ config = jamConfig(), world = demoWorld() })
    e.queryOverride = function() return "[string \"x\"]:1: boom" end
    serverFrames(e, cb, 6)
    eq(#jamSent(e), 1, "no sends while the query fails")
    e.queryOverride = function() error("dostring_in exploded") end
    serverFrames(e, cb, 6)
    eq(#jamSent(e), 1, "no sends while dostring_in throws")
    e.queryOverride = nil
    serverFrames(e, cb, 2)
    eq(#jamSent(e), 2, "recovers")
    -- A unit whose getPoint throws: listed, not alive (a jammer needs a position).
    local world = demoWorld()
    world.groups[1].units[1].failPoint = true
    e, cb = newServer({ config = jamConfig(), world = world })
    local units = unitsByName(lastJam(e))
    eq(units["JAM:r934b;mode=auto #001"].alive, false, "no position -> not alive")
    eq(units["JAM:r934b;mode=auto #001"].lat, nil, "no lat")
    -- Socket send failure is logged (rate-limited) and does not throw.
    e.udpFail = true
    serverFrames(e, cb, 6)
    eq(#jamSent(e), 1, "failed sends not captured")
    local _, failures = readLog():gsub("UDP send to 127%.0%.0%.1:9989 failed", "")
    eq(failures, 1, "send failure logged once")
    e.udpFail = false
    serverFrames(e, cb, 2)
    eq(lastJam(e).seq, 5, "seq kept counting through failures")
    restoreGlobals()
end)

test("jamming: at most 200 units and 60000 bytes per datagram", function()
    local world = { groups = {}, statics = {} }
    for i = 1, 230 do
        world.groups[i] = { name = "G" .. i, side = 2, category = 2,
            units = { { name = "JAM:" .. string.rep("n", 300) .. i } } }
    end
    local e, cb = newServer({ config = jamConfig(), world = world })
    local msg, item = lastJam(e)
    truthy(#item.data <= 60000, "size cap")
    truthy(#msg.units < 200 and #msg.units > 100, "trimmed from the end: " .. #msg.units)
    truthy(readLog():find("datagram over 60000 bytes", 1, true), "trim logged")
    truthy(readLog():find("more than 200 tagged units", 1, true), "cap logged")
    world = { groups = {}, statics = {} }
    for i = 1, 230 do
        world.groups[i] = { name = "G" .. i, side = 2, category = 2, units = { { name = "JAM:" .. i } } }
    end
    e, cb = newServer({ config = jamConfig(), world = world })
    eq(#lastJam(e).units, 200, "200 units max")
    restoreGlobals()
end)

test("jamming: callbacks return nil and autoConnect is unaffected", function()
    local e, cb = newServer({ config = jamConfig(), world = demoWorld() })
    eq(cb.onSimulationFrame(), nil, "frame"); eq(cb.onSimulationStop(), nil, "stop")
    eq(cb.onMissionLoadEnd(), nil, "load")
    cb.onPlayerConnect(2); cb.onPlayerStart(2)
    eq(#e.chats, 1, "advert")
    eq(e.chats[1].line, EXPECTED_LINE, "advert line")
    truthy(readLog():find("autoconnect: advert: ", 1, true), "autoconnect log lines unchanged")
    restoreGlobals()
end)

---------------------------------------------------------------------------------------------------

for _, t in ipairs(tests) do
    local ok, err = pcall(t.fn)
    restoreGlobals()
    if ok then
        print("PASS " .. t.name)
    else
        failures = failures + 1
        print("FAIL " .. t.name .. "\n     " .. tostring(err))
    end
end
print(string.format("%d tests, %d failed", #tests, failures))
os.exit(failures == 0 and 0 or 1)
