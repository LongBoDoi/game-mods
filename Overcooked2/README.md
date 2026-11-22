# Overcooked! 2 mods

| Mod | What it does |
|---|---|
| [OC2Tweaks](OC2Tweaks/) | A screen in the game's own **Settings → OC2 TWEAKS** menu (Vietnamese): **force host** so public Arcade/Versus matchmaking creates your lobby instead of joining someone else's, a corner overlay with every player's **ping** in ms (**F9**), and the saved ban list. The game's own player cards gain a working **Kick** for the host, with **ban** (saved, and re-kicked automatically next time). Plus **F5** to restart the level you're playing straight away — no pause menu, no "are you sure?" — switchable and rebindable on the same screen |
| [RandomLevel](RandomLevel/) | **More Levels → Chơi ngẫu nhiên — tất cả màn** starts a shuffled rotation of installed DIY levels. **F8** skips the current round; **Continue** loads the next random level directly; **Restart** keeps the current level; **F7** at results returns to the main menu. Requires OC2DIYLevel |
| [ChefSkills](ChefSkills/) | After each kitchen loads, every player shapes their chef on a screen built from the game's own dialog art (one card per player, in their chef colour, including online players' live builds and lock-in status): nine skills — **cooking**, **slow burn**, **chopping**, **washing**, **serving**, **running**, **dashing**, **throwing** and **firefighting** — where raising one costs a point freed by lowering another, and a build locks in only when it adds up to zero. The level starts once everyone has locked in. Each player also picks **special skills**: one active used with EMOTE, two passives and an optional quirk that frees points (112 in all, e.g. Blink, Pack Mule, Hot Hands, Rally Cry). Holding the game's **Skip** button skips the screen. Replaces ChefRoles |
| [EmoteSync](EmoteSync/) | Companion for the third-party **Arcade MOD (HostTools)**: its emote wheel settings and custom emote texts belong to each player instead of to whoever is watching. Every bubble shows the sender's text, font size and colour (and the voice-pack file name the sender's game picked) on every screen, over the right lobby card or chef, shared through Steam lobby member data. Emotes are recognised by their localisation key, so every custom text applies whatever language each player's game is in. Everyone in the lobby needs it; chefs of players without it show the game's own bubble |
| [DIYChefFix](DIYChefFix/) | Companion for the third-party **DIYChef FastInit 2.4.0** (not included): fixes its **default chef** and its **chef grid**. DIYChef only accepts chefs your save has unlocked, checks once at startup, and falls back to a DLC chef (Snowman) you may not have. That breaks the default chef ("Invalid default chef") and leaves the grid empty, so it never opens. Now the default is re-checked as unlocks settle and the fallback is a chef you have. The grid lists your `prefer.txt` chefs first, then every other unlocked chef and every custom chef in `Resources\`. Optionally (on by default) still-locked base-game chefs can be picked; DLC chefs still need their DLC |
| [TiengViet](TiengViet/) | **Vietnamese** for the game and every mod here, switched with a **LANGUAGE / NGÔN NGỮ** row at the top of **Settings → Game Settings** (English / Tiếng Việt). The change applies instantly, with no restart. All 1,357 game texts are translated: menus, levels, tutorials, story and DLC. ChefSkills, EmoteSync, ModUpdater, OC2Tweaks and RandomLevel follow the same choice. In Vietnamese, titles use a font built into the mod (Coiny by default, six to choose from in MOD Settings), because the game's title font lacks letters such as ạ, ơ and ư. Each player picks their own language |
| [ModUpdater](ModUpdater/) | Keeps the other mods on the newest release: it checks GitHub each time the game starts, downloads a newer release in the background, and installs it when you close the game, so the next launch runs the new versions. Its page in **MOD Settings** shows the status, switches the automatic install off, and has **Kiểm tra** (check) and **Cập nhật** buttons. **Cập nhật** loads the new versions into the running game on the main menu, with no restart; a setting can do that automatically |

**Install:** download `Overcooked2-v*.zip` from the release, extract it anywhere, and double-click `Install.cmd` with the game closed. It finds the game through Steam and then **stays open**, asking what you want, doing it, and asking again with the list refreshed — so you can install one mod, upgrade BepInEx and check the result in one sitting. It closes when you choose **Q**.

```
Which would you like to install?

  1) OC2Tweaks        Settings > OC2 TWEAKS: ping, kick, bans, force host, F5 restart  [installed 3.0.0.0]
  2) RandomLevel      random DIY levels; Continue plays the next random level     [not installed]
  3) OC2DIYLevel      custom level pack + helper plugins (not bundled: ~400 MB)   [installed, 12 level sets]
  4) BepInEx          the mod loader itself (BepInEx 5 x86)                       [installed 5.4.23.6]

  A) all of them        Q) quit

