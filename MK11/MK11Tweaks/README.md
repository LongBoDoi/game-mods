# MK11 Tweaks — Mortal Kombat 11

An in-game panel (**F1**) that toggles offline cheats, with the same options as the WeMod MK11 trainer.

| Option | What it does |
|---|---|
| Unlimited Health | Your fighter's health stays full |
| Unlimited Attack Meter / Defense Meter | Offensive and defensive bars stay full |
| Weak Opponents | Multiplies every drop in the opponent's health (slider: ×1 to ×20, default ×3) |
| Unlimited Fatal Attacks | Fatal Blow is always available; optionally at any health |
| Unlimited Krushing Blows | Krushing Blows can be repeated; "Easy" also drops their trigger condition |

**Offline only.** Don't use it online or in Towers of Time, which has leaderboards.

## Install

Run `Install.cmd` from the release zip. It copies `MK11Tweaks.asi` into `Binaries\Retail`, and adds the [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader) (`dinput8.dll`) if it isn't already there.

Launch the **DirectX 11** version of the game (`MK11.exe`); the mod does nothing under DX12. It writes `MK11Tweaks.log` next to itself, and refuses to patch anything if the game version doesn't match its signatures.

## Settings

`Binaries\Retail\MK11Tweaks.ini` (written on first launch, and saved whenever you change something in the panel):

- `[General]` `ToggleKey` (F1), `Enabled`, `PlayerSide` (1 or 2: which fighter is "you"), and the panel position.
- `[Cheats]` one key per option above, plus `WeakOpponentsDamage`, `FatalBlowAnyHealth` and `EasyKrushingBlows`.

## How it works

It's a standalone ASI mod (not an MK11Hook plugin). It finds game functions by byte signature (the same ones MK11Hook uses), and gets a per-tick hook by patching the call to the game's script dispatcher. The overlay is Dear ImGui, drawn from a MinHook hook on `IDXGISwapChain::Present`. While the panel is open, input is swallowed through a subclassed WndProc.

## Build

```
build.cmd            build, stage in ..\dist\
build.cmd install    build and install into the game
build.cmd clean      full rebuild
```

No Visual Studio needed. The first build downloads [llvm-mingw](https://github.com/mstorsjo/llvm-mingw) 20260922 into `<repo>\.tools\` (or set `LLVM_MINGW`). It also downloads the pinned dependencies into `deps\`: Dear ImGui v1.92.9b, MinHook @ 8af6b4a, and Ultimate ASI Loader v9.7.4 (NoPDB x64).
