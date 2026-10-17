namespace Blackglass.Accounts.Domain.Insurance;

/// <summary>
/// The account-level insurance coverage window (spec §6.2, owner ruling S7-D3). Activating the same policy again extends
/// it: new expires_at = greatest(expires_at, now) + duration. A running coverage (now &lt; expires_at) keeps its start;
/// none, or an expired one (now &gt;= expires_at), starts a fresh coverage at now. Benefits never multiply: two
/// activations give a longer window, not two. Times are UTC with whole microseconds (PostgreSQL's precision); a duration
/// is above zero, whole microseconds and at most 3650 days (a provisional sanity bound). Pure: the caller supplies the
/// database's clock_timestamp() as now.
/// </summary>
public static class CoverageMath
{
    const long TicksPerMicro = 10;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(3650);

    public static (DateTime StartsAt, DateTime ExpiresAt) Extend(DateTime? startsAt, DateTime? expiresAt, DateTime now, TimeSpan duration)
    {
        Check(now, nameof(now));
        if (duration <= TimeSpan.Zero || duration.Ticks % TicksPerMicro != 0 || duration > MaxDuration)
            throw new ArgumentException("A coverage duration must be whole microseconds, above zero and at most 3650 days.", nameof(duration));
        if (startsAt.HasValue != expiresAt.HasValue)
            throw new ArgumentException("A coverage has both a start and an expiry, or neither.", nameof(startsAt));

        var running = false;
        if (startsAt is { } start && expiresAt is { } expiry)
        {
            Check(start, nameof(startsAt));
            Check(expiry, nameof(expiresAt));
            if (expiry <= start) throw new ArgumentException("A coverage must expire after it starts.", nameof(expiresAt));
            running = now < expiry;
        }

        var newStart = running ? startsAt!.Value : now;
        var from = running ? expiresAt!.Value : now;
        if (from.Ticks > DateTime.MaxValue.Ticks - duration.Ticks)
            throw new ArgumentException("The extended coverage would end past the last representable time.", nameof(duration));
        return (newStart, from + duration);
    }

    /// <summary>starts_at &lt;= now &lt; expires_at: a coverage is over exactly at its expiry.</summary>
    public static bool IsActive(DateTime startsAt, DateTime expiresAt, DateTime now)
    {
        Check(startsAt, nameof(startsAt));
        Check(expiresAt, nameof(expiresAt));
        Check(now, nameof(now));
        return startsAt <= now && now < expiresAt;
    }

    static void Check(DateTime time, string parameter)
    {
        if (time.Kind != DateTimeKind.Utc) throw new ArgumentException($"Times must be UTC, not {time.Kind}.", parameter);
        if (time.Ticks % TicksPerMicro != 0) throw new ArgumentException("Times must be whole microseconds.", parameter);
    }
}
