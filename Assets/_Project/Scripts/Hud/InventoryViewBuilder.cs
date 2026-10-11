using UnityEngine;

namespace Blackglass
{
    /// <summary>The optional gameplay references the panel's view reads; a missing one hides the part it feeds.</summary>
    public sealed class InventoryViewSources
    {
        public SquadInventory inventory;
        public SquadRoster roster;
        public MissionDirector director;
        public IntelligenceService intelligence;
        public LootContainer container;
    }

    /// <summary>
    /// The only code that reads gameplay for the inventory panel (the HUD's snapshot boundary, decision 042, for the modal).
    /// It decides what is shown and which actions are offered, with the reason when one is not. Container contents pass
    /// LootKnowledge (searched only) and the container's location passes the intelligence rule, so the panel is never a
    /// bypass around the fog of war.
    /// </summary>
    public static class InventoryViewBuilder
    {
        public static void Build(InventoryViewSources s, string inspectedId, InventorySelection selection, InventoryView into)
        {
            into.Clear();
            var inventory = s != null ? s.inventory : null;
            if (inventory == null)
                return;
            var core = inventory.Core;
            var state = core.Active;
            var catalogue = core.Catalogue;
            into.HasInventory = true;
            into.Mode = core.Mode;
            into.Header = core.Mode == InventoryMode.Loadout ? "LOADOUT" : "INVENTORY - MISSION";
            into.BagCapacity = core.BagCapacity;

            BuildTabs(s, state, inspectedId, into);
            if (string.IsNullOrEmpty(into.InspectedId))
                return;
            var loadout = state.Loadout(into.InspectedId);
            if (loadout == null)
                return;
            var equipped = loadout.Resolve(catalogue);

            for (var i = 0; i < 3; i++)
            {
                var slot = (ItemSlot)(i + 1);
                var id = loadout.Equipment.InstanceIdIn(slot);
                var entry = loadout.Bag.Find(id);
                into.Slots[i] = new InventorySlotView
                {
                    Slot = slot, Title = slot.ToString().ToUpperInvariant(),
                    ItemLabel = entry != null ? LabelOf(catalogue, entry.DefinitionId) : "(empty)", InstanceId = entry != null ? id : string.Empty,
                };
            }
            var firstOfDefinition = new System.Collections.Generic.HashSet<string>();
            foreach (var entry in loadout.Bag.Entries)
            {
                var tag = string.Empty;
                if (core.Mode == InventoryMode.InMission && firstOfDefinition.Add(entry.DefinitionId))
                {
                    var unsecured = core.UnsecuredCount(into.InspectedId, entry.DefinitionId);
                    if (unsecured > 0)
                        tag = "NEW +" + unsecured;
                }
                into.Bag.Add(new InventoryRow
                {
                    InstanceId = entry.InstanceId, Label = LabelOf(catalogue, entry.DefinitionId), Quantity = entry.Quantity,
                    IsEquipped = loadout.Equipment.Contains(entry.InstanceId), Tag = tag,
                });
            }
            if (core.Mode == InventoryMode.Loadout)
            {
                foreach (var entry in state.Stash.Entries)
                    into.Stash.Add(new InventoryRow { InstanceId = entry.InstanceId, Label = LabelOf(catalogue, entry.DefinitionId), Quantity = entry.Quantity, Tag = string.Empty });
            }

            var container = s.container;
            if (container != null && LootKnowledge.CanSeeLocation(s.intelligence, container) && LootKnowledge.CanSeeContents(container))
            {
                into.HasContainer = true;
                foreach (var entry in container.Contents.Entries)
                    into.Container.Add(new InventoryRow { InstanceId = entry.InstanceId, Label = LabelOf(catalogue, entry.DefinitionId), Quantity = entry.Quantity, Tag = string.Empty });
            }

            ResolveSelection(selection, loadout, state, container, into);
            BuildActions(s, core, loadout, equipped, into);
        }

        static void BuildTabs(InventoryViewSources s, InventoryState state, string inspectedId, InventoryView into)
        {
            foreach (var loadout in state.Loadouts)
            {
                var id = loadout.OperativeId;
                var member = s.roster != null ? s.roster.Find(id) : null;
                var name = member != null ? member.Definition.DisplayName : id;
                var unit = FindUnit(s.director, id);
                into.Tabs.Add(new InventoryTab { OperativeId = id, Name = name, IsDown = unit != null && !unit.IsAlive });
            }
            var chosen = into.Tabs.FindIndex(t => t.OperativeId == inspectedId);
            if (chosen < 0 && into.Tabs.Count > 0)
                chosen = 0;
            for (var i = 0; i < into.Tabs.Count; i++)
            {
                var tab = into.Tabs[i];
                tab.IsInspected = i == chosen;
                into.Tabs[i] = tab;
            }
            if (chosen >= 0)
            {
                into.InspectedId = into.Tabs[chosen].OperativeId;
                into.InspectedName = into.Tabs[chosen].Name;
            }
        }

