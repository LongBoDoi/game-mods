# MonitorCoop — a monitor each for local co-op in Stardew Valley

The game's local co-op splits one window between two to four players. This mod stretches that window
over your monitors instead and gives each player a whole one, in the order the monitors are arranged
on your desktop. Only the computer running the game needs the mod.

## Using it

Install SMAPI and the mod, start local co-op the way you normally would (the co-op menu, or press
Start on another controller in a save that already has cabins), and the window spreads itself over
the monitors as soon as the second player joins. When everyone but player one leaves, or you go back
to the title, the window goes back to the size, position and fullscreen mode it had before.

| | |
|---|---|
| **F7** | Turns the monitor layout off and on without restarting. Off means the game's own split-screen in one window. |
| `monitorcoop` | A SMAPI console command: lists the monitors the game can see with their numbers, how many players are in split-screen, and what the mod is currently doing. |
| `monitorcoop reload` | Re-reads `config.json` and applies it straight away. |

Players beyond the monitors you have share one, split the way the game would split a whole screen:
with two monitors, three players give two side by side on the first monitor and one on the second,
and four players give two on each.

Monitors of different sizes, monitors that are not aligned, and a monitor to the left of the primary
one (which Windows gives negative coordinates) all work: each player gets their own monitor's full
resolution. A gap in the arrangement is part of the window but no monitor shows it, so nobody sees it.

Every player's cursor belongs to their own monitor: it reaches all of that monitor and stops at its
edges, so player one's mouse pointer cannot wander onto someone else's screen. Alt-tab frees the
pointer when you need the other monitors for something else, and `ConfinePointerToOwnScreen` turns
the holding off if you would rather it roam (the cursor then waits at the edge of the player's own
screen while the pointer is away).

## Settings

`Mods\MonitorCoop\config.json`, written the first time the mod runs:

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | `false` leaves local co-op exactly as the game ships it. |
| `ToggleKey` | `F7` | Any SMAPI key binding, e.g. `"LeftControl + F7"`. |
| `Displays` | `[]` | The monitors to use, in seating order, e.g. `[0, 2]`. Empty decides from the desktop layout. The numbers are the ones `monitorcoop` lists. |
| `PlayerOneOnPrimary` | `true` | Player one gets the primary monitor and the others follow left to right. `false` seats everyone purely left to right, top row first. |
| `ZoomModifier` | `1` | How far the world is zoomed out per player. The game halves it in split-screen because each player normally has part of a window; `1` shows as much of the farm as playing alone does, `0.5` is the vanilla split-screen view. Each player's own zoom and UI scale options still apply on top. |
| `RestoreWindowOnExit` | `true` | Put the window back when the extra players leave. `false` leaves it spanning. |
| `ConfinePointerToOwnScreen` | `true` | Hold the mouse pointer inside player one's monitor while the game has focus. Either way each cursor is measured from its own monitor and reaches all of it; `false` only lets the pointer itself leave. |

## What it does not change

- **Keyboard and mouse still belong to player one.** That is the game's own rule: every other local
  player needs a controller. This mod only decides where each player's screen goes, and keeps each
  player's cursor on it.
- **Nothing about multiplayer itself**: no extra player slots, no change to cabins, saves, or how
  players join. Four players is still the limit.
- **The game's split-screen code does the drawing.** The mod hands the game a different rectangle per
  player; it does not draw anything itself, so the HUD, menus, zoom and UI scale stay the game's own.

## Building and installing

