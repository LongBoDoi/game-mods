# LobbyTools — lobby management for Overcooked! 2

An in-game panel (**F6**, in Vietnamese) for the lobby you host: every remote player's ping, kick,
ban and unban, and force host for public matchmaking. Settings and the ban list are saved
automatically. Only the host needs the mod.

## Using it

Press **F6** to open or close the panel, **Esc** to cancel an open confirmation or close the panel.
Drag the title bar to move it.

| Tab (in the panel) | What it does |
|---|---|
| **Người chơi** (players) | Players from other machines, refreshed every 0.25 s: name, ping in ms, and how many people share that connection. Steam IDs are not shown. |
| **Đuổi** (kick) | Select a player, press **Đuổi**, then **Xác nhận đuổi**. They can rejoin. |
| **Cấm** (ban) | Select a player, press **Cấm**, then **Xác nhận cấm**. The mod saves their Steam ID and kicks them; if that account shows up in a lobby you host again, it is kicked automatically. |
| **Đã cấm** (banned) | The saved list; **Bỏ cấm** lets an account join again. Editable while you are not in a lobby. |
| **Ghép trận** (matchmaking) | **Luôn làm chủ phòng khi ghép trận** (always host when matchmaking): switch it on before searching for a public Arcade/Versus match and you create the lobby and wait for others. Saved immediately, off by default, applies from the next search; the lobby you are in keeps its host. |

The panel works in friend lobbies, Arcade/Versus, during a level and on the campaign map. Every
confirmation re-checks that you are the host, the connection state and the selected player. Players
on your own machine cannot be kicked. Players sharing one remote machine leave together, and the
confirmation says how many people that is.

The ban check also runs with the panel closed and while the game is paused, so a banned account can
appear briefly before the next check. When the game switches connection mode, the mod waits for the
connection to settle before acting again. Invites and manually picking a lobby keep the game's own
flow.

## Ping

Ping is measured after connecting and is **display only**: the mod sets no ping limit, does not
filter matchmaking by connection quality, and never kicks anyone for a poor connection.

The game stores one-way latency in seconds; the mod converts it to round-trip milliseconds. When
both the reliable and unreliable channels have fresh measurements, the panel shows the higher one;
if one channel has not replied for more than 3 seconds, the other channel's fresh measurement wins.
Players sharing a machine share the main connection's figure.

With no valid data the panel shows **Độ trễ: đang đo...** rather than assuming 0 ms. Measurement
keeps running on the active host's remote connections, including across level changes and with the
panel closed. If the other player is loading, has measurement paused on their side or has not
replied, the panel keeps the last figure and adds **(đang cập nhật)** after 3 seconds, then updates
itself when a reply arrives. No new figure can be produced while the remote machine does not process
or answer the probe packets. The mod uses the game's own probe protocol, so the other player needs
no mod. A figure says nothing about packet loss and does not guarantee future connection quality.

## Building and installing

Needs the .NET SDK, the Steam Mono build of the game, and BepInEx 5 **x86**: this build is Unity
2017.4.8 on CLR 2.0 with a 32-bit executable, so the x64 loader used by the Bone's Cafe mod here
will not work. Cross-play between the Steam and Epic builds is untested.

```bat
build.cmd
..\installer\Install.cmd
```

