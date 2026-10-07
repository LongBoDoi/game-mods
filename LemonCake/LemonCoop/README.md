# LemonCoop — local split-screen co-op for Lemon Cake

Adds a second player to Lemon Cake (UE 4.24): two characters, horizontal split-screen, each with
their own room camera, both playable on gamepads. Built on UE4SS (Lua).

## Install

Double-click `Install.cmd` from the `LemonCake` release zip, with the game closed. In a repo checkout,
run `build.cmd install`. The installer does the following; do these by hand if you'd rather:

1. UE4SS v3.0.1 in `LemonCake/Binaries/Win64/` (`dwmapi.dll`, `UE4SS.dll`, `UE4SS-settings.ini`, `Mods/`),
   with `EnableHotReloadSystem = 0` and `bUseUObjectArrayCache = false` in `UE4SS-settings.ini`.
2. This folder at `LemonCake/Binaries/Win64/Mods/LemonCoop/` (only `Scripts/`, `DualMonitor.ps1` and
   `README.md` are needed at runtime) and `LemonCoop : 1` in `Mods/mods.txt`.
3. Gamepad bindings — the game ships keyboard-only input mappings, and the `Saved/Config` layer
   does **not** add mappings (verified: 0 loaded). They are shipped as a patch pak instead:
   `LemonCake/Content/Paks/LemonCake-WindowsNoEditor_P.pak`, built by `build.cmd` from `pak/`
   (repak, V8B, mount `../../../`). It overrides
   `LemonCake/Config/DefaultInput.ini` with the original file plus these lines:

   ```ini
   [/Script/Engine.InputSettings]
   +AxisMappings=(AxisName="MoveForward",Scale=1.000000,Key=Gamepad_LeftY)
   +AxisMappings=(AxisName="MoveRight",Scale=1.000000,Key=Gamepad_LeftX)
   +ActionMappings=(ActionName="Interact",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Gamepad_FaceButton_Bottom)
   +ActionMappings=(ActionName="Sprint",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Gamepad_FaceButton_Left)
   +ActionMappings=(ActionName="Sprint",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Gamepad_RightTrigger)
   +ActionMappings=(ActionName="Pause",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Gamepad_Special_Right)
   +ActionMappings=(ActionName="Recipe 1",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Gamepad_DPad_Up)
   +ActionMappings=(ActionName="Recipe 2",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Gamepad_DPad_Right)
   +ActionMappings=(ActionName="Recipe 3",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Gamepad_DPad_Down)
   +ActionMappings=(ActionName="Recipe 4",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Gamepad_DPad_Left)
   +ActionMappings=(ActionName="Recipe 5",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Gamepad_LeftShoulder)
   +ActionMappings=(ActionName="Recipe 6",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Gamepad_RightShoulder)
   ```

## Playing

- **Steam Input must be off for this game** (Steam → game Properties → Controller → "Disable Steam
  Input"). The game has no native controller support, so Steam otherwise turns the pad into a fake
  keyboard/mouse and Unreal never sees any gamepad keys.
- Controller routing (`Config.FirstGamepadToPlayer2` in `Scripts/main.lua`):
  - `true` (default): the **first** gamepad drives player 2; player 1 uses keyboard/mouse.
  - `false`: gamepad 1 → player 1, gamepad 2 → player 2.
- The mod logs `input: N/M axis and N/M action mappings are gamepad` at startup in `UE4SS.log`;
  if it says NONE, the patch pak is missing.
- The game's own key-rebinding screen (`WBP_ControlsSingle`) removes every mapping for the names it
  manages and re-adds the saved keyboard key — at HUD creation and on every save load — which wipes
  gamepad bindings that come from config. The mod therefore re-adds its gamepad bindings at runtime
  whenever they are missing (checked every 2 s; `mappings: re-added N gamepad bindings` in the log).
  Movement is bound to the game's spare `ArrowForward`/`ArrowRight` axes (same movement code, not
  managed by the rebind screen), so the stick never depends on the restore.
- Player 2 joins automatically about two seconds after you are in the game (new game or loaded save).
  Set `Config.AutoJoin = false` in `Scripts/main.lua` to join by hand instead.
- Keys: **F1** toggle split layout (top/bottom ↔ side by side) · **F2** join player 2 · **F3** remove
  player 2 · **F4** teleport player 2 next to player 1 · **F8** dual-monitor mode (see below).
