using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;

namespace ChefSkills
{
    /// <summary>
    /// The skills a chef can trade levels between. Each one has a level from -MaxLevel to
    /// +MaxLevel (0 = game default), and a chef's levels always add up to zero: raising one skill
    /// costs a point that has to come from lowering another.
    /// </summary>
    public enum Skill
    {
        /// <summary>Cook/mix speed of pots, pans and mixing bowls this chef last filled or placed.</summary>
        Cooking,
        /// <summary>How long that chef's finished food takes to burn (or over-mix).</summary>
        SlowBurn,
        /// <summary>How much each chop counts.</summary>
        Chopping,
        /// <summary>How fast this chef washes plates at a sink.</summary>
        Washing,
        /// <summary>How soon the dirty plate comes back after this chef hands a dish in.</summary>
        Serving,
        /// <summary>Walking/running speed.</summary>
        Running,
        /// <summary>Dash distance and how soon the dash recharges.</summary>
        Dashing,
        /// <summary>How far this chef throws.</summary>
        Throwing,
        /// <summary>How fast this chef's fire extinguisher puts fires out.</summary>
        Firefighting,
    }

    /// <summary>
    /// What each skill level is worth. The numbers are fixed (no config), so a build means the
    /// same on every machine: at 12% per level, level +3 is 36% faster and level -2 is 24% slower.
    /// </summary>
    internal static class SkillStats
    {
        internal static readonly int Count = Enum.GetValues(typeof(Skill)).Length;

        private const float MinMultiplier = 0.1f;
        private const float MaxMultiplier = 5f;

        /// <summary>The highest level a skill can be set to on the card (and minus it, the lowest).</summary>
        internal const int MaxLevel = 3;

        /// <summary>
        /// The most points lowering skills can free (a quirk's points come on top): without a cap a
        /// chef could dump every skill they never use and max everything else.
        /// </summary>
        internal const int MaxPointsFreed = 2 * MaxLevel;

        // What one level of each skill is worth, in percent, in Skill order: Cooking, Slow burn,
        // Chopping, Washing, Serving, Running, Dashing, Throwing, Firefighting.
        private static readonly int[] PercentPerLevel = { 12, 20, 12, 15, 20, 6, 10, 12, 20 };

        // Skill names in English (also what the log uses) and in Vietnamese.
        private static readonly string[] NamesEn =
        {
            "COOKING", "SLOW BURN", "CHOPPING", "WASHING", "SERVING", "RUNNING", "DASHING", "THROWING", "FIREFIGHTING",
        };

        private static readonly string[] NamesVi =
        {
            "NẤU", "LÂU CHÁY", "THÁI", "RỬA ĐĨA", "PHỤC VỤ", "CHẠY", "LƯỚT", "NÉM", "CHỮA CHÁY",
        };

        /// <summary>Skill name on the screen.</summary>
        internal static string[] Names
        {
            get { return Lang.Vi ? NamesVi : NamesEn; }
        }

        /// <summary>
        /// A skill's name followed by <paramref name="suffix"/>, in both languages (Lang.Both), for
        /// text sent to the other machines.
        /// </summary>
        internal static string NameBoth(Skill skill, string suffix)
        {
            return Lang.Both(NamesEn[(int)skill] + suffix, NamesVi[(int)skill] + suffix);
        }

        /// <summary>The line under a player's skills that explains the highlighted one.</summary>
        internal static string[] Descriptions
        {
            get { return Lang.Vi ? DescriptionsVi : DescriptionsEn; }
        }

        private static readonly string[] DescriptionsVi =
        {
            "Nồi, chảo, bát trộn bạn cho đồ vào nấu và trộn nhanh cỡ nào",
            "Món bạn nấu giữ được bao lâu trước khi cháy",
            "Mỗi nhát thái của bạn tính được bao nhiêu",
            "Bạn rửa đĩa bẩn nhanh cỡ nào",
            "Đĩa quay lại sớm cỡ nào sau khi bạn giao món",
            "Bạn chạy nhanh cỡ nào",
            "Bạn lướt xa cỡ nào và hồi lướt nhanh cỡ nào",
            "Bạn ném xa cỡ nào",
            "Bình chữa cháy của bạn dập lửa nhanh cỡ nào",
        };

