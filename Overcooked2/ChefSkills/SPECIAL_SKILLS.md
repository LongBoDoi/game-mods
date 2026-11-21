# Chef Skills: special skills

Besides the nine skill levels, every chef picks **one active special, two passive specials and, if they like, one quirk** on the skill screen. Specials cost no skill points. All 112 are implemented, but none has been tried in the game yet.

## Picking

The four rows under the skills on each card are **ACTIVE**, **PASSIVE**, **PASSIVE** and **QUIRK**. Highlight one with Up/Down and step through the choices with Left/Right. Every row starts at "-" (none), so a chef who picks nothing plays the normal game.

- **Active**: press the **EMOTE** button in the kitchen (Y on a controller) to use it. A label over your chef shows its name and **READY**, or the seconds left before it can be used again. It flashes red when there's nothing to use it on, and no cooldown starts then.
- **Passive**: always on.
- **Quirk**: a drawback that **frees skill points** (shown as **+1** or **+2**), so you can raise your skills further than you lower them.
- **Picks that clash can't go together.** The card skips them:
  - one dash style: Sprinter, Double Dash or Charge Dash;
  - one size: Big Chef or Tiny Chef;
  - one throw path: Homing Throw or Lob;
  - a quirk and the specials it takes away: Heavy Boots with anything built on dashing, Weak Arm with throwing specials, Butterfingers with catching, Pyrophobe with Hose Hands, Germaphobe with Wash Burst, Clumsy with Sticky Fingers.
- **One per kitchen.** Specials that change things for the whole team can be locked in by only one player. If someone else has already locked one in, the card says so and won't lock in until you pick something else. The effect never stacks anyway.
- **Versus.** Specials marked *(versus)* are only offered when the host sets `VersusSpecials = true`.

Config (`[Specials]` in `com.manhl.overcooked2.chefskills.cfg`): `Enabled` (default true) and `VersusSpecials` (default false). Online, the host's settings apply to everyone.

## Online

Everyone needs Chef Skills 4.0.0. A machine on an older version counts as not having the mod, and then the level is played without skills or specials, as before.

The host decides what happens: items, orders, fire, scores and where every chef is. Each player's machine also predicts its own chef's movement, so specials that change speed or position are applied on both. To do that quickly, specials use their own Steam P2P channel (7) through the host, alongside the game's own connections.

## Actives (EMOTE)

| Special | What it does | Cooldown |
|---|---|---|
| **VAULT** | Hop over the counter you face | 6 s |
| **BLINK** | Teleport 3 m forward, stopping before walls and edges | 8 s |
| **RECALL** | Plant a flag; use again within 20 s to teleport back to it (cooldown starts on return) | 25 s |
| **SWAP** | Trade places with the teammate you face (or the nearest) | 20 s |
| **SUMMON** | Pull the teammate you face (or the nearest) to your side | 25 s |
| **GRAPPLE** | Pull yourself to the counter you face, up to 6 m away | 12 s |
| **PACK MULE** | Carry a second item on your back; EMOTE swaps hand and back | 0.5 s |
| **POCKET** | Keep one item hidden in your pocket; EMOTE swaps it with your hand | 0.5 s |
| **TELEKINESIS** | Pull the item you face, up to 6 m away, into your hands | 10 s |
| **POWER CHOP** | Finish the item on the board you face at once | 15 s |
| **WASH BURST** | Wash every plate in the sink you face at once | 40 s |
| **PLATE CONJURER** | Conjure a clean plate in your empty hands | 30 s |
| **SPARE PAN** | Conjure a copy of the empty pan or pot you face (once per level) | once per level |
| **RESCUE** | Turn the burnt (or burning) pot or pan you hold or face back to cooked | 45 s |
| **OVERCLOCK** | The cooker or mixer you face works twice as fast for 10 s | 30 s |
| **FIRE BLAST** | Put out every fire within 3 m at once | 30 s |
| **REROLL** | Swap the newest order for a new random one | 45 s |
| **TIME OUT** | Order timers (and new orders) stop for 6 s (one per kitchen) | 60 s |
| **RALLY CRY** | Every teammate runs 30% faster for 6 s | 30 s |
| **FRENZY** | All your skills +2 for 8 s, then -2 for 8 s | 40 s |
| **OVERDRIVE** | No dash recharge for 5 s | 30 s |
| **GREASE TRAIL** | For 3 s you leave a slippery trail; other chefs who step on it slip (versus) | 25 s |
| **SNEEZE BOMB** | Every chef within 2 m of you (teammates too) sneezes and freezes for 1 s (versus) | 30 s |
| **HEX** | The chef you face: their next 5 chops don't count (versus) | 30 s |
| **DECOY ORDER** | Add a fake order to the rival team's list for 15 s (it scores nothing) (versus) | 45 s |

