using System;
using System.Collections.Generic;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// The mission's item provenance (spec §8.2): owned instances and stacks from the snapshot, loot lots per (operative, line),
    /// loot-first oldest-first consumption, equipment swaps, conservation, mismatch detection and the freeze.
    /// </summary>
    public sealed class MissionItemLedgerTests
    {
        internal const string Rifle = "item.rifle", Vest = "item.vest", Boots = "item.boots", Kit = "item.kit", Scrap = "item.scrap", Charm = "item.charm";
        const string Account = "JG", Other = "JH";

        internal static LedgerCatalogue TestCatalogue(bool withScrap = false)
        {
            var definitions = new List<LedgerItemDefinition>
            {
                new LedgerItemDefinition(Rifle, false, false, EquipSlot.Weapon),
                new LedgerItemDefinition(Vest, false, false, EquipSlot.Armor),
                new LedgerItemDefinition(Boots, false, false, EquipSlot.Utility),
                new LedgerItemDefinition(Kit, true, true, null),
                new LedgerItemDefinition(Charm, false, false, null),
            };
            // A stackable non-consumable: invalid in both real catalogues, used only by the refusal test.
            if (withScrap) definitions.Add(new LedgerItemDefinition(Scrap, true, false, null));
            return new LedgerCatalogue(definitions);
        }

        static OperativeSnapshot Operative(string id, int slot, (string Id, string Def)[] items = null, (string Def, int Qty)[] stacks = null,
            string weapon = null, string armor = null, string utility = null) =>
            new OperativeSnapshot(id, slot, (items ?? new (string, string)[0]).Select(i => new SnapshotItem(i.Id, i.Def)).ToList(),
                (stacks ?? new (string, int)[0]).Select(s => new SnapshotStack(s.Def, s.Qty)).ToList(), weapon, armor, utility);

        static OperativeSnapshot C() => Operative("C", 0, new[] { ("2k", Rifle), ("2m", Vest) }, new[] { (Kit, 2) }, weapon: "2k", armor: "2m");

        MissionItemLedger ledger;
        Runtime world;

        /// <summary>Hand-kept runtime state mirroring what ItemInventory would hold (Task 5 drives the real code).</summary>
        sealed class Runtime
        {
            public readonly Dictionary<string, List<RuntimeEntry>> Bags = new Dictionary<string, List<RuntimeEntry>>();
            public readonly Dictionary<string, string[]> Equipped = new Dictionary<string, string[]>();
            public readonly Dictionary<int, List<RuntimeEntry>> Containers = new Dictionary<int, List<RuntimeEntry>>();

            public RuntimeEquipment EquipmentOf(string op) => new RuntimeEquipment(Equipped[op][0], Equipped[op][1], Equipped[op][2]);

            public IReadOnlyList<OperativeRuntimeState> Operatives(params string[] ids) =>
                ids.Select(id => new OperativeRuntimeState(id, Bags[id], EquipmentOf(id))).ToList();

            public IReadOnlyList<ContainerRuntimeState> AllContainers() =>
                Containers.Select(c => new ContainerRuntimeState(c.Key, c.Value)).ToList();

            public void Set(string op, params (string Id, string Def, int Qty)[] entries) =>
                Bags[op] = entries.Select(e => new RuntimeEntry(e.Id, e.Def, e.Qty)).ToList();
        }

        [SetUp]
        public void SetUp()
        {
            ledger = new MissionItemLedger(TestCatalogue(withScrap: true));
            world = new Runtime();
        }

        void AddC()
        {
            ledger.AddParticipant(Account, new[] { C() });
            world.Set("C", ("2k", Rifle, 1), ("2m", Vest, 1), ("s1", Kit, 2));
            world.Equipped["C"] = new[] { "2k", "2m", null };
        }

        void Container(int index, params (string Id, string Def, int Qty)[] entries)
        {
            ledger.RegisterContainer(index, entries.Select(e => new ContainerEntry(e.Id, e.Def, e.Qty)).ToList());
            world.Containers[index] = entries.Select(e => new RuntimeEntry(e.Id, e.Def, e.Qty)).ToList();
        }

        void AssertOk(string op)
        {
            var check = ledger.VerifyOperative(op, world.Bags[op], world.EquipmentOf(op));
            Assert.That(check.Ok, Is.True, string.Join(",", check.Codes));
        }

        void AssertContainerOk(int index)
        {
            var check = ledger.VerifyContainer(index, world.Containers[index]);
            Assert.That(check.Ok, Is.True, string.Join(",", check.Codes));
        }

        ParticipantFacts Freeze(string account = Account, params string[] ops) =>
            ledger.Freeze(account, world.Operatives(ops.Length == 0 ? new[] { "C" } : ops), world.AllContainers());

        static OperativeFacts Of(ParticipantFacts f, string op) => f.Operatives.Single(o => o.OperativeId == op);

        // ---- snapshot ----

        static IEnumerable<TestCaseData> MalformedSnapshots()
        {
            yield return new TestCaseData((object)new[] { Operative("1C", 0) }).SetName("non-canonical operative id");
            yield return new TestCaseData((object)new[] { Operative("C", 0, new[] { ("x!", Rifle) }) }).SetName("non-canonical item id");
            yield return new TestCaseData((object)new[] { Operative("C", 0), Operative("C", 1) }).SetName("duplicate operative");
            yield return new TestCaseData((object)new[] { Operative("C", 0), Operative("D", 0) }).SetName("duplicate slot");
            yield return new TestCaseData((object)new[] { Operative("C", 0, new[] { ("2k", Rifle) }), Operative("D", 1, new[] { ("2k", Rifle) }) }).SetName("duplicate item");
            yield return new TestCaseData((object)new[] { Operative("C", 8) }).SetName("slot outside 0..7");
            yield return new TestCaseData((object)new[] { Operative("C", 0, new[] { ("2k", "item.unknown") }) }).SetName("unknown definition");
            yield return new TestCaseData((object)new[] { Operative("C", 0, new[] { ("2k", Kit) }) }).SetName("item of a stackable");
            yield return new TestCaseData((object)new[] { Operative("C", 0, stacks: new[] { (Rifle, 1) }) }).SetName("stack of a non-stackable");
            yield return new TestCaseData((object)new[] { Operative("C", 0, stacks: new[] { (Kit, 0) }) }).SetName("stack below 1");
            yield return new TestCaseData((object)new[] { Operative("C", 0, stacks: new[] { (Kit, 1), (Kit, 2) }) }).SetName("two stacks of one definition");
            yield return new TestCaseData((object)new[] { Operative("C", 0, weapon: "2k") }).SetName("equipped item not owned");
            yield return new TestCaseData((object)new[] { Operative("C", 0, new[] { ("2m", Vest) }, weapon: "2m") }).SetName("equipped in the wrong slot");
        }

        [TestCaseSource(nameof(MalformedSnapshots))]
        public void AddParticipant_refuses_each_malformed_snapshot(OperativeSnapshot[] operatives) =>
            Assert.Throws<ArgumentException>(() => ledger.AddParticipant(Account, operatives));

        [Test]
        public void AddParticipant_refuses_a_non_canonical_account_and_a_second_registration()
        {
            Assert.Throws<ArgumentException>(() => ledger.AddParticipant("1JG", new[] { C() }));
            AddC();
            Assert.Throws<ArgumentException>(() => ledger.AddParticipant(Account, new[] { Operative("D", 1) }));
        }

        [Test]
        public void Facts_of_an_untouched_snapshot_hold_exactly_the_snapshot()
        {
            AddC();
            var c = Of(Freeze(), "C");
            Assert.That(c.Slot, Is.EqualTo(0));
            Assert.That(c.HeldItems, Is.EquivalentTo(new[] { "2k", "2m" }));
            Assert.That(c.HeldStacks.Select(s => s.DefinitionId + "x" + s.Quantity), Is.EqualTo(new[] { Kit + "x2" }));
            Assert.That(c.Equipment.Select(e => e.Slot + "=" + e.ItemId), Is.EquivalentTo(new[] { "Weapon=2k", "Armor=2m" }));
            Assert.That(c.Collected, Is.Empty);
            Assert.That(c.ConsumedOwned, Is.Empty);
            Assert.That(c.ConsumedLoot, Is.Empty);
            Assert.That(c.HeldLoot, Is.Empty);
        }

        // ---- containers ----

        [Test]
        public void RegisterContainer_uses_entries_after_fill()
        {
            AddC();
            // Two plan items of the kit, merged by ItemInventory.Add into one entry: one line.
            Container(0, ("e1", Kit, 2));
            Assert.That(ledger.PoolLines().Select(l => $"{l.ContainerIndex},{l.Line},{l.DefinitionId},{l.Quantity}"), Is.EqualTo(new[] { "0,0," + Kit + ",2" }));
        }

        [Test]
        public void PoolLines_are_sorted_and_match_LootPoolHash_input()
        {
            AddC();
            Container(1, ("f1", Boots, 1));
            Container(0, ("e1", Kit, 3), ("e2", Rifle, 1));
            var lines = ledger.PoolLines();
            Assert.That(lines.Select(l => (l.ContainerIndex, l.Line)), Is.EqualTo(new[] { (0, 0), (0, 1), (1, 0) }));
            Assert.That(LootPoolHash.Compute(7, lines), Is.EqualTo(LootPoolHash.Compute(7, lines.Reverse().ToList())));
        }

        [Test]
        public void RegisterContainer_refuses_reuse_and_bad_entries()
        {
            AddC();
            Container(0, ("e1", Kit, 2));
            Assert.Throws<ArgumentException>(() => ledger.RegisterContainer(0, new[] { new ContainerEntry("e9", Kit, 1) }));
            Assert.Throws<ArgumentException>(() => ledger.RegisterContainer(1, new[] { new ContainerEntry("e1", Kit, 1) }), "entry ids are unique");
            Assert.Throws<ArgumentException>(() => ledger.RegisterContainer(2, new[] { new ContainerEntry("e3", "item.unknown", 1) }));
            Assert.Throws<ArgumentException>(() => ledger.RegisterContainer(3, new[] { new ContainerEntry("e4", Rifle, 2) }), "a non-stackable line holds one");
            Assert.Throws<ArgumentException>(() => ledger.RegisterContainer(4, new[] { new ContainerEntry("2k", Rifle, 1) }), "an entry id can't be an owned item id");
            Assert.Throws<ArgumentException>(() => ledger.RegisterContainer(-1, new ContainerEntry[0]));
        }

        // ---- collect ----

        [Test]
        public void Collect_moves_units_and_records_collected()
        {
            AddC();
            Container(0, ("e1", Kit, 2));
            Assert.That(ledger.Collect("C", 0, "e1", 2), Is.EqualTo(LedgerEventResult.Applied));
            world.Set("C", ("2k", Rifle, 1), ("2m", Vest, 1), ("s1", Kit, 3), ("s2", Kit, 1));
            world.Containers[0].Clear();
            AssertOk("C");
            AssertContainerOk(0);
            var c = Of(Freeze(), "C");
            Assert.That(c.Collected.Single().Quantity, Is.EqualTo(2));
            Assert.That(c.HeldLoot.Single().Quantity, Is.EqualTo(2));
            Assert.That(c.HeldStacks.Single().Quantity, Is.EqualTo(2));
        }

        [Test]
        public void Collect_partial_then_rest_merges_one_lot_per_line()
        {
            AddC();
            Container(0, ("e1", Kit, 3));
            Container(1, ("f1", Kit, 1));
            ledger.Collect("C", 0, "e1", 2);
            ledger.Collect("C", 1, "f1", 1);
            ledger.Collect("C", 0, "e1", 1);
            // The (0,0) lot keeps its first sequence, so it is still the older one: two uses take it before (1,0).
            ledger.Use("C", Kit);
            ledger.Use("C", Kit);
            ledger.Use("C", Kit);
            var c = Of(Freeze(Account, "C"), "C");
            Assert.That(c.Collected.Select(x => $"{x.ContainerIndex},{x.Line}={x.Quantity}"), Is.EquivalentTo(new[] { "0,0=3", "1,0=1" }));
            Assert.That(c.ConsumedLoot.Select(x => $"{x.ContainerIndex},{x.Line}={x.Quantity}"), Is.EquivalentTo(new[] { "0,0=3" }));
            Assert.That(c.HeldLoot.Select(x => $"{x.ContainerIndex},{x.Line}={x.Quantity}"), Is.EquivalentTo(new[] { "1,0=1" }));
        }

        [Test]
        public void Collect_beyond_the_line_is_refused_and_applies_nothing()
        {
            AddC();
            Container(0, ("e1", Kit, 2));
            Assert.That(ledger.Collect("C", 0, "e1", 3), Is.EqualTo(LedgerEventResult.Refused));
            world.Set("C", ("2k", Rifle, 1), ("2m", Vest, 1), ("s1", Kit, 2));
            AssertContainerOk(0);
            var f = Freeze();
            Assert.That(f.Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));
            Assert.That(f.FailureCodes, Has.Member("line_exceeded"));
            Assert.That(Of(f, "C").Collected, Is.Empty);
        }

        [Test]
        public void Collect_from_an_unknown_entry_or_with_no_quantity_is_refused()
        {
            AddC();
            Container(0, ("e1", Kit, 2));
            Assert.That(ledger.Collect("C", 0, "nope", 1), Is.EqualTo(LedgerEventResult.Refused));
            Assert.That(ledger.Collect("C", 5, "e1", 1), Is.EqualTo(LedgerEventResult.Refused));
            Assert.That(ledger.Collect("C", 0, "e1", 0), Is.EqualTo(LedgerEventResult.Refused));
            Assert.That(Freeze().FailureCodes, Is.EquivalentTo(new[] { "unknown_container_entry", "quantity_invalid" }));
        }

        [Test]
        public void Two_operatives_collect_one_line_without_exceeding_it()
        {
            ledger.AddParticipant(Account, new[] { C(), Operative("D", 1) });
            Container(0, ("e1", Kit, 3));
            Assert.That(ledger.Collect("C", 0, "e1", 2), Is.EqualTo(LedgerEventResult.Applied));
            Assert.That(ledger.Collect("D", 0, "e1", 1), Is.EqualTo(LedgerEventResult.Applied));
            Assert.That(ledger.Collect("D", 0, "e1", 1), Is.EqualTo(LedgerEventResult.Refused));
            world.Containers[0].Clear();
            AssertContainerOk(0);
        }

        // ---- use ----

        [Test]
        public void Use_takes_loot_before_owned()
        {
            AddC();
            Container(0, ("e1", Kit, 2));
            ledger.Collect("C", 0, "e1", 2);
            for (var i = 0; i < 3; i++) Assert.That(ledger.Use("C", Kit), Is.EqualTo(LedgerEventResult.Applied));
            var c = Of(Freeze(), "C");
            Assert.That(c.ConsumedLoot.Single().Quantity, Is.EqualTo(2));
            Assert.That(c.ConsumedOwned.Single().Quantity, Is.EqualTo(1));
            Assert.That(c.HeldStacks.Single().Quantity, Is.EqualTo(1));
            Assert.That(c.HeldLoot, Is.Empty);
        }

        [Test]
        public void Use_takes_the_oldest_loot_first()
        {
            AddC();
            Container(0, ("e1", Kit, 1));
            Container(1, ("f1", Kit, 1));
            ledger.Collect("C", 1, "f1", 1);
            ledger.Collect("C", 0, "e1", 1);
            ledger.Use("C", Kit);
            var c = Of(Freeze(), "C");
            Assert.That(c.ConsumedLoot.Single().ContainerIndex, Is.EqualTo(1), "(1,0) was collected first");
        }

        [Test]
        public void Use_of_a_non_stackable_or_non_consumable_is_refused()
        {
            AddC();
            Assert.That(ledger.Use("C", Rifle), Is.EqualTo(LedgerEventResult.Refused));
            Assert.That(ledger.Use("C", Scrap), Is.EqualTo(LedgerEventResult.Refused));
            Assert.That(ledger.Use("C", "item.unknown"), Is.EqualTo(LedgerEventResult.Refused));
            var f = Freeze();
            Assert.That(f.FailureCodes, Is.EquivalentTo(new[] { "use_not_stackable", "use_not_consumable" }));
            Assert.That(Of(f, "C").ConsumedOwned, Is.Empty);
        }

        [Test]
        public void Use_beyond_holdings_consumes_what_is_held_and_fails_integrity()
        {
            AddC();
            Assert.That(ledger.Use("C", Kit, 3), Is.EqualTo(LedgerEventResult.Refused));
            world.Set("C", ("2k", Rifle, 1), ("2m", Vest, 1));
            var f = Freeze();
            Assert.That(f.Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));
            Assert.That(f.FailureCodes, Is.EqualTo(new[] { "use_exceeds_held" }));
            Assert.That(Of(f, "C").ConsumedOwned.Single().Quantity, Is.EqualTo(2));
            Assert.That(Of(f, "C").HeldStacks, Is.Empty);
        }

        [Test]
        public void An_unknown_operative_marks_every_unfrozen_participant()
        {
            AddC();
            ledger.AddParticipant(Other, new[] { Operative("D", 1) });
            world.Set("D");
            world.Equipped["D"] = new string[3];
            Assert.That(ledger.Use("Z", Kit), Is.EqualTo(LedgerEventResult.Refused));
            Assert.That(Freeze(Account, "C").Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));
            Assert.That(Freeze(Other, "D").FailureCodes, Is.EqualTo(new[] { "unknown_operative" }));
        }

        // ---- equip ----

        [Test]
        public void Equip_an_owned_item_swaps_and_keeps_the_old_one_held()
        {
            ledger.AddParticipant(Account, new[] { Operative("C", 0, new[] { ("2k", Rifle), ("2n", Rifle) }, weapon: "2k") });
            Assert.That(ledger.Equip("C", "2n"), Is.EqualTo(LedgerEventResult.Applied));
            world.Set("C", ("2k", Rifle, 1), ("2n", Rifle, 1));
            world.Equipped["C"] = new[] { "2n", null, null };
            AssertOk("C");
            var c = Of(Freeze(), "C");
            Assert.That(c.Equipment.Single().ItemId, Is.EqualTo("2n"));
            Assert.That(c.HeldItems, Is.EquivalentTo(new[] { "2k", "2n" }));
        }

        [Test]
        public void Equip_a_collected_rifle_refers_to_its_line()
        {
            AddC();
            Container(0, ("e1", Kit, 1), ("e2", Rifle, 1));
            ledger.Collect("C", 0, "e2", 1);
            Assert.That(ledger.Equip("C", "e2"), Is.EqualTo(LedgerEventResult.Applied));
            world.Set("C", ("2k", Rifle, 1), ("2m", Vest, 1), ("s1", Kit, 2), ("e2", Rifle, 1));
            world.Equipped["C"] = new[] { "e2", "2m", null };
            world.Containers[0].RemoveAt(1);
            AssertOk("C");
            AssertContainerOk(0);
            var c = Of(Freeze(), "C");
            var weapon = c.Equipment.Single(e => e.Slot == EquipSlot.Weapon);
            Assert.That((weapon.ItemId, weapon.ContainerIndex, weapon.Line), Is.EqualTo(((string)null, (int?)0, (int?)1)));
            Assert.That(c.HeldItems, Has.Member("2k"));
        }

        [Test]
        public void Equip_an_item_of_another_operative_is_refused()
        {
            ledger.AddParticipant(Account, new[] { C(), Operative("D", 1, new[] { ("2n", Rifle) }) });
            Assert.That(ledger.Equip("C", "2n"), Is.EqualTo(LedgerEventResult.Refused));
        }

        [Test]
        public void Equip_a_stack_or_slotless_item_is_refused()
        {
            ledger.AddParticipant(Account, new[] { Operative("C", 0, new[] { ("2q", Charm) }, new[] { (Kit, 2) }) });
            Assert.That(ledger.Equip("C", "s1"), Is.EqualTo(LedgerEventResult.Refused));
            Assert.That(ledger.Equip("C", "2q"), Is.EqualTo(LedgerEventResult.Refused));
            world.Set("C", ("2q", Charm, 1), ("s1", Kit, 2));
            world.Equipped["C"] = new string[3];
            Assert.That(Freeze().FailureCodes, Is.EquivalentTo(new[] { "equip_not_held", "equip_no_slot" }));
        }

        // ---- verify ----

        [Test]
        public void VerifyOperative_ok_after_each_event()
        {
            AddC();
            Container(0, ("e1", Kit, 3), ("e2", Boots, 1));
            AssertOk("C");
            ledger.Collect("C", 0, "e1", 2);
            world.Set("C", ("2k", Rifle, 1), ("2m", Vest, 1), ("s1", Kit, 3), ("s2", Kit, 1));
            world.Containers[0][0] = new RuntimeEntry("e1", Kit, 1);
            AssertOk("C");
            AssertContainerOk(0);
            ledger.Use("C", Kit);
            world.Set("C", ("2k", Rifle, 1), ("2m", Vest, 1), ("s1", Kit, 3));
            AssertOk("C");
            ledger.Collect("C", 0, "e2", 1);
            world.Set("C", ("2k", Rifle, 1), ("2m", Vest, 1), ("s1", Kit, 3), ("e2", Boots, 1));
            world.Containers[0].RemoveAt(1);
            AssertOk("C");
            AssertContainerOk(0);
            ledger.Equip("C", "e2");
            world.Equipped["C"] = new[] { "2k", "2m", "e2" };
            AssertOk("C");
            Assert.That(Freeze().Integrity, Is.EqualTo(ManifestIntegrity.Ok));
        }

        [Test]
        public void VerifyOperative_detects_each_kind_of_mismatch()
        {
            AddC();
            LedgerCheck Check(params (string, string, int)[] bag) => ledger.VerifyOperative("C", bag.Select(e => new RuntimeEntry(e.Item1, e.Item2, e.Item3)).ToList(), world.EquipmentOf("C"));
            Assert.That(Check(("2k", Rifle, 1), ("2m", Vest, 1), ("s1", Kit, 3)).Codes, Is.EqualTo(new[] { "bag_count_mismatch" }));
            Assert.That(Check(("2k", Rifle, 1), ("2m", Vest, 1), ("x9", Rifle, 1), ("s1", Kit, 2)).Codes, Has.Member("bag_item_unmapped"));
            Assert.That(Check(("2k", Rifle, 1), ("s1", Kit, 2)).Codes, Has.Member("bag_item_missing"));
            world.Equipped["C"] = new[] { "2k", null, null };
            Assert.That(Check(("2k", Rifle, 1), ("2m", Vest, 1), ("s1", Kit, 2)).Codes, Is.EqualTo(new[] { "equipment_mismatch" }));
        }

        [Test]
        public void VerifyContainer_detects_extra_and_short_entries()
        {
            AddC();
            Container(0, ("e1", Kit, 3));
            Assert.That(ledger.VerifyContainer(0, new[] { new RuntimeEntry("e1", Kit, 2) }).Codes, Is.EqualTo(new[] { "container_line_mismatch" }));
            Assert.That(ledger.VerifyContainer(0, new[] { new RuntimeEntry("e1", Kit, 3), new RuntimeEntry("e9", Kit, 1) }).Codes, Is.EqualTo(new[] { "container_extra_entry" }));
            Assert.That(ledger.VerifyContainer(0, new RuntimeEntry[0]).Codes, Is.EqualTo(new[] { "container_line_mismatch" }));
            Assert.That(ledger.VerifyContainer(9, new RuntimeEntry[0]).Codes, Is.EqualTo(new[] { "unknown_container" }));
        }

        // ---- integrity ----

        [Test]
        public void A_failure_is_sticky_and_keeps_facts_well_formed()
        {
            AddC();
            Container(0, ("e1", Kit, 2));
            ledger.Use("C", Kit, 5);
            ledger.Collect("C", 0, "e1", 2);
            ledger.Use("C", Kit);
            world.Set("C", ("2k", Rifle, 1), ("2m", Vest, 1), ("s1", Kit, 1));
            world.Containers[0].Clear();
            var f = Freeze();
            Assert.That(f.Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));
            var c = Of(f, "C");
            Assert.That(c.HeldStacks.All(s => s.Quantity >= 1) && c.HeldLoot.All(s => s.Quantity >= 1) && c.ConsumedOwned.All(s => s.Quantity >= 1)
                && c.ConsumedLoot.All(s => s.Quantity >= 1) && c.Collected.All(s => s.Quantity >= 1), Is.True);
            Assert.That(c.ConsumedOwned.Select(x => x.DefinitionId).Distinct().Count(), Is.EqualTo(c.ConsumedOwned.Count));
        }

        [Test]
        public void ReportIntegrityFailure_marks_only_that_participant()
        {
            AddC();
            ledger.AddParticipant(Other, new[] { Operative("D", 1) });
            world.Set("D");
            world.Equipped["D"] = new string[3];
            ledger.ReportIntegrityFailure(Account, "server_detected");
            Assert.That(Freeze(Account, "C").FailureCodes, Is.EqualTo(new[] { "server_detected" }));
            Assert.That(Freeze(Other, "D").Integrity, Is.EqualTo(ManifestIntegrity.Ok));
        }

        [Test]
        public void ReportIntegrityFailure_validates_and_refuses_after_the_freeze()
        {
            AddC();
            Assert.Throws<ArgumentException>(() => ledger.ReportIntegrityFailure(Account, "Bad Code"));
            Assert.Throws<ArgumentException>(() => ledger.ReportIntegrityFailure(Account, new string('a', 41)));
            Assert.Throws<ArgumentException>(() => ledger.ReportIntegrityFailure("JZ", "x"));
            Freeze();
            Assert.Throws<InvalidOperationException>(() => ledger.ReportIntegrityFailure(Account, "late"));
        }

        [Test]
        public void Failure_codes_are_bounded()
        {
            AddC();
            for (var i = 0; i < 40; i++) ledger.ReportIntegrityFailure(Account, "code_" + (char)('a' + i / 26) + (char)('a' + i % 26));
            Assert.That(Freeze().FailureCodes.Count, Is.EqualTo(32));
        }

        // ---- freeze ----

        [Test]
        public void Freeze_verifies_and_a_mismatch_at_the_freeze_is_reported()
        {
            AddC();
            world.Set("C", ("2k", Rifle, 1), ("2m", Vest, 1), ("s1", Kit, 3));
            var f = Freeze();
            Assert.That(f.Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));
            Assert.That(f.FailureCodes, Is.EqualTo(new[] { "bag_count_mismatch" }));
        }

        [Test]
        public void Freeze_requires_every_operative_and_container_state()
        {
            AddC();
            Container(0, ("e1", Kit, 2));
            var f = ledger.Freeze(Account, new OperativeRuntimeState[0], new ContainerRuntimeState[0]);
            Assert.That(f.FailureCodes, Is.EquivalentTo(new[] { "operative_state_missing", "container_state_missing" }));
        }

        [Test]
        public void Freeze_is_idempotent_and_returns_equal_facts()
        {
            AddC();
            var first = Freeze();
            world.Set("C");
            Assert.That(Freeze(), Is.SameAs(first));
            Assert.That(ledger.IsFrozen(Account), Is.True);
        }

        [Test]
        public void Events_after_freeze_change_no_facts()
        {
            AddC();
            Container(0, ("e1", Kit, 2), ("e2", Boots, 1));
            var before = Freeze();
            Assert.That(ledger.Collect("C", 0, "e2", 1), Is.EqualTo(LedgerEventResult.Frozen));
            Assert.That(ledger.Use("C", Kit), Is.EqualTo(LedgerEventResult.Frozen));
            Assert.That(ledger.Equip("C", "e2"), Is.EqualTo(LedgerEventResult.Frozen));
            Assert.That(Freeze(), Is.SameAs(before));
            var c = Of(before, "C");
            Assert.That(c.Collected, Is.Empty);
            Assert.That(c.HeldStacks.Single().Quantity, Is.EqualTo(2));
        }

        [Test]
        public void A_collect_after_freeze_still_lowers_the_container_line()
        {
            AddC();
            ledger.AddParticipant(Other, new[] { Operative("D", 1) });
            Container(0, ("e1", Kit, 3));
            Freeze(Account, "C");
            Assert.That(ledger.Collect("C", 0, "e1", 2), Is.EqualTo(LedgerEventResult.Frozen));
            world.Containers[0][0] = new RuntimeEntry("e1", Kit, 1);
            AssertContainerOk(0);
            Assert.That(ledger.Collect("D", 0, "e1", 2), Is.EqualTo(LedgerEventResult.Refused), "only one is left");
        }

        // ---- conservation ----

        [Test]
        public void Facts_conserve_after_a_mixed_script()
        {
            ledger.AddParticipant(Account, new[] { C(), Operative("D", 1, new[] { ("2n", Rifle) }, new[] { (Kit, 1) }, weapon: "2n") });
            Container(0, ("e1", Kit, 3), ("e2", Rifle, 1));
            Container(1, ("f1", Kit, 2), ("f2", Boots, 1));
            Container(2, ("g1", Vest, 1));
            var script = new Func<LedgerEventResult>[]
            {
                () => ledger.Use("C", Kit), () => ledger.Collect("C", 0, "e1", 2), () => ledger.Collect("D", 1, "f1", 1), () => ledger.Use("D", Kit),
                () => ledger.Use("D", Kit), () => ledger.Collect("C", 0, "e2", 1), () => ledger.Equip("C", "e2"), () => ledger.Collect("D", 1, "f2", 1),
                () => ledger.Equip("D", "f2"), () => ledger.Use("C", Kit), () => ledger.Collect("C", 1, "f1", 1), () => ledger.Use("C", Kit),
                () => ledger.Collect("D", 0, "e1", 1), () => ledger.Collect("D", 2, "g1", 1), () => ledger.Equip("D", "g1"), () => ledger.Equip("C", "2k"),
                () => ledger.Use("D", Kit), () => ledger.Use("C", Kit), () => ledger.Use("C", Kit),
            };
            var pool = ledger.PoolLines();
            foreach (var step in script) Assert.That(step(), Is.EqualTo(LedgerEventResult.Applied));
            var facts = ledger.Freeze(Account, new OperativeRuntimeState[0], new ContainerRuntimeState[0]); // states omitted: only conservation is checked
            Conservation.Check(new[] { C(), Operative("D", 1, new[] { ("2n", Rifle) }, new[] { (Kit, 1) }, weapon: "2n") }, facts, pool, TestCatalogue(withScrap: true));
        }
    }
}
