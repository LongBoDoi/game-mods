# ModUpdater

Keeps this repo's Overcooked! 2 mods on the newest GitHub release, so players don't have to download the zip and run `Install.cmd` again for every release.

## What it does

1. **When the game starts**, it starts `ModUpdater.ps1` hidden in the background. That script asks GitHub for the newest `Overcooked2-v<version>` release and compares it with the release you installed. The installer records that version in `BepInEx\config\com.manhl.overcooked2.package.txt`.
2. **If the release is newer**, the script downloads its zip and unpacks it to `BepInEx\cache\ModUpdater\staged\` while you play.
3. **On the main menu, a dialog asks** what to do with it, the way Arcade MOD asks about its own updates. It names the new version and the one you have, and lists each mod it changes (old and new version) or adds:
   - **Update now** (Cập nhật ngay): loads the new versions into the running game, with no restart. A second dialog says what happened. If a mod only starts on the next launch (one new to you, or one that couldn't be swapped), it offers **Close game**.
   - **View details** (Xem chi tiết): opens the release page in your browser, then asks again.
   - **Later** (Để sau): keeps what you have. Nothing is installed, and you are asked again the next time the game starts.

   The dialog waits until you are on the main menu, outside an online room, with no other dialog open.
4. **When you close the game after Update now**, it runs that release's own `Install.ps1 -Update`, which replaces the files so the next launch loads them from disk. This upgrades the mods that are installed and adds the release's other mods, so a mod that is new in that release reaches you too. It does not bring back a mod you uninstalled (the installer lists those in `BepInEx\config\com.manhl.overcooked2.removed.txt`), and it adds DIYChefFix or RandomLevel only when the third-party mod they extend (DIYChef, OC2DIYLevel) is installed.

All network and file work happens in Windows PowerShell. GitHub requires TLS 1.2, which this game's old Mono can't do. The files can only be replaced after the game exits, because Windows won't let anything replace a DLL the game has loaded.

## In MOD Settings

If **MOD Settings** (OC2ModSettings) is installed, **Mod Updater** has its own page there:

| Row | |
|---|---|
| **Trạng thái** (status) | read-only: checking, downloading, up to date, new version available, or what went wrong |
| **Kiểm tra** button | asks GitHub now. MOD Settings closes, and a dialog shows the check, then the new version, "up to date" or what went wrong. Greyed out while a check or download is running |
| **Cập nhật** button | when a new version is downloaded: the same as **Update now** in the dialog. Greyed out except on the main menu outside an online room |
| **Kiểm tra khi mở game** | on (default): checks each time the game starts. Off: only the **Kiểm tra** button checks |

Without MOD Settings, the switch is `CheckOnStart` in `BepInEx\config\com.manhl.overcooked2.modupdater.cfg`, the dialog still appears, and the status is written to the BepInEx log. Version 1's `AutoUpdate` and `UpdateImmediately` settings are no longer read: nothing installs unless you choose to update.

## Updating without restarting

Mono can't unload a DLL, and Windows keeps the loaded files locked. So ModUpdater does what BepInEx's ScriptEngine does:

1. It reads each changed mod DLL from the download into memory and gives it a unique assembly name, so Mono doesn't hand back the copy already loaded.
2. It loads that copy and destroys the old plugin. The old plugin's `OnDestroy` removes its Harmony patches.
3. It adds the new plugin in the old one's place, under the same BepInEx plugin record, and tells MOD Settings to re-read the mod's settings.

Only mods whose DLL actually changed are swapped. When the game closes, the files are replaced as usual, so the next launch loads them from disk.

- **Main menu only, outside an online room.** Mid-level, the old mod's screens and timers would be left with nothing driving them. Online, the lobby is mid-sync.
- **ModUpdater itself is not swapped.** Its new version takes effect from the next launch.
- **A mod that fails to load** keeps running its old version. It is logged, and it gets its new file when the game closes like the rest.
- **Each loaded version stays in memory until the game closes.** This is harmless: the old copy has no patches and nothing calls it.
- **Mods must clean up after themselves for this to work.** Everything they patch has to come off in `OnDestroy`, and their per-frame work has to run from the plugin's own `Update`. Every mod in this collection does. A mod the update adds is not loaded in game; it starts at the next launch. A future mod that registers Steam callbacks or creates objects that outlive the plugin must remove them in `OnDestroy` too.

## Notes

- **Hand-made and developer installs** (from a repo checkout, which has no `version.txt`, or copied in by hand) record no release version, so every release counts as new and the dialog offers it.
- **Players need one manual install to get ModUpdater.** Releases from before ModUpdater don't include it. After that one install, each new release is offered in game.
- **Moving from ModUpdater 1.x:** the update that brings 2.0 is still installed the 1.x way (automatically when the game closes, if that was on). From then on, every update waits for your answer in the dialog.
- If the game is in a protected folder (such as `C:\Program Files (x86)`), the install fails. The status says so. Run `Install.cmd` as administrator once, or move the Steam library.
- Files: `BepInEx\cache\ModUpdater\state.txt` holds the status the plugin reads. `update.log` holds everything the script and the installer printed.

## Build

```
build.cmd           build ModUpdater.dll and stage it with ModUpdater.ps1 in ..\dist\
build.cmd deploy    also copy both into the game's BepInEx\plugins\ModUpdater (game closed)
```

`scripts\Release.ps1` writes `version.txt` into every release zip. The installer copies that version into the game, and ModUpdater compares it with GitHub.
