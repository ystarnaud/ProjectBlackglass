using Blackglass.Accounts.Domain.Advancement;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Advancement;

public class ProgressionRulesTests
{
    static readonly Blackglass.Accounts.Domain.Catalogue.Progression Track = new([0, 100, 250, 450, 700], 150, ["ability", "combat"]);

    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(-1, 1)]
    [InlineData(699, 4)]
    [InlineData(700, 5)]
    [InlineData(int.MaxValue, 5)]
    public void Rank_never_exceeds_the_track(int xp, int expected)
    {
        var rank = ProgressionRules.RankFor(Track, xp);
        Assert.Equal(expected, rank);
        Assert.InRange(rank, 1, Track.XpThresholds.Count);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(700, 4)]
    [InlineData(700, 5)]
    [InlineData(700, int.MaxValue)]
    [InlineData(100, 2)]
    public void Pending_is_never_negative(int xp, int picksMade)
    {
        Assert.True(ProgressionRules.PendingPicks(Track, xp, picksMade) >= 0);
        if (picksMade >= 4)
            Assert.Equal(0, ProgressionRules.PendingPicks(Track, xp, picksMade));
    }

    [Fact]
    public void Unknown_choice_is_reported_before_no_pending_pick()
    {
        // Rank 1, nothing pending: an unknown choice is still the first refusal.
        Assert.Equal(PickRefusal.UnknownChoice, ProgressionRules.CheckPick(Track, 0, 0, "blink"));
        Assert.Equal(PickRefusal.UnknownChoice, ProgressionRules.CheckPick(Track, 0, 0, null));
        Assert.Equal(PickRefusal.UnknownChoice, ProgressionRules.CheckPick(Track, 0, 0, ""));
        Assert.Equal(PickRefusal.NoPendingPick, ProgressionRules.CheckPick(Track, 0, 0, "combat"));
        Assert.Null(ProgressionRules.CheckPick(Track, 100, 0, "combat"));
    }

    [Fact]
    public void A_single_rank_track_never_offers_a_pick()
    {
        var single = new Blackglass.Accounts.Domain.Catalogue.Progression([0], 10, ["combat"]);
        Assert.Equal(1, ProgressionRules.RankFor(single, int.MaxValue));
        Assert.Equal(0, ProgressionRules.PendingPicks(single, int.MaxValue, 0));
        Assert.Equal(PickRefusal.NoPendingPick, ProgressionRules.CheckPick(single, int.MaxValue, 0, "combat"));
    }
}
