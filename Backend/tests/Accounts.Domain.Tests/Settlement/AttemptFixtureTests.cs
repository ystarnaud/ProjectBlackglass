using System.Text.Json;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Settlement;

namespace Blackglass.Accounts.Domain.Tests.Settlement;

/// <summary>
/// Stage 12 gate B25, owner test 9: the backend judges attempts Unity actually generated (Backend/contracts/attempt-v1,
/// written by Unity's Blackglass > Backend > Export Attempt Fixtures and pinned there by a staleness test). Each fixture's
/// ready report is the exact bytes Unity's JsonUtility writes; the backend runs its whole ready path over them in the
/// endpoint's order (ReadyReport.Parse, ObjectiveList.Validate, LootPoolValidator.Validate against the shipped catalogue's
/// table, the canonical loot hash), then ResultManifest.Parse and the real ManifestValidator over the manifest bytes Unity built
/// from the same play. Every valid fixture must pass; every deliberately invalid one must be refused with exactly its expected
/// errors. Nothing here reconstructs Unity's data: the pool, objectives and hashes are the bytes Unity wrote.
/// </summary>
public class AttemptFixtureTests
{
    static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "contracts", "attempt-v1");
    static readonly GameCatalogue Shipped = GameCatalogue.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "catalogue", "catalogue.json")));

    static bool IsDescription(string path) =>
        !path.EndsWith(".ready.json", StringComparison.Ordinal) && !path.EndsWith(".manifest.json", StringComparison.Ordinal);

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.json").Where(IsDescription).Order(StringComparer.Ordinal)) data.Add(Path.GetFileName(path));
        return data;
    }

    [Fact]
    public void There_are_the_seven_valid_fixtures_and_the_invalid_ones()
    {
        var names = Directory.EnumerateFiles(Folder, "*.json").Where(IsDescription).Select(p => Path.GetFileName(p)).ToList();
        Assert.Equal(7, names.Count(n => !n.Contains("-invalid-", StringComparison.Ordinal)));
        Assert.True(names.Count(n => n.Contains("-invalid-", StringComparison.Ordinal)) >= 4);
    }

    /// <summary>The endpoint's checks in its order (ProcessStore.ReadyAsync): objectives first, then the pool and its hash.</summary>
    static List<string> ReadyErrors(ReadyReport report, LootTable table)
    {
        var objectiveErrors = ObjectiveList.Validate(report.Objectives);
        if (objectiveErrors.Count > 0) return [.. objectiveErrors];
        var poolErrors = LootPoolValidator.Validate(table, report.LootTableVersion, report.LootPool).ToList();
        if (LootHash.Compute(report.LootTableVersion, report.LootPool) != report.LootHash) poolErrors.Add("lootHash is not the pool's canonical hash");
        return poolErrors;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_backend_judges_each_generated_attempt(string name)
    {
        using var description = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Folder, name)));
        var d = description.RootElement;
        var report = ReadyReport.Parse(File.ReadAllBytes(Path.Combine(Folder, d.GetProperty("readyFile").GetString()!)));
        Assert.Equal(Guid.Parse(d.GetProperty("processSessionId").GetString()!), report.ProcessSessionId);
        Assert.Equal(Shipped.Version, d.GetProperty("catalogueVersion").GetString());
        var expectedReady = d.GetProperty("expectedReadyErrors").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(expectedReady, ReadyErrors(report, Shipped.Document.LootTable));
        if (expectedReady.Count > 0)
        {
            Assert.Equal(JsonValueKind.Null, d.GetProperty("manifestFile").ValueKind); // a refused ready report starts no attempt
            return;
        }

        var raw = File.ReadAllBytes(Path.Combine(Folder, d.GetProperty("manifestFile").GetString()!));
        var parsed = Assert.IsType<ParseResult.Parsed>(ResultManifest.Parse(raw));
        Assert.Equal(raw, parsed.CanonicalBytes); // Unity wrote the canonical form
        var m = parsed.Manifest;
        var snapshot = LoadoutSnapshot.Parse(d.GetProperty("snapshot").GetRawText());
        Assert.Equal(Shipped.Version, snapshot.CatalogueVersion);
        Assert.Equal(snapshot.AccountId, m.AccountId);

        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var facts = new AttemptFacts(Guid.Parse(d.GetProperty("attemptId").GetString()!), snapshot.AccountId, report.ProcessSessionId,
            d.GetProperty("buildRevision").GetString()!, Shipped.Version, report.LayoutHash, report.LootTableVersion, report.LootHash,
            now.AddMinutes(-10), now.AddMinutes(-10), now);
        var context = new SettlementContext(snapshot, facts, report.LootPool, report.Objectives, new Dictionary<(int, int), int>(), Shipped, null, SettlementLimits.Default);
        var expectedManifest = d.GetProperty("expectedManifestErrors").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(expectedManifest, ManifestValidator.Validate(m, parsed.ManifestHash, context));
        if (expectedManifest.Count > 0) return;

        var plan = SettlementPlanner.Plan(m, context, null, new byte[32]);
        Assert.Equal(m.Operatives.SelectMany(o => o.ConsumedOwned.Select(c => $"{o.OperativeId}/{c.DefinitionId}/{c.Quantity}")).Order(),
            plan.Debits.Select(x => $"{x.OperativeId}/{x.DefinitionId}/{x.Quantity}").Order());
        Assert.Equal(m.Operatives.SelectMany(o => o.Collected).GroupBy(c => (c.ContainerIndex, c.Line)).ToDictionary(g => g.Key, g => g.Sum(c => c.Quantity)),
            plan.Collections.ToDictionary(p => (p.Key.ContainerIndex, p.Key.Line), p => p.Value));
    }

    /// <summary>B25's forced case end to end: Unity's two medkit lines (1 and 2) are accepted; the old merged line of 3 is refused.</summary>
    [Fact]
    public void The_forced_duplicate_medkits_are_two_valid_lines_and_their_merge_is_refused()
    {
        using var description = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Folder, "01-forced-duplicate-medkits.json")));
        var report = ReadyReport.Parse(File.ReadAllBytes(Path.Combine(Folder, description.RootElement.GetProperty("readyFile").GetString()!)));
        Assert.Equal([new PoolLine(0, 0, "item.medkit", 1), new PoolLine(0, 1, "item.medkit", 2)], report.LootPool);
        Assert.Empty(LootPoolValidator.Validate(Shipped.Document.LootTable, report.LootTableVersion, report.LootPool));
        PoolLine[] merged = [new(0, 0, "item.medkit", 3)];
        Assert.Equal(["line (0,0): quantity 3 is outside the entry's range"], LootPoolValidator.Validate(Shipped.Document.LootTable, report.LootTableVersion, merged));
    }
}
