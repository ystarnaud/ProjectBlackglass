using Blackglass.Accounts.Domain.Insurance;

namespace Blackglass.Accounts.Domain.Tests.Insurance;

public class CoverageMathTests
{
    static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [Fact]
    public void Extend_keeps_the_remaining_time_and_adds_the_duration()
    {
        // No coverage: a new one starts now.
        Assert.Equal((Now, Now + Day), CoverageMath.Extend(null, null, Now, Day));

        // A running coverage keeps its start and its remaining 6 hours; the new day is added on top.
        var startedAt = Now.AddHours(-18);
        var expiresAt = Now.AddHours(6);
        Assert.Equal((startedAt, expiresAt + Day), CoverageMath.Extend(startedAt, expiresAt, Now, Day));

        // One microsecond before expiry still counts as running.
        var almost = Now.AddTicks(10);
        Assert.Equal((startedAt, almost + Day), CoverageMath.Extend(startedAt, almost, Now, Day));

        // Two extensions give a longer coverage, never a multiplied one.
        var once = CoverageMath.Extend(null, null, Now, Day);
        var twice = CoverageMath.Extend(once.StartsAt, once.ExpiresAt, Now.AddHours(1), Day);
        Assert.Equal((Now, Now + 2 * Day), twice);
    }

    [Fact]
    public void Extend_after_expiry_restarts_from_now()
    {
        var startedAt = Now.AddDays(-3);
        // Exactly at expires_at the coverage is over: it restarts at now (greatest(expires_at, now) = now).
        Assert.Equal((Now, Now + Day), CoverageMath.Extend(startedAt, Now, Now, Day));
        Assert.Equal((Now, Now + Day), CoverageMath.Extend(startedAt, Now.AddDays(-1), Now, Day));
    }

    [Fact]
    public void IsActive_excludes_expires_at()
    {
        var startsAt = Now;
        var expiresAt = Now + Day;
        Assert.True(CoverageMath.IsActive(startsAt, expiresAt, startsAt));
        Assert.True(CoverageMath.IsActive(startsAt, expiresAt, expiresAt.AddTicks(-10)));
        Assert.False(CoverageMath.IsActive(startsAt, expiresAt, expiresAt));
        Assert.False(CoverageMath.IsActive(startsAt, expiresAt, startsAt.AddTicks(-10)));
    }

    public static TheoryData<string> BadExtends => ["local now", "sub-microsecond now", "zero duration", "negative duration", "sub-microsecond duration",
        "duration above 3650 days", "only a start", "only an expiry", "expiry not after start", "local expiry", "past the end of DateTime"];

    [Theory]
    [MemberData(nameof(BadExtends))]
    public void Extend_refuses_malformed_input(string label)
    {
        Action call = label switch
        {
            "local now" => () => CoverageMath.Extend(null, null, DateTime.SpecifyKind(Now, DateTimeKind.Local), Day),
            "sub-microsecond now" => () => CoverageMath.Extend(null, null, Now.AddTicks(1), Day),
            "zero duration" => () => CoverageMath.Extend(null, null, Now, TimeSpan.Zero),
            "negative duration" => () => CoverageMath.Extend(null, null, Now, -Day),
            "sub-microsecond duration" => () => CoverageMath.Extend(null, null, Now, Day + TimeSpan.FromTicks(1)),
            "duration above 3650 days" => () => CoverageMath.Extend(null, null, Now, TimeSpan.FromDays(3650) + TimeSpan.FromTicks(10)),
            "only a start" => () => CoverageMath.Extend(Now, null, Now, Day),
            "only an expiry" => () => CoverageMath.Extend(null, Now, Now, Day),
            "expiry not after start" => () => CoverageMath.Extend(Now, Now, Now, Day),
            "local expiry" => () => CoverageMath.Extend(Now.AddDays(-1), DateTime.SpecifyKind(Now.AddDays(1), DateTimeKind.Unspecified), Now, Day),
            "past the end of DateTime" => () => CoverageMath.Extend(Now, new DateTime(DateTime.MaxValue.Ticks - DateTime.MaxValue.Ticks % 10, DateTimeKind.Utc).AddDays(-1), Now, TimeSpan.FromDays(2)),
            _ => throw new InvalidOperationException(label),
        };
        Assert.ThrowsAny<ArgumentException>(call);
    }

    [Fact]
    public void The_largest_duration_is_accepted() =>
        Assert.Equal((Now, Now + TimeSpan.FromDays(3650)), CoverageMath.Extend(null, null, Now, TimeSpan.FromDays(3650)));
}
