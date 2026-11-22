using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TiengViet
{
    /// <summary>
    /// The LANGUAGE / NGÔN NGỮ row at the top of Settings → Game Settings, in the main menu and
    /// wherever else the game shows that screen. It is a copy of the game's own Resolution row (a
    /// left/right selector), so it looks, sounds and navigates like the rows around it with mouse,
    /// keyboard or pad. It is added before the list first sets itself up, so the list wires its
    /// navigation in like any other row. The choice applies and is saved straight away: the
    /// screen's Save/Cancel only cover the game's own options.
    /// </summary>
    [HarmonyPatch(typeof(T17ScrollView), "SingleTimeInitialize")]
    internal static class LanguageRow
    {
        internal const string RowName = "TiengViet.Language";

        private static readonly FieldInfo OptionType = AccessTools.Field(typeof(BaseUIOption<INameListOption>), "m_OptionType");
        private static readonly FieldInfo OptionValue = AccessTools.Field(typeof(BaseUIOption<INameListOption>), "m_Option");
        private static readonly FieldInfo LocalizeOption = AccessTools.Field(typeof(SelectorOption), "m_LocalizeOption");

        /// <summary>A value <see cref="OptionsData.OptionType"/> does not define, so the copy's
        /// Awake leaves the game's Resolution option alone.</summary>
        private const OptionsData.OptionType NoGameOption = (OptionsData.OptionType)(-1);

        private static readonly List<SelectorOption> rows = new List<SelectorOption>();
        private static readonly Choice choice = new Choice();

        private static void Prefix(T17ScrollView __instance)
        {
            try { Add(__instance); }
            catch (Exception error) { Plugin.Log.LogError("Could not add the language row to Game Settings: " + error); }
        }

        private static FrontendOptionsMenu Owner(Component scroll)
        {
            for (Transform at = scroll.transform; at != null; at = at.parent)
            {
                FrontendOptionsMenu menu = at.GetComponent<FrontendOptionsMenu>();
                if (menu != null) return menu;
            }
            return null;
        }

        private static void Add(T17ScrollView scroll)
        {
            if (OptionType == null || OptionValue == null) throw new MissingMemberException("BaseUIOption`1.m_OptionType/m_Option");
            FrontendOptionsMenu menu = Owner(scroll);
            if (menu == null || menu.m_ScrollView != scroll) return;
            RectTransform content = scroll.m_ContentParent;
            if (content == null || content.Find(RowName) != null) return;
            Transform template = content.Find("Resolution");
            if (template == null)
            {
                // Other screens reuse the options menu (lobby kitchen options, mods' level
                // pickers); only Game Settings, the one with the volume rows, should have had it.
                if (content.Find("MusicVolume") != null)
                    Plugin.Log.LogWarning("Game Settings has no Resolution row to copy; no language row. Rows: " + Children(content));
                return;
            }
            Plugin.Log.LogInfo("Language row added to Game Settings, above: " + Children(content));

            // Built under an inactive holder, so the copy is set up before any of it wakes.
            var holder = new GameObject("TiengViet.Build");
            holder.SetActive(false);
            try
            {
                GameObject row = UnityEngine.Object.Instantiate(template.gameObject, holder.transform, false);
                row.name = RowName;
                SelectorOption option = null;
                foreach (SelectorOption each in row.GetComponents<SelectorOption>())
                {
                    OptionType.SetValue(each, NoGameOption);
                    OptionValue.SetValue(each, choice);
                    if (LocalizeOption != null) LocalizeOption.SetValue(each, false);
                    if (option == null) option = each;
                }
                if (option == null) throw new MissingMemberException("Resolution row has no SelectorOption");

                Transform title = row.transform.Find("Title");
                T17Text label = title != null ? title.GetComponent<T17Text>() : null;
                if (label != null)
                {
                    label.m_bNeedsLocalization = true;
                    label.m_LocalizationTag = Translations.LanguageTitleTag;
                    label.SetNewPlaceHolder("Language");
                }
                row.AddComponent<RowView>().Option = option;
                rows.Add(option);

                row.transform.SetParent(content, false);
                row.transform.SetSiblingIndex(0);
            }
            finally { UnityEngine.Object.Destroy(holder); }
        }

        private static string Children(Transform parent)
        {
            var names = new List<string>();
            for (int i = 0; i < parent.childCount; i++) names.Add(parent.GetChild(i).name);
            return string.Join(", ", names.ToArray());
        }

        /// <summary>Shows the current choice on every language row.</summary>
        internal static void SyncAll()
        {
            rows.RemoveAll(delegate(SelectorOption row) { return row == null; });
            foreach (SelectorOption row in rows)
                try { row.SyncUIWithOption(); }
                catch (Exception) { }
        }

        /// <summary>Keeps the row's value right whenever it appears (the setting may have been
        /// changed in the config file or MOD Settings meanwhile).</summary>
        private sealed class RowView : MonoBehaviour
        {
            internal SelectorOption Option;
            private void OnEnable() { if (Option != null) Option.SyncUIWithOption(); }
        }

        /// <summary>The game's option rows talk to an option; this one is the language setting.</summary>
        private sealed class Choice : INameListOption
        {
            private static readonly string[] Names = { "English", "Tiếng Việt" };

            public OptionsData.Categories Category { get { return OptionsData.Categories.System; } }
            public string Label { get { return "Language"; } }
            public string[] GetNames() { return Names; }
            public int GetOption() { return Plugin.Vietnamese ? 1 : 0; }
            public void Commit() { }

            public void SetOption(int value)
            {
                value = ((value % Names.Length) + Names.Length) % Names.Length;
                var language = value == 1 ? GameLanguage.Vietnamese : GameLanguage.English;
                if (Plugin.Language.Value != language) Plugin.Language.Value = language;
                SyncAll();
            }
        }
    }
}
