# OpenFreq DCS multiplayer auto-connect and auto-launch: research and design

Status: design only. No code has been written. Date: 2026-11-09.

Evidence labels used throughout:
- **[V]** verified: read in source or in ED's shipped docs or scripts. A citation is given.
- **[I]** inferred from code behaviour, not documented.
- **[U]** unverified. Needs a manual DCS test before anything relies on it.

---

## 1. Summary

1. **Server GameGUI hook.** A small hook on the DCS server, `OpenFreq-ServerGameGUI.lua`, reads an admin- or DCSServerBot-written `Config\OpenFreq.json`. When a player joins, it sends that player one versioned, machine-parsable chat line with `net.send_chat_to`.
2. **Client GameGUI hook.** A hook on each player's machine, `OpenFreq-ClientGameGUI.lua`, is installed and repaired by the OpenFreq client the same way the Export.lua fragment is today. It:
   - accepts the advert only while it is in a multiplayer session as a client, and only if the line came from the server's player id;
   - tracks join and leave with `onNetConnect` / `onNetDisconnect`;
   - forwards a JSON request over loopback UDP to the OpenFreq app;
   - launches the app if it is not running and the user has opted in. It can only launch the exe path the app itself recorded.
3. **Client app.** A new `DcsAutoConnectService` in the OpenFreq client listens on `127.0.0.1:34323`. It applies the user's policy (consent, server switching, credentials) and drives the existing `IOpenFreqService.Initialize` / `ConnectAsync` / `DisconnectAsync` calls with a per-session settings snapshot. The saved address and password are never overwritten.
4. **Single instance.** A named mutex plus a hand-off makes duplicate instances impossible.
5. **No server exe changes.** The OpenFreq server exe is not touched. DCSServerBot only needs to write one JSON file and copy one hook file.

---

## 2. Reference implementation: how DCS-SRS 2.4.1.0 does it

**License: SRS is GPL-3.0, not MIT** [V] (`LICENSE`:1-2 at tag 2.4.1.0). Do not copy its code. This design re-implements the idea from scratch.

### 2.1 Pieces and where they run

| Piece | DCS environment | File (tag 2.4.1.0) |
|---|---|---|
| Advert sender | **Server** GameGUI hook. The admin copies it to `Saved Games\DCS\Scripts\Hooks`. | `Scripts/DCS-SRS-AutoConnectGameGUI.lua` |
| Advert receiver | **Client** GameGUI hook. The loader `Scripts/Hooks/DCS-SRS-hook.lua` `dofile`s `Mods\Services\DCS-SRS\Scripts\DCS-SRSGameGUI.lua`. | `DCS-SRSGameGUI.lua` |
| App endpoint | SRS client, a C# app | `DCS-SR-Client/Network/DCS/DCSAutoConnectHandler.cs` |
| Launcher | Native Lua module `srs.dll`, loaded into the client GameGUI | `SRS-Lua-Wrapper/SRSExtension.cpp` |

The SRS server exe has **no** auto-connect code [V]: searching the server code for "autoconnect" finds nothing. The SRS installer tells admins to install the server hook by hand (`Installer/Properties/Resources.Designer.cs`:327).

### 2.2 Server side

- Configuration (lines 12-15):
  - `SERVER_SRS_HOST_AUTO` (default false)
  - `SERVER_SRS_PORT = "5002"`
  - `SERVER_SRS_HOST = "127.0.0.1"`
  - `SERVER_SEND_AUTO_CONNECT`
- Prefixes (lines 44-45): `"SRS Running @ "` and `"SRS Running on "`.
- Sending (lines 72-79): `net.send_chat_to(MESSAGE_PREFIX .. host .. ":" .. port, id)`. If `HOST_AUTO` is set, it sends only the port.
- Callbacks used:
  - `onPlayerConnect` (83-90) and `onPlayerChangeSlot` (92-99). Both are guarded by `DCS.isServer()` and skip id 1. **The advert is re-sent on every slot change.**
  - `onChatMessage` (105-126) answers the `-freq` command.
  - `onSimulationFrame` (133-150) runs the optional "nudge" message.
  - `onMissionLoadBegin` is defined but empty.
  - Not used: `onGameEvent`, `onSimulationStart` / `onSimulationStop`.
- The server hook never looks up its public IP. "HOST_AUTO" relies on the client calling `net.get_server_host()`.

### 2.3 Client side

`DCS-SRSGameGUI.lua` `onChatMessage(msg, from)` (lines 333-367):

```lua
if SRS.CLIENT_ACCEPT_AUTO_CONNECT and from == 1 then
    local port = SRS.getPortFromMessage(msg)       -- "SRS Running on (%d+)"
    if port ~= nil then host = net.get_server_host():gsub(":.*","") .. ':' .. port
    else host = SRS.getHostFromMessage(msg) end    -- "SRS Running @ ([%w%.%-_:]+)"
    if srs and OptionsData.getPlugin("DCS-SRS","srsAutoLaunchEnabled") then srs.start_srs(host) end
    SRS.sendConnect(host)                          -- UDP "host:port\n" -> 127.0.0.1:5069
```

- Patterns are at lines 116-118. `sendConnect` is at 141-143.
- The payload is **plain text** `host:port\n`. There is no version and no token.

### 2.4 Auto-launch

`srs.dll` exports `start_srs` (`dllmain.cpp`:27-30). `SRSExtension.cpp` `Launch`, lines 8-29, does the following:
1. Takes a process snapshot. If `SR-ClientRadio.exe` is running, it does nothing (68-83).
2. Otherwise it reads `HKCU\SOFTWARE\DCS-SR-Standalone\SRPathStandalone` (40-43). The installer and the client's "Set SRS Path" button write this value.
3. It calls `ShellExecute(<path>\Client\SR-ClientRadio.exe, "-host=<host>")`.

The in-game option `srsAutoLaunchEnabled` defaults to **true** (`Scripts/DCS-SRS/Options/optionsDb.lua`:4).

### 2.5 App side

- `DCSAutoConnectHandler` binds **`IPAddress.Any`**:5069 (lines 38-40). The port default is in `GlobalSettingsStore.cs`:303.
- It does no validation (93-118).
- `MainWindowViewModel.cs`:337-391 decides what to do:

