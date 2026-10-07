# Game Mods

My mods for various games, one folder per game. Each game has its own GitHub release with a ready-to-install zip.

| Game | Mods | Loader | Release |
|---|---|---|---|
| [Bone's Cafe](BonesCafe/) | BonesTweaks (fast Act hold, infinite ingredients, infinite souls, cafe size) | BepInEx 5 (Mono) | [BonesCafe](../../releases?q=BonesCafe) |
| [Kebab Chefs! - Restaurant Simulator](KebabChefs/) | PlayerTweaks, LoadTweaks | BepInEx 6 (IL2CPP) | [KebabChefs](../../releases?q=KebabChefs) |
| [Lemon Cake](LemonCake/) | LemonCoop (split-screen co-op, save backup + upgrade repair, achievements) | UE4SS 3.0.1 (Lua) + patch pak | [LemonCake](../../releases?q=LemonCake) |
| [Mortal Kombat 11](MK11/) | MK11Tweaks | Ultimate ASI Loader (C++) | [MK11](../../releases?q=MK11) |

## Installing

Download the game's zip from its release, extract it, and double-click `Install.cmd` with the game closed. Each installer finds the game through Steam (or pass `-GameDir "<folder>"`), installs the mod loader if it's missing, and copies the mods in. `Uninstall.cmd` reverses it.

## Layout

```
<Game>/
  README.md        mods for that game + install notes (also the release notes)
  <Mod>/           source, README.md, build.cmd
  installer/       Install.cmd, Uninstall.cmd, Install.ps1
  dist/            build output staged for the release zip (not committed)
scripts/
  Release.ps1      build a game's mods, zip them with its installer, publish a GitHub release
```

## Building

Every mod has a `build.cmd`, which builds the mod and stages the result in `<Game>\dist\`. Run `installer\Install.cmd` in a checkout to install from `dist\`.

| Mods | Needs |
|---|---|
| C# (BepInEx) | .NET SDK, plus the game with its BepInEx installed (the projects reference the game's assemblies) |
| LemonCoop | Nothing: [repak](https://github.com/trumank/repak) is downloaded on first build |
| MK11Tweaks | Nothing: [llvm-mingw](https://github.com/mstorsjo/llvm-mingw) and the C++ dependencies are downloaded on first build |

## Releasing

```
scripts\release.cmd <Game> <version>              e.g.  scripts\release.cmd MK11 1.0.1
scripts\release.cmd <Game> <version> -Publish     also tag <Game>-v<version> and publish the GitHub release
```

Publishing needs a clean, pushed working tree, and uses the GitHub login saved in Git Credential Manager.
