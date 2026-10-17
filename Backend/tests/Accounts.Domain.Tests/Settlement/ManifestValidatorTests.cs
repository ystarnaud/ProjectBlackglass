using Blackglass.Accounts.Domain.Settlement;
using static Blackglass.Accounts.Domain.Tests.Settlement.SettlementFixtures;

namespace Blackglass.Accounts.Domain.Tests.Settlement;

/// <summary>V4-V15 (plan §F): one minimal failing variant per rule, each with its exact code; the fixture itself passes.</summary>
public class ManifestValidatorTests
{
    static IReadOnlyList<string> Validate(ResultManifest m, SettlementContext? c = null) => ManifestValidator.Validate(m, ManifestHash(m), c ?? Context());

    static string ManifestHash(ResultManifest m) => ((ParseResult.Parsed)ResultManifest.Parse(m.ToCanonicalBytes())).ManifestHash;

    static ResultManifest WithFirst(Func<ManifestOperative, ManifestOperative> change, string outcome = "success")
    {
        var m = Manifest(outcome);
        return m with { Operatives = [change(m.Operatives[0]), m.Operatives[1]] };
    }

    [Fact]
    public void The_fixture_is_valid_for_every_outcome()
    {
        foreach (var outcome in new[] { "success", "defeat", "abandoned", "disconnected", "expired" })
            Assert.Empty(Validate(Manifest(outcome)));
    }

    [Fact]
    public void V4_build_and_catalogue_must_be_the_sessions()
    {
        Assert.Equal(["V4.build"], Validate(Manifest() with { BuildRevision = "r2" }));
        Assert.Equal(["V4.catalogue"], Validate(Manifest() with { CatalogueVersion = "sha256:" + new string('a', 64) }));
    }

    [Fact]
    public void V5_layout_and_loot_must_be_the_stored_ones()
    {
        Assert.Equal(["V5.layout"], Validate(Manifest() with { LayoutHash = "0000000000000000" }));
        Assert.Equal(["V5b.loot"], Validate(Manifest() with { LootHash = "sha256:" + new string('b', 64) }));
        Assert.Equal(["V5b.loot"], Validate(Manifest() with { LootTableVersion = 99 }));
    }

    [Fact]
    public void V6_operatives_are_exactly_the_snapshots_in_slot_order()
    {
        var m = Manifest();
        Assert.Equal(["V6.operatives"], Validate(m with { Operatives = [m.Operatives[0]] }));
        Assert.Equal(["V6.operatives"], Validate(m with { Operatives = [m.Operatives[0], m.Operatives[1] with { OperativeId = 99 }] }));
        Assert.Equal(["V6.operatives"], Validate(m with { Operatives = [m.Operatives[0] with { Slot = 1 }, m.Operatives[1] with { Slot = 0 }] }));
    }

    [Fact]
    public void V7_objectives_match_the_registered_set_and_success_needs_every_required_one()
    {
        var m = Manifest();
        Assert.Equal(["V7.objectives"], Validate(m with { Objectives = [new ObjectiveState("extract", "completed")] }));
        Assert.Equal(["V7.objectives"], Validate(m with { Objectives = [new ObjectiveState("extract", "completed"), new ObjectiveState("other", "failed")] }));
        Assert.Equal(["V7.success_incomplete"], Validate(m with { Objectives = [new ObjectiveState("extract", "failed"), new ObjectiveState("intel", "completed")] }));
        Assert.Empty(Validate(Manifest("defeat") with { Objectives = [new ObjectiveState("extract", "failed"), new ObjectiveState("intel", "completed")] }));
    }

    [Fact]
    public void V8_owned_consumption_is_consumables_only_and_within_the_snapshot()
    {
        // Consuming a non-consumable (the vest is held as an item, so conservation is untouched by the claim).
        Assert.Contains("V8.not_consumable", Validate(WithFirst(o => o with { ConsumedOwned = [new("item.light-vest", 1), new("item.medkit", 1)] })));
        // More than the snapshot holds (3): the held stack can't balance either.
        Assert.Contains("V8.owned_exceeds", Validate(WithFirst(o => o with { ConsumedOwned = [new("item.medkit", 4)], HeldStacks = [] })));
    }