- Dual monitor (F8): Unreal renders both players into one window, so the mod makes that window
  borderless and stretches it across both monitors with a side-by-side split, each player's view
  sized to its own monitor (player 1 on the primary). `DualMonitor.ps1` does the window part and is
  launched by the mod; press F8 again to go back to normal. Requires the monitors side by side.
- Hand-off rule: pressing Interact makes the interactables you are touching (and loose items within
  reach) yours; everything else keeps its current owner, so an oven or customer the other player is
  dealing with is not affected.
- `Config` at the top of `Scripts/main.lua`: `AutoJoin`, `AutoJoinDelayTicks`, and `Debug` (test keys
  F5 status dump, F6 cycle P2 through rooms, F7 interact as P2, F8 teleport to a spawner and
  force-unlock it, F9 interact as P1, O teleport to an item — `Debug` cheats, leave it off).
- Edit the Lua, then restart the game. Do not use UE4SS hot reload (Ctrl+R) with this mod.
- Gamepad: left stick move · A interact · X / RT sprint · Start pause · D-pad + LB/RB recipes 1–6.

Money, XP and level are shared: anything player 2 earns goes to player 1's account and the HUD.
Player 2 can pick up, carry, place, bake, serve and clean. The menus (shop, recipes, wardrobe,
save/load) stay on player 1's screen and mouse.

## Steam achievements (Ctrl+Shift+A)

Writes the achievements listed in `AchievementsToUnlock` (top of `Scripts/main.lua`) through the
same engine proxy the game uses (`AchievementWriteCallbackProxy.WriteAchievementProgress` +
`Activate`), so Steam sees normal writes from the real game. Edit that list to choose which.

This deliberately does **not** touch the `SV_ACV*` counters in `SaveSlotGlobal.sav`: those are also
pushed to Steam *leaderboards*, and inflating them would skew other players' rankings. Note that
writing 100% progress does set the Steam *stats* behind each achievement to their threshold values.

Verify what unlocked by reading Steam's own cache:
`Steam\appcache\stats\UserGameStats_<accountid>_1338330.bin` — the `AchievementTimes` block lists an
unlock timestamp per achievement index, ordered as in `UserGameStatsSchema_1338330.bin`.

## Save safety

Every launch, the mod snapshots your `.sav` files into
`%LOCALAPPDATA%\LemonCake\Saved\SaveGames_LemonCoopBackups\<timestamp>\` (newest 15 kept; identical
snapshots are skipped). To roll back, copy a folder's `.sav` files over
`%LOCALAPPDATA%\LemonCake\Saved\SaveGames\` with the game closed.

This exists because **co-op play makes the game save with all four rooms' upgrade flags cleared**,
while money, level, day and recipes stay correct. It has happened on more than one session and the
exact Blueprint path has not been pinned down yet (removing player 2's duplicate HUD and its
`WBP_Shop` did not stop it), so the mod also runs a guard:

`SaveGuard.ps1` watches the save folder while the game runs and, when a save appears with *every*
upgrade flag zeroed, restores them and logs to `%LOCALAPPDATA%\LemonCake\Saved\SaveGuard.log`.
The version the game wrote is kept under `SaveGuard_wiped\<timestamp>\`. It only acts when all
flags in a room are zero **and** `SV_Day >= 10`, so a genuinely new game is never touched.
Run `SaveGuard.ps1 -Once` by hand to repair a save without the game running.

`gvas.js` and `fixsave.js` (Node) here read and repair saves:
`node fixsave.js <in.sav> <out.sav>` restores all four upgrade arrays;
`node gvas.js <file.sav>` dumps a save's contents. Close the game before writing a save.

## Known limitations

- Player 2 uses the default character look.
- Minigames (bug catching, cats/pets) are player-1 only.
- Player 2 is not saved; they rejoin automatically next session.
- The HUD is one shared full-width overlay, not per-player.

## How it works (for modders)

See the comments at the top of `Scripts/main.lua`. Short version: `CreatePlayer(1)` + the engine's
built-in split-screen, a runtime `PlayerStart` (the map has none), a `CameraActor` for player 2
moved between the game's four fixed room-camera positions, and a hook on `Actor:IsOverlappingActor`
that re-points every interactable's cached `Player` reference at whichever pawn is interacting.

Do not hook static Blueprint-library natives (`GameplayStatics.*`) with UE4SS 3.0.1 — they are
invoked via `EX_CallMath` and the hook breaks the call game-wide. Do not use Ctrl+R hot reload
with native hooks either; restart the game.
