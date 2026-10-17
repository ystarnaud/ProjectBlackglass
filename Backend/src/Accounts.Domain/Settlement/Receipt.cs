using System.Globalization;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Ids;
using static Blackglass.Accounts.Domain.Settlement.CanonicalJson;

namespace Blackglass.Accounts.Domain.Settlement;

public sealed record ReceiptConsumed(long OperativeId, string DefinitionId, int Quantity);

public sealed record ReceiptGrant(long OperativeId, string DefinitionId, int Quantity, IReadOnlyList<long> ItemIds);

/// <summary>An operative in the receipt; Alive is null for a void (no manifest says).</summary>
public sealed record ReceiptOperative(long OperativeId, bool? Alive);

public sealed record ReceiptRetained(string DefinitionId, int Quantity, IReadOnlyList<long> ItemIds);

/// <summary>The receipt's insurance part: not covered, or covered with the snapshot's policy version and what was applied.</summary>
public sealed record ReceiptInsurance(bool Covered, string? PolicyId, int? PolicyVersion, bool Applied, IReadOnlyList<RefundLine> Refunds,
    IReadOnlyList<ReceiptRetained> Retained, IReadOnlyList<XpGrant> Xp)
{
    public static readonly ReceiptInsurance NotCovered = new(false, null, null, false, [], [], []);
}

/// <summary>Everything receipt v1 says (plan §C), and nothing else: never a seed, salt, manifest byte or credential.</summary>
public sealed record ReceiptData(Guid AttemptId, long AccountId, string Outcome, DateTime SettledAt, string CatalogueVersion, int EnergyCost,
    string EnergyState, int EnergyRefunded, IReadOnlyList<ReceiptConsumed> Consumed, IReadOnlyList<ReceiptGrant> Granted, IReadOnlyList<XpGrant> Xp,
    IReadOnlyList<ReceiptOperative> Operatives, ReceiptInsurance Insurance, long InventoryVersion);

/// <summary>
/// Receipt v1 (plan §C; spec §10.5 extended): written once in the settlement or void transaction and served verbatim. The
/// bytes are canonical JSON (<see cref="CanonicalJson"/>); lists are sorted by operative id (numeric), then definition id
/// (ordinal); item ids ascending; settledAt is UTC with microseconds ("yyyy-MM-ddTHH:mm:ss.ffffffZ"); ids are Base58.
/// </summary>
public static class ReceiptWriter
{
    public const int Version = 1;

    public static byte[] Write(ReceiptData d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var insurance = d.Insurance.Covered
            ? JObj(("applied", JBool(d.Insurance.Applied)), ("covered", JBool(true)), ("policyId", JStr(d.Insurance.PolicyId!)),
                ("policyVersion", JNum(d.Insurance.PolicyVersion!.Value)),
                ("refunds", JArr(d.Insurance.Refunds.OrderBy(r => r.DefinitionId, StringComparer.Ordinal)
                    .Select(r => (Node)JObj(("definitionId", JStr(r.DefinitionId)), ("quantity", JNum(r.Quantity)))))),
                ("retained", JArr(d.Insurance.Retained.OrderBy(r => r.DefinitionId, StringComparer.Ordinal)
                    .Select(r => (Node)JObj(("definitionId", JStr(r.DefinitionId)), ("itemIds", Ids(r.ItemIds)), ("quantity", JNum(r.Quantity)))))),
                ("xp", Xp(d.Insurance.Xp)))
            : JObj(("covered", JBool(false)));
        return ToBytes(JObj(
            ("accountId", JStr(Base58Id.Format(d.AccountId))),
            ("attemptId", JStr(d.AttemptId.ToString("D"))),
            ("catalogueVersion", JStr(d.CatalogueVersion)),
            ("consumed", JArr(d.Consumed.OrderBy(x => x.OperativeId).ThenBy(x => x.DefinitionId, StringComparer.Ordinal)
                .Select(x => (Node)JObj(("definitionId", JStr(x.DefinitionId)), ("operativeId", JStr(Base58Id.Format(x.OperativeId))), ("quantity", JNum(x.Quantity)))))),
            ("energy", JObj(("cost", JNum(d.EnergyCost)), ("refunded", JNum(d.EnergyRefunded)), ("state", JStr(d.EnergyState)))),
            ("granted", JArr(d.Granted.OrderBy(x => x.OperativeId).ThenBy(x => x.DefinitionId, StringComparer.Ordinal)
                .Select(x => (Node)JObj(("definitionId", JStr(x.DefinitionId)), ("itemIds", Ids(x.ItemIds)), ("operativeId", JStr(Base58Id.Format(x.OperativeId))),
                    ("quantity", JNum(x.Quantity)))))),
            ("insurance", insurance),
            ("inventoryVersion", JNum(d.InventoryVersion)),
            ("operatives", JArr(d.Operatives.OrderBy(o => o.OperativeId)
                .Select(o => (Node)JObj(("alive", o.Alive is { } alive ? JBool(alive) : new Null()), ("operativeId", JStr(Base58Id.Format(o.OperativeId))))))),
            ("outcome", JStr(d.Outcome)),
            ("receiptVersion", JNum(Version)),
            ("settledAt", JStr(Timestamp(d.SettledAt))),
            ("xp", Xp(d.Xp))));
    }

