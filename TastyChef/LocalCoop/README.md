# LocalCoop — local co-op for Tasty Chef

Tasty Chef has online co-op for up to four players, but no way for several people to play on one PC.
This mod gives every extra player their own copy of the game in their own window — a whole monitor
each when you have enough monitors, otherwise a share of Player 1's — and joins those copies to
Player 1 through the game's own **LAN room**. Everything the game's co-op does (shared restaurant,
orders, build mode, day votes, the end-of-day screen) is then the game's own code at work, because it
*is* the game's online co-op, only on one PC.

## Using it

1. Start Tasty Chef as usual (from Steam).
2. At the title screen press **F9**. A window opens for Player 2 (press F9 again for Players 3 and 4).
3. Player 2 presses **LB + RB together** on their controller. That controller is theirs from now on,
   and stops working in Player 1's window.
4. Player 1's game creates a LAN room by itself and Player 2 joins it. Player 1 picks the save in the
   room (multiplayer needs a save at day 4 or later, or a new multiplayer save) and presses **Start**.

**Shift+F9** closes the most recently added window. Closing Player 1's game closes all the others.

| Players | Two or more monitors | One monitor |
|---|---|---|
| 2 | a monitor each | left and right halves (or top and bottom) |
| 3–4 | a monitor each while there are enough, otherwise quarters of Player 1's monitor | quarters |

## What each player gets

- **Player 1** keeps the keyboard, the mouse, and every controller nobody else has taken.
- **Every other player** needs a controller. Windows sends the keyboard only to the window in front,
  and that is always Player 1's: the other windows never take focus, even when clicked.
- Each window shows its own player's view and HUD, at the size of its own screen area.
- Only Player 1's window plays sound by default; the others show the same restaurant, so their sound
  would just double it (`Guests.Sound` turns it on).

## How the windows are kept apart

The extra copies are started by Player 1's game with `-tccoop-player <n>`, and they:

