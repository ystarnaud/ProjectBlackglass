using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A list of entries with an optional entry limit (0 = uncapped). A non-stackable item takes one entry; a stack takes
    /// one entry up to its definition's maximum. No grid, no weight. Mutating methods never lose units: Add returns what
    /// did not fit, Remove returns what was taken.
    /// </summary>
    [Serializable]
    public sealed class ItemInventory
    {
        [SerializeField] int capacity;
        [SerializeField] List<ItemEntry> entries = new List<ItemEntry>();

        public ItemInventory(int capacity = 0)
        {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity cannot be negative.");
            this.capacity = capacity;
        }

        public int Capacity => capacity;
        public bool IsUncapped => capacity == 0;
        public int Count => entries.Count;
        public IReadOnlyList<ItemEntry> Entries => entries;
        public int FreeEntries => IsUncapped ? int.MaxValue : Math.Max(0, capacity - entries.Count);

        public ItemEntry Find(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return null;
            foreach (var entry in entries)
            {
                if (entry.InstanceId == instanceId)
                    return entry;
            }
            return null;
        }

        public int CountOf(string definitionId)
        {
            var total = 0;
            foreach (var entry in entries)
            {
                if (entry.DefinitionId == definitionId)
                    total += entry.Quantity;
            }
            return total;
        }

        /// <summary>How many units of this definition could be added right now.</summary>
        public int RoomFor(ItemDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (!definition.IsStackable)
                return FreeEntries;
            if (IsUncapped)
                return int.MaxValue;
            long room = (long)FreeEntries * definition.MaxStack;
            foreach (var entry in entries)
            {
                if (entry.DefinitionId == definition.Id)
                    room += Math.Max(0, definition.MaxStack - entry.Quantity);
            }
            return (int)Math.Min(room, int.MaxValue);
        }

        /// <summary>Adds units, filling open stacks first. Returns how many did not fit (0 when everything was added).</summary>
        public int Add(ItemDefinition definition, int quantity)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (quantity < 0)
                throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity cannot be negative.");
            var left = quantity;
            if (definition.IsStackable)
            {
                foreach (var entry in entries)
                {
                    if (left == 0)
                        return 0;
                    if (entry.DefinitionId != definition.Id || entry.Quantity >= definition.MaxStack)
                        continue;
                    var take = Math.Min(left, definition.MaxStack - entry.Quantity);
                    entry.SetQuantity(entry.Quantity + take);
                    left -= take;
                }
            }
            while (left > 0 && FreeEntries > 0)
            {
                var take = Math.Min(left, definition.MaxStack);
                entries.Add(new ItemEntry(definition.Id, take));
                left -= take;
            }
            return left;
        }

        /// <summary>Removes up to `quantity` units from the entry. Returns how many were removed (0 for an unknown entry or a quantity below 1).</summary>
        public int Remove(string instanceId, int quantity)
        {
            var entry = Find(instanceId);
            if (entry == null || quantity < 1)
                return 0;
            var take = Math.Min(quantity, entry.Quantity);
            entry.SetQuantity(entry.Quantity - take);
            if (entry.Quantity == 0)
                entries.Remove(entry);
            return take;
        }

        /// <summary>Adds an existing entry as it is (a moved non-stackable keeps its instance id). False when full or the id is already here.</summary>
        internal bool TryAddEntry(ItemEntry entry)
        {
            if (entry == null || FreeEntries < 1 || Find(entry.InstanceId) != null)
                return false;
            entries.Add(entry);
            return true;
        }

        internal bool RemoveEntry(ItemEntry entry) => entries.Remove(entry);

        public ItemInventory Clone()
        {
            var copy = new ItemInventory(capacity);
            foreach (var entry in entries)
                copy.entries.Add(entry.Clone());
            return copy;
        }
    }
}
