using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static Blackglass.Tests.ContractVectors;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 12 gate B25 (Stage 11 final review I1, the owner's nine tests): an online attempt's container keeps one entry per
    /// successful roll, registered as one pool line, never merged, split, truncated or dropped. The offline Initialize is
    /// unchanged and still merges. Forced duplicate-roll fixtures (medkit ×1 and medkit ×2 in one container) plus 200 seeded
    /// missions over the shipped table. Tests 7 and 9 have their backend half in the attempt-v1 fixtures (Task 7).
    /// </summary>
    public sealed class OnlineContainerFillTests
    {
        const string Account = "JG";
        const string Operative = "C";
        const int TableVersion = 1;

        readonly List<Object> made = new List<Object>();
        ItemDefinition medkit, rifle, vest;
        ItemCatalogue catalogue;
        LedgerCatalogue ledgerCatalogue;

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [SetUp]
        public void SetUp()
        {
            var archetype = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            medkit = Track(ItemDefinition.Create("item.medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, 3, default, null, 40));
            rifle = Track(ItemDefinition.Create("item.service-rifle", "Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: archetype));
            vest = Track(ItemDefinition.Create("item.light-vest", "Vest", ItemCategory.Armor, ItemSlot.Armor));
            catalogue = Track(ItemCatalogue.Create(medkit, rifle, vest));
            ledgerCatalogue = ItemLedgerMapping.Catalogue(catalogue);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        LootContainer NewContainer() => Track(new GameObject("LootContainer")).AddComponent<LootContainer>();

        static readonly LootItemPlan[] TwoMedkits = { new LootItemPlan("item.medkit", 1), new LootItemPlan("item.medkit", 2) };

        /// <summary>A container filled in attempt mode and registered as container 0, with one operative holding owned medkits.</summary>
        sealed class Mission
        {
            public LootContainer Container;
            public IReadOnlyList<ItemEntry> Entries;
            public MissionItemLedger Ledger;
            public ItemInventory Bag;
        }

        Mission Fill(IReadOnlyList<LootItemPlan> rolls, int ownedMedkits = 0)
        {
            var container = NewContainer();
            Assert.That(container.InitializeForAttempt(rolls, catalogue, out var entries, out var error, out _), Is.True, error);
            var ledger = new MissionItemLedger(ledgerCatalogue);
            var stacks = ownedMedkits > 0 ? new[] { new SnapshotStack("item.medkit", ownedMedkits) } : new SnapshotStack[0];
            ledger.AddParticipant(Account, new[] { new OperativeSnapshot(Operative, 0, new SnapshotItem[0], stacks, null, null, null) });
            AttemptLoot.Register(ledger, 0, entries.Select(e => new ContainerEntry(e.InstanceId, e.DefinitionId, e.Quantity)).ToList());
            var bag = new ItemInventory(8);
            if (ownedMedkits > 0) Assert.That(bag.Add(medkit, ownedMedkits), Is.EqualTo(0));
            return new Mission { Container = container, Entries = entries, Ledger = ledger, Bag = bag };
        }

        /// <summary>What UnitItems.TryCollect does for one entry, and what the session tells the ledger.</summary>
        static int Collect(Mission m, ItemCatalogue items, int line, int quantity)
        {
            var id = m.Entries[line].InstanceId;
            var moved = ItemTransfer.Move(m.Container.Contents, m.Bag, id, quantity, items).Moved;
            if (moved > 0) Assert.That(m.Ledger.Collect(Operative, 0, id, moved), Is.EqualTo(LedgerEventResult.Applied));
            return moved;
        }

        void Use(Mission m)
        {
            var entry = m.Bag.Entries.First(e => e.DefinitionId == "item.medkit");
            Assert.That(m.Bag.Remove(entry.InstanceId, 1), Is.EqualTo(1));
            Assert.That(m.Ledger.Use(Operative, "item.medkit"), Is.EqualTo(LedgerEventResult.Applied));
        }

        static IReadOnlyList<RuntimeEntry> Runtime(ItemInventory inventory) =>
            inventory.Entries.Select(e => new RuntimeEntry(e.InstanceId, e.DefinitionId, e.Quantity)).ToList();

        ParticipantFacts Freeze(Mission m) => m.Ledger.Freeze(Account,
            new[] { new OperativeRuntimeState(Operative, Runtime(m.Bag), new RuntimeEquipment(null, null, null)) },
            new[] { new ContainerRuntimeState(0, Runtime(m.Container.Contents)) });

        static string Line(PoolLine l) => $"{l.ContainerIndex},{l.Line},{l.DefinitionId},{l.Quantity}";

        // ---- the owner's nine tests ----

        [Test]
        public void Two_medkit_rolls_make_two_entries_and_two_valid_lines()
        {
            var m = Fill(TwoMedkits);
            Assert.That(m.Container.AttemptMode, Is.True);
            Assert.That(m.Container.Contents.Entries.Select(e => e.Quantity), Is.EqualTo(new[] { 1, 2 }), "two entries, not one of 3");
            Assert.That(m.Entries.Select(e => e.InstanceId), Is.EqualTo(m.Container.Contents.Entries.Select(e => e.InstanceId)), "the entries are the container's, in roll order");
            Assert.That(m.Entries.Select(e => e.InstanceId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(m.Ledger.PoolLines().Select(Line), Is.EqualTo(new[] { "0,0,item.medkit,1", "0,1,item.medkit,2" }));
            Assert.That(PoolRule(m.Ledger.PoolLines()), Is.Empty, "each line inside the shipped table's medkit range 1..2");
        }

        [Test]
        public void Partial_and_complete_collects_keep_separate_provenance()
        {
            var m = Fill(TwoMedkits);
            Assert.That(Collect(m, catalogue, 1, 1), Is.EqualTo(1), "a partial collect from line 1");
            Assert.That(Collect(m, catalogue, 0, 1), Is.EqualTo(1), "a complete collect of line 0");
            Assert.That(m.Container.Contents.Entries.Single().InstanceId, Is.EqualTo(m.Entries[1].InstanceId), "line 1 keeps its entry");
            Assert.That(m.Container.Contents.Entries.Single().Quantity, Is.EqualTo(1));
            var facts = Freeze(m);
            Assert.That(facts.Integrity, Is.EqualTo(ManifestIntegrity.Ok), string.Join(",", facts.FailureCodes));
            var o = facts.Operatives.Single();
            Assert.That(o.Collected.Select(c => (c.ContainerIndex, c.Line, c.Quantity)), Is.EqualTo(new[] { (0, 0, 1), (0, 1, 1) }));
            Assert.That(o.HeldLoot.Select(c => (c.ContainerIndex, c.Line, c.Quantity)), Is.EqualTo(new[] { (0, 0, 1), (0, 1, 1) }));
        }

        [Test]
        public void Destination_merging_does_not_change_source_accounting()
        {
            var m = Fill(TwoMedkits);
            Collect(m, catalogue, 0, 1);
            Collect(m, catalogue, 1, 2);
            Assert.That(m.Bag.Entries.Count, Is.EqualTo(1), "the bag merged both lines into one stack");
            Assert.That(m.Bag.Entries[0].Quantity, Is.EqualTo(3));
            Assert.That(m.Ledger.VerifyContainer(0, Runtime(m.Container.Contents)).Ok, Is.True);
            var facts = Freeze(m);
            Assert.That(facts.Integrity, Is.EqualTo(ManifestIntegrity.Ok), string.Join(",", facts.FailureCodes));
            Assert.That(facts.Operatives[0].Collected.Select(c => (c.Line, c.Quantity)), Is.EqualTo(new[] { (0, 1), (1, 2) }), "both lines named");
        }

        [Test]
        public void Loot_first_oldest_first_consumption_across_the_lines()
        {
            var m = Fill(TwoMedkits, ownedMedkits: 2);
            Collect(m, catalogue, 1, 2); // line 1 first: the older lot
            Collect(m, catalogue, 0, 1);
            Use(m);
            Use(m);
            Use(m);
            Use(m);
            var o = Freeze(m).Operatives[0];
            Assert.That(o.ConsumedLoot.Select(c => (c.Line, c.Quantity)), Is.EqualTo(new[] { (0, 1), (1, 2) }), "both lots used before owned stock");
            Assert.That(o.ConsumedOwned.Single().Quantity, Is.EqualTo(1), "owned stock only after both lots");
            Assert.That(o.HeldStacks.Single().Quantity, Is.EqualTo(1));
        }

        [Test]
        public void The_oldest_lot_is_by_collection_sequence_not_line_index()
        {
            var m = Fill(TwoMedkits);
            Collect(m, catalogue, 1, 2);
            Collect(m, catalogue, 0, 1);
            Use(m);
            var o = Freeze(m).Operatives[0];
            Assert.That(o.ConsumedLoot.Select(c => (c.Line, c.Quantity)), Is.EqualTo(new[] { (1, 1) }), "line 1 was collected first");
        }

        [TestCase("item.medkit", 4, "loot_roll_exceeds_stack")]
        [TestCase("item.medkit", 0, "loot_roll_quantity_invalid")]
        [TestCase("item.medkit", -1, "loot_roll_quantity_invalid")]
        [TestCase("item.service-rifle", 2, "loot_roll_not_single")]
        [TestCase("item.unknown", 1, "loot_roll_unknown_item")]
        public void Invalid_quantities_are_refused_before_ready(string definition, int quantity, string code)
        {
            var container = NewContainer();
            var rolls = new[] { new LootItemPlan("item.light-vest", 1), new LootItemPlan(definition, quantity) };
            Assert.That(container.InitializeForAttempt(rolls, catalogue, out var entries, out var error, out var failedRoll), Is.False);
            Assert.That(error, Is.EqualTo(code));
            Assert.That(failedRoll, Is.EqualTo(1), "the failing roll's index");
            Assert.That(entries, Is.Empty);
            Assert.That(container.IsEmpty, Is.True, "nothing is kept from a failed fill: the vest roll before it is not kept either");
        }

        [Test]
        public void A_roll_over_max_stack_fails_generation_with_its_code()
        {
            var container = NewContainer();
            Assert.That(container.InitializeForAttempt(new[] { new LootItemPlan("item.medkit", 4) }, catalogue, out _, out var error, out var roll), Is.False);
            Assert.That((error, roll), Is.EqualTo(("loot_roll_exceeds_stack", 0)), "never truncated to 3");
            Assert.That(container.Contents.CountOf("item.medkit"), Is.EqualTo(0));
        }

        [Test]
        public void Pool_lines_are_stable_after_source_mutations()
        {
            var m = Fill(new[] { new LootItemPlan("item.medkit", 1), new LootItemPlan("item.light-vest", 1), new LootItemPlan("item.medkit", 2) });
            var before = m.Ledger.PoolLines().Select(Line).ToList();
            Assert.That(m.Container.Contents.Remove(m.Entries[0].InstanceId, 1), Is.EqualTo(1), "remove entry 0");
            Collect(m, catalogue, 2, 1);
            Assert.That(m.Ledger.PoolLines().Select(Line), Is.EqualTo(before));
            Assert.That(before, Is.EqualTo(new[] { "0,0,item.medkit,1", "0,1,item.light-vest,1", "0,2,item.medkit,2" }));
            Assert.That(m.Container.Contents.Entries.Select(e => e.InstanceId), Is.EqualTo(new[] { m.Entries[1].InstanceId, m.Entries[2].InstanceId }),
                "the remaining entries keep their ids, so their lines never renumber");
        }

        [Test]
        public void The_loot_hash_matches_the_backend()
        {
            var m = Fill(TwoMedkits);
            // The backend's LootHash.Compute text, hashed here independently of the core's port.
            var text = "blackglass-loot-pool/1\ntable=1\n0,0,item.medkit,1\n0,1,item.medkit,2\n";
            using (var sha = SHA256.Create())
            {
                var expected = "sha256:" + string.Concat(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text)).Select(b => b.ToString("x2")));
                Assert.That(LootPoolHash.Compute(TableVersion, m.Ledger.PoolLines()), Is.EqualTo(expected));
            }
        }

        // Captured on the tree before Task 3 changed any loot code (offline settings, the shipped table and catalogue).
        static readonly (int Seed, ulong Layout, ulong Loot, string Contents)[] OfflinePins =
        {
            (1, 0xf64ad60bbfd24a67UL, 0x72b5b5459c9aa9c8UL, "[item.medkitx2][item.medkitx3][item.servo-bootsx1,item.servo-bootsx1]"),
            (7, 0x0703d7b2912a8581UL, 0x02a4ccaf825fdc89UL, "[item.light-vestx1,item.medkitx1][item.combat-bladex1][item.service-riflex1]"),
            (12345, 0x2c3d80e94eec4e34UL, 0xe63068ccb71eb0c2UL, "[item.servo-bootsx1,item.servo-bootsx1][item.combat-bladex1][item.medkitx3]"),
            (424242, 0x99e27c58b573507aUL, 0x6c80d8ea7c9415f2UL, "[item.service-riflex1,item.medkitx1][item.servo-bootsx1][item.light-vestx1,item.light-vestx1]"),
            (900001, 0xd8b07cc7ca0d7d26UL, 0x65f81ce30faa53a9UL, "[item.medkitx1][item.medkitx3][item.servo-bootsx1,item.light-vestx1]"),
        };

        [Test]
        public void Offline_generation_and_hashes_are_unchanged()
        {
            var table = LootFixtures.ShippedTable;
            var shipped = LootFixtures.ShippedCatalogue;
            foreach (var pin in OfflinePins)
            {
                Assert.That(LootFixtures.TryPlan(new MissionSettings { seed = pin.Seed }, table, out var plan, out var layout), Is.True);
                Assert.That(layout.Hash, Is.EqualTo(pin.Layout), $"seed {pin.Seed}: layout hash");
                Assert.That(plan.Hash, Is.EqualTo(pin.Loot), $"seed {pin.Seed}: LootPlan.Hash");
                var contents = new StringBuilder();
                foreach (var c in plan.Containers)
                {
                    var container = NewContainer();
                    container.Initialize(c.Items, shipped);
                    Assert.That(container.AttemptMode, Is.False);
                    contents.Append("[" + string.Join(",", container.Contents.Entries.Select(e => e.DefinitionId + "x" + e.Quantity)) + "]");
                }
                Assert.That(contents.ToString(), Is.EqualTo(pin.Contents), $"seed {pin.Seed}: the offline merged contents");
            }
        }

        [Test]
        public void Offline_initialize_still_merges()
        {
            var container = NewContainer();
            container.Initialize(TwoMedkits, catalogue);
            Assert.That(container.AttemptMode, Is.False);
            Assert.That(container.Contents.Entries.Select(e => e.Quantity), Is.EqualTo(new[] { 3 }), "offline stacking is unchanged");
        }

        // ---- Review Focus 2 and the owner's deposit ruling (Track L final rulings §3): registered containers are removal-only ----

        static string Contents(LootContainer c) => string.Join(",", c.Contents.Entries.Select(e => e.InstanceId + ":" + e.DefinitionId + "x" + e.Quantity));

        [Test]
        public void An_attempted_deposit_leaves_the_container_unchanged()
        {
            var m = Fill(TwoMedkits, ownedMedkits: 2);
            var before = Contents(m.Container);
            Assert.That(m.Container.Contents.Add(vest, 1), Is.EqualTo(1), "Add takes nothing");
            Assert.That(m.Container.Contents.Add(medkit, 1), Is.EqualTo(1), "not even into an open medkit stack");
            Assert.That(m.Container.Contents.TryAddEntry(new ItemEntry("x1", "item.light-vest", 1)), Is.False);
            Assert.That(m.Container.Contents.RoomFor(medkit), Is.EqualTo(0));
            Assert.That(m.Container.Contents.FreeEntries, Is.EqualTo(0));
            var kit = m.Bag.Entries.First().InstanceId;
            var deposit = ItemTransfer.Move(m.Bag, m.Container.Contents, kit, 1, catalogue);
            Assert.That((deposit.Moved, deposit.Failure), Is.EqualTo((0, TransferFailure.DestinationFull)), "a deposit from a bag is refused");
            Assert.That(m.Bag.CountOf("item.medkit"), Is.EqualTo(2), "the bag keeps its medkits");
            Assert.That(Contents(m.Container), Is.EqualTo(before));
        }

        [Test]
        public void The_registered_pool_and_its_hash_survive_attempted_deposits()
        {
            var m = Fill(TwoMedkits);
            var pool = m.Ledger.PoolLines().Select(Line).ToList();
            var hash = LootPoolHash.Compute(TableVersion, m.Ledger.PoolLines());
            m.Container.Contents.Add(medkit, 2);
            m.Container.Contents.TryAddEntry(new ItemEntry("x2", "item.medkit", 1));
            Assert.That(m.Ledger.PoolLines().Select(Line), Is.EqualTo(pool));
            Assert.That(LootPoolHash.Compute(TableVersion, m.Ledger.PoolLines()), Is.EqualTo(hash));
            var facts = Freeze(m);
            Assert.That(facts.Integrity, Is.EqualTo(ManifestIntegrity.Ok), string.Join(",", facts.FailureCodes));
        }

        [Test]
        public void Collection_still_works_after_an_attempted_deposit()
        {
            var m = Fill(TwoMedkits);
            m.Container.Contents.Add(vest, 1);
            Assert.That(Collect(m, catalogue, 1, 2), Is.EqualTo(2));
            Assert.That(Collect(m, catalogue, 0, 1), Is.EqualTo(1));
            Assert.That(m.Container.IsEmpty, Is.True);
            Assert.That(Freeze(m).Integrity, Is.EqualTo(ManifestIntegrity.Ok));
        }

        [Test]
        public void An_unexpected_mutation_is_an_integrity_failure_at_the_freeze()
        {
            var m = Fill(TwoMedkits);
            m.Container.Contents.Remove(m.Entries[0].InstanceId, 1); // no event reported it: the ledger is the backstop
            var facts = Freeze(m);
            Assert.That(facts.Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));
            Assert.That(facts.FailureCodes, Has.Member("container_line_mismatch"));
        }

        [Test]
        public void An_attempt_container_cannot_be_reinitialized_into_an_unlocked_one()
        {
            var m = Fill(TwoMedkits);
            var before = Contents(m.Container);
            Assert.Throws<System.InvalidOperationException>(() => m.Container.Initialize(new[] { new LootItemPlan("item.light-vest", 1) }, catalogue));
            Assert.Throws<System.InvalidOperationException>(() => m.Container.InitializeWith(new ItemInventory(0), searched: true));
            Assert.That(Contents(m.Container), Is.EqualTo(before));
            Assert.That(m.Container.Contents.AdditionsLocked, Is.True);
        }

        [Test]
        public void An_offline_container_still_takes_additions_and_stacks()
        {
            var container = NewContainer();
            container.Initialize(new[] { new LootItemPlan("item.medkit", 1) }, catalogue);
            Assert.That(container.Contents.Add(medkit, 1), Is.EqualTo(0), "offline stacking unchanged");
            Assert.That(container.Contents.Entries.Single().Quantity, Is.EqualTo(2));
            Assert.That(container.Contents.TryAddEntry(new ItemEntry("x3", "item.light-vest", 1)), Is.True);
            var plain = new ItemInventory(0);
            Assert.That(plain.Add(medkit, 4), Is.EqualTo(0));
            Assert.That(plain.Entries.Select(e => e.Quantity), Is.EqualTo(new[] { 3, 1 }));
        }

        // ---- the shipped table ----

        static string ProjectPath(params string[] parts) => Path.Combine(new[] { ProjectRoot }.Concat(parts).ToArray());

        [Test]
        public void Every_shipped_loot_entry_fits_one_entry()
        {
            var exported = Load("..", "catalogue", "catalogue.json");
            var items = Arr(exported["items"]).Select(Obj).ToDictionary(i => Str(i["id"]));
            var entries = Arr(Obj(exported["lootTable"])["entries"]).Select(Obj).ToList();
            Assert.That(entries, Is.Not.Empty);
            foreach (var e in entries)
            {
                var item = items[Str(e["itemId"])];
                var max = Int(e["maxQuantity"]);
                Assert.That(Int(e["minQuantity"]), Is.GreaterThanOrEqualTo(1), Str(e["itemId"]));
                if (Bool(item["stackable"])) Assert.That(max, Is.LessThanOrEqualTo(Int(item["maxStack"])), Str(e["itemId"]) + ": a roll must fit one stack");
                else Assert.That(max, Is.EqualTo(1), Str(e["itemId"]) + ": a non-stackable roll is one item");
            }
            var table = AssetDatabase.LoadAssetAtPath<LootTable>("Assets/_Project/Data/Items/LootTable.asset");
            Assert.That(table, Is.Not.Null);
            foreach (var e in table.Entries)
                Assert.That(e.maxQuantity, Is.LessThanOrEqualTo(e.item.IsStackable ? e.item.MaxStack : 1), e.item.Id);
        }

        [Test]
        public void Two_hundred_seeded_missions_keep_one_line_per_roll()
        {
            var table = AssetDatabase.LoadAssetAtPath<LootTable>("Assets/_Project/Data/Items/LootTable.asset");
            var shipped = AssetDatabase.LoadAssetAtPath<ItemCatalogue>("Assets/_Project/Data/Items/ItemCatalogue.asset");
            var shippedLedger = ItemLedgerMapping.Catalogue(shipped);
            var duplicates = 0;
            for (var seed = 1; seed <= 200; seed++)
            {
                if (!LootFixtures.TryPlan(seed, table, out var plan)) continue;
                var ledger = new MissionItemLedger(shippedLedger);
                for (var c = 0; c < plan.Containers.Count; c++)
                {
                    var rolls = plan.Containers[c].Items;
                    var container = NewContainer();
                    Assert.That(container.InitializeForAttempt(rolls, shipped, out var entries, out var error, out _), Is.True, $"seed {seed}: {error}");
                    Assert.That(entries.Select(e => (e.DefinitionId, e.Quantity)), Is.EqualTo(rolls.Select(r => (r.DefinitionId, r.Quantity))), $"seed {seed} container {c}");
                    AttemptLoot.Register(ledger, c, entries.Select(e => new ContainerEntry(e.InstanceId, e.DefinitionId, e.Quantity)).ToList());
                    if (rolls.GroupBy(r => r.DefinitionId).Any(g => g.Count() > 1)) duplicates++;
                }
                var pool = ledger.PoolLines();
                Assert.That(pool.Count, Is.EqualTo(plan.Containers.Sum(k => k.Items.Count)), $"seed {seed}: one line per roll");
                Assert.That(PoolRule(pool, table), Is.Empty, $"seed {seed}");
            }
            Assert.That(duplicates, Is.GreaterThan(0), "the seeds include containers with two rolls of one definition");
        }

        /// <summary>The backend LootPoolValidator's rules (lines 0..n-1, count in min..max, quantity in an entry's range), for the forced cases.</summary>
        static List<string> PoolRule(IReadOnlyList<PoolLine> pool, LootTable table = null)
        {
            var errors = new List<string>();
            var ranges = table != null
                ? table.Entries.Select(e => (e.item.Id, e.minQuantity, e.maxQuantity)).ToList()
                : new List<(string, int, int)> { ("item.medkit", 1, 2), ("item.service-rifle", 1, 1), ("item.light-vest", 1, 1) };
            int min = table != null ? table.MinItems : 1, max = table != null ? table.MaxItems : 2;
            foreach (var g in pool.GroupBy(l => l.ContainerIndex))
            {
                var lines = g.Select(l => l.Line).OrderBy(x => x).ToList();
                if (!lines.SequenceEqual(Enumerable.Range(0, lines.Count))) errors.Add($"container {g.Key}: lines");
                if (lines.Count < min || lines.Count > max) errors.Add($"container {g.Key}: count");
            }
            foreach (var l in pool)
                if (!ranges.Any(r => r.Item1 == l.DefinitionId && l.Quantity >= r.Item2 && l.Quantity <= r.Item3)) errors.Add(Line(l));
            return errors;
        }
    }
}
