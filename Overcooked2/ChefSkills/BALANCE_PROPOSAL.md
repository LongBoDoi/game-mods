# Chef Skills: balance proposal

A pass over all 9 skills, 112 specials and the point economy, based on ChefSkills 4.2.1. Each item was checked against the code (`Specials/Catalog.cs`, `Specials/Modules/*.cs`, `Skills.cs`, `Patches.cs`), not only against `SPECIAL_SKILLS.md`, because in a few places they disagree. Every number below is a starting point for playtesting, not a final value.

## Why players see "some are too strong, some too weak"

Most of the imbalance comes from a few rules, not from individual specials. Fix these and about half of the problem goes away before any special is touched.

1. **Freed points have no limit.** In 3-4 player games a specialist needs about 9 points (Running, Dashing and their role skill at +3) and can free up to 18 by dumping skills they never use (Throwing, Firefighting, Serving...). The budget never binds, so everyone plays +3/+3/+3. Quirk points are then worth almost nothing in 3-4p and far too much in solo and 2p.
2. **Nothing stops bonuses from stacking.** "+N to every skill" effects (Slow Starter, Glass Cannon, Frenzy, Second Wind, Hot Streak, Inspiring) add with no limit: Chopping can reach about +10. Speed multipliers multiply each other (Sprinter × Tiny Chef × Momentum × Running). Score multipliers multiply each other (Bounty × Clutch = ×4 on base and tip). Cook multipliers multiply each other (Overclock × Sizzle Master × Cooking +3 ≈ 8×).
3. **Battery recycles any active.** Battery refunds 50% of the *teammate's* full cooldown whatever the holder's own cooldown is, and Pocket and Pack Mule have a 0.5 s cooldown. One Battery + Pocket chef brings Time Out back almost at once.
4. **"One per kitchen" doesn't hold in solo.** Cooldowns are per chef (`ChefState.CooldownUntil`), and the one-per-kitchen check runs only on the select screen. A solo player's two chefs each get their own Time Out, Rally Cry, Reroll or Swap.
5. **About 25 specials are near-dead.** Some are dominated by a sibling (Grapple by Blink, Germaphobe by Tunnel Vision, Quick Respawn by Safety Net). Others only work on rare level types (Cannoneer, Driver, Weatherproof, Portal Pro), or never trigger (Slipstream).
6. **Some drawbacks are free.** Sleepy and Tunnel Vision give +2 for almost no cost. Quick Respawn + Fragile respawns *faster* than vanilla and still pays out a point.

## How the tiers were judged

Picks only compete within their own slot (active vs active, passive vs passive, quirk vs quirk), so each slot is rated on its own scale:

| Tier | Meaning |
|---|---|
| **S** | Must-pick: worth two or more other picks in that slot |
| **A** | Strong default |
| **B** | Par (benchmarks: Lucky Charm, Prep Cook, Long Arms, Charge Dash) |
| **C** | Situational: B or A on its own level type, idle elsewhere. Fine, because builds are picked per level after looking at the kitchen |
| **F** | Weak even on its own levels |

The goal after the changes: no S and no F in any slot, most picks at B-A, and hazard/level-specific picks at C.

---

## 1. System changes (ship these first)

