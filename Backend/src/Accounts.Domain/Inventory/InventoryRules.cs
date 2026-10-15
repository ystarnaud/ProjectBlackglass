using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Inventory;

/// <summary>
/// Bag arithmetic shared by the catalogue validator and the starter planner. The capacity itself is never a constant here:
/// it is the catalogue's inventory.bagCapacity, exported from Unity's InventoryRules asset.
/// </summary>
public static class InventoryRules
{
    /// <summary>The smallest bag capacity a catalogue may declare.</summary>
    public const int MinBagCapacity = 1;

    /// <summary>The largest bag capacity a catalogue may declare (a sanity bound, not the game's value).</summary>
    public const int MaxBagCapacity = 64;

    /// <summary>
    /// The bag entries that adding these contents would produce, as Unity's ItemInventory.Add does when it fills open stacks
    /// first: one entry per unit of a non-stackable item, and per stackable definition ceil(total quantity / maximum
    /// stack), however the quantity was split across grants. Quantities below 1 add nothing. Saturates at int.MaxValue.
    /// </summary>
    public static int BagEntries(IEnumerable<(CatalogueItem Item, int Quantity)> contents)
    {
        long entries = 0;
        var stackable = new Dictionary<string, (CatalogueItem Item, long Total)>(StringComparer.Ordinal);
        foreach (var (item, quantity) in contents)
        {
            if (quantity < 1) continue;
            if (!item.Stackable)
            {
                entries += quantity;
                continue;
            }
            stackable[item.Id] = stackable.TryGetValue(item.Id, out var seen) ? (seen.Item, seen.Total + quantity) : (item, quantity);
        }
        foreach (var (item, total) in stackable.Values)
        {
            var maxStack = Math.Max(1, item.MaxStack);
            entries += (total + maxStack - 1) / maxStack;
        }
        return (int)Math.Min(entries, int.MaxValue);
    }
}
