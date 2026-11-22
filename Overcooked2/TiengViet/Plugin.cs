using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace TiengViet
{
    internal enum GameLanguage { English, Vietnamese }

    /// <summary>Title fonts with every Vietnamese letter: six built into the mod, one from
    /// Windows, or the game's own (which lacks them).</summary>
    internal enum TitleFont { Coiny, Grandstander, Baloo2, Nunito, PaytoneOne, FiraSans, SegoeUI, GameFont }

    /// <summary>Read by name by MOD Settings and the Configuration Manager.</summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public Func<object, string> ObjToStr;
        public int? Order;
    }

    /// <summary>
    /// Tiếng Việt: plays Overcooked! 2 and every mod in this collection in Vietnamese.
    ///
    /// The game has no language menu on PC (it follows Steam's language), so this adds one: a
    /// LANGUAGE / NGÔN NGỮ row at the top of Settings → Game Settings (see <see cref="LanguageRow"/>).
    /// While Vietnamese is chosen, every text the game looks up by tag comes from the bundled
    /// table (vi.tsv, see <see cref="Translations"/>), and the choice is published to the other
    /// mods through <see cref="Slot"/>, which each of them reads through its own Lang class. The
    /// change applies at once: text on screen is redrawn in the new language.
    ///
    /// The game's title font has no Vietnamese letters such as ạ, ả, ơ or ư, so a title that
    /// needs one is drawn in a Windows font that has them (<see cref="HeaderFont"/>).
    /// </summary>
    [BepInPlugin(Id, "Tieng Viet", Version)]
    [BepInProcess("Overcooked2.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.manhl.overcooked2.tiengviet";
        public const string Version = "1.0.0";

        /// <summary>The AppDomain slot every mod in the collection reads: "vi" or "en".</summary>
        internal const string Slot = "com.manhl.overcooked2.language";

        internal static ManualLogSource Log;
        internal static ConfigEntry<GameLanguage> Language;
        internal static ConfigEntry<TitleFont> Titles;

        internal static bool Vietnamese { get { return Language != null && Language.Value == GameLanguage.Vietnamese; } }

        private Harmony harmony;

        private void Awake()
        {
            Log = Logger;
            Language = Config.Bind("General", "Language", GameLanguage.Vietnamese,
                "Ngôn ngữ của game và các mod trong bộ này. Đổi được ngay trong game: Cài đặt → Cài đặt trò chơi → NGÔN NGỮ.\n" +
                "Language of the game and of this collection's mods. Also in game: Settings → Game Settings → LANGUAGE.");
            Titles = Config.Bind("General", "TitleFont", TitleFont.Coiny, new ConfigDescription(
                "Phông cho tiêu đề và nút của game khi chơi tiếng Việt, vì phông gốc thiếu các chữ như ạ, ả, ơ, ư. " +
                "Sáu phông đầu có sẵn trong mod, Segoe UI lấy từ Windows; đổi là thấy ngay.\n" +
                "Font for the game's titles and buttons in Vietnamese, since the game's own lacks letters such as ạ, ả, ơ, ư. " +
                "The first six come with the mod, Segoe UI is Windows'; changes show at once.",
                null, new ConfigurationManagerAttributes { ObjToStr = HeaderFont.Label, Order = -1 }));

            Publish();
            Translations.Load();
            ModTexts.Load();

            harmony = new Harmony(Id);
            Patch(typeof(LocalizationGet), "the game's text lookup");
            Patch(typeof(LanguageRow), "the language option in Game Settings");
            Patch(typeof(HeaderFont.TextSet), "the title font fix");
            Patch(typeof(HeaderFont.TextEnable), "the title font fix");

            Language.SettingChanged += delegate { Apply(); };
            Titles.SettingChanged += delegate { HeaderFont.Reset(); HeaderFont.Sweep(); };
            Log.LogInfo("Tiếng Việt " + Version + ": " + (Vietnamese ? "tiếng Việt" : "English") + ", " +
                Translations.Count + " game texts and " + ModTexts.Count + " texts of other mods translated.");
        }

        /// <summary>Every plugin has loaded by now, so other mods' types can be found.</summary>
        private void Start()
        {
            try { HeaderFont.PatchFontReaders(harmony); }
            catch (Exception error) { Log.LogError("Could not patch MOD Settings' font check: " + error); }
            try { ModTexts.Patch(harmony); }
            catch (Exception error) { Log.LogError("Could not patch other mods' text tables: " + error); }
        }

        private void Patch(Type patch, string what)
        {
            try { harmony.PatchAll(patch); }
            catch (Exception error) { Log.LogError("Could not patch " + what + ": " + error); }
        }

        private static void Publish()
        {
            AppDomain.CurrentDomain.SetData(Slot, Vietnamese ? "vi" : "en");
        }

        /// <summary>Switches everything on screen to the language just chosen.</summary>
        internal static void Apply()
        {
            Publish();
            Refresh.All();
            ModTexts.Relabel();
            LanguageRow.SyncAll();
            Log.LogInfo("Language: " + (Vietnamese ? "tiếng Việt" : "English"));
        }

        private void OnDestroy()
        {
            if (harmony != null) harmony.UnpatchSelf();
        }
    }
}
