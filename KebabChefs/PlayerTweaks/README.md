# Player Tweaks — Kebab Chefs! - Restaurant Simulator

A BepInEx 6 (IL2CPP) plugin with an in-game overlay (**F1**) full of quality-of-life tweaks. Every tweak can be switched off, and with the master switch off the game's own values are left alone.

## Tabs

- **Player**: walk, sprint, crouch and jump speed, gravity, mouse sensitivity, item reach, and how many items you can carry.
- **Teleport**: jump to places found in the loaded scene, save your own waypoints, and bind hotkeys to them.
- **Extras**:
  - **Multi-pick**: scroll while looking at an item to mark more of the same kind, then pick them all up with one click.
  - **Place all**: hold the modifier key and click a plate or tray to put down everything you're carrying, neatly arranged. Placed items can snap to a grid, with an align key.
  - **Store hold-repeat**: hold the mouse button at a store shelf instead of clicking once per item.
  - **Shopping boxes**: right-click to put the held item back in, hold to repeat, and extra take-out/destroy keys.
  - **Fish cutting**: skip the knife minigame.
  - **Slicify**: accepts anything sliceable, and loads an armful in one trip.
  - **Spices**: set how much one shake adds, and skip the hold.
  - **Service counters**: top one up while portions are still on it, with a configurable stack limit.
  - **Plate cabinets**: stop waiters drawing clean plates from chosen cabinets, so one can be kept as a reserve (shared across co-op).
- **Cat**: decide when the thief cat visits instead of leaving it to chance, or summon it with a key (shared across co-op).
- **Menu**: a corner overlay showing how many portions of each dish are plated and ready, a sort order for the dish picker, and saved menu presets you can restore in one click.

## Settings

`BepInEx\config\com.manhl.kebabchefs.playertweaks.cfg`, written on first launch. Changes made in the overlay are saved there too.

## Build

```
build.cmd
```

Needs the .NET SDK, plus the game with BepInEx 6 IL2CPP already run once, because the project compiles against the interop assemblies BepInEx generates in `BepInEx\interop`. The build also installs the DLL into the game, if the game is closed. If the game is somewhere else: `build.cmd -p:GameDir="D:\Games\Kebab Chefs!"`.
