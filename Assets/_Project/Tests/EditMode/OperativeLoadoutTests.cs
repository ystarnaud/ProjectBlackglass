using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class OperativeLoadoutTests
    {
        readonly List<Object> made = new List<Object>();
        ItemDefinition rifle, marksman, vest, medkit;
        ItemCatalogue catalogue;

        [SetUp]
        public void SetUp()
        {
            var ranged = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            var mark = Track(CombatArchetype.Create("Marksman", CombatRole.Ranged, 16f, 40, 2.5f));
            rifle = Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: ranged));
            marksman = Track(ItemDefinition.Create("marksman", "Marksman Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: mark));
            vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor, modifiers: new StatModifiers { maxHealth = 20 }));
            medkit = Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, maxStack: 3, healAmount: 40));
            catalogue = Track(ItemCatalogue.Create(rifle, marksman, vest, medkit));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [Test]
        public void Equip_ReferencesTheBagEntry_AndNeverCreatesACopy()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(rifle, 1);
            var id = loadout.Bag.Entries[0].InstanceId;

            var result = loadout.Equipment.TryEquip(loadout.Bag, catalogue, id);

            Assert.That(result.Ok, Is.True, result.Reason);
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(id));
            Assert.That(loadout.Bag.Count, Is.EqualTo(1), "equipping adds no entry");
            Assert.That(loadout.Resolve(catalogue).Weapon == rifle, Is.True);
        }

        [Test]
        public void Equip_Refuses_ItemsNotInTheBag_AndItemsWithoutASlot()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(medkit, 1);

            Assert.That(loadout.Equipment.TryEquip(loadout.Bag, catalogue, "missing").Ok, Is.False);
            Assert.That(loadout.Equipment.TryEquip(loadout.Bag, catalogue, loadout.Bag.Entries[0].InstanceId).Ok, Is.False);
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.Null.Or.Empty);
        }

        [Test]
        public void Equip_ASecondWeapon_ReplacesTheReference_AndKeepsBothInTheBag()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(rifle, 1);
            loadout.Bag.Add(marksman, 1);
            var first = loadout.Bag.Entries[0].InstanceId;
            var second = loadout.Bag.Entries[1].InstanceId;

            loadout.Equipment.TryEquip(loadout.Bag, catalogue, first);
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, second);

            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(second));
            Assert.That(loadout.Equipment.Contains(first), Is.False);
            Assert.That(loadout.Bag.Count, Is.EqualTo(2));
        }

        [Test]
        public void Unequip_ClearsOnlyTheReference()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(vest, 1);
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, loadout.Bag.Entries[0].InstanceId);

            Assert.That(loadout.Equipment.Unequip(ItemSlot.Armor), Is.True);
            Assert.That(loadout.Equipment.Unequip(ItemSlot.Armor), Is.False);
            Assert.That(loadout.Bag.Count, Is.EqualTo(1));
            Assert.That(loadout.Resolve(catalogue).Armor == null, Is.True);
        }

        [Test]
        public void Resolve_IsSet_EvenWhenNothingIsEquipped_AndSumsModifiers()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            Assert.That(loadout.Resolve(catalogue).IsSet, Is.True);
            Assert.That(loadout.Resolve(catalogue).Weapon == null, Is.True);

            loadout.Bag.Add(vest, 1);
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, loadout.Bag.Entries[0].InstanceId);

            Assert.That(loadout.Resolve(catalogue).Modifiers.maxHealth, Is.EqualTo(20));
        }

        [Test]
        public void Resolve_IgnoresAReferenceWhoseEntryIsGone()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(vest, 1);
            var id = loadout.Bag.Entries[0].InstanceId;
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, id);
            loadout.Bag.Remove(id, 1);   // bypasses the state rules on purpose

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("op-1.*1 equipment reference"));
            Assert.That(loadout.Resolve(catalogue).Armor == null, Is.True);
        }

        [Test]
        public void Resolve_DropsADanglingReference_SoItIsNotRevivedWhenTheSameIdReturns()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(vest, 1);
            var entry = loadout.Bag.Entries[0];
            var id = entry.InstanceId;
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, id);
            loadout.Bag.Remove(id, 1);   // bypasses the state rules on purpose

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("op-1.*1 equipment reference"));
            loadout.Resolve(catalogue);
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Armor), Is.Null.Or.Empty);

            Assert.That(loadout.Bag.TryAddEntry(entry), Is.True);   // the same instance id comes back
            Assert.That(loadout.Resolve(catalogue).Armor == null, Is.True, "not silently equipped again");
        }

        [Test]
        public void Resolve_OnAHealthyLoadout_LogsNothing()
        {
            var loadout = new OperativeLoadout("op-1", 8);
            loadout.Bag.Add(vest, 1);
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, loadout.Bag.Entries[0].InstanceId);

            Assert.That(loadout.Resolve(catalogue).Armor == vest, Is.True);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void State_RefusesToMoveAnEquippedItem_ThenAllowsItAfterUnequip()
        {
            var state = new InventoryState();
            var loadout = state.EnsureLoadout("op-1", 8);
            loadout.Bag.Add(rifle, 1);
            var id = loadout.Bag.Entries[0].InstanceId;
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, id);

            var blocked = state.MoveToStash("op-1", id, 1, catalogue);
            loadout.Equipment.Unequip(ItemSlot.Weapon);
            var moved = state.MoveToStash("op-1", id, 1, catalogue);

            Assert.That(blocked.Failure, Is.EqualTo(TransferFailure.Equipped));
            Assert.That(moved.Moved, Is.EqualTo(1));
            Assert.That(state.Stash.Find(id), Is.Not.Null);
            Assert.That(loadout.Bag.Find(id), Is.Null);
        }

        [Test]
        public void State_MoveOfAnEquippedItem_ReportsTheEntryAsRemaining()
        {
            var state = new InventoryState();
            var loadout = state.EnsureLoadout("op-1", 8);
            loadout.Bag.Add(vest, 1);
            var id = loadout.Bag.Entries[0].InstanceId;
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, id);

            var result = state.MoveToStash("op-1", id, 1, catalogue);

            Assert.That(result.Failure, Is.EqualTo(TransferFailure.Equipped));
            Assert.That(result.Moved, Is.EqualTo(0));
            Assert.That(result.Remaining, Is.EqualTo(1));
        }

        [Test]
        public void State_MoveToBag_RespectsTheBagLimit_AndKeepsTheRestInTheStash()
        {
            var state = new InventoryState();
            var loadout = state.EnsureLoadout("op-1", 1);
            loadout.Bag.Add(vest, 1);
            state.Stash.Add(rifle, 1);

            var result = state.MoveToBag("op-1", state.Stash.Entries[0].InstanceId, 1, catalogue);

            Assert.That(result.Failure, Is.EqualTo(TransferFailure.DestinationFull));
            Assert.That(state.Stash.Count, Is.EqualTo(1));
        }

        [Test]
        public void State_UnknownOperative_IsNotFound()
        {
            var state = new InventoryState();
            Assert.That(state.MoveToStash("nobody", "x", 1, catalogue).Failure, Is.EqualTo(TransferFailure.NotFound));
        }

        [Test]
        public void EnsureLoadout_IsIdempotent_AndLoadoutsAreKeyedByOperativeId()
        {
            var state = new InventoryState();

            var a = state.EnsureLoadout("op-1", 8);
            var again = state.EnsureLoadout("op-1", 8);
            state.EnsureLoadout("op-2", 8);

            Assert.That(again, Is.SameAs(a));
            Assert.That(state.Loadouts, Has.Count.EqualTo(2));
            Assert.That(state.Loadout("op-2").OperativeId, Is.EqualTo("op-2"));
            Assert.That(state.Loadout("op-3"), Is.Null);
        }

        [Test]
        public void Clone_SharesNoCollection_AndKeepsInstanceIdsAndEquipment()
        {
            var state = new InventoryState();
            var loadout = state.EnsureLoadout("op-1", 8);
            loadout.Bag.Add(rifle, 1);
            var id = loadout.Bag.Entries[0].InstanceId;
            loadout.Equipment.TryEquip(loadout.Bag, catalogue, id);
            state.Stash.Add(medkit, 2);

            var copy = state.Clone();
            copy.Loadout("op-1").Equipment.Unequip(ItemSlot.Weapon);
            copy.Loadout("op-1").Bag.Add(vest, 1);
            copy.Stash.Remove(copy.Stash.Entries[0].InstanceId, 2);

            Assert.That(copy.Loadout("op-1").Bag.Find(id), Is.Not.Null, "same item, same instance id");
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(id));
            Assert.That(loadout.Bag.Count, Is.EqualTo(1));
            Assert.That(state.Stash.CountOf("medkit"), Is.EqualTo(2));
        }
    }
}
