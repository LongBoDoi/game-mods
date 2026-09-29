using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace PlayerTweaks;

/// <summary>How the dish picker orders what it offers.</summary>
public enum DishSort
{
    Name,
    PriceHigh,
    PriceLow,
}

/// <summary>
/// Reads and edits today's menu without going through the tablet UI.
///
/// Everything here goes through the game's own high-level menu calls - AddToMenu,
/// RemoveFromMenu - each of which has a ServerRpc/ClientRpc pair behind it, so an edit
/// replicates in co-op exactly the way a click on the tablet does.
///
/// The one thing that is emphatically *not* done here is offering every recipe the game
/// knows about. Menu.GetRecipes() returns the whole catalogue, including entries that are
/// not dishes you can cook: inactive ones, invisible ones, ones excluded by game mode or
/// DLC, and - the one that caused real trouble - the heading of a variant family.
///
/// A family like "Bánh quy" is a heading whose real dishes are "Bánh quy bơ" and its
/// siblings; the tablet gives the family one slot and cycles the variants inside it
/// (RecipeSlot.connectedMainRecipe / NextConnectedRecipe), and RecipeList's own
/// IsRecipeOrConnectedOnMenu treats the whole family as a single menu entry. Putting the
/// heading itself on the menu produced something the kitchen could not make and the
/// tablet had no row to remove.
///
/// IsTabletListed below is the filter that keeps that out, and the test it uses is simply
/// whether the recipe lists any ingredients - a heading lists none. That is a fact about
/// the recipe rather than a guess about which flag means what, which matters, because the
/// first attempt at this filter guessed from IsPartOfConnectedRecipes and got it exactly
/// backwards: it dropped every cookable variant and kept the heading.
/// </summary>
public static class MenuTweaks
{
    public static string Status = "chưa hoạt động";

    /// <summary>
    /// The categories offered as buttons, matching the tablet's own tabs. None and
    /// BakeryMode are omitted: one is the absence of a category, the other is a game mode.
    /// </summary>
    public static readonly FoodTag[] Categories =
    {
        FoodTag.Soup, FoodTag.Kebab, FoodTag.FastFood, FoodTag.Chicken, FoodTag.Vegetarian,
        FoodTag.Bakery, FoodTag.WorldCuisine, FoodTag.Pizza, FoodTag.Luxury, FoodTag.Fish,
    };

    public static string Label(FoodTag tag) => tag switch
    {
        FoodTag.FastFood => "Đồ ăn nhanh",
        FoodTag.WorldCuisine => "Quốc tế",
        FoodTag.Vegetarian => "Chay",
        FoodTag.Soup => "Súp",
        FoodTag.Kebab => "Kebab",
        FoodTag.Chicken => "Gà",
        FoodTag.Bakery => "Bánh",
        FoodTag.Pizza => "Pizza",
        FoodTag.Luxury => "Cao cấp",
        FoodTag.Fish => "Cá",
        _ => tag.ToString(),
    };

    // ------------------------------------------------------------------ state

    private static readonly HashSet<string> Logged = new();

    private static void LogOnce(string message)
    {
        if (Logged.Add(message)) Plugin.Logger.LogInfo(message);
    }

    private static Menu Current()
    {
        try { return Menu.Instance; } catch { return null; }
    }

    /// <summary>True when the menu system is reachable; sets Status when it is not.</summary>
    private static bool TryGetMenu(out Menu menu)
    {
        menu = Current();
        if (menu != null) return true;
        Status = "chưa mở được thực đơn - hãy mở máy tính bảng trước";
        return false;
    }

    // -------------------------------------------------------------- inspecting

    /// <summary>How many dishes the game allows on the menu at once; 0 when unknown.</summary>
    public static int MaxOnMenu()
    {
        try
        {
            var menu = Current();
            return menu == null ? 0 : menu.maxItemOnMenu;
        }
        catch { return 0; }
    }

