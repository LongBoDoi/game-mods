using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace ChefSkills
{
    /// <summary>Where a player's skill bar is drawn in a level.</summary>
    internal enum SkillBarMode { OnScreen, AboveHead, Hidden }

    /// <summary>
    /// Each local player's own skill bar settings, all also set on the skill screen's card:
    /// <list type="bullet">
    /// <item><c>[Display] SkillBarPlayerN</c>: on screen, over their chef's head, or hidden (DISPLAY row).</item>
    /// <item><c>SkillBarSizePlayerN</c>: how big it is drawn, 50-150% (BAR SIZE row; over the head too).</item>
    /// <item><c>SkillBarPositionPlayerN</c>: where it sits on screen: <c>Auto</c> (along the bottom,
    /// sharing it with the other players) or <c>x,y</c>, the bar's bottom centre as fractions of the
    /// screen from its bottom-left corner (BAR POSITION row, placed by moving a preview).</item>
    /// </list>
    /// A player is counted among the players on this PC in player order (so online, the only player
    /// here is always player 1).
    /// </summary>
    internal static class SkillBarSettings
    {
        internal const int Players = 4;
        internal const int MinSize = 50, MaxSize = 150, SizeStep = 10;

        private static readonly ConfigEntry<SkillBarMode>[] s_modes = new ConfigEntry<SkillBarMode>[Players];
        private static readonly ConfigEntry<int>[] s_sizes = new ConfigEntry<int>[Players];
        private static readonly ConfigEntry<string>[] s_positions = new ConfigEntry<string>[Players];

        internal static void Bind(ConfigFile config)
        {
            for (int i = 0; i < Players; i++)
            {
                string who = "player " + (i + 1) + " on this PC (counted in player order, online too)";
                s_modes[i] = config.Bind("Display", "SkillBarPlayer" + (i + 1), SkillBarMode.OnScreen, new ConfigDescription(
                    "Where " + who + " sees their skill bar in a level: OnScreen, AboveHead (small, over their chef, " +
                    "showing only what is ready or running) or Hidden. Also the skill screen's DISPLAY row.",
                    null,
                    new ConfigurationManagerAttributes { DispName = "Skill bar: player " + (i + 1), ObjToStr = FormatMode, Order = -10 - 3 * i }));
                s_sizes[i] = config.Bind("Display", "SkillBarSizePlayer" + (i + 1), 100, new ConfigDescription(
                    "How big " + who + "'s skill bar is drawn, in percent (on screen and over the head). Also the skill screen's BAR SIZE row.",
                    new AcceptableValueRange<int>(MinSize, MaxSize),
                    new ConfigurationManagerAttributes { DispName = "Skill bar size: player " + (i + 1), Order = -11 - 3 * i }));
                s_positions[i] = config.Bind("Display", "SkillBarPositionPlayer" + (i + 1), "Auto", new ConfigDescription(
                    "Where " + who + "'s on-screen skill bar sits: Auto (along the bottom, next to the other players') or x,y " +
                    "(the bar's bottom centre, as fractions of the screen from the bottom-left, e.g. 0.15,0.05). " +
                    "Easiest set on the skill screen's BAR POSITION row.",
                    null,
                    new ConfigurationManagerAttributes { DispName = "Skill bar position: player " + (i + 1), Order = -12 - 3 * i }));
            }
        }

        private static bool Valid(int rank)
        {
            return rank >= 0 && rank < Players && s_modes[rank] != null;
        }

        /// <summary>The mode of the <paramref name="rank"/>-th player on this PC (0 = first).</summary>
        internal static SkillBarMode Get(int rank)
        {
            if (!Valid(rank)) return SkillBarMode.OnScreen;
            SkillBarMode mode = s_modes[rank].Value;
            return Enum.IsDefined(typeof(SkillBarMode), mode) ? mode : SkillBarMode.OnScreen;
        }

        /// <summary>Steps the player's mode forward or back (wrapping) and saves it.</summary>
        internal static SkillBarMode Step(int rank, int step)
        {
            int count = Enum.GetValues(typeof(SkillBarMode)).Length;
            var mode = (SkillBarMode)((((int)Get(rank) + step) % count + count) % count);
            if (!Valid(rank)) return mode;
            s_modes[rank].Value = mode;
            Save(s_modes[rank].ConfigFile);
            Plugin.Log.LogInfo("Thanh kỹ năng của người chơi " + (rank + 1) + " trên máy này: " + mode);
            return mode;
        }

        /// <summary>The player's size as a scale (1 = normal).</summary>
        internal static float Scale(int rank)
        {
            return SizePercent(rank) / 100f;
        }

        internal static int SizePercent(int rank)
        {
            if (!Valid(rank) || s_sizes[rank] == null) return 100;
            return Mathf.Clamp(s_sizes[rank].Value, MinSize, MaxSize);
        }

        /// <summary>Steps the player's size by one step up or down; false when already at the end.</summary>
        internal static bool StepSize(int rank, int step)
        {
            if (!Valid(rank) || s_sizes[rank] == null) return false;
            int before = SizePercent(rank);
            int after = Mathf.Clamp(before + step * SizeStep, MinSize, MaxSize);
            if (after == before) return false;
            s_sizes[rank].Value = after;
            Save(s_sizes[rank].ConfigFile);
            return true;
        }

        /// <summary>The player's own on-screen spot (bottom centre, screen fractions), or false for Auto.</summary>
        internal static bool Position(int rank, out Vector2 position)
        {
            position = new Vector2(0.5f, 0.03f);
            if (!Valid(rank) || s_positions[rank] == null) return false;
            return Parse(s_positions[rank].Value, out position);
        }

        /// <summary>Saves the player's spot; null goes back to Auto.</summary>
        internal static void SetPosition(int rank, Vector2? position)
        {
            if (!Valid(rank) || s_positions[rank] == null) return;
            s_positions[rank].Value = position.HasValue
                ? Clamp01(position.Value.x).ToString("0.000", CultureInfo.InvariantCulture) + "," +
                  Clamp01(position.Value.y).ToString("0.000", CultureInfo.InvariantCulture)
                : "Auto";
            Save(s_positions[rank].ConfigFile);
            Plugin.Log.LogInfo("Vị trí thanh kỹ năng của người chơi " + (rank + 1) + ": " + s_positions[rank].Value);
        }

        private static float Clamp01(float value)
        {
            return Mathf.Clamp01(value);
        }

        private static bool Parse(string text, out Vector2 position)
        {
            position = new Vector2(0.5f, 0.03f);
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(',');
            float x, y;
            if (parts.Length != 2 ||
                !float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return false;
            position = new Vector2(Mathf.Clamp01(x), Mathf.Clamp01(y));
            return true;
        }

        private static void Save(ConfigFile file)
        {
            try
            {
                file.Save();
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không lưu được cài đặt thanh kỹ năng: " + error.Message);
            }
        }

        /// <summary>The mode as the skill screen's DISPLAY row shows it.</summary>
        internal static string Name(SkillBarMode mode)
        {
            switch (mode)
            {
                case SkillBarMode.AboveHead: return Lang.T("OVER HEAD", "TRÊN ĐẦU");
                case SkillBarMode.Hidden: return Lang.T("HIDDEN", "ẨN");
                default: return Lang.T("ON SCREEN", "TRÊN MÀN HÌNH");
            }
        }

        /// <summary>The spot as the skill screen's BAR POSITION row shows it.</summary>
        internal static string PositionName(int rank)
        {
            if (Get(rank) != SkillBarMode.OnScreen) return "-";
            Vector2 spot;
            return Position(rank, out spot) ? Lang.T("CUSTOM", "TỰ CHỌN") : Lang.T("AUTO", "TỰ ĐỘNG");
        }

        private static string FormatMode(object value)
        {
            SkillBarMode mode = value is SkillBarMode ? (SkillBarMode)value : SkillBarMode.OnScreen;
            switch (mode)
            {
                case SkillBarMode.AboveHead: return Lang.T("Above the chef's head", "Trên đầu đầu bếp");
                case SkillBarMode.Hidden: return Lang.T("Hidden", "Ẩn");
                default: return Lang.T("On screen", "Trên màn hình");
            }
        }
    }
}