        static void ResolveSelection(InventorySelection selection, OperativeLoadout loadout, InventoryState state, LootContainer container,
            InventoryView into)
        {
            if (selection.IsNone)
                return;
            ItemEntry entry = null;
            switch (selection.Kind)
            {
                case InventoryListKind.Bag:
                case InventoryListKind.Slot:
                    entry = loadout.Bag.Find(selection.InstanceId);
                    break;
                case InventoryListKind.Stash:
                    entry = into.Mode == InventoryMode.Loadout ? state.Stash.Find(selection.InstanceId) : null;
                    break;
                case InventoryListKind.Container:
                    entry = into.HasContainer ? container.Contents.Find(selection.InstanceId) : null;
                    break;
            }
            if (entry == null)
                return;
            into.Selected = selection;
        }

        static void BuildActions(InventoryViewSources s, InventorySession core, OperativeLoadout loadout, EquippedItems equipped, InventoryView into)
        {
            var inMission = core.Mode == InventoryMode.InMission;
            var catalogue = core.Catalogue;
            var unit = FindUnit(s.director, into.InspectedId);
            var items = unit != null ? unit.GetComponent<UnitItems>() : null;
            var unitBlock = UnitBlock(unit, into.InspectedName);   // "" when the unit is alive and on the mission

            if (into.HasContainer)
            {
                var canTake = inMission && unitBlock.Length == 0;
                var why = !inMission ? "The mission is over: this loot is out of reach." : unitBlock;
                into.TakeAll = canTake ? InventoryAction.Usable : InventoryAction.Blocked(why);
                into.QueueTakeAll = into.TakeAll;
            }

            if (into.Selected.IsNone)
                return;
            var entry = FindEntry(into.Selected, loadout, core.Active, s.container);
            var definition = entry != null && catalogue != null ? catalogue.Find(entry.DefinitionId) : null;
            if (definition == null)
            {
                into.DetailTitle = entry != null ? $"Unknown item ({entry.DefinitionId})" : string.Empty;
                into.DetailBody = "This item's definition is missing. It cannot be used or equipped.";
                return;
            }
            var current = definition.Slot != ItemSlot.None ? SlotItem(equipped, definition.Slot) : null;
            into.DetailTitle = definition.DisplayName + (entry.Quantity > 1 ? " x" + entry.Quantity : string.Empty);
            into.DetailBody = ItemText.Describe(definition)
                + (definition.Slot != ItemSlot.None && (current == null || current != definition) ? "\n\n" + ItemText.Compare(definition, current) : string.Empty)
                + (definition.Description.Length > 0 ? "\n\n" + definition.Description : string.Empty);

            var locked = inMission ? InventorySession.LockedReason : string.Empty;
            switch (into.Selected.Kind)
            {
                case InventoryListKind.Bag:
                case InventoryListKind.Slot:
                {
                    var isEquipped = loadout.Equipment.Contains(entry.InstanceId);
                    if (definition.Slot != ItemSlot.None)
                    {
                        if (isEquipped)
                            into.Unequip = inMission ? InventoryAction.Blocked(locked) : InventoryAction.Usable;
                        else if (!inMission)
                            into.Equip = InventoryAction.Usable;
                        else if (unitBlock.Length > 0 || items == null)
                            into.Equip = InventoryAction.Blocked(unitBlock.Length > 0 ? unitBlock : into.InspectedName + " cannot swap gear.");
                        else
                        {
                            // In a mission this is a timed swap order (UnitItems.SwapSeconds); only unequipping and the stash stay locked.
                            var swap = items.CheckEquip(entry.InstanceId);
                            into.Equip = swap == ItemUseFailure.None ? InventoryAction.Usable : InventoryAction.Blocked(UseReason(swap));
                        }
                    }
                    if (!isEquipped)
                        into.ToStash = inMission ? InventoryAction.Blocked(locked) : InventoryAction.Usable;
                    else if (!inMission)
                        into.ToStash = InventoryAction.Blocked(definition.DisplayName + " is equipped. Unequip it first.");
                    else
                        into.ToStash = InventoryAction.Blocked(locked);
                    if (definition.Category == ItemCategory.Consumable)
                    {
                        if (!inMission)
                        {
                            into.Use = InventoryAction.Blocked("Consumables are used during a mission.");
                            into.QueueUse = into.Use;
                        }
                        else if (unitBlock.Length > 0 || items == null)
                        {
                            into.Use = InventoryAction.Blocked(unitBlock.Length > 0 ? unitBlock : into.InspectedName + " cannot use items.");
                            into.QueueUse = into.Use;
                        }
                        else
                        {
                            var now = items.Check(entry.InstanceId, true);
                            var queued = items.Check(entry.InstanceId, false);
                            into.Use = now == ItemUseFailure.None ? InventoryAction.Usable : InventoryAction.Blocked(UseReason(now));
                            into.QueueUse = queued == ItemUseFailure.None ? InventoryAction.Usable : InventoryAction.Blocked(UseReason(queued));
                        }
                    }
                    break;
                }
                case InventoryListKind.Stash:
                {
                    var bagHasRoom = loadout.Bag.RoomFor(definition) > 0;
                    into.ToBag = bagHasRoom ? InventoryAction.Usable : InventoryAction.Blocked(into.InspectedName + "'s bag is full.");
                    // Equip from here moves the item to the bag and equips it in one step.
                    if (definition.Slot != ItemSlot.None && !definition.IsStackable)
                        into.Equip = bagHasRoom ? InventoryAction.Usable : InventoryAction.Blocked(into.InspectedName + "'s bag is full.");
                    break;
                }
                case InventoryListKind.Container:
                {
                    var canTake = inMission && unitBlock.Length == 0;
                    var why = !inMission ? "The mission is over: this loot is out of reach." : unitBlock;
                    into.Take = canTake ? InventoryAction.Usable : InventoryAction.Blocked(why);
                    into.QueueTake = into.Take;
                    break;
                }
            }
        }