| Current state | What SRS does |
|---|---|
| Not connected | Connects silently, with no prompt |
| Connected to the same address | Ignores the advert |
| Connected to a different address | Shows a Yes/No prompt if `AutoConnectMismatchPrompt` (default true). Otherwise it switches silently. |

- The `AutoConnect` and `AutoConnectPrompt` settings appear in the UI but no code reads them [V].

### 2.6 SRS weaknesses this design avoids

1. **Listener not loopback-only.** The listener binds to all interfaces, so any LAN or internet host can make the client connect anywhere with no prompt. OpenFreq binds 127.0.0.1 and requires a token.
2. **No consent.** Connect and launch both happen by default. OpenFreq keeps both off until the user opts in.
3. **Weak sender check.** The only check is `from == 1`. The patterns are unanchored, so any server-originated text containing the prefix matches, including relayed text. OpenFreq anchors the pattern and versions the line, and the server hook drops player chat that starts with the prefix. Policy still treats the advert as untrusted.
4. **Unvalidated host.** The host goes to `ShellExecute` as `-host=`. OpenFreq never puts advert data on a command line.
5. **Repeated adverts.** The advert is re-sent on every slot change, which spams chat. OpenFreq de-duplicates per player.
6. **Native code bugs.** `SRSExtension.cpp`:47 writes the terminator at a byte count used as a WCHAR index, which overflows. The server nudge builds Lua by string concatenation for `net.dostring_in` (line 218), which allows injection. OpenFreq uses no native DLL and no `dostring_in`.

---

## 3. DCS Lua environment facts

The source is the local install, `D:\DCS World OpenBeta` 2.9.31.29434. **`API\DCS_ControlAPI.html` no longer exists.** The current docs are `API\Sim_ControlAPI.md` and `Sim_ControlAPI.html`, which name the table `Sim.*`. Every shipped hook still uses `DCS.*`, for example `Scripts\Hooks\common.lua`:8,28. **Use `DCS.*`.**

| Fact | Status | Source |
|---|---|---|
| `Saved Games\<DCS>\Scripts\Hooks\*.lua` are sorted by name, each loaded into its own isolated environment in the GUI Lua state. All hooks' callbacks are called in load order. | V | Sim_ControlAPI.md:22-29 |
| Only `onPlayerTryConnect`, `onPlayerTrySendChat` and `onPlayerTryChangeSlot` return values. Returning non-nil breaks the chain, so **our hooks must return nil from everything else** to coexist with SRS. | V | :31-38 |
| Standard Lua 5.1 libraries, **including `io.*` and `os.*`**, are available in hooks. `MissionScripting.lua` sanitises only the *mission* environment. | V | :58-67; `Scripts\MissionScripting.lua` |
| `require("socket")` works in hooks. Some hooks add `.\LuaSocket\?.lua` / `?.dll` to the package path first. | V | SRS autoconnect hook:47-53; JAFDTCStatusMsgHook.lua:29 |
| Callbacks: `onMissionLoadBegin/End`, `onSimulationStart/Stop/Frame/Pause/Resume`, `onGameEvent`, `onNetConnect(localPlayerID)`, `onNetMissionChanged(newMissionName)`, `onNetMissionEnd()`, `onNetDisconnect(reason_msg, err_code)`, `onPlayerConnect/Disconnect/Start/Stop/ChangeSlot(id)`, `onChatMessage(message, from)` | V | :576-685 |
| `onPlayerDisconnect`, `onPlayerStart` and `onPlayerStop` are "never called for local playerID". | V | :641-656 |
| `net.ERR_*` disconnect codes include KICKED, TIMEOUT, BANNED and INVALID_PASSWORD. | V | :627-638 |
| `net.send_chat_to(message, playerID to)`: "direct chat message to a player". The docs list **no `from` parameter**. | V | :331-334 |
| `net.recv_chat(message[, from=0])` shows chat only locally. | V | :336-340 |
| `net.get_my_player_id()` ("always 1 for the server") and `net.get_server_id()` ("Currently, always 1"). | V | :365-371 |
| `net.get_player_info(id, attr)`: `ipaddr` and `ucid` are server-only. | V | Sim_ControlAPI.md |
| `DCS.isMultiplayer()`: true in multiplayer. `DCS.isServer()`: true **as a server or in single-player**. | V | :111-117 |
| `net.dostring_in` is "OBSOLETE and UNSAFE" and needs `autoexec.cfg` allow-listing. It runs only in the *local* states, so it is **not** a server-to-client channel. | V | :303-320 |
| `net.lua2json` / `net.json2lua` exist. | V | Sim_ControlAPI.md |
| `net.get_server_host()` exists but is undocumented. On a client it returns the joined server's address, possibly as `host:port`. | V (usage), U (exact format) | `Scripts\UI\MultiplayerSelectRoleMap\MultiplayerSelectRole.lua`:294; SRS client :339 |
| `net.get_server_settings()` exists but is undocumented. Its fields include `geoCountry`. Whether it includes a server *name* is unknown. | V (usage), U (fields) | MultiplayerSelectRole.lua:293-311 |
| No documented "server name" or "server identity" API exists. `DCS.getServerName` does not exist. | V (absence in docs) | — |
| A chat line from the server (`net.send_chat_to` called on the server) reaches the client's `onChatMessage` with `from == 1`. | I | SRS relies on it (§2.3) |
| `net.send_chat_to` text **is visible** in the recipient's chat window, and a client `onChatMessage` cannot suppress it (no return value is documented). | U, assume visible | — |
| `onNetConnect` / `onNetDisconnect` fire on a *client* when it joins or leaves a server. They do **not** fire on mission rotation, slot change or respawn. | U, the core assumption; manual test T13 | — |
| `onSimulationStop` / `onSimulationStart` fire on a client during mission rotation, so they **must not** be used to mean "left the server". | U | — |
| `onSimulationFrame` runs on a client while it sits in slot selection, but not during the loading screen. | U | — |
| `os.execute('start "" "<exe>"')` from the GUI state does not stall DCS. It may flash a console window. | U, test T5 | — |
| The pre-2.9 docs say `net.send_chat_to` has an optional `from` argument. | U | The two research passes disagreed. Irrelevant to the design, because only a server-side script can set it. |

