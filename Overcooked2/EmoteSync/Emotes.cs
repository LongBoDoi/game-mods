using System;

namespace EmoteSync
{
    /// <summary>
    /// The game's own emote bubbles, named by the localisation key on their text (the same on
    /// every machine, whatever language each player runs the game in).
    /// </summary>
    internal static class Emotes
    {
        internal const int Count = 10;
        internal const int None = -1;
        internal const int OK = 4;

        /// <summary>Localisation key of each emote's bubble text.</summary>
        internal static readonly string[] Keys =
        {
            "Text.Emote.Cooking", "Text.Emote.Serving", "Text.Emote.Washup", "Text.Emote.Preparing",
            "Text.Emote.OK", "Text.Emote.Celebrate", "Text.Emote.Swearing", "Text.Emote.Disappointed",
            "Text.Emote.Hi", "Text.Emote.GreatJob"
        };

        /// <summary>
        /// HostTools' setting for each emote. The OK bubble has two: HostTools tells emotes apart by
        /// the text in the bubble, and the OK bubble reads "好！" in Chinese (its Emote_Good) and
        /// "OK!" in most other languages (its Emote_OK). See <see cref="HostTools.SlotFor"/>.
        /// </summary>
        internal static readonly string[] Settings =
        {
            "Emote_Cooking", "Emote_Serving", "Emote_Cleaning", "Emote_Preparing",
            "Emote_OK", "Emote_We_Did_It", "Emote_Cursing", "Emote_Better_Luck_Next_Time",
            "Emote_Hello", "Emote_Well_Played"
        };

        internal const string ChineseOKSetting = "Emote_Good";

        internal static int FromKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return None;
            for (int i = 0; i < Keys.Length; i++)
                if (string.Equals(Keys[i], key, StringComparison.Ordinal)) return i;
            return None;
        }

        /// <summary>The bubble text this machine's game shows for an emote, in its own language.</summary>
        internal static string GameText(int emote)
        {
            if (emote < 0 || emote >= Count) return null;
            try
            {
                string text;
                return Localization.Get(Keys[emote], out text) && !string.IsNullOrEmpty(text) ? text : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Recognises a bubble whose localisation key is already gone, by its text: the game's own
        /// text in this machine's language, or the text HostTools itself matches on.
        /// </summary>
        internal static int FromText(string shown)
        {
            if (string.IsNullOrEmpty(shown)) return None;
            for (int i = 0; i < Count; i++)
                if (string.Equals(GameText(i), shown, StringComparison.Ordinal)) return i;
            return HostTools.EmoteForDefaultText(shown);
        }
    }
}
