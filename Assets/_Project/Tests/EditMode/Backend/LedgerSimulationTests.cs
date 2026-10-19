using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>
    /// The ledger against Unity's real inventory: stacking, merging, splitting at the maximum stack, partial moves at bag
    /// capacity and entry-id behaviour all agree with the ledger's model after every mutation.
    /// </summary>
    public sealed class LedgerSimulationTests
    {
        const string Rifle = MissionItemLedgerTests.Rifle, Vest = MissionItemLedgerTests.Vest, Boots = MissionItemLedgerTests.Boots,
            Kit = MissionItemLedgerTests.Kit, Charm = MissionItemLedgerTests.Charm;

        readonly List<Object> made = new List<Object>();
        ItemCatalogue catalogue;
        LedgerCatalogue ledgerCatalogue;

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [SetUp]
        public void SetUp()
        {
            catalogue = Track(ItemCatalogue.Create(
                Track(ItemDefinition.Create(Rifle, "Rifle", ItemCategory.Weapon, ItemSlot.Weapon)),
                Track(ItemDefinition.Create(Vest, "Vest", ItemCategory.Armor, ItemSlot.Armor)),
                Track(ItemDefinition.Create(Boots, "Boots", ItemCategory.Utility, ItemSlot.Utility)),
                Track(ItemDefinition.Create(Kit, "Kit", ItemCategory.Consumable, ItemSlot.None, maxStack: 3, healAmount: 10)),
                Track(ItemDefinition.Create(Charm, "Charm", ItemCategory.Utility, ItemSlot.None))));
            ledgerCatalogue = MissionItemLedgerTests.TestCatalogue();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        static OperativeSnapshot Op(string id, int slot, (string, string)[] items, int kits, string weapon = null, string armor = null, string utility = null) =>
            new OperativeSnapshot(id, slot, items.Select(i => new SnapshotItem(i.Item1, i.Item2)).ToList(),
                kits > 0 ? new[] { new SnapshotStack(Kit, kits) } : new SnapshotStack[0], weapon, armor, utility);

        LedgerSimulation Sim(int capacity, params OperativeSnapshot[] operatives) => new LedgerSimulation(catalogue, ledgerCatalogue, "JG", operatives, capacity);

        [Test]
        public void A_picked_medkit_merges_into_an_owned_stack()
        {
            var sim = Sim(6, Op("C", 0, new (string, string)[0], 2));
            sim.FillContainer(0, new[] { (Kit, 1) });
            Assert.That(sim.Collect("C", 0, 0, 1).Moved, Is.EqualTo(1));
            Assert.That(sim.BagOf("C").Entries.Select(e => e.Quantity), Is.EqualTo(new[] { 3 }), "one runtime entry of 3");
            sim.AssertConsistent();
            Assert.That(sim.Use("C", Kit), Is.True);
            sim.AssertConsistent();
            var c = sim.Freeze().Operatives.Single();
            Assert.That(c.ConsumedLoot.Single().Quantity, Is.EqualTo(1), "the loot unit went first");
            Assert.That(c.HeldStacks.Single().Quantity, Is.EqualTo(2));
            Assert.That(c.HeldLoot, Is.Empty);
        }

        [Test]
        public void Splitting_at_max_stack_is_tracked()
        {
            var sim = Sim(6, Op("C", 0, new (string, string)[0], 2));
            sim.FillContainer(0, new[] { (Kit, 3) });
            Assert.That(sim.Collect("C", 0, 0, 3).Moved, Is.EqualTo(3));
            Assert.That(sim.BagOf("C").Entries.Select(e => e.Quantity), Is.EqualTo(new[] { 3, 2 }));
            sim.AssertConsistent();
            var c = sim.Freeze().Operatives.Single();
            Assert.That(c.HeldStacks.Single().Quantity + c.HeldLoot.Single().Quantity, Is.EqualTo(5));
        }

        [Test]
        public void A_full_bag_takes_part_of_a_stack_line()
        {
            var sim = Sim(2, Op("C", 0, new[] { ("2k", Rifle) }, 2, weapon: "2k"));
            sim.FillContainer(0, new[] { (Kit, 3) });
            var entryId = sim.Container(0).Entries[0].InstanceId;
            var first = sim.Collect("C", 0, 0, 3);
            Assert.That(first.IsPartial, Is.True);
            Assert.That(first.Moved, Is.EqualTo(1));
            Assert.That(sim.Container(0).Entries.Single().InstanceId, Is.EqualTo(entryId), "the remainder keeps its entry id");
            Assert.That(sim.Container(0).Entries.Single().Quantity, Is.EqualTo(2));
            sim.AssertConsistent();
            Assert.That(sim.Use("C", Kit) && sim.Use("C", Kit), Is.True);
            sim.AssertConsistent();
            Assert.That(sim.Collect("C", 0, 0, 2).Moved, Is.EqualTo(2), "the rest, after the uses made room");
            Assert.That(sim.Container(0).Entries, Is.Empty);
            sim.AssertConsistent();
            var c = sim.Freeze().Operatives.Single();
            Assert.That(c.Collected.Single().Quantity, Is.EqualTo(3));
            Assert.That(c.ConsumedLoot.Single().Quantity, Is.EqualTo(1));
            Assert.That(c.ConsumedOwned.Single().Quantity, Is.EqualTo(1));
        }

        [Test]
        public void A_non_stackable_keeps_its_entry_id_through_collect_and_equip()
        {
            var sim = Sim(6, Op("C", 0, new[] { ("2k", Rifle) }, 0, weapon: "2k"));
            sim.FillContainer(0, new[] { (Rifle, 1), (Boots, 1) });
            var rifleId = sim.Container(0).Entries[0].InstanceId;
            Assert.That(sim.Collect("C", 0, 0, 1).Moved, Is.EqualTo(1));
            Assert.That(sim.BagOf("C").Find(rifleId), Is.Not.Null);
            Assert.That(sim.Equip("C", rifleId), Is.True);
            sim.AssertConsistent();
            Assert.That(sim.Equip("C", "2k"), Is.True, "and back");
            Assert.That(sim.Equip("C", rifleId), Is.True);
            sim.AssertConsistent();
            var weapon = sim.Freeze().Operatives.Single().Equipment.Single();
            Assert.That((weapon.ContainerIndex, weapon.Line), Is.EqualTo(((int?)0, (int?)0)));
        }

        [Test]
        public void Two_lines_of_one_definition_are_used_oldest_first()
        {
            var sim = Sim(6, Op("C", 0, new (string, string)[0], 0));
            sim.FillContainer(0, new[] { (Kit, 2) });
            sim.FillContainer(1, new[] { (Kit, 2) });
            sim.Collect("C", 1, 0, 2);
            sim.Collect("C", 0, 0, 2);
            for (var i = 0; i < 3; i++) Assert.That(sim.Use("C", Kit), Is.True);
            sim.AssertConsistent();
            var c = sim.Freeze().Operatives.Single();
            Assert.That(c.ConsumedLoot.Select(x => $"{x.ContainerIndex}={x.Quantity}"), Is.EquivalentTo(new[] { "1=2", "0=1" }));
        }

        [Test]
        public void A_rogue_bag_change_is_caught()
        {
            var sim = Sim(6, Op("C", 0, new (string, string)[0], 1));
            sim.RogueAdd("C", Kit, 1);
            var check = sim.Ledger.VerifyOperative("C", sim.OperativeStates()[0].Bag, sim.OperativeStates()[0].Equipment);
            Assert.That(check.Codes, Is.EqualTo(new[] { "bag_count_mismatch" }));
            var facts = sim.Freeze();
            Assert.That(facts.Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));
        }

        static readonly string[] Definitions = { Rifle, Vest, Boots, Kit, Charm };

        [Test]
        public void Seeded_random_missions_stay_consistent_and_conserve()
        {
            var clock = Stopwatch.StartNew();
            var actions = 0;
            for (var seed = 1; seed <= 200; seed++)
            {
                var random = new Random(seed);
                var nextItem = 100L;
                var snapshot = new List<OperativeSnapshot>();
                var capacity = 3 + random.Next(6);
                foreach (var slot in Enumerable.Range(0, 1 + random.Next(3)))
                {
                    var items = new List<(string, string)>();
                    string weapon = null, armor = null, utility = null;
                    foreach (var definition in new[] { Rifle, Vest, Boots, Charm })
                    {
                        if (random.Next(2) == 0) continue;
                        var id = Base58Id.Format(nextItem++);
                        items.Add((id, definition));
                        if (random.Next(2) == 0) continue;
                        if (definition == Rifle) weapon = id; else if (definition == Vest) armor = id; else if (definition == Boots) utility = id;
                    }
                    var kits = random.Next(4);
                    capacity = Math.Max(capacity, items.Count + (kits > 0 ? 1 : 0));
                    snapshot.Add(Op(Base58Id.Format(10 + slot), slot, items.ToArray(), kits, weapon, armor, utility));
                }
                var sim = Sim(capacity, snapshot.ToArray());
                foreach (var index in Enumerable.Range(0, 1 + random.Next(4)))
                    sim.FillContainer(index, Enumerable.Range(0, 1 + random.Next(4))
                        .Select(_ => Definitions[random.Next(Definitions.Length)]).Select(d => (d, d == Kit ? 1 + random.Next(5) : 1)).ToList());
                var containerCount = sim.ContainerIndexes.Count();
                var operativeIds = snapshot.Select(s => s.OperativeId).ToList();
                var steps = random.Next(41);
                for (var step = 0; step < steps; step++)
                {
                    var op = operativeIds[random.Next(operativeIds.Count)];
                    switch (random.Next(3))
                    {
                        case 0:
                            var index = random.Next(containerCount + 1); // sometimes an unknown container
                            sim.Collect(op, index, random.Next(Math.Max(1, sim.LinesIn(index)) + 1), 1 + random.Next(4));
                            break;
                        case 1:
                            sim.Use(op, Definitions[random.Next(Definitions.Length)]);
                            break;
                        default:
                            var bag = sim.BagOf(op).Entries;
                            if (bag.Count > 0) sim.Equip(op, bag[random.Next(bag.Count)].InstanceId);
                            break;
                    }
                    actions++;
                    sim.AssertConsistent();
                }
                var facts = sim.Freeze();
                Assert.That(facts.Integrity, Is.EqualTo(ManifestIntegrity.Ok), "seed " + seed + ": " + string.Join(",", facts.FailureCodes));
                Conservation.Check(snapshot, facts, sim.Pool, ledgerCatalogue);
            }
            Assert.That(actions, Is.GreaterThan(2000), "the runs did real work");
            Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(10));
        }
    }
}
