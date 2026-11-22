using System;

namespace EmoteSync
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
    }
}
