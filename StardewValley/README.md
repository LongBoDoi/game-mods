# Stardew Valley mods

| Mod | What it does |
|---|---|
| [MonitorCoop](MonitorCoop/) | Local co-op on a monitor each instead of a split window: the game's window stretches over your monitors and every player gets a whole one, in the order the monitors are arranged. Different resolutions, uneven arrangements and more players than monitors all handled |

**Install:** download `StardewValley-v*.zip` from the release, extract it anywhere, and double-click `Install.cmd` with the game closed. It finds the game through Steam, installs SMAPI if it's missing, and copies the mod into `Mods\MonitorCoop\`. `Uninstall.cmd` removes the mod and keeps your settings.

**Using it:** start the game through SMAPI and begin local co-op as usual — the co-op menu, or Start on another controller in a save that has cabins. The window spreads over your monitors as the second player joins, and goes back to its old size, position and fullscreen mode when they leave.

- **F7** turns the monitor layout off and on; off is the game's own split-screen in one window.
- **`monitorcoop`** in the SMAPI console lists the monitors with their numbers, the players, and what the mod is doing. `monitorcoop reload` re-reads the settings.
- More players than monitors share one, split the way the game splits a single screen: with two monitors, three players give two side by side on the first and one on the second.

**Cursors stay on their own monitor:** each player's cursor reaches every corner of their own screen and stops at its edges, so player one's pointer can't wander onto someone else's. Alt-tab frees the pointer when you need the other monitors.

**Keyboard and mouse still belong to player one** — that is the game's own rule, so every other local player needs a controller. The mod only decides where each player's screen goes and keeps their cursor on it; it adds no player slots and changes nothing about saves or how players join.

**Settings:** `Mods\MonitorCoop\config.json` — the toggle key, which monitors to use and in what order, whether player one gets the primary monitor, whether the mouse pointer is held inside player one's monitor, and how far the world is zoomed out (the game halves the zoom for split-screen; the mod undoes that by default, since each player now has a whole monitor). They survive uninstalling.

For Stardew Valley 1.6 with SMAPI 4. See [MonitorCoop/README.md](MonitorCoop/README.md) for the details, the settings table and the test checklist.
