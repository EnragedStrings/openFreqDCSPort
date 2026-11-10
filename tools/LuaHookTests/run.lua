-- Lua test harness for the OpenFreq DCS GameGUI hooks (auto-connect).
--
--   lua tools/LuaHookTests/run.lua [repoRoot]
--
-- Runs with plain Lua 5.1-5.4 and no DCS: DCS, net, lfs, LuaSocket, io.open (a virtual file
-- system), os.execute and the clock are all stubbed. Exit code 0 = all tests passed.
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
    e.socket = {
        udp = function()
            return {
                setsockname = function() return 1 end,
                settimeout = function() end,
                sendto = function(_, data, host, port) e.sent[#e.sent + 1] = { data = data, host = host, port = port } end,
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

local function newServer(opts)
    opts = opts or {}
    local e = newEnv({ mp = opts.mp, server = opts.server ~= false })
    if opts.config ~= false then vfs[SERVER_JSON] = opts.config or serverConfig() end
    e.ucids = { [2] = "ucid-two", [3] = "ucid-three" }
    dofile(SERVER_HOOK)
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
