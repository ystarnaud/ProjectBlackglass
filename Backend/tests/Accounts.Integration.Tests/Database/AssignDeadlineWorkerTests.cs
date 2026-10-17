using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Api.Attempts;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The assignment deadline (Task 7; owner ruling S7-D6) against a real PostgreSQL 17: <see cref="AttemptStore.SweepAsync"/>
/// selects admitted attempts whose <c>assign_deadline &lt;= clock_timestamp()</c> (database time) FOR UPDATE SKIP LOCKED in a
/// short transaction, then releases each in its own transaction with <c>cancel_reason = 'no_capacity'</c>; and
/// <see cref="AssignDeadlineWorker"/>, the hosted service that runs it every BLACKGLASS_ATTEMPT_SWEEP_SECONDS. Deadlines are
/// made due by backdating the row as the superuser (time is never mocked, spec §18.1), or, for the exact boundary, by a
/// frozen <c>clock_timestamp()</c> on the store's connections. The 0000-0007 invariants hold after every test.
/// </summary>
[Trait("Requires", "Postgres")]
public class AssignDeadlineWorkerTests : IAsyncLifetime
{
    TestDatabase db = null!;
    AccountAdminStore accounts = null!;
    AdmissionStore admissions = null!;
    AttemptStore attempts = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        accounts = new AccountAdminStore(db.AsApp());
        var hosts = new DevHosts(db.AsApp());
        await hosts.RegisterAsync("dev-fixture-1", 10, Op);
        await hosts.HeartbeatAsync("dev-fixture-1", Op);
        admissions = new AdmissionStore(db.AsApp(), new ServiceHealth(devHostsEnabled: true, leaseSeconds: 600), new CryptoAttemptSecrets(), GameCatalogue.Embedded, 60);
        attempts = new AttemptStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    async Task<long> AccountAsync() => (await accounts.CreateAccountAsync(null, null, Op)).AccountId;

    /// <summary>A provisioned account admitted by the real store (a reserve of 10 from 50): its attempt id.</summary>
    async Task<(long Account, Guid Attempt)> AdmittedAsync()
    {
        var account = await AccountAsync();
        var operative = await ScalarAsync<long>("select min(operative_id) from operatives where account_id = @account", new { account });
        var admission = await admissions.AdmitAsync(account, Guid.CreateVersion7(), new DeployRequest("procedural-standard", [operative]));
        return (account, Guid.Parse(JsonDocument.Parse(admission.Body).RootElement.GetProperty("attemptId").GetString()!));
    }

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    async Task<T> RowAsync<T>(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<T>(sql, args);
    }

    Task<string> StateOfAsync(Guid attempt) => ScalarAsync<string>(
        """
        select a.state || ':' || coalesce(a.cancel_reason, '-') || ':' || a.xmin::text || ' ' || p.state || ':' || p.xmin::text || ' ' || h.state || ':' || h.xmin::text
          || ' ' || (select count(*) from energy_ledger l where l.attempt_id = a.attempt_id) || ' ' || w.xmin::text || ':' || w.balance
        from mission_attempts a join attempt_participants p using (attempt_id) join energy_holds h using (attempt_id, account_id)
          left join energy_wallets w on w.account_id = p.account_id
        where a.attempt_id = @attempt
        """, new { attempt });

    Task<int> BalanceAsync(long account) => ScalarAsync<int>("select balance from energy_wallets where account_id = @account", new { account });

    Task<long> ReleaseEntriesAsync(Guid attempt) => ScalarAsync<long>("select count(*) from energy_ledger where kind = 'release' and attempt_id = @attempt", new { attempt });

    NpgsqlConnectionStringBuilder AppConnection(int maxPoolSize) => new()
    {
        Host = db.Host, Port = int.Parse(db.EnvFor("app")[DbSettings.PortVar]!, System.Globalization.CultureInfo.InvariantCulture), Database = db.Name,
        Username = "accounts_app", Password = db.Password("app"), ApplicationName = "blackglass-tests", MaxPoolSize = maxPoolSize,
    };

    /// <summary>
    /// The app role on connections whose unqualified clock_timestamp() reads the value in bg_test_clock.frozen (a test-only
    /// function before pg_catalog in their search_path: the technique of InsuranceAdminStoreTests and AdmissionStoreTests).
    /// The store's SQL is unchanged. Dropped before the invariants run.
    /// </summary>
    async Task<NpgsqlDataSource> FrozenClockAsync(DateTime at)
    {
        await ExecuteAsSuperuserAsync(db,
            """
            create schema bg_test_clock;
            create table bg_test_clock.frozen (at timestamptz not null);
            create function bg_test_clock.clock_timestamp() returns timestamptz language sql stable as 'select at from bg_test_clock.frozen';
            grant usage on schema bg_test_clock to accounts_app;
            grant select on bg_test_clock.frozen to accounts_app;
            grant execute on function bg_test_clock.clock_timestamp() to accounts_app;
            """);
        await ExecuteAsSuperuserAsync(db, "insert into bg_test_clock.frozen (at) values (@at)", new { at });
        var builder = AppConnection(2);
        builder.SearchPath = "bg_test_clock, pg_catalog, public";
        return NpgsqlDataSource.Create(builder);
    }

