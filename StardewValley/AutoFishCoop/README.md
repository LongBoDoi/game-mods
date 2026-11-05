# AutoFishCoop — Auto Fishing per player in local co-op

[Auto Fishing 2.0](https://www.nexusmods.com/stardewvalley/mods/21339) by Tibbles keeps one set of settings
for the whole game. In local split-screen that set is shared by everyone: player one's `X` key turns auto
fishing off for all of them, the casting power keys move everybody's power, and the stamina cut-off that
stops one player casting stops the rest too. This mod gives each player their own copy, and buttons on their
own controller to change it.

It does not replace Auto Fishing, it sits next to it: install both, and keep Auto Fishing's own `config.json`
as the settings everyone starts from.

## Using it

Start local co-op as usual. Each player gets their own copy of Auto Fishing's settings the moment they join,
starting from Auto Fishing's `config.json`.

**Press the D-pad up to turn casting again by itself on and off.** It says which it now is on your own
screen, and changes nobody else's fishing.

**Click the left stick to open your own menu:**

```
          Auto Fishing - Player 2

  Auto fishing                       [x]
  Hook the fish for me               [x]
  Cast again by itself               [ ]
  Casting power                     100%
  Close
```

The D-pad moves between the rows, **A** picks one, **B** closes. It is the game's own menu furniture, so it
behaves like everything else in the game, and because Stardew gives every split-screen player their own menu
fed by their own controller, the menu that opens belongs to the player who opened it. Each player can have
their own, at the same time, on their own screen.

**The D-pad no longer walks** — the left stick does that. Walking is what the game would otherwise do with
those buttons, so a shortcut on the D-pad would take a step north every time it was used. The D-pad still
works everywhere it is not walking: in menus, in dialogue, in the chat box and in the arcade games. Set
`DisableDpadMovement` to `false` to have it back.

That answers the awkward part of local co-op: a keyboard only ever reaches player one, so there is no key
that could have spoken for player two. A controller each does.

The rest follows the player it belongs to as well:

- Auto Fishing's own keys change only the player who pressed them (player one, with the keyboard).
- The stamina cut-off that switches auto casting off applies to the player who ran out.
- A player can be given different settings to start with, in `Players` below.
- Settings belong to the farmer, not the screen: a player who leaves and rejoins gets theirs back, and a
  different farmer in their place starts fresh. They last until the game closes or `autofishcoop reload`.

| | |
|---|---|
| `autofishcoop` | A SMAPI console command: what each player's settings are at the moment, where they started, and which buttons do what. |
| `autofishcoop reload` | Re-reads `config.json` and starts everyone again from it. |
| `autofishcoop menu` | Opens player one's menu from the console, without a controller, to see how it looks. |

## Settings

`Mods\AutoFishCoop\config.json`, written the first time the mod runs:

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | `false` hands Auto Fishing its own settings back: one set for everyone, as before, and none of the buttons below. |
| `MenuButton` | `"LeftStick"` | Pressed to open the menu. Clicking the left stick in is a button the game itself does not use; any SMAPI binding works, so `"LeftStick, F8"` lets a player on the keyboard press `F8` instead. |
| `ToggleAutoCastButton` | `"DPadUp"` | Pressed to turn casting again by itself on and off, without opening the menu. |
| `ToggleAutoFishButton` | `"None"` | The same for auto fishing itself. Unbound, since one shortcut is usually enough — `"DPadDown"` is free if you want it. |
| `DisableDpadMovement` | `true` | Stops the D-pad walking the character, so its buttons are free to be shortcuts. The left stick still walks, and the D-pad still works in menus. |
| `RequireFishingRod` | `false` | `true` answers those buttons only for a player holding a fishing rod, which is worth having if you put them back on buttons the game uses for something else. |
| `Players` | `{}` | What particular players start with, keyed by player number. Anything left out is taken from Auto Fishing's own `config.json`. |

`Players` takes any of Auto Fishing's settings — `AutoCast`, `AutoFish`, `AutoHit`, `CaughtDoubleFish`,
`CastingPower`, `StaminaThreshold`:

```json
"Players": {
  "2": { "AutoFish": false, "CastingPower": 0.5 }
}
```

That leaves player one exactly as Auto Fishing is configured, and starts player two with the fishing minigame
to play themselves and a half-strength cast. It is a starting point, not a rule: if that player turns auto
fishing back on with their own button, it stays on.

## What it does not change

- **Auto Fishing does the fishing.** This mod only decides which settings Auto Fishing is looking at while it
  works for a given player; it does not cast, hook or land anything itself.
- **Nothing for a single player**, apart from the D-pad, and nothing in online multiplayer: every machine
  already has its own copy of the settings there. The per-player part is only about two to four players
  sharing one machine.
- **Auto Fishing's `config.json` stays the source of the settings.** This mod never writes to it.

## Building and installing

Needs the .NET SDK and the game with SMAPI installed (the project references the game's assemblies and
SMAPI's Harmony). Auto Fishing itself is not needed to build: it is only ever looked up by name at runtime.

```bat
build.cmd
..\installer\Install.cmd
```

`build.cmd` stages `AutoFishCoop.dll` and `manifest.json` in `..\dist\AutoFishCoop\`, which
`scripts\release.cmd StardewValley <version>` zips with the installer.

- `build.cmd -p:GameDir="D:\Games\Stardew Valley"` builds against another install.
- `build.cmd -p:DeployToGame=true` also copies the mod into the game's `Mods` folder (close it first).

## How it works

Stardew gives every split-screen player their own copy of the game and raises SMAPI's events once per player,
with `Context.ScreenId` saying whose turn it is. Auto Fishing does its work from those events — casting,
hooking, landing the fish, its own keys — but reads one settings object for all of them, because it was
written for one player.

So this mod keeps a set of settings per player and lends Auto Fishing whichever set belongs to the player it
is about to work for. A player here is their farmer's `UniqueMultiplayerID`, which never changes: a screen ID is
new every time a screen opens, so a backed-out join would turn player two into somebody else, and a player
number moves when someone ahead of them leaves. Harmony prefixes on `AutoFish.OnUpdateTicked` and `ModEntry.OnButtonPressed` write that
player's settings into Auto Fishing's own settings object first. What the player before them left in there is
kept at the moment of the swap, not when it was handed over, because Auto Fishing changes those settings
itself: the stamina cut-off switches `AutoCast` off, and the power keys move `CastingPower`. Those changes
belong to the player who caused them, and that is how they stay with them.

Nothing is reimplemented and nothing is skipped: Auto Fishing runs its own code, reading what it always reads.

The menu is an `IClickableMenu` put into `Game1.activeClickableMenu`, which is one of the statics Stardew
keeps per split-screen player, and the game feeds each player's menu from that player's own controller. So
opening it during one player's update opens it for that player alone, and the player it belongs to is simply
the farmer on the screen it was opened on. Row navigation, the A button, B to close, the UI scale and where it sits on a
split screen are all the base class's own doing; the menu only draws its rows and changes one player's
settings. What a toggle has to say goes to `Game1.hudMessages`, which is per player in the same way.

Taking the D-pad off walking duty is SMAPI's doing rather than a patch. The game reads the controller through
`Game1.input`, which SMAPI replaces with an input state it can hide buttons from, and it keeps a hidden
button hidden until it is really let go of — so one word per press is enough, and because there is one such
state per split-screen player it is hidden only from the player holding that pad. It happens only while a
player is walking about: a menu, a cut scene, the chat box or an arcade game all leave the D-pad alone,
which is why it still works in the mod's own menu.

There is one more thing per player. To dismiss the "you caught a fish" pose, Auto Fishing writes a click into
the mouse state it looked up when it started, which belongs to whichever player's game was current at the
time — so one player's catch reached into another player's mouse. A prefix on
`FishingManager.CloseCaughtFishDialogue` swaps that cached handle for one that reads `Game1.input` each time.
Stardew swaps `Game1.input` to the running player's input state, so the handle follows whoever is fishing.

Auto Fishing is never compiled against, only looked up by name at startup, so a version this mod does not
understand is a line in the log and a mod that stands aside rather than a broken game. The same goes for a
patch that throws: the first failure switches the swapping off for the session and says so.

## Testing

```bat
dotnet run --project ..\tests\StardewValley.Tests.csproj
powershell -NoProfile -File ..\tests\CheckAutoFish.ps1
```

The first covers the per-player settings with no game and no Auto Fishing involved: that a toggle picks out
one player, that Auto Fishing's own changes are kept for whoever caused them, that overrides apply to the
player they name and only start them off, that settings stay with the farmer whatever number they come back as, that switching the mod off
hands Auto Fishing its settings back, how the casting power cycles, and that the parts of `config.json`
written by player number are read as the players they mean.

`CheckAutoFish.ps1` checks the built DLL against the installed game, SMAPI and Auto Fishing: every type,
method and field resolves; the three patch targets still have the shape the prefixes need; Auto Fishing's
settings are all still there and still writable; `Game1` statics are still per player, which is what makes
the menu, the messages and the mouse handle belong to one player; the menu methods it overrides are all still
virtual, so the game would still call them; the walking input still reads all four D-pad buttons off the pad
state it gets from `Game1.input`, and SMAPI still keeps a suppressed button suppressed while it is held, so
taking the D-pad off walking duty still works; and Auto Fishing really does still read the settings object
and the cached mouse handle, so the swap is worth doing.

What neither can check is the fishing, or how any of it feels. That needs a real session with two players:

1. One player: fishing should be exactly as Auto Fishing left it. `autofishcoop` reports one player.
2. Load any save and run `autofishcoop menu`: the panel should sit in the middle of the screen, read
   correctly, and answer the mouse, a controller's D-pad and A, and B to close. This is the quickest look at
   it, and needs nobody else.
3. Walk with the D-pad: the character should not move. Walk with the left stick: they should.
4. Press the D-pad up: a message on your own screen saying casting again by itself is now on or off, and no
   step north. Press it again and the message should replace the first one, not stack under it.
5. Click the left stick: your own menu opens, with the rows selectable by D-pad, A and B.
6. Open the inventory and move around it with the D-pad, and play an arcade game at the saloon: the D-pad
   should work normally in both.
7. Add a second player with a controller, and have both fish at once. Both should fish automatically.
8. As player two, click the left stick: the menu should open on player two's screen only.
9. Turn auto fishing off in player two's menu: only player two gets the fishing minigame afterwards. Player
   one keeps fishing automatically.
10. Press the D-pad up as player two while player one is fishing: only player two's casting changes, and only
    player two's screen says anything.
11. Open both players' menus at the same time and confirm each shows and changes its own player's settings.
12. Change the casting power in the menu and cast: the bar should stop where that player asked.
13. Let player one's stamina fall past the cut-off: only player one stops casting, with the message on their
    own screen.
14. Catch a fish as player two and watch player one's screen: nothing should twitch.
15. Put `"2": { "AutoFish": false }` in `Players`, run `autofishcoop reload`, and confirm player two gets the
    fishing minigame while player one does not.
16. Have player two leave and rejoin as the same farmer: their settings should still be there. A different
    farmer in that place should start from Auto Fishing's own settings. Start a join and back out of it, then
    join properly: auto cast should stay on, and the menu should still say Player 2.
17. Set `DisableDpadMovement` to `false`, run `autofishcoop reload`, and confirm the D-pad walks again.
18. Set `Enabled` to `false`, run `autofishcoop reload`, and confirm the buttons do nothing, the D-pad walks,
    and Auto Fishing goes back to one set of settings for everyone.
