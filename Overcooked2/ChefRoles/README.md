# Chef Roles

Each player picks a role at the start of every level. A role makes some jobs faster and others slower. Players who pick **no role** play the normal game.

## The screen

The screen opens right after the loading screen, and after any intro cutscene, before **Ready… Go!**. The kitchen stays frozen while it's open. It isn't a BepInEx window: it is a copy of the game's own in-game pause menu, with the same panel, dimmed background, header font and button bars. The menu's own logic is removed, and its buttons are replaced with one bar per player on this machine.

| Control | Effect |
|---|---|
| **Left / Right** | change your role (no role → Cook → Server) |
| **Select** | lock in your choice (your bar turns green) |
| **Back** | unlock it to change again |
| **F9** (keyboard) | skip: everyone gets no role |

The level starts when every player is ready, or when the 30-second timer runs out (the current choices are kept). Your last pick is pre-selected on the next level. One player controlling two chefs (single player) gets one bar, and the role applies to both chefs.

## Roles

| Stat | Cook | Server | Rule |
|---|---|---|---|
| Cook | **+50%** | −25% | A pot, pan or tray cooks at the speed of the last chef who put food in it or set it down on the heat |
| Chop | — | −25% | Chopping strokes count 0.75× (or 1.5×, etc.) toward each slice |
| Serve | −25% | **+50%** | When a chef hands in a dish at the pass, that dish's dirty plate comes back faster or slower. Thrown dishes use the normal time |
| Run | — | **+25%** | Walking/running speed (not dash) |

All of these are settings in `BepInEx\config\com.manhl.overcooked2.chefroles.cfg`, under `[Role.Cook]` and `[Role.Server]` (1 = normal, 1.5 = 50% faster, 0.75 = 25% slower, allowed range 0.1–5). The screen's descriptions are built from those numbers. Also in `[General]`: `Enabled`, `SkipRolesKey` and `AutoConfirmSeconds` (0 waits for everyone).

## Online

Joining players need Chef Roles to get the screen. Players without it go straight to Ready… Go! with no role. The host's game waits until every player has finished their intro, so keep `AutoConfirmSeconds` above 0 to stop one idle player holding up the lobby.

| Effect | Host's own chefs | Joining player's chefs (with the mod) |
|---|---|---|
| Run | yes | yes (movement runs on each player's own PC) |
| Cook, chop, serve | yes | **no**: these run on the host, which can't tell what role a remote player picked |

Sending each joining player's role to the host would need a custom network message. That isn't built yet.

## Build

No .NET SDK is needed: `build.cmd` uses the C# compiler that ships with Windows, and compiles against the game's own `mscorlib`.

```
ChefRoles\build.cmd            build and stage in dist\
ChefRoles\build.cmd deploy     also install into the game (close the game first)
powershell -NoProfile -File tests\CheckChefRoles.ps1
```

`CheckChefRoles.ps1` reads the built DLL and checks that every Harmony patch target, patch parameter name and private field it reaches by name exists in the installed game. The compiler can't check those, and a wrong one makes the plugin turn itself off when the game starts.

## In-game test checklist

Not yet verified in the game. Check these on the first run:

1. `LogOutput.log` shows `Đã tải Chef Roles`, with no `Không thể bật Chef Roles` error.
2. Load any campaign or arcade level. The pause-menu-style panel titled **CHOOSE YOUR ROLE** appears after loading, with one bar per player and a hint line with a countdown.
3. Left/Right cycles the role on your bar, Select turns it green, and the level starts once everyone is ready.
4. As **Server**, the chef visibly runs faster. As **Cook**, a pot you filled finishes sooner than one filled by a no-role chef.
5. F9 closes the screen immediately, and nobody gets a role.
6. Pausing and quitting or restarting during and after the screen works normally.

If the panel never shows, the log says why (`Không tìm thấy menu tạm dừng…` means the pause menu wasn't found). The level then starts normally with no roles.
