using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>Equipping straight from the stash, and equipping from the bag during a mission (the working state only).</summary>
    public class InventoryEquipFlowTests
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
            vest = Track(ItemDefinition.Create("vest", "Vest", ItemCategory.Armor, ItemSlot.Armor));
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

        // Operative "a" carries a rifle and two medkits; the stash holds a marksman rifle, a vest and a medkit.
        InventorySession NewSession()
        {
            var session = new InventorySession(catalogue, 8);
            session.EnsureOperative("a");
            var bag = session.Session.Loadout("a").Bag;
            bag.Add(rifle, 1);
            bag.Add(medkit, 2);
            session.Session.Stash.Add(marksman, 1);
            session.Session.Stash.Add(vest, 1);
            session.Session.Stash.Add(medkit, 1);
            return session;
        }

        static string StashId(InventorySession s, string definitionId) =>
            s.Session.Stash.Entries.First(e => e.DefinitionId == definitionId).InstanceId;

        [Test]
        public void EquipFromStash_MovesTheItemToTheBag_AndEquipsIt_InOneStep()
        {
            var session = NewSession();
            var id = StashId(session, "marksman");

            var result = session.EquipFromStash("a", id);

            Assert.That(result.Ok, Is.True, result.Reason);
            var loadout = session.Session.Loadout("a");
            Assert.That(session.Session.Stash.Find(id), Is.Null, "left the stash");
            Assert.That(loadout.Bag.Find(id), Is.Not.Null, "is in the bag");
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(id));
            Assert.That(session.EquippedFor(session.Session, "a").Weapon == marksman, Is.True);
        }

        [Test]
        public void EquipFromStash_RaisesEquipmentChanged_ForTheOperative()
        {
            var session = NewSession();
            var heard = new List<string>();
            session.EquipmentChanged += heard.Add;

            session.EquipFromStash("a", StashId(session, "marksman"));
            session.EquipFromStash("a", "no-such-entry");

            Assert.That(heard, Is.EqualTo(new[] { "a" }), "once, for the success only");
        }

        [Test]
        public void EquipFromStash_LeavesAReplacedItemInTheBag()
        {
            var session = NewSession();
            var loadout = session.Session.Loadout("a");
            var rifleId = loadout.Bag.Entries.First(e => e.DefinitionId == "rifle").InstanceId;
            Assert.That(session.Equip("a", rifleId).Ok, Is.True);

            Assert.That(session.EquipFromStash("a", StashId(session, "marksman")).Ok, Is.True);

            Assert.That(loadout.Bag.Find(rifleId), Is.Not.Null, "the rifle is still carried, unequipped");
            Assert.That(loadout.Equipment.Contains(rifleId), Is.False);
        }

        [Test]
        public void EquipFromStash_WithAFullBag_ChangesNothing()
        {
            var session = NewSession();
            var loadout = session.Session.Loadout("a");
            while (loadout.Bag.Count < 8)
                loadout.Bag.Add(vest, 1);
            var id = StashId(session, "marksman");

            var result = session.EquipFromStash("a", id);

            Assert.That(result.Ok, Is.False);
            StringAssert.Contains("full", result.Reason);
            Assert.That(session.Session.Stash.Find(id), Is.Not.Null, "still in the stash");
            Assert.That(loadout.Bag.Find(id), Is.Null);
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.Null.Or.Empty);
        }

        [Test]
        public void EquipFromStash_RefusesAnItemWithNoSlot_AndAMissingOne()
        {
            var session = NewSession();
            var medkitId = StashId(session, "medkit");

            var noSlot = session.EquipFromStash("a", medkitId);
            var missing = session.EquipFromStash("a", "no-such-entry");
            var unknownOperative = session.EquipFromStash("zz", StashId(session, "vest"));

            Assert.That(noSlot.Ok, Is.False);
            Assert.That(missing.Ok, Is.False);
            Assert.That(unknownOperative.Ok, Is.False);
            Assert.That(session.Session.Stash.Find(medkitId), Is.Not.Null, "nothing left the stash");
            Assert.That(session.Session.Stash.Count, Is.EqualTo(3));
        }

        [Test]
        public void EquipFromStash_IsLockedDuringAMission()
        {
            var session = NewSession();
            session.BeginMission("m1");

            var result = session.EquipFromStash("a", StashId(session, "marksman"));

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Reason, Is.EqualTo(InventorySession.LockedReason));
            Assert.That(session.Session.Stash.CountOf("marksman"), Is.EqualTo(1));
        }

        [Test]
        public void EquipInMission_ChangesOnlyTheWorkingState_AndRaisesEquipmentChanged()
        {
            var session = NewSession();
            var rifleId = session.Session.Loadout("a").Bag.Entries.First(e => e.DefinitionId == "rifle").InstanceId;
            session.Session.Loadout("a").Bag.Add(vest, 1);
            session.BeginMission("m1");
            var heard = new List<string>();
            session.EquipmentChanged += heard.Add;

            var result = session.EquipInMission("a", rifleId);

            Assert.That(result.Ok, Is.True, result.Reason);
            Assert.That(session.Working.Loadout("a").Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(rifleId));
            Assert.That(session.Session.Loadout("a").Equipment.InstanceIdIn(ItemSlot.Weapon), Is.Null.Or.Empty, "the session waits for a success");
            Assert.That(session.EquippedFor(session.Active, "a").Weapon == rifle, Is.True);
            Assert.That(heard, Is.EqualTo(new[] { "a" }));
        }

        [Test]
        public void EquipInMission_WorksOnlyOnItemsInTheOperativesOwnBag_AndOnlyInAMission()
        {
            var session = NewSession();
            var rifleId = session.Session.Loadout("a").Bag.Entries.First(e => e.DefinitionId == "rifle").InstanceId;

            Assert.That(session.EquipInMission("a", rifleId).Ok, Is.False, "no mission is running");

            session.BeginMission("m1");
            Assert.That(session.EquipInMission("a", StashId(session, "marksman")).Ok, Is.False, "a stash item is not in the bag");
            Assert.That(session.EquipInMission("a", "no-such-entry").Ok, Is.False);
            Assert.That(session.EquipInMission("zz", rifleId).Ok, Is.False);
            Assert.That(session.Working.Loadout("a").Equipment.InstanceIdIn(ItemSlot.Weapon), Is.Null.Or.Empty);
        }

        [Test]
        public void ASwapDuringAMission_IsDiscardedWithAFailedMission_AndKeptWithASuccess()
        {
            var session = NewSession();
            var rifleId = session.Session.Loadout("a").Bag.Entries.First(e => e.DefinitionId == "rifle").InstanceId;
            session.BeginMission("lost");
            session.EquipInMission("a", rifleId);
            session.Settle("lost", success: false);
            Assert.That(session.Session.Loadout("a").Equipment.InstanceIdIn(ItemSlot.Weapon), Is.Null.Or.Empty);

            session.BeginMission("won");
            session.EquipInMission("a", rifleId);
            session.Settle("won", success: true);
            Assert.That(session.Session.Loadout("a").Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(rifleId));
        }
    }
}