        private static readonly string[] DescriptionsEn =
        {
            "How fast pots, pans and mixers you fill cook and mix",
            "How long the food you cooked lasts before it burns",
            "How much each of your chops counts",
            "How fast you wash dirty plates",
            "How soon the plate comes back after you serve",
            "How fast you run",
            "How far you dash and how soon it recharges",
            "How far you throw",
            "How fast your extinguisher puts out fire",
        };

        /// <summary>
        /// The skill numbers used to be settings (`MaxLevel`, `MaxPointsFreed`, `BalanceVersion` and
        /// every `[Skill.*]` section). They are fixed now, so an old config file's entries for them
        /// are dropped from the file, which would otherwise keep them as if they still did something.
        /// </summary>
        internal static void Bind(ConfigFile config)
        {
            try
            {
                PropertyInfo property = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries");
                var orphans = property != null ? property.GetValue(config, null) as Dictionary<ConfigDefinition, string> : null;
                if (orphans == null) return;
                var old = new List<ConfigDefinition>();
                foreach (ConfigDefinition definition in orphans.Keys)
                    if (definition.Section.StartsWith("Skill.") ||
                        (definition.Section == "General" &&
                         (definition.Key == "MaxLevel" || definition.Key == "MaxPointsFreed" || definition.Key == "BalanceVersion")))
                        old.Add(definition);
                if (old.Count == 0) return;
                foreach (ConfigDefinition definition in old) orphans.Remove(definition);
                config.Save();
                Plugin.Log.LogInfo("Đã bỏ " + old.Count + " cài đặt số kỹ năng cũ khỏi tệp cài đặt (giờ các số này cố định).");
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không dọn được cài đặt số kỹ năng cũ: " + error.Message);
            }
        }

        /// <summary>
        /// The lowest level a skill can go to: -MaxLevel, and 0 when the loadout's quirk already
        /// takes that skill away (Heavy Boots can't dash, so lowering DASHING would be a free point).
        /// </summary>
        internal static int MinLevel(Skill skill, int[] loadout)
        {
            return SpecialCatalog.LocksSkill(loadout, skill) ? 0 : -MaxLevel;
        }

        private static int PercentOf(Skill skill)
        {
            int index = (int)skill;
            return index >= 0 && index < PercentPerLevel.Length ? PercentPerLevel[index] : 0;
        }

        /// <summary>1 = game default, 1.36 = 36% faster, 0.76 = 24% slower.</summary>
        internal static float Multiplier(Skill skill, int level)
        {
            if (level == 0) return 1f;
            return Mathf.Clamp(1f + PercentOf(skill) * level / 100f, MinMultiplier, MaxMultiplier);
        }

