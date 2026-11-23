using System;
using BepInEx.Configuration;

namespace ChefSkills
{
    /// <summary>Where a player's skill bar is drawn in a level.</summary>
    internal enum SkillBarMode { OnScreen, AboveHead, Hidden }

    /// <summary>
    /// <c>[Display] SkillBarPlayer1..4</c>: each local player's own choice of where their skill bar
    /// shows in a level: along the bottom of the screen, over their chef's head, or not at all. A
    /// player is counted among the players on this PC in player order (so online, the only player
    /// here is always player 1). Also set on the skill screen's DISPLAY row.
    /// </summary>
    internal static class SkillBarSettings
    {
        internal const int Players = 4;

        private static readonly ConfigEntry<SkillBarMode>[] s_modes = new ConfigEntry<SkillBarMode>[Players];

        internal static void Bind(ConfigFile config)
        {
            for (int i = 0; i < Players; i++)
                s_modes[i] = config.Bind("Display", "SkillBarPlayer" + (i + 1), SkillBarMode.OnScreen, new ConfigDescription(
                    "Where player " + (i + 1) + " on this PC (counted in player order, online too) sees their skill bar in a level: " +
                    "OnScreen (along the bottom of the screen), AboveHead (over their chef) or Hidden. " +
                    "Also set on the skill screen's DISPLAY row.",
                    null,
                    new ConfigurationManagerAttributes { DispName = "Skill bar: player " + (i + 1), ObjToStr = Format, Order = -10 - i }));
        }

        /// <summary>The mode of the <paramref name="rank"/>-th player on this PC (0 = first).</summary>
        internal static SkillBarMode Get(int rank)
        {
            if (rank < 0 || rank >= Players || s_modes[rank] == null) return SkillBarMode.OnScreen;
            SkillBarMode mode = s_modes[rank].Value;
            return Enum.IsDefined(typeof(SkillBarMode), mode) ? mode : SkillBarMode.OnScreen;
        }

        /// <summary>Steps the player's mode forward or back (wrapping) and saves it.</summary>
        internal static SkillBarMode Step(int rank, int step)
        {
            int count = Enum.GetValues(typeof(SkillBarMode)).Length;
            var mode = (SkillBarMode)((((int)Get(rank) + step) % count + count) % count);
            if (rank < 0 || rank >= Players || s_modes[rank] == null) return mode;
            s_modes[rank].Value = mode;
            try
            {
                s_modes[rank].ConfigFile.Save();
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không lưu được cách hiện thanh kỹ năng: " + error.Message);
            }
            Plugin.Log.LogInfo("Thanh kỹ năng của người chơi " + (rank + 1) + " trên máy này: " + mode);
            return mode;
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

        private static string Format(object value)
        {
            SkillBarMode mode = value is SkillBarMode ? (SkillBarMode)value : SkillBarMode.OnScreen;
            switch (mode)
            {
                case SkillBarMode.AboveHead: return Lang.T("Above the chef's head", "Trên đầu đầu bếp");
                case SkillBarMode.Hidden: return Lang.T("Hidden", "Ẩn");
                default: return Lang.T("On screen (bottom)", "Trên màn hình (phía dưới)");
            }
        }
    }
}
