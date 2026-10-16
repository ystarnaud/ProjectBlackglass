using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Inventory;

/// <summary>
/// One non-stackable item an account owns. OperativeId null means the stash. Equipped is the slot it fills, or null when
/// it is not equipped (never ItemSlot.None); an equipped item is always in its operative's bag.
/// </summary>
public sealed record HeldInstance(long ItemId, string DefinitionId, long? OperativeId, ItemSlot? Equipped);

/// <summary>
/// The fungible units of one definition at one location (OperativeId null is the stash), one row per location and
/// definition, Quantity at least 1. A bag shows ceil(quantity / maxStack) entries for it.
/// </summary>
public sealed record HeldStack(long? OperativeId, string DefinitionId, int Quantity);

/// <summary>An account's inventory as the pure rules see it: its operatives, its items and its stacks. InventoryEditor never modifies one.</summary>
public sealed record InventoryModel(IReadOnlyList<long> Operatives, IReadOnlyList<HeldInstance> Instances, IReadOnlyList<HeldStack> Stacks);
