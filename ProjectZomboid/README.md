# Project Zomboid mods

| Mod | What it does |
|---|---|
| [MonitorCoop](MonitorCoop/) | Local split-screen on a monitor each instead of one split window: the game's window stretches over your monitors and every player gets a whole one, in the order the monitors are arranged. Different resolutions, uneven arrangements and more players than monitors all handled |

**Install:** download `ProjectZomboid-v*.zip` from the release, extract it anywhere, and double-click
`Install.cmd` with the game closed (add `-Shortcut` for a desktop shortcut). It finds the game
through Steam and copies three files into `<game>\MonitorCoop\`. Nothing else in the game folder is
touched, so a game update never breaks the install; `Uninstall.cmd` removes the folder again.

**Start the game through `<game>\MonitorCoop\Play.cmd`** — that is what loads the mod. It starts the
game exactly as the game's own launcher would (it reads the game's own `ProjectZomboid64.json`), so
`-debug` and the rest still work. To keep using Steam's Play button, put this in the game's launch
options:

```
"<game>\MonitorCoop\Play.cmd" %command%
```

Starting the game any other way gives you the game without the mod.

## A monitor each

**Using it:** start the game through the launcher and begin local split-screen the way you normally
do: press a button on a second controller in game. The window spreads over your monitors as the
second player joins, and goes back to its old size, position and window mode when they leave.

- **F7** turns the monitor layout off and on; off is the game's own split-screen in one window.
- **You choose who sits where**: `PlayerMonitors` in the settings is the monitor for player one,
  then player two, and so on, so `PlayerMonitors=2,1` swaps them. Saving the file moves the players
  within a second or two, in game. Left empty, player one gets the primary monitor and the others
  follow left to right.
- **`<user>\Zomboid\MonitorCoop.ini`** holds that and the rest: the toggle key, whether each player
  sees as much of the world as they would playing on that monitor alone, whether the mouse pointer
  is held inside player one's monitor, and `PretendPlayers` for trying a seating arrangement on
  your own. The mod re-reads the file a second or two after you save it, and it survives
  uninstalling.
- **`<user>\Zomboid\console.txt`** is the game's log: every line the mod writes is tagged
  `[MonitorCoop]`, including the monitors it found and the layout it applied.
- More players than monitors share one, split the way the game splits a single screen: with two
  monitors, three players give two side by side on the first and one on the second.

**Every player already has their own menus** in the game's own split-screen — inventory, health,
crafting, the map — and the mod does not touch any of that, so one player opening the map leaves the
others alone. What it changes is where each player's view and HUD go.

**Keyboard and mouse still belong to player one**, which is the game's own rule, so every other
local player needs a controller. Player one's pointer is kept on player one's monitor so it cannot
wander onto someone else's screen; alt-tab frees it.

**No game file is patched on disk.** The mod is a Java agent: the launcher is told to load
`MonitorCoop.jar`, which rewrites the few methods that decide where a player's view goes as they are
loaded, in memory. Uninstalling puts the launcher files back byte for byte.

For Project Zomboid build 42 (the mod is built against the game's own `projectzomboid.jar` and
checks every method it patches is still there; if an update moves one, it says so in the log and
leaves local split-screen exactly as the game ships it). See [MonitorCoop/README.md](MonitorCoop/README.md)
for the settings table, how it works and the test checklist.