    /// <summary>Today's menu, in the order the game holds it.</summary>
    public static List<Recipe> TodaysMenu()
    {
        var list = new List<Recipe>();

        var menu = Current();
        if (menu == null) return list;

        int count = 0;
        try { count = menu.GetMenuRecipeCount(); } catch { return list; }

        for (int i = 0; i < count; i++)
        {
            Recipe r = null;
            try { r = menu.GetMenuRecipeByIndex(i); } catch { }
            if (r != null) list.Add(r);
        }
        return list;
    }

    /// <summary>
    /// Dishes that can be put on the menu right now, in the given category, sorted by
    /// the name the player sees.
    /// </summary>
    public static List<Recipe> Addable(FoodTag? category) => Addable(category, DishSort.Name);

    /// <summary>
    /// The same list in the order asked for. Price sorts fall back to the name when two
    /// dishes cost the same, so the order is stable rather than whatever the catalogue
    /// happened to hold - a picker that reshuffles equal-priced rows between refreshes is
    /// worse than one that never sorted them at all.
    /// </summary>
    public static List<Recipe> Addable(FoodTag? category, DishSort sort)
    {
        var list = BuildCandidates(Current(), category, out _, out _, out _);

        list.Sort((a, b) => sort switch
        {
            DishSort.PriceHigh => Tie(Price(b).CompareTo(Price(a)), a, b),
            DishSort.PriceLow => Tie(Price(a).CompareTo(Price(b)), a, b),
            _ => ByName(a, b),
        });

        return list;
    }

    private static int Tie(int primary, Recipe a, Recipe b) => primary != 0 ? primary : ByName(a, b);

    private static int ByName(Recipe a, Recipe b) =>
        string.Compare(DisplayName(a), DisplayName(b), StringComparison.CurrentCultureIgnoreCase);

    // -------------------------------------------------------------------- price

    /// <summary>
    /// What the dish sells for.
    ///
    /// Recipe exposes both a computed Price and the price field behind it. The computed
    /// one is what the tablet shows, so it is what the picker sorts by; the field is only
    /// a fallback for a recipe whose profile the property cannot reach.
    /// </summary>
    public static float Price(Recipe r)
    {
        if (r == null) return 0f;

        try { return r.Price; }
        catch (Exception e) { LogOnce($"Menu: Recipe.Price unavailable ({e.Message}); using the raw price field."); }

        try { return r.price; } catch { return 0f; }
    }

    /// <summary>The price as the picker prints it. No currency mark - the game draws its
    /// own as a sprite, and inventing one here would only be a guess at what it means.</summary>
    public static string PriceText(Recipe r) => Price(r).ToString("0.##", CultureInfo.InvariantCulture);

    // ----------------------------------------------------------------- editing

    /// <summary>Any category.</summary>
    public static void AddRandomDish() => AddRandomDish(null);

