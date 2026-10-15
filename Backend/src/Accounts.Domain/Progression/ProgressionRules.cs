using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Domain.Advancement;

public enum PickRefusal { UnknownChoice, NoPendingPick }

/// <summary>
/// The advancement rules, ported exactly from Unity's ProgressionTrack.RankFor and PersistentOperativeState
/// (PendingPicks, TryPickChoice). The shared vector (Backend/contracts/progression/vectors.json) pins both sides.
/// </summary>
public static class ProgressionRules
{
    /// <summary>The rank this XP holds: 1 up to the number of thresholds. Negative XP is rank 1.</summary>
    public static int RankFor(Progression track, int xp)
    {
        var rank = 1;
        for (var i = 1; i < track.XpThresholds.Count; i++)
        {
            if (xp >= track.XpThresholds[i])
                rank = i + 1;
        }
        return rank;
    }

    /// <summary>One pick per rank gained after the first, less the picks already made; never negative.</summary>
    public static int PendingPicks(Progression track, int xp, int picksMade) =>
        Math.Max(0, RankFor(track, xp) - 1 - picksMade);

    /// <summary>
    /// Null when the pick is allowed, otherwise why not. An unknown choice (null, empty or not listed, compared
    /// ordinally) is reported before no pending pick, as TryPickChoice does. The same choice may be picked again.
    /// </summary>
    public static PickRefusal? CheckPick(Progression track, int xp, int picksMade, string? choiceId)
    {
        if (string.IsNullOrEmpty(choiceId) || !track.ChoiceIds.Contains(choiceId, StringComparer.Ordinal))
            return PickRefusal.UnknownChoice;
        if (PendingPicks(track, xp, picksMade) <= 0)
            return PickRefusal.NoPendingPick;
        return null;
    }
}
