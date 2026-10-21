using System.Collections.Generic;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 12 Task 5: the server's inventory starts from the admission snapshot, never a local starter loadout. Owned
    /// non-stackables keep the backend item id as their instance id, stacks split by maxStack with local ids, equipment is set
    /// by item id, and a mission finish settles nothing.
    /// </summary>
    public sealed class SnapshotInventoryTests
    {
        readonly List<Object> made = new List<Object>();
        ItemDefinition medkit, rifle, vest;
        ItemCatalogue catalogue;

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [SetUp]
        public void SetUp()
        {
            var archetype = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            medkit = Track(ItemDefinition.Create("item.medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, 3, default, null, 40));
            rifle = Track(ItemDefinition.Create("item.service-rifle", "Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: archetype));
            vest = Track(ItemDefinition.Create("item.light-vest", "Vest", ItemCategory.Armor, ItemSlot.Armor));
            catalogue = Track(ItemCatalogue.Create(medkit, rifle, vest));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        static OperativeSnapshot Operative(string id, int slot, int medkits, string weapon = "2k", string armor = null, params SnapshotItem[] extra)
        {
            var items = new List<SnapshotItem> { new SnapshotItem("2k", "item.service-rifle"), new SnapshotItem("2n", "item.light-vest") };
            items.AddRange(extra);
            var stacks = medkits > 0 ? new[] { new SnapshotStack("item.medkit", medkits) } : new SnapshotStack[0];
            return new OperativeSnapshot(id, slot, items, stacks, weapon, armor, null);
        }

        [Test]
        public void Owned_items_keep_their_backend_ids_and_equipment_is_set_by_item_id()
        {
            var session = InventorySession.FromSnapshot(catalogue, 8, new[] { Operative("C", 0, 2, armor: "2n") }, out var problem);
            Assert.That(problem, Is.Null);
            var loadout = session.Session.Loadout("C");
            Assert.That(loadout.Bag.Find("2k").DefinitionId, Is.EqualTo("item.service-rifle"));
            Assert.That(loadout.Bag.Find("2n").DefinitionId, Is.EqualTo("item.light-vest"));
            Assert.That(loadout.Bag.CountOf("item.medkit"), Is.EqualTo(2));
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), Is.EqualTo("2k"));
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Armor), Is.EqualTo("2n"));
            Assert.That(loadout.Equipment.InstanceIdIn(ItemSlot.Utility), Is.Empty);
            Assert.That(loadout.Bag.Capacity, Is.EqualTo(8));
            Assert.That(session.Session.Stash.Count, Is.EqualTo(0), "the server has no stash");
        }

        [Test]
        public void A_stack_over_max_stack_splits_into_entries_that_sum_to_the_snapshot()
        {
            var session = InventorySession.FromSnapshot(catalogue, 8, new[] { Operative("C", 0, 7) }, out var problem);
            Assert.That(problem, Is.Null);
            var stacks = session.Session.Loadout("C").Bag.Entries.Where(e => e.DefinitionId == "item.medkit").ToList();
            Assert.That(stacks.Select(e => e.Quantity), Is.EqualTo(new[] { 3, 3, 1 }));
            Assert.That(stacks.Select(e => e.InstanceId).Distinct().Count(), Is.EqualTo(3), "local ids");
            Assert.That(stacks.Any(e => e.InstanceId == "2k" || e.InstanceId == "2n"), Is.False);
        }

        [Test]
        public void A_snapshot_that_does_not_fit_the_bag_is_bag_overflow()
        {
            Assert.That(InventorySession.FromSnapshot(catalogue, 2, new[] { Operative("C", 0, 1) }, out var problem), Is.Null);
            Assert.That(problem, Is.EqualTo("bag_overflow"));
            Assert.That(InventorySession.FromSnapshot(catalogue, 3, new[] { Operative("C", 0, 4) }, out problem), Is.Null, "4 medkits need two entries");
            Assert.That(problem, Is.EqualTo("bag_overflow"));
        }

        [Test]
        public void An_unknown_item_or_an_unequippable_slot_is_refused()
        {
            Assert.That(InventorySession.FromSnapshot(catalogue, 8, new[] { Operative("C", 0, 0, extra: new SnapshotItem("2p", "item.unknown")) }, out var problem), Is.Null);
            Assert.That(problem, Is.EqualTo("unknown_item"));
            var bad = new OperativeSnapshot("C", 0, new[] { new SnapshotItem("2k", "item.service-rifle") }, new SnapshotStack[0], "2z", null, null);
            Assert.That(InventorySession.FromSnapshot(catalogue, 8, new[] { bad }, out problem), Is.Null);
            Assert.That(problem, Is.EqualTo("bad_equipment"));
        }

        [Test]
        public void A_snapshot_session_never_grants_starter_items()
        {
            var starter = Track(StarterLoadout.Create(new Dictionary<string, StarterGrant[]> { { "C", new[] { new StarterGrant { item = medkit, quantity = 2 } } }, { "D", new[] { new StarterGrant { item = rifle, quantity = 1 } } } }, new[] { new StarterGrant { item = vest, quantity = 1 } }));
            var session = InventorySession.FromSnapshot(catalogue, 8, new[] { Operative("C", 0, 1) }, out _);
            session.SeedStarter(starter, new[] { "C", "D" }, new List<string>());
            Assert.That(session.Session.Loadout("C").Bag.CountOf("item.medkit"), Is.EqualTo(1));
            Assert.That(session.Session.Loadout("D"), Is.Null);
            Assert.That(session.Session.Stash.Count, Is.EqualTo(0));
        }

        SquadInventory SnapshotSquad(IReadOnlyList<OperativeSnapshot> snapshot, out string problem)
        {
            var go = Track(new GameObject("Squad"));
            go.SetActive(false);
            var squad = go.AddComponent<SquadInventory>();
            var ok = squad.InitializeFromSnapshot(catalogue, null, 8, snapshot, out problem);
            go.SetActive(true);
            Assert.That(ok, Is.EqualTo(problem == null));
            return squad;
        }

        [Test]
        public void The_squad_inventory_in_snapshot_mode_begins_the_attempt_and_settles_nothing_at_the_end()
        {
            var squad = SnapshotSquad(new[] { Operative("C", 0, 2), Operative2() }, out var problem);
            Assert.That(problem, Is.Null);
            Assert.That(squad.SnapshotMode, Is.True);
            squad.BeginMission(AttemptTestData.AttemptId);
            Assert.That(squad.Core.ActiveMissionId, Is.EqualTo(AttemptTestData.AttemptId));
            var working = squad.WorkingLoadout("C");
            Assert.That(working.Bag.Remove(working.Bag.Entries.First(e => e.DefinitionId == "item.medkit").InstanceId, 1), Is.EqualTo(1));
            squad.HandleMissionFinished(MissionPhase.Success);
            Assert.That(squad.Core.Working, Is.Not.Null, "the server never settles: the backend does");
            Assert.That(squad.Core.Session.Loadout("C").Bag.CountOf("item.medkit"), Is.EqualTo(2), "the session is the admission state");
        }

        static OperativeSnapshot Operative2() =>
            new OperativeSnapshot("D", 1, new[] { new SnapshotItem("2m", "item.service-rifle") }, new SnapshotStack[0], "2m", null, null);

        [Test]
        public void The_working_state_matches_the_ledger_at_the_start()
        {
            var snapshot = new[] { Operative("C", 0, 5, armor: "2n"), Operative2() };
            var squad = SnapshotSquad(snapshot, out _);
            squad.BeginMission(AttemptTestData.AttemptId);
            var ledger = new MissionItemLedger(ItemLedgerMapping.Catalogue(catalogue));
            ledger.AddParticipant("JG", snapshot);
            foreach (var o in snapshot)
            {
                var state = AttemptRuntime.OperativeState(squad, o.OperativeId);
                var check = ledger.VerifyOperative(o.OperativeId, state.Bag, state.Equipment);
                Assert.That(check.Ok, Is.True, o.OperativeId + ": " + string.Join(",", check.Codes));
            }
        }
    }
}
