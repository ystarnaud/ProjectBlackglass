using System.Diagnostics.CodeAnalysis;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Inventory;

/// <summary>Raised when an applied batch breaks a final-state or conservation rule. That is a bug in the rules, never a refusal.</summary>
public sealed class InventoryInvariantException(string message) : Exception(message);

/// <summary>
/// The inventory batch rules, ported from Unity's InventorySession, OperativeEquipment, InventoryState and ItemTransfer
/// (same checks in the same order) with the backend's deliberate differences (spec S5-D1, S5-D7, S5-D8): transfers are
/// exact (no clamping, no partial moves), a retired definition can still leave a bag, and a foreign operative or item is
/// reported exactly like an unknown one. Pure: no database, no clock, no randomness.
/// </summary>
public static class InventoryEditor
{
    /// <summary>
    /// Replays the batch in order against a working copy; each operation sees the earlier ones. The first refusal stops it
    /// and nothing applies (the caller's model is never modified). An applied batch then passes ValidateFinal. An empty
    /// batch is an ArgumentException: the API refuses it before it gets here (S5-D6).
    /// </summary>
    public static EditOutcome Apply(InventoryModel before, CatalogueDocument catalogue, IReadOnlyList<InventoryOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0) throw new ArgumentException("A batch needs at least one operation.", nameof(operations));

        var work = new WorkingCopy(before, catalogue);
        for (var index = 0; index < operations.Count; index++)
        {
            var refusal = work.Step(operations[index]);
            if (refusal is not null) return new EditOutcome(null, index, refusal);
        }

