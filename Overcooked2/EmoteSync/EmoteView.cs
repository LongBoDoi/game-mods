using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using Team17.Online;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;

namespace EmoteSync
{
    /// <summary>
    /// Draws every emote bubble with the settings of the player who sent it, in place of HostTools'
    /// rewrite (which used the watching machine's settings, and recognised emotes by their text, so
    /// it missed every bubble whose text wasn't in the one language it expected).
    ///
    /// A bubble is recognised by the localisation key on its text, which is the same in every
    /// language. Bubbles of this machine's players, and every bubble offline, use this machine's
    /// settings. When one of this machine's players emotes, Emote Sync tells the other machines
    /// which lobby card or chef it was, which emote, and exactly what its bubble shows (see
    /// <see cref="SentEmote"/>); they show that same bubble over that same card or chef. A bubble
    /// that the game delivers before that news arrives uses the sender's shared settings in the
    /// meantime, matched to the sender by the card or chef it hangs over.
    ///
    /// The game's own emote message names a player by their number on their own machine, which
    /// can't tell apart chefs from different machines in a kitchen. So when everyone in the lobby
    /// has Emote Sync, in-kitchen emotes from other machines are shown only from Emote Sync's news.
    /// </summary>
    internal static class EmoteView
    {
        // How long a local emote waits for its voice-pack sound to pick a file before using
        // whatever HostTools last picked, and how long other screens wait for the sender's news.
        private const float LocalNameWait = 0.5f;
        private const float RemoteNameWait = 2.5f;
        // The game's copy of an emote Emote Sync already showed (or the other way round) arrives
        // within this long, and is merged into the same bubble instead of starting a second one.
        private const float SameEmoteWindow = 2f;
        private const float NewsWindow = 3f;

        private sealed class Wheel
        {
            public int Option = -1;
            public float StartedAt = float.NegativeInfinity;
            public bool StartedByNews;
            public Bubble Bubble;
            // Latest news from another machine about this wheel.
            public CSteamID NewsFrom;
            public SentEmote News;
            public float NewsAt = float.NegativeInfinity;
            // A local voice-pack emote, sent once its bubble knows the file name.
            public SentEmote Unsent;
        }

        private sealed class Bubble
        {
            public Wheel Wheel;
            public GameObject Dialog;
            public T17Text Text;
            public int Emote = Emotes.None;
            public string Original;
            public int OriginalSize;
            public Color OriginalColour;
            public bool Touched;
            public bool Local;
            public bool OwnerKnown;
            public CSteamID Owner;
            public SentEmote News;
            public float Start;
            public int StartFrame;
            public string Name;   // local voice-pack file name: null while not known yet
        }

        private struct Pick
        {
            public int Frame;
            public string Name;
        }

        private static readonly FieldInfo DialogField = AccessTools.Field(typeof(ClientEmoteWheel), "m_emoteDialog");
        private static readonly FieldInfo WheelField = AccessTools.Field(typeof(ClientEmoteWheel), "m_emoteWheel");

        private static readonly Dictionary<ClientEmoteWheel, Wheel> s_wheels = new Dictionary<ClientEmoteWheel, Wheel>();
        private static readonly List<ClientEmoteWheel> s_dead = new List<ClientEmoteWheel>();
        private static readonly Dictionary<EmoteWheelOption, int> s_optionEmotes = new Dictionary<EmoteWheelOption, int>();
        private static readonly Dictionary<GameOneShotAudioTag, Pick> s_picks = new Dictionary<GameOneShotAudioTag, Pick>();
        private static readonly HashSet<string> s_logged = new HashSet<string>();
        private static bool s_startingFromNews;
        private static float s_nextPrune;

        internal static void LogOnce(string where, Exception error)
        {
            if (s_logged.Add(where)) Plugin.Log.LogWarning("Emote Sync (" + where + "): " + error);
        }

        // ---- Game hooks ---------------------------------------------------------------------

        /// <summary>A one-shot sound was looked up; remember the file HostTools picked for emote sounds.</summary>
        internal static void SoundPicked(GameOneShotAudioTag tag)
        {
            if (!HostTools.IsEmoteSound(tag)) return;
            s_picks[tag] = new Pick { Frame = Time.frameCount, Name = HostTools.VoiceName(tag) };
        }

