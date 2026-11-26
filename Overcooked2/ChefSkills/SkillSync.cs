using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Steamworks;
using Team17.Online;
using UnityEngine;

namespace ChefSkills
{
    /// <summary>
    /// Shares skill-select progress between the machines in an online game, so every screen shows
    /// every player's skills and closes only when all of them are locked in, and so every machine
    /// applies every player's skills to their chefs. That has to happen everywhere, not only on
    /// the host: the host decides what actually happens, but each machine also simulates chopping
    /// and washing progress and its own players' movement, and those must run at the host's
    /// speed or the bars lie and the chef gets pulled back. For the same reason every machine
    /// uses the host's numbers (MaxLevel, PercentPerLevel), which the host writes into the lobby.
    ///
    /// It needs no network message of its own: each machine writes its players' skill levels,
    /// ready flags and chef entity IDs into its own Steam lobby member data (a key/value store
    /// Steam keeps per lobby member and sends to everyone in the lobby; the game itself never uses
    /// it), and the host writes a round number into the lobby data each time its screen opens. A
    /// machine counts as ready for a round only once it has written that round number, so what a
    /// machine wrote during the previous level is never mistaken for being ready now. Machines
    /// without the mod write nothing. If any machine in the lobby has written nothing, the whole
    /// level is played without skills on every machine (see <see cref="PlayersWithoutMod"/>), so
    /// a chef never behaves differently from what a player without the mod expects.
    ///
    /// Value written per machine (version 5; a machine on another Chef Skills version counts as
    /// not having it, since it would apply skills or dashes differently):
    /// "5|round|phase|slot,ready,colour,level.level...,chefId.chefId,active.passive.passive.quirk;..."
    /// The host also writes "chefskills.rules" = "maxLevel|percent.percent...|specialFlags" into the lobby.
    /// </summary>
    internal sealed class SkillSync
    {
        internal enum Phase { Idle, Picking, Done, Skip }

        internal enum CardState { Choosing, Ready, Loading, NoMod }

        /// <summary>One local player's build, as written for the other machines.</summary>
        internal struct Entry
        {
            public int Slot;
            public bool Ready;
            public int Colour;
            public int[] Levels;
            public uint[] Chefs;
            /// <summary>Special-skill loadout, see <see cref="SpecialSlot"/>.</summary>
            public int[] Specials;
        }

        /// <summary>One card for a player on another machine.</summary>
        internal struct RemoteCard
        {
            public string Key;
            public string Name;
            public CardState State;
            public int[] Levels;
            public int Colour;
            public int[] Specials;
            /// <summary>Its chefs' entity IDs (null when not known yet): which team it plays for.</summary>
            public uint[] Chefs;
        }

        private sealed class Published
        {
            public int Round;
            public Phase Phase;
            public readonly List<Entry> Entries = new List<Entry>();
        }

        private const string MemberKey = "chefskills";
        private const string RoundKey = "chefskills.round";
        private const string RulesKey = "chefskills.rules";
        // 4: dashes are reported by the player's machine and followed by the host (DashSync),
        // which only works when both sides do it. 5: special skills (a loadout per player, and
        // the SpecialNet channel every machine has to read). 6: SpecialNet protocol 2 (refusal
        // reasons, visual effects) and the reworked specials. 7: protocol 3 (aimed actives: the
        // chosen target in the press, chefs standing still while aiming; Swap and Summon wind up).
        // 8: the balance pass (a cap on freed points, skill floors, capped bonuses, shared
        // cooldowns, protocol 4), which changes what every machine simulates.
        private const string Version = "8";
        private const float TickSeconds = 0.5f;
        private const int LevelLimit = 5;

        private static readonly FieldInfo LobbyField =
            AccessTools.Field(typeof(SteamOnlineMultiplayerSessionCoordinator), "m_lobbyId");

        // The round this machine last took part in (and the lobby it was in), and a "done" still
        // owed for a round that hadn't started yet when this machine's screen closed.
        private static int s_lastRound;
        private static CSteamID s_roundLobby;
        private static bool s_owesDone;
        private static List<Entry> s_owedEntries;
        private static float s_nextTick;

