# Overcooked! 2 mods

| Mod | What it does |
|---|---|
| [LobbyTools](LobbyTools/) | In-game panel (**F6**, Vietnamese) for the lobby you host: every remote player's **ping** in ms, **kick**, **ban** (saved, and re-kicked automatically next time), and **force host** so public Arcade/Versus matchmaking creates your lobby instead of joining someone else's |

**Install:** download `Overcooked2-v*.zip` from the release, extract it anywhere, and double-click `Install.cmd` with the game closed. It finds the game through Steam, installs BepInEx 5 x86 if it's missing, and copies the mod into `BepInEx\plugins\LobbyTools\`. `Uninstall.cmd` removes the mod and keeps your settings and ban list.

**Using it:** press **F6** in a lobby you host, including during a level or on the campaign map. The **Người chơi** (players) tab refreshes every 0.25 s and shows each remote player's name and ping. Steam IDs are used internally for bans and never shown in the panel.

- **Đuổi** (kick): the player leaves and can rejoin.
- **Cấm** (ban): saves their Steam ID and kicks them. If that account turns up in a lobby you host again, the mod kicks it automatically, even with the panel closed.
- **Bỏ cấm** (unban): removes an account from the list in the **Đã cấm** tab.

Kicking and banning both ask for confirmation, and only the host can do either. Players sharing one remote machine share a connection, so they leave together.

**Force host:** in **F6 → Ghép trận** (matchmaking), switch on **Luôn làm chủ phòng khi ghép trận** before searching for a public Arcade/Versus match. You create the lobby and wait for others to join. The choice is saved, applies from the next search, and is off by default.

**Ping** is display only: it never filters matchmaking and never kicks anyone for a bad connection. With no measurement yet the panel shows **đang đo...** (measuring); while a level loads or the game pauses its own measuring, it keeps the last value and adds **(đang cập nhật)** after 3 seconds without a fresh reply.

**Settings and ban list:** `BepInEx\config\com.manhl.overcooked2.lobbytools.cfg` and `…lobbytools.bans.tsv`. Both survive uninstalling.

For the Steam Mono build only (Unity 2017, 32-bit, BepInEx 5 **x86**) — not the x64 loader other Unity mods here use. Players joining you don't need the mod. See [LobbyTools/README.md](LobbyTools/README.md) for the details and the in-match test checklist.
