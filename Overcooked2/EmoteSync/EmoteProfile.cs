using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace EmoteSync
{
    /// <summary>
    /// One player's emote settings from Arcade MOD (HostTools), already resolved per game emote on
    /// that player's own machine (see <see cref="Emotes"/>): what each bubble shows, its custom
    /// text, and the bubble font size and colour.
    ///
    /// Written to Steam lobby member data as
    /// "2|fontSize|RRGGBBAA|mode,mode,...|base64,base64,..." with one mode and one text per
    /// emote in <see cref="Emotes.Keys"/> order ("2|off" when HostTools' emote text is off). Texts
    /// are UTF-8 in base64 so any character, including | and comma, survives.
    /// </summary>
    internal sealed class EmoteProfile
    {
        internal const string Version = "2";
        internal const string Off = Version + "|off";
        private const int MaxTextLength = 200;

        public int FontSize = 60;
        public Color Colour = new Color(0f, 0f, 0f, 1f);
        public readonly HostTools.Mode[] Modes = new HostTools.Mode[Emotes.Count];
        public readonly string[] Texts = new string[Emotes.Count];

        public EmoteProfile()
        {
            for (int i = 0; i < Texts.Length; i++) Texts[i] = "";
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

        /// <summary>True when the value was written by this version of Emote Sync (settings or "off").</summary>
        public static bool IsOurs(string value)
        {
            return value != null && value.StartsWith(Version + "|", StringComparison.Ordinal);
        }

        public static EmoteProfile Parse(string value)
        {
            if (!IsOurs(value)) return null;
            string[] parts = value.Split('|');
            if (parts.Length < 5) return null;

            string[] modes = parts[3].Split(',');
            string[] texts = parts[4].Split(',');
            var profile = new EmoteProfile();

            int size;
            if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size))
                profile.FontSize = Mathf.Clamp(size, 10, 100);
            Color colour;
            if (TryColour(parts[2], out colour)) profile.Colour = colour;

            for (int i = 0; i < Emotes.Count; i++)
            {
                profile.Modes[i] = i < modes.Length ? ParseMode(modes[i]) : HostTools.Mode.Keep;
                profile.Texts[i] = i < texts.Length ? Decode(texts[i]) : "";
            }
            return profile;
        }

        internal static HostTools.Mode ParseMode(string value)
        {
            int mode;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out mode) &&
                mode >= 0 && mode <= (int)HostTools.Mode.VoicePackName)
                return (HostTools.Mode)mode;
            return HostTools.Mode.Keep;
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

    /// <summary>
    /// An emote a player on another machine started, with exactly what its bubble shows there:
    /// "2|seq|wheel|option|emote|mode|base64text". The wheel is a lobby card ("u" + player slot)
    /// or a chef ("e" + network entity ID), both the same on every machine.
    /// </summary>
    internal sealed class SentEmote
    {
        public int Seq;
        public string Wheel;
        public int Option;
        public int Emote;
        public HostTools.Mode Mode;
        public string Text = "";

        public string Format()
        {
            return EmoteProfile.Version + "|" + Seq.ToString(CultureInfo.InvariantCulture) + "|" + Wheel + "|" +
                Option.ToString(CultureInfo.InvariantCulture) + "|" + Emote.ToString(CultureInfo.InvariantCulture) + "|" +
                ((int)Mode).ToString(CultureInfo.InvariantCulture) + "|" + EmoteProfile.Encode(Text);
        }

        public static SentEmote Parse(string value)
        {
            if (!EmoteProfile.IsOurs(value)) return null;
            string[] parts = value.Split('|');
            if (parts.Length < 7 || string.IsNullOrEmpty(parts[2])) return null;
            var sent = new SentEmote { Wheel = parts[2], Mode = EmoteProfile.ParseMode(parts[5]), Text = EmoteProfile.Decode(parts[6]) };
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sent.Seq) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out sent.Option) ||
                !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out sent.Emote) ||
                sent.Emote < 0 || sent.Emote >= Emotes.Count || sent.Option < 0)
                return null;
            return sent;
        }
    }
}