        // This level is being played without skills because someone in the lobby lacks the mod.
        private static bool s_ignored;

        // The round that belongs to the level being played now (0 until this machine has taken
        // part in one), so builds from the previous level never apply to this level's chefs,
        // whose entity IDs may be reused.
        private static int s_levelRound;

        // This machine is the host and skipped this level's round for everyone.
        private static bool s_hostSkipped;

        // What the host last wrote as its rules.
        private static string s_writtenRules;

        // Chef entity ID -> the skill levels its player picked on their own machine, for every
        // chef on another machine (kept on every machine, see the class comment).
        private static readonly Dictionary<uint, int[]> s_remote = new Dictionary<uint, int[]>();

        // Chef entity ID -> its player's special-skill loadout, for the same chefs.
        private static readonly Dictionary<uint, int[]> s_remoteSpecials = new Dictionary<uint, int[]>();

        // Chef entity ID -> which player plays it (machine and player slot), for the same chefs:
        // a player playing alone controls two chefs.
        private static readonly Dictionary<uint, string> s_remoteOwner = new Dictionary<uint, string>();

        /// <summary>The last round this machine took part in, in this lobby (0 in a new lobby).</summary>
        private static int LastRound(CSteamID lobby)
        {
            return s_roundLobby == lobby ? s_lastRound : 0;
        }

        private static void Remember(CSteamID lobby, int round)
        {
            s_roundLobby = lobby;
            s_lastRound = round;
            s_levelRound = round;
        }

        /// <summary>
        /// A new level is loading: forget the last level's builds, until this level's round (if
        /// any) is joined.
        /// </summary>
        internal static void NewLevel()
        {
            s_levelRound = 0;
            s_ignored = false;
            s_hostSkipped = false;
            s_remote.Clear();
            s_remoteSpecials.Clear();
            s_remoteOwner.Clear();
        }

        /// <summary>
        /// The level another machine's player picked for a skill, by their chef's entity ID (0
        /// when unknown: no mod, skipped, not locked in, or playing offline).
        /// </summary>
        /// <summary>
        /// This level is being played online with everyone on this version of Chef Skills (this
        /// machine took part in its round and nobody without the mod turned up), whether or not
        /// anyone's skills apply.
        /// </summary>
        internal static bool EveryoneHasMod
        {
            get { return s_levelRound > 0 && !s_ignored; }
        }

        internal static int RemoteLevel(uint chef, Skill skill)
        {
            int[] levels;
            if (chef == 0 || !s_remote.TryGetValue(chef, out levels)) return 0;
            return levels[(int)skill];
        }

        /// <summary>Another machine's chef's special-skill loadout (null when unknown).</summary>
        internal static int[] RemoteSpecials(uint chef)
        {
            int[] specials;
            return chef != 0 && s_remoteSpecials.TryGetValue(chef, out specials) ? specials : null;
        }

        /// <summary>
        /// Who plays another machine's chef: the same text for every chef of the same player (null
        /// when unknown).
        /// </summary>
        internal static string RemoteOwner(uint chef)
        {
            string owner;
            return chef != 0 && s_remoteOwner.TryGetValue(chef, out owner) ? owner : null;
        }

        /// <summary>In an online lobby (false offline): which one, and whether this machine hosts it.</summary>
        internal static bool TryGetLobby(out CSteamID lobby, out bool host)
        {
            host = false;
            SteamOnlineMultiplayerSessionCoordinator coordinator;
            if (!TryLobby(out coordinator, out lobby)) return false;
            host = coordinator.IsHost();
            return true;
        }

        private readonly CSteamID _lobby;
        private readonly CSteamID _self;
        private readonly bool _host;
        private string _written;
        private List<Entry> _entries = new List<Entry>();
        private Phase _phase = Phase.Picking;

        internal int Round { get; private set; }
        internal bool IsHost { get { return _host; } }
        internal bool WaitingForHost { get { return Round <= 0; } }
        internal bool HostSkipped { get; private set; }
        internal bool HostStarted { get; private set; }
        internal bool Finished { get; private set; }

