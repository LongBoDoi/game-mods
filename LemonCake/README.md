# Lemon Cake mods

| Mod | What it does |
|---|---|
| [LemonCoop](LemonCoop/) | Local 2-player split-screen co-op with gamepads: each player gets their own room camera, and money and XP are shared. Also backs your saves up at every launch and repairs the shop upgrades that a co-op save can wipe, and can write the Steam achievements listed at the top of `Scripts/main.lua` |

**Install:** download `LemonCake-v*.zip` from the release, extract it anywhere, and double-click `Install.cmd` with the game closed. It finds the game through Steam, installs UE4SS v3.0.1 if it's missing, installs the mod and enables it in `mods.txt`, and adds a small patch pak with the gamepad bindings. `Uninstall.cmd` removes the mod and the pak.

**Before playing:** turn **Steam Input off** for Lemon Cake (Steam → Lemon Cake → Properties → Controller → "Disable Steam Input"). Player 2 joins automatically once you're in the game; by default the first gamepad is player 2 and player 1 uses keyboard and mouse. Keys: F1 split layout · F2 join · F3 remove player 2 · F4 teleport player 2 to player 1 · F8 dual monitor · Ctrl+Shift+A write achievements.

**Saves:** co-op play can make the game save with every shop upgrade cleared. The mod snapshots your `.sav` files into `%LOCALAPPDATA%\LemonCake\Saved\SaveGames_LemonCoopBackups\` at every launch (newest 15 kept) and runs a guard that restores the upgrade flags right after such a save; see [LemonCoop/README.md](LemonCoop/README.md) for rolling back by hand.
