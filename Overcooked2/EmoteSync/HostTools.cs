using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EmoteSync
{
    /// <summary>
    /// Reads the emote settings of Arcade MOD (HostTools). Its classes are internal and the DLL
    /// updates itself, so everything is looked up by name once at start and checked before use.
    /// </summary>
    internal static class HostTools
    {
        internal const string Guid = "com.ch3ngyz.plugin.HostUtilities";

        internal enum Mode { Keep = 0, CustomText = 1, VoicePackName = 2 }

        /// <summary>HostTools' UpdateEmoteWheel postfix, which rewrites bubbles with local settings.</summary>
        internal static MethodInfo TextPostfix;

        /// <summary>The game's own bubble text for each emote HostTools knows, by HostTools' index.</summary>
        internal static string[] DefaultTexts;

        private static GameOneShotAudioTag[] s_tags;
        private static FieldInfo s_modes, s_texts, s_fontSize, s_fontColour;
        private static MethodInfo s_matchesChoice, s_voiceName;

        internal static int Count { get { return DefaultTexts.Length; } }

        internal static bool Init()
        {
            try
            {
                Type text = AccessTools.TypeByName("HostUtilities.CustomizationEmoteText");
                Type audio = AccessTools.TypeByName("HostUtilities.CustomizationAudioPack");
                Type language = AccessTools.TypeByName("HostUtilities.Language");
                if (text == null || audio == null || language == null) return false;

                TextPostfix = AccessTools.Method(text, "ClientEmoteWheel_StartEmote_Postfix");
                s_modes = AccessTools.Field(text, "emoteConfigs");
                s_texts = AccessTools.Field(text, "emoteCustomTexts");
                s_fontSize = AccessTools.Field(text, "defaultEmoteFontSize");
                s_fontColour = AccessTools.Field(text, "defaultEmoteFontColor");
                s_matchesChoice = AccessTools.Method(language, "MatchesChoice", new[] { typeof(string), typeof(string) });
                s_voiceName = AccessTools.Method(audio, "TryGetEmoteFileName");
                FieldInfo defaults = AccessTools.Field(text, "DefaultTexts");
                FieldInfo tags = AccessTools.Field(text, "EmoteTags");
                if (TextPostfix == null || s_modes == null || s_texts == null || s_fontSize == null ||
                    s_fontColour == null || s_matchesChoice == null || s_voiceName == null ||
                    defaults == null || tags == null) return false;

                DefaultTexts = defaults.GetValue(null) as string[];
                s_tags = tags.GetValue(null) as GameOneShotAudioTag[];
                return DefaultTexts != null && s_tags != null && s_tags.Length == DefaultTexts.Length;
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("Đọc Arcade MOD (HostTools) thất bại: " + error.Message);
                return false;
            }
        }

        /// <summary>HostTools' index for a bubble showing the game's own text, or -1.</summary>
        internal static int IndexOf(string shown)
        {
            if (string.IsNullOrEmpty(shown)) return -1;
            for (int i = 0; i < DefaultTexts.Length; i++)
                if (string.Equals(DefaultTexts[i], shown, StringComparison.Ordinal)) return i;
            return -1;
        }

        internal static GameOneShotAudioTag Tag(int index)
        {
            return s_tags[index];
        }

        internal static bool IsEmoteTag(GameOneShotAudioTag tag)
        {
            for (int i = 0; i < s_tags.Length; i++)
                if (s_tags[i] == tag) return true;
            return false;
        }

        /// <summary>
        /// This machine's emote settings, as HostTools has them right now, or null when HostTools
        /// hasn't turned its emote text feature on (it skips it in score mode).
        /// </summary>
        internal static EmoteProfile ReadLocal()
        {
            var modes = s_modes.GetValue(null) as ConfigEntry<string>[];
            if (modes == null) return null;
            var profile = new EmoteProfile(Count);
            var texts = s_texts.GetValue(null) as ConfigEntry<string>[];
            var size = s_fontSize.GetValue(null) as ConfigEntry<int>;
            var colour = s_fontColour.GetValue(null) as ConfigEntry<Color>;
            if (size != null) profile.FontSize = size.Value;
            if (colour != null) profile.Colour = colour.Value;
            for (int i = 0; i < Count; i++)
            {
                string mode = modes != null && i < modes.Length && modes[i] != null ? modes[i].Value : null;
                if (Matches(mode, "Use_Custom_Text")) profile.Modes[i] = Mode.CustomText;
                else if (Matches(mode, "Use_Voice_Pack_File_Name")) profile.Modes[i] = Mode.VoicePackName;
                else profile.Modes[i] = Mode.Keep;
                string custom = texts != null && i < texts.Length && texts[i] != null ? texts[i].Value : null;
                profile.Texts[i] = custom ?? "";
            }
            return profile;
        }

        /// <summary>The voice-pack file name HostTools last picked for this sound ("" when none).</summary>
        internal static string VoiceName(GameOneShotAudioTag tag)
        {
            object[] args = { tag, null };
            bool found = (bool)s_voiceName.Invoke(null, args);
            string name = args[1] as string;
            return found && !string.IsNullOrEmpty(name) ? name : "";
        }

        private static bool Matches(string value, string choice)
        {
            return (bool)s_matchesChoice.Invoke(null, new object[] { value, choice });
        }
    }
}