        /// <summary>A machine without Chef Skills turned up while the screen was open.</summary>
        internal bool MissingMod { get; private set; }

        /// <summary>
        /// Online with other machines: the names of the players whose machine doesn't have Chef
        /// Skills (or has it turned off). Empty offline, alone, or when everyone has it.
        /// </summary>
        internal static List<string> PlayersWithoutMod()
        {
            var missing = new List<string>();
            try
            {
                SteamOnlineMultiplayerSessionCoordinator coordinator;
                CSteamID lobby;
                if (!TryLobby(out coordinator, out lobby)) return missing;
                CSteamID self = SteamUser.GetSteamID();
                foreach (KeyValuePair<CSteamID, List<string>> machine in Machines(coordinator))
                {
                    if (machine.Key == self || Read(lobby, machine.Key) != null) continue;
                    for (int i = 0; i < machine.Value.Count; i++)
                        missing.Add(string.IsNullOrEmpty(machine.Value[i]) ? "?" : machine.Value[i]);
                }
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không kiểm tra được ai trong phòng có Chef Skills: " + error.Message);
            }
            return missing;
        }

        /// <summary>
        /// Plays this level without skills because not everyone has the mod. The host opens and
        /// at once skips a round, so any screen already open elsewhere closes and no joining
        /// player's build is applied; a client owes the host a "done" with no build.
        /// </summary>
        internal static void IgnoreLevel()
        {
            s_ignored = true;
            s_remote.Clear();
            s_remoteSpecials.Clear();
            try
            {
                SteamOnlineMultiplayerSessionCoordinator coordinator;
                CSteamID lobby;
                if (!TryLobby(out coordinator, out lobby)) return;
                if (coordinator.IsHost())
                {
                    s_owesDone = false;
                    s_hostSkipped = true;
                    int round = Math.Max(ReadRound(lobby), LastRound(lobby)) + 1;
                    Remember(lobby, round);
                    SteamMatchmaking.SetLobbyData(lobby, RoundKey, round.ToString(CultureInfo.InvariantCulture));
                    SteamMatchmaking.SetLobbyMemberData(lobby, MemberKey, Format(round, Phase.Skip, null));
                }
                else
                {
                    s_owesDone = true;
                    s_owedEntries = null;
                }
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("Không báo được cho phòng Steam rằng màn này không dùng kỹ năng: " + error.Message);
            }
        }

        private SkillSync(CSteamID lobby, CSteamID self, bool host)
        {
            _lobby = lobby;
            _self = self;
            _host = host;
        }

        /// <summary>
        /// Starts syncing for this level's screen, or returns null when there's nobody to sync
        /// with: playing offline, alone in the lobby, or the host doesn't have Chef Skills (then
        /// there's no round to agree on, and this machine's screen runs on its own).
        /// </summary>
        internal static SkillSync Begin()
        {
            try
            {
                SteamOnlineMultiplayerSessionCoordinator coordinator;
                CSteamID lobby;
                if (!TryLobby(out coordinator, out lobby)) return null;
                CSteamID self = SteamUser.GetSteamID();
                bool host = coordinator.IsHost();

                bool anyoneElse = false;
                foreach (IOnlineMultiplayerSessionUserId member in coordinator.Members())
                {
                    var steam = member as SteamOnlineMultiplayerSessionUserId;
                    if (steam != null && steam.m_steamId != self) anyoneElse = true;
                }
                if (!anyoneElse) return null;

                var sync = new SkillSync(lobby, self, host);
                s_owesDone = false;
                s_owedEntries = null;
                s_ignored = false;
                s_hostSkipped = false;
                s_remote.Clear();
                s_remoteSpecials.Clear();
                // The screen's limits and percentages are the host's from the start.
                SyncRules(lobby, host);
                if (host)
                {
                    sync.Round = Math.Max(ReadRound(lobby), LastRound(lobby)) + 1;
                    Remember(lobby, sync.Round);
                    SteamMatchmaking.SetLobbyData(lobby, RoundKey, sync.Round.ToString(CultureInfo.InvariantCulture));
                    Plugin.Log.LogInfo("Đồng bộ chọn kỹ năng: lượt " + sync.Round + " (chủ phòng).");
                }
                else
                {
                    if (Read(lobby, SteamMatchmaking.GetLobbyOwner(lobby)) == null)
                    {
                        Plugin.Log.LogInfo("Chủ phòng không cài Chef Skills; màn chọn kỹ năng chỉ gồm người chơi trên máy này.");
                        return null;
                    }
                    sync.TryJoinRound();
                }
                return sync;
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("Không đồng bộ được màn chọn kỹ năng qua phòng Steam: " + error.Message);
                return null;
            }
        }

