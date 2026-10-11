using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class ItemInventoryTests
    {
        readonly List<Object> made = new List<Object>();
        ItemDefinition medkit, rifle, vest;
        ItemCatalogue catalogue;

        [SetUp]
        public void SetUp()
        {
            var archetype = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            medkit = Track(ItemDefinition.Create("medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, maxStack: 3, healAmount: 40));
            rifle = Track(ItemDefinition.Create("rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: archetype));
            vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor));
            catalogue = Track(ItemCatalogue.Create(medkit, rifle, vest));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [Test]
        public void Add_StacksUpToTheMaximum_ThenOpensANewEntry()
        {
            var bag = new ItemInventory(4);

            Assert.That(bag.Add(medkit, 2), Is.EqualTo(0));
            Assert.That(bag.Add(medkit, 2), Is.EqualTo(0));

            Assert.That(bag.Count, Is.EqualTo(2));
            Assert.That(bag.Entries[0].Quantity, Is.EqualTo(3));
            Assert.That(bag.Entries[1].Quantity, Is.EqualTo(1));
            Assert.That(bag.CountOf("medkit"), Is.EqualTo(4));
        }

        [Test]
        public void Add_WhenFull_ReturnsTheLeftover_AndChangesNothingElse()
        {
            var bag = new ItemInventory(1);
            bag.Add(medkit, 3);

            var leftover = bag.Add(medkit, 2);

            Assert.That(leftover, Is.EqualTo(2));
            Assert.That(bag.Count, Is.EqualTo(1));
            Assert.That(bag.CountOf("medkit"), Is.EqualTo(3));
        }

        [Test]
        public void NonStackable_TakesOneEntryEach_AndEachHasItsOwnInstanceId()
        {
            var bag = new ItemInventory(0);

            bag.Add(rifle, 2);

            Assert.That(bag.Count, Is.EqualTo(2));
            Assert.That(bag.Entries[0].InstanceId, Is.Not.EqualTo(bag.Entries[1].InstanceId));
            Assert.That(bag.Entries[0].Quantity, Is.EqualTo(1));
        }

        [Test]
        public void RoomFor_CountsOpenStackSpaceAndFreeEntries()
        {
            var bag = new ItemInventory(2);
            bag.Add(medkit, 2);

            Assert.That(bag.RoomFor(medkit), Is.EqualTo(1 + 3));
            Assert.That(bag.RoomFor(rifle), Is.EqualTo(1));
            Assert.That(new ItemInventory(0).RoomFor(rifle), Is.EqualTo(int.MaxValue));
        }

        [Test]
        public void Remove_TakesPartOfAStack_ThenDropsTheEmptyEntry()
        {
            var bag = new ItemInventory(0);
            bag.Add(medkit, 3);
            var id = bag.Entries[0].InstanceId;

            Assert.That(bag.Remove(id, 1), Is.EqualTo(1));
            Assert.That(bag.Entries[0].Quantity, Is.EqualTo(2));
            Assert.That(bag.Remove(id, 5), Is.EqualTo(2));
            Assert.That(bag.Count, Is.EqualTo(0));
            Assert.That(bag.Remove(id, 1), Is.EqualTo(0), "an unknown entry removes nothing");
        }

        [Test]
        public void Clone_IsDeep()
        {
            var bag = new ItemInventory(3);
            bag.Add(medkit, 2);
            var copy = bag.Clone();

            bag.Remove(bag.Entries[0].InstanceId, 2);
            copy.Add(rifle, 1);

            Assert.That(copy.CountOf("medkit"), Is.EqualTo(2));
            Assert.That(bag.Count, Is.EqualTo(0));
            Assert.That(copy.Count, Is.EqualTo(2));
            Assert.That(copy.Capacity, Is.EqualTo(3));
        }

        [Test]
        public void Move_NonStackable_KeepsTheSameInstanceId_AndHasOneLocation()
        {
            var source = new ItemInventory(0);
            var destination = new ItemInventory(2);
            source.Add(rifle, 1);
            var id = source.Entries[0].InstanceId;

            var result = ItemTransfer.Move(source, destination, id, 1, catalogue);

            Assert.That(result.Failure, Is.EqualTo(TransferFailure.None));
            Assert.That(result.Moved, Is.EqualTo(1));
            Assert.That(source.Find(id), Is.Null);
            Assert.That(destination.Find(id), Is.Not.Null);
        }

        [Test]
        public void Move_ToAFullInventory_LeavesTheItemAtTheSource()
        {
            var source = new ItemInventory(0);
            var destination = new ItemInventory(1);
            source.Add(rifle, 1);
            destination.Add(vest, 1);
            var id = source.Entries[0].InstanceId;

            var result = ItemTransfer.Move(source, destination, id, 1, catalogue);

            Assert.That(result.Failure, Is.EqualTo(TransferFailure.DestinationFull));
            Assert.That(result.Moved, Is.EqualTo(0));
            Assert.That(source.Find(id), Is.Not.Null);
            Assert.That(destination.Count, Is.EqualTo(1));
        }

        [Test]
        public void Move_Stack_MovesWhatFits_AndReportsTheLeftoverAtTheSource()
        {
            var source = new ItemInventory(0);
            var destination = new ItemInventory(1);
            source.Add(medkit, 3);
            source.Add(medkit, 3);   // two stacks of 3
            destination.Add(medkit, 2);   // one stack with 1 free
            var id = source.Entries[0].InstanceId;

            var result = ItemTransfer.Move(source, destination, id, 3, catalogue);

            Assert.That(result.Moved, Is.EqualTo(1));
            Assert.That(result.Remaining, Is.EqualTo(2));
            Assert.That(result.IsPartial, Is.True);
            Assert.That(result.Failure, Is.EqualTo(TransferFailure.DestinationFull));
            Assert.That(destination.CountOf("medkit"), Is.EqualTo(3));
            Assert.That(source.CountOf("medkit"), Is.EqualTo(5), "total units are conserved");
        }

        [Test]
        public void Move_ConservesTotals_AcrossARunOfRandomishMoves()
        {
            var a = new ItemInventory(3);
            var b = new ItemInventory(3);
            a.Add(medkit, 3);
            a.Add(medkit, 3);
            a.Add(rifle, 1);
            var total = a.CountOf("medkit") + b.CountOf("medkit");
            var rifles = 1;

            for (var i = 0; i < 20; i++)
            {
                var from = i % 2 == 0 ? a : b;
                var to = i % 2 == 0 ? b : a;
                if (from.Count == 0) continue;
                var entry = from.Entries[i % from.Count];
                ItemTransfer.Move(from, to, entry.InstanceId, 1 + i % 3, catalogue);
            }

            Assert.That(a.CountOf("medkit") + b.CountOf("medkit"), Is.EqualTo(total));
            Assert.That(a.CountOf("rifle") + b.CountOf("rifle"), Is.EqualTo(rifles));
        }

        [Test]
        public void Move_RefusesBadInput_WithoutChangingAnything()
        {
            var source = new ItemInventory(0);
            var destination = new ItemInventory(0);
            source.Add(medkit, 2);
            var id = source.Entries[0].InstanceId;

            Assert.That(ItemTransfer.Move(source, destination, id, 0, catalogue).Failure, Is.EqualTo(TransferFailure.InvalidQuantity));
            Assert.That(ItemTransfer.Move(source, destination, "missing", 1, catalogue).Failure, Is.EqualTo(TransferFailure.NotFound));
            Assert.That(ItemTransfer.Move(source, source, id, 1, catalogue).Failure, Is.EqualTo(TransferFailure.SameContainer));
            var unknown = ItemCatalogue.Create();
            made.Add(unknown);
            Assert.That(ItemTransfer.Move(source, destination, id, 1, unknown).Failure, Is.EqualTo(TransferFailure.UnknownItem));
            Assert.That(source.CountOf("medkit"), Is.EqualTo(2));
            Assert.That(destination.Count, Is.EqualTo(0));
        }

        [Test]
        public void Move_TheSameEntryTwice_SecondCallFindsNothing()
        {
            var source = new ItemInventory(0);
            var one = new ItemInventory(0);
            var two = new ItemInventory(0);
            source.Add(rifle, 1);
            var id = source.Entries[0].InstanceId;

            var first = ItemTransfer.Move(source, one, id, 1, catalogue);
            var second = ItemTransfer.Move(source, two, id, 1, catalogue);

            Assert.That(first.Moved, Is.EqualTo(1));
            Assert.That(second.Failure, Is.EqualTo(TransferFailure.NotFound));
            Assert.That(one.CountOf("rifle") + two.CountOf("rifle"), Is.EqualTo(1));
        }

        [Test]
        public void Move_NonStackableToAFullDestination_ReportsTheWholeEntryAsRemaining()
        {
            var source = new ItemInventory(0);
            var destination = new ItemInventory(1);
            source.Add(rifle, 1);
            destination.Add(vest, 1);

            var result = ItemTransfer.Move(source, destination, source.Entries[0].InstanceId, 1, catalogue);

            Assert.That(result.Failure, Is.EqualTo(TransferFailure.DestinationFull));
            Assert.That(result.Moved, Is.EqualTo(0));
            Assert.That(result.Remaining, Is.EqualTo(1));
        }

        [Test]
        public void Move_StackToAFullDestination_ReportsTheWholeStackAsRemaining()
        {
            var source = new ItemInventory(0);
            var destination = new ItemInventory(1);
            source.Add(medkit, 3);
            destination.Add(vest, 1);

            var result = ItemTransfer.Move(source, destination, source.Entries[0].InstanceId, 2, catalogue);

            Assert.That(result.Failure, Is.EqualTo(TransferFailure.DestinationFull));
            Assert.That(result.Moved, Is.EqualTo(0));
            Assert.That(result.Remaining, Is.EqualTo(3));
        }

        [Test]
        public void Move_NotFound_ReportsNothingRemaining()
        {
            var result = ItemTransfer.Move(new ItemInventory(0), new ItemInventory(0), "missing", 1, catalogue);

            Assert.That(result.Failure, Is.EqualTo(TransferFailure.NotFound));
            Assert.That(result.Remaining, Is.EqualTo(0));
        }

        [Test]
        public void Describe_ExplainsAPartialMove()
        {
            var text = new TransferResult(3, 1, 2, TransferFailure.DestinationFull).Describe("Medkit");

            StringAssert.Contains("1", text);
            StringAssert.Contains("Medkit", text);
            StringAssert.Contains("2 left", text);
        }
    }
}
