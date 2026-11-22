# Overcooked! 2 mods

| Mod | What it does |
|---|---|
| [ChefSkills](ChefSkills/) | After each kitchen loads, every player shapes their chef on a screen built from the game's own dialog art (one card per player, in their chef colour, including online players' live builds and lock-in status): nine skills — **cooking**, **slow burn**, **chopping**, **washing**, **serving**, **running**, **dashing**, **throwing** and **firefighting** — where raising one costs a point freed by lowering another, and a build locks in only when it adds up to zero. The level starts once everyone has locked in. Each player also picks **special skills**: one active used with EMOTE, two passives and an optional quirk that frees points (112 in all, e.g. Blink, Pack Mule, Hot Hands, Rally Cry). Holding the game's **Skip** button skips the screen. Replaces ChefRoles |
| [EmoteSync](EmoteSync/) | Companion for the third-party **Arcade MOD (HostTools)**: its emote wheel settings and custom emote texts belong to each player instead of to whoever is watching. Every bubble shows the sender's text, font size and colour (and the voice-pack file name the sender's game picked) on every screen, over the right lobby card or chef, shared through Steam lobby member data. Emotes are recognised by their localisation key, so every custom text applies whatever language each player's game is in. Everyone in the lobby needs it; chefs of players without it show the game's own bubble |
| [DIYChefFix](DIYChefFix/) | Companion for the third-party **DIYChef FastInit 2.4.0** (not included): fixes its **default chef** and its **chef grid**. DIYChef only accepts chefs your save has unlocked, checks once at startup, and falls back to a DLC chef (Snowman) you may not have. That breaks the default chef ("Invalid default chef") and leaves the grid empty, so it never opens. Now the default is re-checked as unlocks settle and the fallback is a chef you have. The grid lists your `prefer.txt` chefs first, then every other unlocked chef and every custom chef in `Resources\`. Optionally (on by default) still-locked base-game chefs can be picked; DLC chefs still need their DLC |
| [TiengViet](TiengViet/) | **Vietnamese** for the game and every mod here, switched with a **LANGUAGE / NGÔN NGỮ** row at the top of **Settings → Game Settings** (English / Tiếng Việt / Default (Steam), the last, the default, being the game's own language with the mod doing nothing until you pick another). The change applies instantly, with no restart. All 1,357 game texts are translated: menus, levels, tutorials, story and DLC. ChefSkills, EmoteSync, DIYChefFix and ModUpdater follow the same choice. In Vietnamese, titles use a font built into the mod (Coiny by default, six to choose from in MOD Settings), because the game's title font lacks letters such as ạ, ơ and ư. Each player picks their own language |
| [ModUpdater](ModUpdater/) | Keeps the other mods on the newest release: it checks GitHub each time the game starts and, when there is a new version, a dialog on the main menu lists what changes and asks what to do: **Update now** (loads it into the running game, no restart), **View details** (opens the release page) or **Later**. Its page in **MOD Settings** shows the status and has **Kiểm tra** (check) and **Cập nhật** (update) buttons |

**Install:** download `Overcooked2-v*.zip` from the release, extract it anywhere, and double-click `Install.cmd` with the game closed. It finds the game through Steam and then **stays open**, asking what you want, doing it, and asking again with the list refreshed — so you can install one mod, upgrade BepInEx and check the result in one sitting. It closes when you choose **Q**.

```
Which would you like to install?

  1) OC2DIYLevel      custom level pack + helper plugins (not bundled: ~400 MB)                  [installed, 12 level sets]
  2) BepInEx          the mod loader itself (BepInEx 5 x86)                                      [installed 5.4.23.6]
  3) ChefSkills       trade skill levels each level: 9 skills, raise some by lowering others     [installed 4.1.0.0]
  4) EmoteSync        Arcade MOD (HostTools) emote texts follow each player, synced online       [not installed]
  ...

  A) all of them        Q) quit