        /// <summary>
        /// A client waits for the host to open this level's round: the round number in the lobby
        /// must be new to this machine and the host must not already have finished it.
        /// </summary>
        private void TryJoinRound()
        {
            if (Round > 0) return;
            int round = ReadRound(_lobby);
            if (round <= 0 || round == LastRound(_lobby)) return;
            // A round the host has already finished may be an old level's, unless this machine has
            // been through a round in this lobby before: then any newer round is this level's
            // (the host skipped, or started, before this machine got here) and Poll follows it.
            Published host = Read(_lobby, SteamMatchmaking.GetLobbyOwner(_lobby));
            if (host != null && host.Round == round && (host.Phase == Phase.Done || host.Phase == Phase.Skip) &&
                (LastRound(_lobby) <= 0 || round < LastRound(_lobby))) return;
            Round = round;
            Remember(_lobby, round);
            Plugin.Log.LogInfo("Đồng bộ chọn kỹ năng: lượt " + round + ".");
        }

        /// <summary>Writes this machine's current builds (only when they changed).</summary>
        internal void Publish(List<Entry> entries)
        {
            if (Finished) return;
            _entries = entries;
            Write();
        }

        /// <summary>Reads everyone else's state. Call once per frame while the screen is open.</summary>
        internal List<RemoteCard> Poll(out bool allReady)
        {
            allReady = true;
            var cards = new List<RemoteCard>();
            try
            {
                if (!_host) TryJoinRound();
                Write();

                SteamOnlineMultiplayerSessionCoordinator coordinator;
                CSteamID lobby;
                if (!TryLobby(out coordinator, out lobby) || lobby != _lobby)
                {
                    // The session ended; nobody left to wait for.
                    return cards;
                }
                CSteamID owner = SteamMatchmaking.GetLobbyOwner(_lobby);

                foreach (KeyValuePair<CSteamID, List<string>> machine in Machines(coordinator))
                {
                    if (machine.Key == _self) continue;
                    List<string> names = machine.Value;
                    string key = machine.Key.m_SteamID.ToString(CultureInfo.InvariantCulture);
                    Published data = Read(_lobby, machine.Key);

                    if (data == null)
                    {
                        MissingMod = true;
                        for (int i = 0; i < names.Count; i++)
                            cards.Add(Card(key, i, names, CardState.NoMod, null, -1));
                        continue;
                    }

                    bool current = Round > 0 && data.Round == Round && data.Phase != Phase.Idle;
                    if (current && machine.Key == owner)
                    {
                        if (data.Phase == Phase.Skip) HostSkipped = true;
                        if (data.Phase == Phase.Done) HostStarted = true;
                    }
                    bool finished = data.Phase == Phase.Done || data.Phase == Phase.Skip;
                    if (current && finished && data.Entries.Count == 0)
                    {
                        // Finished without a build (its screen closed early, or it skipped).
                        for (int i = 0; i < names.Count; i++)
                            cards.Add(Card(key, i, names, CardState.Ready, null, -1));
                        continue;
                    }
                    if (!current || data.Entries.Count == 0)
                    {
                        allReady = false;
                        for (int i = 0; i < names.Count; i++)
                            cards.Add(Card(key, i, names, CardState.Loading, null, -1));
                        continue;
                    }

                    for (int i = 0; i < data.Entries.Count; i++)
                    {
                        Entry entry = data.Entries[i];
                        bool ready = finished || entry.Ready;
                        allReady &= ready;
                        {
                            RemoteCard card = Card(key, i, names, ready ? CardState.Ready : CardState.Choosing, entry.Levels, entry.Colour);
                            card.Specials = entry.Specials ?? SpecialCatalog.Empty();
                            card.Chefs = entry.Chefs;
                            cards.Add(card);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không đọc được trạng thái chọn kỹ năng của người chơi khác: " + error.Message);
            }
            if (Round <= 0) allReady = false;
            return cards;
        }

        /// <summary>
        /// Marks this machine finished (or, on the host's skip, finished for everyone). Other
        /// machines still on the screen count every player here as ready from now on.
        /// </summary>
        internal void Finish(List<Entry> entries, bool skipForEveryone)
        {
            if (Finished) return;
            Finished = true;
            _entries = entries;
            _phase = skipForEveryone && _host ? Phase.Skip : Phase.Done;
            // Closed before the host opened this round: owe a "done" for when it does (Tick).
            if (Round <= 0)
            {
                s_owesDone = true;
                s_owedEntries = entries;
            }
            else Write();
            if (MissingMod) s_ignored = true;
            if (_phase == Phase.Skip) s_hostSkipped = true;
            RefreshRemote(_lobby);
        }

        private void Write()
        {
            if (Round <= 0) return;
            string value = Format(Round, _phase, _entries);
            if (value == _written) return;
            SteamMatchmaking.SetLobbyMemberData(_lobby, MemberKey, value);
            _written = value;
        }

        /// <summary>
        /// Called every frame by the plugin. While this machine is in an online lobby it makes sure
        /// the lobby knows this machine has Chef Skills (so the others wait for its players), keeps
        /// the host's rules in the lobby (host) or in use here (others), pays a "done" owed from a
        /// screen that closed before the host's round started, and keeps the table of the other
        /// machines' players' skills up to date.
        /// </summary>
        internal static void Tick()
        {
            if (Time.unscaledTime < s_nextTick) return;
            s_nextTick = Time.unscaledTime + TickSeconds;
            try
            {
                SteamOnlineMultiplayerSessionCoordinator coordinator;
                CSteamID lobby;
                if (!TryLobby(out coordinator, out lobby))
                {
                    s_owesDone = false;
                    s_remote.Clear();
                    s_remoteSpecials.Clear();
                    s_levelRound = 0;
                    s_writtenRules = null;
                    SkillStats.UseHostRules(null);
                    return;
                }
                CSteamID self = SteamUser.GetSteamID();
                string mine = SteamMatchmaking.GetLobbyMemberData(lobby, self, MemberKey);

                if (!Plugin.Enabled.Value)
                {
                    if (!string.IsNullOrEmpty(mine)) SteamMatchmaking.SetLobbyMemberData(lobby, MemberKey, "");
                    s_remote.Clear();
                    s_remoteSpecials.Clear();
                    return;
                }
                if (string.IsNullOrEmpty(mine) || !mine.StartsWith(Version + "|", StringComparison.Ordinal))
                    SteamMatchmaking.SetLobbyMemberData(lobby, MemberKey, Format(0, Phase.Idle, null));

                bool host = coordinator.IsHost();
                SyncRules(lobby, host);
                if (!host && s_owesDone)
                {
                    int round = ReadRound(lobby);
                    if (round > 0 && round != LastRound(lobby))
                    {
                        Remember(lobby, round);
                        s_owesDone = false;
                        SteamMatchmaking.SetLobbyMemberData(lobby, MemberKey, Format(round, Phase.Done, s_owedEntries));
                        s_owedEntries = null;
                    }
                }
                RefreshRemote(lobby);
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không cập nhật được dữ liệu Chef Skills trong phòng Steam: " + error.Message);
            }
        }

        /// <summary>
        /// The host writes its MaxLevel and PercentPerLevel numbers into the lobby; everyone else
        /// uses them in place of their own, so the same build means the same speed everywhere.
        /// </summary>
        private static void SyncRules(CSteamID lobby, bool host)
        {
            if (host)
            {
                SkillStats.UseHostRules(null);
                string rules = SkillStats.FormatOwnRules();
                if (rules != s_writtenRules || SteamMatchmaking.GetLobbyData(lobby, RulesKey) != rules)
                {
                    SteamMatchmaking.SetLobbyData(lobby, RulesKey, rules);
                    s_writtenRules = rules;
                }
            }
            else
            {
                s_writtenRules = null;
                SkillStats.UseHostRules(SteamMatchmaking.GetLobbyData(lobby, RulesKey));
            }
        }

        /// <summary>
        /// Rebuilds chef entity ID -> skill levels from what the other machines published for this
        /// level's round, the same way on every machine. Only a locked-in (or finished) build
        /// counts, because that is when its own machine starts using it. It is read live rather
        /// than once, so a player who unlocks and changes their build after the level started ends
        /// up with the build their own screen shows. If the host skipped the round, nobody's build
        /// counts.
        /// </summary>
        private static void RefreshRemote(CSteamID lobby)
        {
            s_remote.Clear();
            s_remoteSpecials.Clear();
            s_remoteOwner.Clear();
            if (s_ignored || s_hostSkipped) return;
            int round = s_roundLobby == lobby ? s_levelRound : 0;
            if (round <= 0) return;
            CSteamID self = SteamUser.GetSteamID();
            CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobby);
            if (owner != self)
            {
                Published host = Read(lobby, owner);
                if (host != null && host.Round == round && host.Phase == Phase.Skip) return;
            }
            int members = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int m = 0; m < members; m++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, m);
                if (member == self) continue;
                Published data = Read(lobby, member);
                if (data == null || data.Round != round || data.Phase == Phase.Idle) continue;
                bool finished = data.Phase == Phase.Done || data.Phase == Phase.Skip;
                foreach (Entry entry in data.Entries)
                {
                    if (entry.Chefs == null || !(finished || entry.Ready)) continue;
                    // The host's rules apply: its MaxLevel, and no build spending more than it freed.
                    // Its specials under the host's rules, and the points its quirk frees.
                    int[] specials = SpecialCatalog.Enforce(entry.Specials);
                    int[] levels = SkillStats.Enforce(entry.Levels, specials);
                    string who = member.m_SteamID.ToString(CultureInfo.InvariantCulture) + ":" +
                                 entry.Slot.ToString(CultureInfo.InvariantCulture);
                    foreach (uint chef in entry.Chefs)
                    {
                        if (chef == 0) continue;
                        s_remote[chef] = levels;
                        s_remoteSpecials[chef] = specials;
                        s_remoteOwner[chef] = who;
                    }
                }
            }
        }