        /// <summary>
        /// ClientEmoteWheel.StartEmote is about to show an emote. Returns false to drop the game's
        /// copy of an emote that Emote Sync's news already showed on this wheel.
        /// </summary>
        internal static bool BeforeStart(ClientEmoteWheel wheel, int option)
        {
            Wheel state = StateOf(wheel);
            float now = Time.realtimeSinceStartup;
            if (!s_startingFromNews && state.StartedByNews && state.Option == option &&
                now - state.StartedAt < SameEmoteWindow)
            {
                state.StartedByNews = false;   // only the one copy
                return false;
            }
            state.Option = option;
            state.StartedAt = now;
            state.StartedByNews = s_startingFromNews;
            return true;
        }

        /// <summary>The game's emote message for another machine's player: false to ignore it.</summary>
        internal static bool AcceptGameMessage(ClientEmoteWheel wheel)
        {
            EmoteWheel emoteWheel = WheelOf(wheel);
            return emoteWheel == null || emoteWheel.ForUI || !EmoteNet.EveryoneHasSync;
        }

        /// <summary>One of this machine's players picked an emote on the wheel.</summary>
        internal static void LocalEmote(ClientEmoteWheel wheel, int option)
        {
            if (!EmoteNet.Online) return;
            EmoteWheel emoteWheel = WheelOf(wheel);
            int emote = EmoteOfOption(emoteWheel, option);
            string key = KeyOf(wheel, emoteWheel);
            if (emote == Emotes.None || key == null) return;   // no bubble (the in-kitchen curse)

            EmoteProfile profile = EmoteNet.Local;
            var sent = new SentEmote { Wheel = key, Option = option, Emote = emote, Mode = HostTools.Mode.Keep };
            if (profile != null)
            {
                sent.Mode = profile.Modes[emote];
                if (sent.Mode == HostTools.Mode.CustomText) sent.Text = profile.Texts[emote];
            }
            if (sent.Mode == HostTools.Mode.VoicePackName)
                StateOf(wheel).Unsent = sent;   // sent by LocalName once the sound has picked a file
            else
                EmoteNet.Publish(sent);
        }

        /// <summary>Another machine's player started an emote.</summary>
        internal static void RemoteEmote(CSteamID from, SentEmote sent)
        {
            ClientEmoteWheel wheel = FindWheel(sent.Wheel);
            if (wheel == null) return;
            EmoteWheel emoteWheel = WheelOf(wheel);
            if (emoteWheel == null || IsLocal(emoteWheel)) return;
            EmoteWheelOption[] options = emoteWheel.m_emoteWheelOptions != null ? emoteWheel.m_emoteWheelOptions.m_options : null;
            if (options == null || sent.Option >= options.Length) return;
            int emote = EmoteOfOption(emoteWheel, sent.Option);
            if (emote != Emotes.None && emote != sent.Emote) return;   // not the wheel the sender meant

            Wheel state = StateOf(wheel);
            float now = Time.realtimeSinceStartup;
            state.NewsFrom = from;
            state.News = sent;
            state.NewsAt = now;

            if (state.Option == sent.Option && now - state.StartedAt < SameEmoteWindow)
            {
                // The game already started it: that bubble is this emote (if it hasn't been drawn
                // yet, Begin picks this news up from the wheel).
                Bubble bubble = state.Bubble;
                if (bubble != null && bubble.Dialog != null) Attach(bubble, from, sent);
                return;
            }

            s_startingFromNews = true;
            try
            {
                wheel.StartEmote(sent.Option);
            }
            finally
            {
                s_startingFromNews = false;
            }
        }

        /// <summary>After ClientEmoteWheel.UpdateEmoteWheel: restyle the wheel's bubble, if any.</summary>
        internal static void Update(ClientEmoteWheel wheel)
        {
            Wheel state = StateOf(wheel);
            GameObject dialog = DialogField.GetValue(wheel) as GameObject;
            if (dialog == null)
            {
                state.Bubble = null;
                return;
            }
            if (state.Bubble == null || state.Bubble.Dialog != dialog)
                state.Bubble = Begin(wheel, state, dialog);
            Draw(state.Bubble);
        }

        // ---- Bubbles ------------------------------------------------------------------------

