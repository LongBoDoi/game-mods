using HarmonyLib;
using UnityEngine;

namespace BonesTweaks
{
    // Basic ingredient stations are already unlimited (IngredientStation.isUnlimited). Only the
    // SuperFridge's premium ingredients (beef, cheese, chicken, chocolate, egg, fish, milk, pork,
    // sugar) are counted, in GameData's inventoryItems[id, 1]; [id, 0] marks them discovered.
    // Every way of taking one checks GameData.HasInventoryItem and then calls
    // AdjustItemCountInInventory(id, -1).
    //
    // While the tweak is on, every discovered ingredient is kept at 99 (at least - a bigger stock
    // at the top rating, where the game allows 999, is never lowered) and taking one does not
    // lower it. The 99 is a real count in the save, so it stays after switching the tweak off.
    internal static class InfiniteIngredients
    {
        internal const int Stock = 99;

        /// <summary>Raises every discovered ingredient to <see cref="Stock"/>; returns whether anything changed.</summary>
        internal static bool TopUp()
        {
            var gd = GameData.Instance;
            var inv = gd != null ? gd.GetInventory() : null;
            if (inv == null) return false;

            bool changed = false;
            for (int i = 0; i < inv.GetLength(0); i++)
            {
                if (inv[i, 0] == 1 && inv[i, 1] < Stock)
                {
                    inv[i, 1] = Stock;
                    changed = true;
                }
            }
            if (changed)
            {
                try { Object.FindObjectOfType<InventoryDisplay>()?.UpdateDisplayItem(false); } catch { }
            }
            return changed;
        }
    }

    /// <summary>Taking an ingredient no longer lowers its count. Gains still apply.</summary>
    [HarmonyPatch(typeof(GameData), nameof(GameData.AdjustItemCountInInventory))]
    internal static class InfiniteTakePatch
    {
        private static bool Prefix(int amount) => !(Plugin.InfiniteIngredients.Value && amount < 0);

        // A newly discovered ingredient arrives with whatever was picked up; bring it to 99 too.
        private static void Postfix(int amount)
        {
            if (Plugin.InfiniteIngredients.Value && amount > 0) InfiniteIngredients.TopUp();
        }
    }

    /// <summary>A discovered ingredient always counts as in stock, so the fridge hands it out and
    /// recipes that need it stay available.</summary>
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
