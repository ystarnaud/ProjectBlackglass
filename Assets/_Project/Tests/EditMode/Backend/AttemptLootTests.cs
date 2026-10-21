using System;
using System.Collections.Generic;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>Stage 12 gate B25, the core half: the roll-fit rule and the registration of a container's entries in roll order.</summary>
    public sealed class AttemptLootTests
    {
        static readonly LedgerItemDefinition Medkit = new LedgerItemDefinition("item.medkit", true, true, null);
        static readonly LedgerItemDefinition Rifle = new LedgerItemDefinition("item.service-rifle", false, false, EquipSlot.Weapon);

        [TestCase(1, null)]
        [TestCase(3, null)]
        [TestCase(4, AttemptLoot.ExceedsStack)]
        [TestCase(0, AttemptLoot.QuantityInvalid)]
        [TestCase(-2, AttemptLoot.QuantityInvalid)]
        public void A_stackable_roll_fits_one_to_max_stack(int quantity, string code) =>
            Assert.That(AttemptLoot.CheckRoll(new LootRoll("item.medkit", quantity), Medkit, 3), Is.EqualTo(code));

        [TestCase(1, null)]
        [TestCase(2, AttemptLoot.NotSingle)]
        [TestCase(0, AttemptLoot.QuantityInvalid)]
        public void A_non_stackable_roll_is_exactly_one(int quantity, string code) =>
            Assert.That(AttemptLoot.CheckRoll(new LootRoll("item.service-rifle", quantity), Rifle, 1), Is.EqualTo(code));

        [Test]
        public void An_unknown_or_mismatched_definition_is_unknown_item()
        {
            Assert.That(AttemptLoot.CheckRoll(new LootRoll("item.medkit", 1), null, 3), Is.EqualTo(AttemptLoot.UnknownItem));
            Assert.That(AttemptLoot.CheckRoll(new LootRoll("item.medkit", 1), Rifle, 1), Is.EqualTo(AttemptLoot.UnknownItem));
            Assert.That(AttemptLoot.CheckRoll(new LootRoll(null, 1), Medkit, 3), Is.EqualTo(AttemptLoot.UnknownItem));
        }

        static MissionItemLedger Ledger() => new MissionItemLedger(new LedgerCatalogue(new[]
        {
            Medkit, Rifle, new LedgerItemDefinition("item.light-vest", false, false, EquipSlot.Armor),
        }));

        [Test]
        public void Lines_are_the_entries_roll_order_indices()
        {
            var ledger = Ledger();
            AttemptLoot.Register(ledger, 2, new[] { new ContainerEntry("e1", "item.medkit", 1), new ContainerEntry("e2", "item.medkit", 2) });
            Assert.That(ledger.PoolLines().Select(l => (l.ContainerIndex, l.Line, l.DefinitionId, l.Quantity)),
                Is.EqualTo(new[] { (2, 0, "item.medkit", 1), (2, 1, "item.medkit", 2) }));
        }

        [Test]
        public void Pool_lines_are_stable_after_source_mutations()
        {
            var ledger = Ledger();
            ledger.AddParticipant("JG", new[] { new OperativeSnapshot("C", 0, new SnapshotItem[0], new SnapshotStack[0], null, null, null) });
            AttemptLoot.Register(ledger, 0, new[]
            {
                new ContainerEntry("e0", "item.medkit", 1), new ContainerEntry("e1", "item.light-vest", 1), new ContainerEntry("e2", "item.medkit", 2),
            });
            var before = ledger.PoolLines().Select(l => (l.ContainerIndex, l.Line, l.DefinitionId, l.Quantity)).ToList();
            // entry 0 removed from the container (no ledger event), then one unit collected from entry 2
            Assert.That(ledger.Collect("C", 0, "e2", 1), Is.EqualTo(LedgerEventResult.Applied));
            Assert.That(ledger.PoolLines().Select(l => (l.ContainerIndex, l.Line, l.DefinitionId, l.Quantity)), Is.EqualTo(before));
            var check = ledger.VerifyContainer(0, new[] { new RuntimeEntry("e1", "item.light-vest", 1), new RuntimeEntry("e2", "item.medkit", 1) });
            Assert.That(check.Codes, Is.EqualTo(new[] { "container_line_mismatch" }), "entry 0's disappearance is caught by its line, the others still match");
        }

        [Test]
        public void A_second_registration_of_a_container_is_refused() =>
            Assert.Throws<ArgumentException>(() =>
            {
                var ledger = Ledger();
                AttemptLoot.Register(ledger, 0, new[] { new ContainerEntry("e1", "item.medkit", 1) });
                AttemptLoot.Register(ledger, 0, new[] { new ContainerEntry("e2", "item.medkit", 1) });
            });
    }
}
