using System.Text;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Insurance;
using Blackglass.Accounts.Domain.Settlement;
using static Blackglass.Accounts.Domain.Tests.Settlement.SettlementFixtures;

namespace Blackglass.Accounts.Domain.Tests.Settlement;

/// <summary>The pure settlement plan (S9-B11-B14): minting, stacks, equipment, XP, insurance and the expected bags.</summary>
public class SettlementPlannerTests
{
    static readonly string Acc = Base58Id.Format(Account);

    static SettlementPlan Plan(string outcome, InsuranceSnapshot? insurance = null, byte[]? salt = null, ResultManifest? m = null) =>
        SettlementPlanner.Plan(m ?? Manifest(outcome), Context(), insurance, salt ?? Salt);

    [Fact]
    public void Success_grants_loot_to_the_collector_and_mints_one_unit_per_reference()
    {
        var plan = Plan("success");
        Assert.Equal(
        [
            new MintUnit(11, "item.service-rifle", "settlement", $"settlement:{Attempt}:{Acc}:c0:l1:u0"),
            new MintUnit(12, "item.servo-boots", "settlement", $"settlement:{Attempt}:{Acc}:c2:l0:u0"),
        ], plan.Mint);
        Assert.Equal([new StackCredit(11, "item.medkit", 1)], plan.Credits);
        Assert.Equal([new OwnedDebit(11, "item.medkit", 1)], plan.Debits);
        Assert.Equal(new Dictionary<(int, int), int> { [(0, 0)] = 2, [(0, 1)] = 1, [(2, 0)] = 1 }, plan.Collections);
    }

    [Fact]
    public void A_non_stackable_line_of_quantity_two_mints_two_units_with_consecutive_refs()
    {
        var pool = new List<Blackglass.Accounts.Domain.Attempts.PoolLine>(Pool) { [1] = new(0, 1, "item.service-rifle", 2) };
        var c = Context() with { Pool = pool, Attempt = Facts() with { LootHash = Blackglass.Accounts.Domain.Attempts.LootHash.Compute(Embedded.Document.LootTable.Version, pool) } };
        var m = Manifest() with { LootHash = c.Attempt.LootHash };
        m = m with
        {
            Operatives = [m.Operatives[0] with { Collected = [new(0, 0, 2), new(0, 1, 2)], HeldLoot = [new(0, 0, 1), new(0, 1, 2)] }, m.Operatives[1]],
        };
        Assert.Empty(ManifestValidator.Validate(m, ((ParseResult.Parsed)ResultManifest.Parse(m.ToCanonicalBytes())).ManifestHash, c));
        var mint = SettlementPlanner.Plan(m, c, null, Salt).Mint.Where(u => u.DefinitionId == "item.service-rifle").Select(u => u.OriginRef);
        Assert.Equal([$"settlement:{Attempt}:{Acc}:c0:l1:u0", $"settlement:{Attempt}:{Acc}:c0:l1:u1"], mint);
    }

    [Fact]
    public void Success_applies_the_mission_equipment_and_gives_completion_xp_to_every_operative_dead_included()
    {
        var plan = Plan("success");
        Assert.Equal(
        [
            new OperativeEquipment(11, EquipTarget.Existing(101), EquipTarget.Existing(102), EquipTarget.Empty),
            new OperativeEquipment(12, EquipTarget.Existing(103), EquipTarget.Empty, EquipTarget.Minted($"settlement:{Attempt}:{Acc}:c2:l0:u0")),
        ], plan.Equipment);
        var xp = Embedded.Document.Progression.MissionCompletionXp;
        Assert.Equal([new XpGrant(11, xp), new XpGrant(12, xp)], plan.Xp); // operative 12 is not alive
    }

    [Fact]
    public void Failure_without_insurance_applies_consumption_only()
    {
        var plan = Plan("defeat");
        Assert.Equal([new OwnedDebit(11, "item.medkit", 1)], plan.Debits);
        Assert.Empty(plan.Mint);
        Assert.Empty(plan.Credits);
        Assert.Empty(plan.Equipment);
        Assert.Empty(plan.Xp);
        Assert.Null(plan.Insurance);
    }

