# Chef Skills

At the start of every level, each player shapes their chef by trading levels between nine skills. Raising a skill costs a point, and points only come from lowering another skill: you can raise past what you've freed while you work out a build, but you can only lock it in when it adds up to exactly zero. Lowering skills frees **at most 6 points**, so a chef can't max every skill they use by dumping all the ones they don't. A quirk's points come on top. A chef who changes nothing plays the normal game.

Since 4.0.0, each player also picks **special skills**: one active (used with your **skill key**, EMOTE unless you change it), two passives and an optional quirk. A quirk is a drawback that frees skill points. There are 112 of them, from Blink and Pack Mule to Hot Hands, Rally Cry and Heavy Boots. See **[SPECIAL_SKILLS.md](SPECIAL_SKILLS.md)** for the full list and the rules.

Every special has its own picture (original cartoon icons in the game's sticker style, drawn by `Art/make_icons.py` and built into the DLL), on the skill screen, the skill bar, the status chips and the details panel. In the kitchen, a **skill bar** at the bottom of the screen shows each of your chefs' picks: a round button for the active (its icon, a cooldown sweep with the seconds left, a ring while its effect lasts, and the key that uses it) and small icons for the passives and quirk (with their recharge or count when they have one). When a press can't be used, the button shakes and the reason floats up over your chef (**NO COUNTER IN FRONT**, **NOT READY**...), at the size of every other floating word whatever the bar's size. What your specials are doing right now (**STREAK +20%**, **POCKET: TOMATO**, **ZEN READY**, **FRENZY +2 (6S)**...) shows as glowing chips in the special's colour, with its icon letters, in a row above the bar; a chip pops and flashes when it appears or changes. Press **Tab** (your **INFO KEY**) for a see-through card at the side of the screen listing your picks with what each one does, its cooldown and its slot; press it again to close it. Each player opens only their own. Each player also chooses where their bar goes (the **DISPLAY** row of the card's **SETTINGS**): along the bottom (several players' bars spread apart, each tagged P1, P2... and underlined in that player's colour; each player can also make theirs bigger or smaller and put it anywhere on the screen), small over their chef's head (only the active while it is ready or running, and a passive while it shows a timer or count, so the stations around the chef stay in view), or hidden. The bar, the panel and the effects all hide while the pause menu, a dialog or the round results are on screen. Fortune Teller's next order appears as a see-through card with a purple **NEXT ORDER** tag at the end of the order bar, and Bounty Hunter's order gets a glowing gold outline and a **BOUNTY x1.5** tag on its bottom edge, so every player can see it. In the kitchen itself, every special that goes off or works on something shows it to everyone: a burst and its name over the chef, coloured rings under the pots, chefs or items it is affecting, beams for teleports and pulls, and short words such as **x2** or **SAVED!**. The colour tells you the kind of special (blue movement, orange carrying, green prep, red cooking, gold orders, pink team, purple buffs).

Chef Skills 2.0.0 replaces **Chef Roles** 1.0.0, which offered three fixed roles (Cook, Server, no role). The installer removes the old `ChefRoles` plugin when it installs this one.

## The screen

The screen opens right after the loading screen, and after any intro cutscene, before **Ready… Go!**. The kitchen stays frozen, dimmed, behind it, and can't be used while the screen is up: Start / Esc doesn't open the pause menu (the press is thrown away) and no emote wheel opens. It isn't a BepInEx window: it is built from the game's own UI art and fonts, the same ones the in-game **Mode Settings** and **Controls** dialogs use:

- the large dialog panel, with a **CHOOSE YOUR SKILLS** header plate and a **READY 2/3** tag counting who has locked in
- one taped card per player in the game, showing:
  - the chef hat in that player's chef colour
  - **BALANCED**, how many points are still to spend, or (in red) how many you are over, with **MAX 6 FREED** once lowering skills can't free any more
  - all nine skills, each with level pips (red below 0, green above) and what that level changes, e.g. **+24%**. A skill only has as many red pips as it can really go down: three, or none for a skill a quirk takes away
  - the special picks under them as one row of icons, in slot order, each kind in its own style: the active biggest, a disc with a gold ring and glow; the passives smaller discs with a green ring; the quirk a rounded square with a red frame and the points it frees (e.g. **+2**). An empty slot shows its faded outline with a **+**
  - on your own cards, an **CHOOSE SKILLS** button under the icons and a **SETTINGS** button under that. Other players' cards show only their skills and special icons, with their specials named under the rows
  - on your own cards, the row you're changing highlighted between your coloured chef-select arrows, with a line explaining it
  - a green **READY!** badge once that player has locked in
