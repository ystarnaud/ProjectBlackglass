using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Ids;

namespace Blackglass.Accounts.Domain.Inventory;

public sealed record PlannedOperative(string DefinitionId, string OriginRef);

/// <summary>One non-stackable item. A null operative is the account stash; the slot is "weapon", "armor" or "utility" when equipped.</summary>
public sealed record PlannedInstance(string DefinitionId, string? OperativeDefinitionId, string? EquippedSlot, string OriginRef);

/// <summary>All the units of one stackable item at one destination (a null operative is the stash).</summary>
public sealed record PlannedStack(string? OperativeDefinitionId, string DefinitionId, int Quantity);

public sealed record StarterPlan(IReadOnlyList<PlannedOperative> Operatives, IReadOnlyList<PlannedInstance> Instances, IReadOnlyList<PlannedStack> Stacks);

/// <summary>
/// What provisioning creates for an account from the catalogue's starter section, mirroring Unity's
/// InventorySession.SeedStarter and ItemInventory.Add: every grant is added to the bag in authored order, then equip takes
/// the first entry of that definition in the bag (the lowest k, D5). Each unit of a non-stackable item is an instance;
/// stackable units sum into one stack per destination and definition. The plan assumes a validated catalogue and throws
/// InvalidOperationException on anything the validator refuses, so it never plans something the game would not have seeded.
/// </summary>
public static class StarterPlanner
{
    public static StarterPlan For(long accountId, CatalogueDocument catalogue)
    {
        var items = catalogue.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var operatives = new List<PlannedOperative>();
        var instances = new List<PlannedInstance>();
        var stacks = new List<PlannedStack>();
        var known = catalogue.Operatives.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in catalogue.Starter.Operatives)
        {
            if (!known.Contains(entry.OperativeId)) throw Refuse($"starter operative '{entry.OperativeId}' is not in the catalogue");
            operatives.Add(new PlannedOperative(entry.OperativeId, StarterRef.Operative(accountId, entry.OperativeId)));
            var grants = entry.Grants.Select(g => (g.ItemId, g.Quantity, g.Equip)).ToList();
            Destination(accountId, entry.OperativeId, grants, items, catalogue.Inventory.BagCapacity, instances, stacks);
        }
        Destination(accountId, null, catalogue.Starter.Stash.Select(g => (g.ItemId, g.Quantity, false)).ToList(), items, null, instances, stacks);
        return new StarterPlan(operatives, instances, stacks);
    }

    static void Destination(long accountId, string? operativeId, List<(string ItemId, int Quantity, bool Equip)> grants,
        Dictionary<string, CatalogueItem> items, int? bagCapacity, List<PlannedInstance> instances, List<PlannedStack> stacks)
    {
        var where = operativeId is null ? "the stash" : $"operative '{operativeId}'";
        var own = new List<PlannedInstance>();
        var firstOf = new Dictionary<string, int>(StringComparer.Ordinal); // definition -> index in own of its k = 0 instance
        var copies = new Dictionary<string, int>(StringComparer.Ordinal);
        var stackTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        var stackOrder = new List<string>();
        var taken = new HashSet<ItemSlot>();
        var contents = new List<(CatalogueItem Item, int Quantity)>();

        foreach (var (itemId, quantity, equip) in grants)
        {
            if (!items.TryGetValue(itemId, out var item)) throw Refuse($"{where}: unknown item '{itemId}'");
            if (quantity < 1) throw Refuse($"{where}: the quantity of '{itemId}' is below 1");
            contents.Add((item, quantity));

            if (item.Stackable)
            {
                if (!stackTotals.TryGetValue(itemId, out var total)) stackOrder.Add(itemId);
                if ((long)total + quantity > int.MaxValue) throw Refuse($"{where}: the quantity of '{itemId}' is too large");
                stackTotals[itemId] = total + quantity;
            }
            else
            {
                for (var n = 0; n < quantity; n++)
                {
                    var k = copies.GetValueOrDefault(itemId);
                    copies[itemId] = k + 1;
                    var reference = operativeId is null
                        ? StarterRef.StashItem(accountId, itemId, k)
                        : StarterRef.OperativeItem(accountId, operativeId, itemId, k);
                    if (k == 0) firstOf[itemId] = own.Count;
                    own.Add(new PlannedInstance(itemId, operativeId, null, reference));
                }
            }

            if (!equip) continue;
            if (item.Slot == ItemSlot.None || item.Stackable) throw Refuse($"{where}: '{itemId}' cannot be equipped");
            if (!taken.Add(item.Slot)) throw Refuse($"{where}: a second item to equip in the {item.Slot} slot");
            var index = firstOf[itemId];
            own[index] = own[index] with { EquippedSlot = SlotName(item.Slot) };
        }

        if (bagCapacity is { } capacity && InventoryRules.BagEntries(contents) > capacity)
            throw Refuse($"{where}: the grants need more than the bag capacity {capacity}");

        instances.AddRange(own);
        foreach (var id in stackOrder) stacks.Add(new PlannedStack(operativeId, id, stackTotals[id]));
    }

    static string SlotName(ItemSlot slot) => slot switch
    {
        ItemSlot.Weapon => "weapon",
        ItemSlot.Armor => "armor",
        ItemSlot.Utility => "utility",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Not an equipment slot."),
    };

    static InvalidOperationException Refuse(string message) => new("The starter loadout cannot be planned: " + message);
}
