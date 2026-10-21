using System.Collections.Generic;
using Blackglass.Backend.Core;

namespace Blackglass
{
    /// <summary>
    /// The one place a Unity item definition becomes the backend core's view of it (Stage 12): stackable when it stacks
    /// beyond one, consumable by category, the slot it equips to. The same facts the catalogue export writes.
    /// </summary>
    public static class ItemLedgerMapping
    {
        public static LedgerItemDefinition Definition(ItemDefinition definition) =>
            new LedgerItemDefinition(definition.Id, definition.IsStackable, definition.Category == ItemCategory.Consumable, Slot(definition.Slot));

        /// <summary>Every definition of the catalogue (empty entries skipped); a repeated id throws, as the catalogue's own validation reports.</summary>
        public static LedgerCatalogue Catalogue(ItemCatalogue catalogue)
        {
            var definitions = new List<LedgerItemDefinition>();
            if (catalogue != null)
                foreach (var item in catalogue.Items)
                    if (item != null)
                        definitions.Add(Definition(item));
            return new LedgerCatalogue(definitions);
        }

        static EquipSlot? Slot(ItemSlot slot)
        {
            switch (slot)
            {
                case ItemSlot.Weapon: return EquipSlot.Weapon;
                case ItemSlot.Armor: return EquipSlot.Armor;
                case ItemSlot.Utility: return EquipSlot.Utility;
                default: return null;
            }
        }
    }
}