        var after = work.ToModel();
        ValidateFinal(before, after, catalogue);
        return new EditOutcome(after, null, null);
    }

    /// <summary>Bag entries: instances + sum of ceil(qty / maxStack) per known stack definition + 1 per unknown-definition stack row (S5-D7).</summary>
    public static int BagEntries(InventoryModel model, CatalogueDocument catalogue, long operativeId) =>
        Entries(model.Instances, model.Stacks, catalogue, operativeId);

    /// <summary>
    /// Throws InventoryInvariantException (a bug, never a refusal) unless `after` keeps the final-state and conservation
    /// rules relative to `before`: the same operatives, the same instance ids and definitions, the same total of every
    /// stack definition; every stack at least 1 and unique per location and definition; at most one item per operative
    /// and slot; every equipped item in an operative's bag, with a known definition of that slot; and no bag that grew
    /// past capacity (a bag already over capacity may stay over). An equipped item that did not change is not re-judged,
    /// so neither a definition the catalogue has since retired nor a known definition whose catalogue slot changed since it
    /// was equipped can make an unrelated edit fail.
    /// </summary>
    public static void ValidateFinal(InventoryModel before, InventoryModel after, CatalogueDocument catalogue)
    {
        if (!before.Operatives.Order().SequenceEqual(after.Operatives.Order())) Fail("the operative set changed");
        var operatives = after.Operatives.ToHashSet();

        var was = IndexById(before.Instances, "before");
        var now = IndexById(after.Instances, "after");
        if (was.Count != now.Count) Fail($"the instance count changed from {was.Count} to {now.Count}");

        foreach (var instance in after.Instances)
        {
            if (!was.TryGetValue(instance.ItemId, out var prior)) Fail($"instance {instance.ItemId} was created");
            else if (prior.DefinitionId != instance.DefinitionId) Fail($"instance {instance.ItemId} changed definition");

            if (instance.OperativeId is { } holder && !operatives.Contains(holder)) Fail($"instance {instance.ItemId} is held by an unknown operative");
            if (instance.Equipped is not { } slot) continue;

            if (instance.OperativeId is null) Fail($"instance {instance.ItemId} is equipped in the stash");
            if (slot == ItemSlot.None) Fail($"instance {instance.ItemId} is equipped in no slot");
            var unchanged = prior.OperativeId == instance.OperativeId && prior.Equipped == instance.Equipped;
            if (!unchanged && FindItem(catalogue, instance.DefinitionId)?.Slot != slot) Fail($"instance {instance.ItemId} does not fit the {slot} slot");
        }

        var slots = new HashSet<(long, ItemSlot)>();
        foreach (var instance in after.Instances)
            if (instance.Equipped is { } slot && !slots.Add((instance.OperativeId!.Value, slot))) Fail($"two items fill the {slot} slot of operative {instance.OperativeId}");

        var rows = new HashSet<(long?, string)>();
        foreach (var stack in after.Stacks)
        {
            if (stack.Quantity < 1) Fail($"a stack of {stack.DefinitionId} holds {stack.Quantity}");
            if (stack.OperativeId is { } holder && !operatives.Contains(holder)) Fail($"a stack of {stack.DefinitionId} is held by an unknown operative");
            if (!rows.Add((stack.OperativeId, stack.DefinitionId))) Fail($"two stack rows of {stack.DefinitionId} share a location");
        }
        var totalBefore = StackTotals(before.Stacks);
        var totalAfter = StackTotals(after.Stacks);
        if (totalBefore.Count != totalAfter.Count || totalBefore.Any(t => !totalAfter.TryGetValue(t.Key, out var other) || other != t.Value))
            Fail("a stack total changed");

        foreach (var operative in after.Operatives)
        {
            var entries = BagEntries(after, catalogue, operative);
            if (entries > BagEntries(before, catalogue, operative) && entries > catalogue.Inventory.BagCapacity)
                Fail($"the bag of operative {operative} grew to {entries} entries past capacity {catalogue.Inventory.BagCapacity}");
        }
    }

    [DoesNotReturn]
    static void Fail(string message) => throw new InventoryInvariantException(message);

    static Dictionary<long, HeldInstance> IndexById(IReadOnlyList<HeldInstance> instances, string side)
    {
        var index = new Dictionary<long, HeldInstance>(instances.Count);
        foreach (var instance in instances)
            if (!index.TryAdd(instance.ItemId, instance)) Fail($"instance {instance.ItemId} appears twice {side}");
        return index;
    }

    static Dictionary<string, long> StackTotals(IReadOnlyList<HeldStack> stacks) =>
        stacks.GroupBy(s => s.DefinitionId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(s => (long)s.Quantity), StringComparer.Ordinal);

    static CatalogueItem? FindItem(CatalogueDocument catalogue, string definitionId)
    {
        foreach (var item in catalogue.Items)
            if (item.Id == definitionId) return item;
        return null;
    }

    static int Entries(IReadOnlyList<HeldInstance> instances, IReadOnlyList<HeldStack> stacks, CatalogueDocument catalogue, long operativeId)
    {
        long entries = instances.Count(i => i.OperativeId == operativeId);
        var known = new List<(CatalogueItem, int)>();
        foreach (var stack in stacks)
        {
            if (stack.OperativeId != operativeId) continue;
            if (FindItem(catalogue, stack.DefinitionId) is { } item) known.Add((item, stack.Quantity));
            else entries++;   // the maximum stack of a retired definition is unknown: one entry per row
        }
        return (int)Math.Min(entries + InventoryRules.BagEntries(known), int.MaxValue);
    }

    /// <summary>The mutable copy a batch works on, so the caller's model is never touched.</summary>
    sealed class WorkingCopy
    {
        readonly CatalogueDocument catalogue;
        readonly HashSet<long> operatives;
        readonly IReadOnlyList<long> operativeList;
        readonly List<HeldInstance> instances;
        readonly List<HeldStack> stacks;

        public WorkingCopy(InventoryModel model, CatalogueDocument catalogue)
        {
            this.catalogue = catalogue;
            operativeList = model.Operatives;
            operatives = model.Operatives.ToHashSet();
            instances = [.. model.Instances];
            stacks = [.. model.Stacks];
        }

        public InventoryModel ToModel() => new([.. operativeList], instances.ToArray(), stacks.ToArray());

        public InventoryRefusal? Step(InventoryOperation operation) => operation switch
        {
            Equip e => DoEquip(e),
            Unequip u => DoUnequip(u),
            MoveInstance { To: Destination.Stash } m => DoItemToStash(m),
            MoveInstance m => DoItemToBag(m),
            MoveStack m => DoMoveStack(m),
            EquipFromStash e => DoEquipFromStash(e),
            _ => throw new ArgumentException($"Unsupported operation {operation.GetType().Name}.", nameof(operation)),
        };

        InventoryRefusal? DoEquip(Equip e)
        {
            if (!operatives.Contains(e.OperativeId)) return InventoryRefusal.UnknownOperative;
            var at = IndexOfInstance(e.ItemId, e.OperativeId);
            if (at < 0) return InventoryRefusal.NotFound;
            if (FindItem(catalogue, instances[at].DefinitionId) is not { } item) return InventoryRefusal.UnknownItem;
            if (item.Slot == ItemSlot.None) return InventoryRefusal.NotEquippable;
            EquipAt(at, item.Slot);
            return null;
        }

        InventoryRefusal? DoUnequip(Unequip u)
        {
            if (!operatives.Contains(u.OperativeId)) return InventoryRefusal.UnknownOperative;
            var at = instances.FindIndex(i => i.OperativeId == u.OperativeId && i.Equipped == u.Slot);
            if (at < 0) return InventoryRefusal.NothingEquipped;
            instances[at] = instances[at] with { Equipped = null };
            return null;
        }

        InventoryRefusal? DoItemToStash(MoveInstance m)
        {
            if (!operatives.Contains(m.OperativeId)) return InventoryRefusal.UnknownOperative;
            var at = IndexOfInstance(m.ItemId, m.OperativeId);
            if (at >= 0 && instances[at].Equipped is not null) return InventoryRefusal.Equipped;
            if (at < 0) return InventoryRefusal.NotFound;
            instances[at] = instances[at] with { OperativeId = null };   // a retired definition may leave (S5-D7)
            return null;
        }

        InventoryRefusal? DoItemToBag(MoveInstance m)
        {
            if (!operatives.Contains(m.OperativeId)) return InventoryRefusal.UnknownOperative;
            var at = IndexOfInstance(m.ItemId, null);
            if (at < 0) return InventoryRefusal.NotFound;
            if (FindItem(catalogue, instances[at].DefinitionId) is null) return InventoryRefusal.UnknownItem;
            if (FreeEntries(m.OperativeId) < 1) return InventoryRefusal.DestinationFull;
            instances[at] = instances[at] with { OperativeId = m.OperativeId, Equipped = null };
            return null;
        }

        InventoryRefusal? DoMoveStack(MoveStack m)
        {
            if (!operatives.Contains(m.OperativeId)) return InventoryRefusal.UnknownOperative;
            if (m.Quantity < 1) return InventoryRefusal.InvalidQuantity;

            var toBag = m.To == Destination.Bag;
            long? source = toBag ? null : m.OperativeId;
            long? target = toBag ? m.OperativeId : null;
            var from = IndexOfStack(source, m.DefinitionId);
            if (from < 0) return InventoryRefusal.NotFound;

            var item = FindItem(catalogue, m.DefinitionId);
            if (toBag && item is null) return InventoryRefusal.UnknownItem;   // a retired stack may still go to the stash
            if (stacks[from].Quantity < m.Quantity) return InventoryRefusal.InsufficientQuantity;

            var into = IndexOfStack(target, m.DefinitionId);
            if (toBag)
            {
                var maxStack = Math.Max(1, item!.MaxStack);
                var held = into < 0 ? 0L : stacks[into].Quantity;
                var extraEntries = (held + m.Quantity + maxStack - 1) / maxStack - (held + maxStack - 1) / maxStack;
                if (extraEntries > FreeEntries(m.OperativeId)) return InventoryRefusal.DestinationFull;
            }

            if (stacks[from].Quantity == m.Quantity) stacks.RemoveAt(from);
            else stacks[from] = stacks[from] with { Quantity = stacks[from].Quantity - m.Quantity };

            into = IndexOfStack(target, m.DefinitionId);   // removing the source row may have shifted it
            if (into < 0) stacks.Add(new HeldStack(target, m.DefinitionId, m.Quantity));
            else stacks[into] = stacks[into] with { Quantity = checked(stacks[into].Quantity + m.Quantity) };
            return null;
        }

        InventoryRefusal? DoEquipFromStash(EquipFromStash e)
        {
            if (!operatives.Contains(e.OperativeId)) return InventoryRefusal.UnknownOperative;
            var at = IndexOfInstance(e.ItemId, null);
            if (at < 0) return InventoryRefusal.NotFound;
            if (FindItem(catalogue, instances[at].DefinitionId) is not { } item) return InventoryRefusal.UnknownItem;
            if (item.Slot == ItemSlot.None || item.Stackable) return InventoryRefusal.NotEquippable;
            if (FreeEntries(e.OperativeId) < 1) return InventoryRefusal.DestinationFull;
            instances[at] = instances[at] with { OperativeId = e.OperativeId, Equipped = null };
            EquipAt(at, item.Slot);
            return null;
        }

        /// <summary>Puts the instance into the slot; the previous occupant stays in the bag, unequipped.</summary>
        void EquipAt(int at, ItemSlot slot)
        {
            var holder = instances[at].OperativeId;
            for (var i = 0; i < instances.Count; i++)
                if (i != at && instances[i].OperativeId == holder && instances[i].Equipped == slot)
                    instances[i] = instances[i] with { Equipped = null };
            instances[at] = instances[at] with { Equipped = slot };
        }

        int FreeEntries(long operativeId) => Math.Max(0, catalogue.Inventory.BagCapacity - Entries(instances, stacks, catalogue, operativeId));

        int IndexOfInstance(long itemId, long? holder) => instances.FindIndex(i => i.ItemId == itemId && i.OperativeId == holder);

        int IndexOfStack(long? holder, string definitionId) => stacks.FindIndex(s => s.OperativeId == holder && s.DefinitionId == definitionId);
    }
}
