# Bone's Cafe Tweaks

An in-game panel (**F1**, in Vietnamese) with four tweaks. All start **off**; switch them on in the panel. Settings are saved automatically.

| Tweak (in the panel) | What it does |
|---|---|
| **Lặp nhanh khi giữ nút Act** (fast Act hold) | Holding **Act** (J / gamepad X) keeps chopping and grinding at a speed you set (4–20 times a second; the game's own repeat is 4). Repeating starts after a short hold, so a quick tap still acts once. |
| **Nguyên liệu vô hạn** (infinite ingredients) | Taking a premium ingredient (beef, cheese, chicken, chocolate, egg, fish, milk, pork, sugar) from the fridge doesn't use it up. Every ingredient you've discovered counts as in stock, so recipes that need it stay available. |
| **Linh hồn vô hạn** (infinite souls) | Summon as many skeleton assistants as you like. Normally each 5 souls collected gives one summon slot (souls are never spent); the counter now shows 99. The summon unlock (its cutscene) is still required. |
| **Tự chọn kích thước quán** (cafe size) | Pick any of the game's six restaurant layouts, 0 (about 5x8) to 5 (15x25), at any rating. Normally your rating decides, and you can only choose once at the top rating. |

Basic ingredient stations are already unlimited in the game; only the fridge's premium ingredients are counted. Infinite ingredients doesn't edit your save: counts simply stop going down, and switching it off brings back the real numbers.

**Cafe size notes:** the size is fixed when the cafe loads. Change it before opening for the day and press **Áp dụng ngay** (apply now) to reload the cafe, the same way the game's own size picker does; otherwise it applies on the next day. Making the cafe smaller sends furniture outside the new walls back to your inventory and unsummons assistants standing outside, exactly as the game does when you shrink it at the top rating. Switching the tweak off tidies up the same way. Challenges keep their own fixed size, and the tutorial is untouched. The layouts are the six the game ships with; sizes in between, or beyond 15x25, would need new level geometry.

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
| `General.KeepLeaderboardsClean` | `true` | Skip leaderboard uploads once a tweak has been on |

`CafeSize.LastLoadedCustom` is internal bookkeeping; leave it alone.

## How it works

- **Fast Act hold:** `PlayerController.CheckHoldChopKey` fires `Act()` once `currentTimeActHold > 0.25f`. A Harmony transpiler replaces that constant with a per-player threshold: `InitialDelay` until the first repeat of a hold, then `RepeatInterval`. It follows the game's own rebinding, because it reads the game's `ActHold` action.
- **Infinite ingredients:** every way of taking a premium ingredient checks `GameData.HasInventoryItem` and then calls `AdjustItemCountInInventory(id, -1)`. A prefix drops the negative adjustments, and a postfix reports discovered ingredients as in stock.
- **Infinite souls:** `SoulsManager.GetNumSummonsAvailable` (souls / 5, or the challenge's fixed number) is raised to 99. `CanSummon` and the HUD counter both read it.
- **Cafe size:** the cafe is one of six pre-built interiors, `GameController.interiorLayouts[0..5]`. A transpiler on `GameController.Awake` swaps the `GetRatingLevel()` used to pick the layout for the chosen size, and a postfix on `GameData.GetCafeSizeOverride` reports that size to everything else (assistant path bounds, the furniture-bounds cleanup, menu goals). Below the top rating, the postfix on `Awake` runs the game's own `CheckFurnitureBounds` for the new size, because the game only does that at the top rating.
- **Leaderboard guard:** `LeaderBoardManager.UpdateScore` is skipped. The game's completion bookkeeping (pop the pending event, run the next one) is replayed so the report screens carry on as they do after a failed upload.

## Build

Requires the .NET SDK and the game with BepInEx installed, because the project references the game's `Managed` folder and BepInEx.

```
build.cmd
build.cmd -p:GameDir="D:\Games\Bone's Cafe"     (if the game is somewhere else)
```

The build copies the DLL into the game's `BepInEx\plugins\BonesTweaks\` (if the game is closed) and into `..\dist\`.
