using System;
using System.Collections.Generic;
using System.Text;

namespace OC2Tweaks
{
    internal enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

    /// <summary>How a ping figure is coloured, in the settings screen and in the corner overlay.</summary>
    internal enum PingBand { Waiting, Good, Fair, Poor, Stale }

    /// <summary>
    /// The words and choices on the settings screen, kept free of Unity so the tests can check
    /// them: what a player's name looks like once it is safe to put on screen, what a ping cell
    /// says, and which keys the hotkey selectors offer.
    /// </summary>
    internal static class MenuText
    {
        internal const int NameLength = 32;

        internal static readonly OverlayCorner[] Corners =
            { OverlayCorner.TopLeft, OverlayCorner.TopRight, OverlayCorner.BottomLeft, OverlayCorner.BottomRight };

        internal static string[] CornerNames
        {
            get
            {
                return Lang.Vi ? new[] { "Trên trái", "Trên phải", "Dưới trái", "Dưới phải" }
                    : new[] { "Top left", "Top right", "Bottom left", "Bottom right" };
            }
        }

        private static string Player { get { return Lang.T("Player", "Người chơi"); } }

        internal static string CornerName(OverlayCorner corner)
        {
            int index = Array.IndexOf(Corners, corner);
            return CornerNames[index < 0 ? 1 : index];
        }

        /// <summary>
        /// Player names are chosen by other people. The game's text supports rich-text tags and a
        /// dialog title is a localisation tag, so angle brackets become look-alikes, control
        /// characters become spaces, and long names are cut so a row never wraps.
        /// </summary>
        internal static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return Player;
            var text = new StringBuilder(name.Length);
            foreach (char ch in name)
            {
                if (ch == '<') text.Append('‹');
                else if (ch == '>') text.Append('›');
                else if (char.IsControl(ch)) text.Append(' ');
                else text.Append(ch);
            }
            string result = text.ToString().Trim();
            if (result.Length == 0) return Player;
            return result.Length > NameLength ? result.Substring(0, NameLength - 1).TrimEnd() + "…" : result;
        }

        /// <summary>A literal for the game's localiser: a quoted tag is shown as written.</summary>
        internal static string Literal(string text) { return "\"" + (text ?? "") + "\""; }

        internal static PingBand Band(float ms, bool current)
        {
            if (!current) return PingBand.Stale;
            return ms < 80f ? PingBand.Good : ms < 150f ? PingBand.Fair : PingBand.Poor;
        }

        /// <summary>The right-hand cell of a player row and of an overlay line.</summary>
        internal static string PingCell(bool known, float ms, bool current, bool hostLacksMod, out PingBand band)
        {
            if (known)
            {
                band = Band(ms, current);
                return ms.ToString("0") + " ms";
            }
            band = PingBand.Waiting;
            return hostLacksMod ? Lang.T("host lacks mod", "chủ phòng thiếu mod") : Lang.T("measuring...", "đang đo...");
        }

        private static readonly Dictionary<string, string> Modifiers = new Dictionary<string, string>
        {
            { "LeftControl", "Ctrl" }, { "RightControl", "Ctrl" }, { "LeftShift", "Shift" }, { "RightShift", "Shift" },
            { "LeftAlt", "Alt" }, { "RightAlt", "Alt" }, { "AltGr", "Alt" }
        };

        /// <summary>
        /// A binding as people read it: BepInEx stores <c>R + LeftControl</c>, the screen shows
        /// <c>Ctrl + R</c>; number-row and keypad keys lose Unity's Alpha/Keypad prefixes.
        /// </summary>
        internal static string KeyLabel(string serialized)
        {
            if (string.IsNullOrEmpty(serialized) || serialized == "None") return Lang.T("Not set", "Chưa đặt");
            string[] parts = serialized.Split(new[] { " + " }, StringSplitOptions.RemoveEmptyEntries);
            var shown = new List<string>();
            for (int i = 1; i < parts.Length; i++)
            {
                string name;
                name = Modifiers.TryGetValue(parts[i], out name) ? name : parts[i];
                if (!shown.Contains(name)) shown.Add(name);
            }
            shown.Add(KeyName(parts[0]));
            return string.Join(" + ", shown.ToArray());
        }

        private static string KeyName(string key)
        {
            if (key.Length == 6 && key.StartsWith("Alpha")) return key.Substring(5);
            if (key.StartsWith("Keypad")) return "Num " + key.Substring(6);
            switch (key)
            {
                case "Return": return "Enter";
                case "Escape": return "Esc";
                case "Backspace": return "Backspace";
                case "UpArrow": return "↑";
                case "DownArrow": return "↓";
                case "LeftArrow": return "←";
                case "RightArrow": return "→";
                default: return key;
            }
        }

        /// <summary>Keys the game itself answers on the keyboard: moving, picking up, chopping,
        /// dashing, menus. A hotkey on one of these, without a modifier, fires during play.</summary>
        private static readonly string[] GameKeys =
        {
            "W", "A", "S", "D", "E", "F", "G", "H", "I", "J", "K", "L", "R", "T", "U", "Y", "Space", "Return",
            "Backspace", "UpArrow", "DownArrow", "LeftArrow", "RightArrow", "LeftShift", "RightShift",
            "LeftControl", "LeftAlt"
        };

        /// <summary>Other mods in this collection that already answer these keys.</summary>
        internal static string KnownKeyOwner(string key)
        {
            switch (key)
            {
                case "F1": case "F2": return "Host Force Theme";
                case "F7": case "F8": return "RandomLevel";
                default: return null;
            }
        }

        /// <summary>
        /// What the hint line says after a key is bound: a clash with another of the mod's keys,
        /// with the game's own controls, or with another mod in this collection.
        /// </summary>
        internal static string KeyNote(string chosen, params string[] others)
        {
            if (Array.IndexOf(others, chosen) >= 0)
                return Lang.T(" This key is already used by another OC2Tweaks function - change one of them.",
                    " Phím này đang dùng cho một chức năng khác của OC2Tweaks - hãy đổi một trong hai.");
            bool plain = chosen.IndexOf(" + ", StringComparison.Ordinal) < 0;
            if (plain && Array.IndexOf(GameKeys, chosen) >= 0)
                return Lang.T(" Note: the game also uses this key while playing; consider holding Ctrl, Shift or Alt for a combination.",
                    " Lưu ý: game cũng dùng phím này khi chơi; nên giữ thêm Ctrl, Shift hoặc Alt để tạo tổ hợp.");
            string owner = plain ? KnownKeyOwner(chosen) : null;
            if (owner == null) return "";
            return Lang.Vi ? " Lưu ý: " + chosen + " cũng là phím của " + owner + " nếu bạn cài mod đó."
                : " Note: " + chosen + " is also a key of " + owner + " if you have that mod installed.";
        }
    }
}
