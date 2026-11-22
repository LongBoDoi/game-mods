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
    }

    /// <summary>
    /// The one button that uses a chef's active special: <c>[Controls] SkillKey</c>. None (the
    /// default) means the game's EMOTE button. Otherwise it is either a keyboard key (used by the
    /// players on the keyboard) or a controller button (Unity's JoystickButton codes, used by the
    /// players on a controller). It can be set by pressing it: on the skill screen's SKILL KEY
    /// row, or with Rebind in MOD Settings.
    ///
    /// A controller button is read from each player's own pad through the game's input layer, so
    /// two local pad players never trigger each other's chef. With one pad player on this PC the
    /// captured Unity code is also read as it is, which keeps any pad layout (not only Xbox) right.
    /// The right-hand player of a shared keyboard keeps EMOTE (one key can't serve both sides).
    /// </summary>
    internal static class SpecialInput
    {
        private const int FirstJoystickCode = (int)KeyCode.JoystickButton0;
        private const int ButtonsPerJoystick = 20;

        private static ConfigEntry<KeyCode> s_key;

        // Unity / DirectInput button numbers, as the game maps them (DirectInputProvider).
        private static readonly ControlPadInput.Button[] ButtonByIndex =
        {
            ControlPadInput.Button.A, ControlPadInput.Button.B, ControlPadInput.Button.X, ControlPadInput.Button.Y,
            ControlPadInput.Button.LB, ControlPadInput.Button.RB, ControlPadInput.Button.Back, ControlPadInput.Button.Start,
            ControlPadInput.Button.LeftAnalog, ControlPadInput.Button.RightAnalog,
        };

        internal static void Bind(ConfigFile config)
        {
            s_key = config.Bind("Controls", "SkillKey", KeyCode.None, new ConfigDescription(
                "The key or controller button that uses your active special. None = the game's EMOTE button. " +
                "Press Rebind, then the key (keyboard) or the button (controller: Y, LB, RB, Back or a stick click). " +
                "Backspace clears it back to EMOTE. Also set on the skill screen's SKILL KEY row.",
                null,
                new ConfigurationManagerAttributes { DispName = "Skill key", ObjToStr = FormatSetting, Order = 0 }));
            s_detailsKey = config.Bind("Controls", "DetailsKey", KeyCode.Tab, new ConfigDescription(
                "The key or controller button that shows or hides the panel with your skills' details during a level. " +
                "Press Rebind, then the key or the button (Back or a stick click work well on a controller). " +
                "Backspace clears it (no panel key).",
                null,
                new ConfigurationManagerAttributes { DispName = "Skill details key", ObjToStr = FormatDetails, Order = -1 }));
        }

        private static ConfigEntry<KeyCode> s_detailsKey;
        private static readonly HashSet<ControlPadInput.PadNum> s_detailsHeld = new HashSet<ControlPadInput.PadNum>();

        private static string FormatDetails(object value)
        {
            return FormatDetails(value, Lang.Vi);
        }

        private static string FormatDetails(object value, bool vi)
        {
            KeyCode key = value is KeyCode ? (KeyCode)value : KeyCode.None;
            if (key == KeyCode.None) return vi ? "Không có" : "None";
            return IsJoystick(key) ? (vi ? "Tay cầm " : "Controller ") + ButtonName(PadButton(key)) : KeyName(key);
        }

        internal static KeyCode DetailsKey
        {
            get { return s_detailsKey != null ? s_detailsKey.Value : KeyCode.None; }
        }

        /// <summary>The details key's name to print ("TAB", "BACK"), or null when there is none.</summary>
        internal static string DetailsKeyName()
        {
            KeyCode key = DetailsKey;
            if (key == KeyCode.None) return null;
            return IsJoystick(key) ? ButtonName(PadButton(key)) : KeyName(key);
        }

        /// <summary>
        /// Call once a frame: was the details key just pressed (the keyboard key, or the button on
        /// any local player's pad)?
        /// </summary>
        internal static bool DetailsPressed()
        {
            KeyCode key = DetailsKey;
            if (key == KeyCode.None) return false;
            if (!IsJoystick(key)) return Input.GetKeyDown(key);
            bool pressed = false, anyPad = false;
            ControlPadInput.Button button = PadButton(key);
            foreach (ChefState state in SpecialRuntime.Chefs)
            {
                if (!state.Local || state.Provider == null) continue;
                bool keyboard, right;
                ControlPadInput.PadNum pad;
                if (!Device(state.Provider.GetID(), out keyboard, out right, out pad) || keyboard) continue;
                anyPad = true;
                bool down = PadDown(pad, button);
                if (down && !s_detailsHeld.Contains(pad)) pressed = true;
                if (down) s_detailsHeld.Add(pad);
                else s_detailsHeld.Remove(pad);
            }
            // No pad player known (or the game can't read this pad): Unity's own reading.
            return anyPad ? pressed : Input.GetKeyDown(key);
        }

        private static string FormatSetting(object value)
        {
            return FormatSetting(value, Lang.Vi);
        }

        private static string FormatSetting(object value, bool vi)
        {
            KeyCode key = value is KeyCode ? (KeyCode)value : KeyCode.None;
            if (key == KeyCode.None) return vi ? "BIỂU CẢM" : "EMOTE";
            return IsJoystick(key) ? (vi ? "Tay cầm " : "Controller ") + ButtonName(PadButton(key)) : KeyName(key);
        }

        private static KeyCode Key
        {
            get { return s_key != null ? s_key.Value : KeyCode.None; }
        }

        private static bool IsJoystick(KeyCode key)
        {
            return (int)key >= FirstJoystickCode;
        }

        /// <summary>The pad button a JoystickButton code stands for (Invalid when none).</summary>
        private static ControlPadInput.Button PadButton(KeyCode key)
        {
            if (!IsJoystick(key)) return ControlPadInput.Button.Invalid;
            int index = ((int)key - FirstJoystickCode) % ButtonsPerJoystick;
            return index < ButtonByIndex.Length ? ButtonByIndex[index] : ControlPadInput.Button.Invalid;
        }

        private static KeyCode CodeFor(ControlPadInput.Button button)
        {
            int index = Array.IndexOf(ButtonByIndex, button);
            return index < 0 ? KeyCode.None : (KeyCode)(FirstJoystickCode + index);
        }

        /// <summary>The player's controls: keyboard (and which side) or a pad.</summary>
        private static bool Device(PlayerInputLookup.Player player, out bool keyboard, out bool rightSide, out ControlPadInput.PadNum pad)
        {
            keyboard = false;
            rightSide = false;
            pad = ControlPadInput.PadNum.Invalid;
            try
            {
                GameInputConfig config = PlayerInputLookup.GetInputConfig();
                PlayerGameInput input = config != null ? config.GetInputData(player) : null;
                if (input == null) return false;
                pad = input.Pad;
                keyboard = KeyboardUtils.IsKeyboard(input);
                rightSide = input.Side == PadSide.Right;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // How many local chefs are played with a pad (cached per frame).
        private static int s_padFrame = -1, s_padPlayers;

        private static int LocalPadPlayers()
        {
            if (s_padFrame == Time.frameCount) return s_padPlayers;
            s_padFrame = Time.frameCount;
            var pads = new HashSet<ControlPadInput.PadNum>();
            foreach (ChefState state in SpecialRuntime.Chefs)
            {
                if (!state.Local || state.Provider == null) continue;
                bool keyboard, right;
                ControlPadInput.PadNum pad;
                if (Device(state.Provider.GetID(), out keyboard, out right, out pad) && !keyboard) pads.Add(pad);
            }
            s_padPlayers = pads.Count;
            return s_padPlayers;
        }

        /// <summary>Is this local chef's skill button held now?</summary>
        internal static bool IsDown(ChefState state)
        {
            if (state == null || state.Controls == null || state.Provider == null) return false;
            KeyCode key = Key;
            bool keyboard, right;
            ControlPadInput.PadNum pad;
            if (key != KeyCode.None && Device(state.Provider.GetID(), out keyboard, out right, out pad))
            {
                if (keyboard && !right && !IsJoystick(key)) return Input.GetKey(key);
                if (!keyboard && IsJoystick(key))
                {
                    if (LocalPadPlayers() <= 1) return Input.GetKey(key) || PadDown(pad, PadButton(key));
                    return PadDown(pad, PadButton(key));
                }
            }
            PlayerControls.ControlSchemeData scheme = state.Controls.ControlScheme;
            return scheme != null && scheme.m_curseButton != null && scheme.m_curseButton.IsDown();
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
                bool keyboard, right;
                ControlPadInput.PadNum pad;
                if (!Device(_player, out keyboard, out right, out pad) || keyboard || pad == ControlPadInput.PadNum.Invalid)
                    return pressed;
                try
                {
                    GameInputConfig config = PlayerInputLookup.GetInputConfig();
                    PlayerGameInput input = config != null ? config.GetInputData(_player) : null;
                    if (input != null && input.Side != PadSide.Both) return pressed;
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

        /// <summary>What to print for this player's skill button ("Y", "Q", "EMOTE").</summary>
        internal static string NameFor(PlayerInputLookup.Player player)
        {
            KeyCode key = Key;
            bool keyboard, right;
            ControlPadInput.PadNum pad;
            bool known = Device(player, out keyboard, out right, out pad);
            if (known && key != KeyCode.None)
            {
                if (keyboard && !right && !IsJoystick(key)) return KeyName(key);
                if (!keyboard && IsJoystick(key)) return ButtonName(PadButton(key));
            }
            return known && !keyboard ? Lang.T("EMOTE (Y)", "BIỂU CẢM (Y)") : Lang.T("EMOTE", "BIỂU CẢM");
        }

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
                case ControlPadInput.Button.LeftAnalog: return "LS";
                case ControlPadInput.Button.RightAnalog: return "RS";
                case ControlPadInput.Button.Back: return "BACK";
                default: return button.ToString().ToUpperInvariant();
            }
        }

        // --- rebinding on the skill screen -------------------------------------------------------

        /// <summary>One player waiting to press their new skill key (or details key).</summary>
        internal sealed class Capture
        {
            internal readonly PlayerInputLookup.Player Player;
            /// <summary>Setting the details key, not the skill key.</summary>
            internal readonly bool Details;
            /// <summary>A new key was saved.</summary>
            internal bool Saved { get; private set; }
            internal readonly bool Keyboard;
            internal readonly ControlPadInput.PadNum Pad;
            internal readonly float Started;
            private readonly HashSet<ControlPadInput.Button> _held = new HashSet<ControlPadInput.Button>();
            private readonly ILogicalButton[] _gameButtons;
            private bool _keysReleased;

            internal Capture(PlayerInputLookup.Player player, bool details)
            {
                Player = player;
                Details = details;
                Started = Time.unscaledTime;
                bool keyboard, right;
                ControlPadInput.PadNum pad;
                Device(player, out keyboard, out right, out pad);
                Keyboard = keyboard;
                Pad = pad;
                // The game's own action buttons: a key or button that does one of these can't be the skill button.
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

            /// <summary>The game uses this input for something else right now.</summary>
            private bool GameUses()
            {
                for (int i = 0; i < _gameButtons.Length; i++)
                    if (_gameButtons[i].IsDown()) return true;
                return false;
            }

            /// <summary>
            /// Call every frame. True once done: <paramref name="result"/> is a message to show
            /// ("SKILL KEY: Q", "CANCELLED", "X IS USED BY THE GAME").
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

            private string Label
            {
                get { return Details ? Lang.T("DETAILS KEY", "PHÍM CHI TIẾT") : Lang.T("SKILL KEY", "PHÍM KỸ NĂNG"); }
            }

            /// <summary>The other binding already uses this key or button.</summary>
            private bool TakenByOther(KeyCode key)
            {
                KeyCode other = Details ? Key : DetailsKey;
                if (other == KeyCode.None) return false;
                if (other == key) return true;
                return IsJoystick(other) && IsJoystick(key) && PadButton(other) == PadButton(key);
            }

            private string OtherLabel
            {
                get { return Details ? Lang.T("YOUR SKILL KEY", "PHÍM KỸ NĂNG CỦA BẠN") : Lang.T("YOUR DETAILS KEY", "PHÍM CHI TIẾT CỦA BẠN"); }
            }

            private bool Store(KeyCode key, out string result, string shown)
            {
                SaveBinding(Details, key);
                Saved = true;
                result = Label + ": " + shown;
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
                if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
                    return Store(KeyCode.None, out result, Details ? Lang.T("NONE", "KHÔNG CÓ") : Lang.T("EMOTE", "BIỂU CẢM"));
                foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
                {
                    if (key == KeyCode.None || key == KeyCode.Mouse0 || IsJoystick(key)) continue;
                    if (!Input.GetKeyDown(key)) continue;
                    if (GameUses() || key == KeyCode.W || key == KeyCode.A || key == KeyCode.S || key == KeyCode.D ||
                        key == KeyCode.UpArrow || key == KeyCode.DownArrow || key == KeyCode.LeftArrow || key == KeyCode.RightArrow)
                    {
                        result = KeyName(key) + Lang.T(" IS USED BY THE GAME", " ĐÃ ĐƯỢC GAME DÙNG");
                        return true;
                    }
                    if (TakenByOther(key))
                    {
                        result = KeyName(key) + Lang.T(" IS ", " ĐÃ LÀ ") + OtherLabel;
                        return true;
                    }
                    return Store(key, out result, KeyName(key));
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
                    if (pressed == ControlPadInput.Button.Invalid) pressed = b;
                }
                if (pressed == ControlPadInput.Button.Invalid) return false;
                if (pressed == ControlPadInput.Button.B)
                {
                    result = Lang.T("CANCELLED", "ĐÃ HỦY");
                    return true;
                }
                if (pressed == ControlPadInput.Button.LTrigger || pressed == ControlPadInput.Button.RTrigger)
                {
                    result = Lang.T("TRIGGERS CAN'T BE USED", "KHÔNG DÙNG ĐƯỢC NÚT CÒ");
                    return true;
                }
                KeyCode code = CodeFor(pressed);
                // Y is EMOTE: the skill key's default, so for the details key it's taken.
                if (code == KeyCode.None || pressed == ControlPadInput.Button.Start || GameUses() ||
                    (Details && pressed == ControlPadInput.Button.Y))
                {
                    result = ButtonName(pressed) + Lang.T(" IS USED BY THE GAME", " ĐÃ ĐƯỢC GAME DÙNG");
                    return true;
                }
                if (!Details && pressed == ControlPadInput.Button.Y) return Store(KeyCode.None, out result, Lang.T("EMOTE (Y)", "BIỂU CẢM (Y)"));
                if (TakenByOther(code))
                {
                    result = ButtonName(pressed) + Lang.T(" IS ", " ĐÃ LÀ ") + OtherLabel;
                    return true;
                }
                return Store(code, out result, ButtonName(pressed));
            }
        }

        private static void SaveBinding(bool details, KeyCode value)
        {
            ConfigEntry<KeyCode> entry = details ? s_detailsKey : s_key;
            if (entry == null) return;
            entry.Value = value;
            try
            {
                entry.ConfigFile.Save();
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không lưu được phím: " + error.Message);
            }
            Plugin.Log.LogInfo((details ? "Phím xem chi tiết kỹ năng: " + FormatDetails(value, false) : "Nút dùng kỹ năng: " + FormatSetting(value, false)));
        }
    }
}
