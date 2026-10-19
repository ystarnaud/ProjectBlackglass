using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// A read model of receipt v1 (Backend ReceiptWriter; Backend/contracts/manifest-v1/receipt), for Stage 16's display. Read
    /// with the strict reader because JsonUtility can't hold alive: null (a void) or the two insurance shapes. The reader refuses
    /// another receiptVersion and a missing or ill-typed required member; it tolerates unknown members, since receipts are
    /// backend-written and may gain fields. Ids stay as their Base58 text. Display only: nothing here settles anything.
    /// </summary>
    public sealed class ReceiptV1
    {
        public int ReceiptVersion { get; private set; }
        public string AttemptId { get; private set; }
        public string AccountId { get; private set; }
        /// <summary>A settled outcome word, or infra_void for a protective void.</summary>
        public string Outcome { get; private set; }
        /// <summary>As sent: UTC with six fraction digits.</summary>
        public string SettledAt { get; private set; }
        public string CatalogueVersion { get; private set; }
        public long InventoryVersion { get; private set; }
        public ReceiptEnergy Energy { get; private set; }
        public IReadOnlyList<ReceiptLine> Consumed { get; private set; }
        public IReadOnlyList<ReceiptGrant> Granted { get; private set; }
        public IReadOnlyList<ReceiptXp> Xp { get; private set; }
        public IReadOnlyList<ReceiptOperative> Operatives { get; private set; }
        public ReceiptInsurance Insurance { get; private set; }

        sealed class Invalid : Exception { }

        public static bool TryRead(byte[] utf8, out ReceiptV1 receipt)
        {
            receipt = null;
            if (utf8 == null || !StrictJsonReader.TryRead(utf8, out var root)) return false;
            try
            {
                var m = Members(root);
                if (Int(m, "receiptVersion") != 1) return false;
                receipt = new ReceiptV1
                {
                    ReceiptVersion = 1,
                    AttemptId = Str(m, "attemptId"),
                    AccountId = Str(m, "accountId"),
                    Outcome = Str(m, "outcome"),
                    SettledAt = Str(m, "settledAt"),
                    CatalogueVersion = Str(m, "catalogueVersion"),
                    InventoryVersion = Long(m, "inventoryVersion"),
                    Energy = ReadEnergy(Members(Get(m, "energy"))),
                    Consumed = List(m, "consumed", e => { var c = Members(e); return new ReceiptLine(Str(c, "operativeId"), Str(c, "definitionId"), Int(c, "quantity")); }),
                    Granted = List(m, "granted", e =>
                    {
                        var g = Members(e);
                        return new ReceiptGrant(Str(g, "operativeId"), Str(g, "definitionId"), Int(g, "quantity"), Strings(g, "itemIds"));
                    }),
                    Xp = List(m, "xp", ReadXp),
                    Operatives = List(m, "operatives", e =>
                    {
                        var o = Members(e);
                        var alive = Get(o, "alive");
                        if (alive is JsonNull) return new ReceiptOperative(Str(o, "operativeId"), null);
                        return new ReceiptOperative(Str(o, "operativeId"), alive is JsonBool b ? b.Value : throw new Invalid());
                    }),
                    Insurance = ReadInsurance(Members(Get(m, "insurance"))),
                };
                return true;
            }
            catch (Invalid)
            {
                receipt = null;
                return false;
            }
        }

        static ReceiptEnergy ReadEnergy(Dictionary<string, JsonNode> e) => new ReceiptEnergy(Int(e, "cost"), Int(e, "refunded"), Str(e, "state"));

        static ReceiptXp ReadXp(JsonNode e)
        {
            var x = Members(e);
            return new ReceiptXp(Str(x, "operativeId"), Int(x, "amount"));
        }

        static ReceiptInsurance ReadInsurance(Dictionary<string, JsonNode> i)
        {
            if (!(Get(i, "covered") is JsonBool covered)) throw new Invalid();
            if (!covered.Value) return ReceiptInsurance.NotCovered;
            return new ReceiptInsurance(true, Bool(i, "applied"), Str(i, "policyId"), Int(i, "policyVersion"),
                List(i, "refunds", e => { var r = Members(e); return new ReceiptRefund(Str(r, "definitionId"), Int(r, "quantity")); }),
                List(i, "retained", e => { var r = Members(e); return new ReceiptRetained(Str(r, "definitionId"), Int(r, "quantity"), Strings(r, "itemIds")); }),
                List(i, "xp", ReadXp));
        }

        static Dictionary<string, JsonNode> Members(JsonNode node)
        {
            if (!(node is JsonObject o)) throw new Invalid();
            var found = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            foreach (var p in o.Members)
            {
                if (p.NameIsInvalidUtf16 || found.ContainsKey(p.Name)) throw new Invalid();
                found.Add(p.Name, p.Value);
            }
            return found;
        }

        static JsonNode Get(Dictionary<string, JsonNode> m, string name) => m.TryGetValue(name, out var v) ? v : throw new Invalid();

        static string Str(Dictionary<string, JsonNode> m, string name) =>
            Get(m, name) is JsonString s && !s.IsInvalidUtf16 ? s.Value : throw new Invalid();

        static bool Bool(Dictionary<string, JsonNode> m, string name) => Get(m, name) is JsonBool b ? b.Value : throw new Invalid();

        static long Long(Dictionary<string, JsonNode> m, string name) =>
            Get(m, name) is JsonNumber n && long.TryParse(n.RawText, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : throw new Invalid();

        static int Int(Dictionary<string, JsonNode> m, string name)
        {
            var v = Long(m, name);
            return v <= int.MaxValue ? (int)v : throw new Invalid();
        }

        static IReadOnlyList<T> List<T>(Dictionary<string, JsonNode> m, string name, Func<JsonNode, T> read)
        {
            if (!(Get(m, name) is JsonArray a)) throw new Invalid();
            var list = new List<T>(a.Items.Count);
            foreach (var item in a.Items) list.Add(read(item));
            return new ReadOnlyCollection<T>(list);
        }

        static IReadOnlyList<string> Strings(Dictionary<string, JsonNode> m, string name) =>
            List(m, name, e => e is JsonString s && !s.IsInvalidUtf16 ? s.Value : throw new Invalid());
    }

    public sealed class ReceiptEnergy
    {
        public ReceiptEnergy(int cost, int refunded, string state) { Cost = cost; Refunded = refunded; State = state; }
        public int Cost { get; }
        public int Refunded { get; }
        public string State { get; }
    }

    public sealed class ReceiptLine
    {
        public ReceiptLine(string operativeId, string definitionId, int quantity) { OperativeId = operativeId; DefinitionId = definitionId; Quantity = quantity; }
        public string OperativeId { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    public sealed class ReceiptGrant
    {
        public ReceiptGrant(string operativeId, string definitionId, int quantity, IReadOnlyList<string> itemIds)
        {
            OperativeId = operativeId; DefinitionId = definitionId; Quantity = quantity; ItemIds = itemIds;
        }
        public string OperativeId { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }
        public IReadOnlyList<string> ItemIds { get; }
    }

    public sealed class ReceiptXp
    {
        public ReceiptXp(string operativeId, int amount) { OperativeId = operativeId; Amount = amount; }
        public string OperativeId { get; }
        public int Amount { get; }
    }

    /// <summary>An operative in the receipt; Alive is null for a void (no manifest said).</summary>
    public sealed class ReceiptOperative
    {
        public ReceiptOperative(string operativeId, bool? alive) { OperativeId = operativeId; Alive = alive; }
        public string OperativeId { get; }
        public bool? Alive { get; }
    }

    public sealed class ReceiptRefund
    {
        public ReceiptRefund(string definitionId, int quantity) { DefinitionId = definitionId; Quantity = quantity; }
        public string DefinitionId { get; }
        public int Quantity { get; }
    }

    public sealed class ReceiptRetained
    {
        public ReceiptRetained(string definitionId, int quantity, IReadOnlyList<string> itemIds) { DefinitionId = definitionId; Quantity = quantity; ItemIds = itemIds; }
        public string DefinitionId { get; }
        public int Quantity { get; }
        public IReadOnlyList<string> ItemIds { get; }
    }

    /// <summary>Not covered ({covered:false}), or covered with the snapshot's policy and what was applied.</summary>
    public sealed class ReceiptInsurance
    {
        public static readonly ReceiptInsurance NotCovered = new ReceiptInsurance(false, false, null, 0,
            new ReceiptRefund[0], new ReceiptRetained[0], new ReceiptXp[0]);

        public ReceiptInsurance(bool covered, bool applied, string policyId, int policyVersion, IReadOnlyList<ReceiptRefund> refunds,
            IReadOnlyList<ReceiptRetained> retained, IReadOnlyList<ReceiptXp> xp)
        {
            Covered = covered; Applied = applied; PolicyId = policyId; PolicyVersion = policyVersion; Refunds = refunds; Retained = retained; Xp = xp;
        }

        public bool Covered { get; }
        public bool Applied { get; }
        public string PolicyId { get; }
        public int PolicyVersion { get; }
        public IReadOnlyList<ReceiptRefund> Refunds { get; }
        public IReadOnlyList<ReceiptRetained> Retained { get; }
        public IReadOnlyList<ReceiptXp> Xp { get; }
    }
}