        // --- reading and writing ----------------------------------------------------------------

        private static bool TryLobby(out SteamOnlineMultiplayerSessionCoordinator coordinator, out CSteamID lobby)
        {
            coordinator = null;
            lobby = default(CSteamID);
            if (LobbyField == null || !SteamPlayerManager.Initialized) return false;
            var platform = GameUtils.RequestManagerInterface<IOnlinePlatformManager>();
            if (platform == null) return false;
            coordinator = platform.OnlineMultiplayerSessionCoordinator() as SteamOnlineMultiplayerSessionCoordinator;
            if (coordinator == null || coordinator.Members() == null) return false;
            lobby = (CSteamID)LobbyField.GetValue(coordinator);
            return lobby.IsValid() && lobby.IsLobby();
        }

        /// <summary>Each machine in the session, in the session's order, with its players' names.</summary>
        private static List<KeyValuePair<CSteamID, List<string>>> Machines(SteamOnlineMultiplayerSessionCoordinator coordinator)
        {
            var machines = new List<KeyValuePair<CSteamID, List<string>>>();
            IOnlineMultiplayerSessionUserId[] members = coordinator.Members();
            if (members == null) return machines;
            foreach (IOnlineMultiplayerSessionUserId member in members)
            {
                var steam = member as SteamOnlineMultiplayerSessionUserId;
                if (steam == null || !steam.m_steamId.IsValid()) continue;
                List<string> names = null;
                foreach (KeyValuePair<CSteamID, List<string>> machine in machines)
                    if (machine.Key == steam.m_steamId) names = machine.Value;
                if (names == null)
                {
                    names = new List<string>();
                    machines.Add(new KeyValuePair<CSteamID, List<string>>(steam.m_steamId, names));
                }
                names.Add(member.DisplayName);
            }
            return machines;
        }

