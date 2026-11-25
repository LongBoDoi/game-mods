# Chef Skills: special skills

Besides the nine skill levels, every chef picks **one active special, two passive specials and, if they like, one quirk** on the skill screen. Specials cost no skill points. 109 are offered (Cannoneer, Driver and Weatherproof were merged into Transit Pro and Sure-Footed in 4.3.0). 4.1.0 reworked and re-checked every one of them against the game's code, and 4.3.0 rebalanced them (see [BALANCE_PROPOSAL.md](BALANCE_PROPOSAL.md) for the reasons); they still need trying in the game.

## Picking

The four rows under the skills on each card are **ACTIVE**, **PASSIVE**, **PASSIVE** and **QUIRK**. Highlight one with Up/Down and step through the choices with Left/Right. Every row starts at "-" (none), so a chef who picks nothing plays the normal game.

- **Active**: press your **skill key** in the kitchen to use it. It is the **EMOTE** button (Y on a controller) unless you change it on the card's **SKILL KEY** row (Select, then press any key or button: shoulders, triggers and the d-pad work too). The **skill bar** (at the bottom of the screen or wherever you put it, at the size you choose; small over your chef; or hidden: the card's **DISPLAY**, **BAR SIZE** and **BAR POSITION** rows) shows it as a round button: glowing when ready, otherwise a cooldown sweep with the seconds left, a ring while its effect lasts, and the key. When there's nothing to use it on, the button shakes, the reason floats up over your chef (for example **NO COUNTER IN FRONT**), and no cooldown starts.
- **Choosing a target.** Actives that work on something in front of you (**Swap**, **Summon**, **Telekinesis**, **Hex**, **Power Chop**, **Wash Burst**, **Spare Pan**, **Rescue**, **Overclock**) and the jumps (**Vault**, **Blink**, **Grapple**) let you choose. **Hold** the skill key: rings show everything in reach (for a jump, where it would land each way round you), with the one it would pick anyway (the one you face, or the nearest) highlighted. Your chef stands still while you hold it. Move (keys or stick) to move the highlight to the next target that way; **let go** to use it on the highlighted one, even one behind you. A quick tap still uses it on the one in front, as before.
- **Passive**: always on. Its small icon in the skill bar shows a recharge sweep or a count when it has one (Safety Net's 30 s, Double Dash charges, Hot Streak's streak...). Press **Tab** in the kitchen (change it, or set a controller button, on the card's **INFO KEY** row) for a card with all your picks and what they do; each player opens their own.
- **Quirk**: a drawback that **frees skill points** (shown as **+1** or **+2**), on top of the 6 that lowering skills can free. A **+2** quirk costs every chef on every level (Heavy Boots, Sleepy); a **+1** quirk costs some roles or levels and not others, so pick one your role doesn't mind. A quirk that takes a skill away keeps that skill at 0 or above (Heavy Boots: DASHING; Weak Arm: THROWING; Germaphobe: WASHING), so it can't be a free point. Playing alone with two chefs, the quirk applies to both (the picker says **SOLO: BOTH CHEFS**).
- **Picks that clash can't go together.** The card skips them:
  - one dash style: Sprinter, Double Dash or Charge Dash;
  - one size: Big Chef or Tiny Chef;
  - one throw path: Homing Throw or Lob;
  - Second Chance or Combo Keeper (and not both in the same kitchen, whoever picks them);
  - a quirk and the specials it takes away or buys back: Heavy Boots with anything built on dashing (and Adrenaline), Weak Arm with throwing specials, Butterfingers with catching, Pyrophobe with Hose Hands, Fire Blast and Fireproof, Germaphobe with Wash Burst, Clumsy with Sticky Fingers, Fragile with Safety Net.
- **One per kitchen.** Specials that change things for the whole team can be locked in by only one player (in versus, one per team). If someone else has already locked one in, the card says so and won't lock in until you pick something else. When a one-per-kitchen active is used, it goes on cooldown for the whole team.
- **Playing alone.** Your two chefs share one active cooldown: using it on one chef puts the other's on cooldown too.
- **Versus.** Specials marked *(versus)* are only offered when the host sets `VersusSpecials = true`. Decoy Order is only offered on versus levels.

Config (`[Specials]` in `com.manhl.overcooked2.chefskills.cfg`): `Enabled` (default true) and `VersusSpecials` (default false). Online, the host's settings apply to everyone. `[Controls] SkillKey` (keyboard) and `SkillButton` (controller) are the skill key (EMOTE by default), also in MOD Settings.

Before locking in, **Hide** (Y on a controller, Ctrl or Tab on the keyboard) hides the screen so you can look at the kitchen and plan; press it again, or Select, Lock in or Back, to bring it back. **Lock in** is Enter on the keyboard and X on a controller (a free button on half of a shared controller; the card shows it); Select only picks.

## Limits on stacking

Specials keep their full strength on their own; only piles of them are cut:

- **Skill bonuses:** all "+N to every skill" effects together (Frenzy, Glass Cannon, Slow Starter, Second Wind, Hot Streak, Inspiring...) add at most **+2** to a skill (**+3** to RUNNING and DASHING) and take away at most 3.
- **Speed:** specials and buffs that change run or dash speed multiply together up to **×1.6**, before the RUNNING or DASHING skill. Charge Dash's charged dash is outside that cap. A stop (Sleepy, Sneeze Bomb, aiming) still stops.
- **Score:** one dish's tip is multiplied by at most **×2.5** and its score by at most **×2** by all specials together. A Close Enough hand-in gets no Clutch or Bounty Hunter bonus.
- **Cooking:** Overclock and Sizzle Master together make one pot cook at most **×4** as fast; the COOKING skill applies on top.

## Seeing them work

Every special shows when it does something, to every player, in its group's colour (blue movement, orange carrying and throwing, green prep, red cooking and fire, gold orders, pink team, purple buffs, teal hazards, crimson versus, grey quirks):

- a burst and the special's name over the chef when an active goes off;
- a ring under whatever it is working on while it works: a pot cooking faster (**x1.6**), a protected cooker, a board chopping by itself, a buffed chef, an item being pulled or flying to a teammate;
- a beam for teleports, pulls, swaps and passes;
- a short word when a passive kicks in (**SAVED!**, **BLOCKED!**, **CAUGHT!**, **LUCKY!**, **COMBO KEPT!**).

"Your" pots and pans (Sizzle Master, Steady Mixer, Cold Hands, Fire Marshal) are the ones you carry or put food in: picking one up makes it yours alone, and adding food (by hand or by throwing it in) adds you to its owners (a Strong Arm toss that lands in a station counts as putting it there), until another chef picks it up. The chef who last put food in a pot (or set it down) also decides its COOKING and SLOW BURN speed.

## Online

Everyone needs Chef Skills 4.3.0. A machine on an older version counts as not having the mod, and then the level is played without skills or specials, as before.

The host decides what happens: items, orders, fire, scores and where every chef is. Each player's machine also predicts its own chef's movement, so specials that change speed or position are applied on both. To do that quickly, specials use their own Steam P2P channel (7) through the host, alongside the game's own connections. A press of the skill key carries where your chef stands and faces on your own PC, and the target you chose, so the host aims from there rather than from its slightly late view of you. While you choose a target, every PC knows: your chef stands still on all of them, and your teammates see what you have chosen. Things only your own PC sees (holding DASH for Sprinter or Charge Dash, a dash hitting someone for Shove or Pickpocket) are sent to the host from your PC.

## Actives (SKILL KEY)

| Special | What it does | Cooldown |
|---|---|---|
| **VAULT** | Hop over whatever is right in front of you (walls too), landing just past it. The obstacle must be within 1.6 m, and the move is instant (the hop is only how it looks) | 9 s |
| **BLINK** | Teleport to the farthest free spot up to 6 m ahead, straight through counters and walls | 10 s |
| **RECALL** | Plant a flag; use again within 20 s to teleport back to it (cooldown starts on return) | 15 s |
| **SWAP** | Trade places with the teammate you face (or the nearest, or the one you choose). They see it coming: a ring and **SWAP?** while you choose, a short wind-up, then both of you fly across (about half a second in all). Not a Lone Wolf | 14 s |
| **SUMMON** | Pull the teammate you face (or the nearest, or the one you choose) to your side; they see it coming and fly over. Not a Lone Wolf | 14 s |
| **GRAPPLE** | Pull yourself to the counter you face, up to 7 m away, flying over gaps too | 9 s |
| **PACK MULE** | Carry a second item on your back; SKILL KEY swaps hand and back | 0.5 s |
| **POCKET** | Pocket up to 2 ingredients, hidden; SKILL KEY stores or takes the last out | 0.5 s |
| **TELEKINESIS** | Pull the item you face, up to 6 m away, into your hands. It pulls through walls and out of crates | 8 s |
| **POWER CHOP** | Finish the item on the board you face at once | 10 s |
| **WASH BURST** | Wash every plate in the sink you face at once (refused when its drying rack is full) | 40 s |
| **PLATE CONJURER** | Conjure a clean plate in your empty hands. It is used once: once its dish is handed in, its plate doesn't come back | 30 s |
| **SPARE PAN** | Conjure a copy of the empty pan, pot or mixing bowl you face. One copy out at a time (**SPARE STILL OUT**); a copy disappears once it has been emptied (onto a plate, or a bowl into a pan) | 25 s |
| **RESCUE** | Un-burn the pot or pan you hold or face, put out its cooker's fire, and hold it at done for 10 s | 30 s |
| **OVERCLOCK** | The cooker or mixer you face works three times as fast for 10 s (it burns three times as fast too) | 30 s |
| **FIRE BLAST** | Put out every fire within 6 m at once | 20 s |
| **REROLL** | Replace the oldest order with a different dish, at no cost (whole team) (one per kitchen) | 40 s |
| **TIME OUT** | Order timers (and new orders) stop for 10 s (whole team) (one per kitchen) | 50 s |
| **RALLY CRY** | You and every teammate run 30% faster for 8 s (one per kitchen) | 30 s |
| **FRENZY** | All your skills +2 for 10 s, then -1 for 4 s | 30 s |
| **OVERDRIVE** | No dash recharge and +20% speed for 6 s | 30 s |
| **GREASE TRAIL** | For 4 s you leave a slippery trail (each patch lasts 6 s); chefs who step on it slip. In versus your own team is safe; in co-op party play it trips everyone but you (versus) | 20 s |
| **SNEEZE BOMB** | Every chef within 4 m of you (teammates too) sneezes, drops what they hold and freezes for 2 s (versus) | 20 s |
| **HEX** | The chef you face, within 5 m: their next 10 chops (within 30 s) don't count, and their first 3 s of washing don't either (versus) | 20 s |
| **DECOY ORDER** | Add a fake order to the rival team's list for 25 s (it scores nothing). Only offered on versus levels (versus) | 45 s |

## Passives

| Special | What it does |
|---|---|
| **SPRINTER** | Hold DASH to sprint +35% (+24.5% at DASHING -3, +45.5% at +3; 4 s of stamina). You can't dash at all |
| **DOUBLE DASH** | Two dash charges that refill 25% faster: chain two dashes back to back |
| **CHARGE DASH** | Hold DASH to charge, release for up to twice the distance |
| **MOMENTUM** | Keep running to build up to +30% speed. Stopping keeps it for 0.4 s, then it fades; chopping or washing loses it at once |
| **SLIPSTREAM** | Co-op: teammates running close behind you (within 5 m, a 3 m wide lane) get +20% speed, for 2 s after they leave it, and you get +10% while someone is in your stream. Only a teammate heading the opposite way is left out |
| **SURE-FOOTED** | No slipping on ice, slopes or puddles. Moving walkways still carry you but never slow you down |
| **IMMOVABLE** | Dashes, thrown items, fire and wind can't knock or push you |
| **GHOST** | Walk straight through other chefs |
| **QUICK RESPAWN** | Respawn after 1 s, back at the last safe spot you stood on (unlimited, unlike Safety Net) |
| **SAFETY NET** | Once every 30 s, falling off puts you back where you fell |
| **CONVEYOR RIDER** | Moving floors and wind carry you twice as fast and never slow you down |
| **BIG CHEF** | Bigger: can't be knocked, knock others further, dash 25% further, -5% speed |
| **TINY CHEF** | Smaller: +10% speed, but knocks send you further |
| **HOVER** | Dash across a gap: up to 0.6 s in the air (10 s recharge) |
| **WAITER** | Put a dish onto another dish to carry both; hand one in and the other goes in too, if it matches an order (otherwise it stays in your hands) |
| **LONG ARMS** | Pick up, put down, chop and wash one tile further away |
| **MAGNET** | Loose items on the floor within 6 m slide to you (into empty hands); items lying next to another chef, and ones you have just put down or thrown (3 s), stay put |
| **BULK BUYER** | Taking from a crate also gives a spare (8 s recharge): onto the nearest empty board within 6 m if it can be chopped, else onto a counter |
| **CATCHER'S MITT** | Catch from any direction and further away; Strong Arm tosses from 2.5 m |
| **STICKY FINGERS** | Keep what you hold when shoved, and even when you fall off the map; a fall with something in your hands respawns you after 3 s |
| **STRONG ARM** | Throw anything (pots, pans, dishes). Hold USE to aim with the throw arrow, as with food. A teammate you aim at within 8 m gets it (the toss flies to them); teammates catch tosses from 1.8 m. Homing Throw, Lob, Juggler and Rebound work on it like on any throw. It lands in the station it comes down on (a pot on a cooker; a dish on the pass is handed in) as if you put it there |
| **HOMING THROW** | Throws fly to the teammate you aim at (10 m, scaled by THROWING: 13.6 m at +3); they catch from any side. Aim within 30° |
| **LOB** | Throws arc high over counters and chefs, and go 30% further |
| **ASSEMBLY LINE** | Take from a crate, then face a teammate with free hands within 2.5 s: it flies to them (range scaled by THROWING) |
| **JUGGLER** | Re-throw within 2 s of a catch, or of taking from a crate, for a 50% longer throw |
| **REBOUND** | Uncaught throws (yours too) landing within 4.5 m of you hop onto a free counter (within 6 m); a throw falling into a pit counts as landed |
| **HAND-OFF** | PICK UP facing a teammate within 3 m gives them what you hold (it never takes) |
| **MULTITASK** | Up to 2 boards or sinks you leave keep working at 35% speed (a third replaces the oldest) |
| **ECHO KNIFE** | 60% of your chops also chop the last other board you used, if it is within 6 m |
| **LUCKY KNIFE** | Each chop has a 10% chance to finish the item at once (about 30-40% faster chopping) |
| **PREP COOK** | Every 15 s, an ingredient you take from a crate comes chopped |
| **DASH-CHOP** | Dashing past a loaded board chops it 3 times (each board at most once every 2 s) |
| **DISHWASHER** | Every third dish you hand in comes back clean, on a sink's drying rack |
| **BUTLER** | Your dirty plates come back twice as fast, straight into a sink |
| **HOT HANDS** | A pot or pan you carry cooks 25% faster than on a cooker, and stops when done |
| **COLD HANDS** | Pots and pans you carry or fill wait 8 s at done before they burn |
| **SIZZLE MASTER** | Pots and pans you carry or fill cook (and burn) 60% faster |
| **STEADY MIXER** | Bowls you carry or fill mix 75% faster and never over-mix |
| **FIREPROOF** | Walk through flames, and fire right next to you dies out in 2 s |
| **FIRE MARSHAL** | A cooker under a pot or pan you carried or filled never catches fire, and the food in it burns half as fast |
| **HOSE HANDS** | Hold USE with empty hands facing a fire to spray it like an extinguisher |
| **SLOW SPREAD** | Fire can't spread within 7 m of you |
| **CHARMER** | Dishes you hand in give +35% tip |
| **MAITRE D'** | Every order lasts 15% longer (whole team) (one per kitchen). Only on levels whose orders have a time limit |
| **CLUTCH** | A dish handed in during its order's last 20% scores x1.5 |
| **COMBO KEEPER** | A missed order or wrong dish keeps the tip streak, and Hot Streak's streak (every 25 s; only spent when the streak is above x1) (one per kitchen; not with Second Chance) |
| **SECOND CHANCE** | An order's first expiry costs nothing: no points and the streak is kept (every 30 s, whole team) (one per kitchen; not with Combo Keeper) |
| **GAMBLER** | Your tips are multiplied by 0, 1, 2 or 2.5 at random |
| **FRESH DELIVERY** | A dish handed in within 25 s of its first part finishing cooking gets +50% tip. A dish with nothing cooked never counts |
| **CLOSE ENOUGH** | Every 45 s, a dish nobody ordered counts as the oldest order (one per kitchen) |
| **FORTUNE TELLER** | The next order shows early as a see-through card on the order bar, and arrives with 10% more time (not on phased levels) (one per kitchen) |
| **BOUNTY HUNTER** | One order at a time is framed in gold on the order bar: hand it in for x1.5 points. The next bounty is marked 15 s after one is claimed (one per kitchen) |
| **GOLDEN HOUR** | In the last 60 s of the level, your tips are doubled (nothing on levels without a clock) |
| **INSPIRING** | Teammates within 3.5 m of you chop about 24% and wash about 30% faster (not you) (one per kitchen) |
| **MEDIC** | Teammates who fall with empty hands (nothing held, pocketed or on a Pack Mule back) respawn next to you after 1 s; anyone carrying something respawns normally. You respawn within 2.5 s. Not for Lone Wolves |
| **BODYGUARD** | Teammates within 6 m of you can't be knocked back or slip |
| **BATTERY** | Your active (with a cooldown of 5 s or more) cuts up to 35% off a teammate's cooldown, at most half your own (kept if none). Never your own other chef (one per kitchen) |
| **BUDDY SYSTEM** | Near your closest teammate (5 m) both run 15% faster and share speed buffs (only while near) |
| **QUARTERMASTER** | Teammates taking from a crate within 5 m of you get a spare (5 s) |
| **SECOND WIND** | In the last 60 s, all skills +1 (RUNNING and DASHING +3). Nothing on levels without a clock |
| **ADRENALINE** | After a knock, slip, respawn, or your team missing an order or handing in a wrong dish: +50% run speed and free dashes for 8 s |
| **ZEN** | Stand still (not working) 1.5 s while you control the chef: your next chop or wash finishes at once. A parked chef doesn't charge |
| **COFFEE RUN** | Every correct dish you hand in gives you +25% run speed for 10 s |
| **GLASS CANNON** | All skills +1, but a knock (even one Immovable, Big Chef or Bodyguard blocks), slip, fall, or your team missing an order or handing in a wrong dish makes them -1 for 20 s |
| **SLOW STARTER** | All skills -1 for the first 45 s of play, then +1 |
| **UNDER PRESSURE** | +7% run speed for each order waiting beyond the second, up to +21% |
| **LUCKY CHARM** | Each chop stroke (or half-second washing) 25% chance to count double |
| **HOT STREAK** | Your team's orders in a row without a miss: +3% speed each (up to 15%); at 8, +1 to every skill but RUNNING and DASHING |
| **TRANSIT PRO** | After a teleporter, a cannon landing or leaving a platform you steered (and moved): run 40% faster and dash with no recharge for 4 s. Cannons fire you without the fuse, and platforms and carts you steer move 50% faster |
| **LIFEGUARD** | Teammates who fall within 7 m of you are put back instead (not Lone Wolves) |
| **SHOVE** | Your dashes knock harder and make chefs drop what they hold (the same chef at most every 3 s). In versus only rivals (versus) |
| **PICKPOCKET** | Dashing into a chef takes what they hold (not from Sticky Fingers). In versus only rivals (versus) |

## Quirks

| Quirk | Drawback | Frees |
|---|---|---|
| **HEAVY BOOTS** | You can't dash at all. DASHING can't go below 0 | +2 points |
| **CLUMSY** | Dashing drops a plate you hold (clean, dirty or with food); anything else stays in your hands | +1 point |
| **WEAK ARM** | You can't throw. THROWING can't go below 0 | +1 point |
| **BUTTERFINGERS** | You can't catch thrown items | +1 point |
| **PYROPHOBE** | Fire extinguishers won't spray for you | +1 point |
| **GERMAPHOBE** | You can't wash plates. WASHING can't go below 0 | +1 point |
| **VEGETARIAN** | You won't pick up or catch raw meat or fish (seafood and processed meat count); Homing Throw and Assembly Line passes skip you | +1 point |
| **SLEEPY** | Every 30 s you doze off for 2 s: you can't move, chop or wash (a *yawn* warns you 2 s before) | +2 points |
| **FRAGILE** | Respawning takes 5 s longer than the level's own respawn time | +1 point |
| **SHOW-OFF** | After every dish you hand in, you pose for 1 s | +1 point |
| **TUNNEL VISION** | You can only work at the first board or sink you use (sinks side by side count as one). Playing alone, both chefs are held to it | +1 point |
| **LONE WOLF** | Teammates' specials and auras don't affect you (no Medic, Lifeguard, Swap or Summon either) | +1 point |

## Combinations to try

- **The passer:** Assembly Line and Homing Throw, with Throwing +3. Stands at the crates and fires ingredients across the kitchen.
- **The runner:** Heavy Boots frees 2 points for Running +3. Add Pack Mule and Momentum, and make half as many trips.
- **The fire chef:** Hot Hands, Fireproof and Slow burn +3. Cooks on the move and walks through flames.
- **The closer:** Second Wind and Golden Hour. A quiet start, then a huge last minute.
- **The anchor:** Multitask, Zen and Immovable. Works a board and a sink at once, and nobody can knock them off.

## For developers

- **Catalog:** `Specials/Catalog.cs` lists every special. The numbers go over the network, so never renumber them. A retired special is marked `hidden`: never offered, and dropped from any loadout (so a saved last pick can't bring it back), while its number stays reserved.
- **Framework:**
  - `Specials/SpecialRuntime.cs`: activation, cooldowns (shared between a player's own chefs, and team-wide for one-per-kitchen actives), buffs and the modifier lists that movement and skills read (with the stacking caps above).
  - `Specials/SpecialNet.cs`: the P2P channel.
  - `Specials/SpecialHud.cs`: the skill bar.
  - `Specials/SpecialDetails.cs`: each player's skill details card docked to the screen edge (`DetailsKey` / `DetailsButton`, Tab by default).
  - `Specials/SpecialFx.cs`: rings, bursts, beams and floating words, drawn locally or sent to every PC (`...All`, for code that runs only on the host).
  - `Art/`: the special icons. `iconkit.py` is the drawing kit; `icons_*.py` draw one icon per Special value; run `.tools/oc2-python/python.exe -I Overcooked2/ChefSkills/Art/make_icons.py` to rebuild `SpecialIcons.png` (cell n = Special n, 16 columns of 128 px) and `preview.png`. `build.cmd` embeds the sheet; `SpecialFx.IconOf` / `ShowIcon` read it (two letters when a cell is empty). A new special needs an icon there.
  - `SkillPicker.cs`: the grid picker the skill screen opens on a special row.
  - `Specials/SpecialInput.cs`: the skill and info keys (keyboard key and controller button), setting them from the skill screen, and each player's lock-in button.
  - `Specials/SkillBarSettings.cs`: where each local player's skill bar shows (on screen, over the head, hidden).
  - In an active's `Execute`, aim from `state.RequestPosition` / `RequestForward` and refuse with `return state.Refuse("REASON");`. Passives can show timers and counts with `state.SetTimer` / `state.SetCount`, and a status chip over the skill bar with `state.SetStatus("Key", "TEXT", Special.X)` (one chip per key; null clears it).
- **Effects:** each group of specials lives in its own module in `Specials/Modules/` (an `ISpecialModule`, plus its Harmony patches in namespace `ChefSkills.Specials`). A patch that doesn't fit the game version only turns off its own special; the plugin logs it and carries on.
- **Checks:** `tests/BuildPart.ps1 -Modules <file>.cs` builds the framework with one module and runs `tests/CheckChefSkills.ps1` on it. `-Modules *` builds all of them.