Choose, e.g. 1,3 [A]:
```

Press Enter for everything, or pick items by number or name; the `[installed]` markers update after each round. `Uninstall.cmd` works the same way but only lists what's actually installed, and closes itself once there's nothing left. Mods go in `BepInEx\plugins\<Mod>\`; removing one leaves its settings alone.

Because the menu keeps running, the game doesn't have to be closed when you start it: if the game is open when you pick something, it says so, changes nothing, and returns to the menu — close the game and choose again.

To do one thing and exit instead (these never loop, so they're safe in scripts):

| Command | What it does |
|---|---|
| `Install.cmd -All` | all available mods and BepInEx, no prompts, once |
| `Install.cmd -Only ChefSkills` | one mod (BepInEx is added too if it's missing, since mods don't load without it) |
| `Install.cmd -Only DIYChefFix` | the DIYChef fix (install DIYChef FastInit first) |
| `Install.cmd -Only BepInEx` | install or upgrade just the loader |
| `Install.cmd -Only BepInEx -BepInExVersion 5.4.23.2` | pin a specific BepInEx |
| `Uninstall.cmd -Only EmoteSync` | remove one mod |
| `Uninstall.cmd -All` | remove all mods and the loader |
| `Install.cmd -Update` | upgrade the mods already installed and add the release's mods you don't have yet, except ones you uninstalled and DIYChefFix without DIYChef (what ModUpdater runs after the game closes, once you chose **Update now**) |

Every install also removes OC2Tweaks, RandomLevel and the Relay Kitchen level, which are no longer part of this collection (release 4.3.0 installed the first two by mistake). Their settings files are left alone.

**BepInEx** defaults to the newest stable **5.x** release for `win_x86`, not simply the newest release on GitHub: this game is a 32-bit Unity 2017 Mono build, so the x64 loader and the BepInEx 6 prereleases (which currently top that list) don't work on it. A `BepInEx_win_x86_*.zip` placed next to the installer is used instead of downloading. Upgrading only replaces the loader and `BepInEx\core`, so plugins and settings are untouched.

**Removing BepInEx** disables every BepInEx mod in the game, so it asks first and names the other plugins that would stop loading. Their files and all saved settings are kept — reinstalling BepInEx brings them back. Add `-PurgeBepInEx` to delete those as well.

## OC2DIYLevel (third-party level pack)

The installer can also put the **OC2DIYLevel** custom-level pack in place, but it is **not one of my mods and is not included here**: it's someone else's work (see the `READ ME` in the pack itself for the author and their contact), and it's roughly 400 MB of Unity asset bundles, so it is neither committed to this repo nor shipped in the release zip. You download it yourself.

Pick it from the menu and the installer looks for the extracted pack in this order: `-DiyPackDir "<folder>"`, a `plugins\` folder next to the installer, `..\dist\plugins\`, then the newest `*OC2*`/`*Overcooked*` folder in your Downloads. If none of those has it, it asks you to paste the folder, and Enter skips it. Either the `plugins` folder or its parent works.

```
Install.cmd -Only OC2DIYLevel -DiyPackDir "C:\Users\you\Downloads\New Pack OC2"
Uninstall.cmd -Only OC2DIYLevel
```

Removing it asks first and says how much it is about to delete, since putting it back means copying the whole pack in again. `-Yes` skips that question.

**One deliberate difference from the pack's own instructions.** Its `READ ME` tells you to delete your whole `BepInEx\plugins` folder and drop the new one in, to avoid stale level files conflicting. That would also delete these mods and anything else you'd installed. So the installer clears **only the pack's own entries** before copying — which is what actually prevents those conflicts — and records what it copied in `BepInEx\config\com.manhl.overcooked2.diypack.manifest`. Removing the pack takes exactly those entries back out and leaves everything else, including the pack mods' own settings, alone.

Custom levels have to match across the match: everyone playing needs the same pack.


## ModUpdater

Install it once with the installer (it is in the list like the other mods) and later releases come to you: when the game starts, it asks GitHub for the newest `Overcooked2-v*` release, and if that is newer than the one you installed it downloads it in the background. Then a dialog on the main menu names the new version, lists the mods it changes or adds, and asks:

- **Update now**: loads the new versions into the running game, with no restart. When you close the game, that release's own installer runs with `-Update` and replaces the files, upgrading the mods you have and adding this collection's mods you don't have yet. A mod you uninstalled stays uninstalled.
- **View details**: opens the release page, then asks again.
- **Later**: keeps what you have; nothing is installed, and you are asked again next time the game starts.

**Kiểm tra** (check) in MOD Settings asks GitHub straight away and shows the result in a dialog. Nobody needs to download a zip or run `Install.cmd` again. See [ModUpdater/README.md](ModUpdater/README.md) for the settings and the files it keeps.

## ChefSkills

Right after the loading screen (and after any intro cutscene), before **Ready… Go!**, the kitchen stays frozen and a **CHOOSE YOUR SKILLS** dialog appears with one card per player. Online, that includes the other PCs' players, whose cards show their build and whether they've locked in. **Up/Down** picks a skill, **Left** lowers it to free a point, **Right** raises it (even past what you have freed), and **Select** locks the build in only when it adds up to exactly zero; **Back** unlocks it. There is no timer: the level starts only when every player has locked in, or when someone holds the game's **Skip** button for a second, like skipping a cutscene (the host's skip counts for everyone). Each player's last build is pre-selected on the next level.

Online, everyone in the lobby needs Chef Skills: if anyone doesn't have it, each level is played the normal way, with no skill screen and no skill changes for anybody. With everyone on board, running and dashing apply on each player's own PC, and the rest on the host, which matches each joining player's chef to their build. ChefSkills 2.0.0 replaces ChefRoles 1.0.0 (Cook / Server roles), and the installer removes the old plugin. See [ChefSkills/README.md](ChefSkills/README.md) for every skill's numbers and how to tune them, and [ChefSkills/SPECIAL_SKILLS.md](ChefSkills/SPECIAL_SKILLS.md) for the special skills. Chef Skills 4.1.0 changes the online protocol again, so everyone needs 4.1.0 (a lobby mixing 4.0.0 and 4.1.0 plays without skills).

For the Steam Mono build only (Unity 2017, 32-bit, BepInEx 5 **x86**). See each mod's README for details and in-match test checklists.
