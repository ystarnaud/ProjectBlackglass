using System.Collections.Generic;
using Blackglass.Backend.Core;

namespace Blackglass
{
    /// <summary>Plain copies of the runtime inventories for the item ledger's checks and freeze (Stage 12): it never reads a Unity object.</summary>
    public static class AttemptRuntime
    {
        public static IReadOnlyList<RuntimeEntry> Entries(ItemInventory inventory)
        {
            var entries = new List<RuntimeEntry>();
            if (inventory != null)
                foreach (var e in inventory.Entries)
                    entries.Add(new RuntimeEntry(e.InstanceId, e.DefinitionId, e.Quantity));
            return entries;
        }

        /// <summary>The operative's working bag and equipment, or null when the squad has no working loadout for it (no mission running).</summary>
        public static OperativeRuntimeState OperativeState(SquadInventory inventory, string operativeId)
        {
            var loadout = inventory != null ? inventory.WorkingLoadout(operativeId) : null;
            if (loadout == null)
                return null;
            var slots = loadout.Equipment;
            return new OperativeRuntimeState(operativeId, Entries(loadout.Bag),
                new RuntimeEquipment(slots.InstanceIdIn(ItemSlot.Weapon), slots.InstanceIdIn(ItemSlot.Armor), slots.InstanceIdIn(ItemSlot.Utility)));
        }

        public static ContainerRuntimeState ContainerState(int index, LootContainer container) =>
            new ContainerRuntimeState(index, container != null ? Entries(container.Contents) : new List<RuntimeEntry>());
    }
}