        private static Bubble Begin(ClientEmoteWheel wheel, Wheel state, GameObject dialog)
        {
            var bubble = new Bubble
            {
                Wheel = state,
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
            bubble.Emote = Emotes.FromKey(bubble.Text.m_LocalizationTag);
            if (bubble.Emote == Emotes.None) bubble.Emote = Emotes.FromText(bubble.Original);

            EmoteWheel emoteWheel = WheelOf(wheel);
            bubble.Local = !EmoteNet.Online || IsLocal(emoteWheel);
            if (bubble.Local) return bubble;

            if (state.News != null && state.News.Option == state.Option &&
                Time.realtimeSinceStartup - state.NewsAt < NewsWindow)
            {
                Attach(bubble, state.NewsFrom, state.News);
                return bubble;
            }
            Team17.Online.User user = OwnerOf(wheel, emoteWheel);
            if (user != null && user.IsLocal) bubble.Local = true;
            else bubble.OwnerKnown = TrySteamId(user, out bubble.Owner);
            return bubble;
        }

        private static void Attach(Bubble bubble, CSteamID from, SentEmote sent)
        {
            bubble.Local = false;
            bubble.Owner = from;
            bubble.OwnerKnown = true;
            bubble.News = sent;
        }

        private static void Draw(Bubble bubble)
        {
            T17Text text = bubble.Text;
            if (text == null || bubble.Emote == Emotes.None) return;
            int e = bubble.Emote;

            if (bubble.Local)
            {
                EmoteProfile local = EmoteNet.Local;
                if (local == null)
                {
                    ShowGame(bubble);   // HostTools' emote text is off: the game's own bubble
                    return;
                }
                Style(text, local.FontSize, local.Colour);
                switch (local.Modes[e])
                {
                    case HostTools.Mode.CustomText:
                        ShowOrGame(bubble, local.Texts[e]);
                        break;
                    case HostTools.Mode.VoicePackName:
                        string name = LocalName(bubble);
                        if (name == null) Show(bubble, "");
                        else ShowOrGame(bubble, name);
                        break;
                    default:
                        ShowGame(bubble);
                        break;
                }
                return;
            }

            EmoteProfile profile = bubble.OwnerKnown ? EmoteNet.ProfileOf(bubble.Owner) : null;
            if (profile != null) Style(text, profile.FontSize, profile.Colour);
            else Style(text, bubble.OriginalSize, bubble.OriginalColour);

            if (bubble.News != null)
            {
                // Exactly what the sender's own screen shows.
                if (bubble.News.Mode == HostTools.Mode.Keep) ShowGame(bubble);
                else ShowOrGame(bubble, bubble.News.Text);
                return;
            }
            if (profile == null)
            {
                // The sender has no Emote Sync (or HostTools' emote text is off there).
                ShowGame(bubble);
                return;
            }
            switch (profile.Modes[e])
            {
                case HostTools.Mode.CustomText:
                    ShowOrGame(bubble, profile.Texts[e]);
                    break;
                case HostTools.Mode.VoicePackName:
                    // The file name is picked on the sender's machine; wait for their news.
                    if (Time.realtimeSinceStartup - bubble.Start < RemoteNameWait) Show(bubble, "");
                    else ShowGame(bubble);
                    break;
                default:
                    ShowGame(bubble);
                    break;
            }
        }

        private static string LocalName(Bubble bubble)
        {
            if (bubble.Name == null)
            {
                GameOneShotAudioTag tag = HostTools.Sound(bubble.Emote);
                Pick pick;
                if (s_picks.TryGetValue(tag, out pick) && pick.Frame >= bubble.StartFrame - 2)
                    bubble.Name = pick.Name ?? "";
                else if (Time.realtimeSinceStartup - bubble.Start > LocalNameWait)
                    bubble.Name = HostTools.VoiceName(tag);
            }
            SentEmote unsent = bubble.Wheel.Unsent;
            if (bubble.Name != null && unsent != null)
            {
                bubble.Wheel.Unsent = null;
                if (unsent.Emote == bubble.Emote)
                {
                    unsent.Text = bubble.Name;
                    EmoteNet.Publish(unsent);
                }
            }
            return bubble.Name;
        }

        private static void ShowOrGame(Bubble bubble, string value)
        {
            if (string.IsNullOrEmpty(value)) ShowGame(bubble);
            else Show(bubble, value);
        }

        /// <summary>The game's own text, in this machine's language (left alone if never changed).</summary>
        private static void ShowGame(Bubble bubble)
        {
            if (!bubble.Touched) return;
            Show(bubble, Emotes.GameText(bubble.Emote) ?? bubble.Original);
        }

        private static void Show(Bubble bubble, string value)
        {
            if (value == null) return;
            bubble.Touched = true;
            if (bubble.Text.text != value) bubble.Text.SetNonLocalizedText(value);
        }

        private static void Style(T17Text text, int size, Color colour)
        {
            if (text.fontSize != size) text.fontSize = size;
            if (text.color != colour) text.color = colour;
        }

        // ---- Wheels -------------------------------------------------------------------------

        private static Wheel StateOf(ClientEmoteWheel wheel)
        {
            Wheel state;
            if (!s_wheels.TryGetValue(wheel, out state))
            {
                state = new Wheel();
                s_wheels[wheel] = state;
                Prune();
            }
            return state;
        }

        private static EmoteWheel WheelOf(ClientEmoteWheel wheel)
        {
            return wheel != null ? WheelField.GetValue(wheel) as EmoteWheel : null;
        }

        /// <summary>The emote an option on a wheel shows, from the key on its bubble prefab's text.</summary>
        private static int EmoteOfOption(EmoteWheel emoteWheel, int option)
        {
            if (emoteWheel == null || emoteWheel.m_emoteWheelOptions == null) return Emotes.None;
            EmoteWheelOption[] options = emoteWheel.m_emoteWheelOptions.m_options;
            if (options == null || option < 0 || option >= options.Length || options[option] == null) return Emotes.None;
            EmoteWheelOption o = options[option];
            int emote;
            if (s_optionEmotes.TryGetValue(o, out emote)) return emote;
            emote = Emotes.None;
            if (o.m_dialogPrefab != null && (o.m_type == EmoteWheelOption.EmoteType.Dialog || o.m_type == EmoteWheelOption.EmoteType.Both))
            {
                Transform child = o.m_dialogPrefab.transform.Find("Text");
                T17Text text = child != null ? child.GetComponent<T17Text>() : null;
                if (text != null)
                {
                    emote = Emotes.FromKey(text.m_LocalizationTag);
                    if (emote == Emotes.None) emote = Emotes.FromText(text.text);
                }
            }
            s_optionEmotes[o] = emote;
            return emote;
        }

        /// <summary>
        /// Names a wheel the same way on every machine: a lobby card by its player slot (the
        /// session's user list is in the same order everywhere), a chef by its network entity ID.
        /// </summary>
        private static string KeyOf(ClientEmoteWheel wheel, EmoteWheel emoteWheel)
        {
            if (emoteWheel == null) return null;
            if (emoteWheel.ForUI)
            {
                int slot = (int)emoteWheel.m_player;
                return slot >= 0 && slot < (int)PlayerInputLookup.Player.Count ? "u" + slot : null;
            }
            uint id = EntityOf(wheel.transform);
            return id != EntitySerialisationRegistry.kInvalidEntityID ? "e" + id : null;
        }

        private static ClientEmoteWheel FindWheel(string key)
        {
            foreach (KeyValuePair<ClientEmoteWheel, Wheel> entry in s_wheels)
                if (entry.Key != null && KeyOf(entry.Key, WheelOf(entry.Key)) == key) return entry.Key;
            ClientEmoteWheel[] all = UnityEngine.Object.FindObjectsOfType<ClientEmoteWheel>();
            for (int i = 0; i < all.Length; i++)
                if (KeyOf(all[i], WheelOf(all[i])) == key) return all[i];
            return null;
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
            if (id == EntitySerialisationRegistry.kInvalidEntityID) return null;
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
            return EntitySerialisationRegistry.kInvalidEntityID;
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
            float now = Time.realtimeSinceStartup;
            if (now < s_nextPrune) return;
            s_nextPrune = now + 5f;
            s_dead.Clear();
            foreach (KeyValuePair<ClientEmoteWheel, Wheel> entry in s_wheels)
                if (entry.Key == null) s_dead.Add(entry.Key);
            for (int i = 0; i < s_dead.Count; i++) s_wheels.Remove(s_dead[i]);
        }
    }
}
