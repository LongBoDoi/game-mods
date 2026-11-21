# OC2Tweaks — lobby management and quick restart for Overcooked! 2

The settings live in the game's own menu: **Settings → OC2 TWEAKS** (in Vietnamese). There you force
host for public matchmaking, place the corner ping overlay, switch off or rebind the one-key level
restart, and lift saved bans. Kicking and banning happen on the game's own player cards, which gain a
working **Kick** for the host. Everyone's ping — including your own — is on the corner overlay
(**F9**). Settings and the ban list are saved as you change them. Moderation only needs the mod on the
host. For a client to see the other players' ping, the host must run the mod too: only the host can
measure everyone, and it passes the figures on.

Until 3.0.0 all of this was a separate window on **F6**. That window is gone; the **F6** key now does
nothing. **F5** (restart) and **F9** (overlay) still work as before.

This was two plugins until 2.0.0 — **LobbyTools** and **QuickKeyRestart**. The installer carries the
old settings over; see [Upgrading](#upgrading-from-lobbytools--quickkeyrestart).

## Using it

On the main menu, or in a lobby, go to **Settings** and choose **OC2 TWEAKS** (under Game, Controls
and Credits, next to any entries other mods add). The screen is a copy of the game's own Game
Settings screen, so it looks, sounds and navigates like the rest of the menu with mouse, keyboard or
pad:

- **Z / C** (LB / RB on a pad, or a click) switch between the three tabs along the top. The open
  tab is orange. The caps at either end show the current keys, or the game's own LB / RB icons
  once you use a pad; pressing a keyboard key switches them back.
- **Up/down** moves between rows; the list scrolls to follow. Down from the last row reaches XONG.
- **Left/right** (or the arrows) changes a choice; **Enter/A** ticks a box or opens a row.
- **Esc** or **XONG** (done) closes the screen. There is no Save: every change is written to the
  config file as you make it.
- The line under the list says what the selected row does, or what just happened.

| Tab | Rows |
|---|---|
| **Tùy chọn** (options) | **Luôn làm chủ phòng khi tìm trận** (force host: public Arcade/Versus matchmaking creates your lobby and waits for others; off by default, applies from the next search), **Hiện độ trễ ở góc màn hình** and **Vị trí bảng độ trễ** (the corner overlay, see [Ping](#ping)), **Chơi lại màn bằng một phím** (quick restart on/off, see [Quick restart](#quick-restart)). |
| **Phím tắt** (hotkeys) | **Chơi lại màn** (F5), **Bật/tắt bảng độ trễ** (F9), **Thẻ trước** / **Thẻ sau** (Z / C). See [Choosing the keys](#choosing-the-keys). |
| **Đã cấm** (banned) | The saved list with the date of each ban; select one to **Bỏ cấm** (unban). The tab shows the count. |

The screen opens on the tab you last used.

### Kick and ban

The pause menu and the round-results screen show the game's player cards; this build of the game
hides their **Kick** entry on every platform and gives it an empty handler. The mod un-hides it for
the host and points it at the game's own pop-up:

- **Đuổi** — they leave and can rejoin.
- **Cấm...** — asks once more, then saves their Steam ID and kicks them. If that account shows up in
  a lobby you host again, it is kicked automatically, whatever screen you are on.
- **Hủy** — cancel. The cursor starts here, so a stray Enter never kicks or bans anyone.

Clients don't see **Kick**, and nobody sees it on their own players. Every confirmation re-checks
that you are the host, the connection state and the selected player. Players sharing one remote
machine leave together, and the pop-up says how many people that is. Bans are lifted in **Đã cấm**
on the settings screen.

The ban check also runs while the game is paused, so a banned account can appear briefly before the
next check. When the game switches connection mode, the mod waits for the connection to settle
before acting again. Invites and manually picking a lobby keep the game's own flow.

## Ping

Ping is shown on the [corner overlay](#corner-overlay). Every figure is a round trip to the
**host**, in milliseconds — the host's own row is therefore always 0 ms, on the host's screen and on
everyone else's.

| Row | Host's screen | Client's screen |
|---|---|---|
| The host | 0 ms (loopback) | 0 ms |
| You, as a client | — | measured by you, to the host |
| Another client | measured by you | **relayed by the host** once a second |

The game is client-server: a client is only ever connected to the host, so it has no way to time
anyone else. The host's mod passes its figures on. If you join a host without the mod, those rows
say **chủ phòng thiếu mod** (host lacks the mod) after 6 seconds instead of measuring forever.
Figures are green under 80 ms, yellow under 150 ms, red above that, and grey when there has been no
fresh reply for 3 seconds (the last value stays). **đang đo...** means no figure yet.

### Corner overlay

Tick **Hiện độ trễ ở góc màn hình**, or press **F9** anywhere, to keep a small list of everyone's
ping in a corner of the screen while you play. **Vị trí bảng độ trễ** picks the corner (top right by default).
Your own line reads **Bạn**; a shared connection shows its head count. It uses the game's body
font, takes no clicks or keys,
hides itself outside an online room, and remembers whether it was on. Pressing F9 with the settings
screen open updates its tick box too.

## Quick restart

Press **F5** while a level is running and it restarts immediately — the same thing **Tạm dừng →
Chơi lại → Đồng ý** does, without the menu or the confirmation dialog, which is what you want when
a run is already lost three seconds in.

Three things deliberately do nothing:

- **Anything that is not a running level.** The main menu, the campaign map, the lobby and the
  loading screens between levels all ignore the key. Restarting during a scene change is what
  strands a match on a black screen, so the mod checks the game is in a live level first.
- **A second press within 3 seconds.** A restart tears the level down and loads it again; a second
  one while that is still in flight can leave the match unrecoverable.
- **The key while the OC2 TWEAKS screen is open** — it could be the key you are choosing.

The first two write a line to `BepInEx\LogOutput.log` rather than failing silently, so it is clear
which one you hit. A restart is a host decision and the game takes everyone in the match with it,
so only the host needs this.

### Choosing the keys

In **Phím tắt**, select a row and press the new key — any key on the keyboard. Hold **Ctrl**,
**Shift** or **Alt** while pressing it for a combination (`Ctrl + R` restarts on Ctrl+R and nothing
else); a modifier on its own is not a binding, so it keeps waiting. **Esc** (or any pad button)
cancels without closing the screen. While it waits, the row reads **Nhấn phím...** and the menu
ignores every key, so the key you press cannot also move the selection, press XONG or fire the
mod's other hotkeys; it stays that way until you let go.

The line under the list then warns if the key is already one of the mod's other hotkeys, if the game
itself uses it while playing (WASD, Space, E, the arrows… — add a modifier), or if Host Force Theme
(F1/F2) or RandomLevel (F7/F8) uses it. Unticking **Chơi lại màn bằng một phím** in **Tùy chọn**
keeps the key you chose but stops it working. LB/RB always switch tabs on a pad; they are read
through the game's own pad layer, so any pad the game supports works.

## Building and installing

Needs the .NET SDK, the Steam Mono build of the game, and BepInEx 5 **x86**: this build is Unity
2017.4.8 on CLR 2.0 with a 32-bit executable, so the x64 loader used by the Bone's Cafe mod here
will not work. Cross-play between the Steam and Epic builds is untested.

```bat
build.cmd
..\installer\Install.cmd
```

`build.cmd` stages `OC2Tweaks.dll` in `..\dist\`, which `scripts\release.cmd Overcooked2 <version>`
zips with the installer.

- `build.cmd -p:GameDir="D:\Games\Overcooked! 2"` builds against another install.
- `build.cmd -p:DeployToGame=true` also copies the DLL into the game (close it first).
- `..\installer\Install.cmd` installs BepInEx 5 x86 if it is missing, then the mod; it replaces only
  this mod's DLL and leaves other plugins alone.
- `..\installer\Uninstall.cmd` removes the DLL and keeps the config and ban list.

## Settings and ban list

Config: `BepInEx\config\com.manhl.overcooked2.tweaks.cfg`. Everything in it is on the settings
screen; edit the file only for a key combination, and only with the game closed.

- `[Matchmaking] ForceHost = true/false` — **Luôn làm chủ phòng khi tìm trận**.
- `[QuickRestart] Enabled = true/false` — **Chơi lại màn bằng một phím**.
- `[QuickRestart] RestartKey = F5` — a `KeyboardShortcut`, so `R + LeftControl` works.
- `[PingOverlay] Enabled = true/false` — the corner overlay, off by default.
- `[PingOverlay] Corner = TopRight` — `TopLeft`, `TopRight`, `BottomLeft` or `BottomRight`.
- `[PingOverlay] ToggleKey = F9` — a `KeyboardShortcut`.
- `[Settings] PreviousTabKey = Z`, `[Settings] NextTabKey = C` — the tab keys on the settings screen.

A `[General] ToggleKey` left over from 2.x opened the old window and is no longer read.

Ban list: `BepInEx\config\com.manhl.overcooked2.tweaks.bans.tsv`. Steam IDs are stored as digit
strings, names as UTF-8/base64, and the ban time in UTC, so renaming an account does not escape a
ban. Writes replace the file atomically; if a write fails, the in-memory list is unchanged. If the
file is corrupt or unreadable the mod leaves it alone and the **Đã cấm** section says the list is
unavailable — check `BepInEx\LogOutput.log`, fix the file and restart the game. Kicking still works.
Host Force Theme keeps its own separate list.

## Upgrading from LobbyTools + QuickKeyRestart

Choose **OC2Tweaks** in `installer\Install.cmd` and it does all of this for you:

| What you had | Where it ends up |
|---|---|
| `com.manhl.overcooked2.lobbytools.cfg` | renamed to `…tweaks.cfg`, keeping your force-host choice |
| `…lobbytools.bans.tsv` | renamed to `…tweaks.bans.tsv`, byte for byte |
| `RestartKey` in `…quickkeyrestart.cfg` (or the pre-repo `com.yourname.quickkeyrestart.cfg`) | written into the new config's `[QuickRestart]` section |
| `BepInEx\plugins\LobbyTools\`, `BepInEx\plugins\QuickKeyRestart\` | deleted |

Those last two matter: left in place, the superseded plugins keep loading beside OC2Tweaks, which
means a second window on F6, two handlers on the restart key, and two mods writing one ban file.
Nothing else in `BepInEx\plugins` is touched, and the old config files are left on disk rather than
deleted.

Running the installer again does not migrate a second time. `tests\CheckMerge.ps1` asserts all of
the above against a throwaway game folder.

## How it works, and testing

**The settings screen.** A postfix on `FrontendRootMenu.Awake` copies the **Credits** entry in the
Settings tab, relabels it and points it at the mod. The copy chains up/down navigation through
every entry (the game's and other mods') and fits the tab's panel to them, growing it downwards by
moving its pivot — the tab's animator owns its position. The panel's paper is one stretched image
whose torn bottom edge grows with it, so it gets a 6.5% bottom margin, as the Story list has. Other mods add entries later (MOD Stage
Preview only on the first tab switch) and shift the rows below by hand, so this runs again whenever
the panel's children change. The game's ticket number ("05") sits outside the panel's layout,
placed by hand against its bottom edge, so it is put back there each time instead of being left
between rows. The screen itself is built the first time it is opened: the
game's Game Settings screen is copied under an inactive holder, its `FrontendOptionsMenu` (which
would load, save and undo the game's own options) is removed before it ever wakes, and a
`FrontendMenuBehaviour` of the mod's takes its place, hooked to the main menu's private
`OnMenuShow`/`OnMenuHide` so Esc and the menu stack treat it like any other screen.

Rows are copies of the game's own **Windowed** (tick box), **Resolution** (left/right selector) and
a selector with its arrows removed (plain row). A tick box or selector talks to an `IOption`; the
mod's `ToggleBridge`/`ChoiceBridge` implement that over its config entries, and each copied row's
`m_OptionType` is set to a value the game does not define first, so a copied row's `Awake` never
binds to — or changes — the real resolution or window mode. The list initialises while it is
empty, so the game never wires its own navigation; the mod links rows itself and scrolls to keep
the selected row in view. The tab caps take their pad icons from the game's `ControllerIconLookup`
(the bordered Xbox-style set the game's own prompts use on PC), and switch on InControl's active
device reporting a button or stick press, and back on a keyboard key. Copied buttons get the
event system the game gave their originals, which the game only does for objects that existed when
the menu loaded. Pop-ups are the game's own `T17DialogBox`, given literal text (the localiser shows
a quoted tag as written) and the body font while the mod's pop-up is up — the headline font has no
Vietnamese letters with a dot below — then put back as it closes, since the pop-up is shared.

**Player cards.** A prefix on `UIPlayerRootMenu.ReCreateUIPlayers` sets `m_canKickUsers`; the game
still withholds Kick from clients and from local players. A prefix on `UIPlayerMenuBehaviour.Awake`
sets the Kick entry's platform mask (0 in this build) to every platform, and a prefix on
`OnClick(_option)` sends Kick to the mod's prompt — `KickUser` itself is empty and small enough to
be inlined, so it is not the patch target.

**Moderation.** This build's `UIPlayerMenuBehaviour.KickUser` has an empty body, so the mod uses
`ServerUserSystem.RemoveUser`: secondary players first, then the primary one, which lets the game
clean up the chefs, the users and the disconnect itself. Steam IDs come from the real connections,
and the same kick path handles a banned account turning up.

Force host patches `ClientLobbyFlowController.TryJoinGame` to call the game's own `HostGame`,
keeping the mode, the lobby visibility and the game's completion/error handling. The patch runs
after Host Force Theme and respects `__runOriginal`, so no lobby is created twice and joining a
specific lobby still works. The mod does not depend on Host Force Theme.

**Ping.** Figures come from the host's connection table keyed by player session ID, so they are
never attributed to the wrong player. The host's own ping is a zero-millisecond loopback sample. A
client reads its own latency from its connection to the host.

The host relays the rest inside the game's own latency probe (`LatencyMessage`) rather than a new
message type, so a player without the mod is never sent anything it cannot parse. The probe's send
time — always `Time.realtimeSinceStartup`, never negative — carries a negative code instead:
`-(1 + (sessionUniqueId << 15 | stale << 14 | ms))`, at most 2^23 so every code is an exact float.
`sessionUniqueId` is the per-room byte the game itself sends to every client in its user list, so
host and client agree on who is who. A prefix on `LatencyMeasure.OnLatencyReceived` keeps the figure
on a modded client and never runs the game's handler for a code: a vanilla client answers it like
any probe, and the host drops that echo before a negative time can reach the latency average. The
host sends each client one probe per other client per second, skipping the recipient's own row.

To keep measuring, `BaseConnection.GetLatencyTestPaused` is patched to return `false` only for the
connections this machine actually holds: every remote connection while hosting, and the single
connection to the host when joined. Probe scheduling and reply handling stay on the game's own
path, the original pause flag is never changed, and unpatching restores stock behaviour.

**Quick restart** invokes the game's own `InGamePauseMenu.OnRestartConfirmed()` — the handler the
"restart?" dialog calls when you say yes — on the first instance whose `gameObject.scene` is valid
(`Resources.FindObjectsOfTypeAll` also returns prefab templates, which throw when driven). An
active `ServerFlowControllerBase` is the game's own marker for "a level is running". If a game
update renames the method, that one tweak reports itself unavailable on the settings screen and in
the log.

If any of the settings screen's hooks are missing after a game update, the log says so and the
rest of the mod keeps running on its config file.

Offline tests:

```bat
dotnet run --project ..\tests\OC2Tweaks.Tests.csproj
powershell -NoProfile -File ..\tests\CheckRuntime.ps1
powershell -NoProfile -File ..\tests\CheckMerge.ps1
```

They cover saving the ban list, host permission, re-kicking a banned account, the hosting flow, the
ping unit conversion, missing and invalid data, measurement surviving a level load, patch scope,
stale figures, preferring the channel with a fresh reply, the local player's own ping on host and
client, the relay code round trip and its rejection of real send times, relayed figures landing on
the right row, a host without the mod being named, and that a high ping never kicks anyone. For the
settings screen and overlay: player names with rich-text tags, control characters or too many letters
are made safe, ping cells read and colour consistently, bindings read as people write them (`Ctrl + R`,
`Num 7`), clashes with the mod's other keys, the game's controls and other mods are flagged, and the
option bridges wrap, save only real changes and redraw. `CheckRuntime.ps1` checks the built DLL's API calls against the game's real
assemblies, including every name reached by reflection or patched by name — the Settings hooks,
the option fields, the player-card members (with the `OnClick` prefix's parameter name), the ping
targets, the pad icon lookup and pad input (`ControllerIconLookup`, InControl) and
`InGamePauseMenu.OnRestartConfirmed()`. `CheckMerge.ps1` covers the upgrade above.

The ban-file check uses `IOException` because the game's runtime has no
`System.IO.InvalidDataException`.

## In-match checklist

Verified in game for 3.0.0 (offline): the Settings entry and panel, opening and closing (Esc, XONG,
reopen), the Settings panel with MODs and MOD Stage Preview also installed (rows in order, "05" at
the bottom), the three tabs, switching with Z and C including wrapping round, the LB / RB icons on
the caps in pad mode (forced through a test probe) and back to Z / C on a key, up/down and scrolling, the tick
boxes saving to the config file, binding keys by pressing them (a plain key, Ctrl+R, Esc cancelling
without closing the screen, the warning for a key the game uses, the new key not also firing its old
action), F9 updating its tick box live, Vietnamese text and long or tag-laden names, and the unban
pop-up (cursor on Hủy, list and selection after unbanning). The rest
needs other players:

1. Close the game, install the mod, start it and check `BepInEx\LogOutput.log` for load or patch
   errors.
2. Host a friend lobby, invite another Steam account and press F9: the overlay shows player names,
   shared-connection counts and ping. Your own line reads **Bạn** and **0 ms**. No Steam IDs
   anywhere.
3. Start a level, pause, and pick the other player's card → **Kick** → **Đuổi**: they leave, you
   stay host, they can rejoin.
4. Their card → **Kick** → **Cấm...** → **Cấm**: the account is saved and kicked. Invite them again
   or re-matchmake — they must be kicked automatically. **Đã cấm** on the settings screen lists them.
5. Restart the game and host again: the ban still holds. Unban, and check they can stay.
6. Join someone else's lobby: your line has a real ping to the host and the host's is 0 ms.
   - With three or more players and the mod on the host: the other clients' rows show the figures
     the host's overlay shows for them (within a second), in a lobby and during a level.
   - Host without the mod: after about 6 s those rows say **chủ phòng thiếu mod**, and the game
     plays normally — no disconnects, no ping spikes on the host's screen.
   - Mod on the host, one client without it: that client plays normally and the host keeps a sane
     ping for them (the relay echo must not reach the average).
7. **Player cards.** As host, pause during a level: a remote player's card offers **Kick**, which
   opens Đuổi / Cấm... / Hủy. Your own card doesn't offer it, and neither does any card on a
   client.
8. **Pad.** Open the settings screen with a pad: the caps beside the tabs show the LB / RB icons,
   and LB / RB step one tab per press. Press a keyboard key: the caps go back to **Z** / **C**.
9. Test a remote group of two people on one machine: kicking that group leaves other connections
   alone.
10. Switch force host on and search for a public Arcade and a public Versus match: you must be the
    host. Restart to confirm the choice was saved. Switch it off and normal matchmaking returns.
11. With Host Force Theme also forcing host, only one hosting request is made, and picking a
    specific lobby still joins that lobby.
12. Keep a high-ping player in the lobby for at least a minute: they must stay unless banned or
    kicked by hand.
13. Load a level together: ping keeps updating; while the other player has not answered, their
    figure turns grey instead of disappearing.
14. After using the mod's pop-ups, trigger one of the game's own (quit to menu, unsaved Game
    Settings): it uses the game's headline font as normal.

Quick restart, on top of that:

15. **In a level, alone.** Press F5 mid-level: it restarts at once, with no dialog.
16. **Cooldown.** Press F5 twice quickly: the second press is ignored and the log says so.
17. **Not in a level.** Press F5 on the main menu, in the lobby and on the campaign map: nothing
    happens, and the log says it is not in a level.
18. **During a load.** Press F5 while a level is loading: nothing happens, no black screen.
19. **In online co-op, as host.** Press F5: everyone restarts together.
20. **As a client.** With the mod installed on a non-host machine, F5 does nothing to the match.
21. **Rebound.** In **Phím tắt**, select **Chơi lại màn** and press F7: F7 restarts, F5 no longer does,
    and the screen warns that RandomLevel uses F7. Bind Ctrl+R instead: the row reads **Ctrl + R** and
    only Ctrl+R restarts.
22. **Switched off.** Untick the box: the key does nothing, and ticking it back on restores the key
    you had chosen.

Ping overlay:

23. **Toggle.** Press F9 in an online room: the overlay appears top right with every player. F9 again
    hides it. Restart the game: it remembers.
24. **Corners.** **Vị trí bảng độ trễ** cycles all four corners; the overlay stays inside the screen in each.
25. **In play.** With the overlay on, play a level: clicks and keys reach the game normally, and the
    figures keep updating.
26. **Offline.** On the main menu or offline, nothing is drawn.
