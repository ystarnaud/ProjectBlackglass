using System.Text;
using System.Text.Json;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Settlement;

namespace Blackglass.Accounts.Domain.Tests.Settlement;

/// <summary>
/// The shared ledger-v1 scenarios (Backend/contracts/ledger-v1, owner ruling D3), judged independently of Unity: the backend
/// reconstructs each scenario's admission snapshot (LoadoutSnapshot.Parse), loot pool and objectives, parses the hand-written
/// expected manifest with the authoritative ResultManifest.Parse (the same bytes Unity must produce), and runs the real
/// ManifestValidator, which must return exactly the scenario's expected errors. Clean scenarios must also plan a settlement whose
/// owned debits and collections are the manifest's. The validator is never adjusted to a scenario.
/// </summary>
public class LedgerScenarioTests
{
    static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "contracts", "ledger-v1");
    static readonly GameCatalogue Golden = GameCatalogue.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contracts", "catalogue", "golden-catalogue.json")));

    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.Ordinal)) data.Add(Path.GetFileName(path));
        return data;
    }

    [Fact]
    public void There_are_eight_scenarios() => Assert.Equal(8, Scenarios().Count);

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Each_scenario_validates_with_exactly_its_expected_errors(string name)
    {
        using var scenario = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Folder, name)));
        var root = scenario.RootElement;
        var raw = Encoding.UTF8.GetBytes(root.GetProperty("expectedManifest").GetRawText());
        var parsed = Assert.IsType<ParseResult.Parsed>(ResultManifest.Parse(raw));
        Assert.Equal(parsed.CanonicalBytes, Assert.IsType<ParseResult.Parsed>(ResultManifest.Parse(parsed.CanonicalBytes)).CanonicalBytes);
        var m = parsed.Manifest;

        var attempt = root.GetProperty("attempt");
        var tableVersion = attempt.GetProperty("lootTableVersion").GetInt32();
        var pool = root.GetProperty("pool").EnumerateArray().Select(l => new PoolLine(l.GetProperty("containerIndex").GetInt32(), l.GetProperty("line").GetInt32(),
            l.GetProperty("definitionId").GetString()!, l.GetProperty("quantity").GetInt32())).ToList();
        Assert.Equal(LootHash.Compute(tableVersion, pool), m.LootHash);
        Assert.Empty(LootPoolValidator.Validate(Golden.Document.LootTable, tableVersion, pool));
        var objectives = root.GetProperty("objectives").EnumerateArray().Select(o => new Objective(o.GetProperty("objectiveId").GetString()!, o.GetProperty("required").GetBoolean())).ToList();
        var snapshot = LoadoutSnapshot.Parse(root.GetProperty("snapshot").GetRawText());
        Assert.Equal(Golden.Version, snapshot.CatalogueVersion);

        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var facts = new AttemptFacts(Guid.Parse(attempt.GetProperty("attemptId").GetString()!), snapshot.AccountId, Guid.Parse(attempt.GetProperty("processSessionId").GetString()!),
            attempt.GetProperty("buildRevision").GetString()!, Golden.Version, attempt.GetProperty("layoutHash").GetString()!, tableVersion, LootHash.Compute(tableVersion, pool),
            now.AddMinutes(-2), now.AddMinutes(-2), now);
        var context = new SettlementContext(snapshot, facts, pool, objectives, new Dictionary<(int, int), int>(), Golden, null, SettlementLimits.Default);
        var expected = root.GetProperty("expectedErrors").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(expected, ManifestValidator.Validate(m, parsed.ManifestHash, context));

        if (expected.Count > 0) return;
        var plan = SettlementPlanner.Plan(m, context, null, new byte[32]);
        Assert.Equal(m.Operatives.SelectMany(o => o.ConsumedOwned.Select(c => $"{o.OperativeId}/{c.DefinitionId}/{c.Quantity}")).Order(),
            plan.Debits.Select(d => $"{d.OperativeId}/{d.DefinitionId}/{d.Quantity}").Order());
        Assert.Equal(m.Operatives.SelectMany(o => o.Collected).GroupBy(c => (c.ContainerIndex, c.Line)).ToDictionary(g => g.Key, g => g.Sum(c => c.Quantity)),
            plan.Collections.ToDictionary(p => (p.Key.ContainerIndex, p.Key.Line), p => p.Value));
    }

    /// <summary>
    /// Final review I1, an open Stage 12 obligation: ItemInventory.Add merges same-definition plan items in one container, so
    /// two kit plan items (1 + 2) become one entry, one pool line of 3, outside the table's 1..2: the ready report is refused
    /// and the attempt cancelled (InvalidPool). The ledger takes lines from the entries after the fill, so Stage 12 must fill
    /// attempt containers one entry per plan item (or the owner rules another fix). This pins today's backend rule.
    /// </summary>
    [Fact]
    public void A_merged_container_line_is_refused_by_the_ready_report_rule()
    {
        PoolLine[] merged = [new(0, 0, "item.gamma-kit", 3)];
        PoolLine[] unmerged = [new(0, 0, "item.gamma-kit", 1), new(0, 1, "item.gamma-kit", 2)];
        Assert.Equal(["line (0,0): quantity 3 is outside the entry's range"], LootPoolValidator.Validate(Golden.Document.LootTable, 7, merged));
        Assert.Empty(LootPoolValidator.Validate(Golden.Document.LootTable, 7, unmerged));
    }

    // ---- the Unity-only parser expectations (Stage 11 ManifestCodecTests), pinned against the authoritative parser ----

    static string Vector(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "manifest-v1", "valid", name + ".canonical.json"));

    static string Code(byte[] raw) => ResultManifest.Parse(raw) switch
    {
        ParseResult.Malformed malformed => malformed.Code,
        _ => "Parsed",
    };

    static string Code(string json) => Code(Encoding.UTF8.GetBytes(json));

    static string Nested(int containers)
    {
        var minimal = Vector("01-minimal");
        return minimal[..^1] + ",\"x\":" + new string('[', containers - 1) + new string(']', containers - 1) + "}";
    }

    [Fact]
    public void A_byte_order_mark_is_NotJson() => Assert.Equal(ManifestCodes.NotJson, Code([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Vector("01-minimal"))]));

    [Fact]
    public void A_comment_is_NotJson() => Assert.Equal(ManifestCodes.NotJson, Code("{/*x*/" + Vector("01-minimal")[1..]));

    [Fact]
    public void A_lone_surrogate_is_NotJson_where_it_is_read_and_ignored_until_then()
    {
        var minimal = Vector("01-minimal");
        Assert.Equal(ManifestCodes.NotJson, Code(minimal.Replace("\"buildRevision\":\"r1\"", "\"buildRevision\":\"\\ud800\"")));
        Assert.Equal(ManifestCodes.UnsupportedFormat, Code(minimal.Replace("\"outcome\":\"success\"", "\"outcome\":\"\\udc00\"").Replace("\"formatVersion\":1", "\"formatVersion\":2")));
        Assert.Equal(ManifestCodes.BadValue, Code(minimal.Replace("\"buildRevision\":\"r1\"", "\"buildRevision\":\"\\ud83d\\ude00\"")));
    }

    [Fact]
    public void Nesting_64_is_TooDeep_and_65_is_NotJson()
    {
        Assert.Equal(ManifestCodes.TooDeep, Code(Nested(6)));
        Assert.Equal(ManifestCodes.TooDeep, Code(Nested(64)));
        Assert.Equal(ManifestCodes.NotJson, Code(Nested(65)));
    }

    [Fact]
    public void Entry_members_are_read_in_constructor_order()
    {
        var full = Vector("02-full");
        Assert.Equal(ManifestCodes.BadEnum, Code(full.Replace("{\"itemId\":\"2m\",\"slot\":\"armor\"}", "{\"itemId\":\"0\",\"slot\":\"x\"}")));
        Assert.Equal(ManifestCodes.OutOfBounds, Code(full.Replace("{\"containerIndex\":0,\"definitionId\":\"item.medkit\",\"line\":0,\"quantity\":1,\"source\":\"loot\"}",
            "{\"containerIndex\":0,\"definitionId\":\"Bad\",\"line\":70000,\"quantity\":1,\"source\":\"loot\"}")));
    }

    [Theory]
    [InlineData("[]", ManifestCodes.WrongType)]
    [InlineData("\"x\"", ManifestCodes.WrongType)]
    [InlineData("", ManifestCodes.NotJson)]
    [InlineData("{} {}", ManifestCodes.NotJson)]
    [InlineData("{\"a\":1,}", ManifestCodes.NotJson)]
    [InlineData("{\"a\":01}", ManifestCodes.NotJson)]
    [InlineData("{\"a\":\"\t\"}", ManifestCodes.NotJson)]
    [InlineData("{\"a\":\"\\x\"}", ManifestCodes.NotJson)]
    [InlineData("{\"a\":-}", ManifestCodes.NotJson)]
    [InlineData("{\"a\":1.}", ManifestCodes.NotJson)]
    public void Syntax_and_root_kind_match_Unity(string json, string code) => Assert.Equal(code, Code(json));
}
