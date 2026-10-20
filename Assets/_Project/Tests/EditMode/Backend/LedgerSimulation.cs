using System;
using System.Collections.Generic;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// Drives Unity's real item code (ItemInventory, ItemTransfer.Move, ItemInventory.Remove, OperativeEquipment.TryEquip) for
    /// one participant and tells the ledger only what Unity actually applied, with the quantity Unity reported: exactly what the
    /// Stage 12 wiring will do. Nothing in Scripts/Items changes. The bags start in snapshot mode: an owned item's entry id is
    /// its Base58 item id.
    /// </summary>
    internal sealed class LedgerSimulation
    {
        readonly ItemCatalogue catalogue;
        readonly Dictionary<string, ItemInventory> bags = new Dictionary<string, ItemInventory>(StringComparer.Ordinal);
        readonly Dictionary<string, OperativeEquipment> equipment = new Dictionary<string, OperativeEquipment>(StringComparer.Ordinal);
        readonly SortedDictionary<int, ItemInventory> containers = new SortedDictionary<int, ItemInventory>();
        readonly Dictionary<(int, int), string> lineEntries = new Dictionary<(int, int), string>();
        readonly string accountId;

        public LedgerSimulation(ItemCatalogue catalogue, LedgerCatalogue ledgerCatalogue, string accountId, IReadOnlyList<OperativeSnapshot> snapshot, int bagCapacity)
        {
            this.catalogue = catalogue;
            this.accountId = accountId;
            Snapshot = snapshot;
            Ledger = new MissionItemLedger(ledgerCatalogue);
            Ledger.AddParticipant(accountId, snapshot);
            foreach (var o in snapshot)
            {
                var bag = new ItemInventory(bagCapacity);
                foreach (var item in o.Items)
                    Assert.That(bag.TryAddEntry(new ItemEntry(item.ItemId, item.DefinitionId, 1)), Is.True, "the snapshot fits the bag");
                foreach (var stack in o.Stacks)
                    Assert.That(bag.Add(catalogue.Find(stack.DefinitionId), stack.Quantity), Is.EqualTo(0), "the snapshot fits the bag");
                var slots = new OperativeEquipment();
                foreach (var id in new[] { o.WeaponItemId, o.ArmorItemId, o.UtilityItemId }.Where(id => id != null))
                    Assert.That(slots.TryEquip(bag, catalogue, id).Ok, Is.True);
                bags.Add(o.OperativeId, bag);
                equipment.Add(o.OperativeId, slots);
            }
        }

        public MissionItemLedger Ledger { get; }
        public IReadOnlyList<OperativeSnapshot> Snapshot { get; }
        public IReadOnlyList<PoolLine> Pool => Ledger.PoolLines();
        public ItemInventory BagOf(string operativeId) => bags[operativeId];
        public OperativeEquipment EquipmentOf(string operativeId) => equipment[operativeId];
        public ItemInventory Container(int index) => containers[index];
        public IEnumerable<int> ContainerIndexes => containers.Keys;
        public int LinesIn(int index) => lineEntries.Keys.Count(k => k.Item1 == index);

        /// <summary>The container entry id of a pool line, as the fill left it.</summary>
        public string EntryIdOf(int containerIndex, int line) => lineEntries[(containerIndex, line)];

        /// <summary>ItemInventory(0).Add per plan item (merging same-definition stacks), then RegisterContainer with the entries.</summary>
        public void FillContainer(int containerIndex, IEnumerable<(string DefinitionId, int Quantity)> plan)
        {
            var contents = new ItemInventory(0);
            foreach (var (definition, quantity) in plan) contents.Add(catalogue.Find(definition), quantity);
            containers.Add(containerIndex, contents);
            for (var i = 0; i < contents.Entries.Count; i++) lineEntries[(containerIndex, i)] = contents.Entries[i].InstanceId;
            Ledger.RegisterContainer(containerIndex, contents.Entries.Select(e => new ContainerEntry(e.InstanceId, e.DefinitionId, e.Quantity)).ToList());
        }

        /// <summary>ItemTransfer.Move from the line's entry; the ledger hears of it only when Unity moved at least one unit.</summary>
        public TransferResult Collect(string operativeId, int containerIndex, int line, int quantity)
        {
            if (!containers.TryGetValue(containerIndex, out var container) || !lineEntries.TryGetValue((containerIndex, line), out var entryId))
                return TransferResult.Failed(TransferFailure.NotFound, quantity);
            var result = ItemTransfer.Move(container, bags[operativeId], entryId, quantity, catalogue);
            if (result.Moved >= 1) Assert.That(Ledger.Collect(operativeId, containerIndex, entryId, result.Moved), Is.Not.EqualTo(LedgerEventResult.Refused));
            return result;
        }

        /// <summary>UnitItems.Check's rules (a consumable that heals), then bag.Remove(entry, 1); the ledger hears only of a removed unit.</summary>
        public bool Use(string operativeId, string definitionId)
        {
            var definition = catalogue.Find(definitionId);
            if (definition == null || definition.Category != ItemCategory.Consumable || definition.HealAmount < 1) return false;
            var bag = bags[operativeId];
            var entry = bag.Entries.FirstOrDefault(e => e.DefinitionId == definitionId);
            if (entry == null || bag.Remove(entry.InstanceId, 1) != 1) return false;
            Assert.That(Ledger.Use(operativeId, definitionId), Is.Not.EqualTo(LedgerEventResult.Refused));
            return true;
        }

        /// <summary>OperativeEquipment.TryEquip (as InventorySession.EquipInMission); the ledger hears only of a success.</summary>
        public bool Equip(string operativeId, string entryId)
        {
            if (!equipment[operativeId].TryEquip(bags[operativeId], catalogue, entryId).Ok) return false;
            Assert.That(Ledger.Equip(operativeId, entryId), Is.Not.EqualTo(LedgerEventResult.Refused));
            return true;
        }

        /// <summary>A bag change with no ledger event: what a bug in the wiring would look like.</summary>
        public void RogueAdd(string operativeId, string definitionId, int quantity) => bags[operativeId].Add(catalogue.Find(definitionId), quantity);

        public IReadOnlyList<OperativeRuntimeState> OperativeStates() => bags.Select(b => new OperativeRuntimeState(b.Key,
            b.Value.Entries.Select(e => new RuntimeEntry(e.InstanceId, e.DefinitionId, e.Quantity)).ToList(),
            new RuntimeEquipment(equipment[b.Key].InstanceIdIn(ItemSlot.Weapon), equipment[b.Key].InstanceIdIn(ItemSlot.Armor), equipment[b.Key].InstanceIdIn(ItemSlot.Utility))))
            .ToList();

        public IReadOnlyList<ContainerRuntimeState> ContainerStates() => containers.Select(c => new ContainerRuntimeState(c.Key,
            c.Value.Entries.Select(e => new RuntimeEntry(e.InstanceId, e.DefinitionId, e.Quantity)).ToList())).ToList();

        public ParticipantFacts Freeze() => Ledger.Freeze(accountId, OperativeStates(), ContainerStates());

        public void AssertConsistent()
        {
            foreach (var state in OperativeStates())
            {
                var check = Ledger.VerifyOperative(state.OperativeId, state.Bag, state.Equipment);
                Assert.That(check.Ok, Is.True, state.OperativeId + ": " + string.Join(",", check.Codes));
            }
            foreach (var state in ContainerStates())
            {
                var check = Ledger.VerifyContainer(state.ContainerIndex, state.Entries);
                Assert.That(check.Ok, Is.True, "container " + state.ContainerIndex + ": " + string.Join(",", check.Codes));
            }
        }
    }
}