    [Fact]
    public void V8_loot_consumption_names_a_line_this_operative_collected_with_its_definition()
    {
        Assert.Contains("V8.loot_not_collected", Validate(WithFirst(o => o with
        {
            ConsumedLoot = [new LootConsumption(0, 0, "item.medkit", 1), new LootConsumption(1, 0, "item.light-vest", 1)],
        })));
        Assert.Contains("V8.loot_definition", Validate(WithFirst(o => o with { ConsumedLoot = [new LootConsumption(0, 0, "item.light-vest", 1)] })));
        Assert.Contains("V8.not_consumable", Validate(WithFirst(o => o with
        {
            ConsumedLoot = [new LootConsumption(0, 0, "item.medkit", 1), new LootConsumption(0, 1, "item.service-rifle", 1)],
            HeldLoot = [new LootLot(0, 0, 1)],
            Equipment = [EquipmentRef.Item("armor", 102), EquipmentRef.Item("weapon", 101)],
        })));
    }

    [Fact]
    public void V9_collection_per_line_is_summed_with_the_settled_participants()
    {
        Assert.Equal(["V9.pool_exceeded"], Validate(Manifest(), Context(new Dictionary<(int, int), int> { [(0, 0)] = 1 })));
        Assert.Empty(Validate(Manifest(), Context(new Dictionary<(int, int), int> { [(1, 0)] = 1 })));
        Assert.Contains("V9.unknown_line", Validate(WithFirst(o => o with { Collected = [.. o.Collected, new LineCount(7, 0, 1)], HeldLoot = [.. o.HeldLoot, new LootLot(7, 0, 1)] })));
    }

    [Fact]
    public void V10_owned_stacks_balance()
    {
        Assert.Equal(["V10.owned_stack"], Validate(WithFirst(o => o with { HeldStacks = [new OwnedStack("item.medkit", 3)] })));
        Assert.Equal(["V10.owned_stack"], Validate(WithFirst(o => o with { HeldStacks = [] })));
    }

    [Fact]
    public void V10_snapshot_items_are_held_by_their_own_operative_only()
    {
        var m = Manifest();
        // Operative 12 claims operative 11's vest: 11 no longer holds it and 12 holds an item it never had.
        var moved = m with
        {
            Operatives =
            [
                m.Operatives[0] with { HeldItems = [101], Equipment = [EquipmentRef.Item("weapon", 101)] },
                m.Operatives[1] with { HeldItems = [102, 103] },
            ],
        };
        Assert.Contains("V10.owned_item", Validate(moved));
        Assert.Contains("V10.owned_item", Validate(WithFirst(o => o with { HeldItems = [101, 102, 999] })));
    }

    [Fact]
    public void V10_loot_lots_balance_per_line_and_operative()
    {
        Assert.Equal(["V10.loot"], Validate(WithFirst(o => o with { HeldLoot = [new LootLot(0, 0, 2), new LootLot(0, 1, 1)] })));
        Assert.Contains("V10.loot", Validate(WithFirst(o => o with { HeldLoot = [new LootLot(0, 1, 1)] })));
    }

    [Fact]
    public void V11_bag_capacity_is_judged_by_bag_entries_under_the_admission_catalogue()
    {
        // 8 entries allowed: 2 items + 1 rifle + the medkits (3 units in one stack) = 4; 5 more instances push it to 9.
        var crowded = Snapshot() with
        {
            Operatives = [Snapshot().Operatives[0] with { Instances = [.. Snapshot().Operatives[0].Instances, .. Enumerable.Range(200, 5).Select(i => new Blackglass.Accounts.Domain.Attempts.SnapshotInstance(i, "item.combat-blade"))] }, Snapshot().Operatives[1]],
        };
        var m = WithFirst(o => o with { HeldItems = [101, 102, 200, 201, 202, 203, 204] });
        Assert.Equal(["V11.capacity"], Validate(m, Context(snapshot: crowded)));
    }