## Passives

| Special | What it does |
|---|---|
| **SPRINTER** | Hold DASH to sprint +50% (3 s of stamina) instead of dashing |
| **DOUBLE DASH** | Two dash charges: chain two dashes back to back |
| **CHARGE DASH** | Hold DASH to charge, release for up to twice the distance |
| **MOMENTUM** | Running without stopping builds up to +30% speed |
| **SLIPSTREAM** | Teammates running close behind you get +20% speed |
| **SURE-FOOTED** | No slipping on ice or puddles, no drag from wind or moving walkways |
| **IMMOVABLE** | Dashes, thrown items and fire can't knock you back |
| **GHOST** | Walk straight through other chefs |
| **QUICK RESPAWN** | Respawn after 1.5 s instead of 5 s |
| **SAFETY NET** | Once every 30 s, falling off puts you back where you fell |
| **CONVEYOR RIDER** | Conveyors and moving floors carry you twice as fast |
| **BIG CHEF** | Bigger: can't be knocked, knock others further, -10% speed |
| **TINY CHEF** | Smaller: +12% speed, but knocks send you further |
| **HOVER** | Dash across a gap up to one tile wide (10 s recharge) |
| **WAITER** | Put a dish onto another dish to carry both; hand one in and the other goes in too |
| **LONG ARMS** | Pick up, put down, chop and wash one tile further away |
| **MAGNET** | Loose items on the floor within 3 m slide towards you |
| **BULK BUYER** | Taking from a crate also puts a spare on a counter (15 s) |
| **CATCHER'S MITT** | Catch from any direction and further away |
| **STICKY FINGERS** | Keep what you hold when shoved, and even when you fall off the map |
| **STRONG ARM** | Throw anything: pots, pans, plates and dishes fly off as loose items (no catching) |
| **HOMING THROW** | Thrown items bend towards the teammate you face |
| **LOB** | Throws arc high over counters and chefs (same distance) |
| **ASSEMBLY LINE** | Taking from a crate facing a teammate throws it to them |
| **JUGGLER** | Re-throw within 0.5 s of a catch for a 50% longer throw |
| **REBOUND** | Uncaught throws that land near you go onto the nearest empty counter |
| **HAND-OFF** | PICK UP facing a teammate takes their item or gives yours |
| **MULTITASK** | A board or sink you leave keeps working at 25% speed |
| **ECHO KNIFE** | Each chop also chops the last other board you used |
| **LUCKY KNIFE** | Each chop has a 10% chance to finish the item at once |
| **PREP COOK** | Every 20 s, an ingredient you take from a crate comes chopped |
| **DASH-CHOP** | Dashing past a loaded board you face chops it once |
| **DISHWASHER** | Every second dish you hand in comes back clean, on a sink's drying rack |
| **BUTLER** | Your dirty plates come back straight into a sink |
| **HOT HANDS** | A pot or pan you carry keeps cooking at half speed and never burns |
| **COLD HANDS** | Picking up or putting down a pot or pan pauses its burning for 6 s |
| **SIZZLE MASTER** | Your pots and pans cook twice as fast but burn twice as fast |
| **STEADY MIXER** | Mixers you load never over-mix |
| **FIREPROOF** | Flames can't knock you back or block you: walk right through them |
| **FIRE MARSHAL** | Cookers you last put a pot or food on never catch fire (food still burns) |
| **HOSE HANDS** | Hold USE with empty hands facing a fire to spray it like an extinguisher |
| **SLOW SPREAD** | Fire near you spreads half as fast |
| **CHARMER** | Dishes you hand in give +50% tip |
| **MAITRE D'** | Every order lasts 15% longer (whole team) (one per kitchen) |
| **CLUTCH** | A dish handed in during its order's last 15% scores double |
| **COMBO KEEPER** | Once per level, a missed order keeps the tip streak (one per kitchen) |
| **SECOND CHANCE** | The first order that expires costs no points (one per kitchen) |
| **GAMBLER** | Your tips are multiplied by 0, 1, 2 or 3 at random |
| **FRESH DELIVERY** | A dish handed in within 15 s of cooking gets +40% tip |
| **CLOSE ENOUGH** | Every 60 s, a dish from this level nobody ordered counts as the oldest order (one per kitchen) |
| **FORTUNE TELLER** | See the next order before it arrives (not on levels with phases) |
| **BOUNTY HUNTER** | One order at a time is marked; handing it in scores double (one per kitchen) |
| **GOLDEN HOUR** | In the last 30 s of the level, your tips are doubled |
| **INSPIRING** | Teammates within 3 m of you chop and wash about 20% faster (one per kitchen) |
| **MEDIC** | Teammates who fall respawn next to you after 1 s |
| **BODYGUARD** | Teammates within 3 m of you can't be knocked back or slip |
| **BATTERY** | Using your active halves the cooldown of the nearest teammate still recharging |
| **BUDDY SYSTEM** | Speed buffs are shared between you and the teammate nearest you |
| **QUARTERMASTER** | Teammates taking from a crate next to you get a spare |
| **SECOND WIND** | In the last 30 s of the level, RUNNING and DASHING +3 |
| **ADRENALINE** | After a knock, slip or respawn, +40% run speed for 4 s |
| **ZEN** | Stand still (not working) 1.5 s: your next chop or wash finishes at once |
| **COFFEE RUN** | Every correct dish you hand in gives you +20% speed for 5 s |
| **GLASS CANNON** | All skills +1, but a knock, slip or fall makes them -1 for 15 s |
| **SLOW STARTER** | All skills -1 for the first 60 s, then +2 |
| **UNDER PRESSURE** | +5% speed for each order waiting, up to +25% |
| **LUCKY CHARM** | Each chop stroke (or half-second of washing) has a 15% chance to count double |
| **HOT STREAK** | Orders in a row without a miss: +3% speed each (up to 15%); at 5, +1 to every skill |
| **PORTAL PRO** | Out of a teleporter: run 40% faster and dash with no recharge for 2 s |
| **CANNONEER** | Cannons fire you the moment the button is pressed, without the fuse |
| **DRIVER** | Carts and platforms you push or steer move 50% faster |
| **WEATHERPROOF** | Wind and moving floors don't push you around |
| **LIFEGUARD** | Teammates who fall near you are put back instead |
| **SHOVE** | Your dashes knock harder and make chefs drop what they hold (versus) |
| **PICKPOCKET** | Dashing into a chef takes their item (versus) |

