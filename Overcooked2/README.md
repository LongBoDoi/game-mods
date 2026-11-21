# Overcooked! 2 mods

| Mod | What it does |
|---|---|
| [ChefSkills](ChefSkills/) | After each kitchen loads, every player shapes their chef on a screen built from the game's own dialog art (one card per player, in their chef colour, including online players' live builds and lock-in status): nine skills — **cooking**, **slow burn**, **chopping**, **washing**, **serving**, **running**, **dashing**, **throwing** and **firefighting** — where raising one costs a point freed by lowering another, and a build locks in only when it adds up to zero. The level starts once everyone has locked in. Holding the game's **Skip** button skips the screen. Replaces ChefRoles |

**Install:** download `Overcooked2-v*.zip` from the release, extract it anywhere, and double-click `Install.cmd` with the game closed. It finds the game through Steam and then **stays open**, asking what you want, doing it, and asking again with the list refreshed — so you can install one mod, upgrade BepInEx and check the result in one sitting. It closes when you choose **Q**.

```
Which would you like to install?

  1) OC2DIYLevel      custom level pack + helper plugins (not bundled: ~400 MB)   [installed, 12 level sets]
  2) BepInEx          the mod loader itself (BepInEx 5 x86)                       [installed 5.4.23.6]
  3) ChefSkills       trade skill levels each level: 9 skills, raise some by lowering others  [installed 2.1.0.0]

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
| `Install.cmd -Only BepInEx` | install or upgrade just the loader |
| `Install.cmd -Only BepInEx -BepInExVersion 5.4.23.2` | pin a specific BepInEx |
| `Uninstall.cmd -Only ChefSkills` | remove one mod |
| `Uninstall.cmd -All` | remove all mods and the loader |

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

**One deliberate difference from the pack's own instructions.** Its `READ ME` tells you to delete your whole `BepInEx\plugins` folder and drop the new one in, to avoid stale level files conflicting. That would also delete ChefSkills and anything else you'd installed. So the installer clears **only the pack's own entries** before copying — which is what actually prevents those conflicts — and records what it copied in `BepInEx\config\com.manhl.overcooked2.diypack.manifest`. Removing the pack takes exactly those entries back out and leaves everything else, including the pack mods' own settings, alone.

Custom levels have to match across the match: everyone playing needs the same pack.

## ChefSkills

Right after the loading screen (and after any intro cutscene), before **Ready… Go!**, the kitchen stays frozen and a **CHOOSE YOUR SKILLS** dialog appears with one card per player. Online, that includes the other PCs' players, whose cards show their build and whether they've locked in. **Up/Down** picks a skill, **Left** lowers it to free a point, **Right** raises it (even past what you have freed), and **Select** locks the build in only when it adds up to exactly zero; **Back** unlocks it. There is no timer: the level starts only when every player has locked in, or when someone holds the game's **Skip** button for a second, like skipping a cutscene (the host's skip counts for everyone). Each player's last build is pre-selected on the next level.

Online, everyone in the lobby needs Chef Skills: if anyone doesn't have it, each level is played the normal way, with no skill screen and no skill changes for anybody. With everyone on board, every PC matches each player's chef to their build and uses the host's numbers, so progress bars and movement agree between PCs. ChefSkills 2.0.0 replaces ChefRoles 1.0.0 (Cook / Server roles), and the installer removes the old plugin. See [ChefSkills/README.md](ChefSkills/README.md) for every skill's numbers and how to tune them.

For the Steam Mono build only (Unity 2017, 32-bit, BepInEx 5 **x86**). See each mod's README for details and in-match test checklists.
