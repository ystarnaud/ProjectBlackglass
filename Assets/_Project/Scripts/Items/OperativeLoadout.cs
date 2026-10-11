using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>One operative's carried items and what is equipped from them, keyed by the stable operative id.</summary>
    [Serializable]
    public sealed class OperativeLoadout
    {
        [SerializeField] string operativeId;
        [SerializeField] ItemInventory bag;
        [SerializeField] OperativeEquipment equipment = new OperativeEquipment();

        public OperativeLoadout(string operativeId, int bagCapacity)
        {
            if (string.IsNullOrWhiteSpace(operativeId))
                throw new ArgumentException("A loadout needs an operative id.", nameof(operativeId));
            this.operativeId = operativeId;
            bag = new ItemInventory(bagCapacity);
        }

        OperativeLoadout() { }

        public string OperativeId => operativeId;
        public ItemInventory Bag => bag;
        public OperativeEquipment Equipment => equipment;

        /// <summary>
        /// The equipped definitions. A slot whose entry is gone from the bag is cleared first and reported with one warning.
        /// </summary>
        public EquippedItems Resolve(ItemCatalogue catalogue)
        {
            var dropped = equipment.DropDangling(bag);
            if (dropped > 0)
                Debug.LogWarning($"Operative '{operativeId}': dropped {dropped} equipment reference(s) to items no longer in the bag.");
            return equipment.Resolve(bag, catalogue);
        }

        public OperativeLoadout Clone() => new OperativeLoadout { operativeId = operativeId, bag = bag.Clone(), equipment = equipment.Clone() };
    }
}
