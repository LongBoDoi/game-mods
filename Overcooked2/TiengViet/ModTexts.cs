using System;

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace TiengViet
{
    /// <summary>
    /// Vietnamese for other mods that keep their own text tables rather than the game's, built
    /// into the DLL from mods.tsv (<c>Source TAB Key TAB English TAB Vietnamese</c>):
    ///
    /// - <b>ModSettings</b>: MOD Settings (OC2ModSettings) labels its page from a table of 12
    ///   languages keyed by its UiText names (Title, Close, Refresh...).
    /// - <b>HostTools</b>: Arcade MOD (HostTools) looks every text up by key in its own
    ///   Chinese/English/Korean/Japanese table, for its settings, panels and dialogs alike.
    /// - <b>Setting</b>: the names, sections and descriptions other mods give their settings,
    ///   matched by their exact English text (the Key column is empty), as MOD Settings shows them.
    ///
    /// Texts are in C# escape form (<c>\n</c>, <c>\"</c>), as the mods' own sources write them.
    /// Every hook finds its target by name; a mod that is not installed, or that changed, is
    /// skipped and keeps its own words.
    /// </summary>
    internal static class ModTexts
    {
        private static readonly Dictionary<string, string> modSettings = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> hostTools = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> settings = new Dictionary<string, string>();
        private static readonly HashSet<string> vietnamese = new HashSet<string>();
        private static readonly HashSet<string> missed = new HashSet<string>();

        internal static int Count { get { return modSettings.Count + hostTools.Count + settings.Count; } }

        internal static void Load()
        {
            Stream stream = typeof(ModTexts).Assembly.GetManifestResourceStream("TiengViet.mods.tsv");
            if (stream == null) return;
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                int number = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    number++;
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] cells = line.Split('\t');
                    if (cells.Length != 4 || cells[3].Length == 0)
                    {
                        Plugin.Log.LogWarning("mods.tsv line " + number + " is not Source<TAB>Key<TAB>English<TAB>Vietnamese; skipped.");
                        continue;
                    }
                    string text = Unescape(cells[3]);
                    vietnamese.Add(text);
                    switch (cells[0])
                    {
                        case "ModSettings": modSettings[cells[1]] = text; break;
                        case "HostTools": hostTools[cells[1]] = text; break;
                        case "Setting": settings[Unescape(cells[2])] = text; break;
                    }
                }
            }
        }

        private static string Unescape(string text)
        {
            if (text.IndexOf('\\') < 0) return text;
            var result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\\' || i + 1 == text.Length) { result.Append(c); continue; }
                char next = text[++i];
                switch (next)
                {
                    case 'n': result.Append('\n'); break;
                    case 't': result.Append('\t'); break;
                    case 'r': result.Append('\r'); break;
                    default: result.Append(next); break;
                }
            }
            return result.ToString();
        }

        // ---- Hooks -------------------------------------------------------------------------

        private static Type pageType, entryType, pluginType, languageType;

        /// <summary>Every plugin has loaded by now, so the other mods' types can be found.</summary>
        internal static void Patch(Harmony harmony)
        {
            Type texts = AccessTools.TypeByName("OC2ModSettings.Texts");
            Hook(harmony, texts, "Resolve", null, "ModSettingsText");
            Type metadata = AccessTools.TypeByName("OC2ModSettings.EntryMetadata");
            Hook(harmony, metadata, "Read", null, "SettingNames");
            Hook(harmony, AccessTools.TypeByName("OC2ModSettings.RegisteredAction"), "Metadata", null, "SettingNames");
            pageType = AccessTools.TypeByName("OC2ModSettings.NativePage");
            entryType = AccessTools.TypeByName("OC2ModSettings.NativeEntry");
            pluginType = AccessTools.TypeByName("OC2ModSettings.ModSettingsPlugin");
            if (metadata != null)
            {
                nameField = AccessTools.Field(metadata, "Name");
                categoryField = AccessTools.Field(metadata, "Category");
                descriptionField = AccessTools.Field(metadata, "Description");
            }

            languageType = AccessTools.TypeByName("HostUtilities.Language");
            Hook(harmony, languageType, "GetText", new[] { typeof(string), typeof(string) }, "HostToolsText");

            // Arcade MOD handed its setting names to MOD Settings while loading, before these
            // hooks were in place, so both are asked to look them up again.
            if (Plugin.Vietnamese) Relabel();
        }

        private static void Hook(Harmony harmony, Type type, string method, Type[] parameters, string postfix)
        {
            if (type == null) return;
            MethodInfo target = AccessTools.Method(type, method, parameters);
            if (target == null)
            {
                Plugin.Log.LogWarning(type.FullName + "." + method + " not found; that mod keeps its own words.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ModTexts), postfix));
        }

        /// <summary>MOD Settings: <c>Texts.Resolve(UiText key, SupportedLanguages language)</c>.</summary>
        private static void ModSettingsText(object[] __args, ref string __result)
        {
            if (!Plugin.Vietnamese || __args == null || __args.Length == 0 || __args[0] == null) return;
            string text;
            if (modSettings.TryGetValue(__args[0].ToString(), out text)) __result = text;
        }

        private static FieldInfo nameField, categoryField, descriptionField;

        /// <summary>MOD Settings reads each setting's name, section and description from its mod.</summary>
        private static void SettingNames(object __result)
        {
            if (!Plugin.Vietnamese || __result == null || nameField == null) return;
            Translate(__result, nameField);
            Translate(__result, categoryField);
            Translate(__result, descriptionField);
        }

        private static void Translate(object metadata, FieldInfo field)
        {
            if (field == null) return;
            var english = field.GetValue(metadata) as string;
            if (string.IsNullOrEmpty(english)) return;
            string text;
            if (settings.TryGetValue(english, out text)) field.SetValue(metadata, text);
            else if (!vietnamese.Contains(english) && !LooksVietnamese(english) && missed.Add(english))
                Plugin.Log.LogDebug("No Vietnamese for MOD Settings text: " + english.Replace("\n", "\\n"));
        }

        /// <summary>Mods in this collection already write some of their settings in Vietnamese.</summary>
        private static bool LooksVietnamese(string text)
        {
            foreach (char c in text)
                if (HeaderFont.Missing(c) || c == 'đ' || c == 'Đ' || c == 'ă' || c == 'Ă') return true;
            return false;
        }

        /// <summary>Arcade MOD: <c>Language.GetText(string key, string language)</c>.</summary>
        private static void HostToolsText(string key, ref string __result)
        {
            if (!Plugin.Vietnamese || key == null) return;
            string text;
            if (!hostTools.TryGetValue(key, out text)) return;
            __result = text;
            if (!taught) Teach();
        }

        private static bool taught;

        /// <summary>
        /// Arcade MOD also matches texts back to keys: a choice setting accepts any language's
        /// name for a choice, and a setting's shown name leads back to its section. The Vietnamese
        /// texts are added to its table under their own language code so those still match.
        /// </summary>
        private static void Teach()
        {
            taught = true;
            try
            {
                var table = AccessTools.Field(languageType, "languageDict").GetValue(null) as Dictionary<string, Dictionary<string, string>>;
                var configKeys = AccessTools.Field(languageType, "configTranslationKeys").GetValue(null) as HashSet<string>;
                var keyByText = AccessTools.Field(languageType, "configTranslationKeyByText").GetValue(null) as Dictionary<string, string>;
                if (table == null) { taught = false; return; }
                foreach (KeyValuePair<string, string> row in hostTools)
                {
                    Dictionary<string, string> texts;
                    if (!table.TryGetValue(row.Key, out texts)) continue;
                    texts["vi-vn"] = row.Value;
                    if (configKeys != null && keyByText != null && configKeys.Contains(row.Key) && !keyByText.ContainsKey(row.Value))
                        keyByText[row.Value] = row.Key;
                }
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("Could not add Vietnamese to Arcade MOD's table: " + error.Message);
            }
        }

        /// <summary>
        /// The language changed. Both mods relabel themselves when the game's language changes,
        /// which this mod does not touch, so each is told its language is out of date.
        /// </summary>
        internal static void Relabel()
        {
            try
            {
                if (languageType != null)
                {
                    FieldInfo published = AccessTools.Field(languageType, "publishedGameLanguageCode");
                    if (published != null) published.SetValue(null, null);
                }
                if (pluginType != null)
                {
                    object plugin = AccessTools.Field(pluginType, "Instance").GetValue(null);
                    if (plugin != null)
                    {
                        object catalog = AccessTools.Field(pluginType, "Catalog").GetValue(plugin);
                        if (catalog != null) AccessTools.Method(catalog.GetType(), "Invalidate").Invoke(catalog, null);
                        object entry = AccessTools.Field(pluginType, "entry").GetValue(plugin);
                        if (entry != null) Forget(entry);
                    }
                }
                if (pageType != null)
                    foreach (UnityEngine.Object page in Resources.FindObjectsOfTypeAll(pageType))
                        Forget(page);
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("Could not relabel MOD Settings: " + error.Message);
            }
        }

        /// <summary>Makes a MOD Settings part think the game's language changed since it last looked.</summary>
        private static void Forget(object owner)
        {
            FieldInfo field = AccessTools.Field(owner.GetType(), "language");
            if (field != null && field.FieldType.IsEnum) field.SetValue(owner, Enum.ToObject(field.FieldType, -1));
        }
    }
}
