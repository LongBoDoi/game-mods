# Overcooked! 2 mods

| Mod | What it does |
|---|---|
| [LobbyTools](LobbyTools/) | In-game panel (**F6**, Vietnamese) for the lobby you host: every remote player's **ping** in ms, **kick**, **ban** (saved, and re-kicked automatically next time), and **force host** so public Arcade/Versus matchmaking creates your lobby instead of joining someone else's |
| [QuickKeyRestart](QuickKeyRestart/) | **F5** restarts the level you're playing straight away — no pause menu, no "are you sure?". Ignored outside a running level and for 3 s after each restart |

**Install:** download `Overcooked2-v*.zip` from the release, extract it anywhere, and double-click `Install.cmd` with the game closed. It finds the game through Steam and then **stays open**, asking what you want, doing it, and asking again with the list refreshed — so you can install one mod, upgrade BepInEx and check the result in one sitting. It closes when you choose **Q**.

```
Which would you like to install?

  1) LobbyTools       F6 lobby panel: ping, force host, kick, saved bans         [not installed]
  2) QuickKeyRestart  F5 restarts the level you are playing                      [installed 1.0.0.0]
  3) OC2DIYLevel      custom level pack + helper plugins (not bundled: ~400 MB)  [installed, 12 level sets]
  4) BepInEx          the mod loader itself (BepInEx 5 x86)                      [installed 5.4.23.6]

  A) all of them        Q) quit

Choose, e.g. 1,3 [A]:
```

Press Enter for everything, or pick items by number or name; the `[installed]` markers update after each round. `Uninstall.cmd` works the same way but only lists what's actually installed, and closes itself once there's nothing left. Mods go in `BepInEx\plugins\<Mod>\`; removing one leaves your settings and ban list alone.

Because the menu keeps running, the game doesn't have to be closed when you start it: if the game is open when you pick something, it says so, changes nothing, and returns to the menu — close the game and choose again.

To do one thing and exit instead (these never loop, so they're safe in scripts):

| Command | What it does |
|---|---|
| `Install.cmd -All` | both mods and BepInEx, no prompts, once |
| `Install.cmd -Only QuickKeyRestart` | one mod (BepInEx is added too if it's missing, since mods don't load without it) |
| `Install.cmd -Only BepInEx` | install or upgrade just the loader |
| `Install.cmd -Only BepInEx -BepInExVersion 5.4.23.2` | pin a specific BepInEx |
| `Uninstall.cmd -Only LobbyTools` | remove one mod |
| `Uninstall.cmd -All` | remove both mods and the loader |

**BepInEx** defaults to the newest stable **5.x** release for `win_x86`, not simply the newest release on GitHub: this game is a 32-bit Unity 2017 Mono build, so the x64 loader and the BepInEx 6 prereleases (which currently top that list) don't work on it. A `BepInEx_win_x86_*.zip` placed next to the installer is used instead of downloading. Upgrading only replaces the loader and `BepInEx\core`, so plugins and settings are untouched.

**Removing BepInEx** disables every BepInEx mod in the game, not just these two, so it asks first and names the other plugins that would stop loading. Their files and all saved settings are kept — reinstalling BepInEx brings them back. Add `-PurgeBepInEx` to delete those as well.

## OC2DIYLevel (third-party level pack)

The installer can also put the **OC2DIYLevel** custom-level pack in place, but it is **not one of my mods and is not included here**: it's someone else's work (see the `READ ME` in the pack itself for the author and their contact), and it's roughly 400 MB of Unity asset bundles, so it is neither committed to this repo nor shipped in the release zip. You download it yourself.

Pick it from the menu and the installer looks for the extracted pack in this order: `-DiyPackDir "<folder>"`, a `plugins\` folder next to the installer, `..\dist\plugins\`, then the newest `*OC2*`/`*Overcooked*` folder in your Downloads. If none of those has it, it asks you to paste the folder, and Enter skips it. Either the `plugins` folder or its parent works.

```
Install.cmd -Only OC2DIYLevel -DiyPackDir "C:\Users\you\Downloads\New Pack OC2"
Uninstall.cmd -Only OC2DIYLevel
```

Removing it asks first and says how much it is about to delete, since putting it back means copying the whole pack in again. `-Yes` skips that question.

**One deliberate difference from the pack's own instructions.** Its `READ ME` tells you to delete your whole `BepInEx\plugins` folder and drop the new one in, to avoid stale level files conflicting. That would also delete LobbyTools, QuickKeyRestart and anything else you'd installed. So the installer clears **only the pack's own entries** before copying — which is what actually prevents those conflicts — and records what it copied in `BepInEx\config\com.manhl.overcooked2.diypack.manifest`. Removing the pack takes exactly those entries back out and leaves everything else, including the pack mods' own settings, alone.

Custom levels have to match across the match: everyone playing needs the same pack.

Only the host needs either mod.

## LobbyTools

**Using it:** press **F6** in a lobby you host, including during a level or on the campaign map. The **Người chơi** (players) tab refreshes every 0.25 s and shows each remote player's name and ping. Steam IDs are used internally for bans and never shown in the panel.

- **Đuổi** (kick): the player leaves and can rejoin.
- **Cấm** (ban): saves their Steam ID and kicks them. If that account turns up in a lobby you host again, the mod kicks it automatically, even with the panel closed.
- **Bỏ cấm** (unban): removes an account from the list in the **Đã cấm** tab.

Kicking and banning both ask for confirmation, and only the host can do either. Players sharing one remote machine share a connection, so they leave together.

**Force host:** in **F6 → Ghép trận** (matchmaking), switch on **Luôn làm chủ phòng khi ghép trận** before searching for a public Arcade/Versus match. You create the lobby and wait for others to join. The choice is saved, applies from the next search, and is off by default.

**Ping** is display only: it never filters matchmaking and never kicks anyone for a bad connection. With no measurement yet the panel shows **đang đo...** (measuring); while a level loads or the game pauses its own measuring, it keeps the last value and adds **(đang cập nhật)** after 3 seconds without a fresh reply.

**Settings and ban list:** `BepInEx\config\com.manhl.overcooked2.lobbytools.cfg` and `…lobbytools.bans.tsv`. Both survive uninstalling.

## QuickKeyRestart

Press **F5** while a level is running and it restarts immediately — the same thing **Pause → Restart → yes** does, without the menu or the dialog.

It deliberately ignores the key outside a running level (main menu, lobby, campaign map, loading screens), because restarting mid-scene-change is what strands a match on a black screen. It also ignores a second press within 3 seconds, since a restart on top of a restart can leave the match unrecoverable. Both cases say so in the BepInEx log.

**Settings:** `BepInEx\config\com.manhl.overcooked2.quickkeyrestart.cfg`. `RestartKey` is a BepInEx `KeyboardShortcut`, so modifiers work (`R + LeftControl`). The F5 default stays clear of F6 (LobbyTools) and F1/F2 (Host Force Theme). Settings from a pre-repo build of this mod (GUID `com.yourname.quickkeyrestart`) are carried over by the installer.

For the Steam Mono build only (Unity 2017, 32-bit, BepInEx 5 **x86**) — not the x64 loader other Unity mods here use. Players joining you don't need either mod. See [LobbyTools/README.md](LobbyTools/README.md) and [QuickKeyRestart/README.md](QuickKeyRestart/README.md) for the details and the in-match test checklists.
