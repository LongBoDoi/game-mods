using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>
/// Reads how many portions of each dish on today's menu are plated up and ready to serve,
/// and turns that into a colour for the corner overlay.
///
/// The number is the one already on the card above a service counter - the row of dots -
/// which is ServiceCounter.ActiveServings out of MaxServingCount. A dish plated onto two
/// counters is the sum of both, because that is how many a waiter can still take.
///
/// Counting service counters rather than raw ingredients is deliberate, and it is a
/// correction: the first version of this measured the ingredients in the fridges and
/// worked out how many portions could in principle be cooked. That is a different
/// question, and not the one being asked - a kitchen full of chicken still has nothing to
/// serve until someone plates it. There are also only a handful of counters, so the
/// reading costs a single FindObjectsByType rather than a walk over every item in the
/// restaurant.
///
/// The recipe ingredient lists are still read, but only to tell a real dish apart from
/// the heading of a variant family - see <see cref="HasIngredients"/>.
/// </summary>
public static class DishStatus
{
    public enum Level
    {
        /// <summary>Nothing plated: no counter is holding this dish.</summary>
        Out,
        /// <summary>Down to the last few portions, at or below the configured threshold.</summary>
        Low,
        /// <summary>Comfortably stocked.</summary>
        Full,
        /// <summary>Not a cookable dish - a variant family heading that got onto the menu.</summary>
        Unknown,
    }

    public sealed class Entry
    {
        public string Name;

        /// <summary>Portions ready to serve across every counter; -1 when not a real dish.</summary>
        public int Servings;

        /// <summary>What those counters could hold in total, for the log.</summary>
        public int Capacity;

        public Level Level;

        /// <summary>Kept out of the overlay by choice. Still listed in the panel.</summary>
        public bool Hidden;
    }

    // ------------------------------------------------------------ what to show

    private static readonly HashSet<string> HiddenNames =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The config text the set was last built from, so parsing is not per frame.</summary>
    private static string _hiddenRaw;

    private static void SyncHidden()
    {
        string raw = Plugin.DishHudHidden.Value ?? "";
        if (raw == _hiddenRaw) return;

        _hiddenRaw = raw;
        HiddenNames.Clear();

        foreach (var part in raw.Split('|'))
        {
            var name = part.Trim();
            if (name.Length > 0) HiddenNames.Add(name);
        }
    }

    public static bool IsHidden(string dishName)
    {
        if (string.IsNullOrEmpty(dishName)) return false;
        SyncHidden();
        return HiddenNames.Contains(dishName);
    }

    /// <summary>
    /// Shows or hides one dish in the overlay.
    ///
    /// Dishes are remembered by display name rather than by recipe, because the list has to
    /// survive a session: a Recipe pointer means nothing next time the game starts, and the
    /// name is what the player picked from anyway.
    /// </summary>
    public static void SetHidden(string dishName, bool hide)
    {
        if (string.IsNullOrEmpty(dishName)) return;

        SyncHidden();

        bool changed = hide ? HiddenNames.Add(dishName) : HiddenNames.Remove(dishName);
        if (!changed) return;

        _hiddenRaw = string.Join("|", HiddenNames);
        Plugin.DishHudHidden.Value = _hiddenRaw;
        Plugin.DishHudHidden.ConfigFile.Save();

        Invalidate();
    }

    /// <summary>Puts every dish back on the overlay.</summary>
    public static void ShowAll()
    {
        SyncHidden();
        if (HiddenNames.Count == 0) return;

        HiddenNames.Clear();
        _hiddenRaw = "";
        Plugin.DishHudHidden.Value = "";
        Plugin.DishHudHidden.ConfigFile.Save();

        Invalidate();
    }

    /// <summary>The current reading, in menu order. Only ever replaced between frames.</summary>
    public static readonly List<Entry> Entries = new();

    public static string Status = "đã tắt";

    // ------------------------------------------------------------------ state

    // Portions ready per recipe, keyed the way the game itself identifies a recipe over
    // the network - ServePlateServerRpc carries the recipe name, so a name is enough to
    // tell two recipes apart.
    private static readonly Dictionary<string, int> Ready =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> Capacity =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Logged = new();

    /// <summary>Portions at or below which a dish shows yellow. A counter holds five.</summary>
    private const int LowServings = 3;

    /// <summary>
    /// How long a reading may stand without any counter event before it is taken again.
    ///
    /// The overlay is driven by the counters themselves - see <see cref="Install"/> - so
    /// this is not the update mechanism, it is insurance. If a patch failed to apply, or
    /// the game changes a serving by a route we did not patch, an overlay with no timer at
    /// all would sit there showing a stale number for the rest of the session with nothing
    /// to say so. One cheap re-read every few seconds bounds how wrong it can get.
    /// </summary>
    private const float ResyncSeconds = 3f;

