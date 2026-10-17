using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Insurance;

namespace Blackglass.Accounts.Domain.Settlement;

/// <summary>A participant's insurance snapshot as admission captured it (S7-D3): the policy version and its complete terms.</summary>
public sealed record InsuranceSnapshot(string PolicyId, int PolicyVersion, InsuranceTerms Terms);

/// <summary>Owned units consumed from an operative's stack: delete the row when it held exactly this many, else decrement.</summary>
public sealed record OwnedDebit(long OperativeId, string DefinitionId, int Quantity);

/// <summary>One non-stackable unit to mint, with its unique (origin, origin_ref). OperativeId null is the stash.</summary>
public sealed record MintUnit(long? OperativeId, string DefinitionId, string Origin, string OriginRef);

/// <summary>Stackable units to add (upsert) at a location. OperativeId null is the stash.</summary>
public sealed record StackCredit(long? OperativeId, string DefinitionId, int Quantity);

/// <summary>What fills a slot after settlement: an existing item, a unit minted by this settlement (by origin_ref), or nothing.</summary>
public sealed record EquipTarget(long? ItemId, string? MintRef)
{
    public static readonly EquipTarget Empty = new(null, null);

    public static EquipTarget Existing(long itemId) => new(itemId, null);

    public static EquipTarget Minted(string originRef) => new(null, originRef);
}

public sealed record OperativeEquipment(long OperativeId, EquipTarget Weapon, EquipTarget Armor, EquipTarget Utility);

public sealed record XpGrant(long OperativeId, int Amount);

/// <summary>A retained loot line (insurance): units kept from (ContainerIndex, Line), whose definition is DefinitionId.</summary>
public sealed record RetainedLot(int ContainerIndex, int Line, string DefinitionId, int Units);

public sealed record RefundLine(string DefinitionId, int Quantity);

public sealed record InsurancePayout(string PolicyId, int PolicyVersion, IReadOnlyList<RefundLine> Refunds, IReadOnlyList<RetainedLot> Retained, IReadOnlyList<XpGrant> Xp);

/// <summary>An operative's bag and equipment as the settlement must leave them (S9-B14.3's post-apply check).</summary>
public sealed record ExpectedBag(long OperativeId, IReadOnlyList<long> ItemIds, IReadOnlyList<string> MintRefs, IReadOnlyDictionary<string, int> Stacks,
    OperativeEquipment Equipment);

/// <summary>A loot grant to an operative's bag on success, for the receipt: the definition, the units and the minted refs.</summary>
public sealed record GrantLine(long OperativeId, string DefinitionId, int Quantity, IReadOnlyList<string> MintRefs);

/// <summary>
/// The immutable result of planning one participant's settlement. Equipment is empty unless the outcome is success (failure
/// keeps the stored equipment, A-23). Insurance is the applied payout (null when uninsured or the outcome is not covered);
/// Insured says whether a snapshot existed. Collections are this manifest's per-line collected totals (pool_collections).
/// </summary>
public sealed record SettlementPlan(string Outcome, IReadOnlyList<OwnedDebit> Debits, IReadOnlyList<MintUnit> Mint, IReadOnlyList<StackCredit> Credits,
    IReadOnlyList<OperativeEquipment> Equipment, IReadOnlyList<XpGrant> Xp, bool Insured, InsurancePayout? Insurance,
    IReadOnlyList<ExpectedBag> ExpectedBags, IReadOnlyDictionary<(int ContainerIndex, int Line), int> Collections, IReadOnlyList<GrantLine> Granted);

/// <summary>
/// The pure settlement plan (S9-B11-B14) for a manifest that passed <see cref="ManifestValidator"/>, computed from the
/// admission catalogue, the snapshot and the insurance snapshot only. Spec §8.1:
/// <list type="bullet">
/// <item>every outcome expends the owned consumables the manifest consumed;</item>
/// <item>success grants the held loot to the holding operative's bag (non-stackables minted one unit per reference
/// <c>settlement:&lt;attempt&gt;:&lt;account&gt;:c&lt;ci&gt;:l&lt;line&gt;:u&lt;k&gt;</c>), applies the mission equipment, and gives
/// missionCompletionXp to every snapshot operative, dead included;</item>
/// <item>a covered failure refunds floor(units × percent / 100) per consumed definition to the stash, retains the ranked loot
/// lines to the stash (refs <c>insurance:…</c>), and gives each operative the progress XP; an uncovered failure only expends.</item>
/// </list>
/// All arithmetic is 64-bit with a single floor, capped at int.MaxValue.
/// </summary>
public static class SettlementPlanner
{
    public static SettlementPlan Plan(ResultManifest m, SettlementContext c, InsuranceSnapshot? insurance, byte[] retentionSalt)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(retentionSalt);
        var catalogue = c.AdmissionCatalogue;
        var pool = c.Pool.ToDictionary(l => (l.ContainerIndex, l.Line));
        var account = Base58Id.Format(m.AccountId);
        var attempt = m.AttemptId.ToString("D");
        var operatives = m.Operatives.OrderBy(o => o.Slot).ToList();
        var success = m.Outcome == "success";