`build.cmd` stages `LobbyTools.dll` in `..\dist\`, which `scripts\release.cmd Overcooked2 <version>`
zips with the installer.

- `build.cmd -p:GameDir="D:\Games\Overcooked! 2"` builds against another install.
- `build.cmd -p:DeployToGame=true` also copies the DLL into the game (close it first).
- `..\installer\Install.cmd` installs BepInEx 5 x86 if it is missing, then the mod; it replaces only
  this mod's DLL and leaves other plugins alone.
- `..\installer\Uninstall.cmd` removes the DLL and keeps the config and ban list.

## Settings and ban list

Config: `BepInEx\config\com.manhl.overcooked2.lobbytools.cfg`.

- `[General] ToggleKey = F6` — the key that opens and closes the panel. F6 stays clear of Host Force
  Theme's F1/F2 and QuickKeyRestart's F5.
- `[Matchmaking] ForceHost = true/false` — the force-host choice, saved as soon as you change it in
  the **Ghép trận** tab.

The panel uses Windows' Segoe UI so Vietnamese diacritics render, restores the cursor when it
closes, and blocks clicks from reaching the game menu behind it (other mods still see their own
hotkeys).

Ban list: `BepInEx\config\com.manhl.overcooked2.lobbytools.bans.tsv`. Steam IDs are stored as digit
strings, names as UTF-8/base64, and the ban time in UTC, so renaming an account does not escape a
ban. Writes replace the file atomically; if a write fails, the in-memory list is unchanged. If the
file is corrupt or unreadable the mod leaves it alone and locks ban editing — check
`BepInEx\LogOutput.log`, fix the file and restart the game. Host Force Theme keeps its own separate
list.

## How it works, and testing

This build's `UIPlayerMenuBehaviour.KickUser` has an empty body, so the mod uses
`ServerUserSystem.RemoveUser`: secondary players first, then the primary one, which lets the game
clean up the chefs, the users and the disconnect itself. The moderation component survives level
changes, reads Steam IDs from the real connections, and reuses the same kick path when it meets a
banned account.

Force host patches `ClientLobbyFlowController.TryJoinGame` to call the game's own `HostGame`,
keeping the mode, the lobby visibility and the game's completion/error handling. The patch runs
after Host Force Theme and respects `__runOriginal`, so no lobby is created twice and joining a
specific lobby still works. The mod does not depend on Host Force Theme.

Ping data comes from the host's connection table keyed by player session ID, so figures are never
attributed to the wrong player. To keep measuring, `BaseConnection.GetLatencyTestPaused` is patched
to return `false` only for remote connections present in the current host's connection table. Probe
scheduling and reply handling stay on the game's own path, the original pause flag is never changed,
and unpatching restores stock behaviour. When the connection mode changes or you are no longer the
host, the patch does not override the pause behaviour.

Offline tests:

```bat
dotnet run --project ..\tests\LobbyTools.Tests.csproj
powershell -NoProfile -File ..\tests\CheckRuntime.ps1
```

They cover saving the ban list, host permission, re-kicking a banned account, the hosting flow, the
ping unit conversion, missing and invalid data, measurement surviving a level load, patch scope,
stale figures while waiting for a reply, preferring the channel with a fresh reply, the Vietnamese
responses, and that a high ping never kicks anyone. `CheckRuntime.ps1` checks the built DLL's API
calls against the game's real assemblies. Display, disconnects and Steam matchmaking need an online
test.

The ban-file check uses `IOException` because the game's runtime has no
`System.IO.InvalidDataException`.

## In-match checklist

1. Close the game, install the mod, start it and check `BepInEx\LogOutput.log` for load or patch
   errors.
2. Host a friend lobby and invite another Steam account. Press F6 and check the Vietnamese text
   renders with its diacritics, plus the player names, shared-connection counts and ping. No Steam
   IDs in the panel.
3. Kick and confirm: the other player leaves, you stay host, and they can rejoin.
4. Ban and confirm: the account is saved and kicked. Close the panel, then invite them again or
   re-matchmake — they must be kicked automatically.
5. Restart the game and host again: the ban still holds. Unban, and check they can stay.
6. Enter a level or the campaign map: the list and kick/ban still work. Join someone else's lobby:
   no kicking, banning or automatic kicks there.
7. Clicking the panel does not trigger the menu behind it, and closing it restores the cursor.
8. Test a remote group of two people on one machine: kicking that group leaves other connections
   alone.
9. Switch force host on and search for a public Arcade and a public Versus match: you must be the
   host. Restart to confirm the choice was saved. Switch it off and normal matchmaking returns.
10. With Host Force Theme also forcing host, only one hosting request is made, and picking a
    specific lobby still joins that lobby.
11. Keep a high-ping player in the lobby for at least a minute, panel closed: they must stay unless
    banned or kicked by hand.
12. Load a level together: the panel keeps its ping figures instead of showing measurement paused.
    While the other player has not answered you see **(đang cập nhật)**; once they do, the figures
    update again.
