using System;
using System.Collections.Generic;
using System.IO;
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
    /// is drawn entirely in a font that has them all (Coiny by default, see the TitleFont
    /// setting); it gets its own font back as soon as its text no longer needs it. This
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
            // Default (Steam): no swaps, so every title goes back to the game's font.
            if (string.IsNullOrEmpty(text) || Plugin.Off) return false;
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
            if (source != null && source == repairedBody && gameBody != null) source = gameBody;
        }

        /// <summary>
        /// The game's body font (Body_Font, Carlito Bold 1.103) draws Ậ, Ệ and Ộ with the dot below
        /// but without the hat, so BẬT reads BẠT. A text in it that has one of those letters is
        /// drawn in a repaired copy (fonts.bundle's "body", made by tools/carlito.py), which is the
        /// same font with the same metrics, and goes back to Body_Font when the letters are gone.
        /// </summary>
        private static readonly char[] Hatless = { 'Ậ', 'Ệ', 'Ộ' };
        private static Font gameBody, repairedBody;
        private static bool lookedBody;

        private static Font RepairedBody
        {
            get
            {
                if (lookedBody) return repairedBody;
                lookedBody = true;
                try { repairedBody = Bundled("body"); }
                catch (Exception error) { Plugin.Log.LogWarning("Could not load the repaired body font: " + error.Message); }
                return repairedBody;
            }
        }

        /// <summary>Swaps a body text to the repaired copy and back; true when the text is in either.</summary>
        private static bool CheckBody(Text text, Font font)
        {
            bool repaired = font == repairedBody;
            if (!repaired && font.name != "Body_Font") return false;
            bool needs = !Plugin.Off && text.text != null && text.text.IndexOfAny(Hatless) >= 0;
            if (repaired)
            {
                // Swapped by an older copy of the mod (see Adopt): find the game's font by name.
                if (!needs && gameBody == null)
                    foreach (Font each in Resources.FindObjectsOfTypeAll<Font>())
                        if (each != null && each.name == "Body_Font" && each != repairedBody) { gameBody = each; break; }
                if (!needs && gameBody != null) text.font = gameBody;
            }
            else if (needs)
            {
                Font use = RepairedBody;
                if (use == null) return true;
                gameBody = font;
                text.font = use;
            }
            return true;
        }

        private static Font Substitute
        {
            get
            {
                if (looked) return substitute;
                looked = true;
                TitleFont choice = Plugin.Titles.Value;
                try
                {
                    if (choice == TitleFont.GameFont) substitute = null;
                    else if (choice == TitleFont.SegoeUI) substitute = WindowsFont();
                    else substitute = Bundled(choice.ToString().ToLowerInvariant()) ?? WindowsFont();
                }
                catch (Exception error)
                {
                    Plugin.Log.LogWarning("Could not load a title font for Vietnamese: " + error.Message);
                    substitute = null;
                }
                Plugin.Log.LogInfo(substitute != null ? "Vietnamese titles use " + substitute.name + "." : "Titles keep the game's font.");
                return substitute;
            }
        }

        /// <summary>The fonts built into the mod (fonts.bundle, made by tools/fonts.py).</summary>
        private static AssetBundle bundle;

        /// <summary>Where the loaded bundle is kept for a newer copy of the mod.</summary>
        private const string BundleSlot = "com.manhl.overcooked2.tiengviet.fonts";

        /// <summary>
        /// Mod Updater can load a new version of this mod into the running game, next to the old
        /// assembly. Unity opens a bundle only once, so the new copy must use the one the old copy
        /// opened: from the AppDomain slot, or (from versions before the slot) by looking through
        /// the loaded bundles for one with these fonts.
        /// </summary>
        private static AssetBundle LoadedBefore()
        {
            var kept = AppDomain.CurrentDomain.GetData(BundleSlot) as AssetBundle;
            if (kept != null) return kept;
            try
            {
                foreach (AssetBundle loaded in Resources.FindObjectsOfTypeAll<AssetBundle>())
                    if (loaded != null && loaded.Contains("assets/tiengviet/body.ttf")) return loaded;
            }
            catch (Exception) { }
            return null;
        }

        private static Font Bundled(string name)
        {
            if (bundle == null) bundle = LoadedBefore();
            if (bundle == null)
            {
                Stream stream = typeof(HeaderFont).Assembly.GetManifestResourceStream("TiengViet.fonts.bundle");
                if (stream == null) { Plugin.Log.LogWarning("fonts.bundle is missing from TiengViet.dll."); return null; }
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int got = stream.Read(bytes, read, bytes.Length - read);
                    if (got <= 0) break;
                    read += got;
                }
                bundle = AssetBundle.LoadFromMemory(bytes);
                if (bundle == null) { Plugin.Log.LogWarning("Could not open the mod's font bundle."); return null; }
                AppDomain.CurrentDomain.SetData(BundleSlot, bundle);
            }
            Font font = bundle.LoadAsset<Font>("assets/tiengviet/" + name + ".ttf");
            if (font == null) Plugin.Log.LogWarning("The font bundle has no " + name + ".");
            return font;
        }

        /// <summary>Segoe UI Bold, which Windows has (Unity only finds regular, bold and italic faces).</summary>
        private static Font WindowsFont()
        {
            var installed = new HashSet<string>(Font.GetOSInstalledFontNames(), StringComparer.OrdinalIgnoreCase);
            foreach (string name in new[] { "Segoe UI Bold", "Arial Bold", "Arial" })
                if (installed.Contains(name))
                {
                    Font font = Font.CreateDynamicFontFromOSFont(name, 48);
                    font.name = name;
                    return font;
                }
            return null;
        }

        /// <summary>How each choice reads in MOD Settings.</summary>
        internal static string Label(object value)
        {
            if (!(value is TitleFont)) return Convert.ToString(value);
            switch ((TitleFont)value)
            {
                case TitleFont.Coiny: return "Coiny";
                case TitleFont.Grandstander: return "Grandstander Black";
                case TitleFont.Baloo2: return "Baloo 2 ExtraBold";
                case TitleFont.Nunito: return "Nunito Black";
                case TitleFont.PaytoneOne: return "Paytone One";
                case TitleFont.FiraSans: return "Fira Sans ExtraBold";
                case TitleFont.SegoeUI: return "Segoe UI Bold (Windows)";
                default: return Plugin.Vietnamese ? "Phông gốc của game (thiếu chữ tiếng Việt)" : "Game's own font (no Vietnamese letters)";
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
            if (font != null && CheckBody(text, font)) return;
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

        /// <summary>
        /// After Mod Updater loaded this version into the running game: titles an older copy
        /// swapped carry that copy's Swapped component, which nothing moves any more. Each is put
        /// back with the old copy's own Restore and removed, so <see cref="Sweep"/> swaps the
        /// title again with this copy. Returns how many there were.
        /// </summary>
        internal static int Adopt()
        {
            int count = 0;
            string name = typeof(Swapped).FullName;
            foreach (MonoBehaviour each in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (each == null || each is Swapped || each.GetType().FullName != name) continue;
                count++;
                try
                {
                    System.Reflection.MethodInfo restore = each.GetType().GetMethod("Restore",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (restore != null) restore.Invoke(each, null);
                }
                catch (Exception) { }
                try { UnityEngine.Object.DestroyImmediate(each); }
                catch (Exception) { }
            }
            return count;
        }

        /// <summary>Checks every text loaded in a scene.</summary>
        internal static void Sweep()
        {
            foreach (Text text in Resources.FindObjectsOfTypeAll<Text>())
                if (Refresh.InScene(text))
                    try { Check(text); }
                    catch (Exception) { }
        }

        private static readonly TextGenerator measurer = new TextGenerator();
        private static readonly Dictionary<string, float> shifts = new Dictionary<string, float>();
        private const int MeasureSize = 100;

        /// <summary>
        /// How far up a text in <paramref name="to"/> must move, per point of font size, so that its
        /// capitals sit where <paramref name="from"/> puts them. Each font places its letters in
        /// the line by its own ascent and descent (Coiny's letters sit much lower than Naughty
        /// Squirrel's), so a title swapped as it is drops below the middle of its banner. This lays
        /// out an "H" in both fonts with the text's own alignment and compares their middles.
        /// </summary>
        private static float Shift(Text text, Font from, Font to)
        {
            int row = (int)text.alignment / 3;
            string key = from.GetInstanceID() + "/" + to.GetInstanceID() + "/" + row + "/" + text.lineSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture);
            float shift;
            if (shifts.TryGetValue(key, out shift)) return shift;
            TextGenerationSettings settings = text.GetGenerationSettings(Vector2.zero);
            settings.fontSize = MeasureSize;
            settings.scaleFactor = 1f;
            settings.resizeTextForBestFit = false;
            settings.richText = false;
            settings.horizontalOverflow = HorizontalWrapMode.Overflow;
            settings.verticalOverflow = VerticalWrapMode.Overflow;
            settings.generateOutOfBounds = true;
            float a = Middle(settings, from), b = Middle(settings, to);
            shift = float.IsNaN(a) || float.IsNaN(b) ? 0f : (a - b) / MeasureSize;
            shifts[key] = shift;
            return shift;
        }

        private static float Middle(TextGenerationSettings settings, Font font)
        {
            settings.font = font;
            measurer.Invalidate();
            if (!measurer.Populate("H", settings)) return float.NaN;
            IList<UIVertex> verts = measurer.verts;
            float low = float.MaxValue, high = float.MinValue;
            for (int i = 0; i < verts.Count; i++)
            {
                low = Mathf.Min(low, verts[i].position.y);
                high = Mathf.Max(high, verts[i].position.y);
            }
            return verts.Count == 0 || high <= low ? float.NaN : (low + high) / 2f;
        }

        /// <summary>
        /// Remembers the font (and size) a text had before it was swapped, and moves the swapped
        /// text up or down so its letters sit where the game's font would put them.
        /// </summary>
        internal sealed class Swapped : MonoBehaviour
        {
            /// <summary>A label is never shrunk below this share of its own size.</summary>
            private const float SmallestScale = 0.7f;

            private Text text;
            private Font original;
            /// <summary>The move for the font now shown, per point of font size (see Shift).</summary>
            private float shiftPerSize;
            /// <summary>The label's own sizes, and the sizes it was last left with here.</summary>
            private int size, largest, shownSize, shownLargest;
            private bool fitting;

            internal void Remember(Text owner, Font font)
            {
                text = owner;
                if (font != null && font != substitute)
                {
                    original = font;
                    size = shownSize = owner.fontSize;
                    largest = shownLargest = owner.resizeTextMaxSize;
                }
            }

            /// <summary>
            /// Code that builds a label often sets its size after its words (MOD Settings clones
            /// a big title and then sets 22). A size that differs from the one left here was set
            /// by someone else, so it becomes the label's own size.
            /// </summary>
            private void Adopt()
            {
                if (text.fontSize != shownSize) size = shownSize = text.fontSize;
                if (text.resizeTextMaxSize != shownLargest) largest = shownLargest = text.resizeTextMaxSize;
            }

            private void Show(int fontSize, int maxSize)
            {
                if (text.fontSize != fontSize) text.fontSize = fontSize;
                if (text.resizeTextMaxSize != maxSize) text.resizeTextMaxSize = maxSize;
                shownSize = fontSize;
                shownLargest = maxSize;
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
                if (fitting || text == null) return;
                fitting = true;
                try
                {
                    float shift = original != null && text.font != null && text.font != original ? Shift(text, original, text.font) : 0f;
                    if (shift != shiftPerSize)
                    {
                        shiftPerSize = shift;
                        text.SetVerticesDirty();
                    }
                    Adopt();
                    if (size <= 0) return;
                    Show(size, largest);
                    string words = text.text;
                    if (string.IsNullOrEmpty(words) || words.IndexOf('\n') >= 0) return;
                    bool best = text.resizeTextForBestFit;
                    // Too wide: a wrapping label would break onto a second line. Too tall (a
                    // font with taller lines than the game's): a clipping label shows nothing.
                    // Best fit already keeps a label inside its height.
                    bool wraps = text.horizontalOverflow == HorizontalWrapMode.Wrap;
                    bool clips = !best && text.verticalOverflow == VerticalWrapMode.Truncate;
                    if (!wraps && !clips) return;
                    int reference = best ? largest : size;
                    Rect box = text.rectTransform.rect;
                    if (box.width <= 0 || box.height <= 0 || reference <= 0) return;
                    TextGenerationSettings settings = text.GetGenerationSettings(Vector2.zero);
                    settings.resizeTextForBestFit = false;
                    settings.fontSize = reference;
                    settings.horizontalOverflow = HorizontalWrapMode.Overflow;
                    settings.verticalOverflow = VerticalWrapMode.Overflow;
                    TextGenerator generator = text.cachedTextGeneratorForLayout;
                    float scale = 1f;
                    if (wraps)
                    {
                        float wide = generator.GetPreferredWidth(words, settings) / text.pixelsPerUnit;
                        if (wide > box.width) scale = box.width / wide;
                    }
                    if (clips)
                    {
                        float tall = generator.GetPreferredHeight(words, settings) / text.pixelsPerUnit;
                        if (tall > box.height) scale = Mathf.Min(scale, box.height / tall);
                    }
                    if (scale >= 1f) return;
                    int fitted = Mathf.Max(Mathf.FloorToInt(reference * SmallestScale), Mathf.FloorToInt(reference * scale));
                    if (best) Show(size, Mathf.Max(text.resizeTextMinSize, fitted));
                    else Show(fitted, largest);
                }
                finally { fitting = false; }
            }

            /// <summary>Boxes laid out after the text was set fit it again once they have a size.</summary>
            private void OnRectTransformDimensionsChange()
            {
                if (text != null && text.font == substitute) Fit();
            }

            /// <summary>Moves the letters just built. The move is worked out in Fit, not here:
            /// laying out a test letter can rebuild the font's texture, and that must not happen
            /// while the canvas is drawing.</summary>
            internal void Move(VertexHelper mesh)
            {
                if (!isActiveAndEnabled || text == null || shiftPerSize == 0f || text.font == null || text.font == original) return;
                float points = text.fontSize;
                if (text.resizeTextForBestFit)
                {
                    points = text.cachedTextGenerator.fontSizeUsedForBestFit;
                    // In case the generator counts in screen pixels rather than points.
                    if (points > text.resizeTextMaxSize * 1.01f && text.pixelsPerUnit > 0f) points /= text.pixelsPerUnit;
                }
                float move = shiftPerSize * points;
                if (move == 0f) return;
                UIVertex vertex = new UIVertex();
                for (int i = 0; i < mesh.currentVertCount; i++)
                {
                    mesh.PopulateUIVertex(ref vertex, i);
                    vertex.position.y += move;
                    mesh.SetUIVertex(vertex, i);
                }
            }

            internal void Restore()
            {
                if (text == null || original == null) return;
                Adopt();
                shiftPerSize = 0f;
                if (text.font != original) text.font = original;
                if (size > 0) Show(size, largest);
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

        /// <summary>
        /// A swapped title is moved as soon as Text has built its letters, before anything else
        /// reads them: the game's button prompts (the A and B icons in "A ACCEPT B CANCEL") place
        /// their icon images where the text left room for them, straight after this, so the
        /// icons move with the words. Mesh effects (outline, shadow) then work on the moved letters.
        /// </summary>
        [HarmonyPatch(typeof(Text), "OnPopulateMesh", new Type[] { typeof(VertexHelper) })]
        internal static class TextPopulate
        {
            private static void Postfix(Text __instance, VertexHelper toFill)
            {
                try
                {
                    if (substitute == null || __instance.font != substitute) return;
                    Swapped swapped = __instance.GetComponent<Swapped>();
                    if (swapped != null) swapped.Move(toFill);
                }
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
