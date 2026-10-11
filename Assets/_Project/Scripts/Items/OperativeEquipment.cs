using System;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The definitions an operative has equipped right now, resolved from its loadout. IsSet is true for any resolved
    /// loadout (even with every slot empty) and false for the default value, which means "this unit has no equipment
    /// system" and leaves a unit's configuration exactly as the definition and its archetype give it.
    /// </summary>
    public readonly struct EquippedItems
    {
        public EquippedItems(ItemDefinition weapon, ItemDefinition armor, ItemDefinition utility)
        {
            IsSet = true;
            Weapon = weapon;
            Armor = armor;
            Utility = utility;
        }

        public bool IsSet { get; }
        public ItemDefinition Weapon { get; }
        public ItemDefinition Armor { get; }
        public ItemDefinition Utility { get; }

        /// <summary>The passive modifiers of everything equipped, summed.</summary>
        public StatModifiers Modifiers
        {
            get
            {
                var total = default(StatModifiers);
                if (Weapon != null)
                    total = StatModifiers.Combine(total, Weapon.Modifiers);
                if (Armor != null)
                    total = StatModifiers.Combine(total, Armor.Modifiers);
                if (Utility != null)
                    total = StatModifiers.Combine(total, Utility.Modifiers);
                return total;
            }
        }
    }

    /// <summary>
    /// Three equipment slots, each holding the instance id of an entry in the owner's bag. Equipping never copies an item:
    /// the entry stays in the bag and the slot points at it. A slot whose entry is gone resolves to empty.
    /// </summary>
    [Serializable]
    public sealed class OperativeEquipment
    {
        [SerializeField] string weapon = "";
        [SerializeField] string armor = "";
        [SerializeField] string utility = "";

        public string InstanceIdIn(ItemSlot slot)
        {
            switch (slot)
            {
                case ItemSlot.Weapon: return weapon;
                case ItemSlot.Armor: return armor;
                case ItemSlot.Utility: return utility;
                default: return string.Empty;
            }
        }

        public bool Contains(string instanceId) =>
            !string.IsNullOrEmpty(instanceId) && (weapon == instanceId || armor == instanceId || utility == instanceId);

        public EditResult TryEquip(ItemInventory bag, ItemCatalogue catalogue, string instanceId)
        {
            var entry = bag != null ? bag.Find(instanceId) : null;
            if (entry == null)
                return EditResult.Fail("That item is not in this bag.");
            var definition = catalogue != null ? catalogue.Find(entry.DefinitionId) : null;
            if (definition == null)
                return EditResult.Fail($"Unknown item '{entry.DefinitionId}' cannot be equipped.");
            if (definition.Slot == ItemSlot.None)
                return EditResult.Fail($"{definition.DisplayName} can't be equipped.");
            Set(definition.Slot, instanceId);
            return EditResult.Success;
        }

        public bool Unequip(ItemSlot slot)
        {
            if (string.IsNullOrEmpty(InstanceIdIn(slot)))
                return false;
            Set(slot, string.Empty);
            return true;
        }

        /// <summary>
        /// Clears every slot whose instance id is set but no longer in the bag, so the item cannot silently come back
        /// equipped if the same instance returns later. Returns how many references were dropped.
        /// </summary>
        internal int DropDangling(ItemInventory bag)
        {
            var dropped = 0;
            if (IsDangling(bag, weapon)) { weapon = string.Empty; dropped++; }
            if (IsDangling(bag, armor)) { armor = string.Empty; dropped++; }
            if (IsDangling(bag, utility)) { utility = string.Empty; dropped++; }
            return dropped;
        }

        static bool IsDangling(ItemInventory bag, string instanceId) =>
            !string.IsNullOrEmpty(instanceId) && (bag == null || bag.Find(instanceId) == null);

        public EquippedItems Resolve(ItemInventory bag, ItemCatalogue catalogue) =>
            new EquippedItems(Lookup(bag, catalogue, weapon), Lookup(bag, catalogue, armor), Lookup(bag, catalogue, utility));

        static ItemDefinition Lookup(ItemInventory bag, ItemCatalogue catalogue, string instanceId)
        {
            var entry = bag != null ? bag.Find(instanceId) : null;
            return entry != null && catalogue != null ? catalogue.Find(entry.DefinitionId) : null;
        }

        void Set(ItemSlot slot, string instanceId)
        {
            switch (slot)
            {
                case ItemSlot.Weapon: weapon = instanceId; break;
                case ItemSlot.Armor: armor = instanceId; break;
                case ItemSlot.Utility: utility = instanceId; break;
            }
        }

        public OperativeEquipment Clone() => new OperativeEquipment { weapon = weapon, armor = armor, utility = utility };
    }
}
