using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace EmoteSync
{
    /// <summary>
    /// One player's emote settings from Arcade MOD (HostTools): what each emote's bubble shows,
    /// the custom text for it, and the bubble font size and colour.
    ///
    /// Written to Steam lobby member data as
    /// "1|fontSize|RRGGBBAA|mode,mode,...|base64,base64,..." (texts are UTF-8 in base64 so any
    /// character, including | and comma, survives).
    /// </summary>
    internal sealed class EmoteProfile
    {
        private const string Version = "1";
        private const int MaxTextLength = 200;

        public int FontSize = 60;
        public Color Colour = new Color(0f, 0f, 0f, 1f);
        public readonly HostTools.Mode[] Modes;
        public readonly string[] Texts;

        public EmoteProfile(int count)
        {
            Modes = new HostTools.Mode[count];
            Texts = new string[count];
            for (int i = 0; i < count; i++) Texts[i] = "";
        }

        public string Format()
        {
            var text = new StringBuilder();
            text.Append(Version).Append('|');
            text.Append(FontSize.ToString(CultureInfo.InvariantCulture)).Append('|');
            Color32 c = Colour;
            text.Append(c.r.ToString("X2")).Append(c.g.ToString("X2"))
                .Append(c.b.ToString("X2")).Append(c.a.ToString("X2")).Append('|');
            for (int i = 0; i < Modes.Length; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(((int)Modes[i]).ToString(CultureInfo.InvariantCulture));
            }
            text.Append('|');
            for (int i = 0; i < Texts.Length; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(Encode(Texts[i]));
            }
            return text.ToString();
        }

        public static EmoteProfile Parse(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string[] parts = value.Split('|');
            if (parts.Length < 5 || parts[0] != Version) return null;

            string[] modes = parts[3].Split(',');
            string[] texts = parts[4].Split(',');
            var profile = new EmoteProfile(modes.Length);

            int size;
            if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size))
                profile.FontSize = Mathf.Clamp(size, 10, 100);
            Color colour;
            if (TryColour(parts[2], out colour)) profile.Colour = colour;

            for (int i = 0; i < modes.Length; i++)
            {
                int mode;
                if (int.TryParse(modes[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out mode) &&
                    mode >= 0 && mode <= (int)HostTools.Mode.VoicePackName)
                    profile.Modes[i] = (HostTools.Mode)mode;
                profile.Texts[i] = i < texts.Length ? Decode(texts[i]) : "";
            }
            return profile;
        }

        internal static string Encode(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length > MaxTextLength) text = text.Substring(0, MaxTextLength);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }

        internal static string Decode(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                return text.Length > MaxTextLength ? text.Substring(0, MaxTextLength) : text;
            }
            catch (FormatException)
            {
                return "";
            }
        }

        private static bool TryColour(string hex, out Color colour)
        {
            colour = new Color(0f, 0f, 0f, 1f);
            if (hex == null || hex.Length != 8) return false;
            var bytes = new byte[4];
            for (int i = 0; i < 4; i++)
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                    return false;
            colour = new Color32(bytes[0], bytes[1], bytes[2], bytes[3]);
            return true;
        }
    }
}
