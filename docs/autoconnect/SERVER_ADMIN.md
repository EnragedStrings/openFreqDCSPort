# DCS auto-connect: server admin guide

This guide is for people who run a DCS multiplayer server and an OpenFreq server. When it is set up,
players who join your DCS server get one chat line telling their OpenFreq client where your OpenFreq
server is. Their client then connects by itself, if the player has allowed that.

The OpenFreq server program itself needs no changes. Everything here happens on the DCS server.

Design background: [AUTOCONNECT_DESIGN.md](AUTOCONNECT_DESIGN.md). DCSServerBot: [DCSSERVERBOT.md](DCSSERVERBOT.md).

## What players see

Each player gets **one** line in DCS chat when they join, for example:

```
OPENFREQ/1 host=voice.example.com port=9937 sid=example-main auth=1 -- OpenFreq voice available
```

- The line is sent once per player per connection. A slot change, a respawn, a death or a mission
  rotation does not send it again.
- It is sent again after a mission load only if you changed the config.
- A player can type `-openfreq` in chat to get the line again (at most once every 10 seconds).
- **The line never contains a password.** If your OpenFreq server has one, the player's client asks
  for it once and can remember it for your server.
- Players cannot fake the line: the hook drops any player chat message that starts with `OPENFREQ/`,
  and the client only accepts the line from the server itself.

## Install (manual, without DCSServerBot)

You need two files from this repository's `DCS/Server` folder (the OpenFreq DCS Server installer
also copies them to `<install folder>\DCS-Hooks`):

| File | Copy to |
|---|---|
| `OpenFreq-ServerGameGUI.lua` | `Saved Games\<instance>\Scripts\Hooks\OpenFreq-ServerGameGUI.lua` |
| `OpenFreq.example.json` | `Saved Games\<instance>\Config\OpenFreq.json` (rename it), then edit it |

`<instance>` is the DCS write folder of the server, for example `Saved Games\DCS.release_server` or
`Saved Games\DCS.server2`. It is the folder that contains `Logs\dcs.log` for that server.

Then restart DCS once. After that, config changes apply on the next mission load; you do not need to
restart DCS again.

## Config: `Config\OpenFreq.json`

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

| Key | Required | Meaning |
|---|---|---|
| `enabled` | yes | Must be `true` to send anything. |
| `host` | yes | The OpenFreq server address players connect to: a DNS name with at least one dot, or an IPv4 address. Lowercased automatically. No IPv6, no `localhost`. |
| `port` | yes | The OpenFreq **WebSocket** port (TCP), 1024-65535. The OpenFreq default is 9987; use whatever your server runs on. The audio UDP port is not needed: the client learns it from the server. |
| `sid` | no | A short name for your server, `[A-Za-z0-9._-]`, up to 32 characters. Shown to players. |
| `auth` | no | `true` if your OpenFreq server needs a password. The client then asks the player for it. Default `false`. |
| `chat_command` | no | The command players type to get the line again. Default `-openfreq`. |

Notes:
- A `password` key, if present, is **ignored**. Passwords are never advertised.
- The section can also be spelled `autoconnect`. A `jamming` section is reserved for a later
  feature that will live in the same hook file and the same config file.
- If the file is missing or invalid, or `enabled` is not `true`, the hook does nothing.
- Private addresses (for example `192.168.x.x`) are only accepted by players who reach your DCS
  server over a private address too (LAN play). Public DCS servers must advertise a public address.

## Several DCS servers on one machine

Each DCS instance has its own write folder (`Saved Games\DCS.server1`, `Saved Games\DCS.server2`, ...).
Install the hook and an `OpenFreq.json` in **each** instance that should advertise OpenFreq. They are
independent: each can point to a different OpenFreq server or port, or be disabled.

## Coexistence

- The hook returns nothing from every DCS callback except to drop a fake advert, so DCS-SRS,
  DCSServerBot, and other hooks keep working. It does not use `net.dostring_in` or `os.execute` and
  does no network I/O.
- It works on dedicated servers and on player-hosted servers. It does nothing in single player.
- SRS auto-connect and OpenFreq auto-connect can run side by side.

## Checking that it works

- The hook writes to `Saved Games\<instance>\Logs\OpenFreqServer.log` (at most 200 lines per DCS
  run): the advert line it will send, or why it is disabled, and dropped look-alikes.
- Join your server with a second PC (or ask a player) and look for the line in chat.

## Removing it

Delete `Scripts\Hooks\OpenFreq-ServerGameGUI.lua` (or set `"enabled": false`) and restart DCS.
