-- OpenFreq-ClientGameGUI.lua
--
-- Installed and repaired by the OpenFreq client app into Saved Games\<DCS>\Scripts\Hooks.
-- A thin, stable loader: the logic lives in
--   Saved Games\<DCS>\Mods\Services\OpenFreqDCS\Scripts\OpenFreqAutoConnectGameGUI.lua
-- so app updates only replace that file. Does nothing in single player, on a server, or when
-- that file is missing. All callbacks return nil (coexists with DCS-SRS and other hooks).
pcall(function()
    local lfsLib = rawget(_G, "lfs")
    if not lfsLib then
        local ok, mod = pcall(require, "lfs")
        if ok then lfsLib = mod end
    end
    if not lfsLib or type(lfsLib.writedir) ~= "function" then
        return
    end
    local writeDir = lfsLib.writedir()
    local path = writeDir .. [[Mods\Services\OpenFreqDCS\Scripts\OpenFreqAutoConnectGameGUI.lua]]
    local probe = io.open(path, "rb")
    if not probe then
        return
    end
    probe:close()

    local ok, err = pcall(function()
        local module = dofile(path)
        local callbacks = module.create({ DCS = DCS, net = net, lfs = lfsLib })
        DCS.setUserCallbacks(callbacks)
    end)
    if not ok then
        local file = io.open(writeDir .. [[Logs\OpenFreqDCS.log]], "ab")
        if file then
            file:write(os.date("!%Y-%m-%dT%H:%M:%SZ "), "autoconnect hook load error: ", tostring(err), "\r\n")
            file:close()
        end
    end
end)
