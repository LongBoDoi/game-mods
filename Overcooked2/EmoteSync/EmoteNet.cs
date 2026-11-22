using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using Team17.Online;
using UnityEngine;

namespace EmoteSync
{
    /// <summary>
    /// Shares emote settings and emotes between the machines in an online game through Steam lobby
    /// member data, the per-member key/value store Steam keeps for each lobby and sends to everyone
    /// in it (the game itself never uses it). Each machine writes two keys of its own:
    ///   emotesync.profile  its emote settings (see <see cref="EmoteProfile"/>), rewritten whenever
    ///                      they change, or "2|off" when HostTools' emote text is off;
    ///   emotesync.emote    the last emote one of its players started (see <see cref="SentEmote"/>).
    /// A machine that writes neither has no Emote Sync (or an older one), and its chefs' bubbles are
    /// left as the game draws them.
    /// </summary>
    internal static class EmoteNet
    {
        private const string ProfileKey = "emotesync.profile";
        private const string EmoteKey = "emotesync.emote";
        private const float LocalRefreshSeconds = 0.25f;
        private const float PublishSeconds = 0.5f;
        private const float PollSeconds = 0.05f;

        private sealed class Member
        {
            public string RawProfile;
            public EmoteProfile Profile;
            public bool HasSync;
            public string RawEmote;
        }

        private static readonly FieldInfo LobbyField =
            AccessTools.Field(typeof(SteamOnlineMultiplayerSessionCoordinator), "m_lobbyId");

        private static readonly Dictionary<ulong, Member> s_members = new Dictionary<ulong, Member>();
        private static readonly List<ulong> s_gone = new List<ulong>();
        private static readonly HashSet<ulong> s_present = new HashSet<ulong>();

        private static CSteamID s_lobby = CSteamID.Nil;
        private static CSteamID s_self = CSteamID.Nil;
        private static string s_writtenProfile;
        private static EmoteProfile s_local;
        private static bool s_localRead;
        private static float s_nextLocal, s_nextPublish, s_nextPoll;
        private static int s_seq = Environment.TickCount & 0xFFFFF;

        /// <summary>In an online Steam lobby with Emote Sync running.</summary>
        internal static bool Online { get { return s_lobby.IsValid(); } }

        /// <summary>
        /// Online, and every other machine in the lobby runs this Emote Sync, so every in-kitchen
        /// emote arrives as a <see cref="SentEmote"/> naming exactly whose chef it is.
        /// </summary>
        internal static bool EveryoneHasSync { get; private set; }

        /// <summary>This machine's settings (null when HostTools' emote text feature is off).</summary>
        internal static EmoteProfile Local
        {
            get
            {
                float now = Time.realtimeSinceStartup;
                if (!s_localRead || now >= s_nextLocal)
                {
                    s_local = HostTools.ReadLocal();
                    s_localRead = true;
                    s_nextLocal = now + LocalRefreshSeconds;
                }
                return s_local;
            }
        }

        internal static EmoteProfile ProfileOf(CSteamID member)
        {
            Member m;
            return s_members.TryGetValue(member.m_SteamID, out m) ? m.Profile : null;
        }

        internal static bool HasSync(CSteamID member)
        {
            Member m;
            return s_members.TryGetValue(member.m_SteamID, out m) && m.HasSync;
        }

        /// <summary>Tells the other machines about an emote one of this machine's players started.</summary>
        internal static void Publish(SentEmote sent)
        {
            if (!Online) return;
            s_seq = (s_seq + 1) & 0x7FFFFFFF;
            sent.Seq = s_seq;
            SteamMatchmaking.SetLobbyMemberData(s_lobby, EmoteKey, sent.Format());
        }

        internal static void Tick(bool enabled)
        {
            CSteamID lobby;
            if (!TryLobby(out lobby))
            {
                if (s_lobby.IsValid()) Reset(CSteamID.Nil);
                return;
            }
            if (lobby != s_lobby) Reset(lobby);

            float now = Time.realtimeSinceStartup;
            if (now >= s_nextPublish)
            {
                s_nextPublish = now + PublishSeconds;
                string value = "";
                if (enabled)
                {
                    EmoteProfile local = Local;
                    value = local != null ? local.Format() : EmoteProfile.Off;
                }
                if (value != s_writtenProfile)
                {
                    SteamMatchmaking.SetLobbyMemberData(lobby, ProfileKey, value);
                    s_writtenProfile = value;
                }
            }

            if (enabled && now >= s_nextPoll)
            {
                s_nextPoll = now + PollSeconds;
                Poll(lobby);
            }
        }

        private static void Reset(CSteamID lobby)
        {
            s_lobby = lobby;
            s_members.Clear();
            s_writtenProfile = null;
            s_nextPublish = 0f;
            s_nextPoll = 0f;
            EveryoneHasSync = false;
            s_self = lobby.IsValid() ? SteamUser.GetSteamID() : CSteamID.Nil;
        }

        private static void Poll(CSteamID lobby)
        {
            s_present.Clear();
            bool everyone = true;
            int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < count; i++)
            {
                CSteamID id = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                if (!id.IsValid() || id == s_self) continue;
                s_present.Add(id.m_SteamID);

                Member member;
                bool isNew = !s_members.TryGetValue(id.m_SteamID, out member);
                if (isNew)
                {
                    member = new Member();
                    s_members[id.m_SteamID] = member;
                }

                string profile = SteamMatchmaking.GetLobbyMemberData(lobby, id, ProfileKey);
                if (profile != member.RawProfile)
                {
                    member.RawProfile = profile;
                    member.Profile = EmoteProfile.Parse(profile);
                    member.HasSync = EmoteProfile.IsOurs(profile);
                }
                if (!member.HasSync) everyone = false;

                string emote = SteamMatchmaking.GetLobbyMemberData(lobby, id, EmoteKey);
                if (emote != member.RawEmote)
                {
                    member.RawEmote = emote;
                    // Whatever a member had written before this machine first saw them is old news.
                    SentEmote sent = isNew ? null : SentEmote.Parse(emote);
                    if (sent != null)
                    {
                        try
                        {
                            EmoteView.RemoteEmote(id, sent);
                        }
                        catch (Exception error)
                        {
                            EmoteView.LogOnce("receive", error);
                        }
                    }
                }
            }
            EveryoneHasSync = everyone && s_present.Count > 0;

            s_gone.Clear();
            foreach (ulong id in s_members.Keys)
                if (!s_present.Contains(id)) s_gone.Add(id);
            for (int i = 0; i < s_gone.Count; i++) s_members.Remove(s_gone[i]);
        }

        private static bool TryLobby(out CSteamID lobby)
        {
            lobby = CSteamID.Nil;
            if (LobbyField == null || !SteamPlayerManager.Initialized) return false;
            var platform = GameUtils.RequestManagerInterface<IOnlinePlatformManager>();
            if (platform == null) return false;
            var coordinator = platform.OnlineMultiplayerSessionCoordinator() as SteamOnlineMultiplayerSessionCoordinator;
            if (coordinator == null || coordinator.Members() == null) return false;
            lobby = (CSteamID)LobbyField.GetValue(coordinator);
            return lobby.IsValid() && lobby.IsLobby();
        }
    }
}
