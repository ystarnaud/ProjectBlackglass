namespace Blackglass.Accounts.Domain.Energy;

/// <summary>
/// A wallet as stored. RegenAnchor (UTC, whole microseconds) is the database time up to which regeneration has been
/// accounted, and only moves forward. ProgressMicros is the progress toward the next unit, measured under the policy
/// PolicyVersion names: 0 &lt;= ProgressMicros &lt; that policy's interval. Balance &gt;= 0 and may exceed the cap (E6).
/// </summary>
public sealed record WalletState(int Balance, DateTime RegenAnchor, long ProgressMicros, int PolicyVersion);

/// <summary>
/// The outcome of one materialization. Gained is the total regenerated across every policy segment. ConsumedAnchor is
/// the wallet's anchor before materialization, the source of the ledger key; it is non-null exactly when Gained &gt; 0.
/// </summary>
public sealed record Regeneration(WalletState After, int Gained, DateTime? ConsumedAnchor);
