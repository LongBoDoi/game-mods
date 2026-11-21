using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
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
    /// What each skill level is worth, read from the BepInEx config so it can be tuned without a
    /// rebuild. A level is worth PercentPerLevel: at 12, level +3 is 36% faster and level -2 is
    /// 24% slower.
    /// </summary>
    internal static class SkillStats
    {
        internal static readonly int Count = Enum.GetValues(typeof(Skill)).Length;

        private const float MinMultiplier = 0.1f;
        private const float MaxMultiplier = 5f;

        private static readonly ConfigEntry<int>[] Percent = new ConfigEntry<int>[Count];
        private static ConfigEntry<int> _maxLevel;

        /// <summary>Skill name on the screen.</summary>
        internal static readonly string[] Names =
        {
            "COOKING", "SLOW BURN", "CHOPPING", "WASHING", "SERVING", "RUNNING", "DASHING", "THROWING", "FIREFIGHTING",
        };

        /// <summary>The line under a player's skills that explains the highlighted one.</summary>
        internal static readonly string[] Descriptions =
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

        internal static void Bind(ConfigFile config)
        {
            _maxLevel = config.Bind("General", "MaxLevel", 3,
                new ConfigDescription("Mức cao nhất (và thấp nhất, số âm) của mỗi kỹ năng.",
                    new AcceptableValueRange<int>(1, 5)));

            BindSkill(config, Skill.Cooking, 12, "Tốc độ nấu/trộn của nồi, chảo, bát trộn mà đầu bếp cho nguyên liệu vào gần nhất");
            BindSkill(config, Skill.SlowBurn, 20, "Thời gian món đã chín giữ được trước khi cháy (hoặc trộn quá tay)");
            BindSkill(config, Skill.Chopping, 12, "Tốc độ thái");
            BindSkill(config, Skill.Washing, 15, "Tốc độ rửa đĩa");
            BindSkill(config, Skill.Serving, 15, "Tốc độ đĩa bẩn quay lại sau khi đầu bếp giao món");
            BindSkill(config, Skill.Running, 6, "Tốc độ chạy");
            BindSkill(config, Skill.Dashing, 15, "Quãng lướt và tốc độ hồi lướt");
            BindSkill(config, Skill.Throwing, 12, "Độ xa khi ném");
            BindSkill(config, Skill.Firefighting, 20, "Tốc độ dập lửa của bình cứu hỏa");
        }

        private static void BindSkill(ConfigFile config, Skill skill, int percent, string what)
        {
            Percent[(int)skill] = config.Bind("Skill." + skill, "PercentPerLevel", percent,
                new ConfigDescription(what + ": mỗi mức +1 nhanh hơn (mỗi mức -1 chậm hơn) bấy nhiêu phần trăm.",
                    new AcceptableValueRange<int>(0, 50)));
        }

        // Online, the host's numbers replace this machine's own, so every machine computes the
        // same speed for the same chef (null: use this machine's config).
        private static int s_hostMaxLevel;
        private static int[] s_hostPercent;

        internal static int MaxLevel
        {
            get
            {
                if (s_hostPercent != null) return s_hostMaxLevel;
                return _maxLevel != null ? Mathf.Clamp(_maxLevel.Value, 1, 5) : 3;
            }
        }

        private static int PercentOf(Skill skill)
        {
            if (s_hostPercent != null) return s_hostPercent[(int)skill];
            ConfigEntry<int> entry = Percent[(int)skill];
            return entry != null ? Mathf.Clamp(entry.Value, 0, 50) : 0;
        }

        /// <summary>1 = game default, 1.36 = 36% faster, 0.76 = 24% slower.</summary>
        internal static float Multiplier(Skill skill, int level)
        {
            if (level == 0) return 1f;
            return Mathf.Clamp(1f + PercentOf(skill) * level / 100f, MinMultiplier, MaxMultiplier);
        }

        /// <summary>This machine's own numbers, as the host publishes them: "max|p.p.p...".</summary>
        internal static string FormatOwnRules()
        {
            var text = new StringBuilder();
            text.Append((_maxLevel != null ? Mathf.Clamp(_maxLevel.Value, 1, 5) : 3).ToString(CultureInfo.InvariantCulture)).Append('|');
            for (int i = 0; i < Count; i++)
            {
                if (i > 0) text.Append('.');
                ConfigEntry<int> entry = Percent[i];
                text.Append((entry != null ? Mathf.Clamp(entry.Value, 0, 50) : 0).ToString(CultureInfo.InvariantCulture));
            }
            // The special-skill switches (see SpecialCatalog.OwnFlags).
            text.Append('|').Append(SpecialCatalog.OwnFlags().ToString(CultureInfo.InvariantCulture));
            return text.ToString();
        }

        /// <summary>
        /// Uses the host's numbers (from <see cref="FormatOwnRules"/> on the host) instead of this
        /// machine's own; null or unreadable goes back to this machine's own config.
        /// </summary>
        internal static void UseHostRules(string rules)
        {
            int max;
            int[] percent = null;
            int? flags = null;
            if (!string.IsNullOrEmpty(rules))
            {
                string[] parts = rules.Split('|');
                string[] values = parts.Length >= 2 ? parts[1].Split('.') : null;
                int flagValue;
                if (parts.Length >= 3 &&
                    int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out flagValue))
                    flags = flagValue;
                if (values != null && values.Length == Count &&
                    int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
                {
                    percent = new int[Count];
                    for (int i = 0; i < Count && percent != null; i++)
                    {
                        int value;
                        if (int.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                            percent[i] = Mathf.Clamp(value, 0, 50);
                        else percent = null;
                    }
                    s_hostMaxLevel = Mathf.Clamp(max, 1, 5);
                }
            }
            s_hostPercent = percent;
            SpecialCatalog.UseHostFlags(percent != null ? flags : null);
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
        /// Points freed by lowering skills and not yet spent raising others: 0 when balanced, below 0
        /// when more were spent than freed (only a balanced build can be locked in).
        /// </summary>
        internal static int FreePoints(int[] levels)
        {
            int sum = 0;
            for (int i = 0; i < levels.Length; i++) sum += levels[i];
            return -sum;
        }

        /// <summary>Free points counting what the loadout's quirk frees.</summary>
        internal static int FreePoints(int[] levels, int[] loadout)
        {
            return FreePoints(levels) + SpecialCatalog.Refund(loadout);
        }

        /// <summary>
        /// A build that is valid under the current settings: every level within MaxLevel and no
        /// more points spent than freed (counting <paramref name="refund"/> from a quirk).
        /// Anything else (say MaxLevel was lowered) starts over at 0.
        /// </summary>
        internal static int[] Sanitise(int[] levels, int refund)
        {
            int[] result = Empty();
            if (levels == null) return result;
            int max = MaxLevel;
            for (int i = 0; i < result.Length && i < levels.Length; i++)
            {
                if (levels[i] > max || levels[i] < -max) return Empty();
                result[i] = levels[i];
            }
            return FreePoints(result) + refund < 0 ? Empty() : result;
        }

        /// <summary>
        /// A build under the rules in use (online, the host's): each level clamped to MaxLevel,
        /// and all 0 if it spends more points than it frees (counting a quirk's refund).
        /// </summary>
        internal static int[] Enforce(int[] levels, int refund)
        {
            int[] result = Empty();
            if (levels == null) return result;
            int max = MaxLevel;
            for (int i = 0; i < result.Length && i < levels.Length; i++)
                result[i] = Mathf.Clamp(levels[i], -max, max);
            return FreePoints(result) + refund < 0 ? Empty() : result;
        }

        /// <summary>"COOKING +2, RUNNING -2" for the log; "no changes" for an all-zero build.</summary>
        internal static string Summary(int[] levels)
        {
            var text = new StringBuilder();
            for (int i = 0; i < Count && levels != null && i < levels.Length; i++)
            {
                if (levels[i] == 0) continue;
                if (text.Length > 0) text.Append(", ");
                text.Append(Names[i]).Append(' ').Append(levels[i] > 0 ? "+" : "").Append(levels[i])
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
            _levels[player] = SkillStats.Enforce(levels, SpecialCatalog.Refund(loadout));
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
                SpecialCatalog.Refund(LastPickedSpecials(player)));
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