- the game's control bar along the bottom, with the real button icons for Player 1's keyboard or controller

| Control | Effect |
|---|---|
| **Up / Down** | pick a skill, **CHOOSE SKILLS** or **SETTINGS** |
| **Left** | lower the skill one level, freeing a point (up to 6 freed in all). A quirk that takes a skill away keeps that skill at 0 or above (Heavy Boots: Dashing; Weak Arm: Throwing; Germaphobe: Washing). Picking such a quirk brings that skill back up to 0 |
| **Right** | raise the skill one level, spending a point. You can go over what you have freed (the card says **1 POINT OVER**); lower something else to pay for it before locking in. |
| **Select** on **CHOOSE SKILLS** (or click it, or click one of the special icons) | open the picker. Along its top are all four slots (**ACTIVE**, **PASSIVE**, **PASSIVE**, **QUIRK**), drawn in the same styles as on the card, with what is in each; **Left / Right** moves between them, and the grid under them shows every choice for the slot in focus as icon tiles, grouped by category (Movement, Carrying & Throwing, Prep Stations...). **Down** or **Select** goes into the grid; there the directions move (Up from the top row goes back to the slots), **Select** picks for that slot and goes back to the slots, and **Back** goes back without picking. **Select** on the slot's current pick (marked **CURRENT**) takes it out again, emptying the slot. **Back** on the slots (or a right-click) closes the picker. With the mouse, click a slot to focus it and a tile to pick it; the wheel scrolls. The strip at the bottom says what the highlighted one does, and why it can't be picked when it can't: **TAKEN** (another player has a one-per-kitchen special) or **CLASH** (it doesn't go with another of your picks). With several players on one PC, each player's picker stays in their own part of the screen, over their own card, and nothing is dimmed, so the others keep choosing while it is open |
| **Select** on **SETTINGS** (or click it) | open the settings panel over your card: **SKILL KEY**, **INFO KEY**, **DISPLAY**, **BAR SIZE** and **BAR POSITION**, below. **Up / Down** picks one, **Back** closes the panel (or click off it, with one player on the PC). **Lock in** waits until the panel is closed |
| **Lock in**, on any row | lock in your build, only when it adds up to exactly zero (the card says **BALANCED**) and no "one per kitchen" special you picked is already taken. It is Enter on the keyboard and X on a controller; a player on half of a shared controller gets a free button on their half instead. Every card shows its own player's lock-in button, and it is never that player's Select |
| **Back** | unlock it to change again |
| **Select** on the panel's **SKILL KEY** row | set the key or button that uses your active: press it next. Any key or button works, including shoulders, triggers, the d-pad and stick clicks; one the game also uses is still accepted, and the card says **(ALSO A GAME BUTTON)**. Keyboard: Esc cancels, Backspace goes back to EMOTE. Controller: Start cancels, Y goes back to EMOTE. Keyboard players set the keyboard key (`SkillKey`) and controller players the controller button (`SkillButton`), so neither overwrites the other; both are also in **MOD Settings** |
| **Select** on the panel's **INFO KEY** row | set the key or button that shows or hides your skill details in the kitchen (Tab on the keyboard unless changed, no controller button until you set one), the same way. Backspace, or pressing the controller button already set, clears it. It can't be the same as your skill key. Also in **MOD Settings** (`DetailsKey`, `DetailsButton`) |
| **Left / Right** (or Select) on the panel's **DISPLAY** row | where your skill bar shows in the kitchen: **ON SCREEN** (along the bottom), **OVER HEAD** (small, over your chef) or **HIDDEN**. Each player on the PC has their own (`[Display] SkillBarPlayer1`..`4`) |
| **Left / Right** (or Select) on the panel's **BAR SIZE** row | make your skill bar smaller or bigger, 50% to 150% (on screen and over your head; `SkillBarSizePlayer1`..`4`) |
| **Select** on the panel's **BAR POSITION** row | with the bar **ON SCREEN**: a preview of your bar appears where it sits; move it with the directions (held, it keeps moving), **Select** or **Lock in** keeps it there, **Back** puts it back, **Hide** returns it to the automatic spot along the bottom (`SkillBarPositionPlayer1`..`4`). The other players carry on meanwhile |
| **Hide** (Y on a controller, Ctrl or **Tab** on the keyboard) | hide the screen to look at the kitchen and plan; press it again, or Select, Lock in or Back, to bring it back |
| **hold Skip** for 1 second | skip, the same way you skip a cutscene: the players on this machine play this level with no skill changes. The host's skip ends the screen for everyone. Skip is the game's own button (B on a controller, so a quick tap is still Back; the game's Skip key on a keyboard), and the bar shows its icon with a fill while you hold it |

A move the rules don't allow (going past the top or bottom level, or locking in with points left to spend or overspent) flashes the points line red; going below a skill's floor also says why (**HEAVY BOOTS: DASHING STAYS AT 0 OR ABOVE**). There is no time limit: the screen stays open until **every** player has locked in, then shows **LET'S COOK!** and the level starts. Your last build is pre-selected on the next level, and skipping doesn't forget it. One player controlling two chefs (single player) gets one card, and the build applies to both chefs.

## Skills

| Skill | Per level | At +3 / −3 | What it changes |
|---|---|---|---|
| **Cooking** | 12% | +36% / −36% | How fast a pot, pan, tray or mixing bowl cooks or mixes. It goes at the speed of the chef who last put food in it or set it down on the heat or in the mixer, so that chef's Cooking and Slow burn decide the pot's speed |
| **Slow burn** | 20% | +60% / −60% | Once that food is done, how long it lasts before it burns (or over-mixes). +60% lasts 1.6× as long; −60% burns 2.5× as fast |
| **Chopping** | 12% | +36% / −36% | Chopping strokes count 1.36× (or 0.64×, etc.) toward each slice. What a stroke adds past the end of an item is kept for your next item, so a level is worth the same on every item |
| **Washing** | 15% | +45% / −45% | How fast you wash plates at a sink |
| **Serving** | 20% | +60% / −60% | When you hand in a dish at the pass, how soon that dish's dirty plate comes back. Thrown dishes use the normal time |
| **Running** | 6% | +18% / −18% | Walking/running speed (not dash) |
| **Dashing** | 10% | +30% / −30% | Dash speed (so distance) **and** how soon the dash recharges, so +3 is about 1.7× as much dashing and -3 about half |
| **Throwing** | 12% | +36% / −36% | How hard, and so how far, you throw |
| **Firefighting** | 20% | +60% / −60% | How fast your fire extinguisher puts fires out |

Specials and buffs can push a skill past its card level (Frenzy, Glass Cannon, Second Wind...), but together they add at most **+2** to a skill (+3 to Running and Dashing) and take away at most 3. Specials that change speed multiply together up to **×1.6** at most, before Running or Dashing.

**Stations show it.** A station working at a raised or lowered skill shows it, in the skill card's colours (green faster, red slower), on every PC:

- **Rings:** a ring on the board, sink, pot, pan or mixing bowl while it works at the chef's rate. A burning station gets one while your extinguisher sprays it. A plate return gets one from the moment you hand a dish in until its plate comes back.
- **Skill label:** the ring's label names the skill and its percentage (**CHOPPING +36%**) for as long as the station works at that rate. It is in smaller letters than the specials' labels, so a few of them stacked stay low over the station.
- **Chop flashes:** on a board, a flash under each stroke the level added (green) or didn't count (red).
- **Sparkles:** a faster sink, pot or fire gives off a sparkle now and then.

Rings marking a station, these and the special skills' ones, lie on its work surface (the counter top, the hob, the sink), not on the floor inside the counter.

**Words stack.** Every word over something in the kitchen (ring labels, floating words, **SWAP?** and the aiming hint) joins the one stack over that thing, shared by the skills and the specials:
- **What shares a stack:** a station and whatever stands on it (a board and its item, a cooker and its pot, a sink), or a chef and whatever they hold. The stack stands centred over the middle of the station (or the chef).
- **Labels stacked:** labels that stay (**CHOPPING +36%**, **x2**, **ECHO**, **SWAP?**) stand one above the other at the bottom, the oldest lowest, each in its own colour. A new one pops in; when one goes, the ones above it slide down.
- **Floating words:** they pop up above the labels and fly up as they fade, the newest lowest; a word never runs into the one above it.
- **On top, but not over menus:** the stack starts above the game's own icons over the same things (ingredient icons, progress bars, cooking dials, emote bubbles, and the over-head skill bar for a chef). On screen the words are drawn over the kitchen's icons, the game's orders, timer and score, and the skill bar. They stay under what is there to be read or chosen: your skill info card, the skill screen and an open emote wheel (words under the wheel fade out until it closes). They hide under the pause menu, dialogs, the round results, tutorial popups, cutscenes and the screen wipe between scenes.

Everything is in `BepInEx\config\com.manhl.overcooked2.chefskills.cfg`:

- `[General]`: `Enabled`. The skill numbers aren't settings: every skill goes from -3 to +3 on the card (specials can push past it), lowering skills frees at most 6 points, and each level is worth what the table above says. A skill never drops below 10% of normal speed.
- A config file from an earlier version still has `MaxLevel`, `MaxPointsFreed`, `BalanceVersion` and `[Skill.<name>]` sections from when the numbers could be changed; the mod removes them from the file on the first start.
- `[Controls] SkillKey` and `SkillButton`: the keyboard key and the controller button that use your active special. `None` (the default) is the game's EMOTE button. Any key or button works: `SkillButton` can be `LB`, `RB`, `LT`, `RT`, `Back`, `LS`, `RS`, the d-pad or a face button. Set them by pressing them on the skill screen (**SETTINGS**, then **SKILL KEY**), or in MOD Settings (**Rebind** for the key, a list for the button). Each controller player's own controller is read, and on a controller shared by two players only the player whose half has that button uses it. The right-hand player of a shared keyboard keeps EMOTE. A controller button saved in `SkillKey` by an older version moves to `SkillButton` by itself.
- `[Controls] DetailsKey` and `DetailsButton`: the keyboard key (default `Tab`) and the controller button (default none; Back or a stick click work well) that show or hide **your own** skill details card during a level. Set them on the skill screen (**SETTINGS**, then **INFO KEY**), or in MOD Settings.
- `[Display] SkillBarPlayer1` to `SkillBarPlayer4`: where each player on this PC sees their skill bar: `OnScreen` (default), `AboveHead` or `Hidden`. Players are counted in player order on this PC, so online the one player here is always player 1. Also set on the skill screen's **DISPLAY** row.
- `[Display] SkillBarSizePlayer1`..`4` (50-150, default 100) and `SkillBarPositionPlayer1`..`4` (`Auto`, or `x,y`: the bar's bottom centre as fractions of the screen from the bottom-left, e.g. `0.15,0.05`). Easiest set on the skill screen's **BAR SIZE** and **BAR POSITION** rows.

Chef Roles' old `com.manhl.overcooked2.chefroles.cfg` isn't used any more; it's safe to delete.

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

Every PC uses **the same numbers**: they are fixed in the mod, so a build means the same speed on every PC, and a build that spends more points than it frees counts as no changes everywhere. The host's `[Specials]` switches are written into the lobby and apply on every PC in place of their own. A PC running a different Chef Skills version counts as not having the mod.

**Special skills online.** Each PC writes its players' picks into the same lobby member data as the levels. Anything that has to arrive at once, like an active being used or a speed buff starting, goes over Chef Skills' own Steam P2P channel (7), routed through the host the way the game routes its own messages. Using the active sends the press, with where your chef stands and faces on your PC and the target you chose (if you held the key to choose one), to the host, which checks the cooldown and that there is something to use it on (or sends back why not), does it, and tells every PC so all of them show the same cooldowns and apply the same buffs. See [SPECIAL_SKILLS.md](SPECIAL_SKILLS.md).

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
3a. Point rules: lower three skills to -3 (9 levels): the card frees only 6 and says **MAX 6 FREED**. Throwing and Firefighting go down to -3 like the rest. Pick Heavy Boots with Dashing at -2: Dashing jumps back to 0 and Left on it is refused.
4. With **Running** and **Dashing** raised, the chef visibly runs faster and dashes further and more often. With **Cooking** +3, a pot you filled finishes sooner than one filled by a chef at 0. With **Slow burn** −3, finished food burns noticeably sooner.
5. **Chopping**, **Washing** (the bar fills faster or slower, and the plate pops as it fills), **Serving**, **Throwing** and **Firefighting** each change what they say, raised and lowered.
5a. With **Chopping**, **Washing**, **Cooking**, **Slow burn**, **Serving** and **Firefighting** raised (then lowered), the station shows a green (red) ring on its top while it works, labelled with the skill and its percentage (**CHOPPING +36%**, or **CHOPPING -36%** lowered) in small letters. A board flashes on each extra (or lost) stroke. Online, the other PC shows the same rings, words and flashes for both players' chefs. With every skill at 0, no station shows anything.
5b. Words never hide the game's icons or each other:
   - **Sizzle Master pot:** fill a pot with **Cooking** raised. **COOKING +36%** (green, small) and the pot's **x2** (bigger) stand one above the other above the pot's ingredient icons and cooking dial, never across them or behind anything.
   - **Two words on a board:** chop with **Chopping** raised and Lucky Charm. The small **CHOPPING +36%** label stays over the board, and each **LUCKY x2** pops up above it and flies up as it fades, without running into the one before.
   - **A chef holding a pot:** the words over that chef sit above the pot's icons, and above the over-head skill bar when it shows.
   - **What goes over the words:** with a word over your chef, open the emote wheel: the words under it fade out and come back when it closes. Open your skill info card (Tab) over a station's label: the card covers it. Pause: every word hides, and comes back on resume. In a level with a tutorial popup, no word or skill bar shows over the popup.
6. The next level starts with your last build pre-selected. Holding Skip (B on a controller) fills the bar under **HOLD: SKIP** and closes the screen after a second, with no skill changes for that level. A quick tap of B only undoes a lock-in.
7. Pausing and quitting or restarting during and after the screen works normally.
8. Online, with Chef Skills on both PCs: each screen shows both players' cards. The other player's pips change as they press Left/Right and the card turns **READY!** when they lock in. The level starts on both PCs only after both have locked in, and the log shows `Đồng bộ chọn kỹ năng: lượt N` with the same N on both.
9. Online, the client raises **Chopping** to +3: on the host's game the client's chef chops visibly faster, and on **both** PCs the chopping bar is full exactly when the item turns into the chopped one. The same for **Washing** (the bar fills as the clean plate appears, on both PCs, also with two chefs at one sink) and **Cooking** in a pot the client filled.
10. Online, the client raises **Running** and **Dashing** to +3: the client's chef runs and dashes faster on both PCs, without being pulled back or stuttering, also while dashing repeatedly.
10a. Online, as the client, mash dash (also right as it recharges, with quick taps, and while another chef is bumping you): every dash you see on your PC also happens on the host's screen, and your chef is never snapped back after one. Dashing into the host's chef with **Dashing** +3 knocks it no harder than at 0. Getting into a cannon and pressing dash still gets you back out of it.
10b. Special skills, offline first and then online as the client:
   - **CHOOSE SKILLS** opens the picker with the four slots along its top: Left/Right moves between them, Down goes into that slot's grid, Select picks and goes back to the slots (the picker stays open); Select on the current pick empties the slot, and there is no NONE tile. The keys sit inside the picker, under the details, without the directions. Clashing picks (for example Heavy Boots with Sprinter) show CLASH and can't be picked, the current pick shows CURRENT, and a click on a card's special icon opens the picker on that slot, a click on a slot focuses it and a click on a tile picks it.
   - Pips: every skill shows three red pips on the left. Pick Heavy Boots: Dashing loses its red pips. Online, other players' cards show their skills and a row of special icons (named under the rows) but no **CHOOSE SKILLS** or **SETTINGS** button.
   - **SETTINGS** opens a panel over your card with **SKILL KEY**, **INFO KEY**, **DISPLAY**, **BAR SIZE** and **BAR POSITION**; Up/Down picks, Left/Right steps **DISPLAY** and **BAR SIZE**, Select sets a key or starts moving the bar, Back closes it, and Lock in does nothing while it is open.
   - While the skill screen is open (also while it is hidden to look at the kitchen), Start and Esc don't open the pause menu, and it doesn't pop up the moment the screen closes either. After the level starts, Start pauses as usual.
   - A quirk adds its points to the card.
   - The skill bar at the bottom shows your active with its cooldown sweep and key, and your passives; a press with nothing to use it on shakes the button, and the reason floats up over the chef at the normal word size (also with **BAR SIZE** at 60% or 150%).
   - Tab slides your details card in at the left edge (each pick, what it does, cooldown, slot) and slides it back out; a second local player's own info key opens theirs at the right edge without touching yours. The pause menu hides the bars, the cards and the effects.
   - Two or more local players: the on-screen bars stand well apart, each with a P1/P2 tag and a line in that player's colour. Set one player's **DISPLAY** to **OVER HEAD**: their small bar follows their chef while the other's stays at the bottom; **HIDDEN** removes it. One player opening a picker leaves the other's card visible and usable.
   - **BAR SIZE** 60% shrinks the bar; **BAR POSITION** moves a preview of it over the skill screen and the bar is there in the kitchen. **OVER HEAD** shows only the ready or running active and timed passives, small, and disappears while the active recharges.
   - Each card shows the player's name (the Steam name; an extra local player is the same name with their number). The rows scroll with the highlight, with small arrows when more are above or below, and the line under them is never squeezed. The hint at the bottom shows the player's own SELECT button (A) with what it does on that row, and their LOCK IN button (X).
   - On half of a shared controller, the log line `Người chơi N: tay cầm ...` at the skill screen lists that player's select, back, hide and lock-in buttons, and lock-in and select are different.
   - Fortune Teller: a see-through NEXT ORDER card sits after the last order on the bar and turns into the real order when it arrives. Bounty Hunter: the bounty order has a glowing gold outline and a BOUNTY x1.5 tag, on every machine, and the next bounty only appears 15 s after one is claimed.
   - In **SETTINGS**, set **SKILL KEY** to a key (keyboard) and to RT or LB (controller) and use the active with them; the emote button then only emotes. A key the game uses (Space, W) is accepted with **(ALSO A GAME BUTTON)**.
   - **Hide** (Y / Tab) hides the screen and shows the kitchen; pressing it again, Select, Lock in or Back brings it back with nothing changed.
   - Vault over a counter, a tall unit and a wall (it lands on the first safe floor just past it), and Blink 6 m through counters and walls (to the farthest free spot), as the host and as the client. A refusal names its reason (NOTHING TO VAULT OVER, NO FLOOR BEHIND IT, NO ROOM TO LAND, ONLY PITS AHEAD, EDGE OF THE KITCHEN); a refused Blink also logs a "BLINK:" line with what it rejected.
   - **Choosing a target.** Hold the skill key with Swap or Summon (two or more teammates), Telekinesis, Hex, Power Chop, Wash Burst, Spare Pan, Rescue, Overclock, Vault, Blink or Grapple: rings show what it can be used on (for the jumps, where each way lands), the one it would pick anyway highlighted, and the chef stands still. The movement keys or stick move the highlight to the next target that way, with a click; letting go uses it on the highlighted one, even one behind you. A quick tap still uses it on the one in front. As the client, the host does the same thing to the same target.
   - Swap and Summon: while you hold the key, your teammate's screen shows a ring and **SWAP?** over their chef. After you let go both chefs stand still for a moment with a beam between them and **SWAP!** over the teammate, then fly to their new places in an arc, leaving rings behind, and land with **SWAPPED!** (or **SUMMONED!**). The same on both PCs, with no snapping. Safety Net and Lifeguard fly the fallen chef back up the same way, holding the chef still for the flight; the chef's model is never left behind at the landing spot.
   - Effects match on both PCs: an active's burst and name show where the chef used it (also for Vault and Blink, on the player's own PC); Medic's beam and **MEDIC!** show where the chef really reappears; Sure-Footed glows on ice and travelators, and Immovable and Conveyor Rider against bellows and water-gun wind, on both PCs.
   - Sizzle Master: your pot shows a ring and **x1.6** while it cooks faster, on both PCs; a teammate's pot doesn't.
   - Try one special from each module: Blink (Teleport), Sprinter (Movement), Pack Mule (Carrying), Homing Throw (Throwing), Power Chop (Stations), Fire Blast (Cooking), Charmer (Orders) and Rally Cry (Team buffs). Check each does what [SPECIAL_SKILLS.md](SPECIAL_SKILLS.md) says on both PCs, with no snapping back.
   - With nobody picking any specials, the level plays exactly as before.
   - `LogOutput.log` lists `Kỹ năng đặc biệt: 8 nhóm` and no `Không áp dụng được` warnings.
11. Online, play a second level in a row and restart one: the screen waits for everyone again each time.
12. Online, the host's legend says **HOLD: SKIP FOR ALL**, and holding it closes the screen on both PCs. A client's hold closes only the client's screen, and the host's screen shows that client as **READY!**.
13. Online, with a player who doesn't have the mod (try either the host or a client without it): no skill screen opens on any PC, the level starts straight into **Ready… Go!**, every chef runs and works at normal speed, and the modded PCs' logs say `Không phải ai trong phòng cũng cài Chef Skills`.

If the game's dialog art hasn't loaded within 5 seconds, the log says `Chưa tải được hình giao diện của game…` and the screen falls back to plain coloured panels, with the same controls. If the screen fails outright, the log says `Màn chọn kỹ năng bị lỗi…` and the level starts normally with no skill changes.
