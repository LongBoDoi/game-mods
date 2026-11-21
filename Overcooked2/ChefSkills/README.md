# Chef Skills

At the start of every level, each player shapes their chef by trading levels between nine skills. Raising a skill costs a point, and points only come from lowering another skill: you can raise past what you've freed while you work out a build, but you can only lock it in when it adds up to exactly zero. A chef who changes nothing plays the normal game.

Chef Skills 2.0.0 replaces **Chef Roles** 1.0.0, which offered three fixed roles (Cook, Server, no role). The installer removes the old `ChefRoles` plugin when it installs this one.

## The screen

The screen opens right after the loading screen, and after any intro cutscene, before **Ready… Go!**. The kitchen stays frozen, dimmed, behind it. It isn't a BepInEx window: it is built from the game's own UI art and fonts, the same ones the in-game **Mode Settings** and **Controls** dialogs use:

- the large dialog panel, with a **CHOOSE YOUR SKILLS** header plate and a **READY 2/3** tag counting who has locked in
- one taped card per player in the game, showing:
  - the chef hat in that player's chef colour
  - **BALANCED**, how many points are still to spend, or (in red) how many you are over
  - all nine skills, each with level pips (red below 0, green above) and what that level changes, e.g. **+24%**
  - on your own cards, the skill you're changing highlighted between your coloured chef-select arrows, with a line explaining it
  - a green **READY!** badge once that player has locked in
- the game's control bar along the bottom, with the real button icons for Player 1's keyboard or controller

