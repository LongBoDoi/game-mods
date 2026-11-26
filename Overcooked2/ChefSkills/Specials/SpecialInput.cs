using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace ChefSkills
{
    /// <summary>
    /// MOD Settings (OC2ModSettings) and BepInEx's ConfigurationManager read these fields by name
    /// from a config entry's tags: the name shown, and how the value is written.
    /// </summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public string DispName;
        public Func<object, string> ObjToStr;
        public int? Order;
        public bool? Browsable;
    }

    /// <summary>
    /// A controller button a binding can use. None: no button (for the skill button, the game's
    /// EMOTE, as before 4.4.0); Emote: the game's EMOTE button, chosen on purpose. Settings are
    /// saved by name, so new entries go at the end.
    /// </summary>
    internal enum PadKey { None, A, B, X, Y, LB, RB, LT, RT, Back, LS, RS, DPadUp, DPadDown, DPadLeft, DPadRight, Emote }

    /// <summary>Which binding a capture on the skill screen sets.</summary>
    internal enum CaptureTarget { Skill, Details, Switch }

    /// <summary>
    /// The skill key (uses a chef's active special) and the skill info key (shows or hides that
    /// player's skill details in a level). Each has a keyboard key (<c>[Controls] SkillKey</c>,
    /// <c>DetailsKey</c>) and a controller button (<c>SkillButton</c>, <c>DetailsButton</c>), so
    /// players on the keyboard and on controllers keep their own. Any key or button works,
    /// shoulders, triggers, d-pad and stick clicks included. Both are set by pressing them: on the
    /// skill screen's key rows, or in MOD Settings.
    ///
    /// A controller button is read from each player's own controller through the game's input
    /// layer (and, on a controller shared by two players, only for the player whose half has it),
    /// so local players never trigger each other's chef. The skill key's default (None) is the
    /// game's EMOTE button; the right-hand player of a shared keyboard keeps EMOTE too.
    ///
    /// Also here: each player's LOCK IN button on the skill screen, chosen so it is never one of
    /// that player's other menu buttons.
    /// </summary>
    internal static class SpecialInput
    {
        private const int FirstJoystickCode = (int)KeyCode.JoystickButton0;
        private const int ButtonsPerJoystick = 20;

        private static ConfigEntry<KeyCode> s_key;
        private static ConfigEntry<KeyCode> s_detailsKey;
        private static ConfigEntry<PadKey> s_button;
        private static ConfigEntry<PadKey> s_detailsButton;
        private static ConfigEntry<KeyCode> s_switchKey;
        private static ConfigEntry<KeyCode> s_switchKeyRight;
        private static ConfigEntry<PadKey> s_switchButton;
        private static ConfigEntry<int> s_inputLayout;
        private static ConfigEntry<bool> s_switchAfterUse;

        /// <summary>After using one of two actives, select the other one by itself.</summary>
        internal static bool SwitchAfterUse
        {
            get { return s_switchAfterUse != null && s_switchAfterUse.Value; }
        }

        /// <summary>The controller defaults since 4.4.0: SKILL on RB, SWITCH on RT (mirrored on a left half).</summary>
        private const PadKey DefaultSkillButton = PadKey.RB, DefaultSwitchButton = PadKey.RT;
        private const int CurrentInputLayout = 2;

        // When a default button is one the game already uses for this player, the next of these
        // that is free on their half of the controller is used instead (as LOCK IN does).
        private static readonly ControlPadInput.Button[] SkillFallbacks =
        {
            ControlPadInput.Button.RB, ControlPadInput.Button.LB, ControlPadInput.Button.RTrigger, ControlPadInput.Button.LTrigger,
            ControlPadInput.Button.RightAnalog, ControlPadInput.Button.LeftAnalog,
            ControlPadInput.Button.DPadUp, ControlPadInput.Button.DPadRight,
        };
        private static readonly ControlPadInput.Button[] SwitchFallbacks =
        {
            ControlPadInput.Button.RTrigger, ControlPadInput.Button.LTrigger, ControlPadInput.Button.RB, ControlPadInput.Button.LB,
            ControlPadInput.Button.LeftAnalog, ControlPadInput.Button.RightAnalog,
            ControlPadInput.Button.DPadDown, ControlPadInput.Button.DPadLeft,
        };

        // Keyboard SWITCH KEY when none is set: the first of these the game doesn't use (left or
        // whole keyboard; the right-hand player of a shared keyboard).
        private static readonly KeyCode[] SwitchKeyChoices =
        {
            KeyCode.Q, KeyCode.R, KeyCode.F, KeyCode.Z, KeyCode.X, KeyCode.C, KeyCode.G,
        };
        private static readonly KeyCode[] SwitchKeyRightChoices =
        {
            KeyCode.RightControl, KeyCode.Period, KeyCode.Comma, KeyCode.Semicolon, KeyCode.Quote, KeyCode.O, KeyCode.U,
            KeyCode.Keypad0,
        };

        // The game's button for each PadKey.
        private static readonly ControlPadInput.Button[] PadButtons =
        {
            ControlPadInput.Button.Invalid, ControlPadInput.Button.A, ControlPadInput.Button.B, ControlPadInput.Button.X,
            ControlPadInput.Button.Y, ControlPadInput.Button.LB, ControlPadInput.Button.RB, ControlPadInput.Button.LTrigger,
            ControlPadInput.Button.RTrigger, ControlPadInput.Button.Back, ControlPadInput.Button.LeftAnalog,
            ControlPadInput.Button.RightAnalog, ControlPadInput.Button.DPadUp, ControlPadInput.Button.DPadDown,
            ControlPadInput.Button.DPadLeft, ControlPadInput.Button.DPadRight,
        };

        // Unity / DirectInput button numbers, as older versions stored a controller button in the key setting.
        private static readonly ControlPadInput.Button[] LegacyButtons =
        {
            ControlPadInput.Button.A, ControlPadInput.Button.B, ControlPadInput.Button.X, ControlPadInput.Button.Y,
            ControlPadInput.Button.LB, ControlPadInput.Button.RB, ControlPadInput.Button.Back, ControlPadInput.Button.Start,
            ControlPadInput.Button.LeftAnalog, ControlPadInput.Button.RightAnalog,
        };

        // Each half of a controller shared by two players (the game's PadSidednessDefinition).
        private static readonly ControlPadInput.Button[] LeftSide =
        {
            ControlPadInput.Button.LB, ControlPadInput.Button.LTrigger, ControlPadInput.Button.Back, ControlPadInput.Button.LeftAnalog,
            ControlPadInput.Button.DPadLeft, ControlPadInput.Button.DPadRight, ControlPadInput.Button.DPadUp, ControlPadInput.Button.DPadDown,
        };
        private static readonly ControlPadInput.Button[] RightSide =
        {
            ControlPadInput.Button.RB, ControlPadInput.Button.RTrigger, ControlPadInput.Button.A, ControlPadInput.Button.B,
            ControlPadInput.Button.X, ControlPadInput.Button.Y, ControlPadInput.Button.Start, ControlPadInput.Button.RightAnalog,
        };

        internal static void Bind(ConfigFile config)
        {
            s_key = config.Bind("Controls", "SkillKey", KeyCode.None, new ConfigDescription(
                "Keyboard: the key that uses your active special (any key). None = the game's EMOTE key. " +
                "Press Rebind, then the key; Backspace clears it. Also set on the skill screen's SKILL KEY row.",
                null,
                new ConfigurationManagerAttributes { DispName = "Skill key (keyboard)", ObjToStr = FormatSkillKey, Order = 0 }));
            s_button = config.Bind("Controls", "SkillButton", DefaultSkillButton, new ConfigDescription(
                "Controller: the button that uses your selected active special (any button: shoulders, triggers, d-pad and stick clicks too). " +
                "Default RB (LB on the left half of a shared controller; another free button if the game uses it, e.g. to switch chefs alone). " +
                "Emote = the game's EMOTE button (Y). Each player's own controller is read. " +
                "Also set on the skill screen's SKILL KEY row by pressing the button.",
                null,
                new ConfigurationManagerAttributes { DispName = "Skill button (controller)", ObjToStr = FormatSkillButton, Order = -1 }));
            s_detailsKey = config.Bind("Controls", "DetailsKey", KeyCode.Tab, new ConfigDescription(
                "Keyboard: the key that shows or hides your skills' details during a level (any key). " +
                "Press Rebind, then the key; Backspace clears it (no key). Also set on the skill screen's INFO KEY row.",
                null,
                new ConfigurationManagerAttributes { DispName = "Skill info key (keyboard)", ObjToStr = FormatDetailsKey, Order = -2 }));
            s_detailsButton = config.Bind("Controls", "DetailsButton", PadKey.None, new ConfigDescription(
                "Controller: the button that shows or hides your skills' details during a level (any button; Back or a stick " +
                "click work well). None = no button. Also set on the skill screen's INFO KEY row by pressing the button.",
                null,
                new ConfigurationManagerAttributes { DispName = "Skill info button (controller)", ObjToStr = FormatDetailsButton, Order = -3 }));

            s_switchButton = config.Bind("Controls", "SwitchButton", DefaultSwitchButton, new ConfigDescription(
                "Controller: the button that switches between your two active specials. Default RT (LT on the left half " +
                "of a shared controller; another free button if the game uses it). None = no button. " +
                "Also set on the skill screen's SWITCH KEY row by pressing the button.",
                null,
                new ConfigurationManagerAttributes { DispName = "Switch button (controller)", ObjToStr = FormatSwitchButton, Order = -4 }));
            s_switchKey = config.Bind("Controls", "SwitchKey", KeyCode.None, new ConfigDescription(
                "Keyboard: the key that switches between your two active specials. None = the first of Q, R, F, Z, X, C, G " +
                "the game doesn't use. Also set on the skill screen's SWITCH KEY row.",
                null,
                new ConfigurationManagerAttributes { DispName = "Switch key (keyboard)", ObjToStr = FormatSwitchKey, Order = -5 }));
            s_switchKeyRight = config.Bind("Controls", "SwitchKeyRight", KeyCode.None, new ConfigDescription(
                "Keyboard, the right-hand player of a shared keyboard: the switch key. None = the first of R CONTROL, " +
                "PERIOD, COMMA, SEMICOLON, QUOTE, O, U, NUM 0 the game doesn't use. Also set on that player's SWITCH KEY row.",
                null,
                new ConfigurationManagerAttributes { DispName = "Switch key (right-hand keyboard player)", ObjToStr = FormatSwitchKeyRight, Order = -6 }));
            s_inputLayout = config.Bind("Controls", "InputLayout", 0, new ConfigDescription(
                "Which version of the default buttons this file has been moved to (set by the mod).",
                null,
                new ConfigurationManagerAttributes { Browsable = false }));
            s_switchAfterUse = config.Bind("Controls", "SwitchAfterUse", false, new ConfigDescription(
                "With two actives: after using one, the SKILL KEY moves to the other by itself.",
                null,
                new ConfigurationManagerAttributes { DispName = "Switch after use", Order = -7 }));
            MigrateLayout();

            // Older versions kept a controller button in the key setting: move it to the button setting.
            Migrate(s_key, s_button);
            Migrate(s_detailsKey, s_detailsButton);
            s_key.SettingChanged += delegate { Migrate(s_key, s_button); };
            s_detailsKey.SettingChanged += delegate { Migrate(s_detailsKey, s_detailsButton); };
        }

        /// <summary>
        /// Once, on the first start of 4.4.0: a skill button still at the old default (None, the
        /// game's EMOTE) moves to RB. A player who chose EMOTE on purpose can't be told apart from
        /// the old default; they set it back on the SKILL KEY row (the release notes say so).
        /// </summary>
        private static void MigrateLayout()
        {
            if (s_inputLayout == null || s_inputLayout.Value >= CurrentInputLayout) return;
            if (s_button != null && s_button.Value == PadKey.None)
            {
                s_button.Value = DefaultSkillButton;
                Plugin.Log.LogInfo("Nút kỹ năng (tay cầm) chuyển từ BIỂU CẢM sang " + DefaultSkillButton + " (4.4.0). Chọn lại BIỂU CẢM ở hàng PHÍM KỸ NĂNG nếu muốn.");
            }
            s_inputLayout.Value = CurrentInputLayout;
            Save(s_inputLayout.ConfigFile);
        }

        private static void Migrate(ConfigEntry<KeyCode> key, ConfigEntry<PadKey> button)
        {
            if (key == null || button == null || !IsJoystick(key.Value)) return;
            int index = ((int)key.Value - FirstJoystickCode) % ButtonsPerJoystick;
            ControlPadInput.Button legacy = index < LegacyButtons.Length ? LegacyButtons[index] : ControlPadInput.Button.Invalid;
            PadKey moved = ToPadKey(legacy);
            key.Value = KeyCode.None;
            if (moved != PadKey.None) button.Value = moved;
            Plugin.Log.LogInfo("Đã chuyển nút tay cầm sang cài đặt riêng cho tay cầm: " + key.Definition.Key + " -> " + button.Definition.Key + " = " + moved);
        }

        // --- settings as shown ------------------------------------------------------------------

        private static string FormatSkillKey(object value)
        {
            KeyCode key = value is KeyCode ? (KeyCode)value : KeyCode.None;
            if (key == KeyCode.None) return Lang.T("EMOTE", "BIỂU CẢM");
            return IsJoystick(key) ? Lang.T("Controller button (moves to Skill button)", "Nút tay cầm (chuyển sang Nút kỹ năng)") : KeyName(key);
        }

        private static string FormatDetailsKey(object value)
        {
            KeyCode key = value is KeyCode ? (KeyCode)value : KeyCode.None;
            if (key == KeyCode.None) return Lang.T("None", "Không có");
            return IsJoystick(key) ? Lang.T("Controller button (moves to Skill info button)", "Nút tay cầm (chuyển sang Nút xem kỹ năng)") : KeyName(key);
        }

        private static string FormatSkillButton(object value)
        {
            PadKey key = value is PadKey ? (PadKey)value : PadKey.None;
            return key == PadKey.None || key == PadKey.Emote ? Lang.T("EMOTE (Y)", "BIỂU CẢM (Y)") : ButtonName(ToButton(key));
        }

        private static string FormatSwitchButton(object value)
        {
            PadKey key = value is PadKey ? (PadKey)value : PadKey.None;
            return key == PadKey.None || key == PadKey.Emote ? Lang.T("None", "Không có") : ButtonName(ToButton(key));
        }

        private static string FormatSwitchKey(object value)
        {
            KeyCode key = value is KeyCode ? (KeyCode)value : KeyCode.None;
            return key == KeyCode.None ? Lang.T("Automatic", "Tự động") + " (" + KeyName(AutoSwitchKey(false)) + ")" : KeyName(key);
        }

        private static string FormatSwitchKeyRight(object value)
        {
            KeyCode key = value is KeyCode ? (KeyCode)value : KeyCode.None;
            return key == KeyCode.None ? Lang.T("Automatic", "Tự động") + " (" + KeyName(AutoSwitchKey(true)) + ")" : KeyName(key);
        }

        private static string FormatDetailsButton(object value)
        {
            PadKey key = value is PadKey ? (PadKey)value : PadKey.None;
            return key == PadKey.None ? Lang.T("None", "Không có") : ButtonName(ToButton(key));
        }

        // --- the bindings -------------------------------------------------------------------------

        private static KeyCode Key
        {
            get { return s_key != null && !IsJoystick(s_key.Value) ? s_key.Value : KeyCode.None; }
        }

        private static KeyCode DetailsKey
        {
            get { return s_detailsKey != null && !IsJoystick(s_detailsKey.Value) ? s_detailsKey.Value : KeyCode.None; }
        }

        /// <summary>The skill button as set (Invalid: the game's EMOTE).</summary>
        private static ControlPadInput.Button Button
        {
            get { return s_button != null ? ToButton(s_button.Value) : ControlPadInput.Button.Invalid; }
        }

        private static ControlPadInput.Button DetailsButton
        {
            get { return s_detailsButton != null ? ToButton(s_detailsButton.Value) : ControlPadInput.Button.Invalid; }
        }

        private static bool IsJoystick(KeyCode key)
        {
            return (int)key >= FirstJoystickCode;
        }

        private static ControlPadInput.Button ToButton(PadKey key)
        {
            int index = (int)key;
            return index > 0 && index < PadButtons.Length ? PadButtons[index] : ControlPadInput.Button.Invalid;
        }

        private static PadKey ToPadKey(ControlPadInput.Button button)
        {
            int index = Array.IndexOf(PadButtons, button);
            return index > 0 ? (PadKey)index : PadKey.None;
        }

        // --- players' controls --------------------------------------------------------------------

        /// <summary>True when the player plays on the keyboard (false if unknown).</summary>
        internal static bool IsKeyboard(PlayerInputLookup.Player player)
        {
            bool keyboard;
            PadSide side;
            ControlPadInput.PadNum pad;
            return Device(player, out keyboard, out side, out pad) && keyboard;
        }

        /// <summary>The player's controls: keyboard or a controller, which half of it (Both when not shared), and its number.</summary>
        private static bool Device(PlayerInputLookup.Player player, out bool keyboard, out PadSide side, out ControlPadInput.PadNum pad)
        {
            keyboard = false;
            side = PadSide.Both;
            pad = ControlPadInput.PadNum.Invalid;
            try
            {
                GameInputConfig config = PlayerInputLookup.GetInputConfig();
                PlayerGameInput input = config != null ? config.GetInputData(player) : null;
                if (input == null) return false;
                pad = input.Pad;
                keyboard = KeyboardUtils.IsKeyboard(input);
                side = input.Side;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Is this button on the player's half of a shared controller (always, on an unshared one)?</summary>
        private static bool OnSide(PadSide side, ControlPadInput.Button button)
        {
            if (button == ControlPadInput.Button.Invalid) return false;
            if (side == PadSide.Left) return Array.IndexOf(LeftSide, button) >= 0;
            if (side == PadSide.Right) return Array.IndexOf(RightSide, button) >= 0;
            return true;
        }

        private static bool PadDown(ControlPadInput.PadNum pad, ControlPadInput.Button button)
        {
            if (button == ControlPadInput.Button.Invalid) return false;
            try
            {
                return Singleton<PCPadInputProvider>.Get().IsDown(pad, button);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // --- which controller buttons a player really gets ---------------------------------------

        // Players playing alone with two chefs (the game's chef switch is then in use).
        private static readonly HashSet<PlayerInputLookup.Player> s_solo = new HashSet<PlayerInputLookup.Player>();

        /// <summary>This player controls two chefs (set by the skill screen and the kitchen).</summary>
        internal static void SetSolo(PlayerInputLookup.Player player, bool solo)
        {
            if (solo) s_solo.Add(player);
            else s_solo.Remove(player);
        }

        // Per player, per frame: the skill and switch buttons worked out (they are read every frame).
        private static readonly Dictionary<PlayerInputLookup.Player, KeyValuePair<int, ControlPadInput.Button[]>> s_resolved =
            new Dictionary<PlayerInputLookup.Player, KeyValuePair<int, ControlPadInput.Button[]>>();

        /// <summary>
        /// This controller player's skill button ([0]; Invalid = the game's EMOTE) and switch
        /// button ([1]; Invalid = none), on their half of the controller.
        /// </summary>
        private static ControlPadInput.Button[] PadButtonsFor(PlayerInputLookup.Player player, PadSide side)
        {
            KeyValuePair<int, ControlPadInput.Button[]> cached;
            if (s_resolved.TryGetValue(player, out cached) && cached.Key == Time.frameCount) return cached.Value;
            HashSet<ControlPadInput.Button> game = GameButtons(player);
            PadKey skillSetting = s_button != null ? s_button.Value : DefaultSkillButton;
            PadKey switchSetting = s_switchButton != null ? s_switchButton.Value : DefaultSwitchButton;
            ControlPadInput.Button skill = skillSetting == PadKey.None || skillSetting == PadKey.Emote
                ? ControlPadInput.Button.Invalid
                : Resolve(side, skillSetting, DefaultSkillButton, SkillFallbacks, game, ControlPadInput.Button.Invalid);
            ControlPadInput.Button change = switchSetting == PadKey.None || switchSetting == PadKey.Emote
                ? ControlPadInput.Button.Invalid
                : Resolve(side, switchSetting, DefaultSwitchButton, SwitchFallbacks, game,
                    skill == ControlPadInput.Button.Invalid ? ControlPadInput.Button.Y : skill);
            var result = new[] { skill, change };
            s_resolved[player] = new KeyValuePair<int, ControlPadInput.Button[]>(Time.frameCount, result);
            return result;
        }

        /// <summary>
        /// A setting as a button on the player's half: mirrored (RB to LB, RT to LT) when it is on
        /// the other half; and when it is still the default and the game uses it (or it is
        /// <paramref name="avoid"/>), the first free fallback instead.
        /// </summary>
        private static ControlPadInput.Button Resolve(PadSide side, PadKey setting, PadKey fallbackFor,
            ControlPadInput.Button[] fallbacks, HashSet<ControlPadInput.Button> game, ControlPadInput.Button avoid)
        {
            ControlPadInput.Button button = ToButton(setting);
            if (!OnSide(side, button)) button = Mirror(button);
            if (!OnSide(side, button)) button = ControlPadInput.Button.Invalid;
            bool isDefault = setting == fallbackFor;
            // A button the player chose is kept even if the game uses it; the default steps aside.
            // One that can't be used here (the other half, the other binding) falls back too.
            if (button != ControlPadInput.Button.Invalid && button != avoid && !(isDefault && game.Contains(button))) return button;
            foreach (ControlPadInput.Button choice in fallbacks)
            {
                ControlPadInput.Button candidate = OnSide(side, choice) ? choice : Mirror(choice);
                if (OnSide(side, candidate) && candidate != avoid && !game.Contains(candidate)) return candidate;
            }
            return ControlPadInput.Button.Invalid;
        }

        private static ControlPadInput.Button Mirror(ControlPadInput.Button button)
        {
            switch (button)
            {
                case ControlPadInput.Button.RB: return ControlPadInput.Button.LB;
                case ControlPadInput.Button.LB: return ControlPadInput.Button.RB;
                case ControlPadInput.Button.RTrigger: return ControlPadInput.Button.LTrigger;
                case ControlPadInput.Button.LTrigger: return ControlPadInput.Button.RTrigger;
                default: return button;
            }
        }

        /// <summary>
        /// The controller buttons the game uses for this player in the kitchen: pick up, chop,
        /// dash, emote and pause, and, when they play alone, the chef switch.
        /// </summary>
        private static HashSet<ControlPadInput.Button> GameButtons(PlayerInputLookup.Player player)
        {
            var used = new HashSet<ControlPadInput.Button> { ControlPadInput.Button.Start };
            AddReal(used, player, AmbiPadButton.One);
            AddReal(used, player, AmbiPadButton.Two);
            AddReal(used, player, AmbiPadButton.Three);
            AddReal(used, player, AmbiPadButton.Four);
            if (s_solo.Contains(player)) AddReal(used, player, AmbiPadButton.Five);
            return used;
        }

        // --- the game's keyboard keys --------------------------------------------------------------

        private static HashSet<string> s_gameKeys;
        private static float s_gameKeysAt = -100f;

        /// <summary>Every key the game's keyboard bindings use (names, lower case), refreshed now and then.</summary>
        private static HashSet<string> GameKeys()
        {
            if (s_gameKeys != null && Time.unscaledTime - s_gameKeysAt < 5f) return s_gameKeys;
            s_gameKeysAt = Time.unscaledTime;
            var keys = new HashSet<string>();
            try
            {
                var bindings = HarmonyLib.AccessTools.Field(typeof(PCPadInputProvider), "m_UserKeyboardBindings").GetValue(null) as KeyboardBindings;
                if (bindings != null)
                    foreach (KeyboardBindingSet set in new[] { bindings.m_CombinedKeyboard, bindings.m_SplitKeyboard })
                    {
                        if (set == null) continue;
                        AddKeys(keys, set.m_ButtonBindings);
                        AddKeys(keys, set.m_PositiveValueBindings);
                        AddKeys(keys, set.m_NegativeValueBindings);
                    }
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không đọc được phím của game: " + error.Message);
            }
            s_gameKeys = keys;
            return keys;
        }

        private static void AddKeys<T>(HashSet<string> into, Dictionary<T, List<InControl.Key>> map)
        {
            if (map == null) return;
            foreach (List<InControl.Key> list in map.Values)
                if (list != null)
                    foreach (InControl.Key key in list) into.Add(NormalKeyName(key.ToString()));
        }

        /// <summary>A key name the game's and Unity's spellings share ("alpha1" and "key1" both "1").</summary>
        private static string NormalKeyName(string name)
        {
            name = name.ToLowerInvariant();
            if (name.StartsWith("alpha")) return name.Substring(5);
            if (name.StartsWith("key") && name.Length == 4 && char.IsDigit(name[3])) return name.Substring(3);
            if (name.StartsWith("keypad")) return "pad" + name.Substring(6);
            return name;
        }

        /// <summary>The keyboard SWITCH KEY used when none is set: the first choice the game and the other keys leave free.</summary>
        private static KeyCode AutoSwitchKey(bool right)
        {
            HashSet<string> game = GameKeys();
            // Both players of a shared keyboard press the same keys: keep off every key either of
            // them has (the skill and info keys, and the other side's switch key; the left side's
            // own pick never depends on the right's, so this can't loop).
            KeyCode otherSwitch = right ? SwitchKeyFor(false) : ConfiguredSwitchKey(true);
            foreach (KeyCode key in right ? SwitchKeyRightChoices : SwitchKeyChoices)
            {
                if (game.Contains(NormalKeyName(key.ToString()))) continue;
                if (key == Key || key == DetailsKey || key == otherSwitch) continue;
                return key;
            }
            return KeyCode.None;
        }

        /// <summary>The switch key as set for one side (None when automatic).</summary>
        private static KeyCode ConfiguredSwitchKey(bool right)
        {
            ConfigEntry<KeyCode> entry = right ? s_switchKeyRight : s_switchKey;
            return entry != null && !IsJoystick(entry.Value) ? entry.Value : KeyCode.None;
        }

        /// <summary>The keyboard SWITCH KEY for the left (or only) player, or the right-hand one.</summary>
        private static KeyCode SwitchKeyFor(bool right)
        {
            KeyCode key = ConfiguredSwitchKey(right);
            return key != KeyCode.None ? key : AutoSwitchKey(right);
        }

        // --- the skill key -----------------------------------------------------------------------

        /// <summary>Is this local chef's skill button held now?</summary>
        internal static bool IsDown(ChefState state)
        {
            if (state == null || state.Controls == null || state.Provider == null) return false;
            bool keyboard;
            PadSide side;
            ControlPadInput.PadNum pad;
            PlayerInputLookup.Player player = state.Provider.GetID();
            if (Device(player, out keyboard, out side, out pad))
            {
                if (keyboard && side != PadSide.Right && Key != KeyCode.None) return Input.GetKey(Key);
                if (!keyboard)
                {
                    ControlPadInput.Button button = PadButtonsFor(player, side)[0];
                    if (button != ControlPadInput.Button.Invalid) return PadDown(pad, button);
                }
            }
            PlayerControls.ControlSchemeData scheme = state.Controls.ControlScheme;
            return scheme != null && scheme.m_curseButton != null && scheme.m_curseButton.IsDown();
        }

        /// <summary>What to print for this player's skill button ("Y", "Q", "RT", "EMOTE").</summary>
        internal static string NameFor(PlayerInputLookup.Player player)
        {
            bool keyboard;
            PadSide side;
            ControlPadInput.PadNum pad;
            bool known = Device(player, out keyboard, out side, out pad);
            if (known)
            {
                if (keyboard && side != PadSide.Right && Key != KeyCode.None) return KeyName(Key);
                if (!keyboard)
                {
                    ControlPadInput.Button button = PadButtonsFor(player, side)[0];
                    if (button != ControlPadInput.Button.Invalid) return ButtonName(button);
                }
            }
            return known && !keyboard ? Lang.T("EMOTE (Y)", "BIỂU CẢM (Y)") : Lang.T("EMOTE", "BIỂU CẢM");
        }

        // --- the switch key ----------------------------------------------------------------------

        /// <summary>Is this local chef's switch key (or button) held now?</summary>
        internal static bool IsSwitchDown(ChefState state)
        {
            if (state == null || state.Provider == null) return false;
            bool keyboard;
            PadSide side;
            ControlPadInput.PadNum pad;
            PlayerInputLookup.Player player = state.Provider.GetID();
            if (!Device(player, out keyboard, out side, out pad)) return false;
            if (keyboard)
            {
                KeyCode key = SwitchKeyFor(side == PadSide.Right);
                return key != KeyCode.None && Input.GetKey(key);
            }
            ControlPadInput.Button button = PadButtonsFor(player, side)[1];
            return button != ControlPadInput.Button.Invalid && PadDown(pad, button);
        }

        /// <summary>What to print for this player's switch key ("RT", "Q"), or null when they have none.</summary>
        internal static string SwitchNameFor(PlayerInputLookup.Player player)
        {
            bool keyboard;
            PadSide side;
            ControlPadInput.PadNum pad;
            if (!Device(player, out keyboard, out side, out pad)) return null;
            if (keyboard)
            {
                KeyCode key = SwitchKeyFor(side == PadSide.Right);
                return key != KeyCode.None ? KeyName(key) : null;
            }
            ControlPadInput.Button button = PadButtonsFor(player, side)[1];
            return button != ControlPadInput.Button.Invalid ? ButtonName(button) : null;
        }

        // --- the skill info key ------------------------------------------------------------------

        private static readonly Dictionary<PlayerInputLookup.Player, bool> s_detailsWasDown = new Dictionary<PlayerInputLookup.Player, bool>();
        private static readonly Dictionary<PlayerInputLookup.Player, int> s_detailsFrame = new Dictionary<PlayerInputLookup.Player, int>();
        private static readonly Dictionary<PlayerInputLookup.Player, bool> s_detailsResult = new Dictionary<PlayerInputLookup.Player, bool>();

        /// <summary>
        /// Was this player's skill info key just pressed (their keyboard key, or the button on their
        /// own controller)? Call it every frame for each local player, so a press is counted once.
        /// </summary>
        internal static bool DetailsPressed(PlayerInputLookup.Player player)
        {
            int frame;
            bool result;
            if (s_detailsFrame.TryGetValue(player, out frame) && frame == Time.frameCount && s_detailsResult.TryGetValue(player, out result))
                return result;
            bool down = DetailsDown(player);
            bool was;
            s_detailsWasDown.TryGetValue(player, out was);
            result = down && !was;
            s_detailsWasDown[player] = down;
            s_detailsFrame[player] = Time.frameCount;
            s_detailsResult[player] = result;
            return result;
        }

        private static bool DetailsDown(PlayerInputLookup.Player player)
        {
            bool keyboard;
            PadSide side;
            ControlPadInput.PadNum pad;
            if (!Device(player, out keyboard, out side, out pad)) return false;
            // Both players of a shared keyboard use the key: it only opens each one's own card.
            if (keyboard) return DetailsKey != KeyCode.None && Input.GetKey(DetailsKey);
            return OnSide(side, DetailsButton) && PadDown(pad, DetailsButton);
        }

        /// <summary>This player's skill info key to print ("TAB", "BACK"), or null when they have none.</summary>
        internal static string DetailsNameFor(PlayerInputLookup.Player player)
        {
            bool keyboard;
            PadSide side;
            ControlPadInput.PadNum pad;
            if (!Device(player, out keyboard, out side, out pad)) return null;
            if (keyboard) return DetailsKey != KeyCode.None ? KeyName(DetailsKey) : null;
            return OnSide(side, DetailsButton) ? ButtonName(DetailsButton) : null;
        }

        // --- the skill screen's LOCK IN button ----------------------------------------------------

        // Tried in this order for a controller player's LOCK IN: the first on their half of the
        // controller that none of their other menu buttons use.
        private static readonly ControlPadInput.Button[] LockInChoices =
        {
            ControlPadInput.Button.X, ControlPadInput.Button.Y, ControlPadInput.Button.RB, ControlPadInput.Button.LB,
            ControlPadInput.Button.RTrigger, ControlPadInput.Button.LTrigger, ControlPadInput.Button.Back,
            ControlPadInput.Button.RightAnalog, ControlPadInput.Button.LeftAnalog, ControlPadInput.Button.B, ControlPadInput.Button.A,
        };

        /// <summary>The controller buttons the game gives this player for SELECT, BACK, HIDE and pause, plus the d-pad.</summary>
        private static HashSet<ControlPadInput.Button> MenuButtons(PlayerInputLookup.Player player)
        {
            var used = new HashSet<ControlPadInput.Button>
            {
                ControlPadInput.Button.DPadUp, ControlPadInput.Button.DPadDown,
                ControlPadInput.Button.DPadLeft, ControlPadInput.Button.DPadRight, ControlPadInput.Button.Start,
            };
            AddReal(used, player, AmbiPadButton.Confirm);
            AddReal(used, player, AmbiPadButton.Cancel);
            AddReal(used, player, AmbiPadButton.Four);
            AddReal(used, player, AmbiPadButton.Start);
            return used;
        }

        private static void AddReal(HashSet<ControlPadInput.Button> into, PlayerInputLookup.Player player, AmbiPadButton button)
        {
            try
            {
                GameInputConfig config = PlayerInputLookup.GetInputConfig();
                if (config == null) return;
                foreach (ControlPadInput.ButtonIdentifier id in config.GetRealButtons(player, button)) into.Add(id.button);
            }
            catch (Exception)
            {
            }
        }

        private static string RealNames(PlayerInputLookup.Player player, AmbiPadButton button)
        {
            var found = new HashSet<ControlPadInput.Button>();
            AddReal(found, player, button);
            var names = new List<string>();
            foreach (ControlPadInput.Button b in found) names.Add(b.ToString());
            return names.Count > 0 ? string.Join("/", names.ToArray()) : "-";
        }

        /// <summary>
        /// A controller player's LOCK IN button: X on a whole controller, or whichever free button
        /// their half of a shared one has. Invalid for a keyboard player (they use Enter).
        /// </summary>
        internal static ControlPadInput.Button LockInPadButton(PlayerInputLookup.Player player)
        {
            bool keyboard;
            PadSide side;
            ControlPadInput.PadNum pad;
            if (!Device(player, out keyboard, out side, out pad) || keyboard) return ControlPadInput.Button.Invalid;
            HashSet<ControlPadInput.Button> used = MenuButtons(player);
            // Nor the player's SKILL or SWITCH button.
            foreach (ControlPadInput.Button mine in PadButtonsFor(player, side)) used.Add(mine);
            foreach (ControlPadInput.Button choice in LockInChoices)
                if (OnSide(side, choice) && !used.Contains(choice)) return choice;
            return ControlPadInput.Button.Invalid;
        }

        /// <summary>The LOCK IN button to read for this player on the skill screen (never their SELECT).</summary>
        internal static ILogicalButton LockInButton(PlayerInputLookup.Player player)
        {
            bool keyboard;
            PadSide side;
            ControlPadInput.PadNum pad;
            bool known = Device(player, out keyboard, out side, out pad);
            if (known && keyboard)
                return new ComboLogicalButton(new ILogicalButton[]
                {
                    new LogicalKeycodeButton(KeyCode.Return, ControlPadInput.Button.Start),
                    new LogicalKeycodeButton(KeyCode.KeypadEnter, ControlPadInput.Button.Start)
                });
            ControlPadInput.Button button = known ? LockInPadButton(player) : ControlPadInput.Button.Invalid;
            if (button != ControlPadInput.Button.Invalid)
            {
                try
                {
                    return PlayerInputLookup.ConstructDebugBoundButton(pad, button);
                }
                catch (Exception error)
                {
                    Plugin.Log.LogDebug("Không tạo được nút chốt: " + error.Message);
                }
            }
            return PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UIResultsToggleProfile, player);
        }

        /// <summary>One line for the log: the player's controls and menu buttons on the skill screen.</summary>
        internal static string Describe(PlayerInputLookup.Player player)
        {
            bool keyboard;
            PadSide side;
            ControlPadInput.PadNum pad;
            if (!Device(player, out keyboard, out side, out pad)) return "không rõ thiết bị";
            if (keyboard) return "bàn phím" + (side != PadSide.Both ? " (" + side + ")" : "") + ": chọn = Space, chốt = Enter, đổi = " + SwitchKeyFor(side == PadSide.Right);
            ControlPadInput.Button lockIn = LockInPadButton(player);
            ControlPadInput.Button[] mine = PadButtonsFor(player, side);
            return "tay cầm " + pad + (side != PadSide.Both ? " (nửa " + side + ")" : "") +
                ": chọn = " + RealNames(player, AmbiPadButton.Confirm) + ", hủy = " + RealNames(player, AmbiPadButton.Cancel) +
                ", ẩn = " + RealNames(player, AmbiPadButton.Four) +
                ", chốt = " + (lockIn != ControlPadInput.Button.Invalid ? lockIn.ToString() : "nút X của game") +
                ", kỹ năng = " + (mine[0] != ControlPadInput.Button.Invalid ? mine[0].ToString() : "biểu cảm") +
                ", đổi = " + (mine[1] != ControlPadInput.Button.Invalid ? mine[1].ToString() : "không có") +
                ", đổi đầu bếp của game = " + RealNames(player, AmbiPadButton.Five);
        }

        // --- reading a pad directly (menus) -------------------------------------------------------

        /// <summary>
        /// A player's own controller read straight from the game's pad layer, for menus: the
        /// d-pad or left stick (as the game's menu directions read them), A and B. Each press
        /// counts once. Nothing for keyboard players or a pad shared by two players.
        /// </summary>
        internal sealed class PadNav
        {
            private readonly PlayerInputLookup.Player _player;
            private readonly bool[] _was = new bool[6];
            private bool _primed;

            internal PadNav(PlayerInputLookup.Player player)
            {
                _player = player;
            }

            internal const int Up = 0, Down = 1, Left = 2, Right = 3, Select = 4, Cancel = 5;

            /// <summary>Call once a frame: which of the six were just pressed.</summary>
            internal bool[] Poll()
            {
                var pressed = new bool[6];
                var down = new bool[6];
                bool keyboard;
                PadSide side;
                ControlPadInput.PadNum pad;
                if (!Device(_player, out keyboard, out side, out pad) || keyboard || pad == ControlPadInput.PadNum.Invalid || side != PadSide.Both)
                    return pressed;
                try
                {
                    PCPadInputProvider provider = Singleton<PCPadInputProvider>.Get();
                    if (provider == null) return pressed;
                    float stickY = provider.GetValue(pad, ControlPadInput.Value.LStickY);
                    float stickX = provider.GetValue(pad, ControlPadInput.Value.LStickX);
                    float dpadY = provider.GetValue(pad, ControlPadInput.Value.DPadY);
                    float dpadX = provider.GetValue(pad, ControlPadInput.Value.DPadX);
                    down[Up] = provider.IsDown(pad, ControlPadInput.Button.DPadUp) || stickY < -0.75f || dpadY < -0.75f;
                    down[Down] = provider.IsDown(pad, ControlPadInput.Button.DPadDown) || stickY > 0.75f || dpadY > 0.75f;
                    down[Left] = provider.IsDown(pad, ControlPadInput.Button.DPadLeft) || stickX < -0.75f || dpadX < -0.75f;
                    down[Right] = provider.IsDown(pad, ControlPadInput.Button.DPadRight) || stickX > 0.75f || dpadX > 0.75f;
                    down[Select] = provider.IsDown(pad, ControlPadInput.Button.A);
                    down[Cancel] = provider.IsDown(pad, ControlPadInput.Button.B);
                }
                catch (Exception)
                {
                    return pressed;
                }
                // The first reading only learns what is already held (the press that opened the menu).
                for (int i = 0; i < 6; i++)
                {
                    pressed[i] = _primed && down[i] && !_was[i];
                    _was[i] = down[i];
                }
                _primed = true;
                return pressed;
            }
        }

        // --- names ------------------------------------------------------------------------------

        internal static string KeyName(KeyCode key)
        {
            string name = key.ToString();
            if (name.StartsWith("Alpha")) return name.Substring(5);
            if (name.StartsWith("Keypad")) return "NUM " + name.Substring(6).ToUpperInvariant();
            if (name.StartsWith("Mouse")) return "MOUSE " + (int.Parse(name.Substring(5)) + 1);
            if (name.StartsWith("Left")) return "L " + name.Substring(4).ToUpperInvariant();
            if (name.StartsWith("Right")) return "R " + name.Substring(5).ToUpperInvariant();
            return name.ToUpperInvariant();
        }

        internal static string ButtonName(ControlPadInput.Button button)
        {
            switch (button)
            {
                case ControlPadInput.Button.Invalid: return "?";
                case ControlPadInput.Button.LTrigger: return "LT";
                case ControlPadInput.Button.RTrigger: return "RT";
                case ControlPadInput.Button.LeftAnalog: return "LS";
                case ControlPadInput.Button.RightAnalog: return "RS";
                case ControlPadInput.Button.DPadUp: return Lang.T("D-PAD UP", "D-PAD LÊN");
                case ControlPadInput.Button.DPadDown: return Lang.T("D-PAD DOWN", "D-PAD XUỐNG");
                case ControlPadInput.Button.DPadLeft: return Lang.T("D-PAD LEFT", "D-PAD TRÁI");
                case ControlPadInput.Button.DPadRight: return Lang.T("D-PAD RIGHT", "D-PAD PHẢI");
                default: return button.ToString().ToUpperInvariant();
            }
        }

        // --- rebinding on the skill screen -------------------------------------------------------

        /// <summary>
        /// One player waiting to press their new skill key, switch key or skill info key: a
        /// keyboard player sets the keyboard key, a controller player the controller button.
        /// </summary>
        internal sealed class Capture
        {
            internal readonly PlayerInputLookup.Player Player;
            internal readonly CaptureTarget Target;
            /// <summary>Setting the skill info key.</summary>
            internal bool Details
            {
                get { return Target == CaptureTarget.Details; }
            }
            /// <summary>A new key was saved.</summary>
            internal bool Saved { get; private set; }
            internal readonly bool Keyboard;
            internal readonly ControlPadInput.PadNum Pad;
            internal readonly PadSide Side;
            internal readonly float Started;
            private readonly HashSet<ControlPadInput.Button> _held = new HashSet<ControlPadInput.Button>();
            private readonly ILogicalButton[] _gameButtons;
            private bool _keysReleased;

            internal Capture(PlayerInputLookup.Player player, CaptureTarget target)
            {
                Player = player;
                Target = target;
                Started = Time.unscaledTime;
                bool keyboard;
                PadSide side;
                ControlPadInput.PadNum pad;
                Device(player, out keyboard, out side, out pad);
                Keyboard = keyboard;
                Pad = pad;
                Side = side;
                // The game's own buttons: one of these can still be picked, but the player is told
                // it also does that in the kitchen.
                var ids = new[]
                {
                    PlayerInputLookup.LogicalButtonID.PickupAndDrop, PlayerInputLookup.LogicalButtonID.WorkstationInteract,
                    PlayerInputLookup.LogicalButtonID.Dash, PlayerInputLookup.LogicalButtonID.Pause,
                    PlayerInputLookup.LogicalButtonID.PlayerSwitch,
                    PlayerInputLookup.LogicalButtonID.UIUp, PlayerInputLookup.LogicalButtonID.UIDown,
                    PlayerInputLookup.LogicalButtonID.UILeft, PlayerInputLookup.LogicalButtonID.UIRight,
                };
                var buttons = new List<ILogicalButton>();
                foreach (PlayerInputLookup.LogicalButtonID id in ids)
                {
                    try
                    {
                        ILogicalButton button = PlayerInputLookup.GetButton(id, player);
                        if (button != null) buttons.Add(button);
                    }
                    catch (Exception)
                    {
                    }
                }
                _gameButtons = buttons.ToArray();
                if (!Keyboard)
                    foreach (ControlPadInput.Button b in Enum.GetValues(typeof(ControlPadInput.Button)))
                        if (b != ControlPadInput.Button.Invalid && PadDown(Pad, b)) _held.Add(b);
            }

            /// <summary>The game uses this input for something else in the kitchen.</summary>
            private bool GameUses()
            {
                for (int i = 0; i < _gameButtons.Length; i++)
                    if (_gameButtons[i].IsDown()) return true;
                return false;
            }

            /// <summary>
            /// Call every frame. True once done: <paramref name="result"/> is a message to show
            /// ("SKILL KEY: Q", "CANCELLED", "X IS YOUR SKILL KEY").
            /// </summary>
            internal bool Poll(out string result)
            {
                result = null;
                if (Time.unscaledTime - Started > 8f)
                {
                    result = Lang.T("NO KEY PRESSED", "CHƯA BẤM PHÍM NÀO");
                    return true;
                }
                return Keyboard ? PollKeyboard(out result) : PollPad(out result);
            }

            private static string LabelOf(CaptureTarget target)
            {
                switch (target)
                {
                    case CaptureTarget.Details: return Lang.T("INFO KEY", "PHÍM XEM KỸ NĂNG");
                    case CaptureTarget.Switch: return Lang.T("SWITCH KEY", "PHÍM ĐỔI");
                    default: return Lang.T("SKILL KEY", "PHÍM KỸ NĂNG");
                }
            }

            private static string YoursOf(CaptureTarget target)
            {
                switch (target)
                {
                    case CaptureTarget.Details: return Lang.T("YOUR INFO KEY", "PHÍM XEM KỸ NĂNG CỦA BẠN");
                    case CaptureTarget.Switch: return Lang.T("YOUR SWITCH KEY", "PHÍM ĐỔI CỦA BẠN");
                    default: return Lang.T("YOUR SKILL KEY", "PHÍM KỸ NĂNG CỦA BẠN");
                }
            }

            private bool Done(out string result, string shown, bool gameButton)
            {
                Saved = true;
                result = LabelOf(Target) + ": " + shown + (gameButton ? Lang.T(" (ALSO A GAME BUTTON)", " (CŨNG LÀ NÚT CỦA GAME)") : "");
                return true;
            }

            private bool PollKeyboard(out string result)
            {
                result = null;
                if (!_keysReleased)
                {
                    // Wait for the key that started the capture to be let go.
                    if (Input.anyKey) return false;
                    _keysReleased = true;
                }
                if (!Input.anyKeyDown) return false;
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    result = Lang.T("CANCELLED", "ĐÃ HỦY");
                    return true;
                }
                bool right = Side == PadSide.Right;
                if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
                {
                    SaveKey(Target, right, KeyCode.None);
                    string cleared = Target == CaptureTarget.Details ? Lang.T("NONE", "KHÔNG CÓ")
                        : Target == CaptureTarget.Switch ? Lang.T("AUTOMATIC", "TỰ ĐỘNG") + " (" + KeyName(SwitchKeyFor(right)) + ")"
                        : Lang.T("EMOTE", "BIỂU CẢM");
                    return Done(out result, cleared, false);
                }
                foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
                {
                    if (key == KeyCode.None || key == KeyCode.Mouse0 || IsJoystick(key)) continue;
                    if (!Input.GetKeyDown(key)) continue;
                    // Neither this player's other keys nor the other player's on a shared keyboard
                    // (they press the same keys): the skill and info keys, and both switch keys.
                    var others = new Dictionary<CaptureTarget, KeyCode>
                    {
                        { CaptureTarget.Skill, Key }, { CaptureTarget.Details, DetailsKey }, { CaptureTarget.Switch, SwitchKeyFor(right) },
                    };
                    foreach (KeyValuePair<CaptureTarget, KeyCode> other in others)
                    {
                        if (other.Key == Target || other.Value != key) continue;
                        result = KeyName(key) + Lang.T(" IS ", " ĐÃ LÀ ") + YoursOf(other.Key);
                        return true;
                    }
                    if (key == SwitchKeyFor(!right) && Side != PadSide.Both)
                    {
                        result = KeyName(key) + Lang.T(" IS THE OTHER PLAYER'S SWITCH KEY", " LÀ PHÍM ĐỔI CỦA NGƯỜI KIA");
                        return true;
                    }
                    if (Target == CaptureTarget.Skill && right)
                    {
                        // The right-hand player of a shared keyboard keeps EMOTE (their keys are the game's).
                        result = Lang.T("RIGHT-HAND PLAYER: SKILL KEY STAYS EMOTE", "NGƯỜI CHƠI BÊN PHẢI: PHÍM KỸ NĂNG LÀ BIỂU CẢM");
                        return true;
                    }
                    SaveKey(Target, right, key);
                    return Done(out result, KeyName(key), GameUses());
                }
                return false;
            }

            private bool PollPad(out string result)
            {
                result = null;
                ControlPadInput.Button pressed = ControlPadInput.Button.Invalid;
                foreach (ControlPadInput.Button b in Enum.GetValues(typeof(ControlPadInput.Button)))
                {
                    if (b == ControlPadInput.Button.Invalid) continue;
                    if (!PadDown(Pad, b))
                    {
                        _held.Remove(b);
                        continue;
                    }
                    if (_held.Contains(b)) continue;
                    _held.Add(b);
                    // On a shared controller, the other half's buttons belong to the other player.
                    if (pressed == ControlPadInput.Button.Invalid && OnSide(Side, b)) pressed = b;
                }
                if (pressed == ControlPadInput.Button.Invalid) return false;
                if (pressed == ControlPadInput.Button.Start)
                {
                    result = Lang.T("CANCELLED", "ĐÃ HỦY");
                    return true;
                }
                PadKey key = ToPadKey(pressed);
                // Y is EMOTE: picking it for the skill means the game's EMOTE, chosen on purpose.
                if (Target == CaptureTarget.Skill && pressed == ControlPadInput.Button.Y)
                {
                    SaveButton(CaptureTarget.Skill, PadKey.Emote);
                    return Done(out result, Lang.T("EMOTE (Y)", "BIỂU CẢM (Y)"), false);
                }
                // The info or switch button pressed again clears it.
                ConfigEntry<PadKey> own = Target == CaptureTarget.Details ? s_detailsButton : Target == CaptureTarget.Switch ? s_switchButton : null;
                ControlPadInput.Button[] mine = PadButtonsFor(Player, Side);
                ControlPadInput.Button current = Target == CaptureTarget.Switch ? mine[1] : Target == CaptureTarget.Details ? DetailsButton : mine[0];
                if (own != null && pressed == current)
                {
                    SaveButton(Target, PadKey.None);
                    return Done(out result, Lang.T("NONE", "KHÔNG CÓ"), false);
                }
                var others = new Dictionary<CaptureTarget, ControlPadInput.Button>
                {
                    { CaptureTarget.Skill, mine[0] == ControlPadInput.Button.Invalid ? ControlPadInput.Button.Y : mine[0] },
                    { CaptureTarget.Details, DetailsButton },
                    { CaptureTarget.Switch, mine[1] },
                };
                foreach (KeyValuePair<CaptureTarget, ControlPadInput.Button> other in others)
                {
                    if (other.Key == Target || other.Value != pressed) continue;
                    result = ButtonName(pressed) + Lang.T(" IS ", " ĐÃ LÀ ") + YoursOf(other.Key);
                    return true;
                }
                SaveButton(Target, key);
                return Done(out result, ButtonName(pressed), GameUses());
            }
        }

        private static void SaveKey(CaptureTarget target, bool right, KeyCode value)
        {
            ConfigEntry<KeyCode> entry = target == CaptureTarget.Details ? s_detailsKey
                : target == CaptureTarget.Switch ? (right ? s_switchKeyRight : s_switchKey) : s_key;
            if (entry == null) return;
            entry.Value = value;
            Save(entry.ConfigFile);
            Plugin.Log.LogInfo("Phím (bàn phím) " + entry.Definition.Key + ": " + value);
        }

        private static void SaveButton(CaptureTarget target, PadKey value)
        {
            ConfigEntry<PadKey> entry = target == CaptureTarget.Details ? s_detailsButton
                : target == CaptureTarget.Switch ? s_switchButton : s_button;
            if (entry == null) return;
            entry.Value = value;
            Save(entry.ConfigFile);
            s_resolved.Clear();
            Plugin.Log.LogInfo("Nút (tay cầm) " + entry.Definition.Key + ": " + value);
        }

        private static void Save(ConfigFile file)
        {
            try
            {
                file.Save();
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không lưu được phím: " + error.Message);
            }
        }
    }
}
