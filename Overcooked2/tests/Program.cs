using System;
using System.IO;
using OC2Tweaks;
using Team17.Online;

internal static class Program
{
    private static int checks;
    private static string directory;

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        checks++;
    }
    private static void Throws(Action action, string message)
    {
        try { action(); } catch { checks++; return; }
        throw new Exception(message);
    }
    private static BanStore NewStore(string filename)
    {
        var store = new BanStore(Path.Combine(directory, filename));
        store.Load();
        return store;
    }

    private static void Main()
    {
        directory = Path.Combine(Path.GetTempPath(), "OC2Tweaks-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Persistence();
            ModerationPolicy();
            ForceHostPolicy();
            ConnectionMonitorIntegration();
            ClientLocalPingIntegration();
            RelayEncoding();
            RelayIntegration();
            SettingsText();
            Console.WriteLine("PASS: " + checks + " checks (Vietnamese feedback, display-only ping, saved-ID kicks, force hosting, persistence and the settings screen's text).");
        }
        finally
        {
            // This runner only deletes the unique temporary directory it created.
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// What the Settings screen puts on its rows: names other people chose are made safe to
    /// show, ping cells read and colour the same everywhere, and the hotkey selectors never
    /// change a binding just by opening.
    /// </summary>
    private static void SettingsText()
    {
        Check(MenuText.SafeName("<color=red>Boss</color>") == "‹color=red›Boss‹/color›", "rich-text tags in a name are neutralised");
        Check(MenuText.SafeName("a\nb\tc") == "a b c", "control characters in a name become spaces");
        Check(MenuText.SafeName(null) == "Người chơi" && MenuText.SafeName("   ") == "Người chơi", "a missing name falls back to Người chơi");
        string longName = MenuText.SafeName(new string('x', 80));
        Check(longName.Length == MenuText.NameLength && longName.EndsWith("…"), "a long name is cut to fit one row");
        Check(MenuText.SafeName("Nguyễn Văn A") == "Nguyễn Văn A", "Vietnamese names are left alone");
        Check(MenuText.Literal("Đuổi") == "\"Đuổi\"", "dialog text is passed to the localiser as a quoted literal");

        PingBand band;
        Check(MenuText.PingCell(true, 42.4f, true, false, out band) == "42 ms" && band == PingBand.Good, "a fresh low ping is green");
        Check(MenuText.PingCell(true, 120f, true, false, out band) == "120 ms" && band == PingBand.Fair, "a middling ping is yellow");
        Check(MenuText.PingCell(true, 150f, true, false, out band) == "150 ms" && band == PingBand.Poor, "150 ms and up is red");
        Check(MenuText.PingCell(true, 30f, false, false, out band) == "30 ms" && band == PingBand.Stale, "a figure with no fresh reply keeps its value and turns grey");
        Check(MenuText.PingCell(false, 0f, false, false, out band) == "đang đo..." && band == PingBand.Waiting, "no figure yet reads as measuring");
        Check(MenuText.PingCell(false, 0f, false, true, out band) == "chủ phòng thiếu mod" && band == PingBand.Waiting, "a host without the mod is named");

        Check(MenuText.KeyLabel("F5") == "F5", "a plain key reads as itself");
        Check(MenuText.KeyLabel("R + LeftControl") == "Ctrl + R", "a combination reads modifier first, in short form");
        Check(MenuText.KeyLabel("R + LeftControl + RightControl + LeftShift") == "Ctrl + Shift + R", "left and right modifiers are named once");
        Check(MenuText.KeyLabel("Alpha3") == "3" && MenuText.KeyLabel("Keypad7") == "Num 7" && MenuText.KeyLabel("Return") == "Enter",
            "number-row, keypad and Enter keys lose Unity's names");
        Check(MenuText.KeyLabel("None") == "Chưa đặt" && MenuText.KeyLabel("") == "Chưa đặt", "an empty binding says so");
        Check(MenuText.KeyNote("F9", "F5", "F9").Contains("chức năng khác"), "a key another of the mod's hotkeys uses is flagged");
        Check(MenuText.KeyNote("W", "F5").Contains("game cũng dùng"), "a key the game uses while playing is flagged");
        Check(MenuText.KeyNote("W + LeftControl", "F5") == "", "the same key with a modifier is fine");
        Check(MenuText.KeyNote("F7", "F5").Contains("RandomLevel") && MenuText.KeyNote("F1", "F5").Contains("Host Force Theme"),
            "keys other mods in this collection use are flagged");
        Check(MenuText.KeyNote("F10", "F5", "F9") == "", "a free key gets no warning");
        Check(MenuText.Corners.Length == MenuText.CornerNames.Length, "every corner has a name");
        Check(MenuText.CornerName(OverlayCorner.BottomLeft) == "Dưới trái" && MenuText.CornerName((OverlayCorner)99) == "Trên phải",
            "corner names, with top right for anything unknown");

        int picked = 1, writes = 0, redraws = 0;
        var choice = new ChoiceBridge(() => new[] { "A", "B", "C" }, () => picked, i => { picked = i; writes++; });
        choice.Changed = () => redraws++;
        choice.SetOption(-1);
        Check(picked == 2 && choice.GetOption() == 2, "the selector wraps left of the first choice round to the last");
        choice.SetOption(3);
        Check(picked == 0 && writes == 2 && redraws == 2, "and right of the last back to the first, redrawing each time");
        choice.SetOption(0);
        Check(writes == 2 && redraws == 3, "choosing the current value saves nothing but still redraws");
        picked = 7;
        Check(choice.GetOption() == 0, "a saved value outside the list shows as the first choice");

        bool on = false;
        int toggles = 0;
        var toggle = new ToggleBridge(() => on, v => { on = v; toggles++; });
        toggle.SetOption(1);
        toggle.SetOption(1);
        Check(on && toggles == 1 && toggle.GetOption() == 1, "a tick box saves once per real change");
        toggle.SetOption(0);
        Check(!on && toggles == 2 && toggle.GetOption() == 0, "and unticking saves the change");
    }
    private static void ForceHostPolicy()
    {
        var flow = new ClientLobbyFlowController();
        Plugin.ForceHost.Value = false;
        Check(ForceHostMatchmaking.Prefix(flow, true) && flow.HostCalls == 0,
            "Disabled force host must preserve normal matchmaking.");
        Plugin.ForceHost.Value = true;
        Check(!ForceHostMatchmaking.Prefix(flow, true) && flow.HostCalls == 1,
            "Enabled force host must invoke hosting once and skip normal matchmaking.");
        Check(ForceHostMatchmaking.Prefix(flow, false) && flow.HostCalls == 1,
            "A previous plugin's handled matchmaking call must not start another host request.");
        Plugin.ForceHost.Value = false;
        Check(ForceHostMatchmaking.Prefix(flow, true) && flow.HostCalls == 1,
            "Disabling again must restore normal matchmaking on the next attempt.");
        Plugin.ForceHost.Value = true;
        flow.FailHosting = true;
        Throws(delegate { ForceHostMatchmaking.Prefix(flow, true); },
            "An immediate host failure must not silently fall back to joining another host.");
        Plugin.ForceHost.Value = false;
    }


    private static void ConnectionMonitorIntegration()
    {
        ConnectionMonitor.Validate();
        ResetRoster();
        Plugin.Bans = NewStore("ping-bans.tsv");
        GameUtils.Platform.Host = false;
        ConnectionMonitor.Tick(0);
        GameUtils.Platform.Host = true;
        var parties = Moderation.Parties();
        var server = new Team17.Online.Multiplayer.Server();
        var poor = new Team17.Online.Multiplayer.Connection.RemoteConnection();
        var good = new Team17.Online.Multiplayer.Connection.RemoteConnection();
        server.Add(parties[0].Session, poor);
        server.Add(parties[1].Session, good);
        ConnectionModeSwitcher.SetServer(server);
        poor.Set(new[] { 0.1f, 0.125f, 0.15f }, new[] { 0.1f }, 0);
        good.Set(new[] { 0.05f }, new[] { 0.04f }, 0);
        ConnectionMonitor.Tick(0);
        Check(Math.Abs(ConnectionMonitor.Get(parties[0].Session).Sample.PingMs - 250) < 0.01f,
            "One-way seconds must convert to round-trip milliseconds, using the worse measured channel.");
        Check(ConnectionMonitor.Display(parties[0].Session, 0) == "Độ trễ: 250 ms",
            "Player list must show measured ping in Vietnamese.");
        Check(ConnectionMonitor.Display(parties[1].Session, 0) == "Độ trễ: 100 ms",
            "Measurements must map to each session rather than an aggregate order.");
        Check(!ConnectionMonitor.Display(parties[0].Session, 0).Contains(parties[0].SteamId),
            "Player list must not display the saved account ID.");
        for (float now = 0; now <= 60; now += 0.25f)
        {
            poor.Set(new[] { 0.5f }, new[] { 0.5f }, now);
            ConnectionMonitor.Tick(now);
        }
        Check(ServerUserSystem.Removed.Count == 0 && ServerUserSystem.m_Users.Count == 4,
            "Sustained high ping must never kick any player or couch guest.");
        Check(Plugin.Bans.Snapshot().Length == 0, "Ping monitoring must never save bans.");
        poor.Set(new float[0], new float[0], 60);
        ConnectionMonitor.Tick(0);
        Check(ConnectionMonitor.Display(parties[0].Session, 0) == "Độ trễ: đang đo...",
            "Missing samples must show measuring in Vietnamese, not a fabricated zero ping.");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, -1f })
        {
            poor.Set(new[] { invalid }, new float[0], 60);
            ConnectionMonitor.Tick(0);
            Check(ConnectionMonitor.Display(parties[0].Session, 0) == "Độ trễ: đang đo...",
                "Invalid telemetry must not display as a valid latency.");
        }
        poor.Set(new[] { 0.5f }, new[] { 0.5f }, 60);
        poor.Paused = true;
        bool pausedResult = true;
        Check(!PersistentPing.Prefix(poor, ref pausedResult) && !pausedResult && poor.Paused,
            "Tracked host connection must run probes without changing the game's original pause flag.");
        ConnectionMonitor.Tick(60);
        Check(ConnectionMonitor.Display(parties[0].Session, 60) == "Độ trễ: 1000 ms",
            "A paused game flag must not hide the current ping.");
        poor.Paused = false;
        ServerUserSystem.m_Users._items[0].GameState = GameState.LoadKitchen;
        poor.Set(new[] { 0.125f }, new[] { 0.125f }, 61);
        ConnectionMonitor.Tick(61);
        pausedResult = true;
        Check(!PersistentPing.Prefix(poor, ref pausedResult) && !pausedResult &&
            ConnectionMonitor.Display(parties[0].Session, 61) == "Độ trễ: 250 ms",
            "Scene loading must continue both probes and numeric ping updates.");
        ServerUserSystem.m_Users._items[0].GameState = GameState.InLevel;
        poor.Set(new[] { 0.5f }, new[] { 0.5f }, 62);
        ConnectionMonitor.Tick(62);
        Check(ConnectionMonitor.Display(parties[0].Session, 62) == "Độ trễ: 1000 ms" && ServerUserSystem.Removed.Count == 0,
            "Measurements must continue in a level without kicking high-ping players.");
        Check(ConnectionMonitor.Display(parties[0].Session, 66) == "Độ trễ: 1000 ms (đang cập nhật)",
            "No fresh reply must retain the last ping and label it as awaiting an update.");
        poor.Set(new[] { 0.02f }, new[] { 0.5f }, 66);
        poor.SetChannelTimes(66, 62);
        ConnectionMonitor.Tick(66);
        Check(ConnectionMonitor.Display(parties[0].Session, 66) == "Độ trễ: 40 ms",
            "An old high-ping channel must not override a current measurement on the other channel.");
        poor.Set(new[] { 0.5f }, new[] { 0.02f }, 66);
        poor.SetChannelTimes(62, 66);
        ConnectionMonitor.Tick(66);
        Check(ConnectionMonitor.Display(parties[0].Session, 66) == "Độ trễ: 40 ms",
            "Fresh unreliable-channel replies must take priority over stale reliable samples too.");
        var unrelated = new Team17.Online.Multiplayer.Connection.RemoteConnection { Session = parties[0].Session, Paused = true };
        pausedResult = true;
        Check(PersistentPing.Prefix(unrelated, ref pausedResult) && pausedResult,
            "A stale or unrelated connection must retain the original pause behaviour.");
        pausedResult = true;
        Check(PersistentPing.Prefix(new Team17.Online.Multiplayer.Connection.BaseConnection(), ref pausedResult),
            "Local loopback connections must retain their original pause behaviour.");
        ConnectionModeSwitcher.Status.Progress = eConnectionModeSwitchProgress.InProgress;
        pausedResult = true;
        Check(PersistentPing.Prefix(poor, ref pausedResult) && pausedResult,
            "Connection-mode transitions must retain the game's pause handling.");
        ConnectionModeSwitcher.Status.Progress = eConnectionModeSwitchProgress.Complete;
        Plugin.ConnectionMonitorReady = false;
        pausedResult = true;
        Check(PersistentPing.Prefix(poor, ref pausedResult) && pausedResult,
            "An inactive plugin must not override the pause flag.");
        Plugin.ConnectionMonitorReady = true;
        Moderation.Remove(parties[0], false);
        ConnectionMonitor.Tick(0);
        Check(ConnectionMonitor.Get(parties[0].Session) == null && ConnectionMonitor.Get(parties[1].Session) != null,
            "Departed players must lose their measurements while other sessions remain.");
        GameUtils.Platform.Host = false;
        pausedResult = true;
        Check(PersistentPing.Prefix(good, ref pausedResult) && pausedResult,
            "Client mode must not override another host's probe handling.");
        ConnectionMonitor.Tick(0);
        Check(ConnectionMonitor.Get(parties[1].Session) == null,
            "Client mode must clear the preceding hosted session's measurements.");
        Check(ConnectionMonitor.Display(parties[1].Session, 0) == "Độ trễ: đang đo...",
            "Unavailable telemetry must have a Vietnamese fallback.");
        GameUtils.Platform.Host = true;
        ConnectionModeSwitcher.SetServer(null);
        ConnectionMonitor.Tick(0);
        Check(!ConnectionMonitor.Get(parties[1].Session).Sample.Available,
            "An unavailable server must not invent a zero ping.");
        ResetRoster();
        Check(Moderation.Remove(Moderation.Parties()[0], false) == "Đã đuổi người chơi. Họ vẫn có thể tham gia lại.",
            "Successful kick feedback must be Vietnamese.");
        string reason;
        GameUtils.Platform.Host = false;
        Check(!Moderation.HostCanManage(out reason) && reason == "Chỉ chủ phòng hiện tại mới có thể đuổi hoặc cấm người chơi.",
            "Client permission feedback must be Vietnamese.");
    }

    /// <summary>
    /// A joined client must see its own ping next to the roster; the host sees zero (loopback)
    /// while clients measure the connection to the host. Remote pings stay host-measured.
    /// </summary>
    private static void ClientLocalPingIntegration()
    {
        ResetRoster();
        Plugin.Bans = NewStore("client-ping-bans.tsv");

        // Host: the local machine is a row in the display list, first, with zero loopback ping.
        ConnectionModeSwitcher.SetServer(new Team17.Online.Multiplayer.Server());
        ConnectionModeSwitcher.SetClient(null);
        ConnectionMonitor.Tick(0);
        DisplayParty[] displayed = Moderation.DisplayParties();
        Check(displayed.Length == 3, "Host display list must include the local machine and both remote parties.");
        Check(displayed[0].IsLocal, "Host display list must put the local machine first.");
        ConnectionSample local = ConnectionMonitor.Get(displayed[0].Session).Sample;
        Check(local.Available && local.Measured && Math.Abs(local.PingMs) < 0.01f,
            "Host's own ping must be a zero-millisecond loopback sample.");
        Check(ConnectionMonitor.Display(displayed[0].Session, 0) == "Độ trễ: 0 ms",
            "Host's own ping must display as zero milliseconds in Vietnamese.");
        Check(!displayed[0].Name.Contains("(bạn)") && !displayed[0].Name.Contains("Bạn"),
            "The display party must carry the local flag rather than embedding it in the name.");

        // Client: the local machine measures its connection to the host; remote rows have no source.
        var client = new Team17.Online.Multiplayer.Client();
        var toHost = new Team17.Online.Multiplayer.Connection.RemoteConnection();
        toHost.Set(new[] { 0.1f }, new[] { 0.2f }, 0);
        client.SetServerConnection(toHost);
        ConnectionModeSwitcher.SetServer(null);
        ConnectionModeSwitcher.SetClient(client);
        GameUtils.Platform.Host = false;
        ClientUserSystem.m_Users._items = new[]
        {
            new User { IsLocal = true, Machine = User.MachineID.One, GameState = GameState.MainMenu,
                SessionId = Session(76561198000000001, "Me") },
            new User { Machine = User.MachineID.Two, GameState = GameState.MainMenu,
                SessionId = Session(76561198000000002, "Host chef") },
            new User { Machine = User.MachineID.Three, GameState = GameState.MainMenu,
                SessionId = Session(76561198000000003, "Another chef") }
        };
        ConnectionMonitor.Tick(0);
        displayed = Moderation.DisplayParties();
        Check(displayed.Length == 3, "Client display list must include the local machine and the remote roster.");
        Check(displayed[0].IsLocal && !displayed[1].IsLocal && !displayed[2].IsLocal,
            "Client display list must flag only the local machine as local.");
        Check(ConnectionMonitor.Display(displayed[0].Session, 0) == "Độ trễ: 400 ms",
            "Client's own ping must measure the connection to the host as round-trip milliseconds.");
        Check(ConnectionMonitor.Display(displayed[1].Session, 0) == "Độ trễ: đang đo...",
            "A client has no way to measure other players; those rows must show the measuring fallback.");

        // Client's connection must also keep running probes, exactly like the host's remote ones.
        toHost.Paused = true;
        bool paused = true;
        Check(!PersistentPing.Prefix(toHost, ref paused) && !paused && toHost.Paused,
            "Client's host connection must keep probing without changing the original pause flag.");
        var unrelated = new Team17.Online.Multiplayer.Connection.RemoteConnection { Session = displayed[1].Session, Paused = true };
        paused = true;
        Check(PersistentPing.Prefix(unrelated, ref paused) && paused,
            "A client must not keep another player's unrelated connection probing.");

        // Leaving the session clears client measurements like it does a host's.
        GameUtils.Platform.InSession = false;
        ConnectionMonitor.Tick(0);
        Check(ConnectionMonitor.Get(displayed[0].Session) == null && ConnectionMonitor.Get(displayed[1].Session) == null,
            "Leaving the session must clear the client's measurements.");

        // Display-only roster must never offer moderation targets to a client.
        GameUtils.Platform.InSession = true;
        Check(Moderation.Parties().Length == 0, "Client must have no moderation targets even with a display roster.");
        Check(Moderation.Remove(displayed[1], false) == "Chỉ chủ phòng hiện tại mới có thể đuổi hoặc cấm người chơi.",
            "Display-only roster must not bypass the host-only moderation gate.");
    }
    /// <summary>
    /// The host's figures ride in a latency probe's send time. Every code must be negative (real
    /// send times never are), an exact float, and decode back to the same player and figure.
    /// </summary>
    private static void RelayEncoding()
    {
        foreach (int id in new[] { 0, 1, 2, 127, 254, 255 })
            foreach (float ms in new[] { 0f, 1f, 45.4f, 45.6f, 999f, 16383f })
                foreach (bool stale in new[] { false, true })
                {
                    float code = ConnectionMonitor.EncodeRelay((byte)id, ms, stale);
                    byte gotId; float gotMs; bool gotStale;
                    Check(code < 0 && ConnectionMonitor.IsRelay(code) &&
                        ConnectionMonitor.DecodeRelay(code, out gotId, out gotMs, out gotStale) &&
                        gotId == id && gotStale == stale && gotMs == (float)Math.Round(ms, MidpointRounding.AwayFromZero),
                        "Relay code must round-trip player " + id + ", " + ms + " ms, stale=" + stale + ".");
                }
        byte i; float m; bool s;
        Check(ConnectionMonitor.DecodeRelay(ConnectionMonitor.EncodeRelay(3, 50000f, false), out i, out m, out s) &&
            i == 3 && m == ConnectionMonitor.RelayMaxMs, "A ping beyond the field must clamp, not spill into the player id.");
        Check(ConnectionMonitor.DecodeRelay(ConnectionMonitor.EncodeRelay(3, -5f, false), out i, out m, out s) && m == 0,
            "A negative figure must clamp to zero.");
        foreach (float time in new[] { 0f, 0.1f, 1234.5f, float.PositiveInfinity })
            Check(!ConnectionMonitor.IsRelay(time) && !ConnectionMonitor.DecodeRelay(time, out i, out m, out s),
                "A real probe's send time (" + time + ") must never be taken for a relay.");
        foreach (float time in new[] { float.NegativeInfinity, -1.5f, -(1 << 23) - 2f, float.NaN })
            Check(!ConnectionMonitor.DecodeRelay(time, out i, out m, out s),
                "A malformed relay code (" + time + ") must be rejected.");
    }

    /// <summary>
    /// The fix for clients seeing "đang đo..." on every other row: the host passes on what it
    /// measured, and a client shows it against the right player.
    /// </summary>
    private static void RelayIntegration()
    {
        // Host: relay entries cover remote players only, keyed by session unique id.
        ResetRoster();
        Plugin.Bans = NewStore("relay-bans.tsv");
        var parties = Moderation.Parties();
        var server = new Team17.Online.Multiplayer.Server();
        var slow = new Team17.Online.Multiplayer.Connection.RemoteConnection();
        var fast = new Team17.Online.Multiplayer.Connection.RemoteConnection();
        server.Add(parties[0].Session, slow);
        server.Add(parties[1].Session, fast);
        ConnectionModeSwitcher.SetServer(server);
        slow.Set(new[] { 0.1f }, new[] { 0.1f }, 10);
        fast.Set(new[] { 0.02f }, new[] { 0.02f }, 5);
        ConnectionMonitor.Tick(10);
        var entries = ConnectionMonitor.RelayEntries(10);
        Check(entries.Count == 2, "The host must relay each measured remote player and not itself.");
        foreach (RelayEntry entry in entries)
        {
            if (entry.Id == parties[0].Session.UniqueId)
                Check(Math.Abs(entry.PingMs - 200) < 0.01f && !entry.Stale, "Relay must carry the host's figure for that player.");
            else
                Check(entry.Id == parties[1].Session.UniqueId && Math.Abs(entry.PingMs - 40) < 0.01f && entry.Stale,
                    "A figure with no recent reply must be relayed as stale.");
        }
        Check(!ConnectionMonitor.ReceiveRelay(ConnectionMonitor.EncodeRelay(9, 10, false), 10),
            "The host must ignore relays; it measures everyone itself.");
        slow.Set(new float[0], new float[0], 10);
        ConnectionMonitor.Tick(10);
        Check(ConnectionMonitor.RelayEntries(10).Count == 1, "An unmeasured player must not be relayed as zero.");

        // Client: own row measured, host row zero, other clients from the relay.
        var me = Session(76561198000000011, "Me");
        var host = Session(76561198000000012, "Host chef");
        host.IsHost = true;
        var other = Session(76561198000000013, "Another chef");
        var client = new Team17.Online.Multiplayer.Client();
        var toHost = new Team17.Online.Multiplayer.Connection.RemoteConnection();
        toHost.Set(new[] { 0.03f }, new[] { 0.03f }, 0);
        client.SetServerConnection(toHost);
        ConnectionModeSwitcher.SetServer(null);
        ConnectionModeSwitcher.SetClient(client);
        GameUtils.Platform.Host = false;
        ClientUserSystem.m_Users._items = new[]
        {
            new User { IsLocal = true, Machine = User.MachineID.One, GameState = GameState.MainMenu, SessionId = me },
            new User { Machine = User.MachineID.Two, GameState = GameState.MainMenu, SessionId = host },
            new User { Machine = User.MachineID.Three, GameState = GameState.MainMenu, SessionId = other }
        };
        ConnectionMonitor.Tick(0);
        Check(ConnectionMonitor.Display(me, 0) == "Độ trễ: 60 ms", "Client's own row must stay its own measurement.");
        Check(ConnectionMonitor.Display(host, 0) == "Độ trễ: 0 ms",
            "The host's row on a client must read zero, as it does on the host's own panel.");
        Check(ConnectionMonitor.Display(other, 0) == "Độ trễ: đang đo..." && !ConnectionMonitor.HostLacksMod(other, 0),
            "Before any relay, another client's row must say measuring.");
        toHost.Set(new[] { 0.03f }, new[] { 0.03f }, 7);
        ConnectionMonitor.Tick(7);
        Check(ConnectionMonitor.Display(other, 7) == "Độ trễ: chủ phòng chưa cài OC2Tweaks" &&
            ConnectionMonitor.HostLacksMod(other, 7),
            "A host that never relays must be named as the reason, instead of measuring forever.");
        Check(!ConnectionMonitor.HostLacksMod(me, 7) && !ConnectionMonitor.HostLacksMod(host, 7),
            "Only rows that depend on the host's relay may blame the host.");

        Check(ConnectionMonitor.ReceiveRelay(ConnectionMonitor.EncodeRelay(other.UniqueId, 123, false), 8),
            "A client must accept the host's relay.");
        ConnectionMonitor.Tick(8);
        float ms;
        Check(ConnectionMonitor.Display(other, 8) == "Độ trễ: 123 ms" && ConnectionMonitor.TryPing(other, out ms) &&
            ms == 123 && ConnectionMonitor.IsCurrent(other, 8),
            "A relayed figure must show against the player it was measured for.");
        Check(ConnectionMonitor.Display(me, 8) == "Độ trễ: 60 ms",
            "A relay for someone else must not touch the client's own row.");
        Check(ConnectionMonitor.Display(other, 12) == "Độ trễ: 123 ms (đang cập nhật)" && !ConnectionMonitor.IsCurrent(other, 12),
            "A relay that stops arriving must keep the figure but mark it as awaiting an update.");
        ConnectionMonitor.ReceiveRelay(ConnectionMonitor.EncodeRelay(other.UniqueId, 300, true), 13);
        ConnectionMonitor.Tick(13);
        Check(ConnectionMonitor.Display(other, 13) == "Độ trễ: 300 ms (đang cập nhật)",
            "The host's own stale mark must carry over to the client.");
        ConnectionMonitor.Tick(30);
        Check(ConnectionMonitor.Display(other, 30) == "Độ trễ: đang đo..." && !ConnectionMonitor.HostLacksMod(other, 30),
            "A long-silent relay must be dropped, and a host that has relayed before is not blamed.");

        // Leaving clears the relay state, so the next room starts fresh.
        GameUtils.Platform.InSession = false;
        ConnectionMonitor.Tick(31);
        GameUtils.Platform.InSession = true;
        ConnectionMonitor.Tick(32);
        Check(ConnectionMonitor.Display(other, 32) == "Độ trễ: đang đo..." && !ConnectionMonitor.HostLacksMod(other, 37),
            "A new room must wait its own grace period before blaming the host.");
        ConnectionMonitor.Tick(39);
        Check(ConnectionMonitor.HostLacksMod(other, 39), "Relay state must not leak from the previous room.");
        ResetRoster();
    }

    private static void Persistence()
    {
        const string id = "76561198000000001";
        var store = NewStore("bans.tsv");
        Check(store.Ready && store.Snapshot().Length == 0, "Missing list should start empty.");
        string name = "Tên đầu bếp\twith tabs\n<color=red>name</color> 🍳";
        store.Ban(id, name);
        var reloaded = NewStore("bans.tsv");
        Check(reloaded.Contains(id) && reloaded.Snapshot()[0].Name == name, "Unicode/delimiters must roundtrip.");
        Check(reloaded.Snapshot()[0].BannedUtc.Kind == DateTimeKind.Utc, "Dates must remain UTC.");
        reloaded.Ban(id, "renamed");
        Check(reloaded.Snapshot().Length == 1 && reloaded.Contains(id), "Account rename must not duplicate or bypass ban.");
        reloaded.Unban(id);
        Check(!NewStore("bans.tsv").Contains(id), "Unban must survive restart.");
        foreach (string invalid in new[] { "", "0", "01", "-1", "1e5", " 1", "abc", "18446744073709551616" })
            Throws(delegate { store.Ban(invalid, "bad"); }, "Invalid account ID accepted.");

        string broken = Path.Combine(directory, "broken.tsv");
        File.WriteAllText(broken, "invalid data");
        var corrupt = new BanStore(broken);
        Throws(corrupt.Load, "Malformed file should be rejected.");
        Check(!corrupt.Ready, "Corrupt file must disable edits.");
        Throws(delegate { corrupt.Ban(id, "bad"); }, "Corrupt list must not be overwritten.");
        Check(File.ReadAllText(broken) == "invalid data", "Original corrupt list must be preserved.");

        var failed = NewStore("failed.tsv");
        Directory.CreateDirectory(Path.Combine(directory, "failed.tsv"));
        Throws(delegate { failed.Ban(id, "unsaved"); }, "Failed write should report failure.");
        Check(!failed.Contains(id), "Failed save must not commit memory.");

        var saved = NewStore("locked.tsv");
        saved.Ban(id, "keep");
        using (var locked = new FileStream(Path.Combine(directory, "locked.tsv"), FileMode.Open,
            FileAccess.Read, FileShare.None))
        {
            Throws(delegate { saved.Unban(id); }, "Failed unban write should report failure.");
            Check(saved.Contains(id), "Failed unban must preserve in-memory ban.");
        }
        Check(NewStore("locked.tsv").Contains(id), "Failed unban must preserve disk ban.");
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Temporary files should be cleaned.");
    }

    private static SteamOnlineMultiplayerSessionUserId Session(ulong id, string name)
    {
        // The game numbers session members per room; the low byte keeps test ids distinct.
        return new SteamOnlineMultiplayerSessionUserId
        { m_steamId = new Steamworks.CSteamID { m_SteamID = id }, DisplayName = name, UniqueId = (byte)(id & 0xFF) };
    }
    private static void ResetRoster()
    {
        GameUtils.Platform.Host = true;
        GameUtils.Platform.InSession = true;
        ConnectionModeSwitcher.Status.Progress = eConnectionModeSwitchProgress.Complete;
        ConnectionModeSwitcher.SetClient(null);
        ClientUserSystem.m_Users._items = new User[0];
        ServerUserSystem.Removed.Clear();
        ServerUserSystem.AfterRemoval = null;
        // The host's own user is a session member in the game; its session key is how the
        // display list identifies the local machine when it renders everyone's ping.
        var host = new User
        {
            IsLocal = true, Machine = User.MachineID.One, GameState = GameState.MainMenu,
            SessionId = Session(76561198000000001, "Host chef")
        };
        var primary = new User
        {
            Machine = User.MachineID.Two, GameState = GameState.MainMenu,
            SessionId = Session(76561198000000002, "Remote chef")
        };
        var guest = new User { Machine = User.MachineID.Two, GameState = GameState.MainMenu, DisplayName = "Couch guest" };
        var other = new User
        {
            Machine = User.MachineID.Three, GameState = GameState.MainMenu,
            SessionId = Session(76561198000000003, "Other chef")
        };
        ServerUserSystem.m_Users._items = new[] { host, primary, guest, other };
    }
    private static void ModerationPolicy()
    {
        Plugin.Bans = NewStore("policy.tsv");
        ResetRoster();
        RemoteParty[] parties = Moderation.Parties();
        Check(parties.Length == 2 && parties[0].Count == 2, "Roster must group remote couch party and hide host.");
        var target = parties[0];
        GameUtils.Platform.Host = false;
        Check(Moderation.Parties().Length == 0, "Client should have no moderation targets.");
        Moderation.Remove(target, true);
        Check(ServerUserSystem.Removed.Count == 0 && Plugin.Bans.Snapshot().Length == 0, "Client action must do nothing.");
        GameUtils.Platform.Host = true;
        foreach (GameState state in new[] { GameState.InLevel, GameState.InMap, GameState.LoadKitchen,
            GameState.PartyLobby, GameState.VSLobby })
        {
            ResetRoster();
            // Mixed states occur while guests catch up during scene changes.
            ServerUserSystem.m_Users._items[0].GameState = state;
            Check(Moderation.Parties().Length == 2, "Roster must stay live in " + state);
            Moderation.Remove(Moderation.Parties()[0], false);
            Check(ServerUserSystem.Removed.Count == 2 && ServerUserSystem.m_Users.Count == 2,
                "Kick must work in " + state + " and preserve the other players.");
        }
        ResetRoster();
        target = Moderation.Parties()[0];
        ConnectionModeSwitcher.Status.Progress = eConnectionModeSwitchProgress.InProgress;
        Moderation.Remove(target, false);
        Check(ServerUserSystem.Removed.Count == 0, "Connection transition action must be rejected.");

        ResetRoster();
        Moderation.Remove(target, true); // It is a fresh roster; same Steam ID but a different object.
        Check(ServerUserSystem.Removed.Count == 0 && Plugin.Bans.Snapshot().Length == 0, "Stale confirmation must be rejected.");
        target = Moderation.Parties()[0];
        Moderation.Remove(target, false);
        Check(ServerUserSystem.Removed.Count == 2 &&
            ServerUserSystem.Removed[1] == target.Primary, "Guest must be removed before primary.");
        Check(ServerUserSystem.m_Users.Count == 2 && ServerUserSystem.m_Users._items[0].IsLocal,
            "Host and other remote connection must remain.");
        Check(Plugin.Bans.Snapshot().Length == 0, "Kick must not ban.");

        ResetRoster();
        target = Moderation.Parties()[0];
        ServerUserSystem.AfterRemoval = delegate(User user)
        { Check(Plugin.Bans.Contains(target.SteamId), "Ban must be persisted before the removal callbacks."); };
        Moderation.Remove(target, true);
        Check(NewStore("policy.tsv").Contains(target.SteamId), "Ban must be durable.");

        ResetRoster(); // Rematch creates new user/session objects, preserving just the account ID.
        ((SteamOnlineMultiplayerSessionUserId)ServerUserSystem.m_Users._items[1].SessionId).DisplayName = "Renamed chef";
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 2 && ServerUserSystem.m_Users.Count == 2,
            "A renamed saved account must be auto-kicked with its couch guest on rematch.");
        Check(ServerUserSystem.m_Users._items[1].Machine == User.MachineID.Three,
            "Automatic kicking must preserve allowed accounts.");
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 2, "Absent banned players must not trigger repeated removal.");

        ResetRoster();
        GameUtils.Platform.Host = false;
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 0, "Client must never auto-kick another host's players.");
        GameUtils.Platform.Host = true;
        GameUtils.Platform.InSession = false;
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 0, "Offline stale roster must never be auto-kicked.");
        GameUtils.Platform.InSession = true;
        ConnectionModeSwitcher.Status.Progress = eConnectionModeSwitchProgress.InProgress;
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 0, "Auto-kick must defer during connection switching.");
        ConnectionModeSwitcher.Status.Progress = eConnectionModeSwitchProgress.Complete;
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 2, "Deferred saved-ID kick must resume once connection is stable.");

        foreach (GameState state in new[] { GameState.InLevel, GameState.InMap, GameState.LoadKitchen })
        {
            ResetRoster();
            ServerUserSystem.m_Users._items[0].GameState = state;
            Moderation.EnforceBans();
            Check(ServerUserSystem.Removed.Count == 2, "Automatic saved-ID kicking must work in " + state);
        }

        Plugin.Bans = NewStore("policy.tsv"); // Persisted IDs are enforced after restart.
        ResetRoster();
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 2, "Saved-ID kicks must survive restarting the mod.");
        Plugin.Bans.Unban(target.SteamId);
        ResetRoster();
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 0, "Unbanned account must be allowed to stay on rematch.");

        ResetRoster();
        var invalidSession = (SteamOnlineMultiplayerSessionUserId)ServerUserSystem.m_Users._items[1].SessionId;
        invalidSession.m_steamId.m_SteamID = 0;
        Moderation.Remove(Moderation.Parties()[0], true);
        Check(ServerUserSystem.Removed.Count == 0 && Plugin.Bans.Snapshot().Length == 0,
            "Unknown account ID must not be saved as a ban.");
        Moderation.Remove(Moderation.Parties()[0], false);
        Check(ServerUserSystem.Removed.Count == 2, "A regular kick must still work when the ID is unavailable.");

        ResetRoster();
        Plugin.Bans.Ban("76561198000000002", "saved");
        ServerUserSystem.m_Users._items[1].SessionId = Session(0, "ID not available yet");
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 0, "Unavailable IDs must be ignored until resolved.");
        ServerUserSystem.m_Users._items[1].SessionId = Session(76561198000000002, "ID now available");
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 2, "Saved ID discovered later must trigger automatic removal.");

        ResetRoster();
        var local = (SteamOnlineMultiplayerSessionUserId)ServerUserSystem.m_Users._items[1].SessionId;
        local.IsLocal = true;
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 0, "Local sessions must never be auto-kicked.");
        local.IsLocal = false;
        local.IsHost = true;
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 0, "Host sessions must never be auto-kicked.");

        ResetRoster();
        Plugin.Bans.Ban("76561198000000003", "other saved account");
        Moderation.EnforceBans();
        Check(ServerUserSystem.Removed.Count == 3 && ServerUserSystem.m_Users.Count == 1 &&
            ServerUserSystem.m_Users._items[0].IsLocal,
            "Several saved IDs must be auto-kicked in one check without removing the local host.");

        ResetRoster();
        var selected = Moderation.Parties()[0];
        ServerUserSystem.m_Users._items[2].IsLocal = true;
        Throws(delegate { Moderation.Remove(selected, false); }, "Mixed local/remote party must refuse removal.");
        Check(ServerUserSystem.Removed.Count == 0, "Protected local party must not be partially removed.");
    }
}