    static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(50);
        }
    }

    // ---- the sweep -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_worker_releases_only_past_the_deadline()
    {
        var (late, lateAttempt) = await AdmittedAsync();
        var (onTime, onTimeAttempt) = await AdmittedAsync();
        await AttemptFixtures.ExpireDeadlineAsync(db, lateAttempt);
        var waiting = await StateOfAsync(onTimeAttempt);

        Assert.Equal(new SweepReport(1, 1, 0, 0, []), await attempts.SweepAsync(10), SweepComparer.Instance);

        Assert.Equal(("cancelled", "no_capacity"), await RowAsync<(string, string)>("select state, cancel_reason from mission_attempts where attempt_id = @lateAttempt", new { lateAttempt }));
        Assert.Equal("released", await ScalarAsync<string>("select state from attempt_participants where attempt_id = @lateAttempt", new { lateAttempt }));
        Assert.Equal(1L, await ReleaseEntriesAsync(lateAttempt));
        Assert.Equal(50, await BalanceAsync(late));
        Assert.Equal(waiting, await StateOfAsync(onTimeAttempt)); // its deadline is 60 s away: untouched
        Assert.Equal(40, await BalanceAsync(onTime));

        Assert.Equal(new SweepReport(0, 0, 0, 0, []), await attempts.SweepAsync(10), SweepComparer.Instance); // nothing due any more
        Assert.Equal(1L, await ReleaseEntriesAsync(lateAttempt));
    }

    [Fact]
    public async Task Two_workers_never_release_one_attempt_twice()
    {
        // (a) While one worker releases an attempt (stopped on the inventories row, held here, with the attempt row locked),
        // another worker's selection skips it (SKIP LOCKED) and returns at once with nothing due.
        var (first, firstAttempt) = await AdmittedAsync();
        await AttemptFixtures.ExpireDeadlineAsync(db, firstAttempt);
        Task<SweepReport> releasing;
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var held = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select 1 from inventories where account_id = @first for update", new { first }, held);
            releasing = Task.Run(() => attempts.SweepAsync(10));
            await AttemptFixtures.WaitForLockWaitersAsync(db, 1);
            Assert.Equal(new SweepReport(0, 0, 0, 0, []), await attempts.SweepAsync(10).WaitAsync(TimeSpan.FromSeconds(10)), SweepComparer.Instance);
            Assert.False(releasing.IsCompleted);
            await held.CommitAsync();
        }
        Assert.Equal(new SweepReport(1, 1, 0, 0, []), await releasing.WaitAsync(TimeSpan.FromSeconds(30)), SweepComparer.Instance);
        Assert.Equal(1L, await ReleaseEntriesAsync(firstAttempt));
        Assert.Equal(50, await BalanceAsync(first));

        // (b) Both workers selected the attempt before either released it (each selection's lock ends with its short
        // transaction): the releases queue on the attempt row, and the second sees the stored outcome.
        var (second, secondAttempt) = await AdmittedAsync();
        await AttemptFixtures.ExpireDeadlineAsync(db, secondAttempt);
        var one = Assert.Single(await attempts.DueAsync(10));
        var two = Assert.Single(await attempts.DueAsync(10));
        Assert.Equal(one, two);
        Task<ExpireResult> a, b;
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var held = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select 1 from inventories where account_id = @second for update", new { second }, held);
            a = Task.Run(() => attempts.ExpireAsync(one));
            await AttemptFixtures.WaitForLockWaitersAsync(db, 1);
            b = Task.Run(() => attempts.ExpireAsync(two));
            await AttemptFixtures.WaitForLockWaitersAsync(db, 2);
            await held.CommitAsync();
        }
        Assert.Equal([ExpireResult.Released, ExpireResult.AlreadyEnded], (await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30))).Order().ToArray());
        Assert.Equal(1L, await ReleaseEntriesAsync(secondAttempt));
        Assert.Equal(50, await BalanceAsync(second)); // restored exactly once
    }

    [Fact]
    public async Task The_worker_never_cancels_an_attempt_in_a_later_state()
    {
        // A due attempt that moved on (assigned by direct SQL, as Stage 8's claim will) is never cancelled, by the sweep or by a
        // release of a selection taken just before the move; nor in any later state along the section 7.2 path.
        var (account, attempt) = await AdmittedAsync();
        await AttemptFixtures.ExpireDeadlineAsync(db, attempt);
        var selected = Assert.Single(await attempts.DueAsync(10));
        foreach (var state in new[] { "assigned", "launching", "ready", "running", "ending" })
        {
            await AttemptFixtures.MoveAsync(db, attempt, state);
            var before = await StateOfAsync(attempt);

            Assert.Equal(new SweepReport(0, 0, 0, 0, []), await attempts.SweepAsync(10), SweepComparer.Instance);
            Assert.Equal(ExpireResult.NotAdmitted, await attempts.ExpireAsync(selected));
            Assert.Equal(AttemptError.InvalidState, (await Assert.ThrowsAsync<AttemptException>(() => attempts.CancelAsync(account, attempt))).Error);
            Assert.Equal(before, await StateOfAsync(attempt));
        }
        Assert.Equal(0L, await ReleaseEntriesAsync(attempt));
        Assert.Equal(40, await BalanceAsync(account));
    }

    [Fact]
    public async Task The_deadline_boundary_is_database_time()
    {
        // assign_deadline exactly equal to the database now is due; one microsecond later is not. Zero-amount attempts (the
        // fixture), so the frozen clock drives the selection and the attempt's ended_at only.
        var exact = await AttemptFixtures.AdmitAsync(db, await AccountAsync());
        var later = await AttemptFixtures.AdmitAsync(db, await AccountAsync());
        var at = await ScalarAsync<DateTime>("select clock_timestamp()");
        await AttemptFixtures.SetDeadlineAsync(db, exact, at);
        await AttemptFixtures.SetDeadlineAsync(db, later, at.AddTicks(10)); // + 1 µs
        await using var frozen = await FrozenClockAsync(at);
        try
        {
            var store = new AttemptStore(frozen);
            Assert.Equal([new DueAttempt(exact, await ScalarAsync<long>("select created_by from mission_attempts where attempt_id = @exact", new { exact }))],
                await store.DueAsync(10));
            Assert.Equal(new SweepReport(1, 1, 0, 0, []), await store.SweepAsync(10), SweepComparer.Instance);
            Assert.Equal(("cancelled", at), await RowAsync<(string, DateTime)>("select state, ended_at from mission_attempts where attempt_id = @exact", new { exact }));
            Assert.Equal("admitted", await ScalarAsync<string>("select state from mission_attempts where attempt_id = @later", new { later }));

            // One microsecond on, the second is due too.
            await ExecuteAsSuperuserAsync(db, "update bg_test_clock.frozen set at = @next", new { next = at.AddTicks(10) });
            Assert.Equal(new SweepReport(1, 1, 0, 0, []), await store.SweepAsync(10), SweepComparer.Instance);
            Assert.Equal("no_capacity", await ScalarAsync<string>("select cancel_reason from mission_attempts where attempt_id = @later", new { later }));
        }
        finally { await ExecuteAsSuperuserAsync(db, "drop schema bg_test_clock cascade"); }
    }

    [Fact]
    public async Task Due_attempts_come_oldest_deadline_first_and_at_most_the_batch()
    {
        var created = new List<Guid>();
        var now = await ScalarAsync<DateTime>("select clock_timestamp()");
        for (var i = 0; i < 3; i++)
        {
            var attempt = await AttemptFixtures.AdmitAsync(db, await AccountAsync());
            await AttemptFixtures.SetDeadlineAsync(db, attempt, now.AddSeconds(-10 + i * 3)); // -10, -7, -4 s
            created.Add(attempt);
        }
        Assert.Equal(created.Take(2).ToArray(), (await attempts.DueAsync(2)).Select(d => d.AttemptId).ToArray());
        Assert.Equal(new SweepReport(2, 2, 0, 0, []), await attempts.SweepAsync(2), SweepComparer.Instance);
        Assert.Equal(new SweepReport(1, 1, 0, 0, []), await attempts.SweepAsync(2), SweepComparer.Instance);
    }

    // ---- the hosted service --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_worker_starts_without_sweeping_and_survives_failures()
    {
        // Startup: counted on entry, so a sweep at startup would be seen at once; the interval is long enough that no tick falls
        // inside the settle.
        var sweeps = 0;
        using (var quiet = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider())))
        {
            var idle = new AssignDeadlineWorker(_ =>
            {
                Interlocked.Increment(ref sweeps);
                return Task.FromResult(new SweepReport(0, 0, 0, 0, []));
            }, TimeSpan.FromSeconds(10), quiet);
            await idle.StartAsync(default);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            var early = Volatile.Read(ref sweeps);
            await idle.StopAsync(default);
            Assert.Equal(0, early);
            Assert.True(idle.ExecuteTask!.IsCompletedSuccessfully);
        }

        // Failures: a release that fails (an injected fault at the attempt UPDATE, whose message quotes a value) is logged with
        // its SQLSTATE only, and so is a sweep whose selection fails (the app role loses SELECT on mission_attempts); the next
        // sweep still runs, and once the faults are gone the attempt is released exactly once.
        const string Leak = "leaky-message-value-xyz";
        var (account, attempt) = await AdmittedAsync();
        await AttemptFixtures.ExpireDeadlineAsync(db, attempt);
        await ExecuteAsSuperuserAsync(db,
            "create function bg_test_fail() returns trigger language plpgsql as $$ begin raise exception 'injected failure leaky-message-value-xyz' using errcode = 'P0001'; end $$; " +
            "create trigger bg_test_fail before update on mission_attempts for each row execute function bg_test_fail();");
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var worker = new AssignDeadlineWorker(ct => attempts.SweepAsync(AssignDeadlineWorker.BatchSize, ct), TimeSpan.FromMilliseconds(200), factory);
        await worker.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(logs.Text.Contains("SQLSTATE P0001")), "a failed release");
            await ExecuteAsSuperuserAsync(db, "revoke select on mission_attempts from accounts_app");
            await WaitUntilAsync(() => Task.FromResult(logs.Text.Contains("SQLSTATE 42501")), "a failed sweep");
            await ExecuteAsSuperuserAsync(db, "grant select on mission_attempts to accounts_app; drop trigger bg_test_fail on mission_attempts; drop function bg_test_fail();");
            await WaitUntilAsync(async () => await ScalarAsync<string>("select state from mission_attempts where attempt_id = @attempt", new { attempt }) == "cancelled", "the release");
            await WaitUntilAsync(() => Task.FromResult(logs.Text.Contains("released 1 attempts")), "the release log line");
        }
        finally { await worker.StopAsync(default); }

        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully); // failures never ended it; cancellation ends it cleanly
        var text = logs.Text;
        Assert.Contains($"{AttemptSettings.LogCategory}: Assignment deadline release failed for 1 of 1 due attempts: SQLSTATE P0001", text);
        Assert.Contains($"{AttemptSettings.LogCategory}: Assignment deadline sweep failed: SQLSTATE 42501", text);
        Assert.DoesNotContain(Leak, text); // the server's message can quote values: never logged
        Assert.DoesNotContain(db.Password("app"), text);
        Assert.DoesNotContain(db.Name, text);
        Assert.Equal(1L, await ReleaseEntriesAsync(attempt));
        Assert.Equal(50, await BalanceAsync(account));
    }

    [Fact]
    public async Task Serve_runs_the_worker_on_its_interval()
    {
        await using (var api = await ApiServer.StartAsync(db, new() { [AttemptSettings.AttemptSweepSecondsVar] = "1" }))
        {
            Assert.Single(api.App.Services.GetServices<IHostedService>().OfType<AssignDeadlineWorker>());
            var (account, attempt) = await AdmittedAsync();
            await AttemptFixtures.ExpireDeadlineAsync(db, attempt);
            await WaitUntilAsync(async () => await ScalarAsync<string>("select state from mission_attempts where attempt_id = @attempt", new { attempt }) == "cancelled",
                "serve's worker to release the attempt");
            Assert.Equal("no_capacity", await ScalarAsync<string>("select cancel_reason from mission_attempts where attempt_id = @attempt", new { attempt }));
            Assert.Equal(50, await BalanceAsync(account));
        }
        // The default (15 s) is on too: the worker is required for the exactly-once release, so it cannot be switched off.
        await using (var api = await ApiServer.StartAsync(db))
            Assert.Single(api.App.Services.GetServices<IHostedService>().OfType<AssignDeadlineWorker>());
    }

    /// <summary>Compares reports by value, the failure list included (a record compares lists by reference).</summary>
    sealed class SweepComparer : IEqualityComparer<SweepReport>
    {
        public static readonly SweepComparer Instance = new();
        public bool Equals(SweepReport? x, SweepReport? y) =>
            x is not null && y is not null && (x.Due, x.Released, x.AlreadyEnded, x.NotAdmitted) == (y.Due, y.Released, y.AlreadyEnded, y.NotAdmitted)
            && x.Failures.SequenceEqual(y.Failures);
        public int GetHashCode(SweepReport obj) => obj.Due;
    }
}