    public static void AddRandomDish(FoodTag? category)
    {
        try
        {
            if (!TryGetMenu(out var menu)) return;

            if (!menu.CanAddToMenu())
            {
                Status = "thực đơn đã đầy";
                return;
            }

            string where = category.HasValue ? $" trong {Label(category.Value)}" : "";

            var candidates = BuildCandidates(menu, category, out int unlocked, out int already,
                                             out int variantsBlocked);
            if (candidates.Count == 0)
            {
                Status = already > 0 || variantsBlocked > 0
                    ? $"không còn món nào{where} ({already} đã có trên thực đơn)"
                    : $"chưa mở khóa món nào{where}";
                return;
            }

            // A flat roll is correct now that only tablet-listed dishes are candidates.
            // The old two-stage roll existed because variants were candidates too, which
            // weighted a dish by how many variants it had - and it was that same variant
            // handling that put unremovable entries on the menu.
            var pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];

            // AddDish sets Status itself, including for a variant mismatch, so a failure
            // here must not be papered over with an "added" line. The old unconditional
            // log said the roll had landed even when the menu had ended up with a
            // different dish entirely.
            bool ok = AddDish(pick);

            Plugin.Logger.LogInfo(
                $"Menu: {(ok ? "added" : "FAILED to add")} '{SafeName(pick)}'{where} - rolled across " +
                $"{candidates.Count} dishes ({unlocked} unlocked{where}, {already} already on the menu, " +
                $"{variantsBlocked} skipped as a family sibling or as not cookable).");
        }
        catch (Exception e)
        {
            Status = "thêm món thất bại - xem log";
            Plugin.Logger.LogWarning($"Menu: random add failed: {e.Message}");
        }
    }

    /// <summary>
    /// Puts one named dish on the menu, then checks the game put *that* dish there.
    ///
    /// The check is the point: AddToMenu takes a Recipe but the RPC behind it carries only
    /// a name (AddToMenuServerRpc(string recipeName, ...)), which the far side resolves
    /// again with GetRecipeByName. Anything that makes that lookup land on a different
    /// member of the same variant family - "Brownie" instead of "BrownieChocolate" - is
    /// invisible from this side unless the entry is read back and compared.
    ///
    /// GetMenuIndexByRecipe is deliberately *not* what does the comparing. RecipeList has
    /// an IsRecipeOrConnectedOnMenu that treats a whole variant family as one entry, and a
    /// family-aware index lookup answers "is something from this family on the menu",
    /// which is true for a sibling and so cannot catch a swap. Identity can: recipes are
    /// ScriptableObject assets, so one pointer is one asset and a heading can never be
    /// mistaken for a variant.
    /// </summary>
    public static bool AddDish(Recipe recipe)
    {
        if (recipe == null) return false;

        try
        {
            if (!TryGetMenu(out var menu)) return false;

            if (!menu.CanAddToMenu())
            {
                Status = "thực đơn đã đầy";
                return false;
            }

            var before = TodaysMenu();
            menu.AddToMenu(recipe, true);
            var landed = NewlyAdded(before, TodaysMenu());

            if (landed == null)
            {
                Status = $"không thêm được {DisplayName(recipe)} - trò chơi không nhận món này";
                Plugin.Logger.LogWarning(
                    $"Menu: AddToMenu('{SafeName(recipe)}') left it off the menu " +
                    $"(recipeName='{Key(recipe)}', listed={IsTabletListed(recipe)}).");
                return false;
            }

            if (!Same(landed, recipe))
            {
                // Put it back. Detecting the swap and then leaving the wrong dish sitting
                // on the menu is barely better than not noticing - the kitchen still ends
                // up cooking something nobody asked for, and the player has to spot it and
                // remove it by hand.
                try { menu.RemoveFromMenu(landed); }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning(
                        $"Menu: could not roll back the wrong dish '{Key(landed)}': {e.Message}");
                }

                // Remember it, so a random roll stops offering a dish that cannot be put
                // on the menu under its own name.
                string key = Key(recipe);
                if (key != null) Unroutable.Add(key);

                Status = $"không thêm được {DisplayName(recipe)} - trò chơi thay bằng {DisplayName(landed)}";
                Plugin.Logger.LogWarning(
                    $"Menu: VARIANT MISMATCH - asked for '{Key(recipe)}' but the menu took " +
                    $"'{Key(landed)}' instead; rolled back. AddToMenu replicates by name " +
                    "(AddToMenuServerRpc(string recipeName)), so GetRecipeByName is resolving " +
                    "this name to a different member of the same family.");
                return false;
            }

            Status = $"đã thêm {DisplayName(recipe)}";
            return true;
        }
        catch (Exception e)
        {
            Status = "thêm món thất bại - xem log";
            Plugin.Logger.LogWarning($"Menu: add failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// The one entry present in <paramref name="after"/> that was not in
    /// <paramref name="before"/>, or null when the menu did not grow.
    /// </summary>
    private static Recipe NewlyAdded(List<Recipe> before, List<Recipe> after)
    {
        foreach (var candidate in after)
        {
            if (candidate == null) continue;

            bool wasThere = false;
            foreach (var old in before)
                if (Same(old, candidate)) { wasThere = true; break; }

            if (!wasThere) return candidate;
        }
        return null;
    }

    /// <summary>
    /// The same recipe asset, compared by identity.
    ///
    /// Not by name: names are exactly what the variant families confuse, and two members
    /// of one family can share a localised display string.
    /// </summary>
    private static bool Same(Recipe a, Recipe b)
    {
        if (a == null || b == null) return false;
        try { return a.Pointer == b.Pointer; }
        catch { return false; }
    }

    public static bool RemoveDish(Recipe recipe)
    {
        if (recipe == null) return false;

        try
        {
            if (!TryGetMenu(out var menu)) return false;

            string name = DisplayName(recipe);
            menu.RemoveFromMenu(recipe);

            int index = -1;
            try { index = menu.GetMenuIndexByRecipe(recipe); } catch { }

            if (index >= 0)
            {
                Status = $"không xóa được {name}";
                Plugin.Logger.LogWarning($"Menu: RemoveFromMenu('{SafeName(recipe)}') left it on the menu at {index}.");
                return false;
            }

            Status = $"đã xóa {name}";
            return true;
        }
        catch (Exception e)
        {
            Status = "xóa món thất bại - xem log";
            Plugin.Logger.LogWarning($"Menu: remove failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Moves one dish up or down the menu.
    ///
    /// There is no reorder call in the game - the order is simply the order things were
    /// added - so the tail of the list is taken off and put back in the new order. That
    /// keeps the change on the same networked add/remove path as everything else here,
    /// rather than rewriting menuRecipes locally and desyncing co-op.
    /// </summary>
    public static bool Move(int index, int delta)
    {
        try
        {
            if (!TryGetMenu(out var menu)) return false;

            var before = TodaysMenu();
            int target = index + delta;

            if (index < 0 || index >= before.Count) return false;
            if (target < 0 || target >= before.Count)
            {
                Status = delta < 0 ? "món này đã ở đầu thực đơn" : "món này đã ở cuối thực đơn";
                return false;
            }

            var after = new List<Recipe>(before);
            var moving = after[index];
            after.RemoveAt(index);
            after.Insert(target, moving);

            // Only the part of the list that actually changes is rebuilt.
            int first = Math.Min(index, target);

            for (int i = before.Count - 1; i >= first; i--)
            {
                try { menu.RemoveFromMenu(before[i]); }
                catch (Exception e) { Plugin.Logger.LogWarning($"Menu: reorder could not remove '{SafeName(before[i])}': {e.Message}"); }
            }

            for (int i = first; i < after.Count; i++)
            {
                try { menu.AddToMenu(after[i], true); }
                catch (Exception e) { Plugin.Logger.LogWarning($"Menu: reorder could not re-add '{SafeName(after[i])}': {e.Message}"); }
            }

            // Losing a dish to a half-finished rebuild would be far worse than a wrong
            // order, so say so plainly rather than let it pass unnoticed.
            int now = TodaysMenu().Count;
            if (now != before.Count)
            {
                Status = $"sắp xếp lại bị lỗi - còn {now}/{before.Count} món";
                Plugin.Logger.LogWarning($"Menu: reorder rebuilt {now} of {before.Count} dishes.");
                return false;
            }

            Status = $"đã chuyển {DisplayName(moving)} {(delta < 0 ? "lên" : "xuống")}";
            return true;
        }
        catch (Exception e)
        {
            Status = "sắp xếp lại thất bại - xem log";
            Plugin.Logger.LogWarning($"Menu: reorder failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clears today's menu.
    ///
    /// Removes each dish with RemoveFromMenu - the same call the tablet's own remove uses,
    /// and one with a ServerRpc/ClientRpc pair, so it replicates in co-op. ResetMenu()
    /// exists too but its scope is not visible from the stub assemblies, and it may clear
    /// more than the day's selection.
    /// </summary>
    public static void ClearMenu()
    {
        try
        {
            if (!TryGetMenu(out var menu)) return;

            // Collect first, then remove. Removing while walking the indexes would shift
            // them underneath us and skip entries.
            var onMenu = TodaysMenu();

            if (onMenu.Count == 0)
            {
                Status = "thực đơn vốn đã trống";
                return;
            }

            foreach (var r in onMenu)
            {
                try { menu.RemoveFromMenu(r); }
                catch (Exception e) { Plugin.Logger.LogWarning($"Menu: could not remove '{SafeName(r)}': {e.Message}"); }
            }

            int left = 0;
            try { left = menu.GetMenuRecipeCount(); } catch { }

            Status = left == 0 ? $"đã xóa {onMenu.Count} món" : $"đã xóa {onMenu.Count - left} món, còn lại {left}";
            Plugin.Logger.LogInfo($"Menu: cleared {onMenu.Count} dishes, {left} remaining.");
        }
        catch (Exception e)
        {
            Status = "xóa thực đơn thất bại - xem log";
            Plugin.Logger.LogWarning($"Menu: clear failed: {e.Message}");
        }
    }

    // -------------------------------------------------------------- candidates

    /// <summary>
    /// Unlocked, tablet-listed dishes that are neither on today's menu nor a variant of
    /// something on it.
    ///
    /// Variants come from Recipe.ConnectedRecipes, the game's own grouping. Guessing from
    /// names would not work: "BrownieWalnut" carries the variant as a suffix while
    /// "StrawberryMacaron" carries it as a prefix.
    /// </summary>
    private static List<Recipe> BuildCandidates(Menu menu, FoodTag? category,
                                                out int unlocked, out int already,
                                                out int skipped)
    {
        var list = new List<Recipe>();
        unlocked = 0;
        already = 0;
        skipped = 0;

        if (menu == null) return list;

        Il2CppSystem.Collections.Generic.List<Recipe> recipes = null;
        try { recipes = menu.GetRecipes(); } catch { }
        if (recipes == null) return list;

        var blocked = BuildBlockedFamilies(BuildFamilies(recipes));

        // Counted separately so the fallback below can tell "the tablet filter removed
        // everything" apart from "there was nothing to begin with".
        int passedWithoutTabletFilter = 0;
        var relaxed = new List<Recipe>();

        for (int i = 0; i < recipes.Count; i++)
        {
            var r = recipes[i];
            if (r == null) continue;

            // Category filter first, so the "unlocked" tally reflects the category asked
            // for rather than the whole game.
            if (category.HasValue)
            {
                FoodTag tag;
                try { tag = r.PrimaryTag; } catch { continue; }
                if (tag != category.Value) continue;
            }

            // Let the game decide what "unlocked" means rather than guessing at it.
            bool isUnlocked;
            try { isUnlocked = menu.GetRecipeUnlocked(r); } catch { continue; }
            if (!isUnlocked) continue;
            unlocked++;

            bool onMenu;
            try { onMenu = menu.GetRecipeOnMenu(r); } catch { onMenu = false; }
            if (onMenu) { already++; continue; }

            if (IsBlockedVariant(r, blocked)) { skipped++; continue; }

            // Dishes already caught not surviving their own name round trip. Rolling one
            // of these puts a different dish on the menu, so there is no point offering it.
            if (IsUnroutable(r)) { skipped++; continue; }

            passedWithoutTabletFilter++;
            relaxed.Add(r);

            if (!IsTabletListed(r)) { skipped++; continue; }

            list.Add(r);
        }

        // If the tablet filter turned out to be wrong for this build, an empty list would
        // silently break the feature. Fall back to the unfiltered set and say so, so the
        // filter can be corrected from a real observation rather than a guess.
        if (list.Count == 0 && passedWithoutTabletFilter > 0)
        {
            LogOnce($"Menu: the tablet-listed filter excluded all {passedWithoutTabletFilter} otherwise-valid " +
                    "dishes; falling back to the unfiltered set. Dishes added this way may not have a row " +
                    "on the tablet - use the mod's own list to remove them.");
            skipped -= passedWithoutTabletFilter;
            return relaxed;
        }

        return list;
    }

    /// <summary>
    /// Whether this is a dish the kitchen can actually cook, as opposed to a catalogue
    /// entry the tablet only uses as a heading.
    /// </summary>
    private static bool IsTabletListed(Recipe r)
    {
        if (r == null) return false;

        try { if (r.isInvisible) return false; } catch { }
        try { if (!r.isActive) return false; } catch { }

        // IsAvailable() covers game mode, DLC and whatever else the game wants to say no
        // to. Treat a throw as "available" - refusing everything on an unexpected error
        // would be worse than letting one through.
        try { if (!r.IsAvailable()) return false; } catch { }

        // A family heading is not a dish. Two tests, because neither is sufficient alone.
        if (IsFamilyHeading(r)) return false;

        // Every dish you can cook lists ingredients. This was the *only* test here, and it
        // is not enough: it assumes a heading lists none, which holds for Brownie and
        // Macaron but not for Pie - that family has a shared base (PieFoundation,
        // PieMixture, ExtractPieDough), so its heading lists real ingredients, sailed
        // through this check and reached the dish picker. Clicking it added "Pie", which
        // is what looked like RaspberryPie being replaced.
        if (!DishStatus.HasIngredients(r)) return false;

        return true;
    }

    /// <summary>
    /// Whether this recipe is the heading of a variant family rather than one of its
    /// dishes.
    ///
    /// Both flags are needed. HasConnectedRecipes alone would be wrong if the game fills
    /// ConnectedRecipes in at both ends, which would make every variant look like a
    /// heading and empty the picker. Requiring IsPartOfConnectedRecipes to be *false*
    /// keeps the variants: a heading owns a family without belonging to one, while a
    /// variant belongs to a family whether or not it also links back.
    ///
    /// This is the flag pair the very first version of the filter got backwards - it used
    /// IsPartOfConnectedRecipes to exclude, which dropped every cookable variant and kept
    /// the heading. This is the opposite reading, and the narrower one.
    /// </summary>
    private static bool IsFamilyHeading(Recipe r)
    {
        if (r == null) return false;

        try
        {
            if (!r.HasConnectedRecipes) return false;
            if (r.IsPartOfConnectedRecipes) return false;
        }
        catch { return false; }

        return true;
    }

    /// <summary>
    /// Groups the catalogue into variant families, mapping every member's key to the set
    /// of keys in its family.
    ///
    /// Built from the whole catalogue rather than from one recipe's own ConnectedRecipes,
    /// because that array is only reliably populated at one end. Reading it from a variant
    /// alone would miss its siblings, and two dishes from the same family would then both
    /// end up on a menu the tablet shows as a single slot.
    /// </summary>
    private static Dictionary<string, HashSet<string>> BuildFamilies(
        Il2CppSystem.Collections.Generic.List<Recipe> recipes)
    {
        var families = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (int i = 0; i < recipes.Count; i++)
            {
                var r = recipes[i];
                string key = Key(r);
                if (key == null) continue;

                var members = new List<string> { key };
                foreach (var c in Connected(r))
                {
                    string ck = Key(c);
                    if (ck != null) members.Add(ck);
                }

                if (members.Count < 2) continue;   // nothing connected, so no family

                // Absorb anything these members already belong to, so a family named from
                // the heading's side and from a variant's side becomes one set rather
                // than two overlapping ones.
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in members)
                {
                    set.Add(m);
                    if (families.TryGetValue(m, out var existing))
                        foreach (var e in existing) set.Add(e);
                }

                foreach (var m in set) families[m] = set;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Menu: could not group variant families: {e.Message}");
        }

        return families;
    }

    /// <summary>
    /// Every key in a family that already has a member on the menu, so the rest of that
    /// family stays out of the candidate list.
    /// </summary>
    private static HashSet<string> BuildBlockedFamilies(Dictionary<string, HashSet<string>> families)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var r in TodaysMenu())
            {
                string key = Key(r);
                if (key == null) continue;

                blocked.Add(key);
                if (families.TryGetValue(key, out var family))
                    foreach (var m in family) blocked.Add(m);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Menu: could not read variant families: {e.Message}");
        }

        return blocked;
    }

    private static bool IsBlockedVariant(Recipe r, HashSet<string> blocked)
    {
        if (blocked.Count == 0) return false;

        string key = Key(r);
        return key != null && blocked.Contains(key);
    }

    /// <summary>
    /// Dishes whose name does not resolve back to themselves.
    ///
    /// AddToMenu replicates by name - AddToMenuServerRpc(string recipeName) - and the far
    /// side turns that name back into a recipe with GetRecipeByName. When that lookup
    /// returns a sibling instead, asking for one dish puts another on the menu. AddDish
    /// catches that, rolls it back and records the name here.
    ///
    /// Filled only from an add that was actually seen going wrong, never from a guess, so
    /// it can never hide a dish that would have worked.
    /// </summary>
    private static readonly HashSet<string> Unroutable =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool IsUnroutable(Recipe r)
    {
        if (Unroutable.Count == 0) return false;

        string key = Key(r);
        return key != null && Unroutable.Contains(key);
    }

    private static IEnumerable<Recipe> Connected(Recipe r)
    {
        Recipe[] copy;
        try
        {
            var arr = r.ConnectedRecipes;
            if (arr == null || arr.Length == 0) yield break;

            copy = new Recipe[arr.Length];
            for (int i = 0; i < arr.Length; i++) copy[i] = arr[i];
        }
        catch { yield break; }

        foreach (var c in copy)
            if (c != null) yield return c;
    }

    /// <summary>
    /// The recipe a saved key names, or null if this game does not have it.
    ///
    /// Walks the catalogue and compares <see cref="Key"/>, rather than calling the game's own
    /// GetRecipeByName. That lookup is exactly the one that resolves a variant's name to a
    /// sibling - the mismatch AddDish exists to catch - so using it here would let a preset
    /// quietly reload as a different dish. Comparing the key we wrote to the key each recipe
    /// reports cannot land anywhere but on the recipe that wrote it.
    /// </summary>
    public static Recipe RecipeByKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;

        var menu = Current();
        if (menu == null) return null;

        Il2CppSystem.Collections.Generic.List<Recipe> recipes = null;
        try { recipes = menu.GetRecipes(); } catch { }
        if (recipes == null) return null;

        try
        {
            for (int i = 0; i < recipes.Count; i++)
            {
                var r = recipes[i];
                if (r == null) continue;

                if (string.Equals(Key(r), key, StringComparison.OrdinalIgnoreCase)) return r;
            }
        }
        catch { }

        return null;
    }

    internal static string Key(Recipe r)
    {
        try
        {
            if (r == null) return null;
            return string.IsNullOrEmpty(r.recipeName) ? r.name : r.recipeName;
        }
        catch { return null; }
    }

    private static string SafeName(Recipe r)
    {
        try { return r == null ? "?" : r.name; } catch { return "?"; }
    }

    // ------------------------------------------------------------------ naming

    private static readonly Dictionary<string, string> NameCache = new();

    /// <summary>
    /// The dish name in the player's own language.
    ///
    /// recipeName is a localisation key ("ChickenKebab"), not something to show as-is, so
    /// it goes through the game's LocalizationManager first. When there is no entry - or
    /// no manager yet, before the game has loaded its text - the key is spaced out into
    /// something readable instead of being shown run together.
    /// </summary>
    public static string DisplayName(Recipe r)
    {
        if (r == null) return "?";

        string key = Key(r);
        if (string.IsNullOrEmpty(key)) return SafeName(r);

        if (NameCache.TryGetValue(key, out string cached)) return cached;

        string shown = Localised(key) ?? Localised(SafeName(r)) ?? Spaced(key);

        // Only cache a real translation. Caching the fallback would freeze the English
        // key in place for the session if the manager simply was not ready yet.
        if (shown != Spaced(key)) NameCache[key] = shown;

        return shown;
    }

    private static string Localised(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;

        try
        {
            // Static, but still gated on the singleton: asking before the manager has
            // loaded its text would cache the miss and freeze the English key in place.
            if (BiotechGameworks.Localization.LocalizationManager.Instance == null) return null;

            string v = BiotechGameworks.Localization.LocalizationManager.GetLocalizedText(key, true);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    /// <summary>"BrownieWalnut" -> "Brownie Walnut", so an untranslated key still reads.</summary>
    private static string Spaced(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;

        var sb = new StringBuilder(key.Length + 4);
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(key[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