Needs the .NET SDK and the game with SMAPI installed (the project references the game's assemblies
and SMAPI's Harmony).

```bat
build.cmd
..\installer\Install.cmd
```

`build.cmd` stages `MonitorCoop.dll` and `manifest.json` in `..\dist\MonitorCoop\`, which
`scripts\release.cmd StardewValley <version>` zips with the installer.

- `build.cmd -p:GameDir="D:\Games\Stardew Valley"` builds against another install.
- `build.cmd -p:DeployToGame=true` also copies the mod into the game's `Mods` folder (close it first).
- `..\installer\Install.cmd` installs SMAPI if it is missing, then the mod.
- `..\installer\Uninstall.cmd` removes the mod's files and keeps `config.json`.

## How it works

`Game1.SetWindowSize` builds a list of (x, y, width, height) fractions of the window — one per
split-screen player — picks the entry for its own instance, and sizes that player's render targets,
viewport and menus from it. A Harmony transpiler replaces that single lookup with the player's
monitor, so everything downstream stays on the game's own path. The fractions are nudged a quarter of
a pixel inwards because the game floors the origin and rounds the size up: both roundings then land
on exactly the rectangle asked for. A postfix sets the window's position to the exact pixel, which is
only used to blit the finished screen.

The same hook also sets `Game1.zoomModifier`, which the game halves for split-screen; it runs before
the render targets are sized, which is the only point where that value still matters.

Cursors need the matching correction. The game reports the real mouse in window pixels, and in vanilla
split-screen player one's screen always starts at the window's corner, so the two are the same thing.
Spanning the monitors breaks that: a player one whose monitor sits a screen into the window had their
cursor shifted by the other monitor's width and bounded by its size. The game reads the mouse only
through `InputState.GetMouseState` and moves it only through `InputState.SetMousePosition`, so a
postfix on the first measures the position from the player's own screen and bounds it there, and a
prefix on the second turns a position back into window pixels — the two halves of the same change.
SMAPI replaces the game's input handler but builds its own cached state from `base.GetMouseState()`,
so the postfix covers the game, SMAPI's cursor API and other mods alike; `CheckRuntime.ps1` asserts
that call still exists. Player one's pointer is pushed back to the edge of their own monitor while the
game has focus, which is what keeps it off the other players' screens; losing focus (alt-tab) releases
it. The cursors the game simulates for controller players are already measured from their own screen,
so those are only bounded, never moved.

Spanning the window is `GameWindow.IsBorderless`, `GraphicsDeviceManager.PreferredBackBuffer*` and
`GameWindow.Position`, in that order: applying graphics changes re-centres the window on one monitor,
so the position has to come last. The monitors themselves come from `GameWindow.GetDisplayBounds`,
which the game's MonoGame build exposes, so the mod reads the same display layout and coordinates the
game uses and needs no Windows API of its own. A fullscreen window belongs to one monitor, so the mod
switches the game to windowed through its own `Options.setWindowedOption` first and restores the mode
afterwards (that option is also what the game saves as your startup window mode).

The mod only applies a layout once the window really is the size that layout was made for, and it
checks every half second whether the monitors or the player count changed. If something else moves
the window it is nudged back, but only ten times, after which the mod leaves it alone rather than
fight over it.

## Testing

```bat
dotnet run --project ..\tests\StardewValley.Tests.csproj
powershell -NoProfile -File ..\tests\CheckRuntime.ps1
```

The first runs the offline checks for both of this game's mods. The layout half covers this one with no
game involved: how players are spread over monitors, the seating order (including monitors at different
heights and a 2x2 grid), the window that has to be spanned, negative coordinates, mixed resolutions,
players sharing a monitor, and that the fractions handed to the game survive its rounding as the exact
rectangle intended.

`CheckRuntime.ps1` checks the built DLL against the installed game and SMAPI: every type, method and
field it uses resolves, and `Game1.SetWindowSize` still has the shape the transpiler needs — one
split-screen slot lookup stored into a local, with the zoom set before it and the render targets
created after it. If a game update changes that shape, the mod logs an error and leaves the game's own
split-screen alone instead of guessing.

What neither can check is the picture on the monitors. That needs a real session:

1. Start the game through SMAPI with one monitor only: nothing should change, and
   `monitorcoop` should report one monitor and no spanning.
2. With two or more monitors, host a save and add a second player with a controller. The window
   should cover both monitors with one player on each, no border, and nothing cut off.
3. Check both players' HUD corners, the zoom level and that menus open in the middle of their own
   monitor.
4. Move player one's mouse: the cursor should sit under the pointer, reach all four corners of player
   one's own monitor, and stop at its edges instead of crossing to the other player's screen. Clicking
   near those edges should hit what the cursor is on. Check the same for a controller player's cursor
   on their own monitor, and that alt-tab lets the pointer leave.
5. Press **F7**: back to one window and the game's own split-screen. Press it again: back to a
   monitor each.
6. Have player two leave, or go back to the title: the window should return to the size, position and
   fullscreen mode it started with.
7. Repeat from a fullscreen and a borderless-fullscreen start, and confirm the mode comes back.
8. Add a third and a fourth player: the extra players should share a monitor the way the game splits
   one screen.
9. Try it with the monitors at different resolutions, with the second monitor to the left of the
   primary one, and stacked vertically.
10. Change `Displays` in `config.json`, run `monitorcoop reload`, and confirm the players move.
11. Unplug or re-arrange a monitor while playing: within about half a second the layout should be
    rebuilt for what is left.