**Hard limits.** No API exists for a server to run code or start a program on a remote player's PC. Everything on the client happens because the *client's own* hook chooses to act. Chat is the only documented server-to-client data channel available to hooks. The advert line will be visible to the player, so it is designed to read well (§6.1).

---

## 4. OpenFreq code that can be reused

| Concern | Today | Where |
|---|---|---|
| Settings model | `OpenFreqSettings` holds `OpenFreqServerAddress`, `OpenFreqPassword`, history and opt-in booleans that default to false, e.g. `AutoUpdateEnabled` at :68. | `OpenFreq.Client/Models/Settings.cs`:7-115 |
| Settings view model | Properties are `[ObservableProperty]`. `GetSettings()` returns a **snapshot** object. `PersistImmediately` saves outside the clean-shutdown path. | `ViewModels/SettingsViewModel.cs`:34-37, 882-916, 961-976 |
| Persistence | JSON at `%APPDATA%\OpenFreq{profile}\OpenFreq.Client.json`. Settings are normally saved only on clean exit or `PersistImmediately`. | `Services/ConfigurationService.cs`:16-53; `AppDataPaths.cs`:15-27; `MainWindowViewModel.cs`:1228-1274 |
| Connect | `IOpenFreqService.Initialize(OpenFreqSettings, rec, play)`, then `ConnectAsync(timeout?)`, then `DisconnectAsync()`. `Initialize` builds a new `OpenFreqRtcClient` from **the settings object passed in**, so an override snapshot can be passed without touching saved settings. | `Interfaces/IOpenFreqService.cs`:125-127; `Services/OpenFreqService.cs`:421-444, 647-657, 748-770 |
| UI connect | `ConnectAsync` relay command and `ConnectWithTimeoutAsync` (Initialize, then Connect), plus `DisconnectAsync`. | `MainWindowViewModel.cs`:610-736 |
| State events | `ConnectionStateChanged` carries `ConnectionState` (Disconnected, Connecting, Connected, Authenticated) and a `DisconnectReason` (UserRequested, ConnectionLost, ConnectionFailed). | `OpenFreqService.cs`:1740-1761; `OpenFreq.Common/OpenFreqRtcClient.cs`:925-960 |
| Inner reconnect | When a live connection drops unexpectedly, `ReconnectAsync` retries connect, auth and rejoin for **60 s**. Backoff is linear, capped at 5 s, with ±50% jitter. When the budget runs out it reports Disconnected with ConnectionLost. | `OpenFreqRtcClient.cs`:61-63, 246-317, 641-660 |
| Address parsing | `Util.ResolveAddress("host[:port]", 9987)` splits on ':' (so **IPv6 is unsupported**) and resolves DNS. The default WebSocket port is **9987**. The audio UDP port comes from the server in the auth reply (`SignalingMessages.cs`:115), so **the advert only needs the WebSocket port**. | `OpenFreq.Common/Util.cs`:7-48; `OpenFreqRtcClient.cs`:19,160,186 |
| Address history | The address is added to history on Authenticated in DCS or GCI mode. | `MainWindowViewModel.cs`:814-821 |
| BMS precedent (do **not** copy) | When the game asks to connect, BMS code **overwrites** `Settings.OpenFreqPassword` and `OpenFreqServerAddress`. That breaks the "never silently overwrite saved credentials" rule. DCS auto-connect must use a snapshot instead. | `MainWindowViewModel.cs`:520-533 |
| Instance detection | `SelfUpdateLauncher.FindOtherInstances(exePath, selfPid)` matches processes by name and full path. It is used by the update path and by the port-in-use diagnostic. **There is no single-instance mutex.** A second launch currently opens a second window, which then fails to bind UDP 34321. | `OpenFreq.Common/Updates/SelfUpdateLauncher.cs`:41-66; `Program.cs`:196; `DcsExportService.cs`:384-398 |
| Profiles | `--profile <name>` isolates settings so two instances can run on purpose. The mutex name must include the profile. | `Program.cs`:35-46 |
| Existing loopback UDP | Export to app on 127.0.0.1:34321. LOS/coordinate requests from app to Export on 127.0.0.1:34322, using a non-blocking LuaSocket with `setsockname("127.0.0.1", …)` and `settimeout(0)`. **Use 34323 for auto-connect.** | `Interfaces/IDcsExportService.cs`:12-13; `DcsExportService.cs`:357; `DCS/OpenFreqDCS/Scripts/OpenFreqDCS.lua`:42, 1500-1520 |
| DCS script install and repair | `DcsExportInstaller.EnsureInstalled()` runs **on every app start** (`App.axaml.cs`:41) and from the installer (`--dcs-export install`, `Program.cs`:137-176). It does the following for every `Saved Games\DCS*` folder (found with the real FOLDERID_SavedGames): extracts the embedded `DCS/OpenFreqDCS/**` resources to `Mods\Services\OpenFreqDCS` (byte-compare, never overwriting the user's `OpenFreqDCSConfig.lua`), patches a marked block into `Scripts\Export.lua`, and creates a Start-menu shortcut to `Environment.ProcessPath`. `Uninstall()` reverses this. | `Services/DcsExportInstaller.cs`:46-66, 108-146, 148-192, 210-248, 305-339; `OpenFreq.Client.csproj`:23-25 |
| Windows installer | Per-user (`PrivilegesRequired=lowest`), installs to `{localappdata}\OpenFreq DCS Client`, runs `--dcs-export install` / `uninstall`. It writes no registry keys and registers no URL protocol. | `installer/windows/OpenFreqDcsClient.iss`:9-45 |
| Jamming phase 6 | Plans a **server** GameGUI hook, `Scripts/Hooks/OpenFreqJamming.lua`, that sends mission jammers to the OpenFreq server. | `docs/jamming/JAMMING_DESIGN.md`:130-134, 146 |
| Lua tests | **There is no Lua test harness in the repo.** No test runs `.lua`. The only Lua-adjacent tests are C# (`DcsExportInstallerTests.cs`, `DcsCoordinateConversionTests.cs`). | — |

---

## 5. Architecture

```
DCS SERVER (dedicated or client-hosted)            PLAYER PC
---------------------------------------            -----------------------------------------------
Saved Games\<inst>\Config\OpenFreq.json  (admin / DCSServerBot)
        |  read on load + onMissionLoadEnd
Scripts\Hooks\OpenFreq-ServerGameGUI.lua
  onPlayerConnect / onPlayerStart  --- net.send_chat_to("OPENFREQ/1 ...", id) --->  DCS client GUI state
  onPlayerTrySendChat: drop players' "OPENFREQ/" lines                           Scripts\Hooks\OpenFreq-ClientGameGUI.lua
                                                                                   onNetConnect / onNetDisconnect (session)
                                                                                   onChatMessage(from == server id)
                                                                                   validate -> JSON
                                     Mods\Services\OpenFreqDCS\AutoConnect\client.json (written by app only)
                                                                                   | UDP 127.0.0.1:34323 (token)
                                                                                   | if no ack and launch allowed:
                                                                                   |   os.execute start "" "<recorded exe>"
                                                                                   v
                                                                         OpenFreq.Client.exe
                                                                           single-instance mutex + hand-off
                                                                           DcsAutoConnectService (policy)
                                                                           -> IOpenFreqService.Initialize(snapshot)
                                                                           -> ConnectAsync / DisconnectAsync
```

### 5.1 Server hook: `OpenFreq-ServerGameGUI.lua`

- **Where it is installed:** `Saved Games\<instance>\Scripts\Hooks\`. The admin copies it, or DCSServerBot does.
- **When it is active:** it does work only if `DCS.isMultiplayer() and DCS.isServer()` is true. This is evaluated **inside each callback**, not at load time, because hooks load at GUI start before any multiplayer session exists. It must also work on a player-hosted, non-dedicated server.
- **Config:** it reads `lfs.writedir() .. "Config\\OpenFreq.json"` with `io.open` and `net.json2lua`, inside `pcall`.
  - It re-reads the file on `onMissionLoadEnd`, so a DCSServerBot change applies on the next mission without restarting DCS.
  - If the file is missing or invalid, the hook does nothing (fail closed).
  - Because each DCS instance has its own write dir, several DCS servers on one machine work independently.
- **When it sends:**
  1. On `onPlayerConnect(id)` with `id ~= net.get_server_id()`, it records `pending[id] = true`.
  2. It sends on `onPlayerStart(id)`, when the player has entered the simulation and the chat window exists. For robustness it also sends on the player's first `onPlayerChangeSlot(id)` if `onPlayerStart` has not fired yet.
  3. It records `sent[id] = configHash`. It sends **at most once per connection** per config hash. A slot change, respawn or later `onPlayerChangeSlot` does not re-send.
  4. If `onMissionLoadEnd` loads a changed config (new hash), it re-sends once to connected players. An unchanged config sends nothing.
  5. `onPlayerDisconnect(id)` clears the player's entry.
  6. Re-sending on request: a player can type `-openfreq` in chat to get the line again, handled in `onPlayerTrySendChat`.
- **Anti-spoofing:** `onPlayerTrySendChat(playerID, msg, to)` returns `""` for player messages that start with `OPENFREQ/`, so players cannot even post a look-alike. This is defence in depth: the client also requires `from == server id`. For every other message it returns **nil**, so the chain continues to SRS's and DCSServerBot's chat hooks.
- **Return values:** every other callback returns nothing, as required for coexistence (§3).
- **Things it never does:** `net.dostring_in`, `os.execute`, and any network I/O.
- **Jamming sharing:** the same file is the home for jamming phase 6. Its internal layout is `OpenFreqServer.modules = { autoconnect = …, jamming = … }`, and `Config\OpenFreq.json` has an `autoconnect` section and later a `jamming` section. Recommendation: rename the planned `OpenFreqJamming.lua` to a module of this hook, so there is one server file and one config file.

### 5.2 Client hook: `OpenFreq-ClientGameGUI.lua`

- **Installed by the app.** Section 9 describes install and repair.
- **Session tracking:**

| Event | Action |
|---|---|
| `onNetConnect(localPlayerID)` while `isMultiplayer() and not isServer()` | `session = { id = <random hex>, serverHost = net.get_server_host(), since = os.time() }` |
| `onNetDisconnect(reason, code)` | Send `leave {sessionId, reason, code}`, then `session = nil` |
| `onNetMissionChanged`, `onSimulationStop` / `onSimulationStart`, `onPlayerChangeSlot`, `onGameEvent` | **Ignored.** These must never be treated as a leave. |

- **Receiving the advert:** `onChatMessage(message, from)` accepts a line only if **all** of these hold:
  1. `session ~= nil`
  2. `from == net.get_server_id()`
  3. `not DCS.isServer()`
  4. the line matches the anchored pattern `^OPENFREQ/(%d+) (.+)$`
  5. the version is supported
  6. the line is ≤ 300 bytes
  7. every field passes the validation in §7

  Adverts are de-duplicated per session by their exact text.
- **Forwarding:** it builds a `connect` request (§6.2) and sends it to `127.0.0.1:<appPort>` from a non-blocking LuaSocket UDP socket, the same pattern as OpenFreqDCS.lua:1500-1520. It then waits for an `ack` by polling `receive()` in `onSimulationFrame`, throttled to about 4 Hz. It retries every 2 s for up to 90 s.
- **Launching:** if no ack arrives within 3 s **and** `client.json` says `autoLaunch == true` **and** the recorded exe path is valid, it launches the app **once per session** (§8). It then keeps retrying the request until acked.
- **Local only:** it reads its port, token, exe path and consent flags **only** from `client.json`, which the app wrote. Nothing in the advert reaches the launch command or the file paths.
- **Return values:** all callbacks return nil.

### 5.3 App endpoint: `DcsAutoConnectService`

New in `OpenFreq.Client/Services/`, registered in `ServiceCollectionExtensions.AddOpenFreqServices` and started from `App.axaml.cs` with the other services.

- **Socket:** `UdpClient` on `IPEndPoint(IPAddress.Loopback, 34323)`.
  - It drops datagrams that come from a non-loopback address, are over 2 KB, have a wrong token, or are not valid JSON v1.
  - It acks every valid request to the sender's endpoint.
  - Bind failure is reported like `DcsExportService.DescribeListenerFailure`.
- **Session state per DCS session id:** `{dcsServerHost, target, autoConnected, backoff, cts}`.

#### Connect request handling

| Case | What happens |
|---|---|
| `ConnectionMode != DCS` | Ignore and log. BMS and GCI users are never redirected. |
| `AutoConnectEnabled == false` | If `AutoConnectConsentAsked == false`, show a one-time consent dialog (§7.4). Otherwise only show the status text "DCS server offers OpenFreq at X (auto-connect off)". |
| Already connected or connecting to the same target | **No-op.** The ack says `state: connected`. "Same target" means case-insensitive host plus port; also equal when both resolve to the same IP. |
| Connected to a **different** target | Apply the switching policy (§7.5). |
| Not connected | Connect with a **session snapshot**: `var s = Settings.GetSettings(); s.OpenFreqServerAddress = $"{host}:{port}"; s.OpenFreqPassword = ResolvePassword(...)`. Then call `Initialize(s, …)` and `ConnectAsync`, the same sequence as `ConnectWithTimeoutAsync` (`MainWindowViewModel.cs`:696-708). The saved settings and the address box are unchanged. The UI shows "Connected via DCS auto-connect: host:port". |

Refactor: extract the shared body of `ConnectAsync` / `ConnectWithTimeoutAsync` into `ConnectWithSettingsAsync(OpenFreqSettings snapshot, TimeSpan? timeout)` in `MainWindowViewModel`. The auto-connect service calls it through a small `IConnectionCoordinator` interface implemented by the view model, so it can be unit-tested with a fake.

**Manual-session guard:** if the current connection was made **manually** by the user, a `connect` request for a different target is treated as a switch (§7.5). A `leave` never disconnects a manual session.

---

## 6. Message formats

### 6.1 Server to client advert (one chat line, ASCII)

```
OPENFREQ/1 host=opmtnsen.com port=9987 sid=opmtnsen-main auth=0 -- OpenFreq voice available
```

- **Grammar:** `OPENFREQ/<ver>`, then space-separated `key=value` tokens. Everything after ` -- ` is human-readable text that the parser ignores.
- **Keys** (unknown keys are ignored, for forward compatibility):

| Key | Required | Rule | Meaning |
|---|---|---|---|
| `host` | yes | §7.1 | OpenFreq server host |
| `port` | yes | 1024-65535 | WebSocket port |
| `sid` | no | `[A-Za-z0-9._-]{1,32}` | Stable server identity, used for consent memory and the "same server" check |
| `auth` | no | `0` or `1` | 1 means the OpenFreq server needs a password, so the client knows to prompt |
| `pw` | no | percent-encoded, ≤ 64 bytes | Only if the admin sets `advertise_password: true`. Default off, see §7.3. |

- **Versioning:** a client accepts versions ≤ its maximum. Version 2+ may add keys, and a breaking change bumps the number.
- **Alternative considered and rejected:** SRS-style prefixes such as "OpenFreq Running @ host:port". The proposed format is still readable, and it is unambiguous and extensible.

### 6.2 Hook to app (UDP JSON, one datagram)

```json
{ "type": "openfreq.dcs.autoconnect", "v": 1, "token": "<from client.json>", "seq": 17,
  "action": "connect",
  "session": { "id": "9f3c…", "dcsServer": "203.0.113.5:10308", "since": 1762700000 },
  "target":  { "host": "opmtnsen.com", "port": 9987, "sid": "opmtnsen-main", "auth": 1, "pw": null } }
```

- `action` is one of `connect`, `leave` (with `reason` and `code`), or `ping`.
- The app replies:

```json
{ "type": "openfreq.dcs.ack", "v": 1, "seq": 17, "state": "connected|connecting|pending-consent|ignored|rejected", "msg": "..." }
```

- `seq` is a per-session counter, and the app ignores a `seq` it has already seen.
- `client.json` is written by the app, read by the hook, and stored at `Saved Games\<DCS*>\Mods\Services\OpenFreqDCS\AutoConnect\client.json`:

```json
{ "v": 1, "port": 34323, "token": "<128-bit random hex, per install>",
  "exe": "C:\\Users\\X\\AppData\\Local\\OpenFreq DCS Client\\OpenFreq.Client.exe",
  "autoConnect": true, "autoLaunch": false, "profile": "" }
```

---

## 7. Security model

### 7.1 What is validated, and where

| Check | Hook | App |
|---|---|---|
| Sender is the joined server (`from == net.get_server_id()`, session active, client role) | ✔ | — |
| Anchored prefix, version, length ≤ 300 bytes | ✔ | ✔ (JSON schema) |
| Host: lowercase; IPv4 dotted literal, or RFC 1123 hostname with labels `[a-z0-9-]{1,63}`, no leading or trailing hyphen, ≤ 253 chars, at least one dot or a literal. No IPv6 (`Util.ResolveAddress` cannot parse it). No `@`, `/`, `%`, `:`, spaces or quotes. | ✔ | ✔ (authoritative) |
| Port 1024-65535, integer | ✔ | ✔ |
| Resolved address must not be loopback (127/8), link-local, multicast or 0.0.0.0. Private RFC 1918 addresses are allowed **only if** the DCS server host is itself private or loopback (LAN play). This blocks a public server from pointing a player's client at their own router or LAN devices. | — | ✔ |
| Token matches `client.json` | — | ✔ |
| Source endpoint is loopback | — | ✔ (bound to 127.0.0.1 anyway) |

### 7.2 Why a server cannot run commands or launch arbitrary paths

- The only thing the server controls is the text of one chat line. It is parsed into `{host, port, sid, auth, pw}`.
- **None** of those values is ever used in a file path or passed to `os.execute`.
- The launch command line is built only from `client.json.exe`. The app writes that file from `Environment.ProcessPath`, the same source as the Start-menu shortcut (`DcsExportInstaller.cs`:307).
- The hook re-checks the path before launching:
  1. It is absolute.
  2. The filename is exactly `OpenFreq.Client.exe` (case-insensitive).
  3. It contains none of `"`, `%`, `^`, `&`, `|`, `<`, `>`, or a newline.
  4. `io.open(exe, "rb")` succeeds.
- The exe is launched **without arguments**. The hook re-sends the request once the app is up, so no advert data is ever on a command line.

### 7.3 Credentials

- Saved `OpenFreqServerAddress` and `OpenFreqPassword` are **never** written by auto-connect.
- **Password resolution order:**
  1. The password stored for this target in a new `AutoConnectServerPasswords` map, keyed by `host:port`. Entries are written only when the user ticks "Remember" in the prompt.
  2. The saved `OpenFreqPassword`, if the saved `OpenFreqServerAddress` equals the target.
  3. The advertised `pw`, used for this session only and never persisted.
  4. If `auth=1` and no password is known, a password prompt with a "Remember for this server" checkbox, unticked by default.
- **Wrong password:** auth fails, which surfaces as an exception from `ConnectAsync` and a `ConnectionFailed` disconnect. The app then stops retrying and prompts. It never loops.
- **Advertising a password:** `pw` in chat is visible to the player and is likely kept in DCS chat history and logs [U]. The DCSServerBot and admin docs mark it as "only for low-value shared passwords". It is off by default.

### 7.4 Consent

- `AutoConnectEnabled` and `AutoLaunchEnabled` default to false. They change only through the Settings page or the consent dialog. Both setters fire `PersistImmediately` (`SettingsViewModel.cs`:961-976) and rewrite `client.json`, so the hook sees the change without an app restart.
- **First advert while auto-connect is off:** a one-time dialog appears: "This DCS server (dcsHost) offers OpenFreq at host:port. Connect automatically when you join DCS servers?" The buttons are **[Connect now]** (this time only), **[Always]**, which sets AutoConnectEnabled, and **[No]**. A checkbox offers "Also start OpenFreq automatically when DCS needs it", which sets AutoLaunchEnabled.
- Auto-launch consent can only be given inside the app. A server can never cause a launch on a machine where the user has not ticked it.

### 7.5 Switching servers

When connected to target A and a request for B arrives:

| Setting / state | Behaviour |
|---|---|
| `AllowAutomaticServerSwitching == false` (default) | Bring the window to the front with the prompt "Switch from A to B (offered by DCS server X)?" [Switch] [Stay]. "Stay" is remembered for this DCS session, so there is no re-prompt. |
| `AllowAutomaticServerSwitching == true` **and** the current connection was made by auto-connect | Switch silently: DisconnectAsync, then connect with the new snapshot. |
| Current connection is **manual** | Always prompt, whatever the setting says. |

### 7.6 Residual risks

- **Server-originated relays.** Any script on the *server* that relays outside text as server chat could send a valid advert. An example would be a Discord-to-game bridge [U]. Such text only causes a connection to an OpenFreq address, and the user's consent and switching policy still apply. A future mitigation is for the OpenFreq server to report a `serverId` in the auth reply, which the client compares with the advert's `sid`. That needs a server change; see §13.
- **Local processes.** A local process can read `client.json` and send requests. It already runs as the user, so this is not a privilege boundary. The token stops accidental cross-talk and casual scripts.

---

## 8. Launch and single-instance behaviour

1. **App side: named mutex.** In `Program.Main`, after profile parsing and before DI, the app takes `Local\OpenFreq.Client{ProfileSuffix}`.
   - If the mutex is already held, it sends `{"action":"ping"}` to 34323. This also brings the existing window to the front, via a new `show` action in the same protocol.
   - It then exits with code 0.
   - The `--dcs-export` CLI path (`Program.cs`:32) stays exempt, because it is headless.
   - The staged-update relaunch already waits for other instances (`Program.cs`:194-201). The mutex must be released before `LaunchApplyAndRestart`.
2. **Hook side: launch once per session.**
   - It runs `os.execute('start "" "' .. exe .. '"')` only after the first request went unacked for 3 s, and at most once per DCS session.
   - Because of the mutex, launching while the app is running is harmless, so there is no fragile process enumeration in Lua.
   - **Alternatives if T5 shows a console flash or a stall:** `io.popen` in a background-free form, or a small helper. A native DLL as SRS uses is a last resort, because of its native-code cost.
3. **Path recording for portable and installed copies.**
   - `DcsExportInstaller.EnsureInstalled()` already runs on every app start. It is extended to write `client.json` with `exe = Environment.ProcessPath`. A portable copy moved to another folder therefore re-registers itself the next time it starts.
   - The Inno installer's `--dcs-export install` step does the same, so an installed copy is registered even before its first launch.
   - No registry key is needed, because Lua cannot read the registry without native code or shelling out to `reg.exe`.
   - **Several copies:** the last one started wins, which is intended. A `--profile` instance writes `profile` and does **not** overwrite `client.json` unless it is the default profile.
4. **Never killing the app.** On leave, the app only disconnects. An optional `CloseAppOnServerLeave` setting (default off) closes gracefully, and only an instance that was itself started by the hook. The launch is marked by an env var or a `--launched-by-dcs` flag; that flag carries no data.

---

## 9. Leave, respawn and mission-change state machine (app side)

```
            connect(req, session S)                   leave(S) / DCS process exited
Idle ───────────────────────────────► AutoActive(S) ─────────────────────────────► LeavePending(S, 10 s)
  ▲                                     │   ▲  │ drop (ConnectionLost after the inner 60 s)        │
  │                                     │   │  ▼                                                    │ connect(req, S' same target)
  │                                     │  Backoff(S): 5,10,20,40,60,60… s ±20% → retry             │   → AutoActive(S'), no disconnect
  │ user Disconnect / auth failure      │                                                           │ timeout
  └─────────────────────────────────────┴───────────────────────────────────────────────────────────┘ → DisconnectAsync if DisconnectOnServerLeave
```

- **Session boundary.** Only `onNetConnect` and `onNetDisconnect` move the session. Slot change, respawn, death, loading screens, `onNetMissionChanged`, `onSimulationStop` and `onSimulationStart` produce **no** message. The connection is untouched, matching the "must not disconnect" list.
- **Rejoin grace (10 s).** If a request for the same target arrives in time (for example after a quick rejoin or a DCS server restart), the voice connection survives.
- **Crash or kill of DCS.** No `onNetDisconnect` is sent. The service watches the DCS process, polling every 5 s for `DCS.exe` (any DCS exe name). When it disappears, the service treats that as `leave`.
- **Outer reconnect** (`AutoReconnectEnabled`, default on):
  - It applies only while the session is active and the connection is auto-made. The inner 60 s `ReconnectAsync` runs first.
  - After `ConnectionLost` or `ConnectionFailed`, the service retries with exponential backoff (5 s doubling to 60 s, ±20% jitter) until it succeeds or the session ends.
  - It stops on: a user Disconnect (`DisconnectReason.UserRequested`), a version mismatch (`VersionMismatchException`), or an auth or password failure.
- **Duplicate suppression, at three layers:**
  - The server sends once per player per config hash.
  - The hook de-duplicates per session by text.
  - The app ignores repeated `seq` values and treats "already on target" as a no-op.

---

## 10. Settings

These are added to `OpenFreqSettings` (`Models/Settings.cs`), mirrored in `SettingsViewModel` and included in `GetSettings()` / load, following the `AutoUpdateEnabled` pattern.

| Setting | Default | Notes |
|---|---|---|
| `AutoConnectEnabled` | false | Requires consent |
| `AutoLaunchEnabled` | false | Requires consent; written into `client.json` |
| `AutoReconnectEnabled` | true | Outer backoff loop only |
| `DisconnectOnServerLeave` | true | Never applies to manual connections |
| `AllowAutomaticServerSwitching` | false | Never applies over a manual connection |
| `AutoConnectConsentAsked` | false | Internal; set after the one-time dialog |
| `AutoConnectServerPasswords` | `{}` | `Dictionary<string,string>`, keyed by `host:port`. Written only via "Remember". |
| `CloseAppOnServerLeave` | false | Optional, see §8 |

The UI is a "DCS multiplayer" card on the Settings page, shown in DCS mode.

---

## 11. Server configuration

### 11.1 `Saved Games\<instance>\Config\OpenFreq.json`

The server hook reads this file. It is the DCSServerBot contract.

```json
{
  "v": 1,
  "autoconnect": {
    "enabled": true,
    "host": "opmtnsen.com",
    "port": 9987,
    "sid": "opmtnsen-main",
    "auth": true,
    "advertise_password": false,
    "password": null,
    "chat_command": "-openfreq"
  }
}
```

The `jamming` section is reserved for phase 6.

**Manual setup without DCSServerBot:**
1. Copy `OpenFreq-ServerGameGUI.lua` into `Scripts\Hooks`.
2. Create `Config\OpenFreq.json`.
3. Restart DCS once. After that, a config change applies on the next mission load.

Several DCS instances on one machine each have their own write dir, so each gets its own file and is independent.

### 11.2 DCSServerBot extension

There is **no existing OpenFreq extension upstream** [V] (no `openfreq` path in the repo tree). Extensions load by a dotted module name (`core/data/impl/serverimpl.py`:884-901), so an out-of-tree `openfreq.OpenFreq` class works without forking the bot. Whether `extensions_schema.yaml` rejects unknown names is [U].

**nodes.yaml example**, mirroring the SRS keys in `extensions/srs/README.md`:

```yaml
DCS.release_server:
  extensions:
    OpenFreq:            # or openfreq.OpenFreq when loaded out of tree
      enabled: true
      host: opmtnsen.com            # default: node.public_ip
      websocket_port: 9987
      autoconnect: true
      sid: opmtnsen-main            # default: instance name
      auth: true
      advertise_password: false
      password: '...'               # only used if advertise_password
```

**Extension behaviour**, mirroring `extensions/srs/extension.py`:

| Method | Does |
|---|---|
| `prepare()` | Writes `instance.home\Config\OpenFreq.json` (atomic write; jamming keys preserved). If autoconnect is on, copies `OpenFreq-ServerGameGUI.lua` into `instance.home\Scripts\Hooks\`, backing up a foreign file to `.bak`. SRS does the same in `enable_autoconnect`, `extension.py`:160-199. |
| disable / `uninstall()` | Deletes the hook, or sets `enabled:false` |
| `render()` | Returns `host:websocket_port` |
| `get_ports()` | Returns `{"OpenFreq WebSocket": Port(websocket_port, TCP, public=True)}` |
| `is_running()` | Optional TCP probe of the port |

- The extension does **not** start, stop or configure `OpenFreq.Server.exe`.
- Coexistence: DCSServerBot's own `_install_luas` copies `Scripts\` and removes only `Scripts\net\DCSServerBot` (`serverimpl.py`:514-540), so our hook file survives.
- The hook source can ship with the extension, or be downloaded from the OpenFreq release that matches the server version.

---

## 12. Install and repair

| Side | How |
|---|---|
| Client hook | Add `DCS/OpenFreqDCS/Hooks/OpenFreq-ClientGameGUI.lua` as an embedded resource (it is already covered by the `DCS\OpenFreqDCS\**` glob, `csproj`:23-25). `EnsureDcsExportInstalled` copies it to `<SavedGames>\Scripts\Hooks\` with a byte-compare, like `ExtractExportResources`, and writes `AutoConnect\client.json`. `Uninstall()` deletes both. The installer's existing `--dcs-export install` / `uninstall` steps cover this unchanged. |
| Client hook contents | The hook file is a thin, stable loader, like SRS's `DCS-SRS-hook.lua`: it `dofile`s `Mods\Services\OpenFreqDCS\Scripts\OpenFreqAutoConnectGameGUI.lua` inside `pcall`, logging to `Logs\OpenFreqDCS.log`. Updates then only replace the Mods file. |
| Always installed? | Yes: the hook is installed even when auto-connect is off. It forwards nothing unless `client.json.autoConnect` is true, except one "consent needed" request per session, sent only if the app is already running. This avoids writing into Hooks only after consent, which would surprise the user later. |
| Server hook | Shipped in the server zip and installer as `DCS/Server/OpenFreq-ServerGameGUI.lua` with a sample `OpenFreq.json`. It is installed by the admin or by DCSServerBot. |
| One file or two | **Two files** are recommended. A single file that switches role with `isMultiplayer()` / `isServer()` is possible, but `isServer()` is also true in single-player (§3), and players would end up with server code they never use. The client hook must still guard every callback with `isMultiplayer() and not isServer()`, so a player hosting a game does not treat their own chat as adverts. |

---

## 13. Test plan

The requirement's 14 scenarios are referred to below by short name.

**Lua harness to add.** No Lua test harness exists yet. Add a `tools/LuaHookTests` runner that uses a bundled Lua 5.1 interpreter, or MoonSharp or NLua from an xUnit test in `OpenFreq.Client.Tests`. It loads each hook with stub `DCS`, `net`, `lfs` and `socket` tables and a fake clock, then drives the callbacks.

| # | Scenario | Automated | How |
|---|---|---|---|
| T1 | App running, not connected, join server, then it connects | Lua + C# | Hook stub emits `connect`. `DcsAutoConnectService` test with a fake `IConnectionCoordinator` asserts the snapshot address and that saved settings are unchanged. |
| T2 | App not running, launch allowed, then launched and connected | Lua; manual | Lua test asserts exactly one `os.execute` with the recorded path after the ack timeout, and none when `autoLaunch=false`. Manual test in DCS. |
| T3 | App running, then no duplicate instance | C#; manual | Mutex and hand-off unit test, plus a test that a second process exits 0. Manual: double-launch. |
| T4 | Already on the same server, then no-op | C# | Same host, case variants and same resolved IP all count as the same server |
| T5 | Launch from DCS does not stall or flash; portable path; installer path | Manual | `os.execute` in DCS with portable and installed copies, and a path containing spaces |
| T6 | Different server: prompt, switch policy, manual session protected | C# | Policy matrix from §7.5 |
| T7 | Drop while in the same DCS server, then backoff reconnect; stop on user disconnect or auth failure | C# | Fake coordinator raises ConnectionLost; fake clock checks the 5/10/20/40/60 s schedule |
| T8 | Leave DCS server, then disconnect after grace (setting on/off); quick rejoin keeps the connection | Lua + C# | `onNetDisconnect` produces `leave`; state machine test |
| T9 | Loading screen, slot change, respawn, mission rotation: no disconnect, no re-advert | Lua (both hooks); manual | Assert no `leave` and no second send. **Manual verification of the [U] callback assumptions is required.** |
| T10 | Spoofing: chat from a player id, unanchored text, bad host or port, private IP from a public server, wrong token, non-loopback sender | Lua + C# | Table-driven validation tests |
| T11 | Credentials: saved password never overwritten; "Remember" path; wrong password prompts and stops retries | C# | Assert `OpenFreqSettings` after the flows |
| T12 | Server hook: per-player once, multiple players, config reload on mission load, hash change re-sends once, `onPlayerTrySendChat` drops look-alikes and returns nil otherwise | Lua | Stub `net.send_chat_to` recorder |
| T13 | Coexistence with SRS hooks and DCSServerBot hooks, and with OpenFreq Export.lua and other exports; dedicated server | Manual (plus a Lua chain test) | Lua test runs our hook next to a stub SRS hook in the same chain and asserts nil returns. Manual test on a dedicated server with SRS autoconnect and DCSServerBot. |
| T14 | DCSServerBot generation: nodes.yaml to `OpenFreq.json` and hook copy; manual config without the bot | Python unit test in the extension; manual | — |

**Also automated:**
- Installer tests extend `DcsExportInstallerTests.cs`: hook copy and byte-compare, `client.json` content, uninstall removes both, and a `--profile` instance does not overwrite.
- Settings round-trip of the new fields.

---

## 14. Open questions for the user

1. **Port number.** The example uses **9937**, but the OpenFreq default WebSocket port is **9987** (`OpenFreqRtcClient.cs`:19; handbook). Was 9937 a typo, or does opmtnsen use a custom port?
2. **Visible chat line.** DCS has no hidden channel, so players will see the advert line. Is the proposed text acceptable, and should the server hook offer a "quiet" option that skips the human-readable suffix?
3. **Server-side identity check.** Should the OpenFreq server later report a `serverId`/name in its auth reply, so the client can verify the advert's `sid`? This is a small server change, separate from DCSServerBot.
4. **Password in adverts.** Should advertising a password be allowed at all, or should the design support only "prompt and remember"?
5. **Existing DCSServerBot extension.** The brief mentions an extension you "already have", but none exists upstream. Where is it, so its keys can be aligned?
6. **Jamming phase 6.** OK to fold `OpenFreqJamming.lua` into `OpenFreq-ServerGameGUI.lua` with the shared `Config\OpenFreq.json`?
7. **`CloseAppOnServerLeave`.** Do you want this sixth setting, or is disconnect-only enough?
8. **Mode switching.** Should an advert switch a user who is currently in GCI mode into DCS mode, or always be ignored outside DCS mode, as proposed?

## 15. Decisions (2026-11-09)

1. **Port:** 9937 is opmtnsen's configured OpenFreq WebSocket port (its server is not on the 9987 default). Port is always taken from the server config; no default assumed in adverts.
2. **Chat line:** accepted — one short line per join, the `OPENFREQ/1 ...` format of §6.1. No quiet option needed.
3. **Server identity:** `sid` is display-only for now; no server-side verification change.
4. **Password:** **never advertised.** If the server requires one, OpenFreq prompts the player and offers to remember it for that server only (§7.3).
5. **DCSServerBot:** don't write an extension; document the interface (files/keys an extension must write, §11.2) in docs/autoconnect/DCSSERVERBOT.md.
6. **Jamming phase 6:** yes — one `OpenFreq-ServerGameGUI.lua` and one `Config\OpenFreq.json` with sections (`autoConnect`, later `jamming`).
7. **CloseAppOnServerLeave:** add it as a sixth setting, default **off**.
8. **Mode:** adverts are ignored (and logged) outside DCS mode; never switch modes automatically.
