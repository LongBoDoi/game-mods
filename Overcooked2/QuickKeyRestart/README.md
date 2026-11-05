# QuickKeyRestart — one-key level restart for Overcooked! 2

One key restarts the level you are playing. It does what **Pause → Restart → yes** does, without
the menu and without the confirmation dialog, which is what you want when a run is already lost
three seconds in.

Default key: **F5**. Only the host needs the mod — a restart is a host decision, and the game takes
everyone in the match with it.

## Using it

Press **F5** while a level is running. There is no confirmation: the level restarts immediately.

Two things deliberately do nothing:

- **Anything that is not a running level.** The main menu, the campaign map, the lobby and the
  loading screens between levels all ignore the key. Restarting during a scene change is what
  strands a match on a black screen, so the mod checks the game is in a live level first.
- **A second press within 3 seconds.** A restart tears the level down and loads it again; a second
  one while that is still in flight can leave the match unrecoverable. Repeats inside the cooldown
  are logged and dropped.

Both cases write a line to the BepInEx console rather than failing silently, so it is clear which
one you hit.

## Settings

`BepInEx\config\com.manhl.overcooked2.quickkeyrestart.cfg`, written on first run:

```ini
[General]

## Phím chơi lại màn đang chơi ngay lập tức. F5 tránh trùng với F6 của LobbyTools và F1/F2 của Host Force Theme.
# Setting type: KeyboardShortcut
# Default value: F5
RestartKey = F5
```

It is a BepInEx `KeyboardShortcut`, so a modifier works too — `RestartKey = R + LeftControl`
restarts on Ctrl+R and nothing else. Edit the file with the game closed.

The default avoids the other mods in this setup: **F6** is [LobbyTools](../LobbyTools/), and
**F1**/**F2** are Host Force Theme.

If you ran an earlier build of this mod, its settings lived under the GUID
`com.yourname.quickkeyrestart`. The installer renames that file to the name above the first time it
runs, so a key you had already chosen is kept.

## Building and installing

```
build.cmd                                      stages QuickKeyRestart.dll in ..\dist\
build.cmd -p:DeployToGame=true                 also copies it into the game (close the game first)
build.cmd -p:GameDir="D:\Games\Overcooked! 2"  game installed somewhere else
```

Then `..\installer\Install.cmd` installs everything staged in `..\dist\` — see
[../README.md](../README.md). The mod ends up at `BepInEx\plugins\QuickKeyRestart\`.

Needs the .NET SDK and the game with BepInEx 5 **x86** installed: the project references the game's
Unity assemblies and `BepInEx.dll` from your install.

## How it works, and testing

The game's pause menu has a non-public `InGamePauseMenu.OnRestartConfirmed()` — the handler its
"restart?" dialog calls when you say yes. The mod calls that method directly, which is why there is
no dialog to click through.

Two details make that safe to do from a keypress:

- `Resources.FindObjectsOfTypeAll` also returns the pause menu's **prefab templates**, which belong
  to no scene and throw when driven. The mod takes the first instance whose `gameObject.scene` is
  valid.
- An active `ServerFlowControllerBase` in the hierarchy is the game's own marker for "a level is
  running"; that is the in-level check above. If a game update removes the type, the check is
  skipped rather than the mod breaking.

Nothing here is compiled against `Assembly-CSharp` — the game is reached by name at runtime. A game
update that renames one of these types disables the mod with a line in the log instead of stopping
BepInEx from loading it.

Because every one of those names is invisible to the compiler, they are asserted against the
installed game instead:

```
powershell -NoProfile -File ..\tests\CheckQuickKeyRestart.ps1
```

It resolves every reference the DLL carries, checks the plugin is still something BepInEx will load
(`[BepInPlugin]`, the GUID the installer migrates to, `Awake`/`Update`), and checks each reflection
target: `InGamePauseMenu` is still a `Component` with a non-static parameterless
`OnRestartConfirmed()`, and `ServerFlowControllerBase` is still a `Component`. A missing
`ServerFlowControllerBase` is reported as a note, matching the mod degrading instead of failing.

## In-match checklist

The reflection is covered by the check script; what it cannot tell you is how a restart feels in a
real match. With the game open:

1. **In a level, alone.** Press F5 mid-level: it restarts at once, with no dialog.
2. **Cooldown.** Press F5 twice quickly: the second press is ignored and the log says so.
3. **Not in a level.** Press F5 on the main menu, in the lobby and on the campaign map: nothing
   happens, and the log says it is not in a level.
4. **During a load.** Press F5 while a level is loading: nothing happens, no black screen.
5. **In online co-op, as host.** Press F5: everyone restarts together.
6. **As a client.** With the mod installed on a non-host machine, F5 does nothing to the match.
7. **Rebound key.** Set `RestartKey = F7`, relaunch: F7 restarts and F5 no longer does.
8. **Alongside LobbyTools.** F6 opens the lobby panel and F5 restarts; neither key triggers the
   other.
