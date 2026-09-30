using HarmonyLib;

namespace BonesTweaks
{
    // Basic ingredient stations are already unlimited (IngredientStation.isUnlimited). Only the
    // SuperFridge's premium ingredients (beef, cheese, chicken, chocolate, egg, fish, milk, pork,
    // sugar) are counted, in GameData's inventoryItems[id, 1]; [id, 0] marks them discovered.
    // Every way of taking one checks GameData.HasInventoryItem and then calls
    // AdjustItemCountInInventory(id, -1) - so those two methods are the whole lever.
    //
    // Nothing in the save is rewritten: counts simply stop going down, and turning the tweak off
    // returns to the real numbers.

    /// <summary>Taking an ingredient no longer lowers its count. Gains (pick-ups, returning an
    /// ingredient to the fridge) still apply.</summary>
    [HarmonyPatch(typeof(GameData), nameof(GameData.AdjustItemCountInInventory))]
    internal static class InfiniteTakePatch
    {
        private static bool Prefix(int amount) => !(Plugin.InfiniteIngredients.Value && amount < 0);
    }

    /// <summary>A discovered ingredient counts as in stock even at 0, so the fridge hands it out
    /// and recipes that need it stay available.</summary>
    [HarmonyPatch(typeof(GameData), nameof(GameData.HasInventoryItem))]
    internal static class InfiniteStockPatch
    {
        private static void Postfix(GameData __instance, int itemID, ref bool __result)
        {
            if (__result || !Plugin.InfiniteIngredients.Value) return;
            var inv = __instance.GetInventory();
            if (inv != null && itemID >= 0 && itemID < inv.GetLength(0) && inv[itemID, 0] == 1)
                __result = true;
        }
    }
}
