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
            Console.WriteLine("PASS: " + checks + " checks (Vietnamese feedback, display-only ping, saved-ID kicks, force hosting and persistence).");
        }
        finally
        {
            // This runner only deletes the unique temporary directory it created.
            Directory.Delete(directory, true);
        }
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
        return new SteamOnlineMultiplayerSessionUserId
        { m_steamId = new Steamworks.CSteamID { m_SteamID = id }, DisplayName = name };
    }
    private static void ResetRoster()
    {
        GameUtils.Platform.Host = true;
        GameUtils.Platform.InSession = true;
        ConnectionModeSwitcher.Status.Progress = eConnectionModeSwitchProgress.Complete;
        ServerUserSystem.Removed.Clear();
        ServerUserSystem.AfterRemoval = null;
        var host = new User { IsLocal = true, Machine = User.MachineID.One, GameState = GameState.MainMenu };
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
