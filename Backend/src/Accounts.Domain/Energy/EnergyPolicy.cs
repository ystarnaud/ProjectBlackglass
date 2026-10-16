namespace Blackglass.Accounts.Domain.Energy;

/// <summary>
/// One immutable energy policy version (spec §6.1, §19; every value provisional). RegenInterval is a whole number of
/// microseconds, above zero and at most 7 days. EffectiveAt is UTC with whole microseconds, set by the database when the
/// version is published. 0 &lt;= OpeningBalance &lt;= Cap; RegenAmount &gt;= 1. The cap limits natural regeneration only (E6).
/// </summary>
public sealed record EnergyPolicy(int Version, int Cap, int OpeningBalance, TimeSpan RegenInterval, int RegenAmount, int VoidRefundPct, DateTime EffectiveAt);
