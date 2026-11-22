using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TiengViet
{
    /// <summary>
    /// The game's title font (Naughty Squirrel, used by Header_Font) has the accented letters of
    /// French or Spanish but not the Vietnamese ones made with a hook, a dot below or a horn
    /// (ả, ạ, ơ, ư, ấ, ộ...). Unity would borrow each missing letter from some other font, so a
    /// word comes out in two typefaces. Instead, a text in that font that needs one of those letters
    /// is drawn entirely in a Windows font that has them all (Segoe UI Bold by default, see the
    /// HeaderFont setting); it gets its own font back as soon as its text no longer needs it. This
    /// covers every text in that font, the game's and any mod's, whichever language is chosen.
    /// </summary>
    internal static class HeaderFont
    {
        /// <summary>The title fonts. Both are Naughty Squirrel.</summary>
        private static readonly string[] Lacking = { "Header_Font", "NaughtySquirrel2" };

        private static Font substitute;
        private static bool looked;
        private static readonly Dictionary<int, bool> lacking = new Dictionary<int, bool>();

        /// <summary>Letters Naughty Squirrel does not have: Ơ ơ Ư ư and everything in U+1EA0–U+1EF9.</summary>
        internal static bool Missing(char c)
        {
            return (c >= 'Ạ' && c <= 'ỹ') || c == 'Ơ' || c == 'ơ' || c == 'Ư' || c == 'ư';
        }

        /// <summary>
        /// A text needs the substitute when it has a letter the title font lacks. While Vietnamese
        /// is on, so does any text with letters at all, so a row of tabs or buttons is drawn in one
        /// typeface rather than switching between words; numbers and symbols (timers, scores)
        /// keep the game's font.
        /// </summary>
        private static bool NeedsSubstitute(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            bool words = Plugin.Vietnamese;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (Missing(c) || (words && char.IsLetter(c))) return true;
            }
            return false;
        }

        private static bool IsLacking(Font font)
        {
            if (font == null) return false;
            int id = font.GetInstanceID();
            bool result;
            if (!lacking.TryGetValue(id, out result))
            {
                result = Array.IndexOf(Lacking, font.name) >= 0;
                lacking[id] = result;
            }
            if (result && original == null && font.name == "Header_Font") original = font;
            return result;
        }

        /// <summary>The game's own title font, once a text using it has been seen.</summary>
        private static Font original;

        /// <summary>
        /// Other mods that copy the game's look read the font off the game's texts. MOD Settings
        /// (OC2ModSettings) insists the font it copies is the game's own Header_Font or Body_Font
        /// and fails on every frame otherwise, so it is handed the title font instead of the
        /// substitute; the texts it builds from it are then checked like any other.
        /// </summary>
        internal static void PatchFontReaders(Harmony harmony)
        {
            Type lease = AccessTools.TypeByName("OC2ModSettings.NativeFontLease");
            MethodInfo keep = lease != null ? AccessTools.Method(lease, "Keep", new[] { typeof(Font) }) : null;
            if (keep == null) return;
            harmony.Patch(keep, prefix: new HarmonyMethod(typeof(HeaderFont), "GameFontFirst"));
        }

        private static void GameFontFirst(ref Font source)
        {
            if (source != null && source == substitute && original != null) source = original;
        }

        private static Font Substitute
        {
            get
            {
                if (looked) return substitute;
                looked = true;
                var names = new List<string>();
                foreach (string name in (Plugin.HeaderFonts.Value ?? "").Split(','))
                    if (name.Trim().Length > 0) names.Add(name.Trim());
                if (names.Count == 0) return substitute = null;
                try
                {
                    string[] all = Font.GetOSInstalledFontNames();
                    var installed = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);
                    Plugin.Log.LogDebug("Windows fonts Unity can use (" + all.Length + "): " + string.Join(", ",
                        Array.FindAll(all, delegate(string n) { return n.IndexOf("Segoe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Arial", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Bahn", StringComparison.OrdinalIgnoreCase) >= 0; })));
                    string found = names.Find(delegate(string name) { return installed.Contains(name); });
                    if (found == null)
                    {
                        Plugin.Log.LogWarning("None of the HeaderFont fonts is installed (" + Plugin.HeaderFonts.Value +
                            "); Vietnamese titles keep the game's font.");
                        return substitute = null;
                    }
                    substitute = Font.CreateDynamicFontFromOSFont(found, 48);
                    substitute.name = "TiengViet " + found;
                    Plugin.Log.LogInfo("Vietnamese titles use " + found + ".");
                }
                catch (Exception error)
                {
                    Plugin.Log.LogWarning("Could not load a title font for Vietnamese: " + error.Message);
                    substitute = null;
                }
                return substitute;
            }
        }

        /// <summary>The HeaderFont setting changed: put every title back and look the font up again.</summary>
        internal static void Reset()
        {
            foreach (Swapped swapped in Resources.FindObjectsOfTypeAll<Swapped>())
                if (swapped != null) swapped.Restore();
            substitute = null;
            looked = false;
        }

        /// <summary>Checks one text: swap in the substitute, or give its own font back.</summary>
        internal static void Check(Text text)
        {
            if (text == null) return;
            Font font = text.font;
            Swapped swapped;
            if (IsLacking(font))
            {
                if (!NeedsSubstitute(text.text)) return;
                Font use = Substitute;
                if (use == null) return;
                swapped = text.GetComponent<Swapped>();
                if (swapped == null) swapped = text.gameObject.AddComponent<Swapped>();
                swapped.Remember(text, font);
                text.font = use;
                swapped.Fit();
            }
            else if (font != null && font == substitute)
            {
                swapped = text.GetComponent<Swapped>();
                if (swapped == null) return;
                if (NeedsSubstitute(text.text)) swapped.Fit();
                else swapped.Restore();
            }
        }

        /// <summary>Checks every text loaded in a scene.</summary>
        internal static void Sweep()
        {
            foreach (Text text in Resources.FindObjectsOfTypeAll<Text>())
                if (Refresh.InScene(text))
                    try { Check(text); }
                    catch (Exception) { }
        }

        /// <summary>Remembers the font (and size) a text had before it was swapped.</summary>
        internal sealed class Swapped : MonoBehaviour
        {
            /// <summary>A label is never shrunk below this share of its own size.</summary>
            private const float SmallestScale = 0.7f;

            private Text text;
            private Font original;
            private int size, largest;
            private bool fitting;

            internal void Remember(Text owner, Font font)
            {
                text = owner;
                if (font != null && font != substitute)
                {
                    original = font;
                    size = owner.fontSize;
                    largest = owner.resizeTextMaxSize;
                }
            }

            /// <summary>
            /// The substitute is wider than Naughty Squirrel, so a one-line label sized for the
            /// game's font (a menu tab, a button) could wrap onto a second line. Such a label is
            /// made just small enough to fit on one line again, down to 70% of its size: its font
            /// size, or for a label that sizes itself (best fit, which would happily use two lines
            /// when they fit the height) the largest size it may pick.
            /// </summary>
            internal void Fit()
            {
                if (fitting || text == null || size <= 0) return;
                if (text.horizontalOverflow != HorizontalWrapMode.Wrap) return;
                string words = text.text;
                if (string.IsNullOrEmpty(words) || words.IndexOf('\n') >= 0) return;
                fitting = true;
                try
                {
                    bool best = text.resizeTextForBestFit;
                    int reference = best ? largest : size;
                    if (best) { if (text.resizeTextMaxSize != largest) text.resizeTextMaxSize = largest; }
                    else if (text.fontSize != size) text.fontSize = size;
                    float width = text.rectTransform.rect.width;
                    if (width <= 0 || reference <= 0) return;
                    TextGenerationSettings settings = text.GetGenerationSettings(Vector2.zero);
                    settings.resizeTextForBestFit = false;
                    settings.fontSize = reference;
                    float wanted = text.cachedTextGeneratorForLayout.GetPreferredWidth(words, settings) / text.pixelsPerUnit;
                    if (wanted <= width) return;
                    int fitted = Mathf.Max(Mathf.FloorToInt(reference * SmallestScale), Mathf.FloorToInt(reference * width / wanted));
                    if (best) text.resizeTextMaxSize = Mathf.Max(text.resizeTextMinSize, fitted);
                    else text.fontSize = fitted;
                }
                finally { fitting = false; }
            }

            /// <summary>Boxes laid out after the text was set fit it again once they have a size.</summary>
            private void OnRectTransformDimensionsChange()
            {
                if (text != null && text.font == substitute) Fit();
            }

            internal void Restore()
            {
                if (text == null || original == null) return;
                if (text.font != original) text.font = original;
                if (size > 0 && text.fontSize != size) text.fontSize = size;
                if (largest > 0 && text.resizeTextMaxSize != largest) text.resizeTextMaxSize = largest;
            }
        }

        [HarmonyPatch(typeof(Text), "text", MethodType.Setter)]
        internal static class TextSet
        {
            private static void Postfix(Text __instance)
            {
                try { Check(__instance); }
                catch (Exception) { }
            }
        }

        /// <summary>Some texts get their words without the setter (the game's LocalisedText
        /// writes the field in Awake); they are checked as they appear.</summary>
        [HarmonyPatch(typeof(Text), "OnEnable")]
        internal static class TextEnable
        {
            private static void Postfix(Text __instance)
            {
                try { Check(__instance); }
                catch (Exception) { }
            }
        }
    }
}
