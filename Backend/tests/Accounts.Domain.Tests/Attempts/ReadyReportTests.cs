using System.Text;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Attempts;

public class ReadyReportTests
{
    const string Session = "0192f0c1-0000-7000-8000-000000000001";
    const string Hash = "sha256:bce2a2eda82db4789a87efbde6bcae0fe68342ced7877883a5024dded26591c0";

    static string Body(string? replaceFrom = null, string? replaceTo = null)
    {
        var json = $$"""{"processSessionId":"{{Session}}","layoutHash":"cf2e14dece9cc2a6","lootTableVersion":1,"lootHash":"{{Hash}}","objectives":[{"objectiveId":"extract","required":true}],"lootPool":[{"containerIndex":0,"line":0,"definitionId":"stim","quantity":3}]}""";
        return replaceFrom is null ? json : json.Replace(replaceFrom, replaceTo, StringComparison.Ordinal);
    }

    static ReadyReport Parse(string json) => ReadyReport.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void A_valid_ready_body_parses()
    {
        var r = Parse(Body());
        Assert.Equal(Guid.Parse(Session), r.ProcessSessionId);
        Assert.Equal("cf2e14dece9cc2a6", r.LayoutHash);
        Assert.Equal(1, r.LootTableVersion);
        Assert.Equal(Hash, r.LootHash);
        Assert.Equal([new Objective("extract", true)], r.Objectives);
        Assert.Equal([new PoolLine(0, 0, "stim", 3)], r.LootPool);
    }