        var debits = operatives.SelectMany(o => o.ConsumedOwned.OrderBy(u => u.DefinitionId, StringComparer.Ordinal)
            .Select(u => new OwnedDebit(o.OperativeId, u.DefinitionId, u.Quantity))).ToList();
        var mint = new List<MintUnit>();
        var credits = new List<StackCredit>();
        var equipment = new List<OperativeEquipment>();
        var xp = new List<XpGrant>();
        var granted = new List<GrantLine>();
        InsurancePayout? payout = null;

        if (success)
        {
            foreach (var o in operatives)
            {
                var stackUnits = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var unitRefs = new SortedDictionary<string, (int Quantity, List<string> Refs)>(StringComparer.Ordinal);
                foreach (var lot in o.HeldLoot.OrderBy(h => h.ContainerIndex).ThenBy(h => h.Line))
                {
                    var line = pool[(lot.ContainerIndex, lot.Line)];
                    var item = catalogue.FindItem(line.DefinitionId) ?? throw new InvalidOperationException("A validated loot line has no catalogue item.");
                    if (item.Stackable)
                    {
                        stackUnits[line.DefinitionId] = checked(stackUnits.GetValueOrDefault(line.DefinitionId) + lot.Quantity);
                        continue;
                    }
                    var entry = unitRefs.TryGetValue(line.DefinitionId, out var seen) ? seen : (0, new List<string>());
                    for (var k = 0; k < lot.Quantity; k++)
                    {
                        var originRef = Ref("settlement", attempt, account, lot.ContainerIndex, lot.Line, k);
                        mint.Add(new MintUnit(o.OperativeId, line.DefinitionId, "settlement", originRef));
                        entry.Item2.Add(originRef);
                    }
                    unitRefs[line.DefinitionId] = (entry.Item1 + lot.Quantity, entry.Item2);
                }
                foreach (var (definition, quantity) in stackUnits)
                {
                    credits.Add(new StackCredit(o.OperativeId, definition, quantity));
                    granted.Add(new GrantLine(o.OperativeId, definition, quantity, []));
                }
                foreach (var (definition, (quantity, refs)) in unitRefs) granted.Add(new GrantLine(o.OperativeId, definition, quantity, refs));
                equipment.Add(new OperativeEquipment(o.OperativeId, Target(o, "weapon", attempt, account), Target(o, "armor", attempt, account), Target(o, "utility", attempt, account)));
            }
            granted = [.. granted.OrderBy(g => g.OperativeId).ThenBy(g => g.DefinitionId, StringComparer.Ordinal)];
            var completion = catalogue.Document.Progression.MissionCompletionXp;
            xp.AddRange(c.Snapshot.Operatives.OrderBy(o => o.Slot).Select(o => new XpGrant(o.OperativeId, completion)));
        }
        else if (insurance is not null && insurance.Terms.Covers.Contains(m.Outcome))
        {
            var terms = insurance.Terms;
            var refunds = debits.GroupBy(d => d.DefinitionId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new RefundLine(g.Key, Refund(g.Sum(d => (long)d.Quantity), terms.ConsumableRefund.Percent))).ToList();
            foreach (var r in refunds.Where(r => r.Quantity > 0)) credits.Add(new StackCredit(null, r.DefinitionId, r.Quantity));

            var candidates = operatives.SelectMany(o => o.HeldLoot).GroupBy(h => (h.ContainerIndex, h.Line)).OrderBy(g => g.Key)
                .Select(g =>
                {
                    var line = pool[g.Key];
                    var item = catalogue.FindItem(line.DefinitionId) ?? throw new InvalidOperationException("A validated loot line has no catalogue item.");
                    return new RetentionCandidate(g.Key.ContainerIndex, g.Key.Line, line.DefinitionId, item.Category, checked(g.Sum(h => h.Quantity)));
                }).ToList();
            var retained = RetentionRanker.Select(retentionSalt, m.AccountId, candidates, terms.LootRetention);
            foreach (var lot in retained)
            {
                var item = catalogue.FindItem(lot.DefinitionId)!;
                if (item.Stackable) credits.Add(new StackCredit(null, lot.DefinitionId, lot.Units));
                else
                    for (var k = 0; k < lot.Units; k++)
                        mint.Add(new MintUnit(null, lot.DefinitionId, "insurance", Ref("insurance", attempt, account, lot.ContainerIndex, lot.Line, k)));
            }

            var required = c.Objectives.Where(o => o.Required).Select(o => o.ObjectiveId).ToHashSet(StringComparer.Ordinal);
            var completed = m.Objectives.Count(o => required.Contains(o.ObjectiveId) && o.State == "completed");
            var amount = ProgressXp(catalogue.Document.Progression.MissionCompletionXp, completed, required.Count, terms.ProgressXp.Percent);
            var progress = c.Snapshot.Operatives.OrderBy(o => o.Slot).Select(o => new XpGrant(o.OperativeId, amount)).ToList();
            xp.AddRange(progress);
            payout = new InsurancePayout(insurance.PolicyId, insurance.PolicyVersion, refunds, retained, progress);
        }

