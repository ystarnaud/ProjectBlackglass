using System;
using System.Collections.Generic;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 12 Task 6: the attempt session's ledger wiring, its freeze at the first terminal event, and the manifest. The
    /// harness drives Unity's real item code (InventorySession.FromSnapshot, LootContainer.InitializeForAttempt, ItemTransfer,
    /// ItemInventory.Remove, InventorySession.EquipInMission) and tells the session exactly what the UnitItems detail events
    /// report, through the session's own handlers.
    /// </summary>
    public sealed class AttemptSessionTests
    {
        const string CatalogueVersion = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        const string Medkit = "item.medkit", Rifle = "item.service-rifle", Vest = "item.light-vest", Boots = "item.servo-boots";

        readonly List<Object> made = new List<Object>();
        ItemCatalogue catalogue;
        LedgerCatalogue ledgerCatalogue;

        T Track<T>(T o) where T : Object { made.Add(o); return o; }

        [SetUp]
        public void SetUp()
        {
            var archetype = Track(CombatArchetype.Create("Ranged", CombatRole.Ranged, 8f, 15, 1f));
            catalogue = Track(ItemCatalogue.Create(
                Track(ItemDefinition.Create(Medkit, "Medkit", ItemCategory.Consumable, ItemSlot.None, 3, default, null, 40)),
                Track(ItemDefinition.Create(Rifle, "Rifle", ItemCategory.Weapon, ItemSlot.Weapon, weapon: archetype)),
                Track(ItemDefinition.Create(Vest, "Vest", ItemCategory.Armor, ItemSlot.Armor)),
                Track(ItemDefinition.Create(Boots, "Boots", ItemCategory.Utility, ItemSlot.Utility))));
            ledgerCatalogue = ItemLedgerMapping.Catalogue(catalogue);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        static SnapshotOperativeDto Operative(int slot, string id, (string ItemId, string Definition)[] items, int medkits, string weapon = null) =>
            new SnapshotOperativeDto
            {
                slot = slot, operativeId = id, definitionId = "def", experience = 0, rank = 1, choices = new string[0],
                bag = new SnapshotBagDto
                {
                    instances = items.Select(i => new SnapshotInstanceDto { itemId = i.ItemId, definitionId = i.Definition }).ToArray(),
                    stacks = medkits > 0 ? new[] { new SnapshotStackDto { definitionId = Medkit, quantity = medkits } } : new SnapshotStackDto[0],
                },
                equipment = new EquipmentSlotsDto { weapon = weapon },
            };

        AttemptStart Start(params SnapshotOperativeDto[] operatives)
        {
            var snapshot = new LoadoutSnapshotDto { accountId = "JG", inventoryVersion = 1, catalogueVersion = CatalogueVersion, operatives = operatives };
            var response = AttemptTestData.Response(snapshot);
            Assert.That(AttemptStart.TryCreate(AttemptTestData.AttemptId, response, CatalogueVersion, id => id == "def", ledgerCatalogue, out var start, out var refusal),
                Is.True, refusal);
            return start;
        }

        /// <summary>The runtime of one attempt: the working inventory, attempt-mode containers and the session over them.</summary>
        sealed class Harness
        {
            public InventorySession Inventory;
            public readonly List<LootContainer> Containers = new List<LootContainer>();
            public readonly List<IReadOnlyList<ItemEntry>> Entries = new List<IReadOnlyList<ItemEntry>>();
            public AttemptSession Session;
            public int Elapsed = 1234;
            public ItemCatalogue Catalogue;

            public ItemInventory Bag(string operativeId) => Inventory.Working.Loadout(operativeId).Bag;

            public OperativeRuntimeState State(string operativeId)
            {
                var loadout = Inventory.Working != null ? Inventory.Working.Loadout(operativeId) : null;
                if (loadout == null) return null;
                return new OperativeRuntimeState(operativeId, AttemptRuntime.Entries(loadout.Bag), new RuntimeEquipment(
                    loadout.Equipment.InstanceIdIn(ItemSlot.Weapon), loadout.Equipment.InstanceIdIn(ItemSlot.Armor), loadout.Equipment.InstanceIdIn(ItemSlot.Utility)));
            }

            /// <summary>UnitItems.TryCollect's move for one entry, then CollectedDetail with what moved.</summary>
            public int Collect(string operativeId, int container, int line, int quantity)
            {
                if (container >= Containers.Count || line >= Entries[container].Count) return 0;
                var id = Entries[container][line].InstanceId;
                if (Containers[container].Contents.Find(id) == null) return 0;
                var moved = ItemTransfer.Move(Containers[container].Contents, Bag(operativeId), id, quantity, Catalogue).Moved;
                if (moved > 0) Assert.That(Session.OnCollected(operativeId, container, id, moved), Is.Not.EqualTo(LedgerEventResult.Refused));
                return moved;
            }

            /// <summary>UnitItems.TryUse's consumption (one unit), then UsedDetail.</summary>
            public bool Use(string operativeId, string definitionId)
            {
                var definition = Catalogue.Find(definitionId);
                if (definition == null || definition.Category != ItemCategory.Consumable) return false;
                var entry = Bag(operativeId).Entries.FirstOrDefault(e => e.DefinitionId == definitionId);
                if (entry == null || Bag(operativeId).Remove(entry.InstanceId, 1) != 1) return false;
                Assert.That(Session.OnUsed(operativeId, definitionId), Is.Not.EqualTo(LedgerEventResult.Refused));
                return true;
            }

            /// <summary>UnitItems.TryEquip (InventorySession.EquipInMission), then EquippedDetail.</summary>
            public bool Equip(string operativeId, string entryId)
            {
                if (!Inventory.EquipInMission(operativeId, entryId).Ok) return false;
                Assert.That(Session.OnEquipped(operativeId, entryId), Is.Not.EqualTo(LedgerEventResult.Refused));
                return true;
            }
        }

        Harness Begin(AttemptStart start, params LootItemPlan[][] containerRolls)
        {
            var h = new Harness { Catalogue = catalogue };
            h.Inventory = InventorySession.FromSnapshot(catalogue, 8, start.Snapshot, out var problem);
            Assert.That(problem, Is.Null);
            h.Inventory.BeginMission(start.AttemptId);
            foreach (var rolls in containerRolls)
            {
                var container = Track(new GameObject("Container")).AddComponent<LootContainer>();
                Assert.That(container.InitializeForAttempt(rolls, catalogue, out var entries, out var error, out _), Is.True, error);
                h.Containers.Add(container);
                h.Entries.Add(entries.Select(e => e.Clone()).ToList());
            }
            h.Session = new AttemptSession(start, ledgerCatalogue, "r1", 0xcf2e14dece9cc2a6UL, 1,
                h.Entries.Select((e, i) => (i, (IReadOnlyList<ItemEntry>)e)).ToList(), h.State,
                () => h.Containers.Select((c, i) => AttemptRuntime.ContainerState(i, c)).ToList(), () => h.Elapsed);
            return h;
        }

        static readonly LootItemPlan[] TwoMedkits = { new LootItemPlan(Medkit, 1), new LootItemPlan(Medkit, 2) };
        static readonly ObjectiveResult[] Done = { new ObjectiveResult("eliminate", ObjectiveStateKind.Completed), new ObjectiveResult("extract", ObjectiveStateKind.Completed) };

        AttemptStart OneOperative(int medkits = 1) => Start(Operative(0, "C", new[] { ("2k", Rifle) }, medkits, weapon: "2k"));

        [Test]
        public void Collect_and_use_feed_the_ledger_and_stay_consistent()
        {
            var h = Begin(OneOperative(), TwoMedkits, new[] { new LootItemPlan(Vest, 1) });
            Assert.That(h.Session.Start(), Is.Null);
            Assert.That(h.Session.Ledger.PoolLines().Select(l => (l.ContainerIndex, l.Line, l.Quantity)), Is.EqualTo(new[] { (0, 0, 1), (0, 1, 2), (1, 0, 1) }));
            Assert.That(h.Collect("C", 0, 1, 2), Is.EqualTo(2));
            Assert.That(h.Collect("C", 1, 0, 1), Is.EqualTo(1));
            Assert.That(h.Use("C", Medkit), Is.True);
            Assert.That(h.Equip("C", h.Entries[1][0].InstanceId), Is.True);
            var state = h.State("C");
            Assert.That(h.Session.Ledger.VerifyOperative("C", state.Bag, state.Equipment).Ok, Is.True);
            h.Session.Finish(ManifestOutcome.Success, Done, new string[0]);
            Assert.That(h.Session.Outcome.Kind, Is.EqualTo(ManifestBuildKind.Normal), string.Join(",", h.Session.Outcome.Diagnostics));
            var m = h.Session.Outcome.Manifest.Manifest;
            Assert.That(m.Integrity, Is.EqualTo(ManifestIntegrity.Ok));
            Assert.That(m.OutcomeSequence, Is.EqualTo(5), "four events, then the terminal one");
            Assert.That(m.ServerElapsedMs, Is.EqualTo(1234));
            Assert.That(m.LayoutHash, Is.EqualTo("cf2e14dece9cc2a6"));
            Assert.That(m.LootHash, Is.EqualTo(LootPoolHash.Compute(1, h.Session.Ledger.PoolLines())));
            var c = h.Session.Facts.Operatives.Single();
            Assert.That(c.Collected.Select(x => (x.ContainerIndex, x.Line, x.Quantity)), Is.EqualTo(new[] { (0, 1, 2), (1, 0, 1) }));
            Assert.That(c.ConsumedLoot.Single().Quantity, Is.EqualTo(1), "loot first");
            Assert.That(c.Equipment.Single(e => e.Slot == EquipSlot.Armor).Line, Is.EqualTo(0));
        }

        [Test]
        public void Play_after_the_end_changes_nothing()
        {
            var h = Begin(OneOperative(), TwoMedkits);
            h.Session.Start();
            h.Collect("C", 0, 0, 1);
            h.Session.Finish(ManifestOutcome.Success, Done, new string[0]);
            var bytes = h.Session.Outcome.Manifest.CanonicalBytes;
            var facts = h.Session.Facts;
            h.Collect("C", 0, 1, 2);                       // a collect in the same frame as extraction, or later
            Assert.That(h.Session.OnUsed("C", Medkit), Is.EqualTo(LedgerEventResult.Frozen));
            h.Session.Finish(ManifestOutcome.Defeat, Done, new[] { "C" }); // a second terminal event: ignored
            Assert.That(h.Session.Outcome.Manifest.CanonicalBytes, Is.EqualTo(bytes));
            Assert.That(h.Session.Facts, Is.SameAs(facts));
            Assert.That(h.Session.Result.Outcome, Is.EqualTo(ManifestOutcome.Success));
            Assert.That(h.Session.Outcome.Manifest.Manifest.OutcomeSequence, Is.EqualTo(2));
        }

        [Test]
        public void A_ledger_mismatch_is_never_ok()
        {
            var h = Begin(OneOperative(), TwoMedkits);
            h.Session.Start();
            h.Collect("C", 0, 0, 1);
            h.Bag("C").Add(catalogue.Find(Medkit), 1); // a bag change with no detail event: a wiring bug
            h.Session.Finish(ManifestOutcome.Success, Done, new string[0]);
            Assert.That(h.Session.Outcome.Kind, Is.Not.EqualTo(ManifestBuildKind.Failed));
            Assert.That(h.Session.Outcome.Manifest.Manifest.Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));
            Assert.That(h.Session.Outcome.Diagnostics, Has.Member("bag_count_mismatch"));
        }

        [Test]
        public void A_bypassing_container_deposit_is_a_ledger_mismatch_at_the_freeze()
        {
            var h = Begin(OneOperative(), TwoMedkits);
            h.Session.Start();
            h.Containers[0].Contents.Add(catalogue.Find(Vest), 1);
            h.Session.Finish(ManifestOutcome.Success, Done, new string[0]);
            Assert.That(h.Session.Outcome.Manifest.Manifest.Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));
            Assert.That(h.Session.Outcome.Diagnostics, Has.Member("container_extra_entry"));
        }

        [Test]
        public void A_runtime_that_disagrees_at_the_start_is_never_ok()
        {
            var h = Begin(OneOperative(), TwoMedkits);
            h.Bag("C").Add(catalogue.Find(Medkit), 1);
            Assert.That(h.Session.Start(), Is.Null, "the start check records it; it doesn't refuse");
            h.Session.Finish(ManifestOutcome.Success, Done, new string[0]);
            Assert.That(h.Session.Outcome.Manifest.Manifest.Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));
        }

        [Test]
        public void An_ArgumentException_from_the_builder_is_a_failed_outcome()
        {
            var h = Begin(OneOperative(), TwoMedkits);
            h.Session.Start();
            Assert.DoesNotThrow(() => h.Session.Finish(ManifestOutcome.Defeat, Done, new[] { "ZZ" })); // not the participant's operative
            Assert.That(h.Session.Outcome.Kind, Is.EqualTo(ManifestBuildKind.Failed));
            Assert.That(h.Session.Outcome.Manifest, Is.Null, "no bytes");
            Assert.That(h.Session.FailureMessage, Does.Contain("dead operative"));
            Assert.That(h.Session.IsFrozen, Is.True);
        }

        [Test]
        public void A_pool_the_ledger_refuses_is_bad_pool()
        {
            var h = Begin(OneOperative(), TwoMedkits);
            h.Entries[0] = new List<ItemEntry> { new ItemEntry("x1", "item.unknown", 1) };
            h.Session = new AttemptSession(h.Session.Attempt, ledgerCatalogue, "r1", 1UL, 1, h.Entries.Select((e, i) => (i, (IReadOnlyList<ItemEntry>)e)).ToList(),
                h.State, () => new List<ContainerRuntimeState>(), () => 0);
            Assert.That(h.Session.Start(), Is.EqualTo("bad_pool"));
            Assert.That(h.Session.Ledger, Is.Null);
        }

        [Test]
        public void A_dead_operative_shows_with_its_held_items_as_frozen()
        {
            var start = Start(Operative(0, "C", new[] { ("2k", Rifle) }, 1, "2k"), Operative(1, "D", new[] { ("2m", Rifle) }, 0, "2m"));
            var h = Begin(start, TwoMedkits);
            h.Session.Start();
            h.Collect("D", 0, 1, 2);
            h.Session.Finish(ManifestOutcome.Defeat, Done, new[] { "D" });
            var d = h.Session.Outcome.Manifest.Manifest.Operatives.Single(o => o.Slot == 1);
            Assert.That(d.Alive, Is.False);
            Assert.That(h.Session.Facts.Operatives[1].HeldLoot.Single().Quantity, Is.EqualTo(2));
        }

        [Test]
        public void The_clock_counts_from_its_start_or_restart()
        {
            var now = 1000L;
            var clock = new ServerClock(() => now);
            now = 1750;
            Assert.That(clock.ElapsedMs, Is.EqualTo(750));
            clock.Restart();
            now = 1800;
            Assert.That(clock.ElapsedMs, Is.EqualTo(50));
        }

        // ---- Stage 11's seeded missions, through the session, with attempt-mode containers ----

        static readonly string[] Definitions = { Rifle, Vest, Boots, Medkit };

        [Test]
        public void Seeded_random_attempts_stay_consistent_conserve_and_build_normally()
        {
            var actions = 0;
            for (var seed = 1; seed <= 200; seed++)
            {
                var random = new System.Random(seed);
                var next = 100L;
                var operatives = new List<SnapshotOperativeDto>();
                foreach (var slot in Enumerable.Range(0, 1 + random.Next(3)))
                {
                    var items = new List<(string, string)>();
                    string weapon = null;
                    foreach (var definition in new[] { Rifle, Vest, Boots })
                    {
                        if (random.Next(2) == 0) continue;
                        var id = Base58Id.Format(next++);
                        items.Add((id, definition));
                        if (definition == Rifle && random.Next(2) == 0) weapon = id;
                    }
                    operatives.Add(Operative(slot, Base58Id.Format(10 + slot), items.ToArray(), random.Next(4), weapon));
                }
                var start = Start(operatives.ToArray());
                var rolls = Enumerable.Range(0, 1 + random.Next(3)).Select(_ => Enumerable.Range(0, 1 + random.Next(2)).Select(__ =>
                {
                    var d = Definitions[random.Next(Definitions.Length)];
                    return new LootItemPlan(d, d == Medkit ? 1 + random.Next(3) : 1);
                }).ToArray()).ToArray();
                var h = Begin(start, rolls);
                Assert.That(h.Session.Start(), Is.Null, "seed " + seed);
                var ids = start.Snapshot.Select(o => o.OperativeId).ToList();
                for (var step = random.Next(41); step > 0; step--)
                {
                    var op = ids[random.Next(ids.Count)];
                    switch (random.Next(3))
                    {
                        case 0:
                            var c = random.Next(h.Containers.Count + 1);
                            h.Collect(op, c, random.Next(3), 1 + random.Next(3));
                            break;
                        case 1:
                            h.Use(op, Definitions[random.Next(Definitions.Length)]);
                            break;
                        default:
                            var bag = h.Bag(op).Entries;
                            if (bag.Count > 0) h.Equip(op, bag[random.Next(bag.Count)].InstanceId);
                            break;
                    }
                    actions++;
                }
                h.Session.Finish(ManifestOutcome.Success, Done, new string[0]);
                Assert.That(h.Session.Facts.Integrity, Is.EqualTo(ManifestIntegrity.Ok), "seed " + seed + ": " + string.Join(",", h.Session.Facts.FailureCodes));
                Assert.That(h.Session.Outcome.Kind, Is.EqualTo(ManifestBuildKind.Normal), "seed " + seed);
                Conservation.Check(start.Snapshot, h.Session.Facts, h.Session.Ledger.PoolLines(), ledgerCatalogue);
                foreach (var o in made.OfType<GameObject>().ToList()) Object.DestroyImmediate(o);
                made.RemoveAll(o => o == null);
            }
            Assert.That(actions, Is.GreaterThan(2000), "the runs did real work");
        }
    }
}
