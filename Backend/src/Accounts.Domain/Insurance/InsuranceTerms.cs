using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Blackglass.Accounts.Domain.Insurance;

/// <summary>Raised when insurance terms are malformed. The message names the member and the rule, never a secret.</summary>
public sealed class InsuranceTermsException(string message) : Exception(message);

public sealed record ConsumableRefund(int Percent, string Rounding, string Destination);

public sealed record LootRetention(int MaxEntries, int MaxUnitsPerEntry, IReadOnlyList<string> EligibleCategories, IReadOnlyList<string> Rank,
    IReadOnlyList<string> CategoryPriority);

public sealed record ProgressXp(int Percent, string Basis);

/// <summary>
/// The terms of one insurance policy version (spec §6.2), as stored in insurance_policies.terms and copied into an
/// attempt's insurance snapshot. Parse reads strictly: every member required, once, with its exact JSON kind (an integer
/// is written as plain digits: no fraction, exponent or minus zero); no unknown member; every value in range. Covers
/// lists outcomes from {defeat, abandoned, disconnected, expired}, at least one, none twice. Category words are the
/// spec's spelling (Consumable, Armor, Utility, Weapon); eligibleCategories may be empty (no retention) but repeats none;
/// categoryPriority orders all four exactly once; rank is exactly [categoryPriority, saltedHash]. The bounds (percent
/// 0..100, maxEntries 0..64, maxUnitsPerEntry 1..1000) are provisional sanity limits, not balance values. Canonical()
/// writes the one compact form: members in the order above, arrays in their stored order, no whitespace.
/// </summary>
public sealed record InsuranceTerms(IReadOnlyList<string> Covers, ConsumableRefund ConsumableRefund, LootRetention LootRetention, ProgressXp ProgressXp)
{
    public const int MaxPercent = 100;
    public const int MaxEntriesLimit = 64;
    public const int MaxUnitsPerEntryLimit = 1000;

    public static readonly IReadOnlyList<string> Outcomes = ["defeat", "abandoned", "disconnected", "expired"];
    public static readonly IReadOnlyList<string> Categories = ["Consumable", "Armor", "Utility", "Weapon"];
    public static readonly IReadOnlyList<string> RankOrder = ["categoryPriority", "saltedHash"];
    public const string FloorRounding = "floor";
    public const string StashDestination = "stash";
    public const string RequiredObjectivesBasis = "requiredObjectivesCompleted";