    [Fact]
    public void Insured_defeat_refunds_to_the_stash_retains_ranked_loot_and_gives_progress_xp()
    {
        var plan = Plan("defeat", Standard());
        var payout = Assert.IsType<InsurancePayout>(plan.Insurance);
        // floor(1 × 50 / 100) = 0 refunded units: recorded, nothing credited.
        Assert.Equal([new RefundLine("item.medkit", 0)], payout.Refunds);
        // Candidates: (0,0) medkit ×1, (0,1) rifle ×1, (2,0) boots ×1. Weapon ranks first, then Utility; maxEntries 2.
        Assert.Equal([new RetainedLot(0, 1, "item.service-rifle", 1), new RetainedLot(2, 0, "item.servo-boots", 1)], payout.Retained);
        Assert.Equal(
        [
            new MintUnit(null, "item.service-rifle", "insurance", $"insurance:{Attempt}:{Acc}:c0:l1:u0"),
            new MintUnit(null, "item.servo-boots", "insurance", $"insurance:{Attempt}:{Acc}:c2:l0:u0"),
        ], plan.Mint);
        // No required objective completed: floor(150 × 0 × 40 / (1 × 100)) = 0 for each operative.
        Assert.Equal([new XpGrant(11, 0), new XpGrant(12, 0)], payout.Xp);
        Assert.Empty(plan.Equipment);
    }

    [Fact]
    public void Progress_xp_takes_a_single_floor_and_is_zero_with_nothing_required()
    {
        Assert.Equal(60, SettlementPlanner.ProgressXp(150, 1, 1, 40));
        Assert.Equal(19, SettlementPlanner.ProgressXp(97, 1, 2, 40)); // 97 × 1 × 40 / 200 = 19.4
        Assert.Equal(0, SettlementPlanner.ProgressXp(150, 0, 0, 40));
        Assert.Equal(int.MaxValue, SettlementPlanner.ProgressXp(int.MaxValue, 64, 64, 100));
    }

    [Fact]
    public void Refunds_take_a_single_floor_per_definition_in_64_bit_arithmetic()
    {
        Assert.Equal(1, SettlementPlanner.Refund(3, 50));
        Assert.Equal(0, SettlementPlanner.Refund(1, 50));
        Assert.Equal(int.MaxValue, SettlementPlanner.Refund(int.MaxValue, 100));
    }

    [Fact]
    public void Insurance_values_come_from_the_snapshot_not_a_current_policy()
    {
        var generous = new InsuranceSnapshot("standard", 2, InsuranceTerms.Parse("""
            {"covers":["defeat"],"consumableRefund":{"percent":100,"rounding":"floor","destination":"stash"},
             "lootRetention":{"maxEntries":0,"maxUnitsPerEntry":1,"eligibleCategories":[],"rank":["categoryPriority","saltedHash"],
               "categoryPriority":["Weapon","Armor","Utility","Consumable"]},
             "progressXp":{"percent":100,"basis":"requiredObjectivesCompleted"}}
            """));
        // The planner takes exactly one snapshot; the same inputs with the generous terms give a different payout, so the
        // terms used are the ones passed in, never looked up.
        var standard = Plan("defeat", Standard()).Insurance!;
        var other = Plan("defeat", generous).Insurance!;
        Assert.Equal(("standard", 1), (standard.PolicyId, standard.PolicyVersion));
        Assert.Equal([new RefundLine("item.medkit", 1)], other.Refunds);
        Assert.Empty(other.Retained);
    }

    [Fact]
    public void An_uncovered_outcome_pays_nothing_even_when_insured()
    {
        var narrow = new InsuranceSnapshot("standard", 3, InsuranceTerms.Parse("""
            {"covers":["defeat"],"consumableRefund":{"percent":50,"rounding":"floor","destination":"stash"},
             "lootRetention":{"maxEntries":2,"maxUnitsPerEntry":3,"eligibleCategories":["Weapon"],"rank":["categoryPriority","saltedHash"],
               "categoryPriority":["Weapon","Armor","Utility","Consumable"]},
             "progressXp":{"percent":40,"basis":"requiredObjectivesCompleted"}}
            """));
        var plan = Plan("abandoned", narrow);
        Assert.Null(plan.Insurance);
        Assert.True(plan.Insured);
        Assert.Empty(plan.Mint);
    }

    [Fact]
    public void Xp_comes_from_the_admission_catalogue_in_the_context()
    {
        var doc = Embedded.Document with { Progression = Embedded.Document.Progression with { MissionCompletionXp = 77 } };
        var other = GameCatalogue.Load(CanonicalCatalogueJson.ToBytes(doc));
        var plan = SettlementPlanner.Plan(Manifest(), Context() with { AdmissionCatalogue = other }, null, Salt);
        Assert.All(plan.Xp, g => Assert.Equal(77, g.Amount));
    }