        private static RemoteCard Card(string machine, int index, List<string> names, CardState state, int[] levels, int colour)
        {
            string name = index < names.Count ? names[index] : null;
            if (string.IsNullOrEmpty(name))
                name = (names.Count > 0 && !string.IsNullOrEmpty(names[0]) ? names[0] : Lang.T("PLAYER", "NGƯỜI CHƠI")) + " " + (index + 1);
            return new RemoteCard
            {
                Key = machine + ":" + index, Name = name, State = state,
                Levels = levels ?? SkillStats.Empty(), Colour = colour, Specials = SpecialCatalog.Empty()
            };
        }

        private static int ReadRound(CSteamID lobby)
        {
            int round;
            string value = SteamMatchmaking.GetLobbyData(lobby, RoundKey);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out round) ? round : 0;
        }

        private static string Format(int round, Phase phase, List<Entry> entries)
        {
            var text = new StringBuilder();
            text.Append(Version).Append('|').Append(round.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(((int)phase).ToString(CultureInfo.InvariantCulture)).Append('|');
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (i > 0) text.Append(';');
                    Entry e = entries[i];
                    text.Append(e.Slot.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(e.Ready ? '1' : '0').Append(',')
                        .Append(e.Colour.ToString(CultureInfo.InvariantCulture)).Append(',');
                    for (int s = 0; e.Levels != null && s < e.Levels.Length; s++)
                    {
                        if (s > 0) text.Append('.');
                        text.Append(e.Levels[s].ToString(CultureInfo.InvariantCulture));
                    }
                    text.Append(',');
                    for (int c = 0; e.Chefs != null && c < e.Chefs.Length; c++)
                    {
                        if (c > 0) text.Append('.');
                        text.Append(e.Chefs[c].ToString(CultureInfo.InvariantCulture));
                    }
                    text.Append(',').Append(SpecialCatalog.Format(e.Specials));
                }
            }
            return text.ToString();
        }

