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

    /// <summary>A controller button a binding can use (None: that binding's default).</summary>
    internal enum PadKey { None, A, B, X, Y, LB, RB, LT, RT, Back, LS, RS, DPadUp, DPadDown, DPadLeft, DPadRight }

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
            s_button = config.Bind("Controls", "SkillButton", PadKey.None, new ConfigDescription(
                "Controller: the button that uses your active special (any button: shoulders, triggers, d-pad and stick clicks too). " +
                "None = the game's EMOTE button (Y). Each player's own controller is read. " +
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

            // Older versions kept a controller button in the key setting: move it to the button setting.
            Migrate(s_key, s_button);
            Migrate(s_detailsKey, s_detailsButton);
            s_key.SettingChanged += delegate { Migrate(s_key, s_button); };
            s_detailsKey.SettingChanged += delegate { Migrate(s_detailsKey, s_detailsButton); };
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
            return key == PadKey.None ? Lang.T("EMOTE (Y)", "BIỂU CẢM (Y)") : ButtonName(ToButton(key));
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

        // --- the skill key -----------------------------------------------------------------------

        /// <summary>Is this local chef's skill button held now?</summary>
        internal static bool IsDown(ChefState state)
        {
            if (state == null || state.Controls == null || state.Provider == null) return false;
            bool keyboard;
            PadSide side;
            ControlPadInput.PadNum pad;
            if (Device(state.Provider.GetID(), out keyboard, out side, out pad))
            {
                if (keyboard && side != PadSide.Right && Key != KeyCode.None) return Input.GetKey(Key);
                if (!keyboard && OnSide(side, Button)) return PadDown(pad, Button);
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
                if (!keyboard && OnSide(side, Button)) return ButtonName(Button);
            }
            return known && !keyboard ? Lang.T("EMOTE (Y)", "BIỂU CẢM (Y)") : Lang.T("EMOTE", "BIỂU CẢM");
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
            if (keyboard) return "bàn phím" + (side != PadSide.Both ? " (" + side + ")" : "") + ": chọn = Space, chốt = Enter";
            ControlPadInput.Button lockIn = LockInPadButton(player);
            return "tay cầm " + pad + (side != PadSide.Both ? " (nửa " + side + ")" : "") +
                ": chọn = " + RealNames(player, AmbiPadButton.Confirm) + ", hủy = " + RealNames(player, AmbiPadButton.Cancel) +
                ", ẩn = " + RealNames(player, AmbiPadButton.Four) +
                ", chốt = " + (lockIn != ControlPadInput.Button.Invalid ? lockIn.ToString() : "nút X của game");
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
        /// One player waiting to press their new skill key (or skill info key): a keyboard player
        /// sets the keyboard key, a controller player the controller button.
        /// </summary>
        internal sealed class Capture
        {
            internal readonly PlayerInputLookup.Player Player;
            /// <summary>Setting the skill info key, not the skill key.</summary>
            internal readonly bool Details;
            /// <summary>A new key was saved.</summary>
            internal bool Saved { get; private set; }
            internal readonly bool Keyboard;
            internal readonly ControlPadInput.PadNum Pad;
            internal readonly PadSide Side;
            internal readonly float Started;
            private readonly HashSet<ControlPadInput.Button> _held = new HashSet<ControlPadInput.Button>();
            private readonly ILogicalButton[] _gameButtons;
            private bool _keysReleased;

            internal Capture(PlayerInputLookup.Player player, bool details)
            {
                Player = player;
                Details = details;
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

            private string Label
            {
                get { return Details ? Lang.T("INFO KEY", "PHÍM XEM KỸ NĂNG") : Lang.T("SKILL KEY", "PHÍM KỸ NĂNG"); }
            }

            private string OtherLabel
            {
                get { return Details ? Lang.T("YOUR SKILL KEY", "PHÍM KỸ NĂNG CỦA BẠN") : Lang.T("YOUR INFO KEY", "PHÍM XEM KỸ NĂNG CỦA BẠN"); }
            }

            private bool Done(out string result, string shown, bool gameButton)
            {
                Saved = true;
                result = Label + ": " + shown + (gameButton ? Lang.T(" (ALSO A GAME BUTTON)", " (CŨNG LÀ NÚT CỦA GAME)") : "");
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
                {
                    SaveKey(Details, KeyCode.None);
                    return Done(out result, Details ? Lang.T("NONE", "KHÔNG CÓ") : Lang.T("EMOTE", "BIỂU CẢM"), false);
                }
                foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
                {
                    if (key == KeyCode.None || key == KeyCode.Mouse0 || IsJoystick(key)) continue;
                    if (!Input.GetKeyDown(key)) continue;
                    KeyCode other = Details ? Key : DetailsKey;
                    if (other == key)
                    {
                        result = KeyName(key) + Lang.T(" IS ", " ĐÃ LÀ ") + OtherLabel;
                        return true;
                    }
                    SaveKey(Details, key);
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
                // Y is EMOTE, the skill button's default: picking it for the skill goes back to EMOTE.
                if (!Details && pressed == ControlPadInput.Button.Y)
                {
                    SaveButton(false, PadKey.None);
                    return Done(out result, Lang.T("EMOTE (Y)", "BIỂU CẢM (Y)"), false);
                }
                // The info button pressed again clears it.
                if (Details && s_detailsButton != null && key == s_detailsButton.Value)
                {
                    SaveButton(true, PadKey.None);
                    return Done(out result, Lang.T("NONE", "KHÔNG CÓ"), false);
                }
                ControlPadInput.Button skill = Button == ControlPadInput.Button.Invalid ? ControlPadInput.Button.Y : Button;
                ControlPadInput.Button other = Details ? skill : DetailsButton;
                if (pressed == other)
                {
                    result = ButtonName(pressed) + Lang.T(" IS ", " ĐÃ LÀ ") + OtherLabel;
                    return true;
                }
                SaveButton(Details, key);
                return Done(out result, ButtonName(pressed), GameUses());
            }
        }

        private static void SaveKey(bool details, KeyCode value)
        {
            ConfigEntry<KeyCode> entry = details ? s_detailsKey : s_key;
            if (entry == null) return;
            entry.Value = value;
            Save(entry.ConfigFile);
            Plugin.Log.LogInfo(details ? "Phím xem kỹ năng (bàn phím): " + value : "Phím kỹ năng (bàn phím): " + value);
        }

        private static void SaveButton(bool details, PadKey value)
        {
            ConfigEntry<PadKey> entry = details ? s_detailsButton : s_button;
            if (entry == null) return;
            entry.Value = value;
            Save(entry.ConfigFile);
            Plugin.Log.LogInfo(details ? "Nút xem kỹ năng (tay cầm): " + value : "Nút kỹ năng (tay cầm): " + value);
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
