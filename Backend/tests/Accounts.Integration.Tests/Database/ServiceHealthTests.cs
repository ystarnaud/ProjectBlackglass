using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Api.Admin;
using Blackglass.Accounts.Api.Attempts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The fail-closed service-health check (owner ruling S7-D2): healthy only when some host is enabled, not drained, has a
/// heartbeat inside the lease and has free capacity; <c>agent</c> hosts always count, <c>dev_fixture</c> hosts only with
/// BLACKGLASS_DEV_HOSTS=enabled. Any failure propagates (never "healthy", never "unhealthy"), and a check takes no lock on an
/// inventory or a wallet and writes nothing. Also serve's reading of the attempt settings, and the gated, audited
/// development host fixture verbs <c>host register-dev</c> and <c>host heartbeat-dev</c>.
/// </summary>
[Trait("Requires", "Postgres")]
public class ServiceHealthTests : IAsyncLifetime
{
    TestDatabase db = null!;

    public async Task InitializeAsync() => db = await MigratedAsync();

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    /// <summary>A host row written as the superuser (the app never inserts drained or last_heartbeat_at): its heartbeat
    /// <paramref name="heartbeatAgeSeconds"/> before the database clock, or none.</summary>
    Task InsertHostAsync(string hostId, string kind = "agent", bool enabled = true, bool drained = false, int capacity = 3, double? heartbeatAgeSeconds = 1) =>
        ExecuteAsSuperuserAsync(db,
            """
            insert into mission_hosts (host_id, kind, secret_hash, enabled, drained, capacity, last_heartbeat_at)
            values (@hostId, @kind, @hash, @enabled, @drained, @capacity,
                    case when @age::double precision is null then null else clock_timestamp() - make_interval(secs => @age::double precision) end)
            """,
            new { hostId, kind, hash = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), enabled, drained, capacity, age = heartbeatAgeSeconds });

    async Task<bool> CheckAsync(bool devHostsEnabled, int leaseSeconds = 30, bool inTransaction = false)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        if (!inTransaction) return await ServiceHealth.CheckAsync(conn, null, devHostsEnabled, leaseSeconds, default);
        await using var tx = await conn.BeginTransactionAsync();
        var healthy = await ServiceHealth.CheckAsync(conn, tx, devHostsEnabled, leaseSeconds, default);
        await tx.RollbackAsync();
        return healthy;
    }

    [Fact]
    public async Task No_host_is_unhealthy()
    {
        foreach (var dev in new[] { false, true })
        {
            Assert.False(await CheckAsync(dev));
            Assert.False(await CheckAsync(dev, inTransaction: true));
        }
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("never-heartbeat")]
    [InlineData("drained")]
    [InlineData("disabled")]
    [InlineData("full")]
    public async Task A_stale_drained_disabled_or_full_host_is_unhealthy(string fault)
    {
        // One agent host, healthy in every respect but the one under test (lease 30 s, dev hosts on: neither is the reason).
        await InsertHostAsync("host-a", enabled: fault != "disabled", drained: fault == "drained", capacity: fault == "full" ? 0 : 3,
            heartbeatAgeSeconds: fault switch { "stale" => 31, "never-heartbeat" => null, _ => 1 });
        Assert.False(await CheckAsync(devHostsEnabled: true));
        Assert.False(await CheckAsync(devHostsEnabled: true, inTransaction: true));

        // Repairing exactly that attribute makes the same host healthy: it was the only reason.
        await ExecuteAsSuperuserAsync(db, fault switch
        {
            "stale" or "never-heartbeat" => "update mission_hosts set last_heartbeat_at = clock_timestamp()",
            "drained" => "update mission_hosts set drained = false",
            "disabled" => "update mission_hosts set enabled = true",
            _ => "update mission_hosts set capacity = 1",
        });
        Assert.True(await CheckAsync(devHostsEnabled: true));
    }

    [Fact]
    public async Task A_fresh_host_with_capacity_is_healthy()
    {
        await InsertHostAsync("host-a", capacity: 1, heartbeatAgeSeconds: 20);
        foreach (var dev in new[] { false, true }) // an agent host counts whatever the development setting
        {
            Assert.True(await CheckAsync(dev));
            Assert.True(await CheckAsync(dev, inTransaction: true));
        }
        // The lease is the caller's: the same 20-second-old heartbeat is stale under a 10-second lease.
        Assert.False(await CheckAsync(devHostsEnabled: false, leaseSeconds: 10));
        Assert.True(await CheckAsync(devHostsEnabled: false, leaseSeconds: 25));

        // One healthy host among unhealthy ones is enough.
        await InsertHostAsync("host-b", drained: true);
        await InsertHostAsync("host-c", capacity: 0);
        Assert.True(await CheckAsync(devHostsEnabled: false));
    }

    [Fact]
    public async Task A_dev_fixture_host_is_not_capacity_unless_dev_hosts_are_enabled()
    {
        await InsertHostAsync("dev-1", kind: "dev_fixture");
        Assert.False(await CheckAsync(devHostsEnabled: false));
        Assert.False(await CheckAsync(devHostsEnabled: false, inTransaction: true));
        Assert.True(await CheckAsync(devHostsEnabled: true));

        // An unhealthy fixture never counts, even when fixtures are enabled.
        await ExecuteAsSuperuserAsync(db, "update mission_hosts set last_heartbeat_at = clock_timestamp() - interval '1 hour'");
        Assert.False(await CheckAsync(devHostsEnabled: true));
    }

    [Fact]
    public async Task A_failing_health_query_never_admits()
    {
        await InsertHostAsync("host-a");
        Assert.True(await CheckAsync(devHostsEnabled: false));

        // A server error (here the app's SELECT on mission_hosts revoked) propagates: never "healthy", never "unhealthy".
        await ExecuteAsSuperuserAsync(db, "revoke select on mission_hosts from accounts_app");
        try
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => CheckAsync(devHostsEnabled: false));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
        finally { await ExecuteAsSuperuserAsync(db, "grant select on mission_hosts to accounts_app"); }

        // A broken connection propagates too.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            var pid = conn.ProcessID;
            await ExecuteAsSuperuserAsync(db, "select pg_terminate_backend(@pid)", new { pid });
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => ServiceHealth.CheckAsync(conn, null, false, 30, default));
        }

        // And a cancelled call is cancelled, not answered.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServiceHealth.CheckAsync(conn, null, false, 30, new CancellationToken(canceled: true)));

        // A lease the settings could never produce is a bug, refused before any statement.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ServiceHealth.CheckAsync(conn, null, false, 0, default));
        Assert.True(await CheckAsync(devHostsEnabled: false));
    }

    [Fact]
    public async Task A_health_failure_touches_no_wallet_attempt_or_inventory_lock()
    {
        // An account with an inventory and a wallet, and only a development fixture: unhealthy with fixtures disabled.
        var account = (await new AccountAdminStore(db.AsApp()).CreateAccountAsync("healthy-check", null, Op)).AccountId;
        await InsertHostAsync("dev-1", kind: "dev_fixture");
        const string walletXmin = "select xmin::text from energy_wallets where account_id = @account";
        var xminBefore = await ScalarAsSuperuserAsync<string>(db, walletXmin, new { account });
        var ledgerBefore = await ScalarAsSuperuserAsync<long>(db, "select count(*) from energy_ledger");

        await using (var conn = await db.AsApp().OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            Assert.False(await ServiceHealth.CheckAsync(conn, tx, false, 30, default));
            // Still inside the refused call's transaction: whatever it locked is still held, so it would show here.
            var locks = await ScalarAsSuperuserAsync<long>(db,
                """
                select count(*) from pg_locks l join pg_class c on c.oid = l.relation
                where l.pid = @pid
                  and (c.oid in ('inventories'::regclass, 'energy_wallets'::regclass)
                       or c.oid in (select indexrelid from pg_index where indrelid in ('inventories'::regclass, 'energy_wallets'::regclass)))
                """, new { pid = conn.ProcessID });
            Assert.Equal(0L, locks);
            // Nothing written at all: the check's transaction was never even assigned a transaction id.
            Assert.Null(await conn.ExecuteScalarAsync<string?>("select pg_current_xact_id_if_assigned()::text", transaction: tx));
            await tx.RollbackAsync();
        }

        Assert.Equal(xminBefore, await ScalarAsSuperuserAsync<string>(db, walletXmin, new { account }));
        Assert.Equal(ledgerBefore, await ScalarAsSuperuserAsync<long>(db, "select count(*) from energy_ledger"));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from mission_attempts"));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from energy_holds"));
    }

    // ---- serve ----

    [Fact]
    public async Task Serve_reads_the_attempt_settings_and_warns_only_when_dev_hosts_are_enabled()
    {
        foreach (var (settings, dev, lease) in new (Dictionary<string, string?>, bool, int)[]
                 {
                     (new(), false, 30),
                     (new() { [AttemptSettings.DevHostsVar] = "disabled", [AttemptSettings.HostLeaseSecondsVar] = "" }, false, 30),
                     (new() { [AttemptSettings.DevHostsVar] = "enabled", [AttemptSettings.HostLeaseSecondsVar] = "45" }, true, 45),
                 })
        {
            var logs = new CapturingLoggerProvider();
            await using (var api = await ApiServer.StartAsync(db, settings, b => { b.Logging.ClearProviders(); b.Logging.SetMinimumLevel(LogLevel.Trace); b.Logging.AddProvider(logs); }))
            {
                var health = api.App.Services.GetRequiredService<ServiceHealth>();
                Assert.Equal((dev, lease), (health.DevHostsEnabled, health.LeaseSeconds));
                var read = api.App.Services.GetRequiredService<AttemptSettings>();
                Assert.Equal((dev, lease), (read.DevHostsEnabled, read.HostLeaseSeconds));
            }
            const string warning = "development host fixtures count as capacity: never in production";
            if (dev) Assert.Contains($"Warning {AttemptSettings.LogCategory}: ", logs.Text.Split('\n').Single(l => l.Contains(warning, StringComparison.Ordinal)));
            else Assert.DoesNotContain(warning, logs.Text);
        }
    }

    [Fact]
    public async Task Serve_refuses_invalid_attempt_settings_before_the_database()
    {
        foreach (var (name, value) in new[]
                 {
                     (AttemptSettings.DevHostsVar, "on-value-xyz"), (AttemptSettings.DevHostsVar, "Enabled"),
                     (AttemptSettings.HostLeaseSecondsVar, "4"), (AttemptSettings.HostLeaseSecondsVar, "601"),
                     (AttemptSettings.HostLeaseSecondsVar, "half-value-xyz"),
                     (AttemptSettings.AssignDeadlineSecondsVar, "9"), (AttemptSettings.AssignDeadlineSecondsVar, "soon-value-xyz"),
                     (AttemptSettings.DeployMaxBodyBytesVar, "65537"), (AttemptSettings.DeployRateVar, "0"), (AttemptSettings.DeployRateVar, "lots-value-xyz"),
                     (AttemptSettings.AttemptSweepSecondsVar, "0"), (AttemptSettings.AttemptSweepSecondsVar, "often-value-xyz"),
                 })
        {
            var env = db.EnvFor("app");
            env["BLACKGLASS_DB_PORT"] = "1"; // unreachable: a refusal here proves the settings came first
            env["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            env[name] = value;
            var error = new StringWriter();
            var start = await ServeCommand.StartAsync(env, error, default);
            Assert.Equal((ExitCodes.Failure, null), (start.ExitCode, start.App));
            Assert.Contains("Configuration error", error.ToString());
            Assert.Contains(name, error.ToString());
            Assert.DoesNotContain("unavailable", error.ToString(), StringComparison.OrdinalIgnoreCase);
            if (value.Contains("-value-", StringComparison.Ordinal)) Assert.DoesNotContain(value, error.ToString());
        }
    }

    // ---- the development host fixture verbs ----

    static readonly Dictionary<string, string?> DevOn = new() { [AttemptSettings.DevHostsVar] = "enabled" };

    Dictionary<string, string?> Env(Dictionary<string, string?>? extra = null)
    {
        var env = db.EnvFor("app");
        foreach (var (k, v) in extra ?? new()) env[k] = v;
        return env;
    }

    async Task<(int Code, string Out, string Err)> CliAsync(Dictionary<string, string?> env, string? typed, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await AdminCommand.RunAsync(args, env, output, error, default, input: typed is null ? null : new StringReader(typed),
            loadCatalogue: () => throw new InvalidOperationException("The host verbs never load the catalogue."));
        return (code, output.ToString(), error.ToString());
    }

    static string[] Register(string hostId, int capacity, params string[] more) =>
        ["host", "register-dev", "--host-id", hostId, "--capacity", capacity.ToString(System.Globalization.CultureInfo.InvariantCulture), "--operator", "ops", "--reason", "dev host test", .. more];
    static string[] Heartbeat(string hostId) => ["host", "heartbeat-dev", "--host-id", hostId, "--operator", "ops", "--reason", "dev host test"];

    static string Field(string output, string name) => Regex.Match(output, $"(?m)^{Regex.Escape(name)}=(.*)$").Groups[1].Value.TrimEnd('\r');
    static string[] Names(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').Split('=')[0]).ToArray();
    Task<long> CountAsync(string table) => ScalarAsSuperuserAsync<long>(db, $"select count(*) from {table}");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("disabled")]
    public async Task Register_dev_is_refused_without_the_development_setting(string? value)
    {
        // The database is unreachable: a 64 (not 69) proves the gate is checked before any connection.
        var env = Env(new() { [AttemptSettings.DevHostsVar] = value, ["BLACKGLASS_DB_PORT"] = "1" });
        foreach (var args in new[] { Register("dev-1", 2, "--yes"), Register("dev-1", 2), Heartbeat("dev-1") })
        {
            var (code, output, error) = await CliAsync(env, "register\n", args);
            Assert.Equal((ExitCodes.Usage, ""), (code, output));
            Assert.Contains($"{AttemptSettings.DevHostsVar}=enabled", error);
            Assert.DoesNotContain("unavailable", error, StringComparison.OrdinalIgnoreCase);
        }

        // An invalid value is a configuration error that names the setting without echoing it.
        var invalid = await CliAsync(Env(new() { [AttemptSettings.DevHostsVar] = "maybe-value-xyz" }), null, Register("dev-1", 2, "--yes"));
        Assert.Equal((ExitCodes.Failure, ""), (invalid.Code, invalid.Out));
        Assert.Contains(AttemptSettings.DevHostsVar, invalid.Err);
        Assert.DoesNotContain("maybe-value-xyz", invalid.Err);
        Assert.Equal((0L, 0L), (await CountAsync("mission_hosts"), await CountAsync("operator_actions")));
    }

    [Fact]
    public async Task Host_register_dev_is_refused_unless_enabled_and_prints_no_secret()
    {
        // Disabled by default: refused, nothing written.
        Assert.Equal(ExitCodes.Usage, (await CliAsync(Env(), null, Register("dev-1", 2, "--yes"))).Code);
        Assert.Equal(0L, await CountAsync("mission_hosts"));

        // Invalid values are refused before any connection.
        foreach (var args in new[] { Register("Dev_1", 2, "--yes"), Register(new string('a', 65), 2, "--yes"), Register("dev-1", 0, "--yes"), Register("dev-1", 1001, "--yes") })
        {
            var refused = await CliAsync(Env(new(DevOn) { ["BLACKGLASS_DB_PORT"] = "1" }), null, args);
            Assert.Equal((ExitCodes.Usage, ""), (refused.Code, refused.Out));
        }

        // Not confirmed: the target is shown, nothing changes (64 with no terminal, 67 on a wrong answer).
        var preview = await CliAsync(Env(DevOn), null, Register("dev-1", 2));
        Assert.Equal(ExitCodes.Usage, preview.Code);
        Assert.Equal(["target.hostId", "target.hostExists", "target.kind", "target.capacity"], Names(preview.Out));
        Assert.Equal(("dev-1", "false", "dev_fixture", "2"), (Field(preview.Out, "target.hostId"), Field(preview.Out, "target.hostExists"),
            Field(preview.Out, "target.kind"), Field(preview.Out, "target.capacity")));
        Assert.Contains("--yes", preview.Err);
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync(Env(DevOn), "yes\n", Register("dev-1", 2))).Code);
        Assert.Equal((0L, 0L), (await CountAsync("mission_hosts"), await CountAsync("operator_actions")));

        // Enabled and confirmed by typing register: an enabled dev_fixture host with a hashed secret and no heartbeat.
        var (code, output, error) = await CliAsync(Env(DevOn), "register\n", Register("dev-1", 2));
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("Type register", error);
        Assert.Equal(["hostId", "kind", "capacity", "correlationId"], Names(output).Where(n => !n.StartsWith("target.", StringComparison.Ordinal)));
        Assert.Equal(("dev-1", "dev_fixture", "2"), (Field(output, "hostId"), Field(output, "kind"), Field(output, "capacity")));
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
        {
            var row = await su.QuerySingleAsync<(string Kind, bool Enabled, bool Drained, int Capacity, byte[] SecretHash, DateTime? Heartbeat)>(
                "select kind, enabled, drained, capacity, secret_hash, last_heartbeat_at from mission_hosts where host_id = 'dev-1'");
            Assert.Equal(("dev_fixture", true, false, 2, 32, (DateTime?)null), (row.Kind, row.Enabled, row.Drained, row.Capacity, row.SecretHash.Length, row.Heartbeat));
            var audit = await su.QuerySingleAsync<(string Action, long? AccountId, Guid CorrelationId, string Result)>(
                "select action, account_id, correlation_id, result::text from operator_actions");
            Assert.Equal(("host_register_dev", (long?)null, Guid.Parse(Field(output, "correlationId"))), (audit.Action, audit.AccountId, audit.CorrelationId));
            // Neither the stored hash nor anything secret-shaped is printed or audited.
            foreach (var text in new[] { output, error, audit.Result })
            {
                Assert.DoesNotContain(Convert.ToHexString(row.SecretHash), text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Convert.ToBase64String(row.SecretHash), text);
                Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
            }
        }
        // Not counted until it has a heartbeat, even with fixtures enabled.
        Assert.False(await CheckAsync(devHostsEnabled: true));

        // The same id again is a conflict, with nothing written; --yes confirms too.
        var again = await CliAsync(Env(DevOn), null, Register("dev-1", 5, "--yes"));
        Assert.Equal(ExitCodes.Conflict, again.Code);
        Assert.Equal("true", Field(again.Out, "target.hostExists"));
        Assert.DoesNotContain("correlationId=", again.Out);
        Assert.Equal(ExitCodes.Ok, (await CliAsync(Env(DevOn), null, Register("dev-2", 1, "--yes"))).Code);
        Assert.Equal((2L, 2L), (await CountAsync("mission_hosts"), await CountAsync("operator_actions")));
        Assert.Equal(2L, await ScalarAsSuperuserAsync<long>(db, "select count(distinct secret_hash) from mission_hosts"));
    }

    [Fact]
    public async Task Heartbeat_dev_touches_only_a_development_fixture_and_is_audited()
    {
        Assert.Equal(ExitCodes.Ok, (await CliAsync(Env(DevOn), null, Register("dev-1", 2, "--yes"))).Code);
        await InsertHostAsync("agent-1", heartbeatAgeSeconds: 3600);
        var audits = await CountAsync("operator_actions");

        var (code, output, _) = await CliAsync(Env(DevOn), null, Heartbeat("dev-1"));
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(["hostId", "lastHeartbeatAt", "correlationId"], Names(output));
        Assert.Equal("dev-1", Field(output, "hostId"));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{6}Z$", Field(output, "lastHeartbeatAt"));
        Assert.Equal(audits + 1, await CountAsync("operator_actions"));
        Assert.Equal("host_heartbeat_dev", await ScalarAsSuperuserAsync<string>(db, "select action from operator_actions order by id desc limit 1"));
        Assert.Equal("heartbeat", await ScalarAsSuperuserAsync<string>(db, "select result->>'operation' from operator_actions order by id desc limit 1"));
        Assert.True(await CheckAsync(devHostsEnabled: true));
        Assert.False(await CheckAsync(devHostsEnabled: false)); // still never capacity without the setting in the API

        // An agent host and an unknown host are both "no such development fixture" (65), with nothing written.
        foreach (var host in new[] { "agent-1", "nobody" })
        {
            var refused = await CliAsync(Env(DevOn), null, Heartbeat(host));
            Assert.Equal((ExitCodes.NotFound, ""), (refused.Code, refused.Out));
        }
        Assert.True(await ScalarAsSuperuserAsync<bool>(db, "select last_heartbeat_at < clock_timestamp() - interval '1 minute' from mission_hosts where host_id = 'agent-1'"));
        Assert.Equal(audits + 1, await CountAsync("operator_actions"));
    }
}
