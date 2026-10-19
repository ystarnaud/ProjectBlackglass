using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;
using static Blackglass.Tests.ContractVectors;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    /// <summary>
    /// The shared ledger-v1 scenarios (Backend/contracts/ledger-v1; the backend's LedgerScenarioTests judges the same files with
    /// its real validator). Unity replays each scenario through its real inventory code and the ledger, freezes, builds, and
    /// must produce exactly the canonical bytes of the hand-written expected manifest. A mismatch is investigated against the
    /// hand-written manifest, never fixed by copying Unity's output into the scenario.
    /// </summary>
    public sealed class LedgerScenarioVectorTests
    {
        static string Folder => Path.Combine(ProjectRoot, "Backend", "contracts", "ledger-v1");

        readonly List<Object> made = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in made) Object.DestroyImmediate(o);
            made.Clear();
        }

        static IEnumerable<string> Scenarios() => Directory.GetFiles(Path.Combine(ProjectRoot, "Backend", "contracts", "ledger-v1"), "*.json")
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal);

        [Test]
        public void There_are_eight_scenarios() => Assert.That(Scenarios().Count(), Is.EqualTo(8));

        [TestCaseSource(nameof(Scenarios))]
        public void Unity_builds_exactly_the_expected_manifest(string name)
        {
            var scenario = Load("ledger-v1", name);
            var golden = Load("catalogue", "golden-catalogue.json");
            var (catalogue, ledgerCatalogue) = Catalogues(golden);
            var snapshotJson = Obj(scenario["snapshot"]);
            var account = Str(snapshotJson["accountId"]);
            var snapshot = Arr(snapshotJson["operatives"]).Select(Obj).Select(o =>
            {
                var bag = Obj(o["bag"]);
                var equipment = Obj(o["equipment"]);
                return new OperativeSnapshot(Str(o["operativeId"]), Int(o["slot"]),
                    Arr(bag["instances"]).Select(Obj).Select(i => new SnapshotItem(Str(i["itemId"]), Str(i["definitionId"]))).ToList(),
                    Arr(bag["stacks"]).Select(Obj).Select(s => new SnapshotStack(Str(s["definitionId"]), Int(s["quantity"]))).ToList(),
                    (string)equipment["weapon"], (string)equipment["armor"], (string)equipment["utility"]);
            }).ToList();
            var capacity = scenario.ContainsKey("bagCapacity") ? Int(scenario["bagCapacity"]) : Int(Obj(golden["inventory"])["bagCapacity"]);
            var sim = new LedgerSimulation(catalogue, ledgerCatalogue, account, snapshot, capacity);

            var pool = Arr(scenario["pool"]).Select(Obj).Select(l => new PoolLine(Int(l["containerIndex"]), Int(l["line"]), Str(l["definitionId"]), Int(l["quantity"]))).ToList();
            foreach (var container in pool.GroupBy(l => l.ContainerIndex).OrderBy(g => g.Key))
                sim.FillContainer(container.Key, container.OrderBy(l => l.Line).Select(l => (l.DefinitionId, l.Quantity)).ToList());
            Assert.That(sim.Pool.Select(Line), Is.EqualTo(pool.OrderBy(l => l.ContainerIndex).ThenBy(l => l.Line).Select(Line)), "the fill leaves one entry per pool line");

            var attempt = Obj(scenario["attempt"]);
            var tableVersion = Int(attempt["lootTableVersion"]);
            var expected = ExpectedCanonicalBytes(name);
            var expectedManifest = ManifestCodec.Parse(expected).Manifest;
            Assert.That(LootPoolHash.Compute(tableVersion, sim.Pool), Is.EqualTo(expectedManifest.LootHash));

            var fallback = scenario.ContainsKey("build") && Str(scenario["build"]) == "fallback";
            if (!fallback)
                foreach (var e in Arr(scenario["events"]).Select(Obj))
                {
                    var op = Str(e["operativeId"]);
                    switch (Str(e["op"]))
                    {
                        case "collect":
                            sim.Collect(op, Int(e["containerIndex"]), Int(e["line"]), Int(e["quantity"]));
                            break;
                        case "use":
                            Assert.That(sim.Use(op, Str(e["definitionId"])), Is.True, name + ": a use Unity accepts");
                            break;
                        case "equip":
                            var item = Obj(e["item"]);
                            var entryId = item.ContainsKey("itemId") ? Str(item["itemId"]) : sim.EntryIdOf(Int(item["containerIndex"]), Int(item["line"]));
                            Assert.That(sim.Equip(op, entryId), Is.True, name + ": an equip Unity accepts");
                            break;
                        case "rogueAdd":
                            sim.RogueAdd(op, Str(e["definitionId"]), Int(e["quantity"]));
                            continue; // its mismatch is expected at the freeze
                        default:
                            Assert.Fail(name + ": unknown event " + Str(e["op"]));
                            break;
                    }
                    sim.AssertConsistent();
                }

            var header = new ManifestHeader(Str(attempt["attemptId"]), account, Str(attempt["processSessionId"]), Str(attempt["buildRevision"]),
                Str(snapshotJson["catalogueVersion"]), Str(attempt["layoutHash"]), tableVersion, LootPoolHash.Compute(tableVersion, sim.Pool));
            var resultJson = Obj(scenario["result"]);
            ManifestWords.TryOutcome(Str(resultJson["outcome"]), out var outcome);
            var objectives = Arr(scenario["objectives"]).Select(Obj).Select(o =>
            {
                ManifestWords.TryState(Str(o["state"]), out var state);
                return new ObjectiveResult(Str(o["objectiveId"]), state);
            }).ToList();
            var result = new ParticipantResult(outcome, Int(resultJson["outcomeSequence"]), Int(resultJson["serverElapsedMs"]), objectives,
                Arr(resultJson["dead"]).Select(Str).ToList());
            var built = fallback
                ? ManifestBuilder.BuildLedgerMismatchFallback(header, result, account, snapshot)
                : ManifestBuilder.Build(header, result, sim.Freeze());
            Assert.That(built.CanonicalBytes, Is.EqualTo(expected), name + ":\n" + System.Text.Encoding.UTF8.GetString(built.CanonicalBytes));
        }

        static string Line(PoolLine l) => $"{l.ContainerIndex},{l.Line},{l.DefinitionId},{l.Quantity}";

        /// <summary>The scenario's expectedManifest member, read with the strict reader and canonicalized by the codec.</summary>
        static byte[] ExpectedCanonicalBytes(string name)
        {
            Assert.That(StrictJsonReader.TryRead(File.ReadAllBytes(Path.Combine(Folder, name)), out var root), Is.True);
            var manifest = ((JsonObject)root).Members.Single(m => m.Name == "expectedManifest").Value;
            var parsed = ManifestCodec.Parse(CanonicalJsonWriter.ToBytes(manifest));
            Assert.That(parsed.Ok, Is.True, name + ": the expected manifest is format v1 (" + parsed.Code + ")");
            return parsed.CanonicalBytes;
        }

        (ItemCatalogue, LedgerCatalogue) Catalogues(Dictionary<string, object> golden)
        {
            var items = Arr(golden["items"]).Select(Obj).ToList();
            var unity = items.Select(i =>
            {
                var category = (ItemCategory)Enum.Parse(typeof(ItemCategory), Str(i["category"]), true);
                var slot = (ItemSlot)Enum.Parse(typeof(ItemSlot), Str(i["slot"]), true);
                var definition = ItemDefinition.Create(Str(i["id"]), Str(i["id"]), category, slot, Int(i["maxStack"]), healAmount: Int(i["healAmount"]));
                made.Add(definition);
                return definition;
            }).ToArray();
            var catalogue = ItemCatalogue.Create(unity);
            made.Add(catalogue);
            var ledger = new LedgerCatalogue(items.Select(i =>
            {
                var slot = Str(i["slot"]);
                EquipSlot? equip = slot == "none" ? (EquipSlot?)null : ManifestWords.TrySlot(slot, out var s) ? s : throw new InvalidDataException(slot);
                return new LedgerItemDefinition(Str(i["id"]), Bool(i["stackable"]), Str(i["category"]) == "consumable", equip);
            }));
            return (catalogue, ledger);
        }
    }
}
