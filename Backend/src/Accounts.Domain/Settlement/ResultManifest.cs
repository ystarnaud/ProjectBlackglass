using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Hosts;
using Blackglass.Accounts.Domain.Ids;
using static Blackglass.Accounts.Domain.Settlement.CanonicalJson;

namespace Blackglass.Accounts.Domain.Settlement;

public sealed record ObjectiveState(string ObjectiveId, string State);

/// <summary>Owned units of one consumable definition an operative used.</summary>
public sealed record OwnedConsumption(string DefinitionId, int Quantity);

/// <summary>Units an operative used from a loot line it collected; DefinitionId must be the pool line's.</summary>
public sealed record LootConsumption(int ContainerIndex, int Line, string DefinitionId, int Quantity);

/// <summary>An owned stack held at the freeze.</summary>
public sealed record OwnedStack(string DefinitionId, int Quantity);

/// <summary>Units of a loot line held at the freeze.</summary>
public sealed record LootLot(int ContainerIndex, int Line, int Quantity);

/// <summary>Units taken out of a pool line (collected).</summary>
public sealed record LineCount(int ContainerIndex, int Line, int Quantity);

/// <summary>A slot and what fills it: an owned item id, or a held loot line (exactly one of the two).</summary>
public sealed record EquipmentRef(string Slot, long? ItemId, int? ContainerIndex, int? Line)
{
    public static EquipmentRef Item(string slot, long itemId) => new(slot, itemId, null, null);

    public static EquipmentRef Loot(string slot, int containerIndex, int line) => new(slot, null, containerIndex, line);
}

public sealed record ManifestOperative(long OperativeId, int Slot, bool Alive, IReadOnlyList<OwnedConsumption> ConsumedOwned,
    IReadOnlyList<LootConsumption> ConsumedLoot, IReadOnlyList<long> HeldItems, IReadOnlyList<OwnedStack> HeldStacks, IReadOnlyList<LootLot> HeldLoot,
    IReadOnlyList<LineCount> Collected, IReadOnlyList<EquipmentRef> Equipment);

/// <summary>The bounded refusal codes of a body that is not a format-v1 manifest (the 422 codes, plan §C).</summary>
public static class ManifestCodes
{
    public const string TooLarge = "TooLarge";
    public const string NotJson = "NotJson";
    public const string TooDeep = "TooDeep";
    public const string WrongType = "WrongType";
    public const string DuplicateKey = "DuplicateKey";
    public const string UnknownMember = "UnknownMember";
    public const string MissingMember = "MissingMember";
    public const string UnsupportedFormat = "UnsupportedFormat";
    public const string NotInteger = "NotInteger";
    public const string OutOfBounds = "OutOfBounds";
    public const string NonCanonicalId = "NonCanonicalId";
    public const string BadValue = "BadValue";
    public const string BadEnum = "BadEnum";
    public const string BackendOnlyOutcome = "BackendOnlyOutcome";
    public const string DuplicateEntry = "DuplicateEntry";

    public static readonly IReadOnlyList<string> All =
    [
        TooLarge, NotJson, TooDeep, WrongType, DuplicateKey, UnknownMember, MissingMember, UnsupportedFormat, NotInteger, OutOfBounds,
        NonCanonicalId, BadValue, BadEnum, BackendOnlyOutcome, DuplicateEntry,
    ];
}

public abstract record ParseResult
{
    /// <param name="ManifestHash">"sha256:" and the lowercase hex SHA-256 of <paramref name="CanonicalBytes"/>.</param>
    public sealed record Parsed(ResultManifest Manifest, byte[] CanonicalBytes, string ManifestHash) : ParseResult;

    /// <summary>Not a format-v1 manifest. The claimed ids are the top-level attemptId and accountId when each occurs once and
    /// is canonical, whatever else is wrong; otherwise null.</summary>
    public sealed record Malformed(string Code, Guid? ClaimedAttemptId, long? ClaimedAccountId) : ParseResult;
}