        var bags = new List<ExpectedBag>();
        foreach (var snap in c.Snapshot.Operatives.OrderBy(o => o.Slot))
        {
            var stacks = snap.Stacks.ToDictionary(s => s.DefinitionId, s => s.Quantity, StringComparer.Ordinal);
            foreach (var d in debits.Where(d => d.OperativeId == snap.OperativeId))
            {
                var left = stacks[d.DefinitionId] - d.Quantity;
                if (left > 0) stacks[d.DefinitionId] = left;
                else stacks.Remove(d.DefinitionId);
            }
            foreach (var credit in credits.Where(x => x.OperativeId == snap.OperativeId))
                stacks[credit.DefinitionId] = checked(stacks.GetValueOrDefault(credit.DefinitionId) + credit.Quantity);
            var refs = mint.Where(u => u.OperativeId == snap.OperativeId).Select(u => u.OriginRef).ToList();
            var gear = equipment.FirstOrDefault(e => e.OperativeId == snap.OperativeId)
                ?? new OperativeEquipment(snap.OperativeId, Existing(snap.Equipment.Weapon), Existing(snap.Equipment.Armor), Existing(snap.Equipment.Utility));
            bags.Add(new ExpectedBag(snap.OperativeId, [.. snap.Instances.Select(i => i.ItemId).Order()], refs, stacks, gear));
        }

        var collections = operatives.SelectMany(o => o.Collected).GroupBy(x => (x.ContainerIndex, x.Line))
            .ToDictionary(g => g.Key, g => checked(g.Sum(x => x.Quantity)));
        return new SettlementPlan(m.Outcome, debits, mint, credits, equipment, xp, insurance is not null, payout, bags, collections, granted);
    }

    static EquipTarget Existing(long? id) => id is { } x ? EquipTarget.Existing(x) : EquipTarget.Empty;

    static EquipTarget Target(ManifestOperative o, string slot, string attempt, string account)
    {
        var e = o.Equipment.FirstOrDefault(x => x.Slot == slot);
        if (e is null) return EquipTarget.Empty;
        return e.ItemId is { } id ? EquipTarget.Existing(id) : EquipTarget.Minted(Ref("settlement", attempt, account, e.ContainerIndex!.Value, e.Line!.Value, 0));
    }

    /// <summary>A minted unit's origin_ref (S9-B11.2).</summary>
    public static string Ref(string origin, string attempt, string account, int containerIndex, int line, int unit) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{origin}:{attempt}:{account}:c{containerIndex}:l{line}:u{unit}");

    /// <summary>floor(MCX × completed × percent / (required × 100)), one floor in 64-bit arithmetic; 0 when nothing is required (S9-B13.2).</summary>
    public static int ProgressXp(int missionCompletionXp, int completedRequired, int totalRequired, int percent)
    {
        if (totalRequired <= 0) return 0;
        var value = (Int128)missionCompletionXp * completedRequired * percent / ((Int128)totalRequired * 100);
        return (int)Int128.Min(value, int.MaxValue);
    }

    /// <summary>floor(units × percent / 100) in 64-bit arithmetic (S9-B13.3).</summary>
    public static int Refund(long units, int percent) => (int)Int128.Min((Int128)units * percent / 100, int.MaxValue);
}
