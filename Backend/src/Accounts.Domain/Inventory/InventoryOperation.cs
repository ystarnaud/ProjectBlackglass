using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Inventory;

public enum Destination { Stash, Bag }

/// <summary>One step of an inventory batch, always about one operative's bag (spec S5-D3: five operations only).</summary>
public abstract record InventoryOperation(long OperativeId);

public sealed record Equip(long OperativeId, long ItemId) : InventoryOperation(OperativeId);

public sealed record Unequip(long OperativeId, ItemSlot Slot) : InventoryOperation(OperativeId);

/// <summary>Moves one non-stackable between the operative's bag and the stash. To is where the item goes.</summary>
public sealed record MoveInstance(long OperativeId, long ItemId, Destination To) : InventoryOperation(OperativeId);

/// <summary>Moves exactly Quantity units of a definition between the stash and the operative's bag. To is where they go.</summary>
public sealed record MoveStack(long OperativeId, string DefinitionId, int Quantity, Destination To) : InventoryOperation(OperativeId);

public sealed record EquipFromStash(long OperativeId, long ItemId) : InventoryOperation(OperativeId);

public enum InventoryRefusal
{
    UnknownOperative,
    NotFound,
    UnknownItem,
    NotEquippable,
    NothingEquipped,
    Equipped,
    DestinationFull,
    InvalidQuantity,
    InsufficientQuantity,
}

/// <summary>The result of a batch: the new model, or the index and reason of the first refusal.</summary>
public sealed record EditOutcome(InventoryModel? After, int? RefusedIndex, InventoryRefusal? Refusal)
{
    public bool Applied => After is not null;
}
