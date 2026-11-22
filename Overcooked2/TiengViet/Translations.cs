using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace TiengViet
{
    /// <summary>
    /// The Vietnamese table, built into the DLL from vi.tsv. One line per game text:
    /// <c>Tag TAB English TAB Vietnamese</c>; the English column is only there for whoever edits
    /// the file (and to spot texts a game update rewrote). Lines starting with # are comments.
    /// A <c>\n</c> in the text is a line break, as in the game's own tables, and button markers
    /// such as [A] are turned into the game's icons by the game's own markup pass.
    /// </summary>
    internal static class Translations
    {
        private static readonly Dictionary<string, string> table = new Dictionary<string, string>();

        /// <summary>Texts of this mod's own (the language row), in both languages.</summary>
        internal const string LanguageTitleTag = "TiengViet.Option.Language";

        internal static int Count { get { return table.Count; } }

        internal static void Load()
        {
            table.Clear();
            Stream stream = typeof(Translations).Assembly.GetManifestResourceStream("TiengViet.vi.tsv");
            if (stream == null)
            {
                Plugin.Log.LogError("vi.tsv is missing from TiengViet.dll; the game will stay in its own language.");
                return;
            }
            // The game turns [A], [B], [X], [Y] into pad-button icons when it loads its tables.
            MethodInfo markup = AccessTools.Method(typeof(Localization), "CheckForMarkup");
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                int number = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    number++;
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] cells = line.Split('\t');
                    if (cells.Length < 3 || cells[0].Length == 0)
                    {
                        Plugin.Log.LogWarning("vi.tsv line " + number + " is not Tag<TAB>English<TAB>Vietnamese; skipped.");
                        continue;
                    }
                    string text = cells[cells.Length - 1].Replace("\\n", "\n");
                    if (text.Length == 0) continue;
                    // As Localization.LoadTSV does: cells exported with spreadsheet quoting lose
                    // their quotes, and a doubled quote stands for one.
                    if (text.Length > 2) text = text.Replace("\"", "\b").Replace("\b\b", "\"").Replace("\b", "");
                    if (markup != null)
                    {
                        var row = new[] { cells[0], text };
                        try
                        {
                            object[] args = { row };
                            markup.Invoke(null, args);
                            row = (string[])args[0];
                            text = row[1];
                        }
                        catch (Exception) { }
                    }
                    table[cells[0]] = text;
                }
            }
        }

        /// <summary>The text to show for a tag in the language in use, or null to let the game
        /// look it up itself (English mode, or a tag the table does not have).</summary>
        internal static string Find(string tag)
        {
            if (tag == LanguageTitleTag) return Plugin.Vietnamese ? "Ngôn ngữ (Language)" : "Language";
            if (!Plugin.Vietnamese) return null;
            string text;
            return table.TryGetValue(tag, out text) ? text : null;
        }

        /// <summary>
        /// The game's own token replacement: each [token] (matched ignoring case) becomes its value.
        /// Unlike the game's loop this never rescans an inserted value, so a value containing its
        /// own token cannot loop forever.
        /// </summary>
        internal static string Replace(string text, LocToken[] tokens)
        {
            if (tokens == null || tokens.Length == 0) return text;
            var result = new StringBuilder(text);
            foreach (LocToken token in tokens)
            {
                if (string.IsNullOrEmpty(token.m_token)) continue;
                int from = 0;
                while (from <= result.Length)
                {
                    int at = result.ToString().IndexOf(token.m_token, from, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;
                    string value = token.m_value ?? "";
                    result.Remove(at, token.m_token.Length);
                    result.Insert(at, value);
                    from = at + value.Length;
                }
            }
            return result.ToString();
        }
    }

    /// <summary>
    /// The game takes its language from Steam each time it asks (<c>Localization.GetLanguage</c>,
    /// which MOD Settings asks too). English and Vietnamese both answer English, so English
    /// means English whatever Steam is set to, and a text the Vietnamese table lacks falls back
    /// to English. Default (Steam) leaves the answer alone.
    /// </summary>
    [HarmonyPatch(typeof(Localization), "GetLanguage")]
    internal static class LanguageGet
    {
        private static void Postfix(ref SupportedLanguages __result)
        {
            if (Plugin.Language != null && !Plugin.Off) __result = SupportedLanguages.English;
        }
    }

    /// <summary>
    /// Every text the game shows by tag goes through <c>Localization.Get(tag, out text, tokens)</c>
    /// (the one-argument overload calls it too). In Vietnamese the answer comes from the table;
    /// anything the table lacks falls through to the game, so the worst case is English.
    /// </summary>
    [HarmonyPatch]
    internal static class LocalizationGet
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Localization), "Get",
                new[] { typeof(string), typeof(string).MakeByRefType(), typeof(LocToken[]) });
        }

        private static bool Prefix(string tag, ref string localised, LocToken[] findReplace, ref bool __result)
        {
            // A quoted tag is a literal: mods put their own words on the game's dialogs that way.
            // Literals other mods show are in the table too, with the quotes as their tag.
            if (string.IsNullOrEmpty(tag)) return true;
            string text = Translations.Find(tag);
            if (text == null) return true;
            localised = Translations.Replace(text, findReplace);
            __result = true;
            return false;
        }
    }
}
