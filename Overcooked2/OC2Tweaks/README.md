# OC2Tweaks — lobby management and quick restart for Overcooked! 2

An in-game panel (**F6**, in Vietnamese) for the lobby you host: every remote player's ping, kick,
ban and unban, force host for public matchmaking, and a one-key level restart you can switch off or
rebind without leaving the game. Settings and the ban list are saved automatically. Only the host
needs the mod.

This was two plugins until 2.0.0 — **LobbyTools** and **QuickKeyRestart**. One plugin means one
config file, one place to see both hotkeys, and somewhere in game to turn the restart key off. The
installer carries the old settings over; see [Upgrading](#upgrading-from-lobbytools--quickkeyrestart).

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
| **Phím tắt** (hotkeys) | Switch the quick restart on or off, rebind either key, and restart the current level from a button. See [Quick restart](#quick-restart). |

The panel works in friend lobbies, Arcade/Versus, during a level and on the campaign map. Every
confirmation re-checks that you are the host, the connection state and the selected player. Players
on your own machine cannot be kicked. Players sharing one remote machine leave together, and the
confirmation says how many people that is.

The ban check also runs with the panel closed and while the game is paused, so a banned account can
appear briefly before the next check. When the game switches connection mode, the mod waits for the
connection to settle before acting again. Invites and manually picking a lobby keep the game's own
flow.

## Quick restart

Press **F5** while a level is running and it restarts immediately — the same thing **Tạm dừng →
Chơi lại → Đồng ý** does, without the menu or the confirmation dialog, which is what you want when
a run is already lost three seconds in.

Four things deliberately do nothing:

- **Anything that is not a running level.** The main menu, the campaign map, the lobby and the
  loading screens between levels all ignore the key. Restarting during a scene change is what
  strands a match on a black screen, so the mod checks the game is in a live level first.
- **A second press within 3 seconds.** A restart tears the level down and loads it again; a second
  one while that is still in flight can leave the match unrecoverable.
- **The key while the panel is open.** The panel is a menu, and a restart key that also fires while
  you are clicking around in it restarts levels by accident. Close the panel, then press the key.
- **The key while the panel is waiting for a new binding.** Every key belongs to the rebind then.

The first two write a line to `BepInEx\LogOutput.log` rather than failing silently, so it is clear
which one you hit. The **Phím tắt** tab says the same thing live: whether you are in a level, and
how long is left on the cooldown.

A restart is a host decision and the game takes everyone in the match with it, so only the host
needs this.

### Rebinding, in game

In **Phím tắt**, **Đổi phím** next to either key waits for a keypress and saves it straight to the
config file:

- The panel key is a plain key.
- The restart key is a BepInEx `KeyboardShortcut`, so holding **Ctrl**, **Shift** or **Alt** while
  you press makes it a combination — `R + LeftControl` restarts on Ctrl+R and nothing else.

Modifiers on their own are not a binding, so the panel keeps waiting until you press a real key.
**Esc** cancels. If both keys end up on the same plain key, the panel says so instead of leaving you
with a key that does two things.

Unticking **Chơi lại màn ngay bằng một phím** leaves the binding saved but stops the key working, so
turning it back on does not mean rebinding it.

## Building and installing

Needs the .NET SDK, the Steam Mono build of the game, and BepInEx 5 **x86**: this build is Unity
2017.4.8 on CLR 2.0 with a 32-bit executable, so the x64 loader used by the Bone's Cafe mod here
will not work. Cross-play between the Steam and Epic builds is untested.

```bat
build.cmd
..\installer\Install.cmd
```

`build.cmd` stages `OC2Tweaks.dll` in `..\dist\`, which `scripts\release.cmd Overcooked2 <version>`
zips with the installer.

- `build.cmd -p:GameDir="D:\Games\Overcooked! 2"` builds against another install.
- `build.cmd -p:DeployToGame=true` also copies the DLL into the game (close it first).
- `..\installer\Install.cmd` installs BepInEx 5 x86 if it is missing, then the mod; it replaces only
  this mod's DLL and leaves other plugins alone.
- `..\installer\Uninstall.cmd` removes the DLL and keeps the config and ban list.

## Settings and ban list

Config: `BepInEx\config\com.manhl.overcooked2.tweaks.cfg`.

- `[General] ToggleKey = F6` — the key that opens and closes the panel. F6 stays clear of Host Force
  Theme's F1/F2.
- `[Matchmaking] ForceHost = true/false` — the force-host choice, saved as soon as you change it in
  the **Ghép trận** tab.
- `[QuickRestart] Enabled = true/false` — the **Phím tắt** tick box.
- `[QuickRestart] RestartKey = F5` — a `KeyboardShortcut`, so `R + LeftControl` works. Edit the file
  with the game closed, or use **Đổi phím** and leave the file alone.

The panel uses Windows' Segoe UI so Vietnamese diacritics render, restores the cursor when it
closes, and blocks clicks from reaching the game menu behind it (other mods still see their own
hotkeys).

Ban list: `BepInEx\config\com.manhl.overcooked2.tweaks.bans.tsv`. Steam IDs are stored as digit
strings, names as UTF-8/base64, and the ban time in UTC, so renaming an account does not escape a
ban. Writes replace the file atomically; if a write fails, the in-memory list is unchanged. If the
file is corrupt or unreadable the mod leaves it alone and locks ban editing — check
`BepInEx\LogOutput.log`, fix the file and restart the game. Host Force Theme keeps its own separate
list.

## Upgrading from LobbyTools + QuickKeyRestart

Choose **OC2Tweaks** in `installer\Install.cmd` and it does all of this for you:

| What you had | Where it ends up |
|---|---|
| `com.manhl.overcooked2.lobbytools.cfg` | renamed to `…tweaks.cfg`, keeping your panel key and force-host choice |
| `…lobbytools.bans.tsv` | renamed to `…tweaks.bans.tsv`, byte for byte |
| `RestartKey` in `…quickkeyrestart.cfg` (or the pre-repo `com.yourname.quickkeyrestart.cfg`) | written into the new config's `[QuickRestart]` section |
| `BepInEx\plugins\LobbyTools\`, `BepInEx\plugins\QuickKeyRestart\` | deleted |

Those last two matter: left in place, the superseded plugins keep loading beside OC2Tweaks, which
means two F6 panels, two handlers on the restart key, and two mods writing one ban file. Nothing
else in `BepInEx\plugins` is touched, and the old config files are left on disk rather than deleted.

Running the installer again does not migrate a second time. `tests\CheckMerge.ps1` asserts all of
the above against a throwaway game folder.

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

Quick restart invokes the game's own `InGamePauseMenu.OnRestartConfirmed()` — the handler the
"restart?" dialog calls when you say yes — which is why there is no dialog to click through. Two
details make that safe from a keypress:

- `Resources.FindObjectsOfTypeAll` also returns the pause menu's **prefab templates**, which belong
  to no scene and throw when driven. The mod takes the first instance whose `gameObject.scene` is
  valid.
- An active `ServerFlowControllerBase` in the hierarchy is the game's own marker for "a level is
  running"; that is the in-level check above.

`OnRestartConfirmed` is non-public, so it is reached through `AccessTools` rather than a direct
call. If a game update renames it, that one tweak reports itself off in the **Phím tắt** tab and in
the log, and the rest of the panel keeps working.

Offline tests:

```bat
dotnet run --project ..\tests\OC2Tweaks.Tests.csproj
powershell -NoProfile -File ..\tests\CheckRuntime.ps1
powershell -NoProfile -File ..\tests\CheckMerge.ps1
```

They cover saving the ban list, host permission, re-kicking a banned account, the hosting flow, the
ping unit conversion, missing and invalid data, measurement surviving a level load, patch scope,
stale figures while waiting for a reply, preferring the channel with a fresh reply, the Vietnamese
responses, and that a high ping never kicks anyone. `CheckRuntime.ps1` checks the built DLL's API
calls against the game's real assemblies, including the names reached by reflection that no compiler
sees: the force-host methods, the connection telemetry fields, the ping patch target, and
`InGamePauseMenu.OnRestartConfirmed()`. `CheckMerge.ps1` covers the upgrade above. Display,
disconnects and Steam matchmaking need an online test.

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

Quick restart, on top of that:

13. **In a level, alone.** Press F5 mid-level: it restarts at once, with no dialog.
14. **Cooldown.** Press F5 twice quickly: the second press is ignored and the log says so.
15. **Not in a level.** Press F5 on the main menu, in the lobby and on the campaign map: nothing
    happens, and the log says it is not in a level.
16. **During a load.** Press F5 while a level is loading: nothing happens, no black screen.
17. **Panel open.** Press F5 with F6 open: nothing happens. Close it and F5 works again.
18. **In online co-op, as host.** Press F5: everyone restarts together.
19. **As a client.** With the mod installed on a non-host machine, F5 does nothing to the match.
20. **Rebound.** In **Phím tắt**, **Đổi phím** → F7. F7 restarts, F5 no longer does, and the new key
    is in the config after a restart of the game. Then try `Ctrl` held + `R`: only Ctrl+R restarts.
21. **Switched off.** Untick the box: the key does nothing, and ticking it back on restores the key
    you had chosen rather than F5.
22. **Rebinding the panel key.** **Đổi phím** on the top row → F9: F9 opens and closes the panel,
    F6 no longer does, and the panel does not reopen on its own the moment you bind it.
23. **Both keys the same.** Bind both to F8: the panel warns instead of silently doing two things.
