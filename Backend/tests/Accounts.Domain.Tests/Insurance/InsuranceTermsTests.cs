using Blackglass.Accounts.Domain.Insurance;

namespace Blackglass.Accounts.Domain.Tests.Insurance;

public class InsuranceTermsTests
{
    /// <summary>The §6.2 example exactly as the spec prints it (indented, the spec's member order).</summary>
    const string SpecExample = """
        {
          "covers": ["defeat", "abandoned", "disconnected", "expired"],
          "consumableRefund": { "percent": 50, "rounding": "floor", "destination": "stash" },
          "lootRetention": {
            "maxEntries": 2,
            "maxUnitsPerEntry": 3,
            "eligibleCategories": ["Consumable", "Armor", "Utility", "Weapon"],
            "rank": ["categoryPriority", "saltedHash"],
            "categoryPriority": ["Weapon", "Armor", "Utility", "Consumable"]
          },
          "progressXp": { "percent": 40, "basis": "requiredObjectivesCompleted" }
        }
        """;

    const string Canonical = """{"covers":["defeat","abandoned","disconnected","expired"],"consumableRefund":{"percent":50,"rounding":"floor","destination":"stash"},"lootRetention":{"maxEntries":2,"maxUnitsPerEntry":3,"eligibleCategories":["Consumable","Armor","Utility","Weapon"],"rank":["categoryPriority","saltedHash"],"categoryPriority":["Weapon","Armor","Utility","Consumable"]},"progressXp":{"percent":40,"basis":"requiredObjectivesCompleted"}}""";

    [Fact]
    public void Terms_parse_the_6_2_example_and_round_trip_canonically()
    {
        var terms = InsuranceTerms.Parse(SpecExample);

        Assert.Equal(["defeat", "abandoned", "disconnected", "expired"], terms.Covers);
        Assert.Equal(new ConsumableRefund(50, "floor", "stash"), terms.ConsumableRefund);
        Assert.Equal(2, terms.LootRetention.MaxEntries);
        Assert.Equal(3, terms.LootRetention.MaxUnitsPerEntry);
        Assert.Equal(["Consumable", "Armor", "Utility", "Weapon"], terms.LootRetention.EligibleCategories);
        Assert.Equal(["categoryPriority", "saltedHash"], terms.LootRetention.Rank);
        Assert.Equal(["Weapon", "Armor", "Utility", "Consumable"], terms.LootRetention.CategoryPriority);
        Assert.Equal(new ProgressXp(40, "requiredObjectivesCompleted"), terms.ProgressXp);

        Assert.Equal(Canonical, terms.Canonical());
        Assert.Equal(Canonical, InsuranceTerms.Parse(Canonical).Canonical());
        // Member order and whitespace are insignificant to the reader; the canonical form is fixed.
        var reordered = """{"progressXp":{"basis":"requiredObjectivesCompleted","percent":40},"lootRetention":{"categoryPriority":["Weapon","Armor","Utility","Consumable"],"rank":["categoryPriority","saltedHash"],"eligibleCategories":["Consumable","Armor","Utility","Weapon"],"maxUnitsPerEntry":3,"maxEntries":2},"consumableRefund":{"destination":"stash","rounding":"floor","percent":50},"covers":["defeat","abandoned","disconnected","expired"]}""";
        Assert.Equal(Canonical, InsuranceTerms.Parse(reordered).Canonical());
    }

    [Fact]
    public void Terms_accept_the_boundaries()
    {
        var edge = Canonical
            .Replace("\"covers\":[\"defeat\",\"abandoned\",\"disconnected\",\"expired\"]", "\"covers\":[\"expired\"]")
            .Replace("\"percent\":50", "\"percent\":0")
            .Replace("\"maxEntries\":2", "\"maxEntries\":64")
            .Replace("\"maxUnitsPerEntry\":3", "\"maxUnitsPerEntry\":1000")
            .Replace("\"eligibleCategories\":[\"Consumable\",\"Armor\",\"Utility\",\"Weapon\"]", "\"eligibleCategories\":[]")
            .Replace("\"percent\":40", "\"percent\":100");
        Assert.Equal(edge, InsuranceTerms.Parse(edge).Canonical());
    }

