using Blackglass.Accounts.Persistence;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class DbTimeoutsTests
{
    static Dictionary<string, string?> Env(params (string Key, string? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void The_defaults_are_a_5_second_lock_wait_and_a_15_second_statement() =>
        Assert.Equal(new DbTimeouts(5000, 15000), DbTimeouts.FromEnvironment(Env()));

    [Fact]
    public void Both_bounds_are_configurable() =>
        Assert.Equal(new DbTimeouts(250, 2000), DbTimeouts.FromEnvironment(Env((DbTimeouts.LockTimeoutVar, "250"), (DbTimeouts.StatementTimeoutVar, "2000"))));

    [Theory]
    [InlineData(DbTimeouts.LockTimeoutVar, "0")]          // 0 means "wait forever" to PostgreSQL
    [InlineData(DbTimeouts.LockTimeoutVar, "60001")]
    [InlineData(DbTimeouts.LockTimeoutVar, "5s")]
    [InlineData(DbTimeouts.LockTimeoutVar, "-1")]
    [InlineData(DbTimeouts.StatementTimeoutVar, "0")]
    [InlineData(DbTimeouts.StatementTimeoutVar, "600001")]
    [InlineData(DbTimeouts.StatementTimeoutVar, "1e3")]
    public void Unbounded_or_malformed_values_are_refused(string name, string value) =>
        Assert.Throws<DbSettingsException>(() => DbTimeouts.FromEnvironment(Env((name, value))));

    [Fact]
    public void A_lock_wait_longer_than_the_statement_bound_is_refused() =>
        Assert.Throws<DbSettingsException>(() => DbTimeouts.FromEnvironment(Env((DbTimeouts.LockTimeoutVar, "20000"), (DbTimeouts.StatementTimeoutVar, "10000"))));
}
