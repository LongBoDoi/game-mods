# Passive Levels and Two Actives: Design Review

A review of the proposed loadout change for ChefSkills:

- **Every passive gets a fixed level of 1, 2 or 3**, set by how strong it is. Level 1 is a light pick and level 3 a build-defining one.
- **A chef's passives can add up to at most 3 levels.** That allows one level-3, or a level-2 plus a level-1, or three level-1s.
- **Each chef picks 2 actives** instead of 1.
- **The quirk** stays as it is: one, optional.

This document gives a level to all 75 passives (section 2), says what has to change so the levels hold up, and covers what the code needs.

Numbers were checked against the code, with file:line where it matters. In the game UI, consider calling this the passive's **cost** or **weight**: "level" already means the skill levels from −3 to +3, and the game's own levels.

---

## 0. Short answer

**Yes, the system can take this design, and it is a good fit.** A fixed level per passive is just one more number in the catalog. Most of the work is the second active, not the levels.

| Question | Answer |
|---|---|
| Does the loadout/sync format fit? | Yes. A loadout becomes 6 picks instead of 4 (2 actives, 3 passives, 1 quirk). A passive's level is catalog data, like a cooldown, so it isn't stored per player. It needs a version bump. |
| Do the passive effects need code changes for levels? | **No.** A passive behaves the same whatever its level. Only the balance fixes in section 3 change numbers. |
| Does a second active "just work"? | **No. This is the biggest job.** Each chef has one cooldown timer, one skill key, one aim session and one HUD button (SpecialRuntime.cs:88-90). Six places in the modules read "the" active directly. Section 6 covers it. |
| Does the budget fix the imbalance players complain about? | **Mostly.** The strong passives now cost the whole budget, and about 15 broken same-chef combos become impossible (section 4). Four things the budget can't reach still need fixing: combos across two chefs, combos through actives, solo play, and passives that do nothing at any level. |
| Overall power | Passives get a bit weaker: today's two passives are often S + A, and the budget now holds about one A. The second active gives about 1.6× as many active uses. Builds get wider, not stronger. |
| Effort | About **2-2.5 weeks** for one developer, plus playtesting (section 7). |

**Result:** 11 passives at level 3, 30 at level 2, 29 at level 1, and 5 hidden or merged into another passive.

---

## 1. How the levels were set

**Calibration.** Three level-1s, a level-2 plus a level-1, and one level-3 should all be worth about the same:

| Level | Worth about | Tier |
|---|---|---|
| 1 | half a balanced passive | C: useful, narrow or conditional |
| 2 | one balanced passive | B: solid |
| 3 | 1.5-1.8 balanced passives | A: defines a role |

What that means at the edges:
- **S-tier passives** (must-picks today) are too strong even at level 3, so they get a nerf.
- **D- and F-tier passives** are too weak even at level 1, so they get a buff, mostly the buffs already in [BALANCE_PROPOSAL.md](BALANCE_PROPOSAL.md).

The **Level** column below is the level **after** the change in the same row.

**Situational passives are priced on the levels where you'd take them.** Players pick again at the start of every level (README:3). A passive that is B-tier on ice levels and dead elsewhere is therefore level 2. Leaving it at level 1 would make it a free auto-pick on its levels.

**Level-1 passives must have a condition or a drawback.** Every chef will fill leftover points with level-1s. A level-1 that is "always on, no catch" becomes everyone's default third pick. That is why Big Chef keeps a small speed penalty and Buddy System is level 2.