    public static TheoryData<string, string, string> Refusals => new()
    {
        // label, the text to replace in the canonical form, its replacement
        { "unknown top-level member", "{\"covers\"", "{\"bonus\":1,\"covers\"" },
        { "unknown nested member", "\"destination\":\"stash\"", "\"destination\":\"stash\",\"extra\":true" },
        { "duplicate top-level member", "{\"covers\":[\"defeat\",\"abandoned\",\"disconnected\",\"expired\"],", "{\"covers\":[\"defeat\"],\"covers\":[\"defeat\",\"abandoned\",\"disconnected\",\"expired\"]," },
        { "duplicate nested member", "\"percent\":50,", "\"percent\":50,\"percent\":50," },
        { "missing member", "\"maxEntries\":2,", "" },
        { "missing section", ",\"progressXp\":{\"percent\":40,\"basis\":\"requiredObjectivesCompleted\"}", "" },
        { "percent as a string", "\"percent\":50", "\"percent\":\"50\"" },
        { "percent as a decimal", "\"percent\":50", "\"percent\":50.0" },
        { "percent with an exponent", "\"percent\":50", "\"percent\":5e1" },
        { "percent as minus zero", "\"percent\":50", "\"percent\":-0" },
        { "percent above 100", "\"percent\":50", "\"percent\":101" },
        { "percent below 0", "\"percent\":40", "\"percent\":-1" },
        { "rounding other than floor", "\"floor\"", "\"ceil\"" },
        { "destination other than stash", "\"stash\"", "\"bag\"" },
        { "maxEntries above 64", "\"maxEntries\":2", "\"maxEntries\":65" },
        { "maxEntries below 0", "\"maxEntries\":2", "\"maxEntries\":-1" },
        { "maxUnitsPerEntry 0", "\"maxUnitsPerEntry\":3", "\"maxUnitsPerEntry\":0" },
        { "maxUnitsPerEntry above 1000", "\"maxUnitsPerEntry\":3", "\"maxUnitsPerEntry\":1001" },
        { "covers empty", "[\"defeat\",\"abandoned\",\"disconnected\",\"expired\"]", "[]" },
        { "covers unknown outcome", "\"abandoned\"", "\"success\"" },
        { "covers duplicate", "\"abandoned\"", "\"defeat\"" },
        { "covers not an array", "[\"defeat\",\"abandoned\",\"disconnected\",\"expired\"]", "\"defeat\"" },
        { "covers wrong case", "\"defeat\"", "\"Defeat\"" },
        { "eligible category unknown", "\"eligibleCategories\":[\"Consumable\"", "\"eligibleCategories\":[\"Junk\"" },
        { "eligible category duplicate", "\"eligibleCategories\":[\"Consumable\",\"Armor\"", "\"eligibleCategories\":[\"Consumable\",\"Consumable\"" },
        { "eligible category lowercase", "\"eligibleCategories\":[\"Consumable\"", "\"eligibleCategories\":[\"consumable\"" },
        { "rank reordered", "[\"categoryPriority\",\"saltedHash\"]", "[\"saltedHash\",\"categoryPriority\"]" },
        { "rank shortened", "[\"categoryPriority\",\"saltedHash\"]", "[\"categoryPriority\"]" },
        { "rank extended", "[\"categoryPriority\",\"saltedHash\"]", "[\"categoryPriority\",\"saltedHash\",\"value\"]" },
        { "priority missing a category", "\"categoryPriority\":[\"Weapon\",\"Armor\",\"Utility\",\"Consumable\"]", "\"categoryPriority\":[\"Weapon\",\"Armor\",\"Utility\"]" },
        { "priority repeating a category", "\"categoryPriority\":[\"Weapon\",\"Armor\",\"Utility\",\"Consumable\"]", "\"categoryPriority\":[\"Weapon\",\"Armor\",\"Utility\",\"Weapon\"]" },
        { "priority with an unknown category", "\"categoryPriority\":[\"Weapon\",\"Armor\",\"Utility\",\"Consumable\"]", "\"categoryPriority\":[\"Weapon\",\"Armor\",\"Utility\",\"Junk\"]" },
        { "basis other", "\"requiredObjectivesCompleted\"", "\"allObjectives\"" },
        { "section not an object", "\"progressXp\":{\"percent\":40,\"basis\":\"requiredObjectivesCompleted\"}", "\"progressXp\":40" },
        { "string element not a string", "\"abandoned\"", "1" },
        { "null member", "\"rounding\":\"floor\"", "\"rounding\":null" },
        { "trailing comma", "\"basis\":\"requiredObjectivesCompleted\"}", "\"basis\":\"requiredObjectivesCompleted\",}" },
        { "a comment", "{\"covers\"", "{/*c*/\"covers\"" },
        { "trailing content", "\"requiredObjectivesCompleted\"}}", "\"requiredObjectivesCompleted\"}}{}" },
        { "a lone surrogate escape", "\"floor\"", "\"\\ud800\"" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Terms_refuse_unknown_members_duplicates_and_out_of_range_values(string label, string find, string replace)
    {
        Assert.Contains(find, Canonical);
        var json = Canonical.Replace(find, replace);
        Assert.NotEqual(Canonical, json);
        var refused = Assert.Throws<InsuranceTermsException>(() => InsuranceTerms.Parse(json));
        Assert.False(string.IsNullOrEmpty(refused.Message), label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    public void Terms_refuse_a_non_object_document(string json) =>
        Assert.Throws<InsuranceTermsException>(() => InsuranceTerms.Parse(json));
}