    [Fact]
    public void V11_lets_a_bag_admitted_over_capacity_stay_over_but_never_grow()
    {
        // Admitted holding 9 entries (2 items + 7 blades) under an 8-entry bag: an honest result with nothing collected passes.
        var crowded = Snapshot() with
        {
            Operatives = [Snapshot().Operatives[0] with { Instances = [.. Snapshot().Operatives[0].Instances, .. Enumerable.Range(200, 7).Select(i => new Blackglass.Accounts.Domain.Attempts.SnapshotInstance(i, "item.combat-blade"))] }, Snapshot().Operatives[1]],
        };
        var stayed = WithFirst(o => o with { HeldItems = [101, 102, .. Enumerable.Range(200, 7).Select(i => (long)i)], Collected = [], ConsumedLoot = [], HeldLoot = [] });
        Assert.Empty(Validate(stayed, Context(snapshot: crowded)));
        var grew = WithFirst(o => o with { HeldItems = [101, 102, .. Enumerable.Range(200, 7).Select(i => (long)i)], Collected = [new LineCount(0, 1, 1)], ConsumedLoot = [], HeldLoot = [new LootLot(0, 1, 1)] });
        Assert.Equal(["V11.capacity"], Validate(grew, Context(snapshot: crowded)));
    }

    [Fact]
    public void V12_equipment_references_a_held_item_of_the_right_slot_once()
    {
        Assert.Contains("V12.slot", Validate(WithFirst(o => o with { Equipment = [EquipmentRef.Item("armor", 101), EquipmentRef.Item("weapon", 102)] })));
        Assert.Contains("V12.not_held", Validate(WithFirst(o => o with { Equipment = [EquipmentRef.Item("weapon", 103)] })));
        Assert.Contains("V12.not_held", Validate(WithFirst(o => o with { Equipment = [EquipmentRef.Loot("armor", 1, 0)] })));
        Assert.Contains("V12.duplicate_ref", Validate(WithFirst(o => o with { Equipment = [EquipmentRef.Item("armor", 101), EquipmentRef.Item("weapon", 101)] })));
    }

    [Fact]
    public void V13_plausibility()
    {
        Assert.Equal(["V13.elapsed"], Validate(Manifest() with { ServerElapsedMs = 30 * 60_000 + 60_001 }));
        Assert.Empty(Validate(Manifest() with { ServerElapsedMs = 30 * 60_000 + 60_000 }));
        var future = Context() with { Attempt = Facts() with { ParticipantStartedAt = Now.AddSeconds(1) } };
        Assert.Equal(["V13.started"], Validate(Manifest(), future));
        var late = Context() with { Attempt = Facts() with { Now = Started.AddHours(5) } };
        Assert.Equal(["V13.elapsed"], Validate(Manifest() with { ServerElapsedMs = SettlementLimits.Default.MaxElapsedMs + 1 }, late));
    }

    [Fact]
    public void V14_integrity_must_be_ok() => Assert.Equal(["V14.integrity"], Validate(Manifest() with { Integrity = "ledgerMismatch" }));

    [Fact]
    public void V15_a_recorded_hash_must_match_and_an_absent_one_passes()
    {
        var m = Manifest();
        Assert.Empty(Validate(m, Context(recorded: null)));
        Assert.Empty(Validate(m, Context(recorded: ManifestHash(m))));
        Assert.Equal(["V15.recorded_hash"], Validate(m, Context(recorded: "sha256:" + new string('c', 64))));
    }

    [Fact]
    public void Codes_are_sorted_and_distinct()
    {
        var codes = Validate(Manifest() with { BuildRevision = "r2", Integrity = "ledgerMismatch", LayoutHash = "0000000000000000" });
        Assert.Equal(["V14.integrity", "V4.build", "V5.layout"], codes);
    }
}
