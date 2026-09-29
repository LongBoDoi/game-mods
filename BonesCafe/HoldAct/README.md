# Hold Act — Bone's Cafe

Hold the **Act** button (J on keyboard, X / West on a gamepad) to keep chopping and grinding instead of mashing it.

The game already repeats Act while it's held, but only 4 times a second. This mod makes the repeat rate configurable, 10 times a second by default. A quick tap still acts only once: repeating starts after the button has been held for a short delay.

Interact (Space / A) is deliberately left alone. Repeating it would just pick an item up and drop it again.

## Install

Close the game, then double-click `Install.cmd` from the `BonesCafe` release zip. In a repo checkout, run `build.cmd` first, then `..\installer\Install.cmd`.

The installer finds the game in your Steam libraries and installs BepInEx 5.4.23.2 if it's missing (downloaded, or taken from `BepInEx_win_x64_5.4.23.2.zip` if you put that next to it). Then it copies the mod into `BepInEx\plugins\HoldAct\`.

- `Install.cmd -GameDir "D:\path\to\Bone's Cafe"` uses a specific game folder.
- `Uninstall.cmd` removes the mod and leaves BepInEx installed.

## Settings

After the first launch, edit `BepInEx\config\manhl.bonescafe.holdact.cfg` with the game closed:

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` restores the unmodded 0.25 s repeat |
| `RepeatInterval` | `0.1` | Seconds between repeats while held (vanilla 0.25) |
| `InitialDelay` | `0.25` | Seconds to hold before repeating starts |

## How it works

`PlayerController.CheckHoldChopKey` fires `Act()` once `currentTimeActHold > 0.25f`. A Harmony transpiler replaces that constant with a per-player threshold: `InitialDelay` until the first repeat of a hold, and `RepeatInterval` after that. The mod follows the game's own rebinding, because it reads the game's `ActHold` action.

## Build

Requires the .NET SDK and the game with BepInEx installed, because the project references the game's `Managed` folder and BepInEx.

```
build.cmd
build.cmd -p:GameDir="D:\Games\Bone's Cafe"     (if the game is somewhere else)
```

The build copies the DLL into the game's `BepInEx\plugins\HoldAct\` (if the game is closed) and into `..\dist\`.
