using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Saves today's menu so the same set of dishes can be put back tomorrow in one click.
///
/// A preset is a list of recipe keys, nothing more. It is deliberately not a list of Recipe
/// objects: those are ScriptableObject assets that only exist while a save is loaded, so a
/// preset built from them would not survive the restart it exists to survive. Keys do, and
/// <see cref="MenuTweaks.RecipeByKey"/> turns them back into the same assets - by comparing
/// keys rather than by asking the game's own name lookup, which is the lookup that confuses
/// members of a variant family for each other.
///
/// Applying one goes through <see cref="MenuTweaks.ClearMenu"/> and
/// <see cref="MenuTweaks.AddDish"/>, which are the game's own networked menu calls. That is
/// what makes a preset work in co-op: it replicates exactly as though the dishes had been
/// tapped in on the tablet one at a time, and AddDish's read-back check still catches a dish
/// the game substitutes for another.
///
/// A dish the current save has not unlocked simply does not come back, and is reported rather
/// than skipped in silence - a preset saved on a later save file would otherwise appear to
/// half-work with no explanation.
/// </summary>
public static class MenuPresets
{
    /// <summary>
    /// How many presets are kept. A hard cap, because the panel lists them all with no paging
    /// and the config line is a single string.
    /// </summary>
    public const int MaxPresets = 8;

    public static string Status = "chưa lưu thực đơn nào";

    /// <summary>One saved menu: a name to show and the dishes it holds, in menu order.</summary>
    public sealed class Preset
    {
        public string Name;
        public readonly List<string> Keys = new();

        /// <summary>The row's summary text, cached; see <see cref="Describe"/>.</summary>
        internal string Summary;

        /// <summary>True once every key in the summary resolved to a real recipe.</summary>
        internal bool SummaryComplete;

        /// <summary>When an incomplete summary is worth building again.</summary>
        internal float SummaryRetryAt;
    }

    private static readonly List<Preset> Loaded = new();

    /// <summary>The config text the list was last built from, so parsing is not per frame.</summary>
    private static string _raw;
    private static bool _everLoaded;

    // ------------------------------------------------------------------ reading

    public static List<Preset> All()
    {
        Sync();
        return Loaded;
    }

    public static bool CanSaveMore()
    {
        Sync();
        return Loaded.Count < MaxPresets;
    }

    /// <summary>
    /// What a preset holds, written out for its row in the panel.
    ///
    /// Names rather than a count, because a count says nothing about which menu this is - and
    /// telling one saved menu from another is the whole reason to save them separately. The
    /// first few are enough to recognise it by; the rest become "+n".
    ///
    /// Cached, and deliberately so. Turning a key back into a name is a walk of the whole
    /// recipe catalogue, the panel redraws several times a frame, and every preset would pay
    /// for three of those walks on each one. The cache is dropped when the saved list changes;
    /// a summary that still has unresolved keys - which is what it looks like before the game
    /// has loaded its recipes - is rebuilt every couple of seconds until it stops having any.
    /// </summary>
    public static string Describe(Preset preset, int maxNames = 3)
    {
        if (preset == null || preset.Keys.Count == 0) return "trống";

        if (preset.Summary != null &&
            (preset.SummaryComplete || Time.unscaledTime < preset.SummaryRetryAt))
            return preset.Summary;

        var sb = new StringBuilder();
        int shown = Math.Min(maxNames, preset.Keys.Count);
        bool complete = true;

        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(", ");

            var recipe = MenuTweaks.RecipeByKey(preset.Keys[i]);
            if (recipe == null) { complete = false; sb.Append(preset.Keys[i]); }
            else sb.Append(MenuTweaks.DisplayName(recipe));
        }

        if (preset.Keys.Count > shown) sb.Append(" +").Append(preset.Keys.Count - shown);

        preset.Summary = sb.ToString();
        preset.SummaryComplete = complete;
        preset.SummaryRetryAt = Time.unscaledTime + 2f;

        return preset.Summary;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Saves today's menu under the next free name.</summary>
    public static bool SaveCurrent()
    {
        Sync();

        if (Loaded.Count >= MaxPresets)
        {
            Status = $"đã đủ {MaxPresets} thực đơn lưu - hãy xóa bớt";
            return false;
        }

        var today = MenuTweaks.TodaysMenu();
        if (today.Count == 0)
        {
            Status = "thực đơn hôm nay đang trống - không có gì để lưu";
            return false;
        }

        var preset = new Preset { Name = NextName() };

        foreach (var r in today)
        {
            string key = MenuTweaks.Key(r);
            if (!string.IsNullOrEmpty(key)) preset.Keys.Add(key);
        }

        if (preset.Keys.Count == 0)
        {
            Status = "không đọc được tên món nào trên thực đơn";
            return false;
        }

        Loaded.Add(preset);
        Store();

        Status = $"đã lưu '{preset.Name}' ({preset.Keys.Count} món)";
        Plugin.Logger.LogInfo(
            $"Menu: saved preset '{preset.Name}' = {string.Join(", ", preset.Keys)}");
        return true;
    }

