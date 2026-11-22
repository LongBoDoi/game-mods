using System;

namespace ChefSkills
{
    /// <summary>
    /// Which language this mod's own text is shown in. The TiengViet mod's option in the game's
    /// Settings (LANGUAGE / NGÔN NGỮ) sets it for the game and every mod in this collection at
    /// once, by putting "vi" or "en" in a slot of the AppDomain that all of them read, so no mod
    /// needs a reference to another. Without TiengViet this mod stays in English.
    /// Read it when text is built (it can change while the game runs); never cache it.
    /// </summary>
    internal static class Lang
    {
        internal const string Slot = "com.manhl.overcooked2.language";
        private const bool Default = false;

        internal static bool Vi
        {
            get
            {
                string value = AppDomain.CurrentDomain.GetData(Slot) as string;
                return value == null ? Default : value == "vi";
            }
        }

        /// <summary>The English or the Vietnamese wording, whichever is in use.</summary>
        internal static string T(string en, string vi) { return Vi ? vi : en; }

        /// <summary>Separates the two wordings in a <see cref="Both"/> text.</summary>
        internal const char Split = '\u001F';

        /// <summary>
        /// Both wordings in one string, for text made on one machine and shown on others (the
        /// host's refusal reasons, floating words and ring labels go over the network): each
        /// player's screen then shows it in that player's own language, via <see cref="Pick"/>.
        /// Build the whole sentence inside each argument: nothing may be appended afterwards.
        /// </summary>
        internal static string Both(string en, string vi) { return en + Split + vi; }

        /// <summary>The wording in use from a <see cref="Both"/> text; any other text unchanged.
        /// Every place that puts mod text on screen passes it through this.</summary>
        internal static string Pick(string text)
        {
            if (text == null) return null;
            int at = text.IndexOf(Split);
            if (at < 0) return text;
            return Vi ? text.Substring(at + 1) : text.Substring(0, at);
        }
    }
}
