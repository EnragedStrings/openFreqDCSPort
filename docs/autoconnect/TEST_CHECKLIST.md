# DCS auto-connect: test coverage and manual checklist

Scenarios T1-T14 are the ones in [AUTOCONNECT_DESIGN.md](AUTOCONNECT_DESIGN.md) section 13.

Automated tests:
- **Lua** (`tools/LuaHookTests/run.lua`, stubbed DCS/net/lfs/LuaSocket/io/os.execute; run by
  `LuaHookHarnessTests` when `lua` is on PATH, skipped otherwise):
  `lua tools/LuaHookTests/run.lua .`
- **C#** (`OpenFreq.Client.Tests`): `AutoConnectProtocolTests`, `AutoConnectTargetValidatorTests`,
  `DcsAutoConnectEndpointTests`, `DcsAutoConnectControllerTests`, `SingleInstanceGuardTests`,
  `DcsAutoConnectInstallTests`.

The Lua tests prove the hook logic against **assumed** DCS behaviour. The items marked **[DCS]**
below need a real DCS run, because they depend on how DCS itself behaves (design section 3, the [U]
facts).

## Coverage by scenario

| # | Scenario | Automated | Manual in DCS |
|---|---|---|---|
| T1 | App running, not connected, join server -> connects | Lua: advert forwarded with token; C#: `NotConnected_ConnectsWithSessionSnapshot_SavedSettingsUntouched` | [DCS] 1 |
| T2 | App not running, launch allowed -> launched and connected | Lua: one launch of the recorded exe after 3 s, none when off / bad path / no client.json | [DCS] 2 |
| T3 | App running -> no duplicate instance | C#: `SingleInstanceGuardTests` | 3 |
| T4 | Already on the same server -> no-op | C#: `AlreadyOnSameServer_IsNoOp`, `SameResolvedIp_IsNoOp`, `SameAddress` | - |
| T5 | Launch does not stall or flash; portable and installer paths | Lua: command shape and path validation only | [DCS] 2, 4 |
| T6 | Different server: prompt / policy / manual protected | C#: `DifferentServer_*` | 5 |
| T7 | Drop in same DCS server -> back-off; stops on user disconnect, auth failure | C#: `ConnectionLost_RetriesWithExponentialBackoff`, `Backoff_HasTwentyPercentJitter`, `UserDisconnect_StopsReconnects`, `VersionMismatch_StopsRetries`, `WrongPassword_*` | 6 |
| T8 | Leave -> disconnect after grace; setting off; quick rejoin keeps it | Lua: leave only on `onNetDisconnect`; C#: `Leave_*`, `RejoinWithinGrace_KeepsConnection`, `DcsExited_ActsAsLeave` | [DCS] 7 |
| T9 | Loading screen, slot change, respawn, mission rotation -> nothing | Lua: those callbacks are not hooked, no datagram, no re-advert; C#: `SameSessionRepeatedAdvert_IsNoOp` | **[DCS] 8 (core assumption)** |
| T10 | Spoofing: player chat, unanchored text, bad host/port, private IP from public server, wrong token, non-loopback sender | Lua: sender and parser tests; C#: protocol, validator, endpoint tests | [DCS] 9 |
| T11 | Credentials never overwritten; Remember; wrong password prompts and stops | C#: `PasswordPrompt_*`, `PasswordOrder_*`, `WrongPassword_*`, `PasswordInRequest_IsIgnored`; Lua: `pw` never parsed or advertised | - |
| T12 | Server hook: once per player, multiple players, reload on mission load, re-send on change, look-alikes dropped, nil otherwise | Lua: all `server:` tests | [DCS] 10 |
| T13 | Coexistence with SRS / DCSServerBot hooks, Export.lua, dedicated server | Lua: callback chain test, nil returns | **[DCS] 11** |
| T14 | DCSServerBot config generation; manual config without the bot | - (no extension shipped; contract in DCSSERVERBOT.md) | 12 |

## Manual checklist

Use a dedicated server (or a second PC hosting) with `OpenFreq-ServerGameGUI.lua` and a valid
`Config\OpenFreq.json`, and a player PC with OpenFreq in DCS mode. Watch
`Saved Games\...\Logs\OpenFreqDCS.log` (player), `Logs\OpenFreqServer.log` (server) and the OpenFreq
client log.

1. **Join with the app running.** Exactly one `OPENFREQ/1` line appears in chat. With auto-connect
   off and never asked: the consent dialog is in the OpenFreq window and DCS shows one local hint
   line. Answer **Always**: OpenFreq connects; the saved address/password in Settings are unchanged.
2. **Join with the app closed, auto-launch on.** After about 3 s OpenFreq starts once (minimized,
   without stealing focus) and connects. Check: does DCS stall? Does a console window flash? Does DCS
   lose full-screen focus? Repeat with the installed copy and with a portable copy in a folder with
   spaces. (`os.execute` runs `cmd /c start`; a brief console flash is possible and is the main open
   question for this release.)
3. **Start OpenFreq twice** from the Start menu: the second start brings the first window forward and
   exits. Start it from DCS while it is running: nothing visible happens.
4. **Move a portable copy** to another folder, start it once, then repeat step 2: the new copy starts.
5. **Different server:** connect manually to server A, join a DCS server advertising B: a switch
   prompt appears; **Stay** is not asked again in that DCS session.
6. **Drop:** stop the OpenFreq server while in the DCS server. After the client's own 60 s reconnect
   window, retries continue at 5/10/20/40/60 s (client log); restart the server and it reconnects.
   Press Disconnect: no more retries.
7. **Leave:** leave the DCS server to the menu: OpenFreq disconnects about 10 s later (setting on) or
   stays (setting off). Leave and rejoin within 10 s: the connection is kept. Kill DCS from Task
   Manager: OpenFreq disconnects within about 15 s.
8. **Core assumption (T9):** change slot, respawn, die, sit in a loading screen, let the server
   rotate the mission. The connection stays, no second chat line, and `OpenFreqDCS.log` shows no
   "left DCS server" line. Confirms that `onNetConnect`/`onNetDisconnect` do not fire for these.
9. **Spoofing:** from a second player type `OPENFREQ/1 host=evil.example.com port=9987`: the line
   never appears in chat (dropped by the server hook). Also check that `from` for a server-sent line
   is the server id on the client (`OpenFreqDCS.log` shows "advert accepted").
10. **Server hook:** two players join: one line each. Change the port in `OpenFreq.json` and load the
    next mission: each connected player gets one new line. Type `-openfreq`: the line is re-sent.
11. **Coexistence:** run with DCS-SRS (including SRS auto-connect) and DCSServerBot installed: SRS
    still connects, bot chat commands still work, OpenFreq radios (Export.lua, ports 34321/34322)
    still work, on a dedicated server too.
12. **Manual server config** following SERVER_ADMIN.md on two DCS instances on one machine, each with
    its own `OpenFreq.json`.

Also verify once: `net.get_server_host()` returns the joined server's address on the client (shown in
the consent dialog and used for the private-address rule), and `net.recv_chat` shows the local hint.
