using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace ChefRoles
{
    /// <summary>The roles a player can pick. None is the game's default behaviour.</summary>
    public enum ChefRole
    {
        None,
        Cook,
        Server
    }

    /// <summary>The four things a role changes.</summary>
    public enum RoleStat
    {
        /// <summary>How fast pots/pans this chef last filled cook.</summary>
        Cook,
        /// <summary>How fast this chef chops.</summary>
        Chop,
        /// <summary>How fast dirty plates come back after this chef delivers a dish.</summary>
        Serve,
        /// <summary>How fast this chef runs.</summary>
        Run
    }

    /// <summary>
    /// Per-role multipliers, read from the BepInEx config so they can be tuned without a rebuild.
    /// 1.0 = game default, 1.5 = 50% faster, 0.75 = 25% slower.
    /// </summary>
    internal static class RoleStats
    {
        private const float Min = 0.1f;
        private const float Max = 5f;

        private static readonly Dictionary<ChefRole, Dictionary<RoleStat, ConfigEntry<float>>> Entries =
            new Dictionary<ChefRole, Dictionary<RoleStat, ConfigEntry<float>>>();

        internal static void Bind(ConfigFile config)
        {
            BindRole(config, ChefRole.Cook, 1.5f, 1.0f, 0.75f, 1.0f);
            BindRole(config, ChefRole.Server, 0.75f, 0.75f, 1.5f, 1.25f);
        }

        private static void BindRole(ConfigFile config, ChefRole role, float cook, float chop, float serve, float run)
        {
            string section = "Role." + role;
            var stats = new Dictionary<RoleStat, ConfigEntry<float>>();
            stats[RoleStat.Cook] = config.Bind(section, "CookSpeed", cook,
                "Tốc độ nấu của nồi/chảo mà đầu bếp này cho nguyên liệu vào gần nhất (1 = mặc định).");
            stats[RoleStat.Chop] = config.Bind(section, "ChopSpeed", chop,
                "Tốc độ thái (1 = mặc định).");
            stats[RoleStat.Serve] = config.Bind(section, "ServeSpeed", serve,
                "Tốc độ đĩa bẩn quay lại sau khi đầu bếp này giao món (1 = mặc định).");
            stats[RoleStat.Run] = config.Bind(section, "RunSpeed", run,
                "Tốc độ chạy (1 = mặc định).");
            Entries[role] = stats;
        }

        internal static float Get(ChefRole role, RoleStat stat)
        {
            Dictionary<RoleStat, ConfigEntry<float>> stats;
            if (!Entries.TryGetValue(role, out stats)) return 1f;
            ConfigEntry<float> entry;
            if (!stats.TryGetValue(stat, out entry)) return 1f;
            return Mathf.Clamp(entry.Value, Min, Max);
        }

        /// <summary>Upper-case name shown on the role-select screen.</summary>
        internal static string DisplayName(ChefRole role)
        {
            switch (role)
            {
                case ChefRole.Cook: return "COOK";
                case ChefRole.Server: return "SERVER";
                default: return "NO ROLE";
            }
        }

        /// <summary>Short effect summary built from the live config, e.g. "Cook +50%, Serve -25%".</summary>
        internal static string Summary(ChefRole role)
        {
            if (role == ChefRole.None) return "Game default";
            var parts = new List<string>();
            AddPart(parts, "Cook", Get(role, RoleStat.Cook));
            AddPart(parts, "Chop", Get(role, RoleStat.Chop));
            AddPart(parts, "Serve", Get(role, RoleStat.Serve));
            AddPart(parts, "Run", Get(role, RoleStat.Run));
            return parts.Count == 0 ? "Game default" : string.Join(", ", parts.ToArray());
        }

        private static void AddPart(List<string> parts, string label, float multiplier)
        {
            int percent = Mathf.RoundToInt((multiplier - 1f) * 100f);
            if (percent == 0) return;
            parts.Add(string.Format("{0} {1}{2}%", label, percent > 0 ? "+" : "", percent));
        }
    }

    /// <summary>
    /// Role per local player for the current level, keyed by PlayerIDProvider.GetID(). Remote chefs
    /// (ID = Player.Count on this machine) always count as None.
    /// </summary>
    public sealed class RoleManager
    {
        private readonly Dictionary<PlayerInputLookup.Player, ChefRole> _roles =
            new Dictionary<PlayerInputLookup.Player, ChefRole>();

        // What each player picked last time, used as the starting choice on the next level.
        private readonly Dictionary<PlayerInputLookup.Player, ChefRole> _lastPicked =
            new Dictionary<PlayerInputLookup.Player, ChefRole>();

        public void Set(PlayerInputLookup.Player player, ChefRole role)
        {
            _roles[player] = role;
            _lastPicked[player] = role;
        }

        public ChefRole Get(PlayerInputLookup.Player player)
        {
            ChefRole role;
            return _roles.TryGetValue(player, out role) ? role : ChefRole.None;
        }

        public ChefRole LastPicked(PlayerInputLookup.Player player)
        {
            ChefRole role;
            return _lastPicked.TryGetValue(player, out role) ? role : ChefRole.None;
        }

        public void Clear()
        {
            _roles.Clear();
        }

        /// <summary>Role of the chef that owns this GameObject; None for remote or non-chef objects.</summary>
        public ChefRole RoleOf(GameObject chef)
        {
            if (chef == null) return ChefRole.None;
            PlayerIDProvider provider = chef.GetComponent<PlayerIDProvider>();
            if (provider == null || !provider.IsLocallyControlled()) return ChefRole.None;
            return Get(provider.GetID());
        }

        /// <summary>Multiplier for a stat of the chef that owns this GameObject (1 when disabled).</summary>
        public float Multiplier(GameObject chef, RoleStat stat)
        {
            if (!Plugin.Enabled.Value) return 1f;
            return RoleStats.Get(RoleOf(chef), stat);
        }
    }
}
