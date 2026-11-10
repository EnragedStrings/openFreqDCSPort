# Mission jammers (jamming phase 6)

Put a jammer in the mission by naming a unit. The OpenFreq server hook on the **DCS server**
(`OpenFreq-ServerGameGUI.lua`, module `jamming`) finds those units and sends their position and alive state to the
OpenFreq server about every 2 s. The OpenFreq server turns each one into a jammer, exactly as if it were in the
server config, and switches it off when the unit dies, is removed, or the mission ends. Kill the jammer, end the
jamming.

## 1. Naming units

A unit, static object or group whose name starts with the prefix (default `JAM:`) is a jammer. A tag on a group
name puts the jammer on the group's first unit.

```
JAM:<preset>[;key=value...]        a jammer from a preset (docs/jamming/JAMMING_CONFIG.md § Presets)
JAM:@<configId>[;key=value...]     a copy of the server-config jammer <configId>, placed at this unit
```

Anything from a `#` on is ignored, so DCS's copy suffix does no harm: `JAM:r934b;mode=auto #001` is the same tag as
`JAM:r934b;mode=auto`. Values may contain spaces (`name=Sinitsa 1`); the preset or `@id` may not. Keys are case-insensitive; frequencies are in **MHz**.

| Key | Example | Meaning |
|---|---|---|
| `mode` | `mode=auto` | `scripted` (default) or `auto` |
| `spot` | `spot=251.0,243.0` | Spot frequencies, MHz |
| `band` | `band=225-400` or `band=225-250,300-320` | Barrage / partial-band ranges, MHz |
| `autoband` | `autoband=225-400` | Auto mode: only pick inside these ranges, MHz |
| `protect` | `protect=243.0,251.0` | Protected frequencies, MHz |
| `power` | `power=250` | Transmitter power per PA, W |
| `tech` | `tech=Tone` | Technique: Noise, Tone, MultiTone, SteppedTones, Swept, RandomPulse, Spark, Gulls, Recorded |
| `gain` | `gain=8` | Antenna gain, dBi |
| `az`, `bw` | `az=270;bw=60` | Sector antenna: boresight (degrees true) and beamwidth; makes the pattern `Sector` |
| `resp` | `resp=1` | Responsive (follower / preamble) jamming |
| `name` | `name=Sinitsa 1` | Display name (default: the unit name) |

Unknown keys and bad values are logged by the OpenFreq server and ignored; an unknown preset or config id makes the
unit no jammer at all.

A server-config jammer used as a template (`@<configId>`) should have `"template": true`: it is then never radiated
on its own, only through units that name it.

The jammer's id is `dcs:<unit name>`. Its position is the unit's: latitude/longitude from `coord.LOtoLL`, and for
ground units and statics the antenna is the preset's mast height above the unit. Air units use their own altitude.

## 2. DCS server setup

In `Saved Games\<instance>\Config\OpenFreq.json` add a `jamming` section next to `autoConnect`:

```json
{
  "v": 1,
  "autoConnect": { "...": "..." },
  "jamming": {
    "enabled": true,
    "host": "127.0.0.1",
    "port": 9989,
    "secret": "a long random shared secret, at least 16 characters",
    "intervalSeconds": 2,
    "rescanSeconds": 30,
    "prefix": "JAM:"
  }
}
```

`host`/`port` are the OpenFreq server's mission-jammer intake (UDP). The `secret` must match the OpenFreq server's.

## 3. OpenFreq server setup

In `OpenFreq.Server.json`:

```json
"jamming": {
  "enabled": true,
  "dcsHook": {
    "enabled": true,
    "bindAddress": "0.0.0.0",
    "port": 9989,
    "secret": "the same secret",
    "staleAfterSeconds": 30,
    "maxJammers": 200
  },
  "jammers": [ ... ]
}
```

If the OpenFreq server runs on another machine than the DCS server, forward UDP `port` to it. The intake refuses to
start without a secret of at least 16 characters.

## 4. Wire protocol (hook → OpenFreq server)

One UDP datagram per update, at most 60,000 bytes:

```
OFJ1 <hmac> <json>
```

- `OFJ1` and single spaces are literal.
- `<hmac>` is HMAC-SHA256(key = UTF-8 secret, message = the `<json>` bytes exactly as sent), 64 lowercase hex digits.
- `<json>` is one UTF-8 JSON object:

```json
{
  "v": 1,
  "boot": "5f3a9c01",
  "seq": 17,
  "ts": 1731264000,
  "mission": "Op Mountain 03|1731263000",
  "units": [
    {
      "name": "JAM:r934b;mode=auto",
      "group": "Sinitsa",
      "kind": "ground",
      "side": 1,
      "type": "Ural-375",
      "alive": true,
      "lat": 42.1234, "lon": 43.5678, "alt": 512.0,
      "x": -281000.0, "y": 512.0, "z": 647000.0
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `v` | Protocol version, 1 |
| `boot` | Random hex id chosen when the hook loads (8–32 hex chars) |
| `seq` | Starts at 1 per `boot`, +1 every datagram |
| `ts` | Sender's Unix time, s (`os.time()`) |
| `mission` | Mission identity: mission name, `|`, an id that changes on every mission load. Empty string = no mission running (all mission jammers off) |
| `units` | Every tagged unit known in this mission, dead ones included (alive=false), at most 200 |
| `name`, `group` | Unit name and its group name (the tag is in one of them) |
| `kind` | `air`, `ground`, `ship` or `static` |
| `side` | Coalition: 0 neutral, 1 red, 2 blue |
| `alive` | Exists, is active (late activation counts as not alive) and has life > 0 |
| `lat`, `lon`, `alt` | Geodetic position, degrees and m MSL (`alt` is the unit's own height, not the antenna) |
| `x`, `y`, `z` | DCS mission-local position (x north, y altitude, z east), m |

Position fields may be missing for a dead unit.

The OpenFreq server accepts a datagram only when:
1. it starts with `OFJ1 `, the HMAC matches (constant-time compare) and the JSON parses with `v` = 1;
2. `|ts − server time| ≤ 300 s`;
3. `seq` is greater than the last accepted `seq` for that `boot` (a new `boot` resets this).

It then replaces the whole set of mission jammers with the ones in `units` (so a unit missing from the list is
gone). If no datagram is accepted for `staleAfterSeconds`, every mission jammer is removed.

## 5. Hook behaviour

- Only while the DCS instance is a multiplayer server, the `jamming` section is valid and LuaSocket loads.
- Reads units with `net.dostring_in("server", ...)` (the mission scripting environment). A full scan of
  `coalition.getGroups` and `coalition.getStaticObjects` for tagged names runs on mission load and every
  `rescanSeconds` (catches late spawns); every `intervalSeconds` it looks up the known names with
  `Unit.getByName` / `StaticObject.getByName` for position and alive state.
- Sends with a non-blocking LuaSocket UDP socket to an IP resolved once per mission load.
- On `onSimulationStop` sends one datagram with `mission` = "" and no units.
- Never blocks, never throws into DCS (everything in `pcall`), logs to `Logs\OpenFreqServer.log`.