        /// <summary>
        /// This machine's own rules, as the host publishes them: the special-skill switches (see
        /// SpecialCatalog.OwnFlags). The skill numbers are fixed, so they needn't be sent.
        /// </summary>
        internal static string FormatOwnRules()
        {
            return SpecialCatalog.OwnFlags().ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Uses the host's rules (from <see cref="FormatOwnRules"/> on the host) instead of this
        /// machine's own; null or unreadable goes back to this machine's own config.
        /// </summary>
        internal static void UseHostRules(string rules)
        {
            int flags = 0;
            bool read = !string.IsNullOrEmpty(rules) &&
                int.TryParse(rules, NumberStyles.Integer, CultureInfo.InvariantCulture, out flags);
            SpecialCatalog.UseHostFlags(read ? (int?)flags : null);
        }

        /// <summary>The change in percent a level makes, e.g. +36 or -24.</summary>
        internal static int PercentChange(Skill skill, int level)
        {
            return Mathf.RoundToInt((Multiplier(skill, level) - 1f) * 100f);
        }

        /// <summary>A build with every skill at 0.</summary>
        internal static int[] Empty()
        {
            return new int[Count];
        }

        /// <summary>
        /// Points lowering skills frees: one per level below 0, up to <see cref="MaxPointsFreed"/>.
        /// </summary>
        internal static int PointsFreed(int[] levels)
        {
            int freed = 0;
            for (int i = 0; levels != null && i < levels.Length; i++)
                if (levels[i] < 0) freed -= levels[i];
            return Mathf.Min(freed, MaxPointsFreed);
        }

        /// <summary>
        /// Points freed by lowering skills (at most <see cref="MaxPointsFreed"/>) and not yet spent
        /// raising others: 0 when balanced, below 0 when more were spent than freed (only a
        /// balanced build can be locked in).
        /// </summary>
        internal static int FreePoints(int[] levels)
        {
            int spent = 0;
            for (int i = 0; i < levels.Length; i++)
                if (levels[i] > 0) spent += levels[i];
            return PointsFreed(levels) - spent;
        }

        /// <summary>Free points counting what the loadout's quirk frees.</summary>
        internal static int FreePoints(int[] levels, int[] loadout)
        {
            return FreePoints(levels) + SpecialCatalog.Refund(loadout);
        }

        /// <summary>
        /// A build that is valid under the current settings, for the loadout picked with it: each
        /// level moved inside its limits (MaxLevel, its floor) and no more points spent than
        /// freed. Anything that then spends too much (say MaxLevel was lowered) starts over at 0.
        /// </summary>
        internal static int[] Sanitise(int[] levels, int[] loadout)
        {
            return Enforce(levels, loadout);
        }

        /// <summary>
        /// A build under the rules in use (online, the host's): each level clamped between its
        /// floor (see <see cref="MinLevel"/>) and MaxLevel, and all 0 if it spends more points than
        /// it frees (counting the loadout's quirk).
        /// </summary>
        internal static int[] Enforce(int[] levels, int[] loadout)
        {
            int[] result = Empty();
            if (levels == null) return result;
            int max = MaxLevel;
            for (int i = 0; i < result.Length && i < levels.Length; i++)
                result[i] = Mathf.Clamp(levels[i], MinLevel((Skill)i, loadout), max);
            return FreePoints(result) + SpecialCatalog.Refund(loadout) < 0 ? Empty() : result;
        }

        /// <summary>"COOKING +2, RUNNING -2" for the log; "no changes" for an all-zero build.</summary>
        internal static string Summary(int[] levels)
        {
            var text = new StringBuilder();
            for (int i = 0; i < Count && levels != null && i < levels.Length; i++)
            {
                if (levels[i] == 0) continue;
                if (text.Length > 0) text.Append(", ");
                text.Append(NamesEn[i]).Append(' ').Append(levels[i] > 0 ? "+" : "").Append(levels[i])
                    .Append(" (").Append(PercentChange((Skill)i, levels[i]) > 0 ? "+" : "")
                    .Append(PercentChange((Skill)i, levels[i])).Append("%)");
            }
            return text.Length > 0 ? text.ToString() : "không đổi";
        }
    }

    /// <summary>
    /// Skill levels of the players on this machine for the current level, keyed by
    /// PlayerIDProvider.GetID(). Chefs of players on other machines are looked up through
    /// <see cref="SkillSync"/> by their network entity ID, on every machine: several effects are
    /// simulated on each machine separately (chopping and washing progress, and movement, which
    /// the host simulates for every chef while each player's own machine predicts it), so every
    /// machine must apply the same levels to the same chef.
    /// </summary>
    public sealed class SkillManager
    {
        private readonly Dictionary<PlayerInputLookup.Player, int[]> _levels =
            new Dictionary<PlayerInputLookup.Player, int[]>();

        // What each player picked last time, used as the starting build on the next level.
        private readonly Dictionary<PlayerInputLookup.Player, int[]> _lastPicked =
            new Dictionary<PlayerInputLookup.Player, int[]>();

        // Special skills (see SpecialCatalog): this level's, and the last picked.
        private readonly Dictionary<PlayerInputLookup.Player, int[]> _specials =
            new Dictionary<PlayerInputLookup.Player, int[]>();
        private readonly Dictionary<PlayerInputLookup.Player, int[]> _lastSpecials =
            new Dictionary<PlayerInputLookup.Player, int[]>();

        public void Set(PlayerInputLookup.Player player, int[] levels, int[] specials)
        {
            // Under the rules every machine uses (online, the host's), exactly as the other
            // machines apply this build to this player's chefs.
            int[] loadout = SpecialCatalog.Enforce(specials);
            _specials[player] = loadout;
            _levels[player] = SkillStats.Enforce(levels, loadout);
            _lastPicked[player] = (int[])levels.Clone();
            _lastSpecials[player] = specials != null ? (int[])specials.Clone() : SpecialCatalog.Empty();
        }

