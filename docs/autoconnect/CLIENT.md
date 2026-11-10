# DCS auto-connect: player guide

When you join a DCS multiplayer server that offers OpenFreq, OpenFreq can connect to that server's
voice by itself, and DCS can start OpenFreq for you. Both are **off** until you turn them on.

Server admins: see [SERVER_ADMIN.md](SERVER_ADMIN.md).

## Turning it on

1. Start OpenFreq once (installed or portable). It installs a small DCS hook,
   `Saved Games\DCS...\Scripts\Hooks\OpenFreq-ClientGameGUI.lua`, next to its export script. The hook
   does nothing until you allow it.
2. Set OpenFreq to **DCS mode**. Offers are ignored in BMS and GCI mode, and OpenFreq never changes
   the mode by itself.
3. Either tick the options under **Settings > DCS multiplayer**, or wait for the first offer: when you
   join a DCS server that offers OpenFreq, OpenFreq asks once:
   - **Always**: turn auto-connect on and connect.
   - **Connect now**: connect this time only.
   - **No**: don't connect, and don't ask again (you can still turn it on in Settings).
   - The checkbox **Also start OpenFreq automatically when DCS needs it** turns on auto-launch.

The question appears in the OpenFreq window. OpenFreq does not jump in front of a full-screen DCS;
DCS shows a one-line hint in chat instead. Switch to OpenFreq (Alt+Tab) to answer.

## Settings (Settings > DCS multiplayer, DCS mode)

| Setting | Default | What it does |
|---|---|---|
| Auto-connect when a DCS server offers OpenFreq | off | Connect to the OpenFreq server the joined DCS server offers. |
| Start OpenFreq from DCS when needed | off | If OpenFreq is not running when an offer arrives, DCS starts it. |
| Reconnect automatically while in the DCS server | on | If an auto-made connection drops, retry after 5, 10, 20, 40, 60, 60... seconds while you are still in that DCS server. |
| Disconnect after leaving the DCS server | on | After you leave the DCS server (and 10 seconds pass without rejoining), disconnect the auto-made connection. |
| Switch servers without asking | off | If you join a DCS server that offers a different OpenFreq server, switch without asking. |
| Close OpenFreq after leaving | off | Close OpenFreq after leaving the DCS server, only if DCS started it. |

What auto-connect never does:
- It never changes your saved server address or password. Auto-connect uses the offered server for
  that DCS session only.
- It never disconnects or switches a connection **you** made. If you are connected manually and a DCS
  server offers a different server, OpenFreq asks first, whatever the settings say.
- Slot changes, respawns, deaths, loading screens and mission changes never disconnect you.
- If you press **Disconnect**, auto-connect leaves you alone until you join a DCS server again.
- It never closes OpenFreq unless you turned on "Close OpenFreq after leaving" and DCS started it.

If you are already connected to the offered server, nothing happens.

## Passwords

DCS servers never send passwords. If the OpenFreq server needs one, OpenFreq uses, in order:
1. the password you told it to remember for that server;
2. your saved password, if your saved address is that same server;
3. otherwise it asks you. Tick **Remember for this server** to store it (only after it worked).

A wrong password stops auto-connect for that server until you enter a new one; it never retries in a
loop. **Forget remembered server passwords** in the DCS multiplayer card clears them.

## Installed vs portable

Both work without admin rights.
- **Installer:** installs to `%LOCALAPPDATA%\OpenFreq DCS Client` and sets up DCS during install.
- **Portable:** run `OpenFreq.Client.exe` from any folder. Every start re-registers the folder it runs
  from, so moving the folder just needs one start from the new place.

DCS can only start the copy that registered itself last (its path is in
`Saved Games\DCS...\Mods\Services\OpenFreqDCS\AutoConnect\client.json`). Nothing a DCS server sends
is ever used to start a program. Only one OpenFreq window can run at a time; starting it again just
brings the open window to the front. (`--profile <name>` instances are separate and do not take part
in auto-connect.)

## Troubleshooting

| Symptom | Check |
|---|---|
| Nothing happens when joining | Is OpenFreq in DCS mode? Is auto-connect on (or did you answer the question)? Does the server actually offer OpenFreq (look for the `OPENFREQ/1` line in chat, or type `-openfreq`)? |
| "auto-connect is off" in the DCS multiplayer card | You answered **No** earlier. Tick the setting. |
| DCS doesn't start OpenFreq | Auto-launch must be on, and OpenFreq must have been started at least once from its current folder. Folders with non-ASCII characters (for example accented letters) or `& % ^ ! " < > |` in the path are refused for safety. |
| "a public DCS server advertised a private address" | The server's config points to a LAN address; tell the server admin. |
| "UDP port 34323 is already in use" | Another program uses the auto-connect port. Close it and restart OpenFreq. |

Logs:
- OpenFreq: `%LOCALAPPDATA%\OpenFreq\Logs\openfreq-client-*.log` (lines start with `DCS auto-connect`).
- DCS hook: `Saved Games\DCS...\Logs\OpenFreqDCS.log` (lines start with `autoconnect:`).

## Uninstalling

The Windows uninstaller (or `OpenFreq.Client.exe --dcs-export uninstall`) removes the hook and
`client.json`. Other tools' hooks (DCS-SRS etc.) are not touched.
