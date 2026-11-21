using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using Team17.Online;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using UnityEngine.UI;

namespace EmoteSync
{
    /// <summary>
    /// Draws every emote bubble with the settings of the player who sent it, in place of
    /// HostTools' rewrite, which always used the settings of the machine it ran on.
    ///
    /// A bubble is matched to an emote the way HostTools does it (by the game's own text in it), and
    /// to its sender by the chef or lobby card it hangs over. Bubbles of players on this machine,
    /// and every bubble when playing offline, use this machine's settings. Bubbles of players on
    /// another machine use the settings that machine shares (see <see cref="EmoteNet"/>); if it
    /// shares none (no Emote Sync there), the bubble stays as the game draws it, the same on every
    /// screen. A voice-pack file name is decided by the sender's machine and stays blank on other
    /// screens until its name arrives.
    /// </summary>
    internal static class EmoteView
    {
        // How long a local emote waits for its voice-pack sound to pick a file before using
        // whatever HostTools last picked, and how long other screens wait for that name.
        private const float LocalNameWait = 0.5f;
        private const float RemoteNameWait = 2.5f;
        // A shared name counts for a bubble only if it arrived no earlier than this before it.
        private const float NameFreshness = 3f;
        private const int IdentifyFrames = 10;

        private sealed class Bubble
        {
            public GameObject Dialog;
            public T17Text Text;
            public string Original;
            public int OriginalSize;
            public Color OriginalColour;
            public int Index = -1;
            public int Tries;
            public bool Local;
            public bool OwnerKnown;
            public CSteamID Owner;
            public string Key;
            public float Start;
            public int StartFrame;
            public string Name;   // voice-pack file name: null while not known yet, "" for none
            public bool Sent;
        }

        private struct Pick
        {
            public int Frame;
            public string Name;
        }

        private static readonly FieldInfo DialogField = AccessTools.Field(typeof(ClientEmoteWheel), "m_emoteDialog");
        private static readonly FieldInfo WheelField = AccessTools.Field(typeof(ClientEmoteWheel), "m_emoteWheel");

        private static readonly Dictionary<ClientEmoteWheel, Bubble> s_bubbles = new Dictionary<ClientEmoteWheel, Bubble>();
        private static readonly List<ClientEmoteWheel> s_dead = new List<ClientEmoteWheel>();
        private static readonly Dictionary<string, int> s_usedNames = new Dictionary<string, int>();
        private static readonly Dictionary<GameOneShotAudioTag, Pick> s_picks = new Dictionary<GameOneShotAudioTag, Pick>();
        private static readonly HashSet<string> s_logged = new HashSet<string>();

        internal static void LogOnce(string where, Exception error)
        {
            if (s_logged.Add(where)) Plugin.Log.LogWarning("Emote Sync (" + where + "): " + error);
        }

        /// <summary>A one-shot sound was looked up; remember the file HostTools picked for emote sounds.</summary>
        internal static void SoundPicked(GameOneShotAudioTag tag)
        {
            if (!HostTools.IsEmoteTag(tag)) return;
            s_picks[tag] = new Pick { Frame = Time.frameCount, Name = HostTools.VoiceName(tag) };
        }

        internal static void Update(ClientEmoteWheel wheel)
        {
            GameObject dialog = DialogField.GetValue(wheel) as GameObject;
            if (dialog == null)
            {
                s_bubbles.Remove(wheel);
                return;
            }

            Bubble bubble;
            if (!s_bubbles.TryGetValue(wheel, out bubble) || bubble.Dialog != dialog)
            {
                bubble = Begin(wheel, dialog);
                s_bubbles[wheel] = bubble;
                Prune();
            }
            Draw(bubble);
        }

        private static Bubble Begin(ClientEmoteWheel wheel, GameObject dialog)
        {
            var bubble = new Bubble
            {
                Dialog = dialog,
                Start = Time.realtimeSinceStartup,
                StartFrame = Time.frameCount
            };
            Transform child = dialog.transform.Find("Text");
            bubble.Text = child != null ? child.GetComponent<T17Text>() : null;
            if (bubble.Text == null) return bubble;

            bubble.Original = bubble.Text.text;
            bubble.OriginalSize = bubble.Text.fontSize;
            bubble.OriginalColour = bubble.Text.color;
            bubble.Index = HostTools.IndexOf(bubble.Original);

            EmoteWheel emoteWheel = WheelField.GetValue(wheel) as EmoteWheel;
            bubble.Key = KeyOf(wheel, emoteWheel);
            bubble.Local = !EmoteNet.Online || IsLocal(emoteWheel);
            if (!bubble.Local)
            {
                Team17.Online.User user = OwnerOf(wheel, emoteWheel);
                if (user != null && user.IsLocal) bubble.Local = true;
                else bubble.OwnerKnown = TrySteamId(user, out bubble.Owner);
            }
            return bubble;
        }

        private static void Draw(Bubble bubble)
        {
            T17Text text = bubble.Text;
            if (text == null) return;

            EmoteProfile profile = bubble.Local ? EmoteNet.Local
                : bubble.OwnerKnown ? EmoteNet.ProfileOf(bubble.Owner) : null;
            if (profile == null)
            {
                // The sender has no Emote Sync (or HostTools' emote text is off for them): the
                // game's own bubble, which is also what their own screen shows for it.
                Style(text, bubble.OriginalSize, bubble.OriginalColour);
                if (bubble.Index >= 0) Show(text, bubble.Original);
                return;
            }

            Style(text, profile.FontSize, profile.Colour);

            if (bubble.Index < 0)
            {
                // The game may localise the text a frame or two after the bubble appears.
                if (bubble.Tries++ >= IdentifyFrames) return;
                bubble.Index = HostTools.IndexOf(text.text);
                if (bubble.Index < 0) return;
                bubble.Original = text.text;
            }

            string shown = Resolve(bubble, profile);
            Show(text, shown ?? bubble.Original);
        }

