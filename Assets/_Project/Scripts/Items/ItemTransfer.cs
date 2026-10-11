using System;
using UnityEngine;

namespace Blackglass
{
    public enum TransferFailure
    {
        None,
        InvalidQuantity,
        SameContainer,
        NotFound,
        UnknownItem,
        DestinationFull,
        /// <summary>The entry is equipped and must be unequipped first.</summary>
        Equipped,
        /// <summary>A mission is running: equipment and stash transfers are closed.</summary>
        Locked,
    }

    /// <summary>
    /// What a transfer did. Failure is None when everything that could be taken from the entry moved; DestinationFull with
    /// Moved above 0 is a partial move (IsPartial); every other failure moved nothing. Remaining is how many units of the
    /// entry are still at the source: reported for every outcome once the entry is known to exist (success, partial,
    /// DestinationFull, UnknownItem, Equipped); 0 when the move was refused before the entry was looked up (InvalidQuantity,
    /// SameContainer) or there is no such entry (NotFound).
    /// </summary>
    public readonly struct TransferResult
    {
        public TransferResult(int requested, int moved, int remaining, TransferFailure failure)
        {
            Requested = requested;
            Moved = moved;
            Remaining = remaining;
            Failure = failure;
        }

        public int Requested { get; }
        public int Moved { get; }
        public int Remaining { get; }
        public TransferFailure Failure { get; }
        public bool IsPartial => Moved > 0 && Failure != TransferFailure.None;
        public bool Succeeded => Moved > 0 && Failure == TransferFailure.None;

        public static TransferResult Failed(TransferFailure failure, int requested, int remaining = 0) => new TransferResult(requested, 0, remaining, failure);

        public string Describe(string itemName)
        {
            switch (Failure)
            {
                case TransferFailure.None:
                    return $"Moved {Moved} {itemName}.";
                case TransferFailure.DestinationFull:
                    return Moved > 0
                        ? $"Moved {Moved} of {Requested} {itemName}; {Remaining} left (no room)."
                        : $"No room for {itemName}.";
                case TransferFailure.Equipped:
                    return $"{itemName} is equipped. Unequip it first.";
                case TransferFailure.Locked:
                    return InventorySession.LockedReason;
                case TransferFailure.UnknownItem:
                    return "Unknown item; it cannot be moved.";
                case TransferFailure.NotFound:
                    return $"{itemName} is no longer there.";
                case TransferFailure.SameContainer:
                    return "Already there.";
                default:
                    return "Nothing to move.";
            }
        }
    }

    /// <summary>
    /// Moves units between two inventories as one step: it removes from the source only what the destination accepted, so a
    /// failure can neither duplicate nor destroy an item. A non-stackable entry moves whole and keeps its instance id.
    /// </summary>
    public static class ItemTransfer
    {
        public static TransferResult Move(ItemInventory source, ItemInventory destination, string instanceId, int quantity,
            ItemCatalogue catalogue)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (quantity < 1)
                return TransferResult.Failed(TransferFailure.InvalidQuantity, quantity);
            if (ReferenceEquals(source, destination))
                return TransferResult.Failed(TransferFailure.SameContainer, quantity);
            var entry = source.Find(instanceId);
            if (entry == null)
                return TransferResult.Failed(TransferFailure.NotFound, quantity);
            var definition = catalogue != null ? catalogue.Find(entry.DefinitionId) : null;
            if (definition == null)
                return TransferResult.Failed(TransferFailure.UnknownItem, quantity, entry.Quantity);

            var wanted = Math.Min(quantity, entry.Quantity);
            if (!definition.IsStackable)
            {
                if (!destination.TryAddEntry(entry))
                    return TransferResult.Failed(TransferFailure.DestinationFull, quantity, entry.Quantity);
                source.RemoveEntry(entry);
                return new TransferResult(quantity, 1, 0, TransferFailure.None);
            }

            var moved = Math.Min(wanted, destination.RoomFor(definition));
            if (moved < 1)
                return TransferResult.Failed(TransferFailure.DestinationFull, quantity, entry.Quantity);
            var notAdded = destination.Add(definition, moved);
            moved -= notAdded;
            source.Remove(instanceId, moved);
            var remaining = source.Find(instanceId) != null ? source.Find(instanceId).Quantity : 0;
            return new TransferResult(quantity, moved, remaining, moved < wanted ? TransferFailure.DestinationFull : TransferFailure.None);
        }
    }
}
