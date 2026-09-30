# Bone's Cafe Tweaks

An in-game panel (**F1**, in Vietnamese) with four tweaks. All start **off**; switch them on in the panel. Settings are saved automatically.

| Tweak (in the panel) | What it does |
|---|---|
| **Lặp nhanh khi giữ nút Act** (fast Act hold) | Holding **Act** (J / gamepad X) keeps chopping and grinding at a speed you type in (1–50 times a second; the game's own repeat is 4). Repeating starts after a short hold, so a quick tap still acts once. |
| **Nguyên liệu vô hạn** (infinite ingredients) | Every premium ingredient you've discovered (beef, cheese, chicken, chocolate, egg, fish, milk, pork, sugar) is kept at **99**, and taking one from the fridge doesn't lower it. A larger stock (the top rating allows 999) is never reduced. |
| **Linh hồn vô hạn** (infinite souls) | The summon counter stays at **99** and summoning doesn't lower it, so you can summon as many skeleton assistants as you like. Normally each 5 souls collected gives one summon slot (souls are never spent). The summon unlock (its cutscene) is still required. |
| **Tự chọn kích thước quán** (cafe size) | Pick any of the game's six restaurant layouts, 0 (about 5x8) to 5 (15x25), or **Tùy chỉnh** (custom): any width 12–60 × depth 5–40 tiles, built from the largest layout. Normally your rating decides, and you can only choose once at the top rating. |

Basic ingredient stations are already unlimited in the game; only the fridge's premium ingredients are counted. The 99 is written into your save, so after switching infinite ingredients off you keep 99 of each, and they go down normally from there.

**Cafe size notes:** the size is fixed when the cafe loads. Change it before opening for the day and press **Áp dụng ngay** (apply now) to reload the cafe, the same way the game's own size picker does; otherwise it applies on the next day. Making the cafe smaller sends furniture outside the new walls back to your inventory and unsummons assistants standing outside, exactly as the game does when you shrink it at the top rating. Switching the tweak off tidies up the same way. Challenges keep their own fixed size, and the tutorial is untouched. A custom size keeps the front wall and door where they are and grows or shrinks the room to the left, right and back; beyond 25x15 the camera zooms out to fit. Seen working in-game at 60 × 40; customers and assistants walking into the new areas is not confirmed yet.

**Leaderboards:** the game posts challenge scores and daily reputation to global Steam leaderboards. Once any tweak has been on, uploads are skipped until you restart the game, and the panel says so. This can be switched off with `KeepLeaderboardsClean` in the config.

Interact (Space / A) is deliberately not made repeatable, because repeating it would just pick an item up and drop it again.

## Install

Close the game, then double-click `Install.cmd` from the `BonesCafe` release zip. In a repo checkout, run `build.cmd` first, then `..\installer\Install.cmd`.

The installer finds the game in your Steam libraries, installs BepInEx 5.4.23.2 if it's missing, copies the mod into `BepInEx\plugins\BonesTweaks\`, and removes the old `HoldAct` plugin from v1.0 (this plugin replaces it).

- `Install.cmd -GameDir "D:\path\to\Bone's Cafe"` uses a specific game folder.
- `Uninstall.cmd` removes the mod and leaves BepInEx installed.

## Settings

`BepInEx\config\manhl.bonescafe.tweaks.cfg`. Everything is also in the panel, except the first and last:

| Setting | Default | Meaning |
|---|---|---|
| `General.ToggleKey` | `F1` | Key that opens the panel (Input System key name) |
| `FastAct.Enabled` | `false` | Fast Act hold on/off |
| `FastAct.RepeatInterval` | `0.1` | Seconds between repeats while held (vanilla 0.25) |
| `FastAct.InitialDelay` | `0.25` | Seconds to hold before repeating starts |
| `Ingredients.Infinite` | `false` | Infinite ingredients on/off |
| `Souls.Infinite` | `false` | Infinite souls (unlimited summons) on/off |
| `CafeSize.Enabled` | `false` | Custom cafe size on/off |
| `CafeSize.Size` | `5` | Layout 0–5 (0 ≈ 5x8, 1 = 7x8, 2 = 7x12, 3 = 7x16, 4 = 10x20, 5 = 15x25) |
| `CafeSize.Custom` | `false` | Use the width × depth below instead of a preset |
| `CafeSize.Width` | `25` | Custom floor width in tiles (12–60) |
| `CafeSize.Depth` | `15` | Custom floor depth in tiles (5–40) |
| `General.KeepLeaderboardsClean` | `true` | Skip leaderboard uploads once a tweak has been on |

`CafeSize.LastLoadedCustom` is internal bookkeeping; leave it alone.

## How it works

- **Fast Act hold:** `PlayerController.CheckHoldChopKey` fires `Act()` once `currentTimeActHold > 0.25f`. A Harmony transpiler replaces that constant with a per-player threshold: `InitialDelay` until the first repeat of a hold, then `RepeatInterval`. It follows the game's own rebinding, because it reads the game's `ActHold` action.
- **Infinite ingredients:** discovered entries of `GameData.GetInventory()` are raised to 99 when the tweak is switched on, once a second while it is on, and after any gain (so a newly discovered ingredient arrives at 99). A prefix on `AdjustItemCountInInventory` drops the negative adjustments, and a postfix on `HasInventoryItem` reports discovered ingredients as in stock.
- **Infinite souls:** `SoulsManager.GetNumSummonsAvailable` (souls / 5, or the challenge's fixed number) is raised to "currently summoned + 99". `CanSummon` and the HUD counter both subtract the summoned count from it, so they always see 99 free.
- **Cafe size:** the cafe is one of six pre-built interiors, `GameController.interiorLayouts[0..5]`. A transpiler on `GameController.Awake` swaps the `GetRatingLevel()` used to pick the layout for the chosen size, and a postfix on `GameData.GetCafeSizeOverride` reports that size to everything else (assistant path bounds, the furniture-bounds cleanup, menu goals). Below the top rating, the postfix on `Awake` runs the game's own `CheckFurnitureBounds` for the new size, because the game only does that at the top rating.
- **Custom cafe size:** uses layout 5 and reshapes it at load (`CustomLayout.cs`). Every tilemap is rebuilt from the original: tiles left of the door follow the left wall, right of it the right wall, the lower part the bottom wall, and the middle is kept or filled from the front wall's 4-tile pattern. The boundary colliders, black screen blockers, customer spawn areas and the 2D light outlines are stretched with the same rule, the camera is refitted, and the NavMeshPlus surface is rebuilt. `GameController.CheckFurnitureBounds` is replaced with the custom floor rectangle.
- **Leaderboard guard:** `LeaderBoardManager.UpdateScore` is skipped. The game's completion bookkeeping (pop the pending event, run the next one) is replayed so the report screens carry on as they do after a failed upload.

## Build

Requires the .NET SDK and the game with BepInEx installed, because the project references the game's `Managed` folder and BepInEx.

```
build.cmd
build.cmd -p:GameDir="D:\Games\Bone's Cafe"     (if the game is somewhere else)
```

The build copies the DLL into the game's `BepInEx\plugins\BonesTweaks\` (if the game is closed) and into `..\dist\`.