        /// <summary>The text the sender's settings give this bubble, or null for the game's own.</summary>
        private static string Resolve(Bubble bubble, EmoteProfile profile)
        {
            int i = bubble.Index;
            if (i >= profile.Modes.Length) return null;
            switch (profile.Modes[i])
            {
                case HostTools.Mode.CustomText:
                    return string.IsNullOrEmpty(profile.Texts[i]) ? null : profile.Texts[i];
                case HostTools.Mode.VoicePackName:
                    string name = bubble.Local ? LocalName(bubble) : RemoteName(bubble);
                    if (name == null) return "";   // still waiting for the sender's pick
                    return name.Length > 0 ? name : null;
                default:
                    return null;
            }
        }

        private static string LocalName(Bubble bubble)
        {
            if (bubble.Name == null)
            {
                GameOneShotAudioTag tag = HostTools.Tag(bubble.Index);
                Pick pick;
                if (s_picks.TryGetValue(tag, out pick) && pick.Frame >= bubble.StartFrame - 2)
                    bubble.Name = pick.Name ?? "";
                else if (Time.realtimeSinceStartup - bubble.Start > LocalNameWait)
                    bubble.Name = HostTools.VoiceName(tag);
            }
            if (bubble.Name != null && !bubble.Sent)
            {
                bubble.Sent = true;
                if (!string.IsNullOrEmpty(bubble.Key)) EmoteNet.Publish(bubble.Key, bubble.Index, bubble.Name);
            }
            return bubble.Name;
        }

        private static string RemoteName(Bubble bubble)
        {
            if (bubble.Name != null) return bubble.Name;

            SentEmote sent = EmoteNet.EmoteOf(bubble.Owner);
            string usedKey = bubble.Owner.m_SteamID + "|" + bubble.Key;
            int used;
            if (sent != null && sent.Key == bubble.Key && sent.Index == bubble.Index &&
                sent.SeenAt >= bubble.Start - NameFreshness &&
                !(s_usedNames.TryGetValue(usedKey, out used) && used == sent.Seq))
            {
                s_usedNames[usedKey] = sent.Seq;
                bubble.Name = sent.Text ?? "";
            }
            else if (Time.realtimeSinceStartup - bubble.Start > RemoteNameWait)
            {
                bubble.Name = "";
            }
            return bubble.Name;
        }

        private static void Show(T17Text text, string value)
        {
            if (value != null && text.text != value) text.SetNonLocalizedText(value);
        }

        private static void Style(T17Text text, int size, Color colour)
        {
            if (text.fontSize != size) text.fontSize = size;
            if (text.color != colour) text.color = colour;
        }

        /// <summary>
        /// Names a bubble the same way on every machine: a lobby card by its player slot (the
        /// session's user list is the same everywhere), a chef by its network entity ID.
        /// </summary>
        private static string KeyOf(ClientEmoteWheel wheel, EmoteWheel emoteWheel)
        {
            if (emoteWheel == null) return null;
            if (emoteWheel.ForUI) return "u" + (int)emoteWheel.m_player;
            uint id = EntityOf(wheel.transform);
            return id != 0 ? "e" + id : null;
        }

        private static bool IsLocal(EmoteWheel emoteWheel)
        {
            try
            {
                return emoteWheel != null && emoteWheel.IsLocal;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Team17.Online.User OwnerOf(ClientEmoteWheel wheel, EmoteWheel emoteWheel)
        {
            FastList<Team17.Online.User> users = ClientUserSystem.m_Users;
            if (users == null || emoteWheel == null) return null;
            if (emoteWheel.ForUI)
            {
                int slot = (int)emoteWheel.m_player;
                return slot >= 0 && slot < users.Count ? users._items[slot] : null;
            }
            uint id = EntityOf(wheel.transform);
            if (id == 0) return null;
            for (int i = 0; i < users.Count; i++)
            {
                Team17.Online.User user = users._items[i];
                if (user != null && (user.EntityID == id || user.Entity2ID == id)) return user;
            }
            return null;
        }

        private static uint EntityOf(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                uint id = EntitySerialisationRegistry.GetId(t.gameObject);
                if (id != EntitySerialisationRegistry.kInvalidEntityID) return id;
            }
            return 0;
        }

        private static bool TrySteamId(Team17.Online.User user, out CSteamID id)
        {
            id = CSteamID.Nil;
            if (user == null) return false;
            var steam = user.PlatformID as SteamOnlineUserPlatformId;
            if (steam == null || !steam.m_steamId.IsValid()) return false;
            id = steam.m_steamId;
            return true;
        }

        private static void Prune()
        {
            if (s_bubbles.Count < 8) return;
            s_dead.Clear();
            foreach (KeyValuePair<ClientEmoteWheel, Bubble> entry in s_bubbles)
                if (entry.Key == null || entry.Value.Dialog == null) s_dead.Add(entry.Key);
            for (int i = 0; i < s_dead.Count; i++) s_bubbles.Remove(s_dead[i]);
        }
    }
}