| # | Change | Where | Why |
|---|---|---|---|
| S1 | **Cap freed points**: lowered skills count for at most `MaxPointsFreed` points (default `2 × MaxLevel` = 6). The quirk refund adds on top. The card shows **MAX 6 FREED**. | `Skills.cs` `FreePoints`, `Enforce`, `Sanitise`; publish in `FormatOwnRules`/`UseHostRules` | The root fix. With 6 freed, a specialist gets 7-8 of the 9 points they want, so Running vs Dashing vs role becomes a real choice, and every quirk point counts. |
| S2 | **Per-skill floor**: `[Skill.<name>] MinLevel`; Firefighting = -1, Throwing = -1 | same, plus the card's Left stops at the floor | Without it, the 6 freed points all come from the two skills nobody uses, and the cap is free. |
| S3 | **Clamp special level bonuses**: at the end of `SpecialRuntime.LevelBonus`, `Mathf.Clamp(bonus, -3, skill is Running or Dashing ? 3 : 2)` | `SpecialRuntime.cs` (`LevelBonus`) | Every single special keeps its full strength (Frenzy +2, Second Wind +3 movement); only piles are cut. Use **this one clamp only**, not a clamp in `Skills.LevelOf` as well. |
| S4 | **Cap special run/dash speed**: the product of special multipliers is at most ×1.6 before the Running/Dashing skill (upper bound only, so Frozen's 0 still applies) | `SpecialRuntime.RunMultiplier` and the dash equivalent | Stops Sprinter × Tiny × Momentum × Under Pressure × Hot Streak stacks. |
| S5 | **Cap score factors**: in `AdjustDelivery`, `tipFactor ≤ 2.5`, `scoreFactor ≤ 2.0`. A Close Enough hand-in doesn't get the Clutch or Bounty factor | `Orders.cs` `AdjustDelivery` | Bounty × Clutch is ×4 today; Charmer × Gambler × Golden Hour reaches ×4.5 tip. |
| S6 | **Cap special cook multipliers on one pot at ×4** (Overclock × Sizzle Master); the Cooking skill applies outside the cap | `Cooking.cs` `AdjustCook` | Overclock × Sizzle × Cooking +3 is about 8× today. |
| S7 | **Shared cooldowns.** (a) The chefs of one player share one active cooldown. (b) A one-per-kitchen active, when used, puts the same special on cooldown for every same-team chef (done by the host in `OnActivated`). (c) In versus, the select-screen one-per-kitchen check only counts the picker's own team | `SpecialRuntime.OnActivated`, `SkillSelectScreen.AddTeamWide` | Fixes solo double Time Out (about 40% freeze uptime), double Rally Cry and double Reroll. It also fixes two players locking in the same one-per-kitchen pick at the same moment, and one versus team blocking the other. |
| S8 | **Quirk refund rule**: +2 only for drawbacks that cost every role on every level (Heavy Boots, Sleepy after its fix). Role- or level-dependent drawbacks give +1. In single player, quirk tiles show **SOLO: BOTH CHEFS** | `Catalog.cs` refunds, picker | Gives a consistent reason for each refund (see section 5). |
| S9 | **Hidden specials**: add a `Hidden` flag. `Fits()` returns false for it (so `Enforce` drops it from last-picked loadouts on every PC) and `Choices()` leaves it out. Keep the enum numbers | `Catalog.cs` | Needed for the merges in section 4 (Cannoneer, Driver, Weatherproof). |
| S10 | **Version and config**: bump `SkillSync.Version` "7" → "8" and `SpecialNet.Protocol` 3 → 4. Add `[General] BalanceVersion`: on load, if it is below 2, move each `PercentPerLevel` still at its old default to the new one (Dashing 15 → 10, Serving 15 → 20), then save | `SkillSync.cs:81`, `SpecialNet.cs:73`, `Skills.cs` `BindSkill` | That version string is the only compatibility check, so old and new builds would otherwise play together and desync. Changing a BepInEx default doesn't change existing `.cfg` files, and online the host's file is what everyone uses. |

---

## 2. Skill levels

| Skill | Now | After | Change | Why |
|---|---|---|---|---|
| **Dashing** | S | A | 15% → **10%** per level | It scales dash speed *and* divides the recharge (`Patches.cs` ~519-536), so +3 is about 2.1× dash output, the best value per point and always maxed. At 10%: +3 ≈ 1.69×, -3 ≈ 0.49×. |
| **Running** | A | A | keep 6% | It was only automatic because points were unlimited. Under S1 it competes evenly with Dashing. |
| **Cooking** | B | B | keep 12% | README: say that the chef who puts in the last food (or sets the pot down) decides the pot's Cooking and Slow burn speed. |
| **Washing** | B | B | keep 15% | Healthy. |
| **Chopping** | B | B | keep 12%, but **keep leftover stroke credit per chef, not per item** (`Patches.cs` `ChopPatch`/`ClientChopPatch`). Extra strokes from specials (Echo, Lucky Charm, Dash-Chop, Multitask) count 1:1 and don't use or add credit | The credit is lost when an item finishes, so gains come in steps: on an 8-stroke item, +1 saves no chops at all and -3 costs much more than the card's -36%. |
| **Slow burn** | C | C | keep 20% | Insurance. 25% would make -3 burn 4× faster, and through the pot-ownership rule that mostly hurts teammates. |
| **Serving** | D | C | 15% → **20%** | A few seconds of plate return rarely matters; the plate-starved levels need a bigger upside. Not 25%: -3 would make plates take 4× as long for any casual server. |
| **Throwing** | F (dump) | C | MinLevel -1 (S2) | Its only problem was being a free 3-point dump. |
| **Firefighting** | F (dump) | C | MinLevel -1 (S2) | Same. |

---

## 3. Actives

| Special | Now | After | Change | Why |
|---|---|---|---|---|
| **POCKET** | **S** | A | Capacity 3 → **2** (`Carrying.cs` `PocketSize`). Keep the 0.5 s cooldown | The one real must-pick: 4 ingredients per crate trip, hidden, kept through falls. At 2 it still carries a full 3-ingredient pot per trip (hand + pocket), level with Pack Mule. |
| **VAULT** | A+ | A | Cooldown 6 s → **9 s** | The move is instant (the hop is only visual). At 6 s that is about 30 shortcuts a level, the biggest steady saving of any active. Follow-up only if split kitchens are still trivial: walls can't be vaulted (only obstacles under 1.4 m, measured just past the near face so counters against walls still work). |
| **PACK MULE** | A | A | keep | Its only abuse was Battery spam, fixed on Battery. |
| **BLINK** | A | A | keep 10 s / 6 m | The per-level pick for split and gap kitchens; its forced max range overshoots in tight kitchens. Docs: it always lands on the *farthest* free spot. If it dominates after the Vault change, 12 s. |
| **TIME OUT** | A (S with Battery/solo) | A | keep 10 s / 50 s | Was only broken through Battery and solo duplicates (S7, Battery). |
| **TELEKINESIS** | B | B | keep | Docs: it pulls through walls and from crates. |
| **GRAPPLE** | D | B | Cooldown 12 s → **9 s**, range 6 → **7 m** | Strictly worse than Blink today. It *does* fly over gaps (`Teleport.cs` FindGrapple, "gaps in between are flown over"), so 8 m every 7 s would beat Vault and Blink. New text: "...up to 7 m away, over gaps too". |
| **OVERCLOCK** | B | B | keep 3× / 10 s / 30 s | Only its Sizzle Master stack was a problem (S6). Docs: it burns 3× faster too. |
| **WASH BURST** | B | B | keep | Docs: refuses when the drying rack is full. |
| **RALLY CRY** | A | B | **One per kitchen** (with S7) | Fair for one user, but 3-4 staggered copies give the whole team permanent +30%. |
| **PLATE CONJURER** | A | B | The conjured plate is **used once**: on delivery, remove its plate-return entry *before* the Dishwasher and Butler branches in `Stations.cs` `Delivered()` (track it by entity ID on the host) | Today every use adds a permanent plate to the level, so the plate bottleneck is gone by mid-level. Fallback: 45 s cooldown and at most 4 per level. |
| **OVERDRIVE** | B | B | keep | Its copy through Buddy System at any range is fixed on Buddy System. |
| **FRENZY** | C | B | Cooldown 40 → **30 s**, crash 6 → **4 s** | Only safe with S3. |
| **POWER CHOP** | C | B | Cooldown 15 → **10 s** | Puts it at the same rate of saving as Wash Burst. |
| **RECALL** | C | B | Cooldown 25 → **15 s** | The cooldown only starts on return, so the real cycle is travel time + cooldown. |
| **REROLL** | F | B | **Rework**: remove the *oldest* order (a new one replaces it, no penalty) instead of the newest. Cooldown 30 → **40 s**, **one per kitchen** | Today it rerolls the order with the most time left, so it saves nothing, and on 1-3 recipe levels it often rolls the same dish. |
| **RESCUE** | C | C | Cooldown 45 → **30 s**, hold-at-done 5 → **10 s** | Only useful after a mistake, which keeps it situational. The longer hold gives you time to get the saved dish onto a plate. |
| **FIRE BLAST** | D | C | Radius 4 → **6 m**, cooldown 30 → **20 s** | A fire-level pick. With 6 m it can put out one side of the kitchen without walking up first. |
| **SWAP** | D | C | Wind-up 0.45 → **0.25 s**, flight 0.45 → **0.30 s** (both chefs frozen 0.55 s, not 0.9), cooldown 20 → **14 s**. Lone Wolf chefs can't be targeted: check in `CanBeMoved`/`PickTeammate` on the host, not only in `MateTargets` | Freezing two chefs for about a second costs about as much as it gains. Note that Swap + Pack Mule moves several items across a divider per use, so not lower than 14 s. |
| **SUMMON** | D | C | Same timing (shared constants), cooldown 25 → **14 s** | Same role as Swap, so the same cooldown. |
| **SPARE PAN** | F | C | **Rework**: 25 s cooldown, at most **one** conjured container alive per chef ("SPARE STILL OUT"). A copy disappears once it has been emptied onto a plate. Bowls too (`ServerMixingHandler`) | Once per level, then an empty active slot for the rest of it, makes it the weakest pick in the game. "3 copies, then nothing" only delays the dead slot. |

**Versus actives** (rated among versus picks only):

| Special | Now | After | Change |
|---|---|---|---|
| **DECOY ORDER** | B (dead in co-op) | B | Lifetime 15 → **25 s**. Hidden from the picker unless the level is competitive (`OrdersModule.FindFlows()` when the screen opens). Today it is offered and then refuses VERSUS ONLY |
| **SNEEZE BOMB** | C | B | Range 3 → **4 m**, cooldown 30 → **20 s**, victims drop their held item. Still hits teammates (centred on you, so you can control it) |
| **GREASE TRAIL** | D | B | Trail 3 → **4 s**, each patch lasts 3 → **6 s**, width 0.55 → **0.7 m**, cooldown 25 → **20 s**. Your own team is immune *only in versus* (in co-op party mode it keeps hitting everyone) |
| **HEX** | F | C | 5 → **10** strokes, range 3.5 → **5 m**, cooldown 30 → **20 s**. Also skips the first 3 s of a hexed chef's washing. Send the hex start time with the existing hex message and apply the skip in the sink patch on every PC |

---

## 4. Passives

### Nerfs

| Special | Now | After | Change | Why |
|---|---|---|---|---|
| **BOUNTY HUNTER** | **S** | A | ×2 → **×1.5**. After a claim, wait **15 s** before marking the next bounty, per team (a per-team pause, not the shared `s_nextBountyCheck`) | A new bounty is marked the moment one is claimed, so about half of all dishes score double: about +40-50% team score for one pick. After the change, about +15-20%. |
| **BATTERY** | **S** | B | **One per kitchen.** Ignore holders whose active has a catalog cooldown under 5 s. Refund = `min(left, 0.35 × target cooldown, 0.5 × holder's catalog cooldown)`. Skip chefs of the holder's own player (solo) | It recycles Time Out off a 0.5 s Pocket. Without the holder cap, even a 10 s Blink holder keeps Time Out up about 55% of the time. Text: "Your active (5 s+ cooldown) cuts up to 35% off a teammate's cooldown, at most half your own." |
| **GLASS CANNON** | **S** | A | Knocks blocked by Immovable, Big Chef *or* Bodyguard still crack it (call `OnKnocked` in **both** knock-blocking prefixes, `TeamBuffs.cs` and `Movement.cs`). Team expiries and wrong dishes crack it too (add it to the `AnyHas` gates in the expiry and failed-delivery patches). Crack 15 → **20 s** | +1 to all 9 skills with almost 100% uptime, because knocks are rare in co-op and the blocked-knock path never cracks it. |
| **SLOW STARTER** | **S** | B | After the warm-up **+1** instead of +2, warm-up 60 → **45 s** | Averages +1.0 to every skill today. After the change, about +0.5. |
| **SIZZLE MASTER** | A+ | A | ×2 → **×1.6** (cooking and burning), plus S6 | +100% is about 3× Cooking +3, and Cold Hands removes the burn risk. Update the pulse text "x2" → "x1.6". |
| **MULTITASK** | A (S in solo) | A | At most **2 background jobs** per chef (boards and sinks together; a new one replaces the oldest), rate 0.4 → **0.35** | There is no limit today: 4 loaded boards is about 1.6 extra chefs of chopping. A rate cut alone doesn't fix that. |
| **ECHO KNIFE** | A+ | A | Echo on **60%** of chops, only to a board within **6 m** | Deterministic and with no range limit, it doubles a chopper's output across the whole kitchen. The rolls are made on the host and sent with `TopicEcho`, so clients stay in sync. |
| **SPRINTER** | A+ | A | Sprint = `1 + 0.35 × Multiplier(Dashing)`: +24.5% at Dashing -3, +35% at 0, +45.5% at +3 (with Dashing at 10%) | Sprinter can never dash (`CanDash` false), so Dashing became a free -3 dump on top of +50% speed (2.04× with Tiny Chef and Running +3). Docs must say it can't dash. |
| **MAITRE D'** | A | A | Order time ×1.25 → **×1.15** | Longer orders also raise the time-left share that tips are computed from, so this is a hidden tip boost too. Docs: only on fixed-time order levels. |
| **HOMING THROW** | A | B | Range 14 m → **10 m × Throwing multiplier**, aim cone 50° → **30°**. The same range scaling for Assembly Line | Range ignores Throwing today, so Throwing is a free dump. The 50° cone also redirects throws aimed at pots and boards. |
| **HOT STREAK** | A | B | Keep +3% speed per order; the **+1 all skills** needs a streak of **8** instead of 5 | Teammates build it for you, and once at 5 it lasts most of the level. Docs: the streak counts the team's hand-ins; the +1 skips Running and Dashing. |
| **UNDER PRESSURE** | A | B | Counts only orders **beyond the second**, **+7%** each, up to **+21%** | The bar almost always holds 3-5 orders, so today it is a free +15-25% all the time. |
| **CHARMER** | A | B | Tip ×1.5 → **×1.35** | The unconditional +50% that Gambler (same expected value) and Fresh Delivery (same bonus, with a condition) can't beat. |
| **GAMBLER** | D | B | Roll {0, 1, 2, 3} → **{0, 1, 2, 2.5}** (expected ×1.375) | Keeps it within about 0.03 of Charmer. Score-attack players replay levels, so variance costs them nothing and an expected ×1.5 would win. |
| **CLUTCH** | A | B | ×2 → **×1.5**, window last 25% → **last 20%** | ×2 on base and tip makes "hold every dish until it turns red" the best strategy. Update the pulse "x2". |
| **DISHWASHER** | A | B | Every **third** dish instead of every second | On sink levels it removes about half of all washing with no input. |
| **ZEN** | B (S in solo) | B | Stillness only builds while a player controls the chef (the same `controls.enabled && !IsSuppressed()` test as `SpecialRuntime`) | In solo, the parked chef is always charged, so switching makes every chop and wash instant. |
| **TINY CHEF** | A | B | Speed ×1.15 → **×1.10** | Always on, multiplied with everything else, and its drawback rarely matters in co-op. |
| **DASH-CHOP** | B | B | Keep 3 strokes; each board can be hit at most once per **2 s** | Stops dash-spam with Double Dash, Overdrive or Adrenaline. |

### Buffs

| Special | Now | After | Change |
|---|---|---|---|
| **DOUBLE DASH** | C | B | Charges refill **25% faster** (`DashCooldown × 0.75`) | Today its long-run dash rate is the same as vanilla; it only adds a burst. |
| **FRESH DELIVERY** | D | B | Keep ×1.5, window 20 → **25 s**. Docs: counts from the *oldest* cooked part; no-cook dishes never qualify | Not ×1.75: against Charmer ×1.35 that would be the new default pick on half the levels. |
| **GOLDEN HOUR** | D | B | Last 45 s → **last 60 s**. Fix the "closer" combo text (it says 30 s). Docs: never triggers on untimed levels | |
| **SECOND CHANCE** | D | B | An order's first expiry costs **nothing**: no points and no combo (restore the multiplier and combo captured at expiry), with a **30 s team recharge**. **Second Chance and Combo Keeper exclude each other** across the kitchen. In `AfterExpiry`, set a `forgiven` flag so Combo Keeper doesn't fire on an expiry that was already forgiven | Today it refunds only the smaller part of an expiry's cost. Full forgiveness with no recharge, stacked with Combo Keeper, would mean the team's combo never breaks. |
| **COMBO KEEPER** | D | C | Recharge 40 → **25 s**; spends its charge only when the multiplier is above ×1; also keeps Hot Streak | |
| **SECOND WIND** | D | B | Last 30 s → **last 60 s** | Pairs with Golden Hour for the closer build. Docs: does nothing on untimed levels. |
| **STRONG ARM** | D | B (split levels) | Catch radius 1.1 → **1.8 m**, force 0.85 → **1.0**, and it aims at a teammate you face within 8 m | Reliable pot and dish passes give it a job Homing Throw (ingredients) doesn't do. |
| **COFFEE RUN** | C | C | Buff 6 → **10 s** | |
| **BUDDY SYSTEM** | C | C | Range 4 → **5 m**. **Bug fix:** share speed buffs only with a partner in range (`NearestTeammate` is called with `float.MaxValue/4`, so today it copies Overdrive or Adrenaline across the map) | Not 6 m / +20%: in 3-4p that is almost always on. |
| **HOT HANDS** | C | C | In-hand rate 1.0 → **1.25**, and **remove the Sizzle Master factor** in `TickHotHands` (it sits outside the S6 cap) | At 1.5 × Sizzle a carried pot would cook faster than one on the heat and couldn't burn. |
| **FIRE MARSHAL** | C | C | Food past done in its pots burns **half as fast** | Today the cooker is saved but the food still burns, which is most of the cost. |
| **STEADY MIXER** | D | C (B on mixer levels) | Mix speed ×1.3 → **×1.75** | |
| **ADRENALINE** | D | C | 5 → **8 s**. Also triggers for every Adrenaline chef on a team expiry or wrong dish (add it to the same `AnyHas` gates as Glass Cannon) | |
| **BODYGUARD** | D | C | Range 4 → **6 m** | |
| **LIFEGUARD** | D | C | Range 4.5 → **7 m**; ignores Lone Wolf chefs | |
| **MEDIC** | D | C | Teammates who fall **empty-handed** (nothing in the hands, Pocket or on a Pack Mule back) reappear next to you after 1 s; a chef carrying anything respawns normally. Your own respawn is at most 2.5 s. Ignores Lone Wolf chefs | Without the empty-handed rule, Medic + Sticky Fingers/Pocket/Pack Mule turns every pit into an item teleport across a split kitchen. |
| **QUICK RESPAWN** | F | C | 1.5 → **1.0 s**, and you respawn at your **last safe spot** (reuse `SafeSpot()`) | An unlimited but slower Safety Net, instead of a strictly worse one. |
| **STICKY FINGERS** | F | C | Also: falling while holding something respawns you after 3 s (Quick Respawn or Medic win if shorter) | |
| **BUTLER** | D | C | Your dirty plates also come back **twice as fast** | |
| **ASSEMBLY LINE** | D | C | Skips teammates whose hands are full or busy; aim cone 35° → 45°, window 1.5 → **2.5 s**; range scales with Throwing | |
| **CATCHER'S MITT** | D | C | Catch distance `max(base+1, 3.2)` → `max(base+1.5, 4.0)`; with the mitt, Strong Arm tosses are caught from 2.5 m | |
| **QUARTERMASTER** | D | C | Recharge 10 → **5 s**, range 3.5 → **5 m** | |
| **BIG CHEF** | F | C | Speed ×0.9 → **×0.95**, and dashes go **25% further** | Gives it a dash niche against Tiny Chef's run speed. |
| **SLOW SPREAD** | F | C | Fire **can't spread** within **7 m** of you (was 3× slower within 5 m) | A clear firebreak role, distinct from Fireproof and Hose Hands. |
| **LOB** | F | C | Throws go **30% further** (×1.14 speed before the 60° rescale) | Homing for accuracy, Lob for range and clearance. |
| **JUGGLER** | F | C | Window 1 → **2 s**, and grabbing from a crate also opens it | Its trigger becomes common. |
| **REBOUND** | F | C | Includes your own throws; ranges 3 → 4.5 m and 5 → 6 m; a throw that falls 1.5 m below its launch height counts as landed (saves pit throws) | If still unpicked after this pass, merge it into Catcher's Mitt. |
| **HAND-OFF** | F | C | **Give only** (no taking), range 1.7 → **3 m**, cone 50° → 30° | Taking a teammate's item by accident made it a liability. |
| **MAGNET** | F | C | Range 4 → 6 m, speed 3.5 → 6 m/s; with empty hands the item goes straight into them; ignores items within 1.5 m of another chef | It stops pulling away food a teammate has put down to stage. |
| **BULK BUYER** | F | C | A choppable spare goes onto the nearest **empty board** within 6 m first; recharge 12 → **8 s** | |
| **SLIPSTREAM** | F | C | Range 4 → 5 m, width 1.5 → 3 m; only rejects teammates heading the opposite way; linger 1 → 2 s; bonus 30% → **20%**, and the leader gets **+10%** while someone is in their stream. Text: "co-op only" | Today it almost never triggers. |
| **FORTUNE TELLER** | F | C | **One per kitchen**; the foretold order arrives with **+10% time** | Gives it a concrete effect (duplicate picks did nothing). It applies only to that order, so it doesn't stack with Maitre D'. |

### Reworks and merges (level-hazard passives)

Today five passives share three "hazard" rules, and three of them do nothing on about 90% of levels. They become four with distinct jobs:

| Special | Now | After | New job |
|---|---|---|---|
| **SURE-FOOTED** | C | C (A on ice) | Footing: no slipping on ice, slopes or puddles; walkways never slow you down but still carry you (`SurfaceRule = RuleTailwind`). No wind rule any more. Today it also blocks *helpful* walkway pushes, against its own text. Make the ring/label show Sure-Footed, not Weatherproof |
| **IMMOVABLE** | F | C | Knocks **and wind** can't move you (`WindRule = RuleBlock`) |
| **CONVEYOR RIDER** | F | C | Moving floors **and wind** carry you twice as fast (`WindRule = RuleRider`, checked before Immovable) |
| **PORTAL PRO → TRANSIT PRO** | F | C | After a teleporter, a cannon landing or leaving a steered platform: +40% run and free dashes for **4 s** (was 2 s). Also gets Cannoneer's fuse skip and Driver's ×1.5 steering |
| CANNONEER, DRIVER | F | hidden (S9) | Merged into Transit Pro |
| WEATHERPROOF | F | hidden (S9) | Merged into Sure-Footed |

### Bug fixes found along the way

- **Waiter:** the second dish is handed in even when it matches no order, which costs the combo. Hand it in only if it matches a live order; otherwise keep it in your hands.
- **Shove / Pickpocket:** they hit teammates. Skip own-team victims **in versus only**. Shove also: strength 1.75 → 1.4, and the same victim can't be made to drop again for 3 s (was 0.4 s).
- **Hover:** the limit is 0.6 s of flight, not "one tile" as the docs say.

### Keep as they are

Long Arms, Prep Cook, Lucky Charm, Inspiring, Charge Dash, Momentum, Cold Hands, Close Enough (except S5), Fireproof, Hose Hands, Safety Net, Hover, Ghost, Pickpocket (except the own-team fix), and **Lucky Knife** at 10%.

> Lucky Knife looks weak on paper, but the roll happens on every stroke and finishes the item. Expected strokes are `(1-(1-p)^N)/p`: at 10% that is 1.28× faster on 6-stroke items and 1.40× on 8-stroke items, already above Lucky Charm on chopping. 18% would make it 1.55-1.81×, the new chopping must-pick. Fix only its text ("about 30-40% faster").

---

## 5. Quirks

With S1 in place, one point is worth about 3-6% of a chef's output. A **+1** quirk should be free in some roles or levels and cost something in others: the player reads the level and their role. A **+2** must cost about 5-8% for everyone.

| Quirk | Now | After | Change | Why |
|---|---|---|---|---|
| **SLEEPY** (+2) | **S** | B | Doze 1 → **2 s**. While dozing you can't chop or wash either: use a separate "dozing" flag from the Sleepy schedule (not `BuffId.Frozen`, which Sneeze, Grease, Swap and Show-Off also use), checked in `BeforeChop`, `BeforeClientChop` and the sink patch on every PC | Today it only stops movement, so a chef parked at a board loses nothing. 2 s every 30 s, with no dodging, is about 6.7% of the chef's time, what +2 is worth. |
| **TUNNEL VISION** (+2) | **S** | B | Refund 2 → **+1**. Lock to the first **station** used (not the first activity), using a key that is the same on every PC (the station's entity ID or rounded position; **not** `GetInstanceID`, which differs per machine), with sinks within 1.5 m counting as one. Locks **per player**, so in solo both chefs are locked | Today chef A chops and chef B washes in solo for free, and its cost (losing chopping *or* washing, whichever you don't do) is never more than Germaphobe's, so Germaphobe is dead. |
| **FRAGILE** (+1) | A | B | Respawn **+5 s** (added) instead of ×2. Clashes with **Safety Net** | ×2 is applied *after* Quick Respawn/Medic, so Quick Respawn + Fragile = 3 s, faster than vanilla, plus a point. With +5 s: Quick Respawn 1 s + 5 = 6 s. No Quick Respawn clash needed. Docs: "Respawning takes 5 s longer" (the current "10 s instead of 5 s" is wrong; the game uses each level's own respawn time). |
| **PYROPHOBE** (+1) | A | B | Also clashes with **Fire Blast** and **Fireproof** | Today you can buy the drawback back with a special and also dump Firefighting -3: +4 points for nothing. |
| **LONE WOLF** (+1) | A | B | Make it match the docs: Medic, Lifeguard, Swap and Summon really skip Lone Wolf chefs (host-side checks) | Today it still gets the strongest team rescues while ignoring teammates' debuffs. |
| **HEAVY BOOTS** (+2) | A | B | Keep +2. Clashes with **Adrenaline**. **Dashing is locked at 0 or above** while it's picked | Under S1 the dumped Dashing counts toward the 6 freed points, but in solo and 2p, where useful dump stats are scarce, a free Dashing -3 still makes it about +5. |
| **WEAK ARM / GERMAPHOBE** (+1) | B / D | B | Also lock their dead skill at 0 or above (Throwing / Washing) while picked. Germaphobe comes back once Tunnel Vision stops dominating it | Same reasoning as Heavy Boots. The dead-skill lock needs the loadout passed into `Enforce`/`Sanitise` (today they only receive the refund). |
| **CLUMSY** (+1) | D | B | Dashing drops what you hold **only if it's a plate** (clean, dirty or with food) | It costs every role, so there is always a free +1 that beats it. Limited to plates, it's free for choppers and cooks and costly for platers, servers and washers. |
| **VEGETARIAN** (+1) | A | A | keep. Docs: it also blocks catching, and Homing/Assembly passes skip you; seafood and processed meat count | The popular default, but not a must-pick: every role has another equally free +1. |
| **BUTTERFINGERS, SHOW-OFF** (+1) | B | B | keep | Role quirks of the right size. |

---

## 6. Degenerate combos, and what closes each

| Combo | Closed by |
|---|---|
| Battery + Pocket/Pack Mule → Time Out back in about 1 s | Battery changes (5 s minimum, holder cap, one per kitchen) |
| Solo: two chefs, two Time Outs (about 40% freeze), two Rally Cries, Battery between your own chefs | S7, Battery skips same-player chefs |
| Bounty Hunter + Clutch = ×4 base and tip | Both ×1.5, S5 cap ×2.0 |
| Charmer + Gambler + Golden Hour = up to ×4.5 tip | Charmer 1.35, Gambler EV 1.375, S5 cap ×2.5 |
| Glass Cannon + Immovable/Big Chef/Bodyguard = never cracks | Blocked knocks crack it; team mistakes crack it |
| Slow Starter + Glass Cannon + Frenzy + Second Wind + Inspiring = Chopping about +10 | S3 clamp, Slow Starter +1 |
| Sprinter + Dashing -3 + Tiny Chef + Running +3 = 2.04× | Sprint scales with Dashing, Tiny 1.10, S4 cap |
| Heavy Boots + Dashing -3 (a free extra 3 points) | S1, Dashing locked at 0 or above |
| Quick Respawn + Fragile = respawn faster than vanilla, +1 point | Fragile +5 s (added) |
| Pyrophobe + Fire Blast/Fireproof + Firefighting -3 | Clashes, S2 floor |
| Tunnel Vision in solo (one chef chops, one washes) | Station lock per player |
| Sizzle Master + Cold Hands (2× cooking with no risk) | Sizzle 1.6 |
| Overclock + Sizzle + Cooking +3 ≈ 8× | S6 cap ×4 |
| Hot Hands + Sizzle (carried pot cooks faster than on the heat and can't burn) | Hot Hands 1.25 without Sizzle |
| Echo Knife + Multitask + Chopping +3 ≈ 2.4× on several boards | Echo 60% / 6 m, Multitask 2 jobs / 0.35 |
| Zen in solo (parked chef always charged) | Stillness only builds while player-controlled |
| Rally Cry from 3-4 players = permanent +30% | One per kitchen + S7 |
| Medic + Sticky Fingers/Pocket/Pack Mule = pit teleport with items | Medic only for empty-handed chefs |
| Buddy System copies Overdrive/Adrenaline at any range | Shares only within range |
| Homing Throw/Assembly Line + Throwing -3 (range doesn't care) | Range scales with Throwing, S2 floor |
| Second Chance (full forgiveness) + Combo Keeper + Maitre D' = combo never breaks | Second Chance and Combo Keeper exclude each other; 30 s team recharge |
| Under Pressure + Hot Streak = +44% speed nearly always | Under Pressure rework, Hot Streak at 8, S4 cap |
| Dash-Chop + Double Dash/Overdrive/Adrenaline spam | 2 s per-board lockout |

---

## 7. Implementation notes (things that are easy to miss)

- **Every machine runs part of the simulation.** Changes to chopping, washing, movement, Sleepy, Hex on sinks, the Tunnel Vision lock and Swap/Summon timing must run identically on host and client (`BeforeChop` *and* `BeforeClientChop`; the sink patch on every PC). That is why S10's version bump is required, not optional.
- **Two knock-blocking paths exist** (`TeamBuffsImpactPatch` for Bodyguard, `Movement.cs` ~1145 for Immovable/Big Chef). Glass Cannon needs a hook in both.
- **Expiry hooks are gated.** `AfterExpiry` and the failed-delivery patch only run when someone has Second Chance, Combo Keeper or Hot Streak. Add Glass Cannon and Adrenaline to those `AnyHas` checks, and move their handling ahead of Combo Keeper's early return.
- **Bounty's timer is shared** by both teams (`s_nextBountyCheck`), and the same loop re-marks expired bounties. Use a per-team pause instead of pushing the global timer.
- **Fortune Teller and Maitre D'** both touch order lifetime (`OrdersLifetimePatch` returns early when nobody has Maitre D'). Apply Fortune Teller's +10% only to the foretold order so the two never multiply.
- **Merged specials** must be refused in `Fits()`, not only hidden in the picker, or a saved last-picked loadout brings them back.
- **Text updates for each change:** the English *and* Vietnamese catalog strings (`Catalog.cs` ~290-430), `SPECIAL_SKILLS.md`, the README skills table, and the in-game pulses and chips: Clutch "x2", **BOUNTY! x2**, Sizzle "x2", Hot Streak's **MAX STREAK! +1 SKILLS** (now at 8), Slow Starter's **WARMED UP!** chip.
- **Catalog line numbers** for reference: Vault 156, Recall 166, Swap 167, Summon 168, Grapple 175, Pack Mule 178, Pocket 179, Time Out 230, Rally Cry 237, Battery 241, quirks 274-285, clashes 100-108.

### Docs that disagree with the code today (fix even if no balance change ships)

- Vault lands just past an obstacle within 1.6 m (not "up to 4 m"), and the move is instant.
- Blink always takes the farthest free spot.
- Sprinter removes dashing entirely.
- Momentum isn't lost instantly on a stop (0.4 s grace, then it fades); chopping or washing resets it at once.
- Quick Respawn and Fragile use the level's own respawn time, not a fixed 5 s.
- Homing Throw range ignores Throwing; Juggler does nothing with Homing or Assembly Line.
- Under Pressure counts every order on the bar; Coffee Run and Under Pressure are run speed only.
- Inspiring gives +24% chopping and +30% washing to teammates, nothing to yourself.
- Maitre D' only works on fixed-time order levels; Fortune Teller isn't one per kitchen.
- Golden Hour is 45 s, but the "closer" combo says 30 s. Second Wind and Golden Hour do nothing on untimed levels.
- Fire Marshal saves the cooker, not the food; Slow Spread only slows new fires.
- Sleepy chefs can still chop and wash, with a 2 s warning first.
- Lone Wolf still gets Medic, Lifeguard and Swap/Summon. Shove and Pickpocket hit teammates.
- README: `MaxLevel` isn't really "the highest and lowest level any skill can go" (specials go past it). Dashing "+45%" is about 2.1× output because the recharge scales too. Chopping "1.36×" comes in steps.

---

## 8. Suggested order and playtest checks

1. **Patch 1: rules and exploits.** S1-S10, Battery, solo shared cooldowns, the Bounty/Clutch/Glass Cannon/Slow Starter nerfs, Sleepy and Tunnel Vision, Fragile, the Dashing and Serving percentages, the chop-credit fix. This removes every S-tier pick.
2. **Patch 2: the remaining nerfs.** Pocket, Vault, Sizzle, Multitask, Echo, Sprinter, Charmer/Gambler, Homing, Hot Streak, Under Pressure, Dishwasher, Zen, Tiny Chef, Maitre D'.
3. **Patch 3: buffs and reworks for dead picks.** Grapple, Reroll, Spare Pan, Plate Conjurer, the hazard merge, the throwing helpers, the fall specials.

Shipping in this order matters: S1 makes every quirk point valuable, so it must ship **together with** the Sleepy, Tunnel Vision and Dashing fixes, or the current outliers get stronger.

**Playtest checks:**

- Split kitchens: Vault (9 s) vs Blink vs Grapple (9 s, over gaps). If one still wins everywhere, apply the Vault "under 1.4 m" rule or Blink 12 s.
- Solo with Time Out: freeze uptime should be about 20%, not 40%.
- 4-player with Battery on Time Out: Time Out should come back about every 35 s, not every few seconds.
- A full level with Glass Cannon in a decent team: about 70-85% uptime.
- Cooker-limited levels with Spare Pan: no pile-up of pans.
- Whether the throwing helpers (Lob, Juggler, Rebound, Hand-Off, Magnet) get picked. If not, merge rather than buff further: Rebound into Catcher's Mitt, Quartermaster into Bulk Buyer.

Consider logging each player's locked-in picks (the loadout is already in the lobby data) so pick rates can confirm or overturn these tiers after release.