        static ItemEntry FindEntry(InventorySelection selection, OperativeLoadout loadout, InventoryState state, LootContainer container)
        {
            switch (selection.Kind)
            {
                case InventoryListKind.Bag:
                case InventoryListKind.Slot: return loadout.Bag.Find(selection.InstanceId);
                case InventoryListKind.Stash: return state.Stash.Find(selection.InstanceId);
                case InventoryListKind.Container: return container != null ? container.Contents.Find(selection.InstanceId) : null;
                default: return null;
            }
        }

        static ItemDefinition SlotItem(EquippedItems equipped, ItemSlot slot)
        {
            switch (slot)
            {
                case ItemSlot.Weapon: return equipped.Weapon;
                case ItemSlot.Armor: return equipped.Armor;
                case ItemSlot.Utility: return equipped.Utility;
                default: return null;
            }
        }

        static string LabelOf(ItemCatalogue catalogue, string definitionId)
        {
            var definition = catalogue != null ? catalogue.Find(definitionId) : null;
            return definition != null ? definition.DisplayName : $"Unknown item ({definitionId})";
        }

        /// <summary>Why a unit cannot act on the mission ("" when it is on the mission and alive): absent and down read differently.</summary>
        static string UnitBlock(CommandableUnit unit, string name) =>
            unit == null ? name + " is not on the mission." : !unit.IsAlive ? name + " is down." : string.Empty;

        internal static string UseReason(ItemUseFailure failure)
        {
            switch (failure)
            {
                case ItemUseFailure.Dead: return "Down: cannot use items.";
                case ItemUseFailure.NoLoadout: return "No mission is running.";
                case ItemUseFailure.NotOwned: return "That item is gone.";
                case ItemUseFailure.NotUsable: return "That cannot be used.";
                case ItemUseFailure.NoEffect: return "Already at full health.";
                case ItemUseFailure.NotEquippable: return "That cannot be equipped.";
                case ItemUseFailure.AlreadyEquipped: return "Already equipped.";
                default: return string.Empty;
            }
        }

        /// <summary>The squad member's unit for this operative id, dead units included (they stay in the list), else null.</summary>
        internal static CommandableUnit FindUnit(MissionDirector director, string operativeId)
        {
            if (director == null || string.IsNullOrEmpty(operativeId))
                return null;
            foreach (var unit in director.Friendlies)
            {
                if (unit != null && unit.TryGetComponent<UnitIdentity>(out var identity) && identity.OperativeId == operativeId)
                    return unit;
            }
            return null;
        }
    }
}