    public static TheoryData<string, string> Refusals()
    {
        var pool = string.Join(",", Enumerable.Range(0, ReadyReport.MaxPoolLines + 1).Select(i => $$"""{"containerIndex":0,"line":{{i}},"definitionId":"stim","quantity":1}"""));
        return new()
        {
            { "unknown member", Body("\"layoutHash\"", "\"x\":1,\"layoutHash\"") },
            { "duplicate member", Body("\"layoutHash\"", "\"layoutHash\":\"cf2e14dece9cc2a6\",\"layoutHash\"") },
            { "missing lootHash", Body($",\"lootHash\":\"{Hash}\"", "") },
            { "session not canonical", Body(Session, Session.ToUpperInvariant()) },
            { "session braces", Body($"\"{Session}\"", $"\"{{{Session}}}\"") },
            { "layout hash uppercase", Body("cf2e14dece9cc2a6", "CF2E14DECE9CC2A6") },
            { "layout hash short", Body("cf2e14dece9cc2a6", "cf2e14") },
            { "loot hash malformed", Body(Hash, "sha256:xyz") },
            { "table version zero", Body("\"lootTableVersion\":1", "\"lootTableVersion\":0") },
            { "table version fractional", Body("\"lootTableVersion\":1", "\"lootTableVersion\":1.0") },
            { "table version string", Body("\"lootTableVersion\":1", "\"lootTableVersion\":\"1\"") },
            { "negative container", Body("\"containerIndex\":0", "\"containerIndex\":-1") },
            { "negative line", Body("\"line\":0", "\"line\":-1") },
            { "zero quantity", Body("\"quantity\":3", "\"quantity\":0") },
            { "definition malformed", Body("\"stim\"", "\"Stim!\"") },
            { "pool line extra member", Body("\"quantity\":3}", "\"quantity\":3,\"x\":1}") },
            { "objective required not bool", Body("\"required\":true", "\"required\":1") },
            { "objectives not an array", Body("[{\"objectiveId\":\"extract\",\"required\":true}]", "{}") },
            { "oversized pool", Body("[{\"containerIndex\":0,\"line\":0,\"definitionId\":\"stim\",\"quantity\":3}]", "[" + pool + "]") },
            { "not an object", "[]" },
            { "trailing content", Body() + "{}" },
            { "a comment", "/**/" + Body() },
        };
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void ReadyReport_parses_strictly(string label, string json)
    {
        var refused = Record.Exception(() => Parse(json));
        Assert.True(refused is RequestFormatException, $"{label}: expected RequestFormatException, got {refused?.GetType().Name ?? "none"}");
        Assert.Equal("InvalidBody", refused!.Message);
    }

    static readonly LootTable Table = new(1, 3, 1, 2,
        [new LootEntry("stim", 1, 3, 5), new LootEntry("ammo-box", 1, 1, 5), new LootEntry("medkit", 1, 2, 1)]);

    [Fact]
    public void LootPoolValidator_accepts_a_pool_inside_the_table()
    {
        Assert.Empty(LootPoolValidator.Validate(Table, 1, []));
        Assert.Empty(LootPoolValidator.Validate(Table, 1,
            [new PoolLine(2, 0, "medkit", 2), new PoolLine(0, 1, "ammo-box", 1), new PoolLine(0, 0, "stim", 3)]));
    }

    public static TheoryData<string, int, PoolLine[], string> Violations => new()
    {
        { "table version", 2, [new PoolLine(0, 0, "stim", 1)], "lootTableVersion" },
        { "container outside the table", 1, [new PoolLine(3, 0, "stim", 1)], "container 3" },
        { "too many lines", 1, [new PoolLine(0, 0, "stim", 1), new PoolLine(0, 1, "stim", 1), new PoolLine(0, 2, "stim", 1)], "container 0 has 3" },
        { "lines not contiguous", 1, [new PoolLine(0, 1, "stim", 1)], "container 0 lines" },
        { "line repeated", 1, [new PoolLine(0, 0, "stim", 1), new PoolLine(0, 0, "stim", 2)], "container 0 lines" },
        { "unknown definition", 1, [new PoolLine(0, 0, "rocket", 1)], "unknown definition" },
        { "quantity above the entry", 1, [new PoolLine(0, 0, "stim", 4)], "quantity 4" },
        { "quantity of another entry", 1, [new PoolLine(0, 0, "ammo-box", 2)], "quantity 2" },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public void LootPoolValidator_reports_every_violation(string label, int version, PoolLine[] pool, string expected)
    {
        var errors = LootPoolValidator.Validate(Table, version, pool);
        Assert.True(errors.Any(e => e.Contains(expected, StringComparison.Ordinal)), $"{label}: {string.Join(" | ", errors)}");
    }

    [Fact]
    public void LootHash_is_canonical_and_order_independent()
    {
        PoolLine[] pool = [new PoolLine(1, 0, "medkit", 2), new PoolLine(0, 1, "ammo-box", 1), new PoolLine(0, 0, "stim", 3)];
        // The pinned vector: SHA-256 of "blackglass-loot-pool/1\ntable=1\n0,0,stim,3\n0,1,ammo-box,1\n1,0,medkit,2\n".
        Assert.Equal(Hash, LootHash.Compute(1, pool));
        Assert.Equal(Hash, LootHash.Compute(1, pool.Reverse().ToArray()));
        Assert.NotEqual(Hash, LootHash.Compute(2, pool));
        Assert.NotEqual(Hash, LootHash.Compute(1, [pool[0] with { Quantity = 1 }, pool[1], pool[2]]));
        Assert.NotEqual(Hash, LootHash.Compute(1, [pool[0] with { DefinitionId = "stim" }, pool[1], pool[2]]));
        Assert.NotEqual(Hash, LootHash.Compute(1, [pool[0], pool[1]]));
        Assert.Matches("^sha256:[0-9a-f]{64}$", LootHash.Compute(1, []));
    }

    [Fact]
    public void ObjectiveList_validates()
    {
        Assert.Empty(ObjectiveList.Validate([new Objective("extract", true), new Objective("intel.cache-1", false)]));
        Assert.NotEmpty(ObjectiveList.Validate([]));
        Assert.NotEmpty(ObjectiveList.Validate([new Objective("extract", false)]));                              // none required
        Assert.NotEmpty(ObjectiveList.Validate([new Objective("extract", true), new Objective("extract", false)])); // duplicate
        Assert.NotEmpty(ObjectiveList.Validate([new Objective("Extract", true)]));
        Assert.NotEmpty(ObjectiveList.Validate([new Objective(new string('a', 65), true)]));
        Assert.NotEmpty(ObjectiveList.Validate(Enumerable.Range(0, 33).Select(i => new Objective($"o{i}", true)).ToArray()));
        Assert.Empty(ObjectiveList.Validate(Enumerable.Range(0, 32).Select(i => new Objective($"o{i}", true)).ToArray()));
    }
}