| Control | Effect |
|---|---|
| **Up / Down** | pick a skill |
| **Left** | lower it one level, freeing a point |
| **Right** | raise it one level, spending a point. You can go over what you have freed (the card says **1 POINT OVER**); lower something else to pay for it before locking in |
| **Select** | lock in your build, only when it adds up to exactly zero (the card says **BALANCED**) |
| **Back** | unlock it to change again |
| **hold Skip** for 1 second | skip, the same way you skip a cutscene: the players on this machine play this level with no skill changes. The host's skip ends the screen for everyone. Skip is the game's own button (B on a controller, so a quick tap is still Back; the game's Skip key on a keyboard), and the bar shows its icon with a fill while you hold it |

A move the rules don't allow (going past the top or bottom level, or locking in with points left to spend or overspent) flashes the points line red. There is no time limit: the screen stays open until **every** player has locked in, then shows **LET'S COOK!** and the level starts. Your last build is pre-selected on the next level, and skipping doesn't forget it. One player controlling two chefs (single player) gets one card, and the build applies to both chefs.

## Skills

| Skill | Per level | At +3 / −3 | What it changes |
|---|---|---|---|
| **Cooking** | 12% | +36% / −36% | How fast a pot, pan, tray or mixing bowl cooks or mixes. It goes at the speed of the last chef who put food in it or set it down on the heat or in the mixer |
| **Slow burn** | 20% | +60% / −60% | Once that food is done, how long it lasts before it burns (or over-mixes). +60% lasts 1.6× as long; −60% burns 2.5× as fast |
| **Chopping** | 12% | +36% / −36% | Chopping strokes count 1.36× (or 0.64×, etc.) toward each slice |
| **Washing** | 15% | +45% / −45% | How fast you wash plates at a sink |
| **Serving** | 15% | +45% / −45% | When you hand in a dish at the pass, how soon that dish's dirty plate comes back. Thrown dishes use the normal time |
| **Running** | 6% | +18% / −18% | Walking/running speed (not dash) |
| **Dashing** | 15% | +45% / −45% | Dash speed (so distance) and how soon the dash recharges |
| **Throwing** | 12% | +36% / −36% | How hard, and so how far, you throw |
| **Firefighting** | 20% | +60% / −60% | How fast your fire extinguisher puts fires out |

Everything is in `BepInEx\config\com.manhl.overcooked2.chefskills.cfg`:

- `[General]`: `Enabled`, and `MaxLevel` (1–5, default 3), the highest and lowest level any skill can go.
- `[Skill.<name>]`: `PercentPerLevel` (0–50), what one level is worth. 0 turns a skill's effect off. A skill never drops below 10% of normal speed, whatever the numbers.

The screen's pips and percentages are built from those numbers. Online, everyone uses the host's numbers (see below). Chef Roles' old `com.manhl.overcooked2.chefroles.cfg` isn't used any more; it's safe to delete.

## Online

Everyone's screen shows every player in the game, not just the ones on that PC:

| Card says | Meaning |
|---|---|
| your highlighted skill, arrows and **LOCK IN** | one of your players, still choosing |
| **CHOOSING...** | another player, still choosing (their pips update live as they change them) |
| **READY!** | locked in |
| **LOADING...** | that player's game is still loading the level |

**Everyone in the lobby needs Chef Skills.** If any player doesn't have it (or has it turned off with `Enabled = false`, or still has Chef Roles 1.0.0), the level is played the normal way on every PC: the screen doesn't open, and nobody's skills change, so nobody's chef behaves differently from what a player without the mod sees. Every PC checks this for itself before opening the screen. The host also tells the others, so their screens close if they opened anyway, and it applies no joining player's build for that level. The log says `Không phải ai trong phòng cũng cài Chef Skills (thiếu: <names>)`. The check runs again at every level, so skills come back once that player leaves or installs the mod.

Nobody's level starts until every player with Chef Skills has locked in. If someone is away, the host can **hold Skip** to start the level for everyone with no skill changes. A client holding Skip only skips their own players (they count as ready). Until the host's game has opened the screen, a client's tag says **WAITING FOR HOST**. Once the host's level has started, a client who is locked in can't unlock again.

Skills apply to joining players too, not just the host's chefs, and **every PC applies every player's build**. The host decides what really happens, but the game also runs parts of it on each PC, and those have to run at the same speed or the PCs disagree:

| Skills | Where they run |
|---|---|
| Running, dashing | on the host, which moves every chef from its player's buttons, **and** on the player's own PC, which moves its chef straight away and is then corrected to where the host has it. Both use the player's build, so a faster chef isn't pulled back |
| Chopping, washing | on the host, which decides when the item is chopped or the plate is clean, **and** on every PC, which fills the progress bar itself. Both use the chopping or washing chef's build, so the bar fills exactly when the item finishes |
| Cooking, slow burn, serving, throwing, firefighting | on the host only; the other PCs show what the host sends |

This needs no network message of its own. Each PC writes its players' skill levels, lock-in state and their chefs' network entity IDs into its own Steam lobby member data, a small key/value store Steam keeps for each lobby member and sends to the others. The game doesn't use it, so players without the mod are unaffected. The host also writes a round number into the lobby each time its screen opens, so what a PC wrote during the previous level never counts as ready. The game gives each chef the same entity ID on every PC, so every PC matches each other player's chef to the build they picked, once they have locked it in. Every PC keeps reading those builds during the level.

All PCs use **the host's numbers**: the host writes its `MaxLevel` and `PercentPerLevel` settings into the lobby, and everyone else's screen and skills use them in place of their own config. So a build means the same speed on every PC, and a build that spends more points than it frees counts as no changes everywhere. A PC running an older Chef Skills counts as not having the mod.

## Build

No .NET SDK is needed: `build.cmd` uses the C# compiler that ships with Windows, and compiles against the game's own `mscorlib`.

```
ChefSkills\build.cmd            build and stage in dist\
ChefSkills\build.cmd deploy     also install into the game (close the game first)
powershell -NoProfile -File tests\CheckChefSkills.ps1
```

`CheckChefSkills.ps1` reads the built DLL and checks that every Harmony patch target, patch parameter name and private field it reaches by name exists in the installed game. The compiler can't check those, and a wrong one makes the plugin turn itself off when the game starts.

## In-game test checklist

Not yet verified in the game. Check these on the first run:

1. `LogOutput.log` shows `Đã tải Chef Skills`, with no `Không thể bật Chef Skills` error. The old `BepInEx\plugins\ChefRoles` folder is gone after installing.
2. Load any campaign or arcade level. The **CHOOSE YOUR SKILLS** dialog appears after loading, with one card per player (hat in your chef's colour), nine skill rows and a control bar with button icons.
3. Up/Down moves the highlight and the line under the skills explains it. Right on a fresh card raises the skill and the card says **1 POINT OVER** in red; Select then refuses and flashes it. Left on another skill pays for it (**BALANCED**), and Select locks in. Left on its own frees a point (**1 POINT TO SPEND**), which also blocks Select until spent. Waiting a minute without locking in does not start the level.
4. With **Running** and **Dashing** raised, the chef visibly runs faster and dashes further and more often. With **Cooking** +3, a pot you filled finishes sooner than one filled by a chef at 0. With **Slow burn** −3, finished food burns noticeably sooner.
5. **Chopping**, **Washing** (the bar fills faster or slower, and the plate pops as it fills), **Serving**, **Throwing** and **Firefighting** each change what they say, raised and lowered.
6. The next level starts with your last build pre-selected. Holding Skip (B on a controller) fills the bar under **HOLD: SKIP** and closes the screen after a second, with no skill changes for that level. A quick tap of B only undoes a lock-in.
7. Pausing and quitting or restarting during and after the screen works normally.
8. Online, with Chef Skills on both PCs: each screen shows both players' cards. The other player's pips change as they press Left/Right and the card turns **READY!** when they lock in. The level starts on both PCs only after both have locked in, and the log shows `Đồng bộ chọn kỹ năng: lượt N` with the same N on both.
9. Online, the client raises **Chopping** to +3: on the host's game the client's chef chops visibly faster, and on **both** PCs the chopping bar is full exactly when the item turns into the chopped one. The same for **Washing** (the bar fills as the clean plate appears, on both PCs, also with two chefs at one sink) and **Cooking** in a pot the client filled.
10. Online, the client raises **Running** and **Dashing** to +3: the client's chef runs and dashes faster on both PCs, without being pulled back or stuttering, also while dashing repeatedly. With a different `PercentPerLevel` in the client's config, the client's screen shows the host's percentages.
11. Online, play a second level in a row and restart one: the screen waits for everyone again each time.
12. Online, the host's legend says **HOLD: SKIP FOR ALL**, and holding it closes the screen on both PCs. A client's hold closes only the client's screen, and the host's screen shows that client as **READY!**.
13. Online, with a player who doesn't have the mod (try either the host or a client without it): no skill screen opens on any PC, the level starts straight into **Ready… Go!**, every chef runs and works at normal speed, and the modded PCs' logs say `Không phải ai trong phòng cũng cài Chef Skills`.

If the game's dialog art hasn't loaded within 5 seconds, the log says `Chưa tải được hình giao diện của game…` and the screen falls back to plain coloured panels, with the same controls. If the screen fails outright, the log says `Màn chọn kỹ năng bị lỗi…` and the level starts normally with no skill changes.
