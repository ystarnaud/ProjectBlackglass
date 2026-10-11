using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// All owned items: the shared stash (uncapped) and one loadout per operative id. Plain serializable data with no
    /// Unity object references (a future save stores it as it is). Clone() is a deep copy that keeps every instance id: the
    /// mission's working state is the same items in separate collections.
    /// </summary>
    [Serializable]
    public sealed class InventoryState
    {
        [SerializeField] ItemInventory stash = new ItemInventory(0);
        [SerializeField] List<OperativeLoadout> loadouts = new List<OperativeLoadout>();

        public ItemInventory Stash => stash;
        public IReadOnlyList<OperativeLoadout> Loadouts => loadouts;

        public OperativeLoadout Loadout(string operativeId)
        {
            if (string.IsNullOrEmpty(operativeId))
                return null;
            foreach (var loadout in loadouts)
            {
                if (loadout.OperativeId == operativeId)
                    return loadout;
            }
            return null;
        }

        /// <summary>The operative's loadout, created empty with this bag size when it does not exist yet.</summary>
        public OperativeLoadout EnsureLoadout(string operativeId, int bagCapacity)
        {
            var existing = Loadout(operativeId);
            if (existing != null)
                return existing;
            var made = new OperativeLoadout(operativeId, bagCapacity);
            loadouts.Add(made);
            return made;
        }

        public TransferResult MoveToStash(string operativeId, string instanceId, int quantity, ItemCatalogue catalogue)
        {
            var loadout = Loadout(operativeId);
            if (loadout == null)
                return TransferResult.Failed(TransferFailure.NotFound, quantity);
            if (loadout.Equipment.Contains(instanceId))
            {
                var equipped = loadout.Bag.Find(instanceId);
                return TransferResult.Failed(TransferFailure.Equipped, quantity, equipped != null ? equipped.Quantity : 0);
            }
            return ItemTransfer.Move(loadout.Bag, stash, instanceId, quantity, catalogue);
        }

        public TransferResult MoveToBag(string operativeId, string instanceId, int quantity, ItemCatalogue catalogue)
        {
            var loadout = Loadout(operativeId);
            if (loadout == null)
                return TransferResult.Failed(TransferFailure.NotFound, quantity);
            return ItemTransfer.Move(stash, loadout.Bag, instanceId, quantity, catalogue);
        }

        public InventoryState Clone()
        {
            var copy = new InventoryState { stash = stash.Clone() };
            foreach (var loadout in loadouts)
                copy.loadouts.Add(loadout.Clone());
            return copy;
        }
    }
}
