# Hold Act — Bone's Cafe

Hold the **Act** button (J on keyboard, X / West on a gamepad) to keep chopping and grinding instead of mashing it.

The game already repeats Act while it's held, but only 4 times a second. This mod makes the repeat rate configurable, 10 times a second by default. A quick tap still acts only once: repeating starts after the button has been held for a short delay.

Interact (Space / A) is deliberately left alone. Repeating it would just pick an item up and drop it again.

## Install

1. Close the game.
2. Put `HoldAct.dll` next to `Install.cmd` in the `dist` folder. Get the DLL from a release, or build it (see below).
3. Double-click `Install.cmd`.

The installer finds the game in your Steam libraries and installs BepInEx 5.4.23.2 if it's missing. It uses `BepInEx_win_x64_5.4.23.2.zip` if that file is next to it, and downloads it otherwise. Then it copies the mod into `BepInEx\plugins\HoldAct\`.

- `Install.cmd -GameDir "D:\path\to\Bone's Cafe"` uses a specific game folder.
- `Install.cmd -Uninstall` removes the mod and leaves BepInEx installed.

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

Requires the .NET SDK. The project references the game's `Managed` folder and BepInEx, so set `GameDir` in `HoldAct.csproj` if your install is elsewhere.

```
dotnet build -c Release
```

The build copies the DLL into the game's `BepInEx\plugins\HoldAct\` and into `dist\`.
