using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Blackglass.Backend.Core;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.BackendTools
{
    /// <summary>
    /// Writes Backend/contracts/attempt-v1 (Stage 12 Task 7, gate B25 test 9): attempts Unity actually generates and plays,
    /// for the backend to judge with its real ready path and ManifestValidator. Each fixture runs the real pieces: AttemptStart
    /// over a register answer, the server fixture's generation steps (MissionGenerator, ObjectivePlacer, SecurityPlacer,
    /// LootPlacer with the shipped table; three fixtures force their containers instead), the one-entry-per-roll fill
    /// (LootContainer.InitializeForAttempt), the snapshot inventory (InventorySession.FromSnapshot), the item moves Unity's
    /// UnitItems make (ItemTransfer, ItemInventory.Remove, InventorySession.EquipInMission) reported to an AttemptSession, the
    /// freeze, ReadyReportBuilder and the manifest. Per fixture: NN-name.json (the description, canonical JSON), NN-name.ready.json
    /// (JsonUtility's bytes, as Stage 13 sends them) and NN-name.manifest.json (the canonical manifest). The 9N-invalid fixtures
    /// change one thing in a valid one and record the refusal the backend must give. Deterministic: AttemptFixtureExportTests
    /// fails while the committed files differ from a fresh export. Menu: Blackglass > Backend > Export Attempt Fixtures.
    /// </summary>
    public static class AttemptFixtureExport
    {
        public static string Folder => Path.Combine(CatalogueExporter.ProjectRoot, "Backend", "contracts", "attempt-v1");
        public const string BuildRevision = "fixture";
        public const string AccountId = "JG";
        public const int ElapsedMs = 540000;
        const string Medkit = "item.medkit", Marksman = "item.marksman-rifle", Service = "item.service-rifle", Vest = "item.light-vest", Boots = "item.servo-boots";
        const string Kestrel = "a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02", Darius = "6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301";

        [MenuItem("Blackglass/Backend/Export Attempt Fixtures")]
        static void ExportMenu()
        {
            if (TryExport(out var message)) Debug.Log(message);
            else Debug.LogError(message);
        }

        /// <summary>Unity -batchmode -executeMethod entry: exit 0 after writing, 1 when the export is refused.</summary>
        public static void ExportFromCommandLine()
        {
            var ok = TryExport(out var message);
            if (ok) Debug.Log(message);
            else Debug.LogError(message);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        static bool TryExport(out string message)
        {
            var errors = new List<string>();
            var files = Build(errors);
            if (files == null)
            {
                message = "Attempt fixture export refused; nothing was written:\n" + string.Join("\n", errors);
                return false;
            }
            Directory.CreateDirectory(Folder);
            foreach (var stale in Directory.GetFiles(Folder, "*.json"))
                if (!files.ContainsKey(Path.GetFileName(stale)))
                    File.Delete(stale);
            foreach (var file in files)
                File.WriteAllBytes(Path.Combine(Folder, file.Key), file.Value);
            message = $"Attempt fixtures exported to {Folder}: {files.Count} files.";
            return true;
        }

        // ---- the fixtures ----

        sealed class Spec
        {
            public string Name, Covers;
            public int Seed = ServerFixture.Seed;
            public LootItemPlan[][] Forced;
            public Action<Run> Script = _ => { };
            public ManifestOutcome Outcome = ManifestOutcome.Success;
            public string[] Dead = new string[0];
            public string[] ExpectedManifestErrors = new string[0];
        }

        static LootItemPlan Roll(string id, int quantity) => new LootItemPlan(id, quantity);

        static IEnumerable<Spec> Specs() => new[]
        {
            new Spec
            {
                Name = "01-forced-duplicate-medkits",
                Covers = "B25 test 1: two medkit rolls (1 and 2) in one container are two entries and two pool lines; C collects line 0, D line 1, C uses one (loot first)",
                Forced = new[] { new[] { Roll(Medkit, 1), Roll(Medkit, 2) } },
                Script = r => { r.Collect("C", 0, 0, 1); r.Collect("D", 0, 1, 2); r.Use("C", Medkit); },
            },
            new Spec
            {
                Name = "02-forced-mixed",
                Covers = "stackable and non-stackable rolls in three containers; D equips a collected rifle, C a collected vest (loot equipment)",
                Forced = new[] { new[] { Roll(Medkit, 2), Roll(Marksman, 1) }, new[] { Roll(Vest, 1) }, new[] { Roll(Medkit, 1), Roll(Boots, 1) } },
                Script = r => { r.Collect("D", 0, 1, 1); r.EquipLine("D", 0, 1); r.Collect("C", 1, 0, 1); r.EquipLine("C", 1, 0); r.Collect("C", 2, 0, 1); },
            },
            new Spec
            {
                Name = "03-seeded-collect-and-use",
                Covers = "the server fixture (seed 1, two medkit lines in container 1): C takes container 1 and uses two medkits; D takes and equips boots",
                Seed = 1,
                Script = r => { r.CollectAll("C", 1); r.Use("C", Medkit); r.Use("C", Medkit); r.CollectAll("D", 2, 1); r.EquipFirstCollected("D"); },
            },
            new Spec
            {
                Name = "04-seeded-defeat",
                Covers = "a generated mission ending in defeat: C takes container 0 and dies holding it, D uses an owned medkit",
                Seed = 2,
                Script = r => { r.CollectAll("C", 0); r.Use("D", Medkit); },
                Outcome = ManifestOutcome.Defeat,
                Dead = new[] { "C" },
            },
            new Spec
            {
                Name = "05-seeded-owned-only",
                Covers = "a generated mission where nothing is collected: two owned medkits used, the owned vest equipped",
                Seed = 3,
                Script = r => { r.Use("C", Medkit); r.Use("C", Medkit); r.EquipOwned("C", "2n"); },
            },
            new Spec
            {
                Name = "06-seeded-ledger-mismatch",
                Covers = "a generated mission with a bag change no event reported (a wiring bug): the manifest says ledgerMismatch and the backend quarantines it",
                Seed = 4,
                Script = r => { r.CollectAll("C", 0); r.Rogue("D", Medkit, 1); },
                ExpectedManifestErrors = new[] { "V14.integrity" },
            },
            new Spec
            {
                Name = "07-forced-partial-and-complete",
                Covers = "B25 test 2: C collects one of line 1 (of 2) and all of line 0; separate provenance per line",
                Forced = new[] { new[] { Roll(Medkit, 1), Roll(Medkit, 2) } },
                Script = r => { r.Collect("C", 0, 1, 1); r.Collect("C", 0, 0, 1); },
            },
        };

        static string AttemptId(int index) => $"0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e{index:x2}";
        static string SessionId(int index) => $"0192f1a5-0000-7000-8000-0000000000{index:x2}";

        static LoadoutSnapshotDto Snapshot(string catalogueVersion) => new LoadoutSnapshotDto
        {
            accountId = AccountId,
            inventoryVersion = 3,
            catalogueVersion = catalogueVersion,
            operatives = new[]
            {
                new SnapshotOperativeDto
                {
                    slot = 0, operativeId = "C", definitionId = Kestrel, experience = 0, rank = 1, choices = new string[0],
                    bag = new SnapshotBagDto
                    {
                        instances = new[] { new SnapshotInstanceDto { itemId = "2k", definitionId = Marksman }, new SnapshotInstanceDto { itemId = "2n", definitionId = Vest } },
                        stacks = new[] { new SnapshotStackDto { definitionId = Medkit, quantity = 2 } },
                    },
                    equipment = new EquipmentSlotsDto { weapon = "2k" },
                },
                new SnapshotOperativeDto
                {
                    slot = 1, operativeId = "D", definitionId = Darius, experience = 0, rank = 1, choices = new string[0],
                    bag = new SnapshotBagDto
                    {
                        instances = new[] { new SnapshotInstanceDto { itemId = "2m", definitionId = Service } },
                        stacks = new[] { new SnapshotStackDto { definitionId = Medkit, quantity = 1 } },
                    },
                    equipment = new EquipmentSlotsDto { weapon = "2m" },
                },
            },
        };

        /// <summary>One fixture's play: Unity's real item moves, each reported to the session as UnitItems' detail event would.</summary>
        sealed class Run
        {
            public InventorySession Inventory;
            public ItemCatalogue Catalogue;
            public readonly List<LootContainer> Containers = new List<LootContainer>();
            public readonly List<IReadOnlyList<ItemEntry>> Entries = new List<IReadOnlyList<ItemEntry>>();
            public AttemptSession Session;
            public readonly List<JsonNode> Events = new List<JsonNode>();
            public readonly List<string> Errors = new List<string>();
            readonly Dictionary<string, List<string>> collectedItems = new Dictionary<string, List<string>>();

            ItemInventory Bag(string operativeId) => Inventory.Working.Loadout(operativeId).Bag;

            void Expect(LedgerEventResult result, string what)
            {
                if (result != LedgerEventResult.Applied) Errors.Add(what + ": the ledger answered " + result);
            }

            public void Collect(string operativeId, int container, int line, int quantity)
            {
                if (container >= Containers.Count || line >= Entries[container].Count) { Errors.Add($"collect: no line ({container},{line})"); return; }
                var id = Entries[container][line].InstanceId;
                var moved = ItemTransfer.Move(Containers[container].Contents, Bag(operativeId), id, quantity, Catalogue).Moved;
                if (moved < 1) { Errors.Add($"collect ({container},{line}): nothing moved"); return; }
                Expect(Session.OnCollected(operativeId, container, id, moved), "collect");
                if (Catalogue.Find(Entries[container][line].DefinitionId).IsStackable == false)
                {
                    if (!collectedItems.TryGetValue(operativeId, out var list)) collectedItems[operativeId] = list = new List<string>();
                    list.Add(id);
                }
                Events.Add(JsonObject.Of(("op", new JsonString("collect")), ("operativeId", new JsonString(operativeId)), ("containerIndex", JsonNumber.FromInt(container)),
                    ("line", JsonNumber.FromInt(line)), ("quantity", JsonNumber.FromInt(moved))));
            }

            /// <summary>Every line of a container still holding something, in line order (UnitItems.TryCollect with no entry), up to <paramref name="limit"/> lines.</summary>
            public void CollectAll(string operativeId, int container, int limit = int.MaxValue)
            {
                if (container >= Containers.Count) { Errors.Add($"collect: no container {container}"); return; }
                for (var line = 0; line < Entries[container].Count && limit > 0; line++, limit--)
                {
                    var entry = Containers[container].Contents.Find(Entries[container][line].InstanceId);
                    if (entry != null) Collect(operativeId, container, line, entry.Quantity);
                }
            }

            public void Use(string operativeId, string definitionId)
            {
                var entry = Bag(operativeId).Entries.FirstOrDefault(e => e.DefinitionId == definitionId);
                if (entry == null || Bag(operativeId).Remove(entry.InstanceId, 1) != 1) { Errors.Add($"use {definitionId}: nothing held"); return; }
                Expect(Session.OnUsed(operativeId, definitionId), "use");
                Events.Add(JsonObject.Of(("op", new JsonString("use")), ("operativeId", new JsonString(operativeId)), ("definitionId", new JsonString(definitionId))));
            }

            public void Equip(string operativeId, string entryId)
            {
                if (!Inventory.EquipInMission(operativeId, entryId).Ok) { Errors.Add("equip: refused"); return; }
                Expect(Session.OnEquipped(operativeId, entryId), "equip");
            }

            public void EquipOwned(string operativeId, string itemId)
            {
                Equip(operativeId, itemId);
                Events.Add(JsonObject.Of(("op", new JsonString("equip")), ("operativeId", new JsonString(operativeId)), ("item", JsonObject.Of(("itemId", new JsonString(itemId))))));
            }

            public void EquipLine(string operativeId, int container, int line)
            {
                Equip(operativeId, Entries[container][line].InstanceId);
                Events.Add(JsonObject.Of(("op", new JsonString("equip")), ("operativeId", new JsonString(operativeId)),
                    ("item", JsonObject.Of(("containerIndex", JsonNumber.FromInt(container)), ("line", JsonNumber.FromInt(line))))));
            }

            public void EquipFirstCollected(string operativeId)
            {
                if (!collectedItems.TryGetValue(operativeId, out var list) || list.Count == 0) { Errors.Add("equip: nothing collected"); return; }
                for (var c = 0; c < Entries.Count; c++)
                    for (var l = 0; l < Entries[c].Count; l++)
                        if (Entries[c][l].InstanceId == list[0]) { EquipLine(operativeId, c, l); return; }
            }

            /// <summary>A bag change with no event: what a wiring bug would look like.</summary>
            public void Rogue(string operativeId, string definitionId, int quantity)
            {
                Bag(operativeId).Add(Catalogue.Find(definitionId), quantity);
                Events.Add(JsonObject.Of(("op", new JsonString("rogueAdd")), ("operativeId", new JsonString(operativeId)), ("definitionId", new JsonString(definitionId)),
                    ("quantity", JsonNumber.FromInt(quantity))));
            }
        }

        /// <summary>Every file of a fresh export by name, or null with every problem in errors.</summary>
        public static SortedDictionary<string, byte[]> Build(List<string> errors)
        {
            var sources = CatalogueSources.LoadProject();
            var catalogueBytes = CatalogueExporter.Export(sources, errors);
            if (catalogueBytes == null) return null;
            var catalogueVersion = CanonicalCatalogueWriter.VersionOf(catalogueBytes);
            var ledgerCatalogue = ItemLedgerMapping.Catalogue(sources.Items);
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            var built = new Dictionary<string, (ReadyRequest Ready, BuiltManifest Manifest, JsonNode Snapshot)>();
            var index = 0;
            foreach (var spec in Specs())
            {
                index++;
                var problem = Export(spec, index, sources, catalogueVersion, ledgerCatalogue, files, built);
                if (problem != null) errors.Add(spec.Name + ": " + problem);
            }
            if (errors.Count == 0) Invalid(files, built, catalogueVersion);
            return errors.Count == 0 ? files : null;
        }

        static string Export(Spec spec, int index, CatalogueSources sources, string catalogueVersion, LedgerCatalogue ledgerCatalogue,
            SortedDictionary<string, byte[]> files, Dictionary<string, (ReadyRequest, BuiltManifest, JsonNode)> built)
        {
            var dto = Snapshot(catalogueVersion);
            var response = new SessionRegisterResponse
            {
                processSessionId = SessionId(index), seed = spec.Seed, missionTemplate = new MissionTemplateDto { id = "mission.standard", version = 1 },
                catalogueVersion = catalogueVersion, participants = new[] { new RegisterParticipantDto { accountId = AccountId, loadout = dto } },
                deadlines = new LaunchDeadlineDto { launch = "2026-10-21T12:00:00Z" },
            };
            if (!AttemptStart.TryCreate(AttemptId(index), response, catalogueVersion, id => sources.Operatives.Any(o => o != null && o.Id == id), ledgerCatalogue,
                    out var start, out var refusal))
                return "attempt refused: " + refusal;

            // The server fixture's generation steps for this seed, with the snapshot squad's size (as MissionDirector.Run).
            var settings = ServerFixture.Settings();
            settings.seed = spec.Seed;
            settings.friendlyCount = start.Snapshot.Count;
            var request = settings.Validated();
            MissionLayout layout = null;
            LootPlan loot = null;
            for (var attempt = 1; attempt <= request.maxAttempts && layout == null; attempt++)
            {
                if (!MissionGenerator.TryAttempt(request, attempt, out var candidate, out _)) continue;
                if (!ObjectivePlacer.TryPlace(candidate, request, out var objectivePlan, out _)) continue;
                if (!SecurityPlacer.TryPlace(candidate, objectivePlan, request, out var security, out _)) continue;
                layout = candidate;
                loot = LootPlacer.Place(candidate, objectivePlan, security, sources.LootTable);
            }
            if (layout == null) return "the layout never generated";
            var rolls = spec.Forced ?? loot.Containers.Select(c => c.Items.ToArray()).ToArray();

            var run = new Run { Catalogue = sources.Items };
            try
            {
                foreach (var containerRolls in rolls)
                {
                    var go = new GameObject("FixtureContainer") { hideFlags = HideFlags.HideAndDontSave };
                    var container = go.AddComponent<LootContainer>();
                    run.Containers.Add(container);
                    if (!container.InitializeForAttempt(containerRolls, sources.Items, out var entries, out var error, out var roll))
                        return $"fill: {error} container {run.Containers.Count - 1} roll {roll}";
                    run.Entries.Add(entries.Select(e => e.Clone()).ToList());
                }
                run.Inventory = InventorySession.FromSnapshot(sources.Items, sources.InventoryRules.BagCapacity, start.Snapshot, out var inventoryProblem);
                if (run.Inventory == null) return "snapshot inventory: " + inventoryProblem;
                run.Inventory.BeginMission(start.AttemptId);
                run.Session = new AttemptSession(start, ledgerCatalogue, BuildRevision, layout.Hash, sources.LootTable.Version,
                    run.Entries.Select((e, i) => (i, (IReadOnlyList<ItemEntry>)e)).ToList(), id => State(run.Inventory, id),
                    () => run.Containers.Select((c, i) => AttemptRuntime.ContainerState(i, c)).ToList(), () => ElapsedMs);
                var startRefusal = run.Session.Start();
                if (startRefusal != null) return "session refused: " + startRefusal;
                spec.Script(run);
                if (run.Errors.Count > 0) return string.Join("; ", run.Errors);

                var objectives = new List<ReadyObjective>();
                if (request.eliminateHostiles) objectives.Add(new ReadyObjective("eliminate", true));
                if (request.hackTerminal) objectives.Add(new ReadyObjective("hack", true));
                objectives.Add(new ReadyObjective("extract", false));
                var success = spec.Outcome == ManifestOutcome.Success;
                // A success completed every objective; a defeat ended with them still open.
                var results = objectives.Select(o => new ObjectiveResult(o.ObjectiveId, success ? ObjectiveStateKind.Completed : ObjectiveStateKind.Active)).ToList();
                run.Session.Finish(spec.Outcome, results, spec.Dead);
                if (run.Session.Outcome.Kind == ManifestBuildKind.Failed) return "manifest failed: " + string.Join(",", run.Session.Outcome.Diagnostics);
                var expectMismatch = spec.ExpectedManifestErrors.Contains("V14.integrity");
                if ((run.Session.Facts.Integrity == ManifestIntegrity.LedgerMismatch) != expectMismatch)
                    return "integrity " + run.Session.Facts.Integrity + ": " + string.Join(",", run.Session.Facts.FailureCodes);

                var ready = ReadyReportBuilder.Build(start.ProcessSessionId, layout.Hash, sources.LootTable.Version, objectives, run.Session.Ledger.PoolLines());
                var snapshotNode = SnapshotNode(dto);
                Write(files, spec.Name, spec.Covers, start, catalogueVersion, snapshotNode, ready, objectives, results, run.Session.Outcome.Manifest.CanonicalBytes,
                    spec.Forced == null ? spec.Seed : (int?)null, run.Events, new string[0], spec.ExpectedManifestErrors);
                built[spec.Name] = (ready, run.Session.Outcome.Manifest, snapshotNode);
                return null;
            }
            finally
            {
                foreach (var c in run.Containers)
                    if (c != null) Object.DestroyImmediate(c.gameObject);
            }
        }

        static OperativeRuntimeState State(InventorySession inventory, string operativeId)
        {
            var loadout = inventory.Working != null ? inventory.Working.Loadout(operativeId) : null;
            if (loadout == null) return null;
            var slots = loadout.Equipment;
            return new OperativeRuntimeState(operativeId, AttemptRuntime.Entries(loadout.Bag),
                new RuntimeEquipment(slots.InstanceIdIn(ItemSlot.Weapon), slots.InstanceIdIn(ItemSlot.Armor), slots.InstanceIdIn(ItemSlot.Utility)));
        }

        // ---- the deliberately invalid fixtures: one change to a valid one, and the refusal the backend must give ----

        static void Invalid(SortedDictionary<string, byte[]> files, Dictionary<string, (ReadyRequest Ready, BuiltManifest Manifest, JsonNode Snapshot)> built,
            string catalogueVersion)
        {
            var one = built["01-forced-duplicate-medkits"];
            ReadyRequest WithPool(ReadyRequest r, PoolLineDto[] pool, string lootHash) => new ReadyRequest
            {
                processSessionId = r.processSessionId, layoutHash = r.layoutHash, lootTableVersion = r.lootTableVersion, objectives = r.objectives,
                lootPool = pool, lootHash = lootHash,
            };
            PoolLineDto Line(int c, int l, string d, int q) => new PoolLineDto { containerIndex = c, line = l, definitionId = d, quantity = q };
            string Hash(ReadyRequest r, params PoolLineDto[] pool) =>
                LootPoolHash.Compute(r.lootTableVersion, pool.Select(p => new PoolLine(p.containerIndex, p.line, p.definitionId, p.quantity)).ToList());

            var merged = new[] { Line(0, 0, Medkit, 3) };
            WriteInvalidReady(files, "91-invalid-merged-medkits", "the old merged fill of fixture 01 (one line of 3): refused, the I1 failure",
                one, WithPool(one.Ready, merged, Hash(one.Ready, merged)), catalogueVersion, "line (0,0): quantity 3 is outside the entry's range");
            var overStack = new[] { Line(0, 0, Medkit, 4) };
            WriteInvalidReady(files, "92-invalid-roll-over-stack", "a medkit line of 4 (over maxStack 3 and the table's 1..2): refused; Unity fails generation instead",
                one, WithPool(one.Ready, overStack, Hash(one.Ready, overStack)), catalogueVersion, "line (0,0): quantity 4 is outside the entry's range");
            WriteInvalidReady(files, "93-invalid-loot-hash", "fixture 01's pool with another pool's hash: refused",
                one, WithPool(one.Ready, one.Ready.lootPool, Hash(one.Ready, overStack)), catalogueVersion, "lootHash is not the pool's canonical hash");

            // 94: fixture 07's manifest claiming two units from line (0,0), which holds one (collected and held both raised by one).
            var seven = built["07-forced-partial-and-complete"];
            var m = seven.Manifest.Manifest;
            var operatives = m.Operatives.Select(o => o.Slot != 0 ? o : new OperativeFact(o.OperativeId, o.Slot, o.Alive, o.ConsumedOwned, o.ConsumedLoot, o.HeldItems,
                o.HeldStacks, o.HeldLoot.Select(x => x.ContainerIndex == 0 && x.Line == 0 ? new LootLotFact(0, 0, x.Quantity + 1) : x).ToList(),
                o.Collected.Select(x => x.ContainerIndex == 0 && x.Line == 0 ? new LineCount(0, 0, x.Quantity + 1) : x).ToList(), o.Equipment)).ToList();
            var tampered = new ResultManifestV1(m.AttemptId, m.AccountId, m.ProcessSessionId, m.BuildRevision, m.CatalogueVersion, m.Integrity, m.Outcome,
                m.OutcomeSequence, m.ServerElapsedMs, m.LayoutHash, m.LootTableVersion, m.LootHash, m.Objectives, operatives);
            var name = "94-invalid-pool-exceeded";
            var description = Description(name, "fixture 07's manifest claiming two units of line (0,0), whose pool quantity is 1: refused",
                "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e07", catalogueVersion, seven.Snapshot, seven.Ready, "07-forced-partial-and-complete.ready.json",
                name + ".manifest.json", null, new JsonNode[0], new string[0], new[] { "V9.pool_exceeded" });
            files[name + ".json"] = Canonical(description);
            files[name + ".manifest.json"] = ManifestCodec.ToCanonicalBytes(tampered);
        }

        static void WriteInvalidReady(SortedDictionary<string, byte[]> files, string name, string covers, (ReadyRequest Ready, BuiltManifest Manifest, JsonNode Snapshot) from,
            ReadyRequest ready, string catalogueVersion, string error)
        {
            files[name + ".ready.json"] = Utf8(JsonUtility.ToJson(ready));
            files[name + ".json"] = Canonical(Description(name, covers, from.Manifest.Manifest.AttemptId, catalogueVersion, from.Snapshot, ready, name + ".ready.json", null,
                null, new JsonNode[0], new[] { error }, new string[0]));
        }

        // ---- files ----

        static void Write(SortedDictionary<string, byte[]> files, string name, string covers, AttemptStart start, string catalogueVersion, JsonNode snapshot,
            ReadyRequest ready, IReadOnlyList<ReadyObjective> objectives, IReadOnlyList<ObjectiveResult> results, byte[] manifest, int? seed, IReadOnlyList<JsonNode> events,
            string[] readyErrors, string[] manifestErrors)
        {
            files[name + ".ready.json"] = Utf8(JsonUtility.ToJson(ready));
            files[name + ".manifest.json"] = manifest;
            files[name + ".json"] = Canonical(Description(name, covers, start.AttemptId, catalogueVersion, snapshot, ready, name + ".ready.json", name + ".manifest.json",
                seed, events, readyErrors, manifestErrors));
        }

        static JsonObject Description(string name, string covers, string attemptId, string catalogueVersion, JsonNode snapshot, ReadyRequest ready, string readyFile,
            string manifestFile, int? seed, IReadOnlyList<JsonNode> events, IReadOnlyList<string> readyErrors, IReadOnlyList<string> manifestErrors) =>
            JsonObject.Of(
                ("name", new JsonString(name)),
                ("covers", new JsonString(covers)),
                ("attemptId", new JsonString(attemptId)),
                ("processSessionId", new JsonString(ready.processSessionId)),
                ("buildRevision", new JsonString(BuildRevision)),
                ("catalogueVersion", new JsonString(catalogueVersion)),
                ("seed", seed.HasValue ? (JsonNode)JsonNumber.FromInt(seed.Value) : JsonNull.Instance),
                ("snapshot", snapshot),
                ("events", new JsonArray(events)),
                ("readyFile", new JsonString(readyFile)),
                ("manifestFile", manifestFile != null ? (JsonNode)new JsonString(manifestFile) : JsonNull.Instance),
                ("expectedReadyErrors", new JsonArray(readyErrors.Select(e => (JsonNode)new JsonString(e)))),
                ("expectedManifestErrors", new JsonArray(manifestErrors.Select(e => (JsonNode)new JsonString(e)))));

        /// <summary>The admission snapshot in LoadoutSnapshot.ToJson's shape (an empty slot is JSON null).</summary>
        static JsonNode SnapshotNode(LoadoutSnapshotDto s)
        {
            JsonNode Slot(string id) => string.IsNullOrEmpty(id) ? (JsonNode)JsonNull.Instance : new JsonString(id);
            return JsonObject.Of(
                ("accountId", new JsonString(s.accountId)),
                ("inventoryVersion", JsonNumber.FromInt(s.inventoryVersion)),
                ("catalogueVersion", new JsonString(s.catalogueVersion)),
                ("operatives", new JsonArray(s.operatives.Select(o => (JsonNode)JsonObject.Of(
                    ("slot", JsonNumber.FromInt(o.slot)),
                    ("operativeId", new JsonString(o.operativeId)),
                    ("definitionId", new JsonString(o.definitionId)),
                    ("experience", JsonNumber.FromInt(o.experience)),
                    ("rank", JsonNumber.FromInt(o.rank)),
                    ("choices", new JsonArray(o.choices.Select(c => (JsonNode)new JsonString(c)))),
                    ("bag", JsonObject.Of(
                        ("instances", new JsonArray(o.bag.instances.Select(i => (JsonNode)JsonObject.Of(("itemId", new JsonString(i.itemId)), ("definitionId", new JsonString(i.definitionId)))))),
                        ("stacks", new JsonArray(o.bag.stacks.Select(x => (JsonNode)JsonObject.Of(("definitionId", new JsonString(x.definitionId)), ("quantity", JsonNumber.FromInt(x.quantity)))))))),
                    ("equipment", JsonObject.Of(("weapon", Slot(o.equipment.weapon)), ("armor", Slot(o.equipment.armor)), ("utility", Slot(o.equipment.utility)))))))));
        }

        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

        static byte[] Canonical(JsonNode node) => CanonicalJsonWriter.ToBytes(node).Concat(new[] { (byte)'\n' }).ToArray();
    }
}