        /// <summary>A machine's published state, or null when it has none (no Chef Skills).</summary>
        private static Published Read(CSteamID lobby, CSteamID member)
        {
            string value = SteamMatchmaking.GetLobbyMemberData(lobby, member, MemberKey);
            if (string.IsNullOrEmpty(value)) return null;
            string[] parts = value.Split('|');
            if (parts.Length < 4 || parts[0] != Version) return null;
            var data = new Published();
            int number;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return null;
            data.Round = number;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ||
                number < 0 || number > (int)Phase.Skip) return null;
            data.Phase = (Phase)number;
            foreach (string item in parts[3].Split(';'))
            {
                string[] fields = item.Split(',');
                if (fields.Length < 5) continue;
                int slot, colour;
                if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot) ||
                    !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out colour)) continue;

                int[] levels = SkillStats.Empty();
                string[] levelText = fields[3].Split('.');
                for (int s = 0; s < levels.Length && s < levelText.Length; s++)
                {
                    int level;
                    if (int.TryParse(levelText[s], NumberStyles.Integer, CultureInfo.InvariantCulture, out level))
                        levels[s] = Mathf.Clamp(level, -LevelLimit, LevelLimit);
                }

                var chefs = new List<uint>();
                foreach (string id in fields[4].Split('.'))
                {
                    uint chef;
                    if (uint.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out chef) && chef != 0)
                        chefs.Add(chef);
                }

                data.Entries.Add(new Entry
                {
                    Slot = slot, Ready = fields[1] == "1", Colour = colour, Levels = levels, Chefs = chefs.ToArray(),
                    Specials = SpecialCatalog.Parse(fields.Length > 5 ? fields[5] : null)
                });
            }
            return data;
        }
    }
}
