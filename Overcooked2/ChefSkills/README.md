# Chef Skills

At the start of every level, each player shapes their chef by trading levels between nine skills. Raising a skill costs a point, and points only come from lowering another skill: you can raise past what you've freed while you work out a build, but you can only lock it in when it adds up to exactly zero. A chef who changes nothing plays the normal game.

Since 4.0.0, each player also picks **special skills**: one active (used with your **skill key**, EMOTE unless you change it), two passives and an optional quirk. A quirk is a drawback that frees skill points. There are 112 of them, from Blink and Pack Mule to Hot Hands, Rally Cry and Heavy Boots. See **[SPECIAL_SKILLS.md](SPECIAL_SKILLS.md)** for the full list and the rules.

Every special has its own picture (original cartoon icons in the game's sticker style, drawn by `Art/make_icons.py` and built into the DLL), on the skill screen, the skill bar, the status chips and the details panel. In the kitchen, a **skill bar** at the bottom of the screen shows each of your chefs' picks: a round button for the active (its icon, a cooldown sweep with the seconds left, a ring while its effect lasts, and the key that uses it) and small icons for the passives and quirk (with their recharge or count when they have one). When a press can't be used, the button shakes and says why just above it (**NO COUNTER IN FRONT**, **NOT READY**...). What your specials are doing right now (**STREAK +20%**, **POCKET: TOMATO**, **ZEN READY**, **FRENZY +2 (6S)**...) shows as glowing chips in the special's colour, with its icon letters, in a row above the bar; a chip pops and flashes when it appears or changes. Press **Tab** (your **INFO KEY**) for a see-through card at the side of the screen listing your picks with what each one does, its cooldown and its slot; press it again to close it. Each player opens only their own. Each player also chooses where their bar goes (the card's **DISPLAY** row): along the bottom (several players' bars spread apart, each tagged P1, P2... and underlined in that player's colour), small over their chef's head, or hidden. The bar, the panel and the effects all hide while the pause menu, a dialog or the round results are on screen. Fortune Teller's next order appears as a see-through card with a purple **NEXT ORDER** tag at the end of the order bar, and Bounty Hunter's order gets a glowing gold outline and a **BOUNTY x2** tag on its bottom edge, so every player can see it. In the kitchen itself, every special that goes off or works on something shows it to everyone: a burst and its name over the chef, coloured rings under the pots, chefs or items it is affecting, beams for teleports and pulls, and short words such as **x2** or **SAVED!**. The colour tells you the kind of special (blue movement, orange carrying, green prep, red cooking, gold orders, pink team, purple buffs).

Chef Skills 2.0.0 replaces **Chef Roles** 1.0.0, which offered three fixed roles (Cook, Server, no role). The installer removes the old `ChefRoles` plugin when it installs this one.

## The screen

The screen opens right after the loading screen, and after any intro cutscene, before **Ready… Go!**. The kitchen stays frozen, dimmed, behind it. It isn't a BepInEx window: it is built from the game's own UI art and fonts, the same ones the in-game **Mode Settings** and **Controls** dialogs use:

- the large dialog panel, with a **CHOOSE YOUR SKILLS** header plate and a **READY 2/3** tag counting who has locked in
- one taped card per player in the game, showing:
  - the chef hat in that player's chef colour
  - **BALANCED**, how many points are still to spend, or (in red) how many you are over
  - all nine skills, each with level pips (red below 0, green above) and what that level changes, e.g. **+24%**
  - the special picks under them: **ACTIVE**, **PASSIVE**, **PASSIVE** and **QUIRK** (a quirk shows the points it frees, e.g. **HEAVY BOOTS +2**)
  - on your own cards, the row you're changing highlighted between your coloured chef-select arrows, with a line explaining it
  - a green **READY!** badge once that player has locked in
- the game's control bar along the bottom, with the real button icons for Player 1's keyboard or controller

| Control | Effect |
|---|---|
| **Up / Down** | pick a skill or special row |
| **Left** | lower the skill one level, freeing a point |
| **Right** | raise the skill one level, spending a point. You can go over what you have freed (the card says **1 POINT OVER**); lower something else to pay for it before locking in. |
| **Select** on an **ACTIVE**, **PASSIVE** or **QUIRK** row (or click it) | open the picker: every choice for that slot as icon tiles in a grid, grouped by category (Movement, Carrying & Throwing, Prep Stations...). Move with the directions (or point with the mouse; the wheel scrolls), **Select** (or click) picks, **Back** (or right-click) closes. The strip at the bottom says what the highlighted one does, and why it can't be picked when it can't: **TAKEN** (another player has a one-per-kitchen special) or **CLASH** (it doesn't go with another of your picks). With several players on one PC, each player's picker stays in their own part of the screen, over their own card, and nothing is dimmed, so the others keep choosing while it is open |
| **Lock in**, on any row | lock in your build, only when it adds up to exactly zero (the card says **BALANCED**) and no "one per kitchen" special you picked is already taken. It is Enter on the keyboard and X on a controller; a player on half of a shared controller gets a free button on their half instead. Every card shows its own player's lock-in button, and it is never that player's Select |
| **Back** | unlock it to change again |
| **Select** on the **SKILL KEY** row | set the key or button that uses your active: press it next. Any key or button works, including shoulders, triggers, the d-pad and stick clicks; one the game also uses is still accepted, and the card says **(ALSO A GAME BUTTON)**. Keyboard: Esc cancels, Backspace goes back to EMOTE. Controller: Start cancels, Y goes back to EMOTE. Keyboard players set the keyboard key (`SkillKey`) and controller players the controller button (`SkillButton`), so neither overwrites the other; both are also in **MOD Settings** |
| **Select** on the **INFO KEY** row | set the key or button that shows or hides your skill details in the kitchen (Tab on the keyboard unless changed, no controller button until you set one), the same way. Backspace, or pressing the controller button already set, clears it. It can't be the same as your skill key. Also in **MOD Settings** (`DetailsKey`, `DetailsButton`) |
| **Left / Right** (or Select) on the **DISPLAY** row | where your skill bar shows in the kitchen: **ON SCREEN** (along the bottom), **OVER HEAD** (small, over your chef) or **HIDDEN**. Each player on the PC has their own (`[Display] SkillBarPlayer1`..`4`) |
| **Hide** (Y on a controller, Ctrl or **Tab** on the keyboard) | hide the screen to look at the kitchen and plan; press it again, or Select, Lock in or Back, to bring it back |
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
- `[Controls] SkillKey` and `SkillButton`: the keyboard key and the controller button that use your active special. `None` (the default) is the game's EMOTE button. Any key or button works: `SkillButton` can be `LB`, `RB`, `LT`, `RT`, `Back`, `LS`, `RS`, the d-pad or a face button. Set them by pressing them on the skill screen's **SKILL KEY** row, or in MOD Settings (**Rebind** for the key, a list for the button). Each controller player's own controller is read, and on a controller shared by two players only the player whose half has that button uses it. The right-hand player of a shared keyboard keeps EMOTE. A controller button saved in `SkillKey` by an older version moves to `SkillButton` by itself.
- `[Controls] DetailsKey` and `DetailsButton`: the keyboard key (default `Tab`) and the controller button (default none; Back or a stick click work well) that show or hide **your own** skill details card during a level. Set them on the skill screen's **INFO KEY** row, or in MOD Settings.
- `[Display] SkillBarPlayer1` to `SkillBarPlayer4`: where each player on this PC sees their skill bar: `OnScreen` (default), `AboveHead` or `Hidden`. Players are counted in player order on this PC, so online the one player here is always player 1. Also set on the skill screen's **DISPLAY** row.

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

**Dashes are decided by the player's own PC.** In the unmodded game, the host and the player's PC each decide whether a dash press starts a dash: a press made while the dash is still recharging is used up, the host also refuses while the chef is being bumped (which the player's PC never sees), and a quick tap whose press and release reach the host together never reaches it at all. Whenever they disagree, the chef dashes on one PC and not the other, and the player's chef is snapped back a few metres. With Chef Skills on every PC, the player's PC sends the host one short press for each dash its chef actually made, and the host starts that dash without its own recharge or bump checks and without missing a tap. While the chef's normal controls are off (in a cannon, steering a vehicle), the dash button is sent as it is. A stronger **Dashing** makes the dash go further but doesn't knock other chefs harder, since a knock only happens on the host and the knocked player's PC has to snap their chef to match.

All PCs use **the host's numbers**: the host writes its `MaxLevel` and `PercentPerLevel` settings into the lobby, and everyone else's screen and skills use them in place of their own config. So a build means the same speed on every PC, and a build that spends more points than it frees counts as no changes everywhere. The host's `[Specials]` switches apply the same way. A PC running a different Chef Skills version counts as not having the mod.

**Special skills online.** Each PC writes its players' picks into the same lobby member data as the levels. Anything that has to arrive at once, like an active being used or a speed buff starting, goes over Chef Skills' own Steam P2P channel (7), routed through the host the way the game routes its own messages. Pressing the skill key sends the press, with where your chef stands and faces on your PC, to the host, which checks the cooldown and that there is something to use it on (or sends back why not), does it, and tells every PC so all of them show the same cooldowns and apply the same buffs. See [SPECIAL_SKILLS.md](SPECIAL_SKILLS.md).

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
3. Up/Down moves the highlight and the line under the skills explains it. Right on a fresh card raises the skill and the card says **1 POINT OVER** in red; Lock in (X or Enter) then refuses and flashes it. Left on another skill pays for it (**BALANCED**), and Lock in locks in. Left on its own frees a point (**1 POINT TO SPEND**), which also blocks locking in until spent. Waiting a minute without locking in does not start the level.
4. With **Running** and **Dashing** raised, the chef visibly runs faster and dashes further and more often. With **Cooking** +3, a pot you filled finishes sooner than one filled by a chef at 0. With **Slow burn** −3, finished food burns noticeably sooner.
5. **Chopping**, **Washing** (the bar fills faster or slower, and the plate pops as it fills), **Serving**, **Throwing** and **Firefighting** each change what they say, raised and lowered.
6. The next level starts with your last build pre-selected. Holding Skip (B on a controller) fills the bar under **HOLD: SKIP** and closes the screen after a second, with no skill changes for that level. A quick tap of B only undoes a lock-in.
7. Pausing and quitting or restarting during and after the screen works normally.
8. Online, with Chef Skills on both PCs: each screen shows both players' cards. The other player's pips change as they press Left/Right and the card turns **READY!** when they lock in. The level starts on both PCs only after both have locked in, and the log shows `Đồng bộ chọn kỹ năng: lượt N` with the same N on both.
9. Online, the client raises **Chopping** to +3: on the host's game the client's chef chops visibly faster, and on **both** PCs the chopping bar is full exactly when the item turns into the chopped one. The same for **Washing** (the bar fills as the clean plate appears, on both PCs, also with two chefs at one sink) and **Cooking** in a pot the client filled.
10. Online, the client raises **Running** and **Dashing** to +3: the client's chef runs and dashes faster on both PCs, without being pulled back or stuttering, also while dashing repeatedly. With a different `PercentPerLevel` in the client's config, the client's screen shows the host's percentages.
10a. Online, as the client, mash dash (also right as it recharges, with quick taps, and while another chef is bumping you): every dash you see on your PC also happens on the host's screen, and your chef is never snapped back after one. Dashing into the host's chef with **Dashing** +3 knocks it no harder than at 0. Getting into a cannon and pressing dash still gets you back out of it.
10b. Special skills, offline first and then online as the client:
   - Select on a special row opens the picker grid; clashing picks (for example Heavy Boots with Sprinter) show CLASH and can't be picked, the current pick shows CURRENT, and a mouse click opens and picks too.
   - A quirk adds its points to the card.
   - The skill bar at the bottom shows your active with its cooldown sweep and key, and your passives; a press with nothing to use it on shakes the button and names the reason.
   - Tab slides your details card in at the left edge (each pick, what it does, cooldown, slot) and slides it back out; a second local player's own info key opens theirs at the right edge without touching yours. The pause menu hides the bars, the cards and the effects.
   - Two or more local players: the on-screen bars stand well apart, each with a P1/P2 tag and a line in that player's colour. Set one player's **DISPLAY** to **OVER HEAD**: their small bar follows their chef while the other's stays at the bottom; **HIDDEN** removes it. One player opening a picker leaves the other's card visible and usable.
   - On half of a shared controller, the log line `Người chơi N: tay cầm ...` at the skill screen lists that player's select, back, hide and lock-in buttons, and lock-in and select are different.
   - Fortune Teller: a see-through NEXT ORDER card sits after the last order on the bar and turns into the real order when it arrives. Bounty Hunter: the bounty order has a glowing gold outline and a BOUNTY x2 tag, on every machine.
   - Set the **SKILL KEY** row to a key (keyboard) and to RT or LB (controller) and use the active with them; the emote button then only emotes. A key the game uses (Space, W) is accepted with **(ALSO A GAME BUTTON)**.
   - **Hide** (Y / Tab) hides the screen and shows the kitchen; pressing it again, Select, Lock in or Back brings it back with nothing changed.
   - Vault over a counter, a tall unit and a wall (it lands on the first safe floor up to 4 m behind), and Blink 6 m through counters and walls, as the host and as the client. A refusal names its reason (NOTHING TO VAULT OVER, NO FLOOR BEHIND IT, NO ROOM TO LAND, ONLY PITS AHEAD, EDGE OF THE KITCHEN); a refused Blink also logs a "BLINK:" line with what it rejected.
   - Sizzle Master: your pot shows a ring and **x2** while it cooks faster, on both PCs; a teammate's pot doesn't.
   - Try one special from each module: Blink (Teleport), Sprinter (Movement), Pack Mule (Carrying), Homing Throw (Throwing), Power Chop (Stations), Fire Blast (Cooking), Charmer (Orders) and Rally Cry (Team buffs). Check each does what [SPECIAL_SKILLS.md](SPECIAL_SKILLS.md) says on both PCs, with no snapping back.
   - With nobody picking any specials, the level plays exactly as before.
   - `LogOutput.log` lists `Kỹ năng đặc biệt: 8 nhóm` and no `Không áp dụng được` warnings.
11. Online, play a second level in a row and restart one: the screen waits for everyone again each time.
12. Online, the host's legend says **HOLD: SKIP FOR ALL**, and holding it closes the screen on both PCs. A client's hold closes only the client's screen, and the host's screen shows that client as **READY!**.
13. Online, with a player who doesn't have the mod (try either the host or a client without it): no skill screen opens on any PC, the level starts straight into **Ready… Go!**, every chef runs and works at normal speed, and the modded PCs' logs say `Không phải ai trong phòng cũng cài Chef Skills`.

If the game's dialog art hasn't loaded within 5 seconds, the log says `Chưa tải được hình giao diện của game…` and the screen falls back to plain coloured panels, with the same controls. If the screen fails outright, the log says `Màn chọn kỹ năng bị lỗi…` and the level starts normally with no skill changes.
