using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using Team17.Online;
using UnityEngine;

namespace EmoteSync
{
    /// <summary>A voice-pack emote another machine showed: which bubble, and the file name it picked.</summary>
    internal sealed class SentEmote
    {
        public int Seq;
        public string Key;
        public int Index;
        public string Text;
        public float SeenAt;

        // "1|seq|key|index|base64"
        public string Format()
        {
            return "1|" + Seq.ToString(CultureInfo.InvariantCulture) + "|" + Key + "|" +
                Index.ToString(CultureInfo.InvariantCulture) + "|" + EmoteProfile.Encode(Text);
        }

        public static SentEmote Parse(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string[] parts = value.Split('|');
            if (parts.Length < 5 || parts[0] != "1") return null;
            var sent = new SentEmote { Key = parts[2], Text = EmoteProfile.Decode(parts[4]) };
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sent.Seq) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out sent.Index))
                return null;
            return sent;
        }
    }

    /// <summary>
    /// Shares emote settings between the machines in an online game through Steam lobby member
    /// data, the per-member key/value store Steam keeps for each lobby and sends to everyone in it
    /// (the game itself never uses it). Each machine writes two keys of its own:
    ///   emotesync.profile  its HostTools emote settings (see <see cref="EmoteProfile"/>), rewritten
    ///                      whenever they change;
    ///   emotesync.emote    the last voice-pack emote it showed, with the file name it picked.
    /// A machine that writes no profile has no Emote Sync, and its chefs' bubbles are left as the
    /// game draws them.
    /// </summary>
    internal static class EmoteNet
    {
        private const string ProfileKey = "emotesync.profile";
        private const string EmoteKey = "emotesync.emote";
        private const float LocalRefreshSeconds = 0.25f;
        private const float PublishSeconds = 0.5f;
        private const float PollSeconds = 0.1f;

        private sealed class Member
        {
            public string RawProfile;
            public EmoteProfile Profile;
            public string RawEmote;
            public SentEmote Emote;
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
        private static float s_nextLocal, s_nextPublish, s_nextPoll;
        private static bool s_pollNow;
        private static int s_seq = Environment.TickCount & 0xFFFFF;

        /// <summary>In an online Steam lobby with Emote Sync running.</summary>
        internal static bool Online { get { return s_lobby.IsValid(); } }

        /// <summary>This machine's settings (null when HostTools' emote text feature is off).</summary>
        internal static EmoteProfile Local
        {
            get
            {
                float now = Time.realtimeSinceStartup;
                if (s_local == null || now >= s_nextLocal)
                {
                    s_local = HostTools.ReadLocal();
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

        internal static SentEmote EmoteOf(CSteamID member)
        {
            s_pollNow = true;
            Member m;
            return s_members.TryGetValue(member.m_SteamID, out m) ? m.Emote : null;
        }

        /// <summary>Tells the other machines which voice-pack file name a local emote shows.</summary>
        internal static void Publish(string key, int index, string text)
        {
            if (!Online) return;
            s_seq = (s_seq + 1) & 0x7FFFFFFF;
            var sent = new SentEmote { Seq = s_seq, Key = key, Index = index, Text = text ?? "" };
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
                EmoteProfile local = enabled ? Local : null;
                string value = local != null ? local.Format() : "";
                if (value != s_writtenProfile)
                {
                    SteamMatchmaking.SetLobbyMemberData(lobby, ProfileKey, value);
                    s_writtenProfile = value;
                }
            }

            if (enabled && (s_pollNow || now >= s_nextPoll))
            {
                s_pollNow = false;
                s_nextPoll = now + PollSeconds;
                Poll(lobby, now);
            }
        }

        private static void Reset(CSteamID lobby)
        {
            s_lobby = lobby;
            s_members.Clear();
            s_writtenProfile = null;
            s_nextPublish = 0f;
            s_nextPoll = 0f;
            s_self = lobby.IsValid() ? SteamUser.GetSteamID() : CSteamID.Nil;
        }

        private static void Poll(CSteamID lobby, float now)
        {
            s_present.Clear();
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
                }

                string emote = SteamMatchmaking.GetLobbyMemberData(lobby, id, EmoteKey);
                if (emote != member.RawEmote)
                {
                    member.RawEmote = emote;
                    member.Emote = SentEmote.Parse(emote);
                    // Whatever a member had written before this machine first saw them is old news.
                    if (member.Emote != null) member.Emote.SeenAt = isNew ? float.NegativeInfinity : now;
                }
            }

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