Choose, e.g. 1,3 [A]:
```

Press Enter for everything, or pick items by number or name; the `[installed]` markers update after each round. `Uninstall.cmd` works the same way but only lists what's actually installed, and closes itself once there's nothing left. Mods go in `BepInEx\plugins\<Mod>\`; removing one leaves your settings and ban list alone.

Because the menu keeps running, the game doesn't have to be closed when you start it: if the game is open when you pick something, it says so, changes nothing, and returns to the menu — close the game and choose again.

To do one thing and exit instead (these never loop, so they're safe in scripts):

| Command | What it does |
|---|---|
| `Install.cmd -All` | all available mods and BepInEx, no prompts, once |
| `Install.cmd -Only OC2Tweaks` | one mod (BepInEx is added too if it's missing, since mods don't load without it) |
| `Install.cmd -Only RandomLevel` | random DIY rotation (install OC2DIYLevel first) |
| `Install.cmd -Only BepInEx` | install or upgrade just the loader |
| `Install.cmd -Only BepInEx -BepInExVersion 5.4.23.2` | pin a specific BepInEx |
| `Uninstall.cmd -Only OC2Tweaks` | remove one mod |
| `Uninstall.cmd -All` | remove all mods and the loader |
| `Install.cmd -Update` | upgrade only the mods already installed, plus ModUpdater (what ModUpdater runs after the game closes) |

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

**One deliberate difference from the pack's own instructions.** Its `READ ME` tells you to delete your whole `BepInEx\plugins` folder and drop the new one in, to avoid stale level files conflicting. That would also delete OC2Tweaks, RandomLevel and anything else you'd installed. So the installer clears **only the pack's own entries** before copying — which is what actually prevents those conflicts — and records what it copied in `BepInEx\config\com.manhl.overcooked2.diypack.manifest`. Removing the pack takes exactly those entries back out and leaves everything else, including the pack mods' own settings, alone.

Custom levels have to match across the match: everyone playing needs the same pack.

Only the host needs OC2Tweaks or RandomLevel.

## RandomLevel

In **More Levels**, choose **Chơi ngẫu nhiên — tất cả màn**, then pick your usual DIY save slot. During a round, **F8** skips straight to another level without recording a score for the unfinished round; no panel is shown during gameplay. The results screen shows just **F7 — Về menu chính** as a key hint. **Continue** goes straight to another random custom level, **Restart** replays the current level, and **F7** returns to the main menu once the results animation finishes. The regular controller buttons still work for Continue and Restart.

Levels are drawn from all installed sets without repeats within each shuffled cycle; cycle boundaries also avoid the level just played. Choosing a specific DIY level returns to normal play. The host chooses the next scene and the game waits for everyone's Continue as usual. Requires **OC2DIYLevel 0.10.0**; everyone still needs the same custom-level pack. See [RandomLevel/README.md](RandomLevel/README.md) for build commands, settings and the in-game test checklist.

## Original custom level: Relay Kitchen

**Bếp Tiếp Sức — Chợ Đêm / Night Market Relay 2.0** is a separate OC2DIYLevel map in
**GameMods Originals**: a six-minute kitchen for 1–4 players, with nine burger, pizza
and sushi recipes, two opposing conveyor lanes, two crossings, native floor materials,
and preparation, cooking, washing and serving on both banks.
See [RelayKitchen/README.md](RelayKitchen/README.md) for the layout,
build and install instructions, attribution, and current playtest status.

## ModUpdater

Install it once with the installer (it is in the list like the other mods) and later releases arrive by themselves: when the game starts, it asks GitHub for the newest `Overcooked2-v*` release, and if that is newer than the one you installed it downloads it in the background. You can load it straight away from the main menu with **Cập nhật** in MOD Settings, with no restart (or turn on **Cập nhật ngay khi tải xong** to have that happen by itself). When you close the game, that release's own installer runs with `-Update`, upgrading exactly the mods you have (it never adds one you didn't pick). Nobody needs to download a zip or run `Install.cmd` again. See [ModUpdater/README.md](ModUpdater/README.md) for the settings and the files it keeps.

## ChefSkills

Right after the loading screen (and after any intro cutscene), before **Ready… Go!**, the kitchen stays frozen and a **CHOOSE YOUR SKILLS** dialog appears with one card per player. Online, that includes the other PCs' players, whose cards show their build and whether they've locked in. **Up/Down** picks a skill, **Left** lowers it to free a point, **Right** raises it (even past what you have freed), and **Select** locks the build in only when it adds up to exactly zero; **Back** unlocks it. There is no timer: the level starts only when every player has locked in, or when someone holds the game's **Skip** button for a second, like skipping a cutscene (the host's skip counts for everyone). Each player's last build is pre-selected on the next level.

Online, everyone in the lobby needs Chef Skills: if anyone doesn't have it, each level is played the normal way, with no skill screen and no skill changes for anybody. With everyone on board, running and dashing apply on each player's own PC, and the rest on the host, which matches each joining player's chef to their build. ChefSkills 2.0.0 replaces ChefRoles 1.0.0 (Cook / Server roles), and the installer removes the old plugin. See [ChefSkills/README.md](ChefSkills/README.md) for every skill's numbers and how to tune them, and [ChefSkills/SPECIAL_SKILLS.md](ChefSkills/SPECIAL_SKILLS.md) for the special skills. Chef Skills 4.1.0 changes the online protocol again, so everyone needs 4.1.0 (a lobby mixing 4.0.0 and 4.1.0 plays without skills).

## OC2Tweaks

LobbyTools and QuickKeyRestart were separate mods until OC2Tweaks 2.0.0 merged them, so there is one config file, one place to see both hotkeys, and somewhere in game to turn the restart key off. Choosing OC2Tweaks in the installer carries your old settings, ban list and rebound keys over and removes the two old plugins — [OC2Tweaks/README.md](OC2Tweaks/README.md#upgrading-from-lobbytools--quickkeyrestart) lists exactly what moves where.

**Using it:** on the main menu (or in a lobby), open **Settings → OC2 TWEAKS**. It is a screen built from the game's own Game Settings screen, so it works with mouse, keyboard or pad like the rest of the menu; **Z / C** (LB / RB) switch tabs — the caps beside the tabs show the game's LB / RB icons while you use a pad — **Esc** or **XONG** closes it, and every change is saved as you make it. The line under the list explains whichever row is selected. Tabs:

- **Tùy chọn** (options): force host (public Arcade/Versus matchmaking creates your lobby instead of joining someone else's; off by default), the corner ping overlay and its corner, and quick restart on/off.
- **Phím tắt** (hotkeys): select a hotkey and press any key — hold Ctrl/Shift/Alt for a combination, Esc to cancel. Restart (**F5**), overlay (**F9**), and the tab keys (**Z / C**).
- **Đã cấm** (banned): the saved list; select an account to **Bỏ cấm** (unban).

**Kick and ban:** the game's own player cards (pause menu, round results) get a working **Kick** for the host, offering **Đuổi** (kick; they can rejoin) or **Cấm...** (ban: saves their Steam ID, kicks them, and kicks that account automatically whenever it turns up in a lobby you host). Both ask first, with **Hủy** (cancel) selected. Steam IDs are never shown. Players sharing one remote machine share a connection, so they leave together.

**Ping** is on the corner overlay (**F9**, or ticked in **Tùy chọn**), your own line reading **Bạn**. It is display only: it never filters matchmaking and never kicks anyone for a bad connection. With no measurement yet a line shows **đang đo...** (measuring); a figure with no fresh reply for 3 seconds keeps its value and turns grey. Clients see everyone's ping only when the host runs OC2Tweaks too.

**Quick restart:** press **F5** while a level is running and it restarts immediately — the same thing **Pause → Restart → yes** does, without the menu or the dialog. It deliberately ignores the key outside a running level (main menu, lobby, campaign map, loading screens), because restarting mid-scene-change is what strands a match on a black screen, and ignores a second press within 3 seconds. Each case says so in the BepInEx log.

**Settings and ban list:** `BepInEx\config\com.manhl.overcooked2.tweaks.cfg` and `…tweaks.bans.tsv`. Both survive uninstalling. The F5 and F9 defaults stay clear of Host Force Theme's F1/F2 and RandomLevel's F7/F8.

For the Steam Mono build only (Unity 2017, 32-bit, BepInEx 5 **x86**). Players joining you don't need OC2Tweaks or RandomLevel. See each mod's README for details and in-match test checklists.
