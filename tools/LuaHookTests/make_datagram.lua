-- Prints one mission-jammer datagram built by the real DCS server hook, for the C# interop test
-- (OpenFreq.Server.Tests/Jamming/MissionJammerLuaInteropTests.cs).
--
--   lua tools/LuaHookTests/make_datagram.lua <repoRoot> <secret>
--
-- Loads DCS/Server/OpenFreq-ServerGameGUI.lua with no DCS globals (the hook then stays inactive),
-- parses a canned net.dostring_in query result through the hook's own parser, and writes the
-- datagram from jamming.buildDatagram to stdout without a trailing newline.

local repoRoot = (arg and arg[1] or "."):gsub("[\\/]$", "")
local secret = arg and arg[2] or error("secret required")

local hook = dofile(repoRoot .. "/DCS/Server/OpenFreq-ServerGameGUI.lua")
local jamming = hook.modules.jamming

local query = table.concat({
    "OFJQ1",
    "U|JAM:r934b;spot=251 #001|Sinitsa|ground|1|Ural-375|1|42.25|43.5|512.5|-281000.5|512.5|647000.25|u",
    "U|Ural \\p dead|JAM:leer2|ground|1|Ural-375|0|||||||g",
    "U|JAM:il22pp|Porub|air|1|Il-22|1|43|44|7000|-200000|7000|600000|u",
}, "\n")

local units = assert(jamming.parseQueryResult(query))
jamming.boot = "0badc0de"
jamming.seq = 41
local datagram = jamming.buildDatagram(secret, "Interop|1731263000", units)
io.write(datagram)
