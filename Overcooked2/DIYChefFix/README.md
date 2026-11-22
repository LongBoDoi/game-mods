# DIYChef Fix

Fixes the **default chef** and the **chef grid** of the third-party **DIYChef FastInit 2.4.0** plugin
(`dev.gua.overcooked.diychef`, `BepInEx\plugins\OC2DIYChef-FastInit\`). DIYChef is someone else's mod
and is not included here. This plugin leaves its DLL untouched and patches it at runtime. It needs
DIYChef installed, and does nothing without it.

## What was broken

DIYChef only accepts chefs that your save has **unlocked**, and it checks only once, when the save
loads.

- **Default chef.** When the first chef in `prefer.txt` isn't unlocked (for example `Chef_Black_cat`
  before the story unlocks it), DIYChef falls back to `Chef_Snowman` without checking that Snowman is
  unlocked either. Snowman comes with a DLC. The only message is "Invalid default chef", and the
  "default" it picks is a chef you can't have.
- **Chef grid.** The grid, the carousel and the lobby D-pad switch only offer the chefs in
  `prefer.txt` that are unlocked. A `prefer.txt` that only names a default chef gives one chef or
  none. With none, the grid silently never opens and the original carousel shows instead.
- **Custom chefs** in `Resources\` load only if `prefer.txt` lists them.

## What this does

- Works out the default chef again whenever the save's unlocked chefs change (DLC ownership can still
  be pending when the save loads). Names match regardless of case. The fallback is always a chef you
  actually have. A local player still on the earlier fallback moves to the new default; a chef you
  picked yourself stays.
- Only a chef you listed in `prefer.txt` can become the default.
- If the first `prefer.txt` chef can't be used, says which chef and why (missing, not unlocked yet, or
  DLC you don't own), once things have settled.
- After your `prefer.txt` chefs, the grid, carousel and lobby switch offer every other unlocked chef and
  every custom chef.
- Loads every complete custom-chef folder in `Resources\`, after the ones `prefer.txt` lists.
- Logs why the grid didn't open, if it ever doesn't.

## Settings

`BepInEx\config\com.manhl.overcooked2.diycheffix.cfg`, section `[Chefs]`:

| Setting | Default | |
|---|---|---|
| `ShowAllChefs` | `true` | Offer every unlocked chef after the `prefer.txt` ones. `false`: only the `prefer.txt` chefs, like DIYChef on its own |
| `LoadAllCustomChefs` | `true` | Load every custom-chef folder, listed or not. `false`: only the listed ones |
| `UnlockBaseGameChefs` | `true` | Base-game chefs your save hasn't unlocked yet can be picked and used as the default. **DLC chefs still need their DLC.** Nothing is written to the save; set it to `false` to go back to the save's own unlocks |

## Build

`build.cmd` builds it and stages it in `..\dist\`; `build.cmd deploy` also copies it into the game
(game closed). It compiles against the game's own assemblies and BepInEx, and reaches DIYChef only
through reflection, so a different DIYChef version just makes it log an error and change nothing.

## Test checklist

1. Main menu → chef selection: the grid opens, with your `prefer.txt` chefs first, then everyone else.
2. A fresh local player starts as the first `prefer.txt` chef (e.g. `Chef_Black_cat`).
3. `LogOutput.log` has `[DIYChef Fix] Default chef …` with the expected chef, and no `Chef grid did not open`.
4. With `UnlockBaseGameChefs = false` and a locked first chef, a warning names it after a few seconds.
