using System.Collections.Immutable;

namespace Blackglass.Accounts.Persistence.Attempts;

/// <summary>The attempt and participant state names the backend code reasons about, in one place.</summary>
public static class AttemptStates
{
    /// <summary>
    /// The participant states in which a participation is active (owner ruling S7-D4): the account holds its one active
    /// participation, so its inventory is locked. Every participant state except the terminal ones (<c>released</c>, later
    /// <c>settled</c>). The same list is 0007's <c>one_active_participation</c> index predicate and the active states of the
    /// <c>0007_attempts.sql</c> invariant; a test pins the three against each other.
    /// </summary>
    public static readonly ImmutableArray<string> ActiveParticipant =
        ["admitted", "joined", "left", "result_pending", "unresponsive", "quarantined"];
}
