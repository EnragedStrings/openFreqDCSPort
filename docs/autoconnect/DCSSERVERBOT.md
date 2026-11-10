# DCS auto-connect: interface for a DCSServerBot extension

OpenFreq does not ship a DCSServerBot extension (design decision 5). This page is the contract an
extension (or any other server manager) must implement so OpenFreq auto-connect works on servers
managed by [DCSServerBot](https://github.com/Special-K-s-Flightsim-Bots/DCSServerBot). Everything
here can also be done by hand: see [SERVER_ADMIN.md](SERVER_ADMIN.md).

The OpenFreq server program is not started, stopped or configured by this interface. The extension
only advertises an OpenFreq server that runs elsewhere (or on the same machine).

## What the extension must do

For each DCS instance where OpenFreq is enabled (`instance.home` is that instance's write folder,
for example `Saved Games\DCS.release_server`):

1. **Write `instance.home\Config\OpenFreq.json`** (UTF-8, no BOM). Write atomically: write a
   temporary file in the same folder and rename it over the old one. Keep any top-level sections you
   don't own (`jamming` is reserved for a later OpenFreq feature): read the existing file, replace
   only the `autoConnect` section and `v`, write it back.

   ```json
   {
     "v": 1,
     "autoConnect": {
       "enabled": true,
       "host": "voice.example.com",
       "port": 9937,
       "sid": "example-main",
       "auth": true,
       "chat_command": "-openfreq"
     }
   }
   ```

   Rules the hook enforces (an invalid value disables the advert and is logged to
   `instance.home\Logs\OpenFreqServer.log`):

   | Key | Type | Rule |
   |---|---|---|
   | `v` | number | `1` |
   | `autoConnect.enabled` | bool | must be `true` to advertise |
   | `autoConnect.host` | string | DNS name with at least one dot, or IPv4 literal; labels `[a-z0-9-]`, max 253 chars; lowercased by the hook; no IPv6, no `localhost` |
   | `autoConnect.port` | number | OpenFreq WebSocket (TCP) port, integer 1024-65535 |
   | `autoConnect.sid` | string, optional | `[A-Za-z0-9._-]{1,32}` |
   | `autoConnect.auth` | bool, optional | `true` if the OpenFreq server needs a password |
   | `autoConnect.chat_command` | string, optional | `-?[A-Za-z0-9_]{1,31}`, default `-openfreq` |

   **Never write a password into this file.** Passwords are never advertised; a `password` key is
   ignored. Players are prompted by their client and can remember the password per server.

2. **Copy the hook** `OpenFreq-ServerGameGUI.lua` (from the OpenFreq release matching the
   OpenFreq server version, `DCS/Server/` in the repository) to
   `instance.home\Scripts\Hooks\OpenFreq-ServerGameGUI.lua`. If a different file already has that
   name, back it up to `.bak` first. Copy only when the content differs.

3. **On disable/uninstall:** either set `"enabled": false` (keeps the hook, sends nothing), or
   delete `Scripts\Hooks\OpenFreq-ServerGameGUI.lua`. Leave `OpenFreq.json` if other sections are in
   it.

The hook re-reads `OpenFreq.json` on every mission load, so a change applies on the next mission
without restarting DCS. Copying or replacing the hook file needs a DCS restart.

DCSServerBot's own Lua install step copies its `Scripts\` tree and removes only
`Scripts\net\DCSServerBot`, so the OpenFreq hook survives bot updates.

## Proposed `nodes.yaml` mapping

Modelled on the bot's SRS extension. When loaded out of tree, the extension's dotted class name
(for example `openfreq.OpenFreq`) is used as the key.

```yaml
DCS.release_server:
  extensions:
    OpenFreq:
      enabled: true                 # write the config and install the hook
      autoconnect: true             # -> autoConnect.enabled
      host: voice.example.com       # -> autoConnect.host (default: the node's public IP)
      websocket_port: 9937          # -> autoConnect.port
      sid: example-main             # -> autoConnect.sid (default: the instance name)
      auth: true                    # -> autoConnect.auth
      chat_command: -openfreq       # -> autoConnect.chat_command
```

| nodes.yaml key | OpenFreq.json key | Default |
|---|---|---|
| `enabled` | (install the hook and write the file at all) | `false` |
| `autoconnect` | `autoConnect.enabled` | `true` |
| `host` | `autoConnect.host` | node public IP |
| `websocket_port` | `autoConnect.port` | `9987` (OpenFreq default; set it explicitly if your server differs) |
| `sid` | `autoConnect.sid` | instance name, reduced to `[A-Za-z0-9._-]`, max 32 |
| `auth` | `autoConnect.auth` | `false` |
| `chat_command` | `autoConnect.chat_command` | `-openfreq` |

## Suggested extension methods

| Method | Does |
|---|---|
| `prepare()` | Steps 1 and 2 above. |
| `shutdown()` / disable | Step 3. |
| `render()` | Show `host:websocket_port` in the bot's server status. |
| `get_ports()` | `{"OpenFreq WebSocket": websocket_port}` (TCP, public) if the OpenFreq server runs on this node. |
| `is_running()` | Optional: TCP connect to `host:websocket_port`. |

Whether the bot's extension schema accepts an unknown extension name without a schema entry has not
been verified.

## Several instances on one node

Each instance has its own `instance.home`, so each gets its own `OpenFreq.json` and hook copy. They
can point to different OpenFreq servers.
