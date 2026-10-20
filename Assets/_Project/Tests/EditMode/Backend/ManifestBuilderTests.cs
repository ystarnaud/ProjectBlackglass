using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Blackglass.Backend.Core;
using NUnit.Framework;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public sealed class ManifestBuilderTests
    {
        const string Attempt = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", Session = "0192f1a5-0000-7000-8000-000000000001";
        const string Medkit = "item.medkit", Rifle = "item.rifle", Vest = "item.vest", Boots = "item.boots", Kit = "item.kit";
        static readonly string Catalogue = "sha256:" + string.Concat(Enumerable.Repeat("5f2c", 16));
        static readonly string LootHash = "sha256:" + string.Concat(Enumerable.Repeat("a1b2", 16));

        static string Vector(string name) => File.ReadAllText(Path.Combine(ContractVectors.ProjectRoot, "Backend", "contracts", "manifest-v1", "valid", name), new UTF8Encoding(false));

        static ManifestHeader Header(string attempt = Attempt, string revision = "r1", int table = 1) =>
            new ManifestHeader(attempt, "JG", Session, revision, Catalogue, "cf2e14dece9cc2a6", table, LootHash);

        static ParticipantResult Result(ManifestOutcome outcome = ManifestOutcome.Success, int sequence = 8812, int elapsed = 1200000,
            ObjectiveResult[] objectives = null, params string[] dead) =>
            new ParticipantResult(outcome, sequence, elapsed, objectives ?? new ObjectiveResult[0], dead);

        static LedgerCatalogue Definitions(params LedgerItemDefinition[] extra) => new LedgerCatalogue(new[]
        {
            new LedgerItemDefinition(Medkit, true, true, null), new LedgerItemDefinition(Kit, true, true, null),
            new LedgerItemDefinition(Rifle, false, false, EquipSlot.Weapon), new LedgerItemDefinition(Vest, false, false, EquipSlot.Armor),
            new LedgerItemDefinition(Boots, false, false, EquipSlot.Utility),
        }.Concat(extra));

        static OperativeSnapshot Op(string id, int slot, (string, string)[] items, (string, int)[] stacks, string weapon = null, string armor = null, string utility = null) =>
            new OperativeSnapshot(id, slot, items.Select(i => new SnapshotItem(i.Item1, i.Item2)).ToList(), stacks.Select(s => new SnapshotStack(s.Item1, s.Item2)).ToList(),
                weapon, armor, utility);

        static RuntimeEntry E(string id, string def, int qty) => new RuntimeEntry(id, def, qty);

        /// <summary>02-full's events, in the only order loot-first consumption allows (an owned use before the collects).</summary>
        static ParticipantFacts FullFacts(LedgerCatalogue catalogue = null, Action<MissionItemLedger> after = null)
        {
            var ledger = new MissionItemLedger(catalogue ?? Definitions());
            ledger.AddParticipant("JG", new[]
            {
                Op("C", 0, new[] { ("2k", Rifle), ("2m", Vest) }, new[] { (Medkit, 3) }, weapon: "2k", armor: "2m"),
                Op("D", 1, new[] { ("2n", Rifle) }, new (string, int)[0], weapon: "2n"),
            });
            ledger.RegisterContainer(0, new[] { new ContainerEntry("e1", Medkit, 2), new ContainerEntry("e2", Medkit, 1) });
            ledger.RegisterContainer(2, new[] { new ContainerEntry("g1", Boots, 1) });
            Assert.That(ledger.Use("C", Medkit), Is.EqualTo(LedgerEventResult.Applied));
            ledger.Collect("C", 0, "e1", 2);
            ledger.Collect("C", 0, "e2", 1);
            Assert.That(ledger.Use("C", Medkit), Is.EqualTo(LedgerEventResult.Applied));
            ledger.Collect("D", 2, "g1", 1);
            Assert.That(ledger.Equip("D", "g1"), Is.EqualTo(LedgerEventResult.Applied));
            after?.Invoke(ledger);
            return ledger.Freeze("JG", new[]
            {
                new OperativeRuntimeState("C", new[] { E("2k", Rifle, 1), E("2m", Vest, 1), E("s1", Medkit, 3), E("s2", Medkit, 1) }, new RuntimeEquipment("2k", "2m", null)),
                new OperativeRuntimeState("D", new[] { E("2n", Rifle, 1), E("g1", Boots, 1) }, new RuntimeEquipment("2n", null, "g1")),
            }, new[] { new ContainerRuntimeState(0, new RuntimeEntry[0]), new ContainerRuntimeState(2, new RuntimeEntry[0]) });
        }

        static ParticipantResult FullResult() => Result(objectives: new[] { new ObjectiveResult("intel", ObjectiveStateKind.Failed), new ObjectiveResult("extract", ObjectiveStateKind.Completed) },
            dead: new[] { "D" });

        static ParticipantFacts MinimalFacts(bool mismatch = false)
        {
            var ledger = new MissionItemLedger(Definitions());
            ledger.AddParticipant("JG", new[] { Op("C", 0, new (string, string)[0], new (string, int)[0]) });
            if (mismatch) ledger.ReportIntegrityFailure("JG", "server_detected");
            return ledger.Freeze("JG", new[] { new OperativeRuntimeState("C", new RuntimeEntry[0], new RuntimeEquipment(null, null, null)) }, new ContainerRuntimeState[0]);
        }

        [Test]
        public void Builds_02_full_from_facts()
        {
            var facts = FullFacts();
            Assert.That(facts.Integrity, Is.EqualTo(ManifestIntegrity.Ok), string.Join(",", facts.FailureCodes));
            var built = ManifestBuilder.Build(Header(), FullResult(), facts);
            Assert.That(Encoding.UTF8.GetString(built.CanonicalBytes), Is.EqualTo(Vector("02-full.canonical.json")));
            Assert.That(built.ManifestHash + "\n", Is.EqualTo(Vector("02-full.hash.txt")));
        }

        [Test]
        public void Builds_13_ledger_mismatch()
        {
            var built = ManifestBuilder.Build(Header(), Result(ManifestOutcome.Defeat), MinimalFacts(mismatch: true));
            Assert.That(Encoding.UTF8.GetString(built.CanonicalBytes), Is.EqualTo(Vector("13-ledger-mismatch.canonical.json")));
        }

        [Test]
        public void Every_outcome_builds()
        {
            Assert.That(Enum.GetNames(typeof(ManifestOutcome)), Is.EqualTo(new[] { "Success", "Defeat", "Abandoned", "Disconnected", "Expired" }));
            foreach (ManifestOutcome outcome in Enum.GetValues(typeof(ManifestOutcome)))
                StringAssert.Contains("\"outcome\":\"" + ManifestWords.Of(outcome) + "\"", Encoding.UTF8.GetString(ManifestBuilder.Build(Header(), Result(outcome), MinimalFacts()).CanonicalBytes));
        }

        [Test]
        public void The_same_inputs_give_the_same_bytes() =>
            Assert.That(ManifestBuilder.Build(Header(), FullResult(), FullFacts()).CanonicalBytes, Is.EqualTo(ManifestBuilder.Build(Header(), FullResult(), FullFacts()).CanonicalBytes));

        [Test]
        public void An_unfrozen_participant_cannot_be_built()
        {
            Assert.That(typeof(ParticipantFacts).GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(BuiltManifest).GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
        }

        [Test]
        public void Events_after_freeze_do_not_change_the_bytes()
        {
            var ledger = new MissionItemLedger(Definitions());
            ledger.AddParticipant("JG", new[] { Op("C", 0, new (string, string)[0], new[] { (Kit, 2) }) });
            ledger.RegisterContainer(0, new[] { new ContainerEntry("e1", Boots, 1) });
            var states = new[] { new OperativeRuntimeState("C", new[] { E("s", Kit, 2) }, new RuntimeEquipment(null, null, null)) };
            var facts = ledger.Freeze("JG", states, new[] { new ContainerRuntimeState(0, new[] { E("e1", Boots, 1) }) });
            var before = ManifestBuilder.Build(Header(), Result(), facts).CanonicalBytes;
            ledger.Use("C", Kit);
            ledger.Collect("C", 0, "e1", 1);
            ledger.Equip("C", "e1");
            Assert.That(ManifestBuilder.Build(Header(), Result(), ledger.Freeze("JG", states, new ContainerRuntimeState[0])).CanonicalBytes, Is.EqualTo(before));
        }

        static string CodeOf(TestDelegate build) => Assert.Throws<ManifestFormatException>(build).Code;

        [Test]
        public void A_refused_string_fails_closed()
        {
            var facts = MinimalFacts();
            Assert.That(CodeOf(() => ManifestBuilder.Build(Header(), Result(objectives: new[] { new ObjectiveResult("Extract", ObjectiveStateKind.Active) }), facts)), Is.EqualTo(ManifestCodes.BadValue));
            Assert.That(CodeOf(() => ManifestBuilder.Build(Header(), Result(objectives: new[] { new ObjectiveResult("extract\n", ObjectiveStateKind.Active) }), facts)), Is.EqualTo(ManifestCodes.BadValue));
            Assert.That(CodeOf(() => ManifestBuilder.Build(Header(revision: "r1\n"), Result(), facts)), Is.EqualTo(ManifestCodes.BadValue));
            Assert.That(CodeOf(() => ManifestBuilder.Build(Header(revision: new string('r', 65)), Result(), facts)), Is.EqualTo(ManifestCodes.BadValue));
            Assert.That(CodeOf(() => ManifestBuilder.Build(Header(revision: new string('r', 129)), Result(), facts)), Is.EqualTo(ManifestCodes.OutOfBounds));
            Assert.That(CodeOf(() => ManifestBuilder.Build(Header(attempt: Attempt.ToUpperInvariant()), Result(), facts)), Is.EqualTo(ManifestCodes.NonCanonicalId));
            Assert.That(CodeOf(() => ManifestBuilder.Build(Header(table: 0), Result(), facts)), Is.EqualTo(ManifestCodes.OutOfBounds));
            Assert.That(CodeOf(() => ManifestBuilder.Build(Header(), Result(sequence: -1), facts)), Is.EqualTo(ManifestCodes.NotInteger));
            Assert.That(CodeOf(() => ManifestBuilder.Build(Header(), Result(elapsed: ResultManifestV1.MaxElapsedMs + 1), facts)), Is.EqualTo(ManifestCodes.OutOfBounds));
            Assert.That(CodeOf(() => ManifestBuilder.Build(Header(), Result(objectives: new[] { new ObjectiveResult("a", ObjectiveStateKind.Active), new ObjectiveResult("a", ObjectiveStateKind.Failed) }), facts)),
                Is.EqualTo(ManifestCodes.DuplicateEntry));
            Assert.That(CodeOf(() => ManifestBuilder.Build(Header(), Result(), BadDefinitionFacts())), Is.EqualTo(ManifestCodes.BadValue));
        }

        /// <summary>A crafted catalogue lets a definition id the format refuses reach the facts through the ledger (a consumed loot line).</summary>
        static ParticipantFacts BadDefinitionFacts()
        {
            var ledger = new MissionItemLedger(Definitions(new LedgerItemDefinition("item medkit", true, true, null)));
            ledger.AddParticipant("JG", new[] { Op("C", 0, new (string, string)[0], new[] { (Kit, 1) }) });
            ledger.RegisterContainer(0, new[] { new ContainerEntry("e1", "item medkit", 2) });
            ledger.Collect("C", 0, "e1", 2);
            ledger.Use("C", "item medkit");
            return ledger.Freeze("JG", new[] { new OperativeRuntimeState("C", new[] { E("s", Kit, 1), E("t", "item medkit", 1) }, new RuntimeEquipment(null, null, null)) },
                new[] { new ContainerRuntimeState(0, new RuntimeEntry[0]) });
        }

        [Test]
        public void A_known_mismatch_never_builds_as_ok()
        {
            var facts = MinimalFacts(mismatch: true);
            StringAssert.Contains("\"integrity\":\"ledgerMismatch\"", Encoding.UTF8.GetString(ManifestBuilder.Build(Header(), Result(), facts).CanonicalBytes));
            var outcome = ManifestBuilder.BuildForPublication(Header(), Result(), facts, new[] { Op("C", 0, new (string, string)[0], new (string, int)[0]) });
            Assert.That(outcome.Kind, Is.EqualTo(ManifestBuildKind.Normal));
            Assert.That(outcome.Manifest.Manifest.Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));
            Assert.That(outcome.Diagnostics, Is.EqualTo(new[] { "server_detected" }));
        }

        static readonly OperativeSnapshot[] TwoOperatives =
        {
            Op("C", 0, new[] { ("2k", Rifle) }, new[] { (Kit, 2) }, weapon: "2k"),
            Op("D", 1, new[] { ("2n", Vest) }, new (string, int)[0], armor: "2n"),
        };

        [Test]
        public void The_fallback_restates_the_snapshot()
        {
            var built = ManifestBuilder.BuildLedgerMismatchFallback(Header(),
                Result(ManifestOutcome.Defeat, 5, 60000, new[] { new ObjectiveResult("extract", ObjectiveStateKind.Active) }, "D"), "JG", TwoOperatives);
            var expected = "{\"accountId\":\"JG\",\"attemptId\":\"" + Attempt + "\",\"buildRevision\":\"r1\",\"catalogueVersion\":\"" + Catalogue + "\",\"formatVersion\":1," +
                "\"integrity\":\"ledgerMismatch\",\"layoutHash\":\"cf2e14dece9cc2a6\",\"lootHash\":\"" + LootHash + "\",\"lootTableVersion\":1," +
                "\"objectives\":[{\"objectiveId\":\"extract\",\"state\":\"active\"}],\"operatives\":[" +
                "{\"alive\":true,\"collected\":[],\"consumed\":[],\"equipment\":[{\"itemId\":\"2k\",\"slot\":\"weapon\"}],\"held\":[{\"itemId\":\"2k\",\"origin\":\"owned\"}," +
                "{\"definitionId\":\"item.kit\",\"origin\":\"owned\",\"quantity\":2}],\"operativeId\":\"C\",\"slot\":0}," +
                "{\"alive\":false,\"collected\":[],\"consumed\":[],\"equipment\":[{\"itemId\":\"2n\",\"slot\":\"armor\"}],\"held\":[{\"itemId\":\"2n\",\"origin\":\"owned\"}]," +
                "\"operativeId\":\"D\",\"slot\":1}],\"outcome\":\"defeat\",\"outcomeSequence\":5,\"processSessionId\":\"" + Session + "\",\"serverElapsedMs\":60000}";
            Assert.That(Encoding.UTF8.GetString(built.CanonicalBytes), Is.EqualTo(expected));
            Assert.Throws<ArgumentException>(() => ManifestBuilder.BuildLedgerMismatchFallback(Header(), Result(), "JH", TwoOperatives));
        }

        [Test]
        public void BuildForPublication_uses_the_fallback_only_after_a_format_failure()
        {
            var good = ManifestBuilder.BuildForPublication(Header(), FullResult(), FullFacts(), new OperativeSnapshot[0]);
            Assert.That(good.Kind, Is.EqualTo(ManifestBuildKind.Normal));
            Assert.That(good.Diagnostics, Is.Empty);

            var snapshot = new[] { Op("C", 0, new (string, string)[0], new[] { (Kit, 1) }) };
            var rescued = ManifestBuilder.BuildForPublication(Header(), Result(), BadDefinitionFacts(), snapshot);
            Assert.That(rescued.Kind, Is.EqualTo(ManifestBuildKind.LedgerMismatchFallback));
            Assert.That(rescued.Diagnostics, Is.EqualTo(new[] { "normal:BadValue" }));
            Assert.That(rescued.Manifest.Manifest.Integrity, Is.EqualTo(ManifestIntegrity.LedgerMismatch));

            var failed = ManifestBuilder.BuildForPublication(Header(), Result(objectives: new[] { new ObjectiveResult("Bad", ObjectiveStateKind.Active) }), MinimalFacts(),
                new[] { Op("C", 0, new (string, string)[0], new (string, int)[0]) });
            Assert.That(failed.Kind, Is.EqualTo(ManifestBuildKind.Failed));
            Assert.That(failed.Manifest, Is.Null);
            Assert.That(failed.Diagnostics, Is.EqualTo(new[] { "normal:BadValue", "fallback:BadValue" }));
        }

        [Test]
        public void A_parse_proves_the_format_only()
        {
            // Held items no snapshot ever had, and loot nobody collected: well-formed, so it parses. Only the backend's
            // ManifestValidator would refuse it (V10, V9); a local parse is never acceptance.
            var json = Vector("01-minimal.canonical.json").Replace("\"held\":[]",
                "\"held\":[{\"containerIndex\":9,\"line\":9,\"origin\":\"loot\",\"quantity\":99},{\"itemId\":\"zz\",\"origin\":\"owned\"}]");
            Assert.That(ManifestCodec.Parse(Encoding.UTF8.GetBytes(json)).Ok, Is.True);
        }

        [Test]
        public void Facts_only()
        {
            var forbidden = new[] { "seed", "salt", "xp", "experience", "reward", "energy", "insurance", "refund", "retention" };
            foreach (var type in new[] { typeof(ManifestHeader), typeof(ParticipantResult), typeof(ResultManifestV1), typeof(OperativeFact), typeof(BuiltManifest), typeof(ManifestBuildOutcome), typeof(ParticipantFacts), typeof(OperativeFacts) })
                foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                    Assert.That(forbidden.Any(f => member.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0), Is.False, type.Name + "." + member.Name);
            const int seed = 0x5EED1234; // held by the harness, never given to the builder
            var text = Encoding.UTF8.GetString(ManifestBuilder.Build(Header(), FullResult(), FullFacts()).CanonicalBytes);
            Assert.That(text.Contains("5EED") || text.Contains(seed.ToString()) || text.IndexOf("seed", StringComparison.OrdinalIgnoreCase) >= 0, Is.False);
        }

        [Test]
        public void Conservation_holds_for_every_built_manifest()
        {
            var made = new List<Object>();
            try
            {
                var catalogue = LedgerSimulationTests.UnityCatalogue(made);
                var ledgerCatalogue = MissionItemLedgerTests.TestCatalogue();
                var actions = 0;
                for (var seed = 1; seed <= 200; seed++)
                {
                    var sim = LedgerSimulationTests.RandomMission(seed, catalogue, ledgerCatalogue, ref actions);
                    var facts = sim.Freeze();
                    var built = ManifestBuilder.Build(Header(), Result(dead: sim.Snapshot.Take(1).Select(s => s.OperativeId).ToArray()), facts);
                    var parsed = ManifestCodec.Parse(built.CanonicalBytes);
                    Assert.That(parsed.Ok, Is.True, "seed " + seed);
                    Assert.That(parsed.Manifest.Integrity, Is.EqualTo(ManifestIntegrity.Ok), "seed " + seed);
                    Conservation.Check(sim.Snapshot, parsed.Manifest, sim.Pool, ledgerCatalogue);
                }
            }
            finally
            {
                foreach (var o in made) Object.DestroyImmediate(o);
            }
        }
    }
}
