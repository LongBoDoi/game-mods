# Stardew Valley mods

| Mod | What it does |
|---|---|
| [MonitorCoop](MonitorCoop/) | Local co-op on a monitor each instead of a split window: the game's window stretches over your monitors and every player gets a whole one, in the order the monitors are arranged. Different resolutions, uneven arrangements and more players than monitors all handled |
| [AutoFishCoop](AutoFishCoop/) | Gives every local co-op player their own [Auto Fishing 2.0](https://www.nexusmods.com/stardewvalley/mods/21339) settings. That mod keeps one set for the whole game, so one player's keys, casting power and stamina cut-off currently apply to everybody |

**Install:** download `StardewValley-v*.zip` from the release, extract it anywhere, and double-click `Install.cmd` with the game closed. It finds the game through Steam, installs SMAPI if it's missing, and copies each mod into its own folder under `Mods\`. `Uninstall.cmd` removes them and keeps your settings. AutoFishCoop also needs Auto Fishing 2.0, which isn't mine to ship: without it SMAPI simply skips AutoFishCoop, and MonitorCoop is unaffected either way.

## A monitor each

**Using it:** start the game through SMAPI and begin local co-op as usual — the co-op menu, or Start on another controller in a save that has cabins. The window spreads over your monitors as the second player joins, and goes back to its old size, position and fullscreen mode when they leave.

- **F7** turns the monitor layout off and on; off is the game's own split-screen in one window.
- **`monitorcoop`** in the SMAPI console lists the monitors with their numbers, the players, and what the mod is doing. `monitorcoop reload` re-reads the settings.
- More players than monitors share one, split the way the game splits a single screen: with two monitors, three players give two side by side on the first and one on the second.

**Cursors stay on their own monitor:** each player's cursor reaches every corner of their own screen and stops at its edges, so player one's pointer can't wander onto someone else's. Alt-tab frees the pointer when you need the other monitors.

**Keyboard and mouse still belong to player one** — that is the game's own rule, so every other local player needs a controller. The mod only decides where each player's screen goes and keeps their cursor on it; it adds no player slots and changes nothing about saves or how players join.

**Settings:** `Mods\MonitorCoop\config.json` — the toggle key, which monitors to use and in what order, whether player one gets the primary monitor, whether the mouse pointer is held inside player one's monitor, and how far the world is zoomed out (the game halves the zoom for split-screen; the mod undoes that by default, since each player now has a whole monitor). They survive uninstalling.

## Auto Fishing per player

Auto Fishing 2.0 was written for one player and keeps one set of settings for the whole game, so in split-screen its keys, its casting power and its stamina cut-off apply to everyone at once. AutoFishCoop gives each player their own copy: Auto Fishing's own keys now change only the player who pressed them, the stamina cut-off stops only the player who ran out, and a catch on one screen stops reaching into another player's mouse. Keep using Auto Fishing's own `config.json`; each player starts from it.

- **Press the D-pad up** to turn casting again by itself on and off, and **click the left stick** to open your own menu on your own screen: auto fishing, hooking, casting again by itself and the casting power, with the D-pad to move, **A** to pick and **B** to close. Every player presses them on their own controller, so each one changes only that player's fishing — which is the only way round the fact that one keyboard can never speak for player two.
- **The D-pad no longer walks** (the left stick does), which is what leaves it free for shortcuts instead of taking a step north with every press. It still works in menus and arcade games, and `DisableDpadMovement` gives it back.
- **`autofishcoop`** in the SMAPI console shows what each player's settings are, where they started and which buttons do what. `autofishcoop reload` re-reads the settings.
- **`Mods\AutoFishCoop\config.json`** sets those buttons, and can give a player different settings to start with, e.g. `"Players": { "2": { "AutoFish": false } }`.

For Stardew Valley 1.6 with SMAPI 4. See [MonitorCoop/README.md](MonitorCoop/README.md) and [AutoFishCoop/README.md](AutoFishCoop/README.md) for the details, the settings tables and the test checklists.