        public int[] LastPickedSpecials(PlayerInputLookup.Player player)
        {
            int[] specials;
            return SpecialCatalog.Enforce(_lastSpecials.TryGetValue(player, out specials) ? specials : null);
        }

        public int[] LastPicked(PlayerInputLookup.Player player)
        {
            int[] levels;
            return SkillStats.Sanitise(_lastPicked.TryGetValue(player, out levels) ? levels : null,
                LastPickedSpecials(player));
        }

        public void Clear()
        {
            _levels.Clear();
            _specials.Clear();
        }

        /// <summary>The level of a skill for the chef that owns this GameObject (0 if it isn't a chef).</summary>
        public int LevelOf(GameObject chef, Skill skill)
        {
            if (chef == null) return 0;
            PlayerIDProvider provider = chef.GetComponent<PlayerIDProvider>();
            if (provider == null) return 0;
            int level;
            if (provider.IsLocallyControlled())
            {
                int[] levels;
                level = _levels.TryGetValue(provider.GetID(), out levels) ? levels[(int)skill] : 0;
            }
            else level = SkillSync.RemoteLevel(EntityId(chef), skill);
            // Timed buffs from specials (Frenzy, Glass Cannon...), the same on every machine.
            return level + SpecialRuntime.LevelBonus(chef, skill);
        }

        /// <summary>
        /// The special-skill loadout (see <see cref="SpecialSlot"/>) of the chef that owns this
        /// GameObject; all none if it isn't a chef or has none.
        /// </summary>
        public int[] SpecialsOf(GameObject chef)
        {
            if (chef == null || !Plugin.Enabled.Value) return null;
            PlayerIDProvider provider = chef.GetComponent<PlayerIDProvider>();
            if (provider == null) return null;
            if (provider.IsLocallyControlled())
            {
                int[] specials;
                return _specials.TryGetValue(provider.GetID(), out specials) ? specials : null;
            }
            return SkillSync.RemoteSpecials(EntityId(chef));
        }

        /// <summary>Does the chef that owns this GameObject have this special (or quirk)?</summary>
        public bool Has(GameObject chef, Special special)
        {
            int[] specials = SpecialsOf(chef);
            if (specials == null) return false;
            for (int i = 0; i < specials.Length; i++)
                if (specials[i] == (int)special) return true;
            return false;
        }

        /// <summary>Multiplier for a skill of the chef that owns this GameObject (1 when disabled).</summary>
        public float Multiplier(GameObject chef, Skill skill)
        {
            if (!Plugin.Enabled.Value) return 1f;
            return SkillStats.Multiplier(skill, LevelOf(chef, skill));
        }

        /// <summary>
        /// The average multiplier of the chefs working something together (a sink two chefs wash
        /// at), or of <paramref name="fallback"/> when the list is empty.
        /// </summary>
        public float AverageMultiplier(List<GameObject> chefs, GameObject fallback, Skill skill)
        {
            if (!Plugin.Enabled.Value) return 1f;
            if (chefs == null || chefs.Count == 0) return Multiplier(fallback, skill);
            float sum = 0f;
            int count = 0;
            for (int i = 0; i < chefs.Count; i++)
            {
                if (chefs[i] == null) continue;
                sum += Multiplier(chefs[i], skill);
                count++;
            }
            return count > 0 ? sum / count : Multiplier(fallback, skill);
        }

        /// <summary>
        /// The network entity ID of a chef. The host hands these out and every machine uses the same
        /// number for the same chef, which is how the host knows whose skills a remote chef has.
        /// </summary>
        internal static uint EntityId(GameObject chef)
        {
            try
            {
                for (Transform t = chef != null ? chef.transform : null; t != null; t = t.parent)
                {
                    uint id = EntitySerialisationRegistry.GetId(t.gameObject);
                    if (id != EntitySerialisationRegistry.kInvalidEntityID) return id;
                }
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không đọc được mã thực thể của đầu bếp: " + error.Message);
            }
            return EntitySerialisationRegistry.kInvalidEntityID;
        }
    }
}
