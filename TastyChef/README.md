# Tasty Chef mods

| Mod | What it does |
|---|---|
| [LocalCoop](LocalCoop/) | Local co-op for up to four players on one PC: every extra player gets their own window (a monitor each, or a share of one monitor) and their own controller, and joins Player 1 through the game's own LAN room — so the whole of the game's co-op works as it does online |

**Install:** download `TastyChef-v*.zip` from the release, extract it anywhere, and double-click `Install.cmd` with the game closed. It finds the game through Steam, installs BepInEx 5.4.23.5 if it's missing (or updates an older 5.4.23.x, which crashes this game at startup), lets the game run more than one copy, and installs the mod. `Uninstall.cmd` removes the mod and makes the game single-copy again; BepInEx stays.

## Playing together on one PC

**Using it:** start the game as usual and press **F9** at the title screen — a window opens for Player 2 (again for Players 3 and 4). Each new player presses **LB + RB together** on their controller to take it. Player 1's game creates a LAN room by itself, the other windows join it, and Player 1 picks the save and presses Start in the room. **Shift+F9** closes the last window added; closing Player 1's game closes them all.

- **Windows:** a whole monitor each when there are enough monitors, otherwise Player 1's monitor is split (left/right for two, quarters for three or four). `Layout = SplitScreen` always splits.
- **Keyboard and mouse stay with Player 1**, together with any controller nobody took. Every other player needs a controller; their windows never take the keyboard, even when clicked.
- **Nothing of Player 1's is touched:** the other windows keep their own saves and settings, sign in to the room service under their own profile, and show up as `<name> (P2)`. Player 1's window size and monitor come back after co-op.
- **Needs internet** for the room code, like the game's own LAN room; the game then plays directly between the windows on this PC. Multiplayer saves start at day 4.

**Settings:** `BepInEx\config\manhl.tastychef.localcoop.cfg` — the keys, the window layout, sound in the other windows (off by default), and how many windows to open at start.

See [LocalCoop/README.md](LocalCoop/README.md) for how it works, the files it writes and the test checklist.