    /// <summary>The receipt of a settlement: <paramref name="minted"/> maps each planned origin_ref to its new item id.</summary>
    public static byte[] ForSettlement(SettlementPlan plan, ResultManifest m, SettlementContext c, InsuranceSnapshot? insurance,
        IReadOnlyDictionary<string, long> minted, int energyCost, long inventoryVersion, DateTime settledAt)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(minted);
        var consumed = plan.Debits.Select(d => new ReceiptConsumed(d.OperativeId, d.DefinitionId, d.Quantity)).ToList();
        var granted = plan.Granted.Select(g => new ReceiptGrant(g.OperativeId, g.DefinitionId, g.Quantity, [.. g.MintRefs.Select(r => minted[r])])).ToList();
        var xp = plan.Outcome == "success" ? plan.Xp : [];
        var cover = insurance is null
            ? ReceiptInsurance.NotCovered
            : plan.Insurance is { } p
                ? new ReceiptInsurance(true, p.PolicyId, p.PolicyVersion, true, p.Refunds,
                    [.. p.Retained.GroupBy(r => r.DefinitionId, StringComparer.Ordinal).Select(g => new ReceiptRetained(g.Key, g.Sum(r => r.Units),
                        [.. plan.Mint.Where(u => u.Origin == "insurance" && u.DefinitionId == g.Key).Select(u => minted[u.OriginRef])]))],
                    p.Xp)
                : new ReceiptInsurance(true, insurance.PolicyId, insurance.PolicyVersion, false, [], [], []);
        return Write(new ReceiptData(m.AttemptId, m.AccountId, m.Outcome, settledAt, c.AdmissionCatalogue.Version, energyCost, "settled", 0, consumed, granted, xp,
            [.. m.Operatives.Select(o => new ReceiptOperative(o.OperativeId, o.Alive))], cover, inventoryVersion));
    }

    /// <summary>The receipt of a protective void (plan §C): the energy refunded (the policy's void refund), nothing consumed, granted or paid.</summary>
    public static byte[] ForVoid(Guid attemptId, string catalogueVersion, LoadoutSnapshot snapshot, InsuranceSnapshot? insurance, int energyCost, int refunded,
        long inventoryVersion, DateTime settledAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var cover = insurance is null ? ReceiptInsurance.NotCovered : new ReceiptInsurance(true, insurance.PolicyId, insurance.PolicyVersion, false, [], [], []);
        return Write(new ReceiptData(attemptId, snapshot.AccountId, ResultManifest.InfraVoid, settledAt, catalogueVersion, energyCost, "refunded", refunded,
            [], [], [], [.. snapshot.Operatives.Select(o => new ReceiptOperative(o.OperativeId, null))], cover, inventoryVersion));
    }

    public static string Timestamp(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)
            : throw new ArgumentException("A receipt time must be UTC.", nameof(value));

    static Arr Ids(IEnumerable<long> ids) => JArr(ids.Order().Select(id => JStr(Base58Id.Format(id))));

    static Arr Xp(IEnumerable<XpGrant> xp) => JArr(xp.OrderBy(x => x.OperativeId)
        .Select(x => (Node)JObj(("amount", JNum(x.Amount)), ("operativeId", JStr(Base58Id.Format(x.OperativeId))))));
}