**Player count.** A team-wide passive costs the same in every mode, but it is worth more with more teammates. Its level is tuned for 2 players. In 4 players, teams will take all six team-wide passives (3+3+2+2+1+1 = 12, which fits a 4-player team's budget). That's acceptable.

**Solo.** One card covers both chefs (Skills.cs:365-376), so per-chef passives count twice. Rather than give solo its own levels, the solo fixes go into the passives themselves (Multitask, Zen, Inspiring).

---

## 2. The level of every passive

**Bold** marks a level or number that differs from what you'd assume today. *Tier* is the current-code rating from the earlier review.

### Movement

| Passive | Today | Tier | Level | Change needed |
|---|---|---|---|---|
| Sprinter | Hold DASH: run ×1.5 for 4 s; can't dash | A+ | **3** | Sprint bonus scales with Dashing: 1 + 0.35 × Dashing multiplier. Otherwise Dashing is a free −3 skill dump. |
| Double Dash | 2 dash charges | C | 1 | — |
| Charge Dash | Hold to charge, up to ×2 dash | B | 2 | — |
| Momentum | Up to +30% after 3 s running | B | 2 | — |
| Slipstream | Mates behind you +30% (4 × 1.5 m) | F | 1 | **Buff:** proposal rework (5 × 3 m, 2 s linger, you +10%) |
| Sure-Footed | No slips, no wind, no floor push | C (A on ice) | 2 | Footing only (ice, slopes, puddles, walkways). Wind moves to Immovable. |
| Immovable | Knocks can't move you | F | 1 | **Buff:** wind can't move you either |
| Ghost | Walk through chefs | C | 1 | In versus, only through your own team |
| Quick Respawn | Respawn in 1.5 s | F | 1 | **Buff:** 1.0 s, at your last safe spot |
| Safety Net | A fall puts you back, every 30 s | B on fall levels | 2 | — |
| Conveyor Rider | Helpful floors ×2 | F | 1 | **Buff:** helpful wind too |
| Big Chef | Can't be knocked; −10% speed | F | 1 | **Buff:** −5% speed (not 0), dash stride ×1.25. Its knock factor doesn't multiply a Shove. |
| Tiny Chef | +15% speed; knocked further | A | **2** | — (at level 3 nobody would take it over Hot Streak) |
| Hover | Float 0.6 s over gaps, every 10 s | C (A on gaps) | 2 | — |

### Carrying & throwing

| Passive | Today | Tier | Level | Change needed |
|---|---|---|---|---|
| Waiter | A second dish rides on your arm | B | 2 | Live-order bug fix (proposal) |
| Long Arms | Reach one tile further | B | 2 | — |
| Magnet | Floor items slide to you (4 m) | F | — | **Hidden: merged into Catcher's Mitt** |
| Bulk Buyer | Spare crate item, every 12 s | F | 1 | **Buff:** proposal rework (spare on a board, 8 s) |
| Catcher's Mitt | Catch from any side | D | 1 | **Buff:** also gets Rebound and Magnet. Uncaught throws within 4.5 m hop onto a free counter, and floor items within 6 m slide to you. |
| Sticky Fingers | Keep your item through falls, Shove and Pickpocket | F | 1 | **Buff:** 3 s respawn while holding (proposal). In versus, the Shove/Pickpocket guard recharges every 8 s. |
| Strong Arm | Toss pots and plates to mates | D | 1 | **Buff:** catch radius 1.8 m, full force. No aiming: aiming would make it level 2. |
| Homing Throw | Throws home to a mate within 14 m | A | **3** | Range 10 m × Throwing, 30° cone (proposal). Otherwise Throwing is a free −3 dump. |
| Lob | Throws arc over counters | F | 1 | **Buff:** +30% range |
| Assembly Line | Crate item auto-thrown to the mate you face | D | 1 | **Buff:** 2.5 s window. Range scales with Throwing. |
| Juggler | +50% range right after a catch | F | 1 | **Buff:** 2 s window, also after a crate pickup |
| Rebound | Missed throws land on a counter | F | — | **Hidden: merged into Catcher's Mitt** |
| Hand-Off | Give or take an item at 1.7 m | F | 1 | **Buff:** give only, 3 m |

### Prep & cooking

| Passive | Today | Tier | Level | Change needed |
|---|---|---|---|---|
| Multitask | Boards and sinks you leave keep working at 0.4, no limit | A (S solo) | **3** | Max 2 background jobs **per player**. The rate stays 0.4. |
| Echo Knife | Every stroke also strokes your last other board | A+ | **3** | 60% chance, other board within 6 m (proposal). Needs the chop-credit fix. |
| Lucky Knife | 10% per stroke to finish the item | B | 2 | — |
| Prep Cook | A crate item comes pre-chopped every 15 s | B | 2 | — |
| Dash-Chop | Dashing past a board = 3 strokes | B | 2 | 2 s lockout per board |
| Dishwasher | Every 2nd dish returns clean | A | **3** | — (the proposal's "every 3rd" nerf isn't needed now) |
| Butler | Dirty plates go to the sink | D | 1 | **Buff:** they come back 2× faster |
| Hot Hands | Pot cooks in your hands | C | 1 | — |
| Cold Hands | A done pot holds for 8 s | B | 2 | — |
| Sizzle Master | ×2 cooking and burning | A+ | **3** | — (keep ×2). S6 cook cap ×4 still needed for Overclock. |
| Steady Mixer | ×1.3 mixing, never over-mixes | D | **2** | **Buff:** ×1.75 (B on mixer levels) |
| Fireproof | Walk through fire, stamp it out | B on fire levels | 2 | — |
| Fire Marshal | Your cooker can't catch fire | C | 1 | — |
| Hose Hands | Spray fire empty-handed | B on fire levels | 2 | — |
| Slow Spread | Fire spreads at 1/3 speed within 5 m | F | 1 | **Buff:** 1/4 speed within 7 m |

### Orders & score

| Passive | Today | Tier | Level | Change needed |
|---|---|---|---|---|
| Charmer | Tips ×1.5 | A | **2** | — (keep ×1.5) |
| Maitre D' *(team)* | Orders last ×1.25 | A | **3** | — |
| Clutch | Score ×2 in an order's last 25% | A | **2** | **Nerf:** ×1.5 in the last 20% (proposal). At ×2, "hold every dish till it turns red" is the server's must-pick. |
| Combo Keeper *(team)* | A miss keeps the combo, every 40 s | D | 1 | **Buff:** every 25 s |
| Second Chance *(team)* | Each order's first expiry costs no points | D | **2** | **Buff:** also keeps the combo, with a 30 s team recharge. Excludes Combo Keeper across the kitchen. |
| Gambler | Tip × a roll of 0, 1, 2 or 3 (avg ×1.5) | D | **2** | — (same average as Charmer) |
| Fresh Delivery | Tips ×1.5 within 20 s of cooking | D | 1 | — (about half of Charmer once tips are added, not multiplied) |
| Close Enough *(team)* | Every 45 s a wrong dish counts as the oldest order | B | 2 | Those hand-ins get no Clutch or Bounty bonus |
| Fortune Teller | Shows the next order early | F | 1 | **Buff:** the foretold order gets +10% time |
| Bounty Hunter *(team)* | The marked order scores ×2; next one marked at once | **S** | 3 | **Nerf:** ×1.5, 15 s pause before the next mark, tracked **per team** (not the shared `s_nextBountyCheck`, Orders.cs:1393) |
| Golden Hour | Tips ×2 in the last 45 s | D | 1 | — (about half of Charmer) |

### Team support

| Passive | Today | Tier | Level | Change needed |
|---|---|---|---|---|
| Inspiring *(team)* | Mates within 3.5 m: +2 Chopping and Washing levels | B | 2 | Doesn't count chefs of the same player (solo) |
| Medic | Mates respawn next to you after 1 s | D | 1 | **Buff:** proposal (only empty-handed mates; your own respawn ≤ 2.5 s) |
| Bodyguard | Mates within 4 m can't be knocked or slip | D | **2** | **Buff:** 6 m (on ice it gives the team Sure-Footed) |
| Battery | Your active refunds 0.5 × a mate's cooldown | **S** | 3 | **Nerf:** one charge per 10 s, with a refund cap (section 6.4) |
| Buddy System | You and the nearest mate within 4 m run +15% | C | **2** | Fix the buff-sharing range bug (TeamBuffs.cs:580, 586) |
| Quartermaster | A mate at a crate near you gets a spare, every 10 s | D | 1 | **Buff:** the spare lands next to *you* (within 2 m), not next to the crate |

### Boosts

| Passive | Today | Tier | Level | Change needed |
|---|---|---|---|---|
| Second Wind | Last 30 s: +1 all skills, +3 Running/Dashing | D | 1 | **Buff:** last 45 s |
| Adrenaline | +50% run for 5 s after a knock | D | 1 | **Buff:** proposal (more triggers) |
| Zen | Stand still 1.5 s: next chop or wash finishes at once | B (S solo) | 2 | Solo fix (proposal) |
| Coffee Run | +25% run for 6 s after each hand-in | C | 1 | — |
| Glass Cannon | +1 all skills; a knock cracks it to −1 for 15 s | **S** | 3 | **Nerf:** team expiries and wrong dishes also crack it; crack lasts 20 s |
| Slow Starter | −1 all skills for 60 s, then +2 | **S** | **2** | **Nerf:** −1 for 45 s, then +1. Count level time, not real time. |
| Under Pressure | +5% run per waiting order, up to +25% | A | **2** | **Nerf:** +7% per order beyond the 2nd, up to +21% |
| Lucky Charm | 25%: extra chop stroke or double wash | B | 2 | — |
| Hot Streak | Team streak: up to +15% run/dash; +1 skills at 5 | A | 3 | **Nerf:** the +1 skills needs a streak of 8 |

### Hazards & vehicles

| Passive | Today | Tier | Level | Change needed |
|---|---|---|---|---|
| Portal Pro → **Transit Pro** | +40% run for 2 s after a portal | F | 1 | **Buff:** merge Cannoneer and Driver into it (proposal). 4 s, also after a cannon or a steered platform. |
| Cannoneer | Cannon fires without the fuse | F | — | **Hidden: merged into Transit Pro** |
| Driver | Platforms you steer ×1.5 | F | — | **Hidden: merged into Transit Pro** |
| Weatherproof | Wind only pushes along your facing | F | — | **Hidden:** walkways go to Sure-Footed |
| Lifeguard | Catch teammates who fall within 4.5 m | D | **2** | **Buff:** 7 m (no cooldown, so it beats Safety Net on its levels) |

### Versus

| Passive | Today | Tier | Level | Change needed |
|---|---|---|---|---|
| Shove | Dash knock ×1.75, victim drops their item | A | 2 | **Nerf:** ×1.4, 3 s per-victim lockout, never your teammates |
| Pickpocket | Dash into a chef to take their item | B | 2 | Never your teammates |

### Summary

| Level | Passives |
|---|---|
| **3** (11) | Sprinter, Homing Throw, Multitask, Echo Knife, Dishwasher, Sizzle Master, Maitre D', Bounty Hunter, Battery, Glass Cannon, Hot Streak |
| **2** (30) | Charge Dash, Momentum, Sure-Footed, Tiny Chef, Safety Net, Hover, Waiter, Long Arms, Lucky Knife, Prep Cook, Dash-Chop, Cold Hands, Steady Mixer, Fireproof, Hose Hands, Charmer, Clutch, Gambler, Second Chance, Close Enough, Inspiring, Bodyguard, Buddy System, Zen, Slow Starter, Under Pressure, Lucky Charm, Lifeguard, Shove, Pickpocket |
| **1** (29) | Double Dash, Slipstream, Immovable, Ghost, Quick Respawn, Conveyor Rider, Big Chef, Bulk Buyer, Catcher's Mitt, Sticky Fingers, Strong Arm, Lob, Assembly Line, Juggler, Hand-Off, Butler, Hot Hands, Fire Marshal, Slow Spread, Combo Keeper, Fresh Delivery, Fortune Teller, Golden Hour, Medic, Quartermaster, Second Wind, Adrenaline, Coffee Run, Transit Pro |
| Hidden (5) | Magnet, Rebound (→ Catcher's Mitt), Cannoneer, Driver (→ Transit Pro), Weatherproof (→ Sure-Footed) |

Every role has a choice at each shape:

| Role | 1 × L3 | L2 + L1 | 3 × L1 |
|---|---|---|---|
| Chopper | Echo Knife or Multitask | Lucky Knife / Lucky Charm / Prep Cook / Dash-Chop + a non-chop level-1 | (thin: see section 8) |
| Cook | Sizzle Master | Cold Hands + Hot Hands / Fire Marshal | Hot Hands + Fire Marshal + Slow Spread |
| Washer | Dishwasher | Zen + Butler | Butler + two non-wash level-1s (thin: see section 8) |
| Server | Hot Streak | Charmer / Clutch / Gambler + Fresh or Golden Hour | Fresh + Golden Hour + Coffee Run |
| Runner | Sprinter | Momentum / Tiny + Coffee Run or Double Dash | Coffee Run + Double Dash + Big Chef |
| Thrower | Homing Throw | Long Arms + Assembly Line | Strong Arm + Assembly Line + Catcher's Mitt |
| Support | Maitre D', Bounty, Battery | Inspiring / Second Chance / Close Enough + Combo Keeper | Medic + Quartermaster + Combo Keeper |

---

## 3. Changes needed so the levels hold up

1. **Nerf the four S-tier passives** so they fit level 3 (or 2):
   - Bounty Hunter: ×1.5 with a per-team pause.
   - Battery: one charge per 10 s.
   - Glass Cannon: more things crack it.
   - Slow Starter: +1 instead of +2, dropping it to level 2.

   With two actives, Battery's nerf is required, not optional.
2. **Tone down six passives that are A-tier or worse even at their level:**
   - Echo Knife: 60%, 6 m.
   - Clutch: ×1.5, last 20%.
   - Under Pressure: beyond the 2nd order.
   - Hot Streak: +1 skills at a streak of 8.
   - Shove: ×1.4, 3 s lockout.
   - Sprinter and Homing Throw: scale with the skill they make useless (Dashing and Throwing).
3. **Buff 22 of the 29 level-1 passives** to a C tier (most are BALANCE_PROPOSAL buffs). Without these buffs, the level-1 bucket is full of dead picks. Everyone then takes one level-3 or a level-2 + the one good level-1, and players are back to "everyone picks the strong ones".
4. **Merge 5 hazard and throw passives** (table above), so each mechanic has exactly one owner.
5. **Solo fixes:**
   - Multitask: jobs per player.
   - Zen: proposal solo fix.
   - Inspiring: skip same-player chefs.
   - Cooldowns shared per player per active (proposal S7a).
6. **Keep the system caps from BALANCE_PROPOSAL.** The budget only stops combos on one chef. The caps still matter for combos *across* chefs and *through* actives:
   - **S3:** skill bonus clamp of +2 (+3 for Running/Dashing).
   - **S4:** run speed ×1.6 on the *final* multiplier.
   - **S5:** tips and score *added together*, with tips capped at ×2.5 and score at ×2.0.
   - **S6:** cook ×4, mixers included.
   - **S7c:** team-wide picks counted per team in versus.

**BALANCE_PROPOSAL nerfs you no longer need**, because the level already pays for them:
- Sizzle Master stays ×2. The proposal had ×1.6.
- Charmer stays ×1.5. The proposal had ×1.35.
- Gambler keeps its 0-3 roll.
- Dishwasher stays every 2nd dish. The proposal had every 3rd.
- Tiny Chef stays ×1.15. The proposal had ×1.10.
- Maitre D' stays ×1.25. The proposal had ×1.15.
- Multitask keeps rate 0.4; it only gets the job cap.

---

## 4. What the budget fixes on its own

These combos from BALANCE_PROPOSAL's combo table **can no longer be on one chef**, because their levels add up to more than 3:

| Combo | Levels |
|---|---|
| Sizzle Master + Cold Hands / + Hot Hands | 3+2 / 3+1 |
| Echo Knife + Multitask / + Lucky Knife | 3+3 / 3+2 |
| Charmer + Gambler (+ Golden Hour) | 2+2 |
| Glass Cannon + Slow Starter | 3+2 |
| Glass Cannon + Immovable / Big Chef (uncrackable) | 3+1 |
| Sprinter + Tiny Chef, Sprinter + Momentum | 3+2 |
| Under Pressure + Hot Streak | 2+3 |
| Sprinter + Hover / Dash-Chop (dead pairs) | 3+2 |
| Bounty Hunter + Clutch | 3+2 |
| Multitask + Zen | 3+2 |
| Homing Throw + Long Arms | 3+2 |

**What the budget cannot stop**, and the fix that covers each:

| Combo | Why the budget misses it | Fix |
|---|---|---|
| Bounty on chef A × Clutch on chef B | Two chefs | S5: score bonuses add up, capped at ×2.0 |
| Close Enough + Clutch/Bounty | Two chefs; the wrong dish counts as the oldest order, which is usually in Clutch's window | Close Enough hand-ins get no Clutch or Bounty bonus |
| Fresh Delivery + Golden Hour (+ Charmer) | Only 2-3 levels, but tip factors multiply (×1.5 × ×2 = ×3) | S5: tips add up, capped at ×2.5 |
| Overdrive + Rally Cry + speed passives | Actives aren't in the budget | S4 cap on the final run multiplier |
| Frenzy + a mate's Inspiring + Glass Cannon | Actives and teammates | S3 bonus clamp |
| Overclock + Sizzle Master | Active + passive | S6 cook cap ×4 |
| Battery + two actives | Actives | Battery's one charge per 10 s |
| Glass Cannon + a mate's Bodyguard (never cracks) | Two chefs | Team mistakes crack it too |
| Versus: one team takes all team-wide passives | One-per-kitchen counts both teams (SkillSelectScreen.cs:385-398) | S7c: count per team in versus |

---

## 5. The strongest builds after the changes (what to watch)

| Mode / role | Build | Why it's OK now | Watch |
|---|---|---|---|
| Solo | Pocket + Power Chop · Multitask (3) · Tunnel Vision | Multitask counts jobs per player. Solo cooldowns are shared per player. | Multitask on most solo builds |
| 2p chopper | Power Chop + Frenzy · Echo Knife (3) | At 60% / 6 m, Echo is about ×1.6, against Lucky Knife (2) + a level-1 | Echo pick rate over 50% of choppers |
| 2p server | Vault + Pocket · Clutch (2) + Fresh Delivery (1) | Clutch ×1.5. Tips add up. | — |
| 2p cook | Overclock + Rescue · Sizzle Master (3) | Only with S6 | Burn rate with Overclock |
| 4p support | Time Out + Blink · Battery (3) | One charge per 10 s; Time Out back every ~38 s | Time Out uptime ≤ 30% |
| Runner | Overdrive + Vault · Momentum (2) + Coffee Run (1) | S4 holds the total at ×1.6 | — |
| Generalist | Frenzy + any · Glass Cannon (3) | Cracks on team mistakes too | Glass Cannon uptime > 85% |
| Versus | Sneeze Bomb + Hex · Shove (2) + Sticky Fingers (1) | Shove ×1.4. Sticky's guard recharges every 8 s. | Shove pick rate |

## 6. Two actives

### 6.1 Cooldowns: one per slot, with a ×1.25 tax

A single shared cooldown doesn't work, either way you build it:

- **Newest use wins:** Pocket, Pack Mule and Recall's flag plant set a 0.5 s cooldown (Teleport.cs:1426-1427). So Time Out → Pocket press → Time Out is ready again 0.5 s later, and the freeze stays up about 95% of the time.
- **Longest wins:** any 50 s use locks Pocket for 50 s, and today's Spare Pan (9999 s) locks the other active for the whole level.

**Recommended model:**

1. **Each slot gets its own** `CooldownUntil`, `CooldownTotal` and `ActiveUntil` (arrays of 2 on `ChefState`). The network message already carries the special id (`I`), so each machine finds the slot by looking the id up in the loadout. No new message fields are needed.
2. **×1.25 cooldown tax when both slots are filled.** The host applies it in `ServerActivate`, where the catalog cooldown is read (SpecialRuntime.cs:877). Make it host config: `[Specials] SecondActiveCooldown = 1.25`. A once-per-level partner (today's Spare Pan) is not taxed.
3. **One shared 0.5 s lockout** after any activation, so a SKILL, SWITCH, SKILL sequence can't fire both actives within a few frames (clients already have `PendingUntil`; give the host's own chefs the same).
4. **Solo:** BALANCE_PROPOSAL S7 ("the chefs of one player share one active cooldown") becomes "**share each slot's** cooldown". Otherwise one player gets 4 independent cooldowns.

With the tax, each chef uses actives 2 / 1.25 = **1.6×** as often as today, not 2×. Time Out's team uptime drops from 20% to 16%, and Rally Cry's from 27% to 21%.

**Taxed cooldowns** (from BALANCE_PROPOSAL values):

| Active | Alone | Paired |
|---|---|---|
| Vault | 9 s | 11.25 s |
| Blink | 10 s | 12.5 s |
| Grapple | 9 s | 11.25 s |
| Recall | 15 s | 18.75 s |
| Swap / Summon | 14 s | 17.5 s |
| Telekinesis | 8 s | 10 s |
| Power Chop | 10 s | 12.5 s |
| Wash Burst | 40 s | 50 s |
| Plate Conjurer | 30 s | 37.5 s |
| Spare Pan (reworked) | 25 s | 31.25 s |
| Rescue | 30 s | 37.5 s |
| Overclock | 30 s | 37.5 s |
| Fire Blast | 20 s | 25 s |
| Reroll | 40 s | 50 s |
| Time Out | 50 s | 62.5 s |
| Rally Cry | 30 s | 37.5 s |
| Frenzy | 30 s | 37.5 s |
| Overdrive | 30 s | 37.5 s |
| Grease / Sneeze / Hex | 20 s | 25 s |
| Decoy Order | 45 s | 56.25 s |
| Pocket / Pack Mule | 0.5 s | 0.5 s (its cost is the tax on the partner) |

Optional: if you want a single-active build to stay a real choice, give it ×0.9 on its cooldown (not for one-per-kitchen actives). Balance doesn't need it.

### 6.2 Pair rules

| Rule | Members | Why |
|---|---|---|
| **Carry group** (max 1) | Pocket, Pack Mule | Together they hold 5 items (4 after the proposal). The code can't run both: `ExecuteSwap` picks the stash type from `state.Active` (Carrying.cs:379), and the status line shows one stash. |
| **Jump group** (max 1) | Vault, Blink, Grapple | Chaining two jumps covers 13 m and two walls every cycle, and split kitchens stop mattering. Recall + one jump is allowed: it's a round trip, limited by the flag. |
| **Teammate-move group** (max 1) | Swap, Summon | Same targets and timings. In solo you'd control where both chefs stand at all times. |
| **Stun group** (max 1, versus) | Sneeze Bomb, Grease Trail | Freeze chains: about 3+ s of lock every 20-25 s, and Sneeze hits teammates too. |
| **One one-per-kitchen active per chef** | Time Out, Rally Cry, Reroll (proposal) | Otherwise one chef locks 2 of 3 kitchen-wide actives, gets a one-button panic burst (frozen timers + team speed), and collects all of Battery's refunds. This needs a new check in `Fits`. |
| **Clumsy clashes with Pack Mule** | — | Clumsy + Pack Mule was allowed because "it costs the active slot". With two slots it no longer does. |
| Existing quirk clashes (Heavy Boots / Overdrive, Germaphobe / Wash Burst, Pyrophobe / Fire Blast) | — | Already checked against every slot by `Fits` (Catalog.cs:548-564), as long as the loop covers the new slot. |

Groups are plain ints on `SpecialInfo` and `Fits` doesn't care about kind (Catalog.cs:561), so adding `Carry = 4, Jump = 5, MateMove = 6, Stun = 7` is a one-line change per special.

### 6.3 Pairs that break (and their fixes)

| Pair | What goes wrong | Fix |
|---|---|---|
| Any active + Pocket / Pack Mule / Recall on one cooldown field | 0.5 s write resets a 50 s cooldown | Per-slot timers (6.1). `ShortenCooldownLocal` and `RecallActivated` take the slot. |
| Time Out + any active with a duration (Frenzy, Decoy, Overclock, Rally, Overdrive) | Time Out reads the single `ActiveUntil` (Orders.cs:1258-1265). Frenzy used 9.9 s into Time Out stretches the freeze to 25.9 s. Vault (duration 0) ends it at once. Time Out in slot 2 freezes nothing (`chef.Active == TimeOut` checks slot 0 only). | Keep a **per-team "time out until"** value set in Time Out's own activation, so no other active can touch it. |
| Grease Trail + Frenzy / Decoy / Overclock | The trail runs while the shared `ActiveUntil` is in the future (Cooking.cs:802, 848), so 3 s becomes 16 s. | Grease keeps its own end time. |
| Pocket in slot 2 | It behaves as Pack Mule (Carrying.cs:379 reads slot 0). | Pass the fired special to the handler (`state.Using`). Do the same for every handler that reads `state.Active`. |
| Battery holder with two actives | Both trigger refunds, so Time Out comes back twice as fast (25 s cycle, 40% uptime). | One charge per 10 s, with the trigger-capped refund (6.4). |
| Vault + Blink / Blink + Grapple / Vault + Grapple | 13 m and two walls in about 1 s | Jump group |
| Time Out + Rally Cry (one chef) | One-button panic burst, all refunds to one chef | One one-per-kitchen active per chef |
| Sneeze + Grease | Freeze chain | Stun group |
| Pocket or Pack Mule + any real active | Not broken, but it becomes *the* default second pick (a 0.5 s toggle has no cooldown to compete with) | Carry group + tax on the partner. **If playtests show a carry active on more than 50% of chefs, make a carry active in the second slot use 1 of the 3 passive levels.** |
| Overclock + Frenzy, Frenzy + Power Chop | Checked: fine *once S3 and S6 ship*. Overclock + Frenzy is +18% for 10 s under the caps. | Make S3 and S6 prerequisites. |

### 6.4 Passives and quirks that change with two actives

| Pick | Issue | Fix |
|---|---|---|
| **Battery** | It triggers on every holder activation (TeamBuffs.cs:369-405). It picks a target with `mate.Active` and the single cooldown (:360-361, :396). It shortens one cooldown (:412-413, :429). | **One spec:** one charge per holder per 10 s, ignoring actives with a catalog cooldown under 5 s. **Refund = min(time left, 0.5 × the target slot's cooldown, 0.65 × min(the trigger's taxed cooldown, 10 s)).** The target slot is the one with the longest wait. The message carries `I = target special`. Time Out then comes back every ~38 s (26% uptime), under the proposal's ~30% one-active target. These are the figures for a level-3 Battery. |
| Frenzy chip | `chef.Active != Frenzy` (TeamBuffs.cs:815) hides the chip when Frenzy is in slot 2 | Use `HasActive(Frenzy)` |
| **Tunnel Vision** | Wash Burst refuses chop-locked chefs (Stations.cs:1380), but Power Chop (Stations.cs:1346-1352) never checks the wash lock. One chef can hold both and get back the job the quirk took away. | Power Chop refuses for wash-locked chefs. With the proposal's station lock, both actives check the locked station. |
| **Heavy Boots** | A jump active now sits beside another active, so station chefs take +2 and lose little. | Watch it. If more than about 30% of station chefs take it, make the refund +1 when a Jump-group active is held. |
| Sleepy | Frozen already blocks actives (SpecialRuntime.cs:763-767). Slightly cheaper in practice. | No change |
| Buddy System | Shares speed buffs, so Overdrive + Rally Cry both get passed on | Covered by S4 on the final value |
| Medic (proposal) | "Empty-handed" includes the Pocket / Pack Mule stash | Already correct. The Carry group means only one stash to check. |

### 6.5 Tier shift for actives

Once actives come in pairs, an active's value is **what it adds on top of the other pick**:

- **Carry (Pocket / Pack Mule)** becomes nearly universal as the second pick: about 60-75% of chefs without rules, and A+ with the Carry group and tax. Watch this first.
- **Weak actives become real second picks:**
  - Rescue C → B, as Overclock's partner.
  - Fire Blast C → B, on fire levels.
  - Swap / Summon C → B.
  - Frenzy B → B+, as a burst partner.
  - Telekinesis B → B+, with Pocket.
  - Spare Pan F → C- as-is, B- with the rework.
- **Still bad:** today's Reroll. It rerolls the *newest* order (Orders.cs:1166-1168), which saves nothing even as a free pick. It still needs the rework.
- **Mobility:** with one jump per chef, Vault stays top. Recall rises to A- as a jump's partner.
- **Versus:** Hex and Decoy gain the most. A versus chef can now carry one sabotage tool and one co-op tool.

Net effect: the "C = idle on most levels" problem mostly goes away, because the second slot is where level-specific picks go. The new risk is **one default first pick** (carry), which is what the Carry group and the tax are for.

### 6.6 Input: one SKILL key plus a SWITCH key

**How it works.**
- **SKILL** uses the **selected** active.
- **SWITCH** changes the selection between active 1 and active 2.
- The HUD shows the selected active as the big button and the other one smaller and dimmed beside it. Each keeps its own cooldown sweep, so a player can see which one is ready before switching.
- With only one active picked, SWITCH does nothing.

**Default buttons**

| Device | SKILL | SWITCH |
|---|---|---|
| Whole controller | **RB** (today: EMOTE) | **RT** |
| Shared controller, right half | RB | RT |
| Shared controller, left half | **LB** | **LT** |
| Keyboard | stays EMOTE | one new key, chosen after checking the game's keymap (for example Q) |
| Shared keyboard, right-hand player | stays EMOTE | one new key on the right side (for example RightShift) |

Notes on the defaults:
- **The left-half player needs mirrored defaults.** On a controller shared by two players, RB and RT are on the right half only (SpecialInput.cs:67-75), so the left-half player would have no SKILL and no SWITCH button. LB and LT are the mirror image.
- **Everything stays rebindable.** SKILL keeps today's rows (the SKILL KEY row on the card, and MOD Settings). SWITCH gets its own **SWITCH KEY** row, set by pressing, with the same "(ALSO A GAME BUTTON)" warning.
- **EMOTE becomes an explicit choice.** Today a value of `None` means "use EMOTE" (SpecialInput.cs:85). Add an explicit `Emote` entry to `PadKey` so a player can still choose EMOTE once RB is the default.
- **Check RB and RT against the game's own controls before shipping.** In particular, check the solo chef-switch button. If the game already uses RB or RT for that, the defaults clash in solo. In that case, take the next free button the way LOCK IN does (SpecialInput.cs:327-385).

**Existing players' config files.** BepInEx keeps the saved value, and every current config says `SkillButton = None`. A new default in `Bind` therefore only reaches new installs. To move existing players to RB:
- Add a one-time migration with a marker, e.g. `[Controls] InputLayout = 2`: on the first start of 5.0.0, `None` becomes `RB`.
- Side effect: a player who chose EMOTE on purpose is moved too, because `None` can't be told apart from the default.
- Say this in the release notes ("the skill button is now RB; set it back to EMOTE on the SKILL KEY row").

**Behaviour details**

| Situation | Behaviour |
|---|---|
| Which active is selected at the start of a level | Active 1 |
| Selection after using an active | Stays where it is. Optional setting `[Controls] SwitchAfterUse` (off by default) flips it automatically. |
| SWITCH while that active is on cooldown | Allowed. Switching is free and instant, with no cooldown of its own. |
| SWITCH while holding SKILL to aim | Cancels the aim and switches. Nothing was used, so no cooldown starts. |
| Solo (two chefs) | Each chef keeps its own selection, which survives swapping between chefs |
| Network | No change. The selection only exists on the player's own machine. The "use" message already names the special (`I`), so the host and other players don't need to know the selection. |
| Lock-in warning | If two actives are picked and SWITCH has no button, warn at LOCK IN |

**Why this layout** (instead of a second fire key):
- **SKILL still fires.** Muscle memory carries over: you press SKILL to use your active, as today.
- **Fewer buttons.** It needs one extra button rather than a second fire key. That fits half a controller, and the right-hand keyboard player, who today has no binding at all.
- **The cost is one press.** Firing both actives back to back now takes a SWITCH press in between, which trims the two-active burst a little. That fits the ×1.25 tax in 6.1 and needs no number change.

**Still rejected:**
- **Tap / hold:** hold already means "aim".
- **Double-tap:** every Pocket press would wait about 0.25 s.
- **DASH + SKILL chord:** it spends the dash.

**Aiming:** the aim session stores which slot it was opened for (SpecialAim.cs:176, 185), so it fires that slot even if the HUD selection changes mid-hold. Keep one "was down" flag for SKILL and one edge flag for SWITCH.

---

## 7. Code changes

### 7.1 Data

- **The passive's level is catalog data.** Add `int Level` to `SpecialInfo` (Catalog.cs:62) and a `level:` argument to `P()`, e.g. `P(Special.EchoKnife, …, level: 3)`. Actives and quirks have no level.
- **Loadout = 6 picks, with the new slots appended:**

  ```
  index:  0    1    2    3    4    5
          A1   P1   P2   Q    A2   P3
  lobby:  "a1.p1.p2.q.a2.p3"     e.g. "1.40.65.101.39.88"
  ```

  New slots go at the end, so index 0 (active) and index 3 (quirk) keep their meaning. That matters because the quirk refund at Catalog.cs:536 feeds the skill points. An old 4-field string still parses: Active 2 and Passive 3 are empty.
- **`SlotCount` 4 → 6** (Catalog.cs:93). Today, `SpecialSlot`, `KindFor`, `SlotNames` and `SlotHelp` loop over it, and so do 4-slot hard-coded sums (SpecialHud.cs:233, SpecialRuntime.cs:402, SpecialDetails.cs:152).
- **Validation in `Fits` / `Enforce`** (Catalog.cs:548-653):
  - The levels of the passives add up to ≤ 3.
  - No special twice.
  - The new pair groups for actives (section 6.2).
  - At most one one-per-kitchen active.

  `Enforce` drops later passives first when the budget is exceeded, as it already does for clashes. Every machine runs it on every loadout, so they all agree.
- **Hide the merged passives:** add a `Hidden` flag (BALANCE_PROPOSAL S9) so they aren't offered. Keep their enum numbers: the network sends them, so the numbers must never change.
- **Version:** bump `SkillSync.Version` (SkillSync.cs:81) and `SpecialNet.Protocol` (SpecialNet.cs:73). Plugin 5.0.0.
- **Keep levels hard-coded, not in config.** If they were config values, they would have to travel in the host's rules string, or two machines would enforce different budgets.

Unlike the variable-level idea in the first version of this doc, you don't need:
- per-player levels in the loadout,
- a level API in the modules,
- per-level number tables for 75 passives,
- level-specific texts.

### 7.2 Where the work is

| Area | Files | Size | What |
|---|---|---|---|
| Data layer | Catalog.cs | S-M | `Level`, `SlotCount` 6, `Fits` budget + groups, `Enforce`, `Format` / `Parse`, `Hidden` |
| Runtime for 2 actives | SpecialRuntime.cs | M | Timers per slot, `ReadActivation` fires the selected slot, SWITCH edge, `SelectedActive` on `ChefState`, `Fire`, `OnActivateRequest` (today it answers "NOT SYNCED" for anything but slot 0, :826), `ServerActivate` + ×1.25 tax, `OnActivated`, `ShortenCooldownLocal`, `state.Using` |
| Module fixes for 2 actives | Orders.cs:1263 (Time Out), Cooking.cs:802/848 (Grease Trail), Teleport.cs:1420-1428 (Recall), TeamBuffs.cs:358-429 (Battery), TeamBuffs.cs:815 (Frenzy chip), Carrying.cs:379 (Pocket / Pack Mule) | S each | These are bugs the moment a chef has 2 actives |
| Aim + input | SpecialAim.cs, SpecialInput.cs | M | Aim session stores its slot. New `SwitchKey`, `SwitchButton`, `SwitchKeyRight`; `SkillButton` default RB (LB on a left half), `SwitchButton` default RT (LT); explicit `Emote` choice; one-time migration of `None` → RB; capture target enum; LOCK IN must avoid both. |
| Skill screen | SkillSelectView.cs, SkillSelectScreen.cs, SkillPicker.cs | M | Rows: ACTIVE 1, ACTIVE 2, 3 passives, QUIRK, + SWITCH KEY. Level pips (●●○) on every passive tile and row. A "PASSIVES 2/3" line by the points. Tiles that don't fit the budget greyed with "NEEDS 3 / 1 LEFT". |
| In-level HUD | SpecialHud.cs, SpecialDetails.cs | M-L | Two active buttons: the selected one big, the other small and dimmed, each with its own cooldown sweep, ring and deny shake. The key label shows SKILL on the selected one and SWITCH between them. Three passive icons + quirk. Check the width with 4 on-screen players. |
| Balance changes | Modules/*.cs | M | The nerfs, buffs and merges in section 2. Most are BALANCE_PROPOSAL items. |
| Docs | README.md, SPECIAL_SKILLS.md | S | Levels in the passive list, two actives, SWITCH key, new RB/RT defaults |

### 7.3 Order of work (≈ 2-2.5 weeks)

| # | Step | Time |
|---|---|---|
| 0 | BALANCE_PROPOSAL system caps S3-S7 and S10. S7 and S10 touch the same code. | (that patch) |
| 1 | Data layer: levels in the catalog, 6 slots, budget validation, version bump | 1-2 days |
| 2 | Runtime for 2 actives: per-slot timers, SWITCH and selection, the six module fixes, pair groups, tax, Battery spec | 3-4 days |
| 3 | UI: card rows, level pips, budget line, SWITCH KEY row, picker greying, HUD with two active buttons | 3-4 days |
| 4 | The balance changes in section 2 (S-tier nerfs and solo fixes first, then the level-1 buffs) | 2-3 days |
| 5 | Docs + release | 1 day |

You can ship passive levels without the second active: steps 1, 3 and 4 work on their own. That is a good first release, because the level budget alone fixes most of the "everyone picks the strong ones" problem.

---

## 8. Risks and playtest checklist

**Risks:**
- **The level-1 bucket.** If the level-1 buffs don't land, "one level-3 each" becomes the only build, and the strong-pick problem is back in a new form.
- **Choppers and washers have thin level-1 options** (see the table in section 2). Watch whether they are forced into Echo/Multitask/Dishwasher. If so, add a light chop or wash level-1. Candidates: Lucky Charm at 12% as a level-1, or Prep Cook every 30 s.
- **A mixed lobby** (an old build) turns specials off for everyone. That is intended, but say so in the release notes.
- **New default buttons.** Moving SKILL from EMOTE to RB changes muscle memory for existing players (section 6.6 migration). RB and RT must be checked against the game's own buttons, especially solo chef-switching.

**Playtest checklist (metric → action):**

| Check | Target | If it misses |
|---|---|---|
| Shape split (1 × L3 / L2 + L1 / 3 × L1) | Each 20-45% of chefs | Move the extremes: level-3s that get no picks become level 2, level-1s nobody takes get buffed |
| Any single passive | ≤ 35% of chefs in its role | Raise its level, or nerf it |
| Any passive | > 3% of chefs (nothing dead) | Lower its level, or buff it |
| Echo Knife among choppers | ≤ 50% | Echo 50% |
| Pocket / Pack Mule as an active | < 50% of chefs | A carry active in the second slot uses 1 passive level |
| Time Out team uptime (with Battery) | ≤ 30% | Battery trigger cap 0.65 → 0.55 |
| Glass Cannon uptime | ≤ 85% | Crack 20 → 25 s |
| Heavy Boots on station chefs | < 30% | Refund +1 when a jump active is held |
| Multitask in solo | Not on most builds | Solo rate 0.4 → 0.35 |