    private static float _nextResync;
    private static bool _dirty = true;
    private static int _countersSeen;
    private static int _countersServing;

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }

    // ----------------------------------------------------------------- events

    /// <summary>
    /// Hooks the counter so the numbers move when a serving does, rather than on a timer.
    ///
    /// Both directions matter: a customer taking a plate is DecreaseServing, and a cook
    /// putting one out is ServePlate. Patching only the first would leave a freshly plated
    /// dish reading zero until something was taken from it.
    /// </summary>
    public static void Install(BepInEx.Logging.ManualLogSource log)
    {
        var harmony = new HarmonyLib.Harmony(Plugin.Guid + ".dishstatus");

        // OnDecreasedServingClientRpc is in the list because on a co-op client the local
        // DecreaseServing is not what moves the count - the server's message is.
        string[] names =
        {
            nameof(ServiceCounter.DecreaseServing),
            nameof(ServiceCounter.OnDecreasedServingClientRpc),
            nameof(ServiceCounter.ServePlate),
            nameof(ServiceCounter.ServeToCounter),
            nameof(ServiceCounter.ResetServing),
            nameof(ServiceCounter.ResetCounter),
        };

        var mark = new HarmonyLib.HarmonyMethod(
            HarmonyLib.AccessTools.Method(typeof(DishStatus), nameof(MarkDirty)));

        int patched = 0;

        foreach (var name in names)
        {
            try
            {
                var target = HarmonyLib.AccessTools.Method(typeof(ServiceCounter), name);
                if (target == null)
                {
                    log.LogWarning($"Dish HUD: could not resolve ServiceCounter.{name}.");
                    continue;
                }

                harmony.Patch(target, postfix: mark);
                patched++;
            }
            catch (Exception e)
            {
                log.LogWarning($"Dish HUD: could not patch ServiceCounter.{name}: {e.Message}");
            }
        }

        log.LogInfo($"Dish HUD: patched {patched} of {names.Length} serving events; " +
                    $"a re-read every {ResyncSeconds:0.#}s covers the rest.");
    }

    /// <summary>Postfix on every serving change: the next frame takes a fresh reading.</summary>
    private static void MarkDirty() => _dirty = true;

    /// <summary>Called from the menu editor, so an added or removed dish shows at once.</summary>
    public static void Invalidate() => _dirty = true;

    // ------------------------------------------------------------------- tick

    public static void Tick()
    {
        if (!Plugin.DishHudEnabled.Value)
        {
            if (Entries.Count > 0) Entries.Clear();
            Status = "đã tắt";
            _dirty = true;          // so re-enabling reads immediately
            return;
        }

        bool due = _dirty || Time.unscaledTime >= _nextResync;
        if (!due) return;

        _dirty = false;
        _nextResync = Time.unscaledTime + ResyncSeconds;

        try { Rebuild(); }
        catch (Exception e)
        {
            Status = "không đọc được quầy phục vụ - xem log";
            LogOnce($"Dish HUD: rebuild failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Rebuild()
    {
        var menu = MenuTweaks.TodaysMenu();
        if (menu.Count == 0)
        {
            Entries.Clear();
            Status = "thực đơn hôm nay đang trống";
            return;
        }

        ReadCounters();

        // Built to the side and swapped in, so a half-filled list is never what the
        // overlay is drawing.
        var fresh = new List<Entry>(menu.Count);

        SyncHidden();

        foreach (var recipe in menu)
        {
            var e = new Entry { Name = MenuTweaks.DisplayName(recipe) };
            e.Hidden = e.Name != null && HiddenNames.Contains(e.Name);

            // A menu entry with no ingredients is a variant family heading, not a dish -
            // it can never be plated, so counting its portions would just report a
            // permanent zero and hide the real problem.
            if (!HasIngredients(recipe))
            {
                e.Servings = -1;
                e.Level = Level.Unknown;
                fresh.Add(e);
                continue;
            }

            string key = RecipeKey(recipe);

            int ready = 0;
            int capacity = 0;
            if (key != null)
            {
                Ready.TryGetValue(key, out ready);
                Capacity.TryGetValue(key, out capacity);
            }

            e.Servings = ready;
            e.Capacity = capacity;
            e.Level = ready <= 0 ? Level.Out : ready <= LowServings ? Level.Low : Level.Full;

            fresh.Add(e);
        }

        Entries.Clear();
        Entries.AddRange(fresh);

        int shown = 0;
        foreach (var e in Entries) if (!e.Hidden) shown++;

        Status = _countersSeen == 0
            ? "chưa thấy quầy phục vụ nào"
            : $"hiện {shown}/{Entries.Count} món, {_countersServing}/{_countersSeen} quầy đang có đồ";
    }

    /// <summary>Totals the portions waiting on every service counter, by recipe.</summary>
    private static void ReadCounters()
    {
        Ready.Clear();
        Capacity.Clear();
        _countersSeen = 0;
        _countersServing = 0;

        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<ServiceCounter> counters;
        try
        {
            counters = UnityEngine.Object.FindObjectsByType<ServiceCounter>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        }
        catch (Exception e)
        {
            LogOnce($"Dish HUD: service counter lookup failed: {e.Message}");
            return;
        }

        if (counters == null) return;

        for (int i = 0; i < counters.Length; i++)
        {
            var counter = counters[i];
            if (counter == null) continue;
            _countersSeen++;

            Recipe recipe = null;
            try { recipe = counter.activeRecipe; } catch { }
            if (recipe == null) continue;

            string key = RecipeKey(recipe);
            if (key == null) continue;

            int servings = 0;
            try { servings = counter.ActiveServings; } catch { }

            int max = 0;
            try { max = counter.MaxServingCount; } catch { }

            // A counter still holding the recipe but down to nothing counts towards
            // capacity, not towards portions - it is a slot that has run dry, which is
            // exactly the state worth showing red.
            Capacity.TryGetValue(key, out int hadMax);
            Capacity[key] = hadMax + Mathf.Max(0, max);

            if (servings <= 0) continue;
            _countersServing++;

            Ready.TryGetValue(key, out int had);
            Ready[key] = had + servings;
        }
    }

    // ----------------------------------------------------- recipe ingredients

    // Recipes are ScriptableObjects - their ingredient lists never change during a run -
    // so the walk over three parallel arrays is done once per dish and kept. Keyed by the
    // native object, which is the asset itself and cannot be confused with anything else.
    private static readonly Dictionary<IntPtr, Dictionary<string, float>> NeedCache = new();

    private static readonly Dictionary<string, float> Empty = new();

    /// <summary>
    /// Whether this recipe lists any ingredients at all.
    ///
    /// The heading of a variant family lists none - it is a grouping the tablet cycles the
    /// real dishes inside, not something the kitchen can cook - so this is what tells a
    /// dish apart from a heading. MenuTweaks uses it to keep headings off the menu.
    /// </summary>
    public static bool HasIngredients(Recipe recipe) => Requirements(recipe).Count > 0;

    private static Dictionary<string, float> Requirements(Recipe recipe)
    {
        if (recipe == null) return Empty;

        IntPtr id;
        try { id = recipe.Pointer; } catch { return Empty; }
        if (id == IntPtr.Zero) return Empty;

        if (NeedCache.TryGetValue(id, out var cached)) return cached;

        var needs = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // Main and side are both RecipeIngredient - an ingredient with an amount. "Other"
        // is a plain ingredient array paired with its own amounts array, so it is walked
        // by index against that.
        try { AddRecipeIngredients(recipe.MainIngredients, needs); } catch { }
        try { AddRecipeIngredients(recipe.sideIngredients, needs); } catch { }

        try
        {
            var others = recipe.otherIngredients;
            var amounts = recipe.otherIngredientAmounts;

            if (others != null)
                for (int i = 0; i < others.Length; i++)
                {
                    var ing = others[i];
                    if (ing == null) continue;

                    float amount = amounts != null && i < amounts.Length ? amounts[i] : 1f;
                    Need(needs, ing, amount);
                }
        }
        catch { }

        if (needs.Count == 0)
            LogOnce($"Menu: '{RecipeKey(recipe)}' lists no ingredients, so it is a variant " +
                    "family heading rather than a dish - it is kept off the menu, and shows " +
                    "as '?' if it is already on one.");

        NeedCache[id] = needs;
        return needs;
    }

    private static void AddRecipeIngredients(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<RecipeIngredient> list,
        Dictionary<string, float> needs)
    {
        if (list == null) return;

        for (int i = 0; i < list.Length; i++)
        {
            var entry = list[i];
            if (entry == null) continue;

            try { Need(needs, entry.Ingredient, entry.Amount); }
            catch { }
        }
    }

    private static void Need(Dictionary<string, float> needs, Ingredient ing, float amount)
    {
        if (ing == null || amount <= 0f) return;

        string key;
        try { key = ing.ingredientKey; } catch { return; }
        if (string.IsNullOrEmpty(key)) return;

        needs.TryGetValue(key, out float had);
        needs[key] = had + amount;
    }

    private static string RecipeKey(Recipe r)
    {
        try
        {
            if (r == null) return null;
            return string.IsNullOrEmpty(r.recipeName) ? r.name : r.recipeName;
        }
        catch { return null; }
    }

}
