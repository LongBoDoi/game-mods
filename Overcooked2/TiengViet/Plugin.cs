using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace TiengViet
{
    /// <summary>English and Vietnamese are set by the mod; GameDefault leaves the game on Steam's
    /// language and the mod out of the way, as if it were not installed.</summary>
    internal enum GameLanguage { English, Vietnamese, GameDefault }

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
        public const string Version = "1.1.0";

        /// <summary>The AppDomain slot every mod in the collection reads: "vi" or "en".</summary>
        internal const string Slot = "com.manhl.overcooked2.language";

        internal static ManualLogSource Log;
        internal static ConfigEntry<GameLanguage> Language;
        internal static ConfigEntry<TitleFont> Titles;

        internal static bool Vietnamese { get { return Language != null && Language.Value == GameLanguage.Vietnamese; } }

        /// <summary>"Default (Steam)": the game's own language and no changes from this mod, apart
        /// from the language row, so the player can switch back.</summary>
        internal static bool Off { get { return Language != null && Language.Value == GameLanguage.GameDefault; } }

        /// <summary>How each choice reads in the language row and MOD Settings.</summary>
        internal static string LanguageLabel(object value)
        {
            if (!(value is GameLanguage)) return Convert.ToString(value);
            switch ((GameLanguage)value)
            {
                case GameLanguage.English: return "English";
                case GameLanguage.Vietnamese: return "Tiếng Việt";
                default: return "Default (Steam)";
            }
        }

        private Harmony harmony;

        private void Awake()
        {
            Log = Logger;
            Language = Config.Bind("General", "Language", GameLanguage.GameDefault, new ConfigDescription(
                "Ngôn ngữ của game và các mod trong bộ này. Đổi được ngay trong game: Cài đặt → Cài đặt trò chơi → NGÔN NGỮ. " +
                "GameDefault (Mặc định) giữ ngôn ngữ Steam của game và tắt mọi thay đổi của mod này, như khi chưa cài.\n" +
                "Language of the game and of this collection's mods. Also in game: Settings → Game Settings → LANGUAGE. " +
                "GameDefault keeps the game on its Steam language and turns off everything this mod changes, as if it were not installed.",
                null, new ConfigurationManagerAttributes { ObjToStr = LanguageLabel, Order = 0 }));
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
            Patch(typeof(LanguageGet), "the game's language");
            Patch(typeof(LanguageRow), "the language option in Game Settings");
            Patch(typeof(HeaderFont.TextSet), "the title font fix");
            Patch(typeof(HeaderFont.TextEnable), "the title font fix");

            Language.SettingChanged += delegate { Apply(); };
            Titles.SettingChanged += delegate { HeaderFont.Reset(); HeaderFont.Sweep(); };
            Log.LogInfo("Tiếng Việt " + Version + ": " + LanguageLabel(Language.Value) + ", " +
                Translations.Count + " game texts and " + ModTexts.Count + " texts of other mods translated.");
        }

        /// <summary>Every plugin has loaded by now, so other mods' types can be found.</summary>
        private void Start()
        {
            try { HeaderFont.PatchFontReaders(harmony); }
            catch (Exception error) { Log.LogError("Could not patch MOD Settings' font check: " + error); }
            try { ModTexts.Patch(harmony); }
            catch (Exception error) { Log.LogError("Could not patch other mods' text tables: " + error); }
            // Loaded into a running game by Mod Updater: take over what the older copy left.
            try
            {
                int titles = HeaderFont.Adopt();
                int rows = LanguageRow.Adopt();
                if (titles + rows > 0)
                {
                    Log.LogInfo("Took over " + titles + " titles and " + rows + " language rows from the previous version.");
                    HeaderFont.Sweep();
                    Refresh.All();
                }
            }
            catch (Exception error) { Log.LogError("Could not take over from the previous version: " + error); }
        }

        private void Patch(Type patch, string what)
        {
            try { harmony.PatchAll(patch); }
            catch (Exception error) { Log.LogError("Could not patch " + what + ": " + error); }
        }

        private static void Publish()
        {
            // Nothing for Default: each mod then shows the language it has without TiengViet.
            AppDomain.CurrentDomain.SetData(Slot, Off ? null : Vietnamese ? "vi" : "en");
        }

        /// <summary>Switches everything on screen to the language just chosen.</summary>
        internal static void Apply()
        {
            Publish();
            Refresh.All();
            HeaderFont.Sweep();
            ModTexts.Relabel();
            LanguageRow.SyncAll();
            Log.LogInfo("Language: " + LanguageLabel(Language.Value));
        }

        private void OnDestroy()
        {
            if (harmony != null) harmony.UnpatchSelf();
        }
    }
}
