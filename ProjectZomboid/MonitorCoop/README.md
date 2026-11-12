# MonitorCoop — a monitor each for local split-screen in Project Zomboid

The game splits one window between two to four local players. This mod stretches that window over
your monitors instead and gives each player a whole one, in the order the monitors are arranged on
your desktop. Only the computer running the game needs the mod.

Each player already has their own inventory, health panel, map and menus in the game's own
split-screen; this mod does not touch any of that, so opening the map on one screen still leaves
the other players alone. What it changes is where each player's view and HUD go: a whole monitor
instead of half a window.

## Using it

Install it (see below) and start the game through `<game>\MonitorCoop\Play.cmd` — the installer can
put that on your desktop, and it can also go in Steam's launch options so the Play button uses it:

```
"E:\...\ProjectZomboid\MonitorCoop\Play.cmd" %command%
```

That launcher is the whole of the installation: it starts the game exactly as the game's own
launcher would, with the mod loaded as well (see *How it works*). Starting the game any other way
gives you the game without the mod, because no game file is touched.

Then begin local split-screen the way you normally would — press a button on a second controller in
game. The window spreads itself over the monitors as the second player joins, and goes back to the
size, position and window mode it had before when everyone but player one leaves.

| | |
|---|---|
| **F7** | Turns the monitor layout off and on without restarting. Off means the game's own split-screen in one window. |
| `MonitorCoop.ini` | The settings, in your `Zomboid` folder next to the game's own `options.ini`. The mod re-reads it a second or two after you save it, so changes do not need a restart. |
| `console.txt` | Also in the `Zomboid` folder: the mod logs the monitors it found, the layout it applied and anything that went wrong, every line tagged `[MonitorCoop]`. |

**Who sits where** is yours to decide: `PlayerMonitors` in the settings is the monitor for player
one, then player two, and so on — `PlayerMonitors=2,1` swaps the two of them. Saving the file moves
the players within a second or two, without leaving the game. Left empty, the mod works it out from
the desktop arrangement: player one on the primary monitor, the others left to right.

Players beyond the monitors you have share one, split the way the game would split a whole screen:
with two monitors, three players give two side by side on the first monitor and one on the second,
and four players give two on each. `PlayerMonitors` can say that too (`1,1,2` puts players one and
two together on monitor 1 and gives player three monitor 2 to themselves).

Monitors of different sizes, monitors that are not aligned, and a monitor to the left of the primary
one (which Windows gives negative coordinates) all work: each player gets their own monitor's full
resolution. A gap in the arrangement is part of the window but no monitor shows it, so nobody sees
it.

**Keyboard and mouse still belong to player one** — that is the game's own rule, so every other
local player needs a controller. The mod only decides where each player's screen goes; it adds no
player slots and changes nothing about saves or how players join. Player one's mouse pointer is kept
on player one's monitor so it cannot wander onto someone else's screen and click their windows;
alt-tab frees it, and `ConfineMouseToPlayerOne=false` turns the holding off.

## Settings

`<user>\Zomboid\MonitorCoop.ini`, written the first time the mod runs:

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | `false` leaves local split-screen exactly as the game ships it. |
| `ToggleKey` | `F7` | Any key name the game knows (`F1`..`F12`, `A`..`Z`, `0`..`9`, `HOME`, `END`...), or `None`. |
| `PlayerMonitors` | *(empty)* | **Which monitor each player gets**, in player order: `2,1` puts player one on monitor 2 and player two on monitor 1. The numbers are the ones the mod lists in `console.txt` (1 is the primary monitor). Players given the same monitor share it, split the way the game splits a whole screen (`1,1,2` = players one and two on monitor 1, player three alone on monitor 2), and a player the setting does not name goes where there is most room. Empty works it out from the desktop arrangement. |
| `PlayerOneOnPrimary` | `true` | Only used when `PlayerMonitors` is empty: player one gets the primary monitor and the others follow left to right. `false` seats everyone purely left to right, top row first. |
| `RestoreWindowOnExit` | `true` | Put the window back when the extra players leave. `false` leaves it spanning. |
| `FullMonitorView` | `true` | Show each player as much of the world as they would see playing on that monitor alone. The game otherwise keeps the view it had at the window size the session started at, so a bigger window shows the same world more sharply rather than more of it — which would leave every player looking at about as much world as the half window showed them. `false` keeps the game's own behaviour, which is lighter on the graphics card. |
| `ConfineMouseToPlayerOne` | `true` | Hold the mouse pointer inside player one's monitor while the game has focus. |
| `PretendPlayers` | `0` | Act as if this many players were in split-screen, to try a seating arrangement on your own: the window spans and player one lands on their monitor, with the other screens left empty. Back to `0` to play. |
| `ShowPlayerAreas` | `false` | Draw a frame around each player's area in their own colour — player one red, two green, three blue, four yellow, with that many marks inside the top left corner. The frame is exactly the rectangle the mod handed the game, so a screenshot says whether the mod and the monitors agree, and whether what is drawn inside sits where the frame is. |
| `PlayerRects` | *(empty)* | The escape hatch: exact rectangles in desktop pixels, one per player, e.g. `0,0,2560,1440; -1920,356,1920,1080`. Use it if the mod reads your desktop wrongly. |