    [Fact]
    public void Retention_is_stable_across_calls_and_follows_the_salt()
    {
        var everything = new InsuranceSnapshot("standard", 4, InsuranceTerms.Parse("""
            {"covers":["defeat"],"consumableRefund":{"percent":50,"rounding":"floor","destination":"stash"},
             "lootRetention":{"maxEntries":1,"maxUnitsPerEntry":3,"eligibleCategories":["Consumable","Armor","Utility","Weapon"],
               "rank":["categoryPriority","saltedHash"],"categoryPriority":["Consumable","Armor","Utility","Weapon"]},
             "progressXp":{"percent":40,"basis":"requiredObjectivesCompleted"}}
            """));
        // Two equal-priority candidates: make operative 12 also hold medkit loot from (0,0) so the hash decides.
        var m = Manifest("defeat");
        var first = m.Operatives[0] with { Collected = [new(0, 0, 1), new(0, 1, 1)], ConsumedLoot = [], HeldLoot = [new(0, 0, 1), new(0, 1, 1)] };
        var m2 = m with { Operatives = [first, m.Operatives[1]] };
        var picks = Enumerable.Range(0, 3).Select(_ => SettlementPlanner.Plan(m2, Context(), everything, Salt).Insurance!.Retained.Single()).Distinct().ToList();
        Assert.Single(picks);
        var salts = Enumerable.Range(0, 16).Select(i => Enumerable.Repeat((byte)i, 32).ToArray());
        var chosen = salts.Select(s => RetentionRanker.Select(s, Account,
            [new RetentionCandidate(0, 0, "item.medkit", ItemCategory.Consumable, 1), new RetentionCandidate(5, 3, "item.medkit", ItemCategory.Consumable, 1)],
            everything.Terms.LootRetention).Single().ContainerIndex).Distinct().Count();
        Assert.Equal(2, chosen);
    }

    [Fact]
    public void The_expected_bags_follow_the_plan()
    {
        var success = Plan("success").ExpectedBags;
        Assert.Equal([101L, 102L], success[0].ItemIds);
        Assert.Equal([$"settlement:{Attempt}:{Acc}:c0:l1:u0"], success[0].MintRefs);
        Assert.Equal(new Dictionary<string, int> { ["item.medkit"] = 3 }, success[0].Stacks); // 3 − 1 owned + 1 loot
        var defeat = Plan("defeat").ExpectedBags;
        Assert.Empty(defeat[1].MintRefs);
        Assert.Equal(new Dictionary<string, int> { ["item.medkit"] = 2 }, defeat[0].Stacks);
        Assert.Equal(new OperativeEquipment(11, EquipTarget.Existing(101), EquipTarget.Existing(102), EquipTarget.Empty), defeat[0].Equipment);
    }

    [Fact]
    public void The_receipt_is_deterministic_and_never_holds_the_salt()
    {
        var plan = Plan("defeat", Standard());
        var minted = plan.Mint.Select((u, i) => (u.OriginRef, Id: 500L + i)).ToDictionary(x => x.OriginRef, x => x.Id);
        byte[] Write() => ReceiptWriter.ForSettlement(plan, Manifest("defeat"), Context(), Standard(), minted, energyCost: 10, inventoryVersion: 6, Now);
        var bytes = Write();
        Assert.Equal(bytes, Write());
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(Convert.ToHexStringLower(Salt), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Salt), text, StringComparison.Ordinal);
        Assert.StartsWith("{\"accountId\":\"" + Acc + "\",\"attemptId\":\"" + Attempt + "\"", text, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"defeat\"", text, StringComparison.Ordinal);
        Assert.Contains("\"receiptVersion\":1", text, StringComparison.Ordinal);
    }
}

/// <summary>The stored admission snapshot reads back exactly what <see cref="Attempts.LoadoutSnapshot.ToJson"/> wrote.</summary>
public class SnapshotParseTests
{
    [Fact]
    public void A_snapshot_round_trips_through_its_json()
    {
        var snapshot = SettlementFixtures.Snapshot();
        var read = Blackglass.Accounts.Domain.Attempts.LoadoutSnapshot.Parse(snapshot.ToJson());
        Assert.Equal(snapshot.ToJson(), read.ToJson());
        Assert.Equal((snapshot.AccountId, snapshot.InventoryVersion, snapshot.CatalogueVersion), (read.AccountId, read.InventoryVersion, read.CatalogueVersion));
        Assert.Equal(snapshot.Operatives.Select(o => o.Equipment), read.Operatives.Select(o => o.Equipment));
    }
}
