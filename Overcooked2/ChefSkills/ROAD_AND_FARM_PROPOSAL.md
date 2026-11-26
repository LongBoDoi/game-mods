# Chef's Road and the Pantry Farm: Design

A long-term game mode for ChefSkills, made for two expert players. It has two parts:

- **Chef's Road**, a story campaign. You travel a branching map of the game's kitchens, chapter by chapter, with twists on every level, a boss at the end of each chapter, and Reputation as your health. Winning unlocks ChefSkills skills one at a time.
- **The Pantry Farm**, an economy between levels. Tips become coins. Coins buy permanent kitchen upgrades and seeds. Crops grow as you play and turn into premium orders and other perks.

Both stand on one base system, **Heat**, which makes every level harder than vanilla and keeps rising as you get stronger.

This is a design to implement later. Hooks are marked **proven** when ChefSkills already patches them (with file:line) and **to test** when nothing in the repo uses them yet. Section 9 lists the short spikes to run before building.

---

## 0. Short answer

| Question | Answer |
|---|---|
| What is it? | A campaign map of about 40 levels in 6 chapters, plus a farm and upgrade economy. All progress is saved between sessions. |
| Why won't it be easy? | Every level runs at a Heat tier that changes the numbers the game itself uses: order rate, order lifetime, plate return, burn time. Heat rises each chapter, and again in New Game+, faster than upgrades can keep up. |
| What do you work toward? | Skill unlocks (you start with 10 of ChefSkills' skills), relics for the current chapter, coins, kitchen upgrades, farm plots and crops, boss clears, and Road+ tiers after the story ends. |
| New 3D scenes? | **None.** Every level is an existing game kitchen. The map, shop, farm and story are overlay screens built like SkillSelectView. |
| Where does it live? | Inside ChefSkills as a mode (namespace `ChefSkills.Modes.Road` and `ChefSkills.Modes.Farm`). It reuses SpecialNet, the skill catalog, the select screen and the version gate. |
| Online or couch? | Designed **host-authoritative**, so it works for both. On one PC the sync layer simply does nothing. See open question Q1. |
| Biggest risks | Sending the game to a chosen kitchen from the map (spike S1) and finite crates (spike S2). Both have fallbacks. |

---

## 1. Goals and rules

**Goals**

1. Make the game hard again for two players who have mastered every level.
2. Give something to work toward over weeks: a map to finish, things to unlock and build, and crops to grow.
3. Each evening should be fun alone: two or three levels, a boss try, a visit to the shop.

**Design rules that every feature must follow**

- **Difficulty grows faster than power.** All upgrades together are capped (section 6.4). Heat has no such cap. An upgrade should make a hard level possible, never make a vanilla-hard level easy.
- **Rewards add options more than numbers.** New skills, new relics and new crops change how you play. Flat stat boosts are small and capped.
- **Built for 2.** Twists and bosses are tuned for exactly two chefs. Other player counts are not supported in this mode.
- **Losing costs progress, not everything.** A failed level costs Reputation. A lost chapter sends you back to its start. Unlocks, coins, upgrades and the farm are never lost.
- **No new scenes, no new 3D art.** Only the game's kitchens, plus overlay UI and the order-card art that ChefSkills already draws.

**Non-goals:** versus play, 3–4 players, public lobbies, leaderboards.

---

## 2. An evening of play

1. Open the game, choose **Chef's Road** and load the save. The map shows where you stopped.
2. Pick the next node. A Kitchen node shows its level, Heat and twists before you commit.
3. Pick skills from your unlocked pool on the usual select screen, then play.
4. On the results screen you see the target, your score, Reputation change, coins, and anything unlocked.
5. Every few nodes there is a **Market**. Spend coins on upgrades, plant and harvest crops, and choose which premium orders to take into the next levels.
6. At the end of the chapter you fight the **boss**. Losing costs Reputation. Running out of Reputation restarts the chapter.

---

## 3. Heat: the difficulty system

Heat is a number on each level, from 0 up to about 18 at the very top (section 3.3). It turns into multipliers on the game's own values. Heat 0 is vanilla.

### 3.1 The knobs

| Knob | Effect per Heat | Hook | Status |
|---|---|---|---|
| **Order rate** | time between new orders × 0.94 | `ServerOrderControllerBase.m_timerUntilOrder` | proven (Orders.cs:69) |
| **Order lifetime** | order lifetime × 0.95 | `ServerFixedTimeOrderController.GetNextOrderLifetime` postfix | proven (Orders.cs:1569) |
| **Open orders** | +1 open order at Heat 4, 8 and 12 | `ServerOrderControllerBase.AddNewOrder(RecipeList.Entry)` and the controller's order limit field | AddNewOrder proven (Orders.cs:71, :1611). The limit field is **to test**. |
| **Plate return** | plate return time × 1.05 | `PlateReturnController.FoodDelivered`, `m_plateReturnTime` | proven (Patches.cs:255) |
| **Burn time** | time from cooked to burnt × 0.95 | `ServerCookingHandler.SetCookingProgress` | proven (Orders.cs:1993, Cooking.cs:343) |
| **Expiry penalty** | an expired order costs an extra 2% of the target per Heat | `ServerKitchenFlowControllerBase.OnOrderExpired` prefix | proven (Orders.cs:1837) |

Multipliers stack per Heat (0.94^Heat and so on). At Heat 10 that is orders every 54% of the vanilla gap, lifetimes at 60%, plates at 163% of the time, burn at 60%, and two extra open orders.

Starting numbers only. Tune them in play (section 10).

### 3.2 The score target

Each Road level has one target. Hitting it counts as a win. Missing it counts as a loss.

```
target = topStarThreshold(level) × (1.00 + 0.04 × Heat) × twistFactor
```

- `topStarThreshold` is the level's highest star score. Its field in the level config still has to be found (**to test**, spike S5).
- `twistFactor` is 1.0 unless a twist says otherwise (for example, "Silent pass" removes tips, so it lowers the target).
- The target shows on the node card before you play and on a HUD strip during the level.

### 3.3 Heat over the campaign

| Where | Heat |
|---|---|
| Chapter 1 Kitchen | 2 |
| Each later chapter | +1 (Chapter 6 Kitchen = 7) |
| Elite node | chapter Heat + 2 |
| Boss | chapter Heat + 3, plus the boss rules |
| Road+ N (New Game+) | everything + N, up to Road+8 |

With the boss and Road+8, the top of the scale reaches Heat 15+3 = 18. Clamp each knob so it can't break the game (orders no faster than every 6 s, lifetimes no shorter than 25 s).

---

## 4. Chef's Road: the campaign

### 4.1 Structure

- **6 chapters**, one per world of the base game's story. If the DLC kitchens are available, they can be added later as bonus chapters (Q2).
- Each chapter is a **map of 8–10 nodes in 5 columns**. Each column has 2–3 nodes, and paths branch and join, so you choose a route.
- A chapter's maps are generated from a seed when the chapter starts, then saved. Kitchens come from that world's levels, with no repeat in the same chapter.

### 4.2 Node types

| Node | What it is | Reward |
|---|---|---|
| **Kitchen** | One level at chapter Heat with 1 twist | Coins, sometimes seeds |
| **Elite** | One level at Heat + 2 with 2 twists | Coins ×2, plus a choice of 1 relic out of 3 |
| **Market** | Opens the Pantry screen (section 5). No level. | — |
| **Event** | A story card with a choice. For example, "A rival cook bets you 50 coins you can't win the next level with Rush hour on." | Depends on the choice |
| **Rest** | Get back 1 Reputation, or plant a free seed | — |
| **Boss** | The chapter's last node (4.5) | 1 skill unlock (from a choice of 3), 1 rare seed, chapter cleared |

Each chapter's map has at least 1 Market, 1 Elite and 1 Rest before the boss.

### 4.3 Reputation (health)

- You start each chapter with **5 Reputation** (the farm can raise the cap to 7).
- A lost Kitchen or Elite costs 1. A lost boss costs 2.
- A win at 120% or more of the target gives back 1, up to the cap.
- **At 0 Reputation the chapter run ends.** You go back to the chapter's start with a new map. You keep skill unlocks, coins, upgrades and the farm, but lose this chapter's relics.
- You can retry a lost level for free by choosing it again (it costs the Reputation again if you lose again). You can also take a different path.

### 4.4 Twists

Each Kitchen gets one twist and each Elite gets two, chosen from the pool below. Twists unlock chapter by chapter, so the pool grows as you go.

| Twist | Effect | Hook | Status |
|---|---|---|---|
| Rush hour | Order rate × 0.7 for this level | `m_timerUntilOrder` | proven |
| Short fuse | Order lifetime × 0.75 | `GetNextOrderLifetime` | proven |
| Crowded board | +1 open order | AddNewOrder / order limit | to test (limit) |
| Slow dishes | Plate return × 1.5 | `m_plateReturnTime` | proven |
| Hot pans | Burn time × 0.6 | `SetCookingProgress` | proven |
| Silent pass | No tips, only base score. Target × 0.8. | `SendDeliverySuccess` | proven (Orders.cs:1814) |
| Harsh critic | An expired order costs double | `OnOrderExpired` | proven |
| No second plates | A wrong dish costs points | `OnFailedDelivery` / `SendDeliveryFailed` | proven (Orders.cs:1898, :1927) |
| Dull knives | Both chefs chop at × 0.75 | ChefSkills `Skill.Chopping` multiplier | proven (Patches.cs ChopPatch) |
| Tired legs | Both chefs move at × 0.9 | ChefSkills movement skill multiplier | proven |
| Split duties | Chef 1 can't use sinks; chef 2 can't use the pass | interact-event prefix (`ReceiveInteractEvent`) | proven hook, new rule |
| One-trick | Each chef can use only one of their actives | ChefSkills loadout | new, small |
| Blind tickets | An order's recipe stays hidden until its last 15 s | order card UI, same place the bounty frame is drawn (Orders.cs:746) | new, medium |
| Grease fire | A random stove catches fire every 40–60 s | `ServerFlammable.Ignite` | **to test** (S3). Today it's only blocked (Cooking.cs:1365), never called. |
| Short stock | Crates hold a limited number of items | `ServerPickupItemSpawner.HandlePickup` prefix | **to test** (S2) |

Write each twist as a small class with `Apply(level)` and `Remove()`, and with a single owner per knob. When two twists touch the same value, their multipliers multiply.

### 4.5 Bosses

Each boss is a fixed kitchen from its world, at boss Heat, with a fixed set of twists and **one rule of its own**. Boss rules are the main new code in the campaign.

| Chapter | Boss | Twists | Own rule |
|---|---|---|---|
| 1 | **The Rush** | Rush hour | Order rate speeds up every 60 s (× 0.9 each time). |
| 2 | **The Inferno** | Hot pans | Grease fire every 30 s. A fire left burning for 10 s costs 1 Reputation. (If S3 fails: Hot pans × 0.4 plus Slow dishes.) |
| 3 | **The Blind Critic** | Blind tickets, Harsh critic | Every third order is a "critic order": ×3 score if served, −1 Reputation if it expires. |
| 4 | **The Famine** | Short stock | Crates start with little stock. Serving a dish adds 2 stock to a random crate. (If S2 fails: each ingredient type is locked for 20 s at a time, in turn.) |
| 5 | **The Split** | Split duties, One-trick | Every 45 s the two chefs' duties swap. |
| 6 | **The Gauntlet** | A new random twist every 60 s, stacking up to 4 | You must reach 50% of the target by halfway, or lose at once. |

The boss intro shows its rule on a story card before the select screen.

### 4.6 What you unlock

**Skills.** In Road mode the select screen only offers skills you have unlocked.
- At the start you have **10 skills**: 6 passives (levels 1–2), 2 actives and 2 quirks. Pick a set that covers the basics.
- Every boss unlocks 1 skill, chosen from 3.
- Every Elite has a 30% chance to offer one as well, as an alternative to the relic.
- The Pantry sells "recipe books" that unlock a skill of your choice from a tier (section 5.3).
- With 6 bosses, Elites and books, you have about 30–40 of the 75 by the end of the story. Road+ is where you collect the rest.

**Relics** last for the current chapter. They are team-wide copies of existing ChefSkills passives, so they need no new effect code. Examples: "A spare apron" (Second Chance's effect), "The maître d's bell" (Maître d's longer lifetimes), "Lucky streak" (Combo Keeper). A team can hold up to 3. Picking a 4th replaces one.

**Road+.** After chapter 6, Road+1 starts the story again with Heat +1, a harder twist pool and higher rewards, up to Road+8. Each Road+ clear gives a cosmetic badge on the map and a skill unlock.

### 4.7 Story

Short text cards between nodes, in English and Vietnamese (use `Lang.T`, like the rest of ChefSkills). One opening card per chapter, one for each boss, and 2–3 Event cards per chapter.

Suggested frame, to rewrite as you like: a kitchen inspector has downgraded every restaurant in the kingdom. The two of you travel region by region, winning back each one. The final boss turns out to be the inspector's own test kitchen. Since this mod is only for you two, the story can use names, jokes and places that mean something to you.

---

## 5. The Pantry Farm: the economy

### 5.1 Currencies

| Currency | How you get it | What it's for |
|---|---|---|
| **Coins** | 50% of the tips earned in a Road level. Node rewards. Selling crops. | Upgrades, seeds, recipe books |
| **Seeds** | Node rewards, the Pantry shop | Planting |
| **Produce** | Harvesting crops | Premium orders, boosts, selling |

Coins are not lost when a chapter run ends.

### 5.2 The Pantry screen

An overlay screen that opens at Market nodes, and also from the map at any time for the farm only (planting and harvesting, no shopping). It has three tabs: **Shop**, **Farm** and **Kitchen** (upgrades). Use the same UI tech as SkillSelectView. Online, both players can browse. Only the host's confirm spends (Q1).

### 5.3 Shop

- Seeds (common ones always; rare ones rotate each chapter)
- Recipe books (one skill of your choice from a tier, priced by its passive level)
- One-level consumables: "Sharpened knives" (the next level ignores Dull knives), "Extra crew" (removes one twist from the next node)

### 5.4 The farm

- **Plots:** you start with 2 and can buy up to 9.
- **Growth is counted in services, not real time.** A service is one Road level played to the end, won or lost. You only farm by playing.
- **Quality:** a crop's quality (Normal, Fine, Gold) comes from how well you play while it grows. Each win while it's in the ground adds a point. Each win at 120% or more of the target adds two. Gold produce is worth much more.
- **Conditions:** some crops only grow in some conditions, which push you to play in harder ways.

| Crop | Services | Condition | Main use |
|---|---|---|---|
| Herbs | 2 | — | Boost: +5% tips for one level |
| Tomatoes | 3 | — | Premium tomato orders |
| Rice | 3 | — | Premium rice orders |
| Peppers | 4 | Only grows on levels at Heat ≥ 5 | Boost: +1 relic choice at the next Elite |
| Saffron | 6 | — | Premium orders ×3. Sells high. |
| Mushrooms | 5 | Only grows on levels you lose (a pity crop) | Boost: next level's Reputation loss is 0 |
| Truffle | 8 | Seed only from bosses | Recipe book of any tier, or large coin sale |
| Golden onion | 10 | One per chapter, from the story | Raises the Reputation cap by 1 (twice at most) |

### 5.5 Premium orders

Before a level, you can take up to 2 produce into it. Each one turns some of that level's orders into **premium orders**: gold order card, ×2 score (×3 for Gold produce). This uses the existing **bounty order** look and scoring (Orders.cs:746, a gold frame with an ×2 badge). Produce only works on a level with a matching recipe. The node card says which produce fits the level.

### 5.6 Kitchen upgrades (permanent)

| Upgrade | Per level | Max | Hook |
|---|---|---|---|
| Whetstone | chop +5% | 3 | `Skill.Chopping` multiplier |
| Dish rack | plate return −6% | 3 | `Skill.Serving` multiplier / `m_plateReturnTime` |
| Good boots | move +3% | 2 | ChefSkills movement multiplier |
| Cast-iron pans | burn time +6% | 3 | `Skill.SlowBurn` multiplier |
| Bigger pass | +1 premium order per level | 2 | Pantry rules |
| Seed shelf | +1 plot | 7 | Farm |
| Greenhouse | crops grow 1 extra service when you win at Heat ≥ 6 | 1 | Farm |
| Big loadout | passive budget +1 (on top of the base 3) | 2 | ChefSkills loadout (needs PASSIVE_LEVELS_PROPOSAL first) |
| Insurance | start each chapter with +1 Reputation | 1 | Road |

**Power cap:** with everything bought, the stat upgrades come to roughly 15–20% more output. Chapter 6's Heat of 7 alone cuts order lifetimes by 30% and the gap between orders by 35%, and Road+ keeps going (section 3.3). Prices grow by about 1.6× per level. A full kitchen should take about the whole story to buy.

### 5.7 Short stock (finite crates)

The deepest change to the game, so it's a **twist and a boss rule, not the default**.

- When Short stock is active, each crate holds a number of items set before the level. Picking up from an empty crate does nothing and shows "Out!" over the crate.
- The stock comes from your farm. Before the level you choose how many of each ingredient to bring (from produce and a free base amount). That's where planning comes in.
- **Hook:** a prefix on `ServerPickupItemSpawner.HandlePickup` that returns false when the crate is empty. ChefSkills already has postfixes there (Stations.cs:1747, Throwing.cs:1301). **To test (S2):** whether the client shows a pickup it didn't get, and how blocking the pickup behaves when a chef holds the button.
- **Fallback if S2 fails:** "Short stock" becomes "crate closed for 20 s after every N pickups", done on the host the same way. It's weaker but keeps the idea.

---

## 6. What is saved

### 6.1 Saved forever (profile)

- Unlocked skills, coins, upgrades, farm plots and crops (with quality and progress), produce and seeds
- Chapters cleared, the highest Road+ cleared, badges
- Stats (levels played and won, best scores per level and Heat) for a stats page later

### 6.2 Saved for the current chapter (run)

- The chapter's map (seed, node types, kitchens, twists) and which nodes are done
- Current node, Reputation, relics held
- Pending choices (a relic choice not yet made, for example), so quitting mid-choice is safe

### 6.3 Format and safety

- One file per profile: `BepInEx/config/chefskills.road.json`, written with Unity's `JsonUtility` (net35 has no other JSON support built in).
- Plain data classes with a `version` field. Add a migration step whenever a field changes.
- Save after every result screen and every Pantry confirm. Write to a temp file and rename it, and keep 3 rotating backups (`.bak1`–`.bak3`).
- Before a level starts, mark it as started. If the game crashes during it, treat it as **neither a win nor a loss**. This forgives crashes, which matter more than cheating for two players.

### 6.4 Online sync

- **The host's PC owns the save.** The client never writes it.
- After every change the host sends a snapshot of what the screens need (map, Reputation, relics, Pantry state, the client's chef's unlocks). It goes as lobby data (key `chefskills.road`, next to `chefskills.rules`, SkillSync.cs:75) when it fits, or else as a SpecialNet message. Check the Steam size limit in spike S4.
- **Choices:** on the map and in the Pantry both players can move the cursor and see each other's. A choice is made only when **both** confirm, like the skill select screen.
- **Two hosts:** if you sometimes swap who hosts, the save is on the wrong PC. The simplest answer is an "export / import save" button (copy the file). See Q1.
- On one PC (couch), the same code runs with the sync layer off.

---

## 7. Flow inside the game

```
Main menu ──► Road mode (option in ChefSkills settings / a menu button)
   │
   ▼
Map screen ──pick node──► Kitchen/Elite/Boss ──► Pantry pre-level (premium orders, stock)
   ▲                         │
   │                         ▼
   │                  load the node's kitchen (GetNextScene)
   │                         │
   │                         ▼
   │                  skill select (unlocked pool only) ──► level at Heat + twists
   │                         │
   │                         ▼
   └──── results overlay (target, Reputation, coins, unlocks, farm growth)
   ▲
   └─ Market / Event / Rest nodes open their screen without loading a level
```

| Step | Hook | Status |
|---|---|---|
| Show the map and Pantry after the results | `ScoreScreenOutroFlowroutine.Run` / `StarRatingUIController.SetButtonActive` | proven in RandomLevel (branch `origin/chefskills-4.3.0-balance`, commit fabf307, `RandomLevel/Patches.cs`). Needs porting. |
| Load the chosen kitchen | `ServerCampaignFlowController.GetNextScene` postfix, returning the node's scene name | proven in RandomLevel for DIY levels (`NextRandomScene.cs`). For story kitchens it's **to test** (S1). |
| Skill select from the unlocked pool | `LevelIntroFlowroutine.Run` wrapper | proven (Patches.cs:85) |
| Restart a level | `InGamePauseMenu.OnRestartConfirmed` | proven in RandomLevel |
| List the kitchens of each world | the game's scene directory data | **to test** (S1) |

**How a Road session starts (S1 decides):** load the first node's kitchen from the normal story menu, and from then on route every "next level" through the map. If the map is needed before the first level, start from any kitchen and redirect at once.

**Vanilla saves:** levels played in Road mode will probably still write stars to the story save slot. That's harmless, but use a separate story save slot for Road mode to be safe.

---

## 8. Code layout

```
ChefSkills/
  Modes/
    ModeSwitch.cs           Road on/off, version gate, config key
    Heat/
      Heat.cs               knob multipliers for the current level
      HeatPatches.cs        order rate, lifetime, plate return, burn, expiry
      Target.cs             score target and HUD strip
    Road/
      RoadState.cs          run + profile data classes, JsonUtility save, backups, migrations
      MapGen.cs             seeded chapter maps
      Nodes.cs              node types, rewards
      Twists/               one class per twist (Apply/Remove)
      Bosses/               one class per boss rule
      Relics.cs             team-wide passive effects, reused from Specials
      Story.cs              text cards (Lang.T)
      RoadFlow.cs           results hook, GetNextScene routing, restart
      RoadSync.cs           host snapshot, both-confirm choices
      UI/ MapView.cs, ResultsView.cs, StoryCardView.cs
    Farm/
      Economy.cs            coins, prices
      Farm.cs               plots, growth by service, quality
      Premium.cs            premium orders on top of the bounty order
      Stock.cs              finite crates (after S2)
      Upgrades.cs           kitchen upgrades as skill multipliers
      UI/ PantryView.cs
```

Keep Road code out of the existing modules. Where it needs them, call in through small internal APIs: the bounty card, the skill multipliers, the loadout pool.

---

## 9. Plan

### 9.1 Spikes first (1–2 days each, on the Windows PC)

| # | Question | If it fails |
|---|---|---|
| S1 | Can `GetNextScene` send a **story** level to any story kitchen by scene name, online included? Where is the list of kitchens per world? | Play Road from the DIY / More Levels menu like RandomLevel did, or choose the next kitchen on the results screen and accept a menu step |
| S2 | Can a `HandlePickup` prefix block a pickup cleanly on host and client? | "Closed crate" fallback (5.7) |
| S3 | Does calling `ServerFlammable.Ignite` on a stove from the host start a normal fire everywhere? | Inferno boss uses its fallback (4.5) |
| S4 | How big can a lobby data value be? Does a SpecialNet message suit a ~2–4 KB snapshot? | Split the snapshot into keys |
| S5 | Where is a level's star score data? Is it readable at level start? | A per-level target table, filled in by hand from a debug log |
| S6 | Which field limits the number of open orders? | Drop "Crowded board", keep the rest of Heat |

### 9.2 Phases

Each phase ends with something you can play.

| Phase | Content | Playable result | Size (rough) |
|---|---|---|---|
| 1 | Heat knobs, target and HUD strip, Heat chosen in config | Any level at any Heat, starting tonight | small |
| 2 | Twists that use proven hooks, chosen in config | Hard, varied levels without the map | small–medium |
| 3 | Road state and save, map generation, map and results screens, Reputation, routing (after S1) | The campaign, no economy yet | large |
| 4 | Skill unlock pool, relics, story cards, Events and Rest | The full campaign loop | medium |
| 5 | Bosses 1, 3, 5 and 6 (no spikes needed) | Chapter ends | medium |
| 6 | Coins, Pantry screen, shop, kitchen upgrades | The economy | medium |
| 7 | Farm, quality, premium orders | Farming | medium |
| 8 | Short stock and bosses 2 and 4 (after S2 and S3), Road+ | Everything | medium |
| 9 | Online sync polish, export and import, stats page | Long-term comfort | small–medium |

Phases 1–2 can ship as their own ChefSkills update. They fix "too easy" before the big pieces exist.

---

## 10. Tuning

- **Find your Heat first.** After phase 1, play a few levels you know well at Heat 2, 4, 6 and 8. The Heat you two can just barely win should be about where chapter 4 sits. Then move the whole table up or down.
- **Watch for "too easy."** Log each Road level's score as a share of its target. If wins in a chapter are mostly above 130%, raise that chapter's Heat by 1.
- **Watch for a grind wall.** If a chapter takes more than 3 restarts, lower its Heat or make its Rest nodes give 2 Reputation.
- **Economy pace:** aim for one upgrade every 2–3 levels in chapter 1, and one every 4–5 by chapter 6.
- **Keep the numbers in one file** (`Modes/Tuning.cs` or a config section) so you can change them without a code search.

---

## 11. Open questions

| # | Question | Why it matters |
|---|---|---|
| Q1 | Do you play **online on two PCs** or on **one PC**? Does the host change? | Decides how much of section 6.4 is needed. If the host changes, an export/import step or a save on both PCs is needed. |
| Q2 | Do you own the DLC kitchens, and should they be extra chapters? | More content without new work, if S1 can route them. |
| Q3 | Should skill unlocks be shared by both chefs, or each chef's own? | Shared is simpler. Your own lets you each build a role. Default: **shared** pool, both chefs pick from it. |
| Q4 | Does a loss always cost Reputation, or only below 80% of the target? | Sets how harsh the campaign feels. Default: any loss costs 1. |
| Q5 | Should ChefSkills' normal mode still offer all 75 skills? | Default: yes. The unlock pool only applies in Road mode. |