## What it does not change

- **No game file is patched on disk, or changed at all.** The mod is a Java agent: it rewrites the
  few methods it needs *as they are loaded*, in memory, in a game started through its own launcher.
  Everything it installs is three files in `<game>\MonitorCoop\`; the game's own
  `projectzomboid.jar` and launcher files are never written to, so a game update cannot leave the
  mod half-installed and uninstalling is deleting that folder.
- **Nothing about multiplayer itself**: no extra player slots, no change to saves, servers or how
  players join. Four local players is still the limit.
- **The game's own split-screen code does the drawing.** The mod hands the game a different
  rectangle per player; it draws nothing itself, so the HUD, menus, zoom and UI scale stay the
  game's own.

## Building and installing

Needs the game (the mod is compiled against its `projectzomboid.jar`, the way this repo's C# mods
are compiled against their game's assemblies) and a JDK as new as the game's own Java — `build.cmd`
uses `JAVA_HOME`, a `javac` on the path or `<repo>\.tools\jdk`, and downloads one there if it finds
none.

```bat
build.cmd
..\installer\Install.cmd
```

`build.cmd` stages `MonitorCoop.jar` with `Play.cmd` and `Play.ps1` in `..\dist\MonitorCoop\`, which
`scripts\release.cmd ProjectZomboid <version>` zips with the installer.

- `build.cmd -GameDir "D:\Games\ProjectZomboid"` builds against another install.
- `build.cmd install` also installs it (close the game first).
- `build.cmd clean` rebuilds everything.
- `..\installer\Install.cmd -Shortcut` also puts the launcher on the desktop.
- `..\installer\Uninstall.cmd` removes the folder and the shortcut.

A game update needs nothing from you: the launcher reads the game's own settings every time, so new
JVM arguments or a new main class are picked up by themselves. What an update can change is the code
the patches aim at, and the mod checks for exactly that: if a method it needs has moved, it leaves
the game alone, says so in the log, and local split-screen behaves as the game ships it. Running
`..\tests\run.cmd` after an update reports the same thing without starting the game.

## How it works

### The rectangles

The game works out where a split-screen player's view goes in one place: six one-line methods on
`IsoCamera` (`getScreenLeft`, `getScreenTop`, `getScreenWidth`, `getScreenHeight`,
`getOffscreenLeft`, `getOffscreenTop`) that cut the screen into halves and quarters from
`IsoPlayer.numPlayers`. Everything downstream reads the rectangle from there — the camera, the
offscreen buffer and the blit back to the screen, the cursor, the reticle, the sound, the fade — and
so does every Lua window in the HUD, because `getPlayerScreenLeft` and friends are those same
methods. Replacing those six bodies with a rectangle per player is what gives each player a whole
monitor; the rest of the patches are the handful of places that work the same split out again with
their own arithmetic:

- `Core.DoStartFrameStuffInternal` and `Core.DoStartFrameNoZoom` set up the OpenGL viewport for a
  player's frame by halving the screen themselves. Their arguments are handed to the mod at the top
  of the method, and the projection, the viewport and the clipping box are redirected: the viewport
  becomes the player's rectangle (flipped for the frames drawn straight to the window, as the game
  flips them), and the projection is scaled by how much bigger that rectangle is, which keeps
  full-screen effects and the game's own render-to-texture work in proportion.
- `UIManager.resize` places the game's own moodles, clock and speed controls against the edges of
  each player's half of the window. A call at the end of it moves them onto each player's monitor
  (one clock for everybody, so it goes on player one's).
- The dead body and world item atlases clip their drawing to the player's quarter of the offscreen
  buffer. Both also clip to a slot in their own texture, which has nothing to do with the split, so
  every clipping box in them goes through one hook that recognises the split one by its size and
  passes the others straight through.
- The view cone blur tells its shader how big the player's part of the offscreen buffer is, worked
  out from the buffer and the split instead of from `IsoCamera`. Every value it sends the shader
  goes through one hook, which replaces that one and leaves the rest.

Every patch is the same shape: the mod's hook answers exactly what the game would have worked out
for itself while no layout is live, so single player, the main menu and the mod turned off are the
game as it ships. Nothing is guessed, either — each method and call is named with its exact
signature and counted, and if a game update moves one, the class is left alone and the mod turns
itself off with a line in the log instead of drawing nonsense.

### Why it has its own launcher

A Java agent is loaded by the virtual machine at startup, and the library that does the
instrumenting (`instrument.dll`) sits in the game's `jre64\bin` with its own dependencies beside it.
`ProjectZomboid64.exe` starts the virtual machine itself, from the game folder, so that folder is
not on the DLL search path: ask it for an agent and the machine stops with *"Could not find agent
library instrument on the library path"* before the game starts at all. Running the game through
`jre64\bin\javaw.exe` — which is how the game's own `ProjectZomboid64.bat` starts it — puts that
folder on the path, and the agent loads.

So `Play.cmd` reads `ProjectZomboid64.json`, the file the game's own launcher reads, and starts the
game with the same main class, classpath and JVM arguments (including the ones the game picks for
the running version of Windows), plus the agent. Arguments are passed on, so `-debug` works, and the
command Steam hands a launch-option wrapper is dropped. Nothing in the game folder is written to,
which is why a game update cannot half-break the install and why starting the game the usual way
still gives you the game without the mod.

### The window

Spanning the window is GLFW, reached the way the game reaches it and on the thread that owns the
OpenGL context, because that is where the game makes its own window changes: the border comes off,
`glfwSetWindowMonitor` leaves fullscreen and puts the window over the bounding box of the monitors
in use. Everything downstream follows by itself — GLFW reports the new size, and the game copies
that into its own screen size at the top of every frame, which rebuilds the offscreen buffer. The
mod only hands the layout to the game once the window really is the size that layout was made for,
because a rectangle reaching past the window would put a player's view where nobody can see it.

One more thing comes with the bigger window: the game scales each player's view by the window size
the session started at over the window size now, so growing a window shows the same amount of world
more sharply instead of showing more of it. Left alone, that would hand every player a monitor and
still show them the half window's worth of street. While the layout is on, the mod tells the game
the spanned window is the size it started at, which gives each player the view they would have
playing on that monitor by themselves, and puts the old value back afterwards
(`FullMonitorView=false` keeps the game's own behaviour).

### The once-a-frame check

A minimised window is left alone: Windows minimises a fullscreen game that loses focus, and a
window that is not on screen reports a position far off the desktop, so measuring or moving it then
is meaningless. The layout in use stays as it is and the mod gets a fresh go once the window is
back.

Once a frame (from `GameWindow.frameStep`) the mod compares the player count against the layout in
use; every half second it looks at the settings file again, and every couple of seconds it asks GLFW
about the monitors (that one costs a hop to the render thread, so it is kept rare). If something else
moves the window it is nudged back, but only ten times, after which the mod leaves it alone rather
than fight over it.

## Testing

```bat
..\tests\run.cmd
```

The offline checks cover the layout maths with no game involved — how players are spread over
monitors, the seating order (including monitors at different heights, a monitor at negative
coordinates and a 2x2 grid), the window that has to be spanned, mixed resolutions, players sharing
a monitor, where the empty player slot is parked so the black the game paints for it lands on
nobody, and the cursor clamp — and then run the real patches over the installed
`projectzomboid.jar`: every method and call site they need is still there, what comes out of each
patch is a class file the runtime accepts, and the patched `IsoCamera` is loaded for real and asked
where each player's view goes.

What none of that can check is the picture on the monitors. That needs a real session with a
controller for every player but the first — `PretendPlayers=2` gets you most of the way on your
own, and `ShowPlayerAreas=true` outlines what the mod thinks each player's screen is, which is the
quickest way to tell a layout problem from a rendering one:

1. Start the game with one monitor only: nothing should change, and `console.txt` should report one
   monitor and no spanning.
2. With two or more monitors, load a save and add a second player on a controller. The window should
   cover both monitors with one player on each, no border, nothing cut off, and `console.txt` should
   name the layout it applied.
3. Check both players' HUD corners, the moodles, the clock, and that inventory and crafting windows
   open on their own player's monitor.
4. Open the map on one screen: the other player's screen should carry on as it was.
5. Move player one's mouse: the cursor should sit under the pointer, reach all four corners of
   player one's own monitor and stop at its edges instead of crossing onto the other screen.
   Clicking near those edges should hit what the cursor is on, and alt-tab should let the pointer
   leave.
6. Check the view cone shading and the lighting on both screens, and that items and bodies on the
   ground are not clipped or doubled.
7. Press **F7**: back to one window and the game's own split-screen. Press it again: back to a
   monitor each.
8. Have player two leave, or go back to the main menu: the window should return to the size,
   position and window mode it started with.
9. Repeat from a fullscreen and a borderless start, and confirm the mode comes back.
10. Add a third and a fourth player: the extra players should share a monitor the way the game
    splits one screen.
11. Try it with the monitors at different resolutions, with the second monitor to the left of the
    primary one, and stacked vertically.
12. Change `PlayerMonitors` in `MonitorCoop.ini` (e.g. `2,1`), save it, and confirm the players
    swap monitors within a second or two without leaving the game. `PlayerMonitors=1,1` should put
    both players on one monitor, split down the middle, with the other monitor left out of the
    window altogether.
13. Unplug or re-arrange a monitor while playing: within a couple of seconds the layout should be
    rebuilt for what is left.
