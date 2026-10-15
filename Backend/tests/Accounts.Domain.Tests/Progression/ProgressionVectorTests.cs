using System.Text.Json;
using Blackglass.Accounts.Domain.Advancement;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Advancement;

/// <summary>The shared progression vector (Backend/contracts/progression), which Unity's EditMode tests run too.</summary>
public class ProgressionVectorTests
{
    static JsonElement Load() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "progression", "vectors.json"))).RootElement;

    [Fact]
    public void Every_case_matches()
    {
        var root = Load();
        var track = root.GetProperty("track");
        var thresholds = track.GetProperty("xpThresholds").EnumerateArray().Select(e => e.GetInt32()).ToList();
        var choices = track.GetProperty("choiceIds").EnumerateArray().Select(e => e.GetString()!).ToList();
        var progression = new Blackglass.Accounts.Domain.Catalogue.Progression(thresholds, 150, choices);

        var cases = root.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 30, "the vector file lost cases");
        var names = new HashSet<string>();

        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            Assert.True(names.Add(name), $"duplicate case name {name}");
            var xp = c.GetProperty("xp").GetInt32();
            var picksMade = c.GetProperty("picksMade").GetInt32();
            var choiceId = c.GetProperty("choiceId").GetString();
            var outcome = c.GetProperty("outcome").GetString();

            Assert.True(c.GetProperty("rank").GetInt32() == ProgressionRules.RankFor(progression, xp), $"{name}: rank");
            Assert.True(c.GetProperty("pending").GetInt32() == ProgressionRules.PendingPicks(progression, xp, picksMade), $"{name}: pending");
            var refusal = ProgressionRules.CheckPick(progression, xp, picksMade, choiceId);
            Assert.True(outcome == (refusal?.ToString() ?? "ok"), $"{name}: outcome expected {outcome} but was {refusal?.ToString() ?? "ok"}");
        }
    }
}
