# Chef Skills: special skills

Besides the nine skill levels, every chef picks **one active special, two passive specials and, if they like, one quirk** on the skill screen. Specials cost no skill points. All 112 are implemented; 4.1.0 reworked and re-checked every one of them against the game's code, but they still need trying in the game.

## Picking

The four rows under the skills on each card are **ACTIVE**, **PASSIVE**, **PASSIVE** and **QUIRK**. Highlight one with Up/Down and step through the choices with Left/Right. Every row starts at "-" (none), so a chef who picks nothing plays the normal game.

- **Active**: press your **skill key** in the kitchen to use it. It is the **EMOTE** button (Y on a controller) unless you change it on the card's **SKILL KEY** row (Select, then press the new key or button). The **skill bar** at the bottom of the screen shows it as a round button: glowing when ready, otherwise a cooldown sweep with the seconds left, a ring while its effect lasts, and the key. When there's nothing to use it on, the button shakes and says why (for example **NO COUNTER IN FRONT**), and no cooldown starts.
- **Passive**: always on. Its small icon in the skill bar shows a recharge sweep or a count when it has one (Safety Net's 30 s, Double Dash charges, Hot Streak's streak...). Press **Tab** in the kitchen (change it on the card's **DETAILS KEY** row) for a panel with all your picks and what they do.
- **Quirk**: a drawback that **frees skill points** (shown as **+1** or **+2**), so you can raise your skills further than you lower them.
- **Picks that clash can't go together.** The card skips them:
  - one dash style: Sprinter, Double Dash or Charge Dash;
  - one size: Big Chef or Tiny Chef;
  - one throw path: Homing Throw or Lob;
  - a quirk and the specials it takes away: Heavy Boots with anything built on dashing, Weak Arm with throwing specials, Butterfingers with catching, Pyrophobe with Hose Hands, Germaphobe with Wash Burst, Clumsy with Sticky Fingers.
- **One per kitchen.** Specials that change things for the whole team can be locked in by only one player. If someone else has already locked one in, the card says so and won't lock in until you pick something else. The effect never stacks anyway.
- **Versus.** Specials marked *(versus)* are only offered when the host sets `VersusSpecials = true`.

Config (`[Specials]` in `com.manhl.overcooked2.chefskills.cfg`): `Enabled` (default true) and `VersusSpecials` (default false). Online, the host's settings apply to everyone. `[Controls] SkillKey` is the skill key (EMOTE by default), also in MOD Settings with a **Rebind** button that listens for the key or controller button you press.

Before locking in, **Hide** (Y on a controller, Ctrl or Tab on the keyboard) hides the screen so you can look at the kitchen and plan; press it again, or Select or Back, to bring it back.

## Seeing them work

Every special shows when it does something, to every player, in its group's colour (blue movement, orange carrying and throwing, green prep, red cooking and fire, gold orders, pink team, purple buffs, teal hazards, crimson versus, grey quirks):

- a burst and the special's name over the chef when an active goes off;
- a ring under whatever it is working on while it works: a pot cooking faster (**x2**), a protected cooker, a board chopping by itself, a buffed chef, an item being pulled or flying to a teammate;
- a beam for teleports, pulls, swaps and passes;
- a short word when a passive kicks in (**SAVED!**, **BLOCKED!**, **CAUGHT!**, **LUCKY!**, **COMBO KEPT!**).

"Your" pots and pans (Sizzle Master, Steady Mixer, Cold Hands, Fire Marshal) are the ones you carry or put food in: picking one up makes it yours alone, and adding food (by hand or by throwing it in) adds you to its owners, until another chef picks it up.

## Online

Everyone needs Chef Skills 4.1.0. A machine on an older version counts as not having the mod, and then the level is played without skills or specials, as before.

The host decides what happens: items, orders, fire, scores and where every chef is. Each player's machine also predicts its own chef's movement, so specials that change speed or position are applied on both. To do that quickly, specials use their own Steam P2P channel (7) through the host, alongside the game's own connections. A press of the skill key carries where your chef stands and faces on your own PC, so the host aims from there rather than from its slightly late view of you. Things only your own PC sees (holding DASH for Sprinter or Charge Dash, a dash hitting someone for Shove or Pickpocket) are sent to the host from your PC.

## Actives (SKILL KEY)

| Special | What it does | Cooldown |
|---|---|---|
| **VAULT** | Hop over whatever is in front of you, walls too, landing up to 4 m away | 6 s |
| **BLINK** | Teleport up to 6 m ahead, straight through counters and walls | 10 s |
| **RECALL** | Plant a flag; use again within 20 s to teleport back to it (cooldown starts on return) | 25 s |
| **SWAP** | Trade places with the teammate you face (or the nearest) | 20 s |
| **SUMMON** | Pull the teammate you face (or the nearest) to your side | 25 s |
| **GRAPPLE** | Pull yourself to the counter you face, up to 6 m away | 12 s |
| **PACK MULE** | Carry a second item on your back; SKILL KEY swaps hand and back | 0.5 s |
| **POCKET** | Pocket up to 3 ingredients, hidden; SKILL KEY stores or takes the last out | 0.5 s |
| **TELEKINESIS** | Pull the item you face, up to 6 m away, into your hands | 8 s |
| **POWER CHOP** | Finish the item on the board you face at once | 15 s |
| **WASH BURST** | Wash every plate in the sink you face at once | 40 s |
| **PLATE CONJURER** | Conjure a clean plate in your empty hands | 30 s |
| **SPARE PAN** | Conjure a copy of the empty pan or pot you face (once per level) | once per level |
| **RESCUE** | Un-burn the pot or pan you hold or face and put out its cooker's fire | 45 s |
| **OVERCLOCK** | The cooker or mixer you face works three times as fast for 10 s | 30 s |
| **FIRE BLAST** | Put out every fire within 4 m at once | 30 s |
| **REROLL** | Swap the newest order for a different dish | 30 s |
| **TIME OUT** | Order timers (and new orders) stop for 10 s (whole team) (one per kitchen) | 50 s |
| **RALLY CRY** | You and every teammate run 30% faster for 8 s | 30 s |
| **FRENZY** | All your skills +2 for 10 s, then -1 for 6 s | 40 s |
| **OVERDRIVE** | No dash recharge and +20% speed for 6 s | 30 s |
| **GREASE TRAIL** | For 3 s you leave a slippery trail; other chefs who step on it slip (versus) | 25 s |
| **SNEEZE BOMB** | Every chef within 3 m of you (teammates too) sneezes and freezes for 2 s (versus) | 30 s |
| **HEX** | The chef you face: their next 5 chops (within 30 s) don't count (versus) | 30 s |
| **DECOY ORDER** | Add a fake order to the rival team's list for 15 s (it scores nothing) (versus) | 45 s |

## Passives

| Special | What it does |
|---|---|
| **SPRINTER** | Hold DASH to sprint +50% (4 s of stamina) instead of dashing |
| **DOUBLE DASH** | Two dash charges: chain two dashes back to back |
| **CHARGE DASH** | Hold DASH to charge, release for up to twice the distance |
| **MOMENTUM** | Keep running to build up to +30% speed; stopping loses it |
| **SLIPSTREAM** | Teammates running close behind you get +30% speed |
| **SURE-FOOTED** | No slipping on ice or puddles, no drag from wind or moving walkways |
| **IMMOVABLE** | Dashes, thrown items and fire can't knock you back |
| **GHOST** | Walk straight through other chefs |
| **QUICK RESPAWN** | Respawn after 1.5 s instead of 5 s |
| **SAFETY NET** | Once every 30 s, falling off puts you back where you fell |
| **CONVEYOR RIDER** | Moving floors carry you twice as fast and never slow you down |
| **BIG CHEF** | Bigger: can't be knocked, knock others further, -10% speed |
| **TINY CHEF** | Smaller: +15% speed, but knocks send you further |
| **HOVER** | Dash across a gap up to one tile wide (10 s recharge) |
| **WAITER** | Put a dish onto another dish to carry both; hand one in and the other goes in too |
| **LONG ARMS** | Pick up, put down, chop and wash one tile further away |
| **MAGNET** | Loose items on the floor within 4 m slide towards you |
| **BULK BUYER** | Taking from a crate also puts a spare on a counter (12 s) |
| **CATCHER'S MITT** | Catch from any direction and further away |
| **STICKY FINGERS** | Keep what you hold when shoved, and even when you fall off the map |
| **STRONG ARM** | Throw anything (pots, pans, dishes); teammates in its path catch it |
| **HOMING THROW** | Throws fly to the teammate you aim at (14 m); they catch from any side |
| **LOB** | Throws arc high over counters and chefs (same distance) |
| **ASSEMBLY LINE** | Take from a crate, then face a teammate within 1.5 s: it flies to them |
| **JUGGLER** | Re-throw within 1 s of a catch for a 50% longer throw |
| **REBOUND** | Uncaught throws landing within 3 m of you hop onto a free counter |
| **HAND-OFF** | PICK UP facing a teammate takes their item or gives yours |
| **MULTITASK** | A board or sink you leave keeps working at 40% speed |
| **ECHO KNIFE** | Each chop also chops the last other board you used |
| **LUCKY KNIFE** | Each chop has a 10% chance to finish the item at once |
| **PREP COOK** | Every 15 s, an ingredient you take from a crate comes chopped |
| **DASH-CHOP** | Dashing past a loaded board chops it 3 times |
| **DISHWASHER** | Every second dish you hand in comes back clean, on a sink's drying rack |
| **BUTLER** | Your dirty plates come back straight into a sink |
| **HOT HANDS** | A pot or pan you carry keeps cooking at full speed and stops when done |
| **COLD HANDS** | Pots and pans you carry or fill wait 8 s at done before they burn |
| **SIZZLE MASTER** | Pots and pans you carry or fill cook (and burn) twice as fast |
| **STEADY MIXER** | Bowls you carry or fill mix 30% faster and never over-mix |
| **FIREPROOF** | Walk through flames, and fire right next to you dies out in 2 s |
| **FIRE MARSHAL** | A cooker under a pot or pan you carried or filled never catches fire |
| **HOSE HANDS** | Hold USE with empty hands facing a fire to spray it like an extinguisher |
| **SLOW SPREAD** | Fire within 5 m of you spreads 3 times slower |
| **CHARMER** | Dishes you hand in give +50% tip |
| **MAITRE D'** | Every order lasts 25% longer (whole team) (one per kitchen) |
| **CLUTCH** | A dish handed in during its order's last 25% scores double |
| **COMBO KEEPER** | A missed order or wrong dish keeps the tip streak (every 40 s) (one per kitchen) |
| **SECOND CHANCE** | Each order's first expiry costs no points (whole team) (one per kitchen) |
| **GAMBLER** | Your tips are multiplied by 0, 1, 2 or 3 at random |
| **FRESH DELIVERY** | A dish handed in within 20 s of cooking gets +50% tip |
| **CLOSE ENOUGH** | Every 45 s, a dish nobody ordered counts as the oldest order (one per kitchen) |
| **FORTUNE TELLER** | The next order shows early as a see-through card on the order bar (not on phased levels) |
| **BOUNTY HUNTER** | One order at a time is framed in gold on the order bar: hand it in for double points (one per kitchen) |
| **GOLDEN HOUR** | In the last 45 s of the level, your tips are doubled |
| **INSPIRING** | Teammates within 3.5 m of you chop and wash about 25% faster (one per kitchen) |
| **MEDIC** | Teammates who fall respawn next to you after 1 s |
| **BODYGUARD** | Teammates within 4 m of you can't be knocked back or slip |
| **BATTERY** | Your active cuts half a full cooldown off a teammate's (kept if none) |
| **BUDDY SYSTEM** | Near your closest teammate (4 m) both run 15% faster; speed buffs shared |
| **QUARTERMASTER** | Teammates taking from a crate within 3.5 m of you get a spare (10 s) |
| **SECOND WIND** | In the last 30 s, all skills +1 (RUNNING and DASHING +3) |
| **ADRENALINE** | After a knock, slip or respawn: +50% run speed, free dashes for 5 s |
| **ZEN** | Stand still (not working) 1.5 s: your next chop or wash finishes at once |
| **COFFEE RUN** | Every correct dish you hand in gives you +25% speed for 6 s |
| **GLASS CANNON** | All skills +1, but a knock, slip or fall makes them -1 for 15 s |
| **SLOW STARTER** | All skills -1 for the first 60 s of play, then +2 |
| **UNDER PRESSURE** | +5% speed for each order waiting, up to +25% |
| **LUCKY CHARM** | Each chop stroke (or half-second washing) 25% chance to count double |
| **HOT STREAK** | Orders in a row without a miss: +3% speed each (up to 15%); at 5, +1 to every skill |
| **PORTAL PRO** | Out of a teleporter: run 40% faster and dash with no recharge for 2 s |
| **CANNONEER** | Cannons fire you the moment the button is pressed, without the fuse |
| **DRIVER** | Platforms and carts you steer move 50% faster |
| **WEATHERPROOF** | Wind and moving floors only push you the way you face |
| **LIFEGUARD** | Teammates who fall near you are put back instead |
| **SHOVE** | Your dashes knock harder and make chefs drop what they hold (versus) |
| **PICKPOCKET** | Dashing into a chef takes what they hold (not from Sticky Fingers) (versus) |

## Quirks

| Quirk | Drawback | Frees |
|---|---|---|
| **HEAVY BOOTS** | You can't dash at all | +2 points |
| **CLUMSY** | Dashing drops whatever you hold | +1 point |
| **WEAK ARM** | You can't throw | +1 point |
| **BUTTERFINGERS** | You can't catch thrown items | +1 point |
| **PYROPHOBE** | Fire extinguishers won't spray for you | +1 point |
| **GERMAPHOBE** | You can't wash plates | +1 point |
| **VEGETARIAN** | You won't pick up raw meat or fish | +1 point |
| **SLEEPY** | Every 30 s you doze off and can't move for 1 s | +2 points |
| **FRAGILE** | Respawning takes 10 s instead of 5 s | +1 point |
| **SHOW-OFF** | After every dish you hand in, you pose for 1 s | +1 point |
| **TUNNEL VISION** | You can only chop or only wash: the first one you do | +2 points |
| **LONE WOLF** | Teammates' specials and auras don't affect you | +1 point |

## Combinations to try

- **The passer:** Assembly Line and Homing Throw, with Throwing +3. Stands at the crates and fires ingredients across the kitchen.
- **The runner:** Heavy Boots frees 2 points for Running +3. Add Pack Mule and Momentum, and make half as many trips.
- **The fire chef:** Hot Hands, Fireproof and Slow burn +3. Cooks on the move and walks through flames.
- **The closer:** Second Wind and Golden Hour. A quiet start, then a huge last 30 seconds.
- **The anchor:** Multitask, Zen and Immovable. Works a board and a sink at once, and nobody can knock them off.

## For developers

- **Catalog:** `Specials/Catalog.cs` lists every special. The numbers go over the network, so never renumber them.
- **Framework:**
  - `Specials/SpecialRuntime.cs`: activation, cooldowns, buffs and the modifier lists that movement and skills read.
  - `Specials/SpecialNet.cs`: the P2P channel.
  - `Specials/SpecialHud.cs`: the skill bar.
  - `Specials/SpecialDetails.cs`: the skill details panel docked to the screen edge (`DetailsKey`, Tab by default).
  - `Specials/SpecialFx.cs`: rings, bursts, beams and floating words, drawn locally or sent to every PC (`...All`, for code that runs only on the host).
  - `Art/`: the special icons. `iconkit.py` is the drawing kit; `icons_*.py` draw one icon per Special value; run `.tools/oc2-python/python.exe -I Overcooked2/ChefSkills/Art/make_icons.py` to rebuild `SpecialIcons.png` (cell n = Special n, 16 columns of 128 px) and `preview.png`. `build.cmd` embeds the sheet; `SpecialFx.IconOf` / `ShowIcon` read it (two letters when a cell is empty). A new special needs an icon there.
  - `SkillPicker.cs`: the grid picker the skill screen opens on a special row.
  - `Specials/SpecialInput.cs`: the skill key per keyboard side or pad, and setting it from the skill screen.
  - In an active's `Execute`, aim from `state.RequestPosition` / `RequestForward` and refuse with `return state.Refuse("REASON");`. Passives can show timers and counts with `state.SetTimer` / `state.SetCount`, and a status chip over the skill bar with `state.SetStatus("Key", "TEXT", Special.X)` (one chip per key; null clears it).
- **Effects:** each group of specials lives in its own module in `Specials/Modules/` (an `ISpecialModule`, plus its Harmony patches in namespace `ChefSkills.Specials`). A patch that doesn't fit the game version only turns off its own special; the plugin logs it and carries on.
- **Checks:** `tests/BuildPart.ps1 -Modules <file>.cs` builds the framework with one module and runs `tests/CheckChefSkills.ps1` on it. `-Modules *` builds all of them.
