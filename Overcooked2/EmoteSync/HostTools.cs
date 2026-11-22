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

        private static string[] s_settingNames;   // HostTools' EmoteConfigTags
        private static string[] s_defaultTexts;   // HostTools' DefaultTexts (what it matches bubbles on)
        private static GameOneShotAudioTag[] s_sounds;
        private static FieldInfo s_modes, s_texts, s_fontSize, s_fontColour;
        private static MethodInfo s_matchesChoice, s_voiceName;

        // Slot in HostTools' arrays for each emote's setting, and for the OK bubble's Chinese one.
        private static readonly int[] s_slots = new int[Emotes.Count];
        private static int s_chineseOKSlot = -1;

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
                FieldInfo names = AccessTools.Field(text, "EmoteConfigTags");
                FieldInfo defaults = AccessTools.Field(text, "DefaultTexts");
                FieldInfo sounds = AccessTools.Field(text, "EmoteTags");
                if (TextPostfix == null || s_modes == null || s_texts == null || s_fontSize == null ||
                    s_fontColour == null || s_matchesChoice == null || s_voiceName == null ||
                    names == null || defaults == null || sounds == null) return false;

                s_settingNames = names.GetValue(null) as string[];
                s_defaultTexts = defaults.GetValue(null) as string[];
                s_sounds = sounds.GetValue(null) as GameOneShotAudioTag[];
                if (s_settingNames == null || s_defaultTexts == null || s_sounds == null ||
                    s_defaultTexts.Length != s_settingNames.Length || s_sounds.Length != s_settingNames.Length)
                    return false;

                for (int e = 0; e < Emotes.Count; e++)
                {
                    s_slots[e] = Array.IndexOf(s_settingNames, Emotes.Settings[e]);
                    if (s_slots[e] < 0)
                    {
                        Plugin.Log.LogWarning("Arcade MOD (HostTools) không có mục " + Emotes.Settings[e] + ".");
                        return false;
                    }
                }
                s_chineseOKSlot = Array.IndexOf(s_settingNames, Emotes.ChineseOKSetting);
                return true;
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("Đọc Arcade MOD (HostTools) thất bại: " + error.Message);
                return false;
            }
        }

        /// <summary>
        /// HostTools' setting slot that applies to an emote on this machine. The OK bubble follows
        /// Emote_Good when this game runs in Chinese (where the bubble reads "好！", which is what
        /// HostTools matches Emote_Good on) and Emote_OK otherwise.
        /// </summary>
        internal static int SlotFor(int emote)
        {
            if (emote == Emotes.OK && s_chineseOKSlot >= 0 && GameIsChinese()) return s_chineseOKSlot;
            return s_slots[emote];
        }

        /// <summary>The emote whose bubble HostTools recognises by this exact text, or -1.</summary>
        internal static int EmoteForDefaultText(string shown)
        {
            int slot = Array.IndexOf(s_defaultTexts, shown);
            if (slot < 0) return Emotes.None;
            if (slot == s_chineseOKSlot) return Emotes.OK;
            return Array.IndexOf(s_slots, slot);
        }

        internal static GameOneShotAudioTag Sound(int emote)
        {
            return s_sounds[s_slots[emote]];
        }

        internal static bool IsEmoteSound(GameOneShotAudioTag tag)
        {
            return Array.IndexOf(s_sounds, tag) >= 0;
        }

        /// <summary>
        /// This machine's emote settings, resolved per game emote, or null when HostTools hasn't
        /// turned its emote text feature on (it leaves it off in score mode).
        /// </summary>
        internal static EmoteProfile ReadLocal()
        {
            var modes = s_modes.GetValue(null) as ConfigEntry<string>[];
            if (modes == null) return null;
            var texts = s_texts.GetValue(null) as ConfigEntry<string>[];
            var size = s_fontSize.GetValue(null) as ConfigEntry<int>;
            var colour = s_fontColour.GetValue(null) as ConfigEntry<Color>;

            var profile = new EmoteProfile();
            if (size != null) profile.FontSize = size.Value;
            if (colour != null) profile.Colour = colour.Value;
            for (int e = 0; e < Emotes.Count; e++)
            {
                int slot = SlotFor(e);
                string mode = slot < modes.Length && modes[slot] != null ? modes[slot].Value : null;
                if (Matches(mode, "Use_Custom_Text")) profile.Modes[e] = Mode.CustomText;
                else if (Matches(mode, "Use_Voice_Pack_File_Name")) profile.Modes[e] = Mode.VoicePackName;
                else profile.Modes[e] = Mode.Keep;
                string custom = texts != null && slot < texts.Length && texts[slot] != null ? texts[slot].Value : null;
                profile.Texts[e] = custom ?? "";
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

        private static bool GameIsChinese()
        {
            try
            {
                SupportedLanguages language = Localization.GetLanguage();
                return language == SupportedLanguages.Chinese || language == SupportedLanguages.ChineseTraditional;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Matches(string value, string choice)
        {
            return (bool)s_matchesChoice.Invoke(null, new object[] { value, choice });
        }
    }
}