    /// <summary>Replaces today's menu with a saved one.</summary>
    public static bool Apply(Preset preset)
    {
        if (preset == null) return false;
        Sync();

        // Resolved before anything is cleared. Wiping today's menu and only then discovering
        // that none of the preset's dishes exist in this save would leave the player with no
        // menu at all and nothing to put back.
        var recipes = new List<Recipe>();
        var missing = new List<string>();

        foreach (var key in preset.Keys)
        {
            var r = MenuTweaks.RecipeByKey(key);
            if (r == null) missing.Add(key); else recipes.Add(r);
        }

        if (recipes.Count == 0)
        {
            Status = missing.Count == 0
                ? $"'{preset.Name}' đang trống"
                : $"'{preset.Name}': không có món nào trong bản lưu này";
            return false;
        }

        MenuTweaks.ClearMenu();

        int added = 0;
        var refused = new List<string>();

        foreach (var r in recipes)
        {
            if (MenuTweaks.AddDish(r)) added++;
            else refused.Add(MenuTweaks.DisplayName(r));
        }

        var note = new StringBuilder($"đã dùng '{preset.Name}' - {added}/{preset.Keys.Count} món");
        if (missing.Count > 0) note.Append($", thiếu {missing.Count} món chưa mở khóa");
        if (refused.Count > 0) note.Append($", {refused.Count} món bị từ chối");

        Status = note.ToString();

        Plugin.Logger.LogInfo(
            $"Menu: applied preset '{preset.Name}': {added} added" +
            (missing.Count > 0 ? $", missing [{string.Join(", ", missing)}]" : "") +
            (refused.Count > 0 ? $", refused [{string.Join(", ", refused)}]" : "") + ".");

        return added > 0;
    }

    /// <summary>Overwrites a preset with today's menu, keeping its name.</summary>
    public static bool Overwrite(Preset preset)
    {
        if (preset == null) return false;
        Sync();

        var today = MenuTweaks.TodaysMenu();
        if (today.Count == 0)
        {
            Status = "thực đơn hôm nay đang trống - không có gì để lưu";
            return false;
        }

        preset.Keys.Clear();
        foreach (var r in today)
        {
            string key = MenuTweaks.Key(r);
            if (!string.IsNullOrEmpty(key)) preset.Keys.Add(key);
        }

        // Overwriting keeps the same Preset object, so Sync will not rebuild it and the row
        // would go on describing the menu this one just replaced.
        preset.Summary = null;
        preset.SummaryComplete = false;

        Store();
        Status = $"đã ghi đè '{preset.Name}' ({preset.Keys.Count} món)";
        return true;
    }

    public static void Delete(Preset preset)
    {
        if (preset == null) return;
        Sync();

        if (!Loaded.Remove(preset)) return;

        Store();
        Status = $"đã xóa '{preset.Name}'";
    }

    // ------------------------------------------------------------------ storage

    /// <summary>
    /// The next free "Thực đơn N".
    ///
    /// Numbered from what is free rather than from the count, so deleting the second of three
    /// presets does not produce a second "Thực đơn 3".
    /// </summary>
    private static string NextName()
    {
        for (int n = 1; n <= MaxPresets + 1; n++)
        {
            string candidate = $"Thực đơn {n}";

            bool taken = false;
            foreach (var p in Loaded)
                if (string.Equals(p.Name, candidate, StringComparison.Ordinal)) { taken = true; break; }

            if (!taken) return candidate;
        }

        return $"Thực đơn {Loaded.Count + 1}";
    }

    private static void Sync()
    {
        string raw = Plugin.MenuPresetData.Value ?? "";
        if (_everLoaded && raw == _raw) return;

        _raw = raw;
        _everLoaded = true;
        Loaded.Clear();

        foreach (var entry in raw.Split('|'))
        {
            if (entry.Length == 0) continue;

            int split = entry.IndexOf('~');
            if (split <= 0) continue;

            var preset = new Preset { Name = entry.Substring(0, split).Trim() };
            if (preset.Name.Length == 0) continue;

            foreach (var key in entry.Substring(split + 1).Split(','))
            {
                var trimmed = key.Trim();
                if (trimmed.Length > 0) preset.Keys.Add(trimmed);
            }

            Loaded.Add(preset);
            if (Loaded.Count >= MaxPresets) break;
        }

        UpdateStatus();
    }

    private static void Store()
    {
        var sb = new StringBuilder();

        foreach (var p in Loaded)
        {
            if (sb.Length > 0) sb.Append('|');
            sb.Append(Clean(p.Name)).Append('~').Append(string.Join(",", p.Keys));
        }

        _raw = sb.ToString();
        Plugin.MenuPresetData.Value = _raw;
        Plugin.MenuPresetData.ConfigFile.Save();
    }

    /// <summary>Keeps a name out of the separators the config line is built from.</summary>
    private static string Clean(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Thực đơn";
        return name.Replace('|', ' ').Replace('~', ' ').Replace(',', ' ').Trim();
    }

    private static void UpdateStatus()
    {
        Status = Loaded.Count == 0
            ? "chưa lưu thực đơn nào"
            : $"đã lưu {Loaded.Count}/{MaxPresets} thực đơn";
    }
}