## Quirks

| Quirk | Drawback | Frees |
|---|---|---|
| **HEAVY BOOTS** | You can't dash at all | +2 points |
| **CLUMSY** | Dashing drops whatever you hold | +1 points |
| **WEAK ARM** | You can't throw | +1 points |
| **BUTTERFINGERS** | You can't catch thrown items | +1 points |
| **PYROPHOBE** | Fire extinguishers won't spray for you | +1 points |
| **GERMAPHOBE** | You can't wash plates | +1 points |
| **VEGETARIAN** | You won't pick up raw meat or fish | +1 points |
| **SLEEPY** | Every 30 s you doze off and can't move for 1 s | +2 points |
| **FRAGILE** | Respawning takes 10 s instead of 5 s | +1 points |
| **SHOW-OFF** | After every dish you hand in, you pose for 1 s | +1 points |
| **TUNNEL VISION** | You can only chop or only wash: the first one you do | +2 points |
| **LONE WOLF** | Teammates' specials and auras don't affect you | +1 points |

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
  - `Specials/SpecialHud.cs`: the label over each chef.
- **Effects:** each group of specials lives in its own module in `Specials/Modules/` (an `ISpecialModule`, plus its Harmony patches in namespace `ChefSkills.Specials`). A patch that doesn't fit the game version only turns off its own special; the plugin logs it and carries on.
- **Checks:** `tests/BuildPart.ps1 -Modules <file>.cs` builds the framework with one module and runs `tests/CheckChefSkills.ps1` on it. `-Modules *` builds all of them.