- keep their own saves and files in `%USERPROFILE%\AppData\LocalLow\XpercentGames\TastyChef\LocalCoop\Player<n>\`
  (copied from Player 1's saves the first time, so the title screen looks the same);
- read Player 1's settings (language, keybinds, volume…) but write their own changes there too, so
  nothing a guest changes reaches Player 1's settings;
- sign in to Unity's room service under their own profile (it does not let one player join their own
  room), and show up in the room as `<Steam name> (P2)` instead of under Player 1's Steam name;
- skip Steam Input's native API, which merges every controller into one.

All the windows share one graphics card, and the game's VSync does not hold them back: without a cap,
the window in front ran at ~270 fps on a 144 Hz monitor while Player 2's dropped to 11–18 fps with
300 ms stalls (Windows also favours the app in front). So while co-op is on, every window is capped at
its own monitor's refresh rate, and the other players' copies run at above-normal priority, exempt from
Windows' power throttling of background apps. Measured afterwards: 144 fps and a steady 100 fps. Each
window logs its frame rate to its BepInEx log every 10 seconds (`Frames: …`). Playing alone again
gives the game its own frame-rate setting back.

Unity keeps its window settings (monitor, size, fullscreen) in the registry, shared by every copy. When
the first extra window opens, Player 1's game saves them, and a hidden watcher puts them back once every
copy of the game has closed — so the next normal start opens where it always did.

The game ships set to allow one running copy (`single-instance=` in `TastyChef_Data\boot.config`). The
installer removes that line, and the mod removes it again at every start in case a game update restored
it. `Uninstall.cmd` puts it back.

## Needs

- **Internet**, the same as the game's own LAN room: Unity's room service hands out the room code.
  The game itself then plays directly between the copies on this PC.
- **A network adapter with a private IPv4 address** (any home router gives you one). The game's LAN
  room refuses to start without it.
- **A controller per extra player.** XInput (Xbox-style) controllers are what this was tested with.
- **BepInEx 5.4.23.5 or newer.** 5.4.23.2, which other games here use, crashes Tasty Chef (Unity 6000.4)
  before the title screen. The installer installs or updates it.

## Settings

`BepInEx\config\manhl.tastychef.localcoop.cfg`, written the first time the game starts with the mod:

| Setting | Default | What it does |
|---|---|---|
| `Keys.AddPlayer` | `F9` | Opens a window for one more player (Player 1's window only). |
| `Keys.RemovePlayer` | `F9 + LeftShift` | Closes the most recently added player's window. |
| `Windows.Layout` | `Auto` | `Auto`: a monitor each when there are enough monitors, otherwise Player 1's monitor is shared. `SplitScreen`: always share Player 1's monitor. |
| `Windows.TwoPlayerSplit` | `SideBySide` | How two players share one monitor: `SideBySide` or `Stacked`. Three or four always get quarters. |
| `Windows.FrameRateCap` | `0` | While playing together no window draws faster than this; `0` = each window's own monitor refresh rate (or the game's frame-rate limit when lower). See below. |
| `Guests.Sound` | `false` | Sound in the other players' windows too. |
| `Guests.AutoJoin` | `true` | Other windows join Player 1's LAN room by themselves. |
| `Player1.AutoCreateLanRoom` | `true` | With another window open and Player 1 at the title, create the LAN room once. If Player 1 leaves a room, the mod does not create another. |
| `Player1.PlayersAtStart` | `1` | Open this many windows (Player 1 included) as soon as the title screen shows. Also `-tccoop-players <n>` in Steam's launch options. |

## Files it writes

| Where | What |
|---|---|
| `%TEMP%\TastyChefLocalCoop\<Player 1's process id>\` | What the windows tell each other: layout, the controllers taken, the room code. Removed the next time the game starts. |
| `...\XpercentGames\TastyChef\LocalCoop\Player<n>\` | Each extra player's saves, settings (`LocalCoop.prefs`) and Unity log (`Player.log`). |
| `BepInEx\LogOutput.log` / `LogOutput.log.1` … | Player 1's / the other players' BepInEx logs. |

## Building and installing

Needs the .NET SDK and the game with BepInEx installed (the project references the game's assemblies
in `TastyChef_Data\Managed` and BepInEx's `core`). `build.cmd` builds `LocalCoop.dll`, copies it into
`BepInEx\plugins\LocalCoop\` when the game is closed, and stages it in `..\dist\`. Game somewhere else:
`build.cmd -p:GameDir="D:\Games\Tasty Chef"`.

## Test checklist

1. Fresh start, two monitors: F9 → Player 2's window fills the second monitor, Player 1 stays fullscreen
   on its own. Neither window flickers or keeps resizing.
2. With Player 1's window in front, both BepInEx logs show `Frames:` at about their monitor's refresh
   rate (Player 2's in `LogOutput.log.1`), and Player 2's window moves smoothly.
3. `Layout = SplitScreen`, one monitor: Player 1 and Player 2 each get half of Player 1's monitor,
   borderless, with no gap.
4. Player 2's window says "Press LB + RB together"; pressing them on a controller hands it to Player 2,
   and the same controller no longer moves anything in Player 1's window.
5. Player 1's room appears by itself; Player 2 joins and shows as `<name> (P2)` with its own colour.
6. Player 1 picks a day-4+ (or new multiplayer) save and presses Start; both windows load the restaurant
   and each controller moves its own chef. Clicking Player 2's window does not take the keyboard away
   from Player 1.
7. Shift+F9 closes Player 2's window; Player 1's window goes back to its old size/mode and its room
   shows one player.
8. Close Player 1's game with Player 2 still open: Player 2's window closes within a few seconds. Start
   the game normally afterwards: it opens on the same monitor and at the same size as before co-op.
9. Player 1's own settings (volume, language) are unchanged by anything done in Player 2's window.
