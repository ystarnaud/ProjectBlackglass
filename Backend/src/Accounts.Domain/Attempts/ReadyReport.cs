using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Attempts;

/// <summary>One filled container entry of the attempt's loot pool (spec §8.4).</summary>
public sealed record PoolLine(int ContainerIndex, int Line, string DefinitionId, int Quantity);

/// <summary>One objective the generated mission holds.</summary>
public sealed record Objective(string ObjectiveId, bool Required);

/// <summary>
/// The process's ready report (spec §16.3, plan revision 2 R3): {processSessionId, layoutHash (16 lowercase hex digits,
/// the layout's 64-bit hash), lootTableVersion (≥ 1), lootHash (<see cref="Attempts.LootHash"/>), objectives (0 to 64
/// objects {objectiveId, required}), lootPool (0 to <see cref="MaxPoolLines"/> objects {containerIndex, line,
/// definitionId, quantity})}. Parse checks shape only; <see cref="ObjectiveList"/>, <see cref="LootPoolValidator"/> and
/// <see cref="Attempts.LootHash"/> check content.
/// </summary>
public sealed partial record ReadyReport(Guid ProcessSessionId, string LayoutHash, int LootTableVersion, string LootHash,
    IReadOnlyList<Objective> Objectives, IReadOnlyList<PoolLine> LootPool)
{
    public const int MaxPoolLines = 256;
    public const int MaxObjectives = 64;

    [GeneratedRegex(@"^[0-9a-f]{16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LayoutHashPattern();

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LootHashPattern();

    public static ReadyReport Parse(ReadOnlySpan<byte> utf8Json) => StrictJson.Read(utf8Json, root =>
    {
        var m = StrictJson.Members(root, "processSessionId", "layoutHash", "lootTableVersion", "lootHash", "objectives", "lootPool");
        var layout = StrictJson.String(m["layoutHash"], 16);
        var loot = StrictJson.String(m["lootHash"], 71);
        if (!LayoutHashPattern().IsMatch(layout) || !LootHashPattern().IsMatch(loot)) throw StrictJson.Refuse();
        var objectives = StrictJson.Array(m["objectives"], 0, MaxObjectives, e =>
        {
            var o = StrictJson.Members(e, "objectiveId", "required");
            return new Objective(StrictJson.String(o["objectiveId"], 128), StrictJson.Bool(o["required"]));
        });
        var pool = StrictJson.Array(m["lootPool"], 0, MaxPoolLines, e =>
        {
            var l = StrictJson.Members(e, "containerIndex", "line", "definitionId", "quantity");
            var definition = StrictJson.String(l["definitionId"], CanonicalCatalogueJson.MaxIdLength);
            if (!CanonicalCatalogueJson.IsWellFormedId(definition)) throw StrictJson.Refuse();
            return new PoolLine(StrictJson.Int(l["containerIndex"], 0, 1000), StrictJson.Int(l["line"], 0, 1000), definition,
                StrictJson.Int(l["quantity"], 1, 1_000_000));
        });
        return new ReadyReport(StrictJson.Uuid(m["processSessionId"]), layout, StrictJson.Int(m["lootTableVersion"], 1, int.MaxValue),
            loot, objectives, pool);
    });
}

/// <summary>
/// The canonical loot-pool hash (plan revision 2 R3; not Unity's FNV LootPlan.Hash, which also covers tiles):
/// "sha256:" + lowercase hex SHA-256 of the UTF-8 text "blackglass-loot-pool/1\n", "table=&lt;version&gt;\n", then one
/// "&lt;containerIndex&gt;,&lt;line&gt;,&lt;definitionId&gt;,&lt;quantity&gt;\n" per line sorted by (containerIndex, line),
/// integers in invariant decimal. Definition ids hold no comma or newline (the catalogue id pattern).
/// </summary>
public static class LootHash
{
    public static string Compute(int lootTableVersion, IReadOnlyList<PoolLine> pool)
    {
        var text = new StringBuilder("blackglass-loot-pool/1\n");
        text.Append(CultureInfo.InvariantCulture, $"table={lootTableVersion}\n");
        foreach (var l in pool.OrderBy(l => l.ContainerIndex).ThenBy(l => l.Line))
            text.Append(CultureInfo.InvariantCulture, $"{l.ContainerIndex},{l.Line},{l.DefinitionId},{l.Quantity}\n");
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

/// <summary>Validates a reported pool against the catalogue's loot table (spec §7.4, §8.4).</summary>
public static class LootPoolValidator
{
    /// <summary>
    /// Every violation, in order: the reported table version differs from the table's; a container index outside
    /// [0, containerCount); a container's line count outside [minItems, maxItems]; a container's lines not exactly 0..n-1;
    /// a definition not among the table's entries; a quantity outside every matching entry's range. Containers the
    /// mission did not place are simply absent. An empty list means valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(LootTable table, int reportedTableVersion, IReadOnlyList<PoolLine> pool)
    {
        var errors = new List<string>();
        if (reportedTableVersion != table.Version)
            errors.Add($"lootTableVersion {reportedTableVersion} is not the catalogue's {table.Version}");
        foreach (var container in pool.GroupBy(l => l.ContainerIndex).OrderBy(g => g.Key))
        {
            var lines = container.Select(l => l.Line).OrderBy(x => x).ToList();
            if (container.Key >= table.ContainerCount)
                errors.Add($"container {container.Key} is outside 0..{table.ContainerCount - 1}");
            if (lines.Count < table.MinItems || lines.Count > table.MaxItems)
                errors.Add($"container {container.Key} has {lines.Count} lines, outside {table.MinItems}..{table.MaxItems}");
            if (!lines.SequenceEqual(Enumerable.Range(0, lines.Count)))
                errors.Add($"container {container.Key} lines are not 0..{lines.Count - 1}");
        }
        foreach (var l in pool.OrderBy(l => l.ContainerIndex).ThenBy(l => l.Line))
        {
            var entries = table.Entries.Where(e => e.ItemId == l.DefinitionId).ToList();
            if (entries.Count == 0)
                errors.Add($"line ({l.ContainerIndex},{l.Line}): unknown definition '{l.DefinitionId}'");
            else if (!entries.Any(e => l.Quantity >= e.MinQuantity && l.Quantity <= e.MaxQuantity))
                errors.Add($"line ({l.ContainerIndex},{l.Line}): quantity {l.Quantity} is outside the entry's range");
        }
        return errors;
    }
}

/// <summary>The objectives a ready report registers: 1 to 32, unique lowercase-word ids of up to 64 characters, at least one required.</summary>
public static class ObjectiveList
{
    public const int MaxObjectives = 32;
    public const int MaxIdLength = 64;

    public static IReadOnlyList<string> Validate(IReadOnlyList<Objective> objectives)
    {
        var errors = new List<string>();
        if (objectives.Count < 1 || objectives.Count > MaxObjectives) errors.Add($"objectives: {objectives.Count}, not 1..{MaxObjectives}");
        if (objectives.All(o => !o.Required)) errors.Add("objectives: none is required");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < objectives.Count; i++)
        {
            if (!StrictJson.IsWord(objectives[i].ObjectiveId, MaxIdLength)) errors.Add($"objectives[{i}]: malformed id");
            else if (!seen.Add(objectives[i].ObjectiveId)) errors.Add($"objectives[{i}]: repeated id");
        }
        return errors;
    }
}