    static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8,
    };

    public static InsuranceTerms Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try { document = JsonDocument.Parse(json, Options); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InsuranceTermsException("Insurance terms are not valid JSON.");
        }
        using (document)
        {
            var root = Members(document.RootElement, "$", "covers", "consumableRefund", "lootRetention", "progressXp");
            var refund = Members(root["consumableRefund"], "$.consumableRefund", "percent", "rounding", "destination");
            var loot = Members(root["lootRetention"], "$.lootRetention", "maxEntries", "maxUnitsPerEntry", "eligibleCategories", "rank", "categoryPriority");
            var xp = Members(root["progressXp"], "$.progressXp", "percent", "basis");
            var terms = new InsuranceTerms(
                Strings(root["covers"], "$.covers"),
                new ConsumableRefund(Int(refund["percent"], "$.consumableRefund.percent"), Str(refund["rounding"], "$.consumableRefund.rounding"),
                    Str(refund["destination"], "$.consumableRefund.destination")),
                new LootRetention(Int(loot["maxEntries"], "$.lootRetention.maxEntries"), Int(loot["maxUnitsPerEntry"], "$.lootRetention.maxUnitsPerEntry"),
                    Strings(loot["eligibleCategories"], "$.lootRetention.eligibleCategories"), Strings(loot["rank"], "$.lootRetention.rank"),
                    Strings(loot["categoryPriority"], "$.lootRetention.categoryPriority")),
                new ProgressXp(Int(xp["percent"], "$.progressXp.percent"), Str(xp["basis"], "$.progressXp.basis")));
            terms.Validate();
            return terms;
        }
    }

    /// <summary>Throws InsuranceTermsException unless every rule above holds (Parse and Canonical both apply it).</summary>
    public void Validate()
    {
        if (Covers is null || ConsumableRefund is null || LootRetention is null || ProgressXp is null)
            throw new InsuranceTermsException("Insurance terms are incomplete.");
        if (Covers.Count == 0) throw new InsuranceTermsException("$.covers must list at least one outcome.");
        Subset(Covers, Outcomes, "$.covers");

        Range(ConsumableRefund.Percent, 0, MaxPercent, "$.consumableRefund.percent");
        Exactly(ConsumableRefund.Rounding, FloorRounding, "$.consumableRefund.rounding");
        Exactly(ConsumableRefund.Destination, StashDestination, "$.consumableRefund.destination");

        Range(LootRetention.MaxEntries, 0, MaxEntriesLimit, "$.lootRetention.maxEntries");
        Range(LootRetention.MaxUnitsPerEntry, 1, MaxUnitsPerEntryLimit, "$.lootRetention.maxUnitsPerEntry");
        Subset(LootRetention.EligibleCategories, Categories, "$.lootRetention.eligibleCategories");
        if (LootRetention.Rank is null || !LootRetention.Rank.SequenceEqual(RankOrder, StringComparer.Ordinal))
            throw new InsuranceTermsException("$.lootRetention.rank must be exactly [\"categoryPriority\", \"saltedHash\"].");
        Subset(LootRetention.CategoryPriority, Categories, "$.lootRetention.categoryPriority");
        if (LootRetention.CategoryPriority.Count != Categories.Count)
            throw new InsuranceTermsException("$.lootRetention.categoryPriority must order every category exactly once.");

        Range(ProgressXp.Percent, 0, MaxPercent, "$.progressXp.percent");
        Exactly(ProgressXp.Basis, RequiredObjectivesBasis, "$.progressXp.basis");
    }

    public string Canonical()
    {
        Validate();
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            WriteArray(w, "covers", Covers);
            w.WriteStartObject("consumableRefund");
            w.WriteNumber("percent", ConsumableRefund.Percent);
            w.WriteString("rounding", ConsumableRefund.Rounding);
            w.WriteString("destination", ConsumableRefund.Destination);
            w.WriteEndObject();
            w.WriteStartObject("lootRetention");
            w.WriteNumber("maxEntries", LootRetention.MaxEntries);
            w.WriteNumber("maxUnitsPerEntry", LootRetention.MaxUnitsPerEntry);
            WriteArray(w, "eligibleCategories", LootRetention.EligibleCategories);
            WriteArray(w, "rank", LootRetention.Rank);
            WriteArray(w, "categoryPriority", LootRetention.CategoryPriority);
            w.WriteEndObject();
            w.WriteStartObject("progressXp");
            w.WriteNumber("percent", ProgressXp.Percent);
            w.WriteString("basis", ProgressXp.Basis);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void WriteArray(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (var value in values) w.WriteStringValue(value);
        w.WriteEndArray();
    }

    static Dictionary<string, JsonElement> Members(JsonElement e, string path, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InsuranceTermsException($"{path} must be an object.");
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
        {
            if (Array.IndexOf(names, p.Name) < 0) throw new InsuranceTermsException($"{path} has an unknown member.");
            if (!members.TryAdd(p.Name, p.Value)) throw new InsuranceTermsException($"{path}.{p.Name} appears more than once.");
        }
        foreach (var name in names)
        {
            if (!members.ContainsKey(name)) throw new InsuranceTermsException($"{path}.{name} is missing.");
        }
        return members;
    }

    static int Int(JsonElement e, string path)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var value) && e.GetRawText() == value.ToString(CultureInfo.InvariantCulture))
            return value;
        throw new InsuranceTermsException($"{path} must be an integer written as plain digits.");
    }

    static string Str(JsonElement e, string path)
    {
        if (e.ValueKind != JsonValueKind.String) throw new InsuranceTermsException($"{path} must be a string.");
        try { return e.GetString()!; }
        catch (InvalidOperationException) { throw new InsuranceTermsException($"{path} is not a valid string."); }
    }

    static List<string> Strings(JsonElement e, string path)
    {
        if (e.ValueKind != JsonValueKind.Array) throw new InsuranceTermsException($"{path} must be an array.");
        var list = new List<string>();
        var index = 0;
        foreach (var item in e.EnumerateArray()) list.Add(Str(item, $"{path}[{index++}]"));
        return list;
    }

    static void Range(int value, int min, int max, string path)
    {
        if (value < min || value > max) throw new InsuranceTermsException($"{path} must be from {min} to {max}.");
    }

    static void Exactly(string? value, string expected, string path)
    {
        if (!string.Equals(value, expected, StringComparison.Ordinal)) throw new InsuranceTermsException($"{path} must be \"{expected}\".");
    }

    static void Subset(IReadOnlyList<string>? values, IReadOnlyList<string> allowed, string path)
    {
        if (values is null) throw new InsuranceTermsException($"{path} is missing.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
                throw new InsuranceTermsException($"{path} may list only {string.Join(", ", allowed)}.");
            if (!seen.Add(value)) throw new InsuranceTermsException($"{path} lists a value more than once.");
        }
    }
}
