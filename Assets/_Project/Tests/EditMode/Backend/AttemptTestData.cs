using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blackglass.Backend.Core;
using static Blackglass.Tests.ContractVectors;

namespace Blackglass.Tests
{
    /// <summary>
    /// Register answers for the attempt tests, built from the shared contract files: the ledger-v1 snapshots and the golden
    /// catalogue (Backend/contracts). Every call returns fresh objects, so a test can corrupt its copy freely.
    /// </summary>
    internal static class AttemptTestData
    {
        public const string AttemptId = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";
        public const string SessionId = "0192f1a5-0000-7000-8000-000000000001";
        public const int Seed = 424242;
        public const string TemplateId = "procedural-standard";
        public const string OperativeDefinition1 = "00000000-0000-4000-8000-000000000001";

        public static string GoldenCatalogueVersion =>
            File.ReadAllText(Path.Combine(ProjectRoot, "Backend", "contracts", "catalogue", "golden-catalogue.version")).Trim();

        public static bool KnownOperative(string definitionId) =>
            definitionId == OperativeDefinition1 || definitionId == "00000000-0000-4000-8000-000000000002";

        /// <summary>The golden catalogue's items as the ledger sees them.</summary>
        public static LedgerCatalogue GoldenLedgerCatalogue() => LedgerCatalogueOf(Load("catalogue", "golden-catalogue.json"));

        public static LedgerCatalogue LedgerCatalogueOf(Dictionary<string, object> catalogue) =>
            new LedgerCatalogue(Arr(catalogue["items"]).Select(Obj).Select(i =>
            {
                var slot = Str(i["slot"]);
                EquipSlot? equip = slot == "none" ? (EquipSlot?)null : ManifestWords.TrySlot(slot, out var s) ? s : throw new InvalidDataException(slot);
                return new LedgerItemDefinition(Str(i["id"]), Bool(i["stackable"]), Str(i["category"]) == "consumable", equip);
            }));

        /// <summary>A ledger-v1 scenario's admission snapshot as the DTO JsonUtility would read.</summary>
        public static LoadoutSnapshotDto LedgerSnapshot(string scenario) => SnapshotDto(Obj(Load("ledger-v1", scenario)["snapshot"]));

        public static LoadoutSnapshotDto SnapshotDto(Dictionary<string, object> s) => new LoadoutSnapshotDto
        {
            accountId = Str(s["accountId"]),
            inventoryVersion = (long)s["inventoryVersion"],
            catalogueVersion = Str(s["catalogueVersion"]),
            operatives = Arr(s["operatives"]).Select(Obj).Select(o =>
            {
                var bag = Obj(o["bag"]);
                var equipment = Obj(o["equipment"]);
                return new SnapshotOperativeDto
                {
                    slot = Int(o["slot"]),
                    operativeId = Str(o["operativeId"]),
                    definitionId = Str(o["definitionId"]),
                    experience = Int(o["experience"]),
                    rank = Int(o["rank"]),
                    choices = Arr(o["choices"]).Select(Str).ToArray(),
                    bag = new SnapshotBagDto
                    {
                        instances = Arr(bag["instances"]).Select(Obj).Select(i => new SnapshotInstanceDto { itemId = Str(i["itemId"]), definitionId = Str(i["definitionId"]) }).ToArray(),
                        stacks = Arr(bag["stacks"]).Select(Obj).Select(x => new SnapshotStackDto { definitionId = Str(x["definitionId"]), quantity = Int(x["quantity"]) }).ToArray(),
                    },
                    equipment = new EquipmentSlotsDto
                    {
                        weapon = (string)equipment["weapon"],
                        armor = (string)equipment["armor"],
                        utility = (string)equipment["utility"],
                    },
                };
            }).ToArray(),
        };

        /// <summary>The register answer for one participant with this snapshot.</summary>
        public static SessionRegisterResponse Response(LoadoutSnapshotDto snapshot, string catalogueVersion = null) => new SessionRegisterResponse
        {
            processSessionId = SessionId,
            seed = Seed,
            missionTemplate = new MissionTemplateDto { id = TemplateId, version = 1 },
            catalogueVersion = catalogueVersion ?? snapshot.catalogueVersion,
            participants = new[] { new RegisterParticipantDto { accountId = snapshot.accountId, loadout = snapshot } },
            deadlines = new LaunchDeadlineDto { launch = "2026-10-21T12:00:00Z" },
        };
    }
}
