# ModUpdater

Keeps this repo's Overcooked! 2 mods on the newest GitHub release, so players don't have to download the zip and run `Install.cmd` again for every release.

## What it does

1. **When the game starts**, it starts `ModUpdater.ps1` hidden in the background. That script asks GitHub for the newest `Overcooked2-v<version>` release and compares it with the release you installed. The installer records that version in `BepInEx\config\com.manhl.overcooked2.package.txt`.
2. **If the release is newer**, the script downloads its zip and unpacks it to `BepInEx\cache\ModUpdater\staged\` while you play.
3. **Right away, if you want:** on the main menu, **Cập nhật** loads the new versions into the running game, with no restart. The **Cập nhật ngay khi tải xong** setting does this automatically. See [Updating without restarting](#updating-without-restarting).
4. **When you close the game**, it runs that release's own `Install.ps1 -Update`. This upgrades only the mods that are already installed, plus ModUpdater itself, and never adds a mod you didn't choose. The next launch loads the new files.

All network and file work happens in Windows PowerShell. GitHub requires TLS 1.2, which this game's old Mono can't do. The work also has to happen after the game exits, because Windows won't let anything replace a DLL the game has loaded.

## In MOD Settings

If **MOD Settings** (OC2ModSettings) is installed, **Mod Updater** has its own page there:

| Row | |
|---|---|
| **Trạng thái** (status) | read-only: checking, downloading, up to date, update downloaded, or what went wrong |
| **Kiểm tra** button | ask GitHub now (greyed out while a check or download is running) |
| **Cập nhật** button | when an update is downloaded: loads it into the game now, without restarting. Greyed out except on the main menu outside an online room |
| **Cập nhật ngay khi tải xong** | off (default). On: does what **Cập nhật** does by itself as soon as the download finishes and you are on the main menu |
| **Tự động cập nhật** | on (default): updates install by themselves when you close the game. Off: they are still downloaded, but only installed if you loaded them with **Cập nhật** |

Without MOD Settings, the same switches are `UpdateImmediately` and `AutoUpdate` in `BepInEx\config\com.manhl.overcooked2.modupdater.cfg`, and the status is written to the BepInEx log.

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
- **Mods must clean up after themselves for this to work.** Everything they patch has to come off in `OnDestroy`, and their per-frame work has to run from the plugin's own `Update`. ChefSkills and EmoteSync both do this. A future mod that registers Steam callbacks or creates objects that outlive the plugin must remove them in `OnDestroy` too.

## Notes

- **Hand-made and developer installs are not overwritten automatically.** If the mods were installed from a repo checkout, which has no `version.txt`, or copied in by hand, no release version is recorded. ModUpdater still checks and downloads, but it only installs when you press **Cập nhật**.
- **Players need one manual install to get ModUpdater.** Releases from before ModUpdater don't include it. After that one install, updates arrive by themselves.
- If the game is in a protected folder (such as `C:\Program Files (x86)`), the install fails. The status says so. Run `Install.cmd` as administrator once, or move the Steam library.
- Files: `BepInEx\cache\ModUpdater\state.txt` holds the status the plugin reads. `update.log` holds everything the script and the installer printed.

## Build

```
build.cmd           build ModUpdater.dll and stage it with ModUpdater.ps1 in ..\dist\
build.cmd deploy    also copy both into the game's BepInEx\plugins\ModUpdater (game closed)
```

`scripts\Release.ps1` writes `version.txt` into every release zip. The installer copies that version into the game, and ModUpdater compares it with GitHub.