/// <summary>
/// One participant's result manifest, format v1 (S9-B4, plan §G; Backend/contracts/manifest-v1/README.md is the prose and its
/// vectors the authority). Facts only: no XP, rewards, energy, insurance or seed. Parse never throws on bad input: it returns
/// <see cref="ParseResult.Malformed"/> with the first refusal met, in a fixed order (size, UTF-8 and JSON syntax, depth, then
/// the structure in member order: duplicates, unknown members, missing members, formatVersion first, then each member in
/// ordinal key order). A parseable body in a non-canonical encoding (whitespace, member order, list order, escapes) is
/// accepted; <see cref="ToCanonicalBytes"/> writes the one canonical form.
/// </summary>
public sealed partial record ResultManifest(Guid AttemptId, long AccountId, Guid ProcessSessionId, string BuildRevision, string CatalogueVersion,
    string Integrity, string Outcome, int OutcomeSequence, int ServerElapsedMs, string LayoutHash, int LootTableVersion, string LootHash,
    IReadOnlyList<ObjectiveState> Objectives, IReadOnlyList<ManifestOperative> Operatives)
{
    public const int MaxBytes = 65_536;
    public const int FormatVersion = 1;
    public const int MaxElapsedMs = 86_400_000;
    public const int MaxDepthBelowRoot = 4;
    public const int MaxOperatives = 8;
    public const int MaxObjectives = 64;
    public const int MaxEntries = 256;
    public const int MaxStringBytes = 128;
    public const int MaxContainerOrLine = 65_535;

    public static readonly IReadOnlyList<string> Outcomes = ["success", "defeat", "abandoned", "disconnected", "expired"];
    public const string InfraVoid = "infra_void";
    public static readonly IReadOnlyList<string> Integrities = ["ok", "ledgerMismatch"];
    public static readonly IReadOnlyList<string> ObjectiveStates = ["active", "completed", "failed"];
    public static readonly IReadOnlyList<string> Slots = ["armor", "utility", "weapon"];

    static readonly string[] RootMembers =
    [
        "accountId", "attemptId", "buildRevision", "catalogueVersion", "formatVersion", "integrity", "layoutHash", "lootHash", "lootTableVersion",
        "objectives", "operatives", "outcome", "outcomeSequence", "processSessionId", "serverElapsedMs",
    ];

    static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 };

    [GeneratedRegex(@"^(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerLiteral();

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^[0-9a-f]{16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LayoutPattern();

    sealed class Refusal(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }

    static Refusal Refuse(string code) => new(code);

    public static ParseResult Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > MaxBytes) return new ParseResult.Malformed(ManifestCodes.TooLarge, null, null);
        if (!System.Text.Unicode.Utf8.IsValid(raw)) return new ParseResult.Malformed(ManifestCodes.NotJson, null, null);
        JsonDocument document;
        try { document = JsonDocument.Parse(raw.ToArray(), Options); }
        catch (JsonException) { return new ParseResult.Malformed(ManifestCodes.NotJson, null, null); }
        using (document)
        {
            var root = document.RootElement;
            var (claimedAttempt, claimedAccount) = Claims(root);
            try
            {
                CheckDepth(root, 0);
                var manifest = ReadRoot(root);
                var canonical = manifest.ToCanonicalBytes();
                return new ParseResult.Parsed(manifest, canonical, HashOf(canonical));
            }
            catch (Refusal r) { return new ParseResult.Malformed(r.Code, claimedAttempt, claimedAccount); }
            catch (InvalidOperationException) { return new ParseResult.Malformed(ManifestCodes.NotJson, claimedAttempt, claimedAccount); } // e.g. a lone surrogate escape
        }
    }

    /// <summary>"sha256:" and the lowercase hex SHA-256 of <paramref name="bytes"/>.</summary>
    public static string HashOf(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    static (Guid?, long?) Claims(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, null);
        Guid? attempt = null;
        long? account = null;
        int attempts = 0, accounts = 0;
        foreach (var p in root.EnumerateObject())
        {
            if (NameIs(p, "attemptId"))
            {
                attempts++;
                attempt = p.Value.ValueKind == JsonValueKind.String && IsAttemptId(SafeString(p.Value), out var a) ? a : null;
            }
            else if (NameIs(p, "accountId"))
            {
                accounts++;
                account = p.Value.ValueKind == JsonValueKind.String && Base58Id.TryParse(SafeString(p.Value), out var b) ? b : null;
            }
        }
        return (attempts == 1 ? attempt : null, accounts == 1 ? account : null);
    }

    // A name holding a lone surrogate escape can't be unescaped (NameEquals throws once the name is long enough to need it),
    // and it is neither ASCII claim name; ReadRoot then refuses the body as NotJson (Stage 11 final review M1).
    static bool NameIs(JsonProperty p, string name)
    {
        try { return p.NameEquals(name); }
        catch (InvalidOperationException) { return false; }
    }

    static string? SafeString(JsonElement e)
    {
        try { return e.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    static bool IsAttemptId(string? text, out Guid id) =>
        CanonicalUuid.TryParse(text, out id) && text![14] == '7' && text[19] is '8' or '9' or 'a' or 'b';

    static void CheckDepth(JsonElement e, int depth)
    {
        if (e.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return;
        if (depth > MaxDepthBelowRoot) throw Refuse(ManifestCodes.TooDeep);
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject()) CheckDepth(p.Value, depth + 1);
        else
            foreach (var item in e.EnumerateArray()) CheckDepth(item, depth + 1);
    }

    static ResultManifest ReadRoot(JsonElement root)
    {
        var m = Members(root, RootMembers);
        if (Int(m["formatVersion"], 0, int.MaxValue) != FormatVersion) throw Refuse(ManifestCodes.UnsupportedFormat);
        var account = Base58(m["accountId"]);
        var attemptText = Str(m["attemptId"]);
        if (!IsAttemptId(attemptText, out var attempt)) throw Refuse(ManifestCodes.NonCanonicalId);
        var build = Str(m["buildRevision"]);
        if (!BuildIdentity.IsWellFormedRevision(build)) throw Refuse(ManifestCodes.BadValue);
        var catalogue = Hash(m["catalogueVersion"]);
        var integrity = OneOf(m["integrity"], Integrities);
        var layout = Str(m["layoutHash"]);
        if (!LayoutPattern().IsMatch(layout)) throw Refuse(ManifestCodes.NonCanonicalId);
        var lootHash = Hash(m["lootHash"]);
        var tableVersion = Int(m["lootTableVersion"], 1, int.MaxValue);
        var objectives = Distinct(ListOf(m["objectives"], 0, MaxObjectives, ReadObjective), o => o.ObjectiveId);
        var operatives = ListOf(m["operatives"], 1, MaxOperatives, ReadOperative);
        Distinct(operatives, o => o.Slot);
        Distinct(operatives, o => o.OperativeId);
        var outcomeText = Str(m["outcome"]);
        if (outcomeText == InfraVoid) throw Refuse(ManifestCodes.BackendOnlyOutcome);
        if (!Outcomes.Contains(outcomeText)) throw Refuse(ManifestCodes.BadEnum);
        var sequence = Int(m["outcomeSequence"], 0, int.MaxValue);
        var sessionText = Str(m["processSessionId"]);
        if (!CanonicalUuid.TryParse(sessionText, out var session)) throw Refuse(ManifestCodes.NonCanonicalId);
        var elapsed = Int(m["serverElapsedMs"], 0, MaxElapsedMs);
        return new ResultManifest(attempt, account, session, build, catalogue, integrity, outcomeText, sequence, elapsed, layout, tableVersion, lootHash,
            [.. objectives.OrderBy(o => o.ObjectiveId, StringComparer.Ordinal)], [.. operatives.OrderBy(o => o.Slot)]);
    }

    static ObjectiveState ReadObjective(JsonElement e)
    {
        var o = Members(e, "objectiveId", "state");
        var id = Str(o["objectiveId"]);
        if (!StrictJson.IsWord(id, ObjectiveList.MaxIdLength)) throw Refuse(ManifestCodes.BadValue);
        return new ObjectiveState(id, OneOf(o["state"], ObjectiveStates));
    }

    static ManifestOperative ReadOperative(JsonElement e)
    {
        var o = Members(e, "alive", "collected", "consumed", "equipment", "held", "operativeId", "slot");
        var alive = Bool(o["alive"]);
        var collected = Distinct(ListOf(o["collected"], 0, MaxEntries, c =>
        {
            var l = Members(c, "containerIndex", "line", "quantity");
            return new LineCount(Index(l["containerIndex"]), Index(l["line"]), Quantity(l["quantity"]));
        }), c => (c.ContainerIndex, c.Line));
        var consumedOwned = new List<OwnedConsumption>();
        var consumedLoot = new List<LootConsumption>();
        foreach (var item in ListOf(o["consumed"], 0, MaxEntries, c => c))
        {
            var raw = RawMembers(item);
            var source = OneOf(Required(raw, "source"), ["loot", "owned"]);
            if (source == "loot")
            {
                Exact(raw, "containerIndex", "definitionId", "line", "quantity", "source");
                consumedLoot.Add(new LootConsumption(Index(raw["containerIndex"]), Index(raw["line"]), Definition(raw["definitionId"]), Quantity(raw["quantity"])));
            }
            else
            {
                Exact(raw, "definitionId", "quantity", "source");
                consumedOwned.Add(new OwnedConsumption(Definition(raw["definitionId"]), Quantity(raw["quantity"])));
            }
        }
        Distinct(consumedLoot, c => (c.ContainerIndex, c.Line));
        Distinct(consumedOwned, c => c.DefinitionId);
        var equipment = Distinct(ListOf(o["equipment"], 0, MaxEntries, c =>
        {
            var raw = RawMembers(c);
            if (raw.ContainsKey("itemId"))
            {
                Exact(raw, "itemId", "slot");
                return EquipmentRef.Item(OneOf(raw["slot"], Slots), Base58(raw["itemId"]));
            }
            Exact(raw, "containerIndex", "line", "slot");
            return EquipmentRef.Loot(OneOf(raw["slot"], Slots), Index(raw["containerIndex"]), Index(raw["line"]));
        }), r => r.Slot);
        var heldItems = new List<long>();
        var heldStacks = new List<OwnedStack>();
        var heldLoot = new List<LootLot>();
        foreach (var item in ListOf(o["held"], 0, MaxEntries, c => c))
        {
            var raw = RawMembers(item);
            var origin = OneOf(Required(raw, "origin"), ["loot", "owned"]);
            if (origin == "loot")
            {
                Exact(raw, "containerIndex", "line", "origin", "quantity");
                heldLoot.Add(new LootLot(Index(raw["containerIndex"]), Index(raw["line"]), Quantity(raw["quantity"])));
            }
            else if (raw.ContainsKey("itemId"))
            {
                Exact(raw, "itemId", "origin");
                heldItems.Add(Base58(raw["itemId"]));
            }
            else
            {
                Exact(raw, "definitionId", "origin", "quantity");
                heldStacks.Add(new OwnedStack(Definition(raw["definitionId"]), Quantity(raw["quantity"])));
            }
        }
        Distinct(heldLoot, h => (h.ContainerIndex, h.Line));
        Distinct(heldItems, h => h);
        Distinct(heldStacks, h => h.DefinitionId);
        var operativeId = Base58(o["operativeId"]);
        var slot = Int(o["slot"], 0, MaxOperatives - 1);
        return Sorted(new ManifestOperative(operativeId, slot, alive, consumedOwned, consumedLoot, heldItems, heldStacks, heldLoot, collected, equipment));
    }

    /// <summary>The operative with every list in its canonical order.</summary>
    static ManifestOperative Sorted(ManifestOperative o) => o with
    {
        ConsumedOwned = [.. o.ConsumedOwned.OrderBy(c => c.DefinitionId, StringComparer.Ordinal)],
        ConsumedLoot = [.. o.ConsumedLoot.OrderBy(c => c.ContainerIndex).ThenBy(c => c.Line)],
        HeldItems = [.. o.HeldItems.Order()],
        HeldStacks = [.. o.HeldStacks.OrderBy(h => h.DefinitionId, StringComparer.Ordinal)],
        HeldLoot = [.. o.HeldLoot.OrderBy(h => h.ContainerIndex).ThenBy(h => h.Line)],
        Collected = [.. o.Collected.OrderBy(c => c.ContainerIndex).ThenBy(c => c.Line)],
        Equipment = [.. o.Equipment.OrderBy(e => e.Slot, StringComparer.Ordinal)],
    };

    // ---- the canonical writer ----

    /// <summary>The one canonical encoding (plan §G): keys in ordinal order, lists in their stated order, no whitespace.</summary>
    public byte[] ToCanonicalBytes() => CanonicalJson.ToBytes(JObj(
        ("accountId", JStr(Base58Id.Format(AccountId))),
        ("attemptId", JStr(AttemptId.ToString("D"))),
        ("buildRevision", JStr(BuildRevision)),
        ("catalogueVersion", JStr(CatalogueVersion)),
        ("formatVersion", JNum(FormatVersion)),
        ("integrity", JStr(Integrity)),
        ("layoutHash", JStr(LayoutHash)),
        ("lootHash", JStr(LootHash)),
        ("lootTableVersion", JNum(LootTableVersion)),
        ("objectives", JArr(Objectives.OrderBy(o => o.ObjectiveId, StringComparer.Ordinal)
            .Select(o => (Node)JObj(("objectiveId", JStr(o.ObjectiveId)), ("state", JStr(o.State)))))),
        ("operatives", JArr(Operatives.OrderBy(o => o.Slot).Select(o => (Node)WriteOperative(Sorted(o))))),
        ("outcome", JStr(Outcome)),
        ("outcomeSequence", JNum(OutcomeSequence)),
        ("processSessionId", JStr(ProcessSessionId.ToString("D"))),
        ("serverElapsedMs", JNum(ServerElapsedMs))));

    static Obj WriteOperative(ManifestOperative o) => JObj(
        ("alive", JBool(o.Alive)),
        ("collected", JArr(o.Collected.Select(c => (Node)JObj(("containerIndex", JNum(c.ContainerIndex)), ("line", JNum(c.Line)), ("quantity", JNum(c.Quantity)))))),
        ("consumed", JArr(o.ConsumedLoot.Select(c => (Node)JObj(("containerIndex", JNum(c.ContainerIndex)), ("definitionId", JStr(c.DefinitionId)),
                ("line", JNum(c.Line)), ("quantity", JNum(c.Quantity)), ("source", JStr("loot"))))
            .Concat(o.ConsumedOwned.Select(c => (Node)JObj(("definitionId", JStr(c.DefinitionId)), ("quantity", JNum(c.Quantity)), ("source", JStr("owned"))))))),
        ("equipment", JArr(o.Equipment.Select(e => (Node)(e.ItemId is { } id
            ? JObj(("itemId", JStr(Base58Id.Format(id))), ("slot", JStr(e.Slot)))
            : JObj(("containerIndex", JNum(e.ContainerIndex!.Value)), ("line", JNum(e.Line!.Value)), ("slot", JStr(e.Slot))))))),
        ("held", JArr(o.HeldLoot.Select(h => (Node)JObj(("containerIndex", JNum(h.ContainerIndex)), ("line", JNum(h.Line)), ("origin", JStr("loot")), ("quantity", JNum(h.Quantity))))
            .Concat(o.HeldItems.Select(id => (Node)JObj(("itemId", JStr(Base58Id.Format(id))), ("origin", JStr("owned")))))
            .Concat(o.HeldStacks.Select(h => (Node)JObj(("definitionId", JStr(h.DefinitionId)), ("origin", JStr("owned")), ("quantity", JNum(h.Quantity))))))),
        ("operativeId", JStr(Base58Id.Format(o.OperativeId))),
        ("slot", JNum(o.Slot)));

    // ---- strict readers; each refusal carries its code ----

    static Dictionary<string, JsonElement> RawMembers(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Refuse(ManifestCodes.WrongType);
        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
            if (!found.TryAdd(p.Name, p.Value)) throw Refuse(ManifestCodes.DuplicateKey);
        return found;
    }

    static void Exact(Dictionary<string, JsonElement> found, params string[] names)
    {
        if (found.Keys.Any(k => System.Array.IndexOf(names, k) < 0)) throw Refuse(ManifestCodes.UnknownMember);
        if (names.Any(n => !found.ContainsKey(n))) throw Refuse(ManifestCodes.MissingMember);
    }

    static Dictionary<string, JsonElement> Members(JsonElement e, params string[] names)
    {
        var found = RawMembers(e);
        Exact(found, names);
        return found;
    }

    static JsonElement Required(Dictionary<string, JsonElement> found, string name) =>
        found.TryGetValue(name, out var value) ? value : throw Refuse(ManifestCodes.MissingMember);

    static string Str(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) throw Refuse(ManifestCodes.WrongType);
        var s = e.GetString()!;
        return Encoding.UTF8.GetByteCount(s) <= MaxStringBytes ? s : throw Refuse(ManifestCodes.OutOfBounds);
    }

    static int Int(JsonElement e, int min, int max)
    {
        if (e.ValueKind != JsonValueKind.Number) throw Refuse(ManifestCodes.WrongType);
        var raw = e.GetRawText();
        if (!IntegerLiteral().IsMatch(raw)) throw Refuse(ManifestCodes.NotInteger);
        if (raw.Length > 10 || !long.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v)
            || v < min || v > max) throw Refuse(ManifestCodes.OutOfBounds);
        return (int)v;
    }

    static int Index(JsonElement e) => Int(e, 0, MaxContainerOrLine);

    static int Quantity(JsonElement e) => Int(e, 1, int.MaxValue);

    static bool Bool(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Refuse(ManifestCodes.WrongType),
    };

    static string OneOf(JsonElement e, IReadOnlyList<string> allowed)
    {
        var s = Str(e);
        return allowed.Contains(s) ? s : throw Refuse(ManifestCodes.BadEnum);
    }

    static string Hash(JsonElement e)
    {
        var s = Str(e);
        return Sha256Pattern().IsMatch(s) ? s : throw Refuse(ManifestCodes.NonCanonicalId);
    }

    static long Base58(JsonElement e) => Base58Id.TryParse(Str(e), out var id) ? id : throw Refuse(ManifestCodes.NonCanonicalId);

    static string Definition(JsonElement e)
    {
        var s = Str(e);
        return CanonicalCatalogueJson.IsWellFormedId(s) ? s : throw Refuse(ManifestCodes.BadValue);
    }

    static List<T> ListOf<T>(JsonElement e, int min, int max, Func<JsonElement, T> read)
    {
        if (e.ValueKind != JsonValueKind.Array) throw Refuse(ManifestCodes.WrongType);
        var count = e.GetArrayLength();
        if (count < min || count > max) throw Refuse(ManifestCodes.OutOfBounds);
        var list = new List<T>(count);
        foreach (var item in e.EnumerateArray()) list.Add(read(item));
        return list;
    }

    static List<T> Distinct<T, TKey>(List<T> list, Func<T, TKey> key)
    {
        var seen = new HashSet<TKey>();
        foreach (var item in list)
            if (!seen.Add(key(item))) throw Refuse(ManifestCodes.DuplicateEntry);
        return list;
    }
}
