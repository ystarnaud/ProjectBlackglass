using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class InventorySessionTests
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

        InventorySession NewSession()
        {
            var session = new InventorySession(catalogue, 8);
            session.EnsureOperative("a");
            session.EnsureOperative("b");
            session.Session.Loadout("a").Bag.Add(rifle, 1);
            session.Session.Loadout("a").Bag.Add(medkit, 2);
            session.Session.Stash.Add(marksman, 1);
            return session;
        }

        [Test]
        public void Equip_AndStashTransfers_WorkInLoadoutMode_AndRaiseChanged()
        {
            var session = NewSession();
            var changes = 0;
            session.Changed += () => changes++;
            var rifleId = session.Session.Loadout("a").Bag.Entries[0].InstanceId;

            Assert.That(session.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(session.Equip("a", rifleId).Ok, Is.True);
            Assert.That(session.EquippedFor(session.Session, "a").Weapon == rifle, Is.True);
            Assert.That(session.ToStash("a", rifleId, 1).Failure, Is.EqualTo(TransferFailure.Equipped));
            Assert.That(session.Unequip("a", ItemSlot.Weapon).Ok, Is.True);
            Assert.That(session.ToStash("a", rifleId, 1).Moved, Is.EqualTo(1));
            Assert.That(changes, Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void Edits_AreRefusedWhileAMissionRuns_AndStateIsUnchanged()
        {
            var session = NewSession();
            session.Session.Loadout("a").Bag.Add(vest, 1);
            var bag = session.Session.Loadout("a").Bag;
            var rifleId = bag.Entries[0].InstanceId;
            var medkitId = bag.Entries[1].InstanceId;
            var vestId = bag.Entries[2].InstanceId;
            Assert.That(session.Equip("a", rifleId).Ok, Is.True);
            session.BeginMission("m1");

            // Each call would succeed outside a mission: equip the vest, unequip the rifle, stash the medkit, take the marksman rifle.
            var equip = session.Equip("a", vestId);
            var unequip = session.Unequip("a", ItemSlot.Weapon);
            var move = session.ToStash("a", medkitId, 1);
            var back = session.FromStash("a", session.Session.Stash.Entries[0].InstanceId, 1);

            Assert.That(session.Mode, Is.EqualTo(InventoryMode.InMission));
            Assert.That(equip.Ok, Is.False);
            Assert.That(equip.Reason, Is.EqualTo(InventorySession.LockedReason));
            Assert.That(unequip.Ok, Is.False);
            Assert.That(unequip.Reason, Is.EqualTo(InventorySession.LockedReason));
            Assert.That(move.Failure, Is.EqualTo(TransferFailure.Locked));
            Assert.That(back.Failure, Is.EqualTo(TransferFailure.Locked));
            Assert.That(session.Session.Loadout("a").Bag.Count, Is.EqualTo(3));
            Assert.That(session.Session.Stash.CountOf("marksman"), Is.EqualTo(1));
            Assert.That(session.Session.Loadout("a").Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo(rifleId));
            Assert.That(session.Session.Loadout("a").Equipment.InstanceIdIn(ItemSlot.Armor), Is.Null.Or.Empty);
            Assert.That(session.EquippedFor(session.Session, "a").Weapon == rifle, Is.True);
        }

        [Test]
        public void Working_IsADeepCopy_PickupsDoNotTouchTheSessionBeforeSettlement()
        {
            var session = NewSession();
            session.BeginMission("m1");

            session.Working.Loadout("b").Bag.Add(vest, 1);
            session.Working.Stash.Add(medkit, 1);
            session.Working.Loadout("a").Bag.Remove(session.Working.Loadout("a").Bag.Entries[1].InstanceId, 2);

            Assert.That(session.Session.Loadout("b").Bag.Count, Is.EqualTo(0));
            Assert.That(session.Session.Stash.CountOf("medkit"), Is.EqualTo(0));
            Assert.That(session.Session.Loadout("a").Bag.CountOf("medkit"), Is.EqualTo(2));
            Assert.That(session.Session, Is.Not.SameAs(session.Working));
        }

        [Test]
        public void Settle_Success_CommitsTheWorkingStateExactlyOnce()
        {
            var session = NewSession();
            session.BeginMission("m1");
            session.Working.Loadout("b").Bag.Add(vest, 1);

            Assert.That(session.Settle("m1", true), Is.True);
            Assert.That(session.Session.Loadout("b").Bag.CountOf("vest"), Is.EqualTo(1));
            Assert.That(session.Mode, Is.EqualTo(InventoryMode.Loadout));
            Assert.That(session.Working, Is.Null);

            // A duplicate callback, and a later failure for the same id, change nothing.
            Assert.That(session.Settle("m1", true), Is.False);
            Assert.That(session.Settle("m1", false), Is.False);
            Assert.That(session.Session.Loadout("b").Bag.CountOf("vest"), Is.EqualTo(1));
        }

        [Test]
        public void Settle_Failure_OrAbort_DiscardsTheWorkingState()
        {
            var session = NewSession();
            session.BeginMission("m1");
            session.Working.Loadout("b").Bag.Add(vest, 1);
            Assert.That(session.Settle("m1", false), Is.False);
            Assert.That(session.Session.Loadout("b").Bag.Count, Is.EqualTo(0));

            session.BeginMission("m2");
            session.Working.Loadout("b").Bag.Add(vest, 1);
            session.AbortMission();
            Assert.That(session.Session.Loadout("b").Bag.Count, Is.EqualTo(0));
            Assert.That(session.Mode, Is.EqualTo(InventoryMode.Loadout));
        }

        [Test]
        public void Settle_WithAnotherMissionsId_IsIgnored()
        {
            var session = NewSession();
            session.BeginMission("m2");
            session.Working.Loadout("b").Bag.Add(vest, 1);

            Assert.That(session.Settle("m1", true), Is.False);
            Assert.That(session.Mode, Is.EqualTo(InventoryMode.InMission));
            Assert.That(session.Session.Loadout("b").Bag.Count, Is.EqualTo(0));
        }

        [Test]
        public void BeginMission_WhileOneRuns_DiscardsTheOldWorkingStateAndStartsFresh()
        {
            var session = NewSession();
            session.BeginMission("m1");
            session.Working.Loadout("b").Bag.Add(vest, 1);

            session.BeginMission("m2");

            Assert.That(session.ActiveMissionId, Is.EqualTo("m2"));
            Assert.That(session.Working.Loadout("b").Bag.Count, Is.EqualTo(0));
            Assert.That(session.Settle("m1", true), Is.False);
        }

        [Test]
        public void UnsecuredCount_IsWhatTheMissionAddedBeyondTheStart()
        {
            var session = NewSession();
            session.BeginMission("m1");
            session.Working.Loadout("a").Bag.Add(medkit, 1);
            session.Working.Loadout("a").Bag.Add(vest, 1);

            Assert.That(session.UnsecuredCount("a", "medkit"), Is.EqualTo(1));
            Assert.That(session.UnsecuredCount("a", "vest"), Is.EqualTo(1));
            Assert.That(session.UnsecuredCount("a", "rifle"), Is.EqualTo(0));
            Assert.That(session.UnsecuredCount("b", "vest"), Is.EqualTo(0));
        }

        [Test]
        public void UsingAConsumable_InTheMission_DoesNotLowerUnsecuredBelowZero()
        {
            var session = NewSession();
            session.BeginMission("m1");
            session.Working.Loadout("a").Bag.Remove(session.Working.Loadout("a").Bag.Entries[1].InstanceId, 1);

            Assert.That(session.UnsecuredCount("a", "medkit"), Is.EqualTo(0));
        }

        [Test]
        public void SeedStarter_GrantsAndEquipsOnce_AndNeverRegrants()
        {
            var session = new InventorySession(catalogue, 8);
            var starter = StarterLoadout.Create(
                new Dictionary<string, StarterGrant[]>
                {
                    ["a"] = new[]
                    {
                        new StarterGrant { item = rifle, quantity = 1, equip = true },
                        new StarterGrant { item = medkit, quantity = 2, equip = false },
                    },
                },
                new[] { new StarterGrant { item = vest, quantity = 1, equip = false } });
            made.Add(starter);

            var problems = new List<string>();
            session.SeedStarter(starter, new[] { "a", "b" }, problems);
            var bagAfterFirst = session.Session.Loadout("a").Bag.Count;
            session.SeedStarter(starter, new[] { "a", "b" }, problems);

            Assert.That(problems, Is.Empty);
            Assert.That(session.Session.Loadout("a").Bag.Count, Is.EqualTo(bagAfterFirst));
            Assert.That(session.Session.Loadout("a").Bag.CountOf("rifle"), Is.EqualTo(1));
            Assert.That(session.EquippedFor(session.Session, "a").Weapon == rifle, Is.True);
            Assert.That(session.Session.Loadout("b"), Is.Not.Null);
            Assert.That(session.Session.Stash.CountOf("vest"), Is.EqualTo(1));
        }

        [Test]
        public void SeedStarter_ReportsAGrantThatDoesNotFit_WithoutThrowing()
        {
            var session = new InventorySession(catalogue, 1);
            var starter = StarterLoadout.Create(
                new Dictionary<string, StarterGrant[]>
                {
                    ["a"] = new[]
                    {
                        new StarterGrant { item = rifle, quantity = 1, equip = true },
                        new StarterGrant { item = vest, quantity = 1, equip = false },
                    },
                },
                new StarterGrant[0]);
            made.Add(starter);

            var problems = new List<string>();
            session.SeedStarter(starter, new[] { "a" }, problems);

            Assert.That(problems, Has.Count.EqualTo(1));
            StringAssert.Contains("vest", problems[0].ToLowerInvariant());
        }

        [Test]
        public void SquadInventory_SettlesOnTheDirectorsMissionFinished_UsingTheGivenInstanceId()
        {
            var host = new GameObject("inv");
            made.Add(host);
            var inventory = host.AddComponent<SquadInventory>();
            var current = "m1";
            inventory.Initialize(catalogue, null, null, 8, () => current);
            inventory.Core.EnsureOperative("a");
            inventory.Core.BeginMission("m1");
            inventory.Core.Working.Loadout("a").Bag.Add(vest, 1);

            inventory.HandleMissionFinished(MissionPhase.Success);
            inventory.HandleMissionFinished(MissionPhase.Success);

            Assert.That(inventory.Core.Session.Loadout("a").Bag.CountOf("vest"), Is.EqualTo(1));
            Assert.That(inventory.Core.Mode, Is.EqualTo(InventoryMode.Loadout));
        }
    }
}
