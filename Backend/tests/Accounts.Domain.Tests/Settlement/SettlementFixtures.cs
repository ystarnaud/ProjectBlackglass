using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Insurance;
using Blackglass.Accounts.Domain.Settlement;

namespace Blackglass.Accounts.Domain.Tests.Settlement;

/// <summary>
/// One deployed participant (account 1001) and a manifest that settles cleanly against it, under the embedded catalogue:
/// slot 0 is operative 11 (service rifle 101 equipped as weapon, light vest 102 as armor, 3 medkits), slot 1 is operative 12
/// (combat blade 103 equipped). The pool holds medkits (0,0)×2, a service rifle (0,1), a light vest (1,0) and servo boots
/// (2,0). Tests derive failing variants with <c>with</c>.
/// </summary>
internal static class SettlementFixtures
{
    public static readonly GameCatalogue Embedded = GameCatalogue.Embedded;
    public static readonly Guid Attempt = Guid.Parse("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41");
    public static readonly Guid Session = Guid.Parse("0192f1a5-0000-7000-8000-000000000001");
    public const long Account = 1001;
    public static readonly DateTime Started = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime Now = Started.AddMinutes(30);

    public static LoadoutSnapshot Snapshot() => new(Account, 5, Embedded.Version,
    [
        new SnapshotOperative(0, 11, "operative.vanguard", 40, 0, [], [new(101, "item.service-rifle"), new(102, "item.light-vest")],
            [new SnapshotStack("item.medkit", 3)], new SnapshotEquipment(101, 102, null)),
        new SnapshotOperative(1, 12, "operative.scout", 0, 0, [], [new(103, "item.combat-blade")], [], new SnapshotEquipment(103, null, null)),
    ]);

    public static readonly IReadOnlyList<PoolLine> Pool =
    [
        new(0, 0, "item.medkit", 2), new(0, 1, "item.service-rifle", 1), new(1, 0, "item.light-vest", 1), new(2, 0, "item.servo-boots", 1),
    ];

    public static readonly IReadOnlyList<Objective> Objectives = [new("extract", true), new("intel", false)];

    public static AttemptFacts Facts() => new(Attempt, Account, Session, "r1", Embedded.Version, "cf2e14dece9cc2a6", Embedded.Document.LootTable.Version,
        LootHash.Compute(Embedded.Document.LootTable.Version, Pool), Started, Started.AddSeconds(5), Now);

    public static SettlementContext Context(IReadOnlyDictionary<(int, int), int>? settled = null, string? recorded = null, LoadoutSnapshot? snapshot = null) =>
        new(snapshot ?? Snapshot(), Facts(), Pool, Objectives, settled ?? new Dictionary<(int, int), int>(), Embedded, recorded, SettlementLimits.Default);

    public static ManifestOperative First() => new(11, 0, true,
        ConsumedOwned: [new OwnedConsumption("item.medkit", 1)],
        ConsumedLoot: [new LootConsumption(0, 0, "item.medkit", 1)],
        HeldItems: [101, 102],
        HeldStacks: [new OwnedStack("item.medkit", 2)],
        HeldLoot: [new LootLot(0, 0, 1), new LootLot(0, 1, 1)],
        Collected: [new LineCount(0, 0, 2), new LineCount(0, 1, 1)],
        Equipment: [EquipmentRef.Item("armor", 102), EquipmentRef.Item("weapon", 101)]);

    public static ManifestOperative Second() => new(12, 1, false,
        ConsumedOwned: [], ConsumedLoot: [],
        HeldItems: [103], HeldStacks: [],
        HeldLoot: [new LootLot(2, 0, 1)],
        Collected: [new LineCount(2, 0, 1)],
        Equipment: [EquipmentRef.Loot("utility", 2, 0), EquipmentRef.Item("weapon", 103)]);

    public static ResultManifest Manifest(string outcome = "success") => new(Attempt, Account, Session, "r1", Embedded.Version, "ok", outcome, 8812,
        1_200_000, "cf2e14dece9cc2a6", Embedded.Document.LootTable.Version, LootHash.Compute(Embedded.Document.LootTable.Version, Pool),
        [new ObjectiveState("extract", outcome == "success" ? "completed" : "active"), new ObjectiveState("intel", "failed")],
        [First(), Second()]);

    public static InsuranceSnapshot Standard() => new("standard", 1, InsuranceTerms.Parse("""
        {"covers":["defeat","abandoned","disconnected","expired"],
         "consumableRefund":{"percent":50,"rounding":"floor","destination":"stash"},
         "lootRetention":{"maxEntries":2,"maxUnitsPerEntry":3,"eligibleCategories":["Consumable","Armor","Utility","Weapon"],
           "rank":["categoryPriority","saltedHash"],"categoryPriority":["Weapon","Armor","Utility","Consumable"]},
         "progressXp":{"percent":40,"basis":"requiredObjectivesCompleted"}}
        """));

    public static readonly byte[] Salt = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
}
