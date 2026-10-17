using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Api.Admin;
using Blackglass.Accounts.Api.Energy;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Energy;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The energy reconciliation (owner ruling E4) against a real PostgreSQL 17: one REPEATABLE READ, READ ONLY snapshot over
/// every wallet and its ledger, the six rules of <c>db/invariants/0006_energy_ledger.sql</c> (whose findings it must agree
/// with), and one deduplicated <c>anomaly_events</c> row per outstanding finding, written in a separate transaction. It never
/// changes a wallet or the ledger. Corruption is written as the superuser under <c>session_replication_role = replica</c>,
/// because the chain and commit triggers keep the app from creating it. Also the hosted service (it waits one interval before
/// its first sweep, survives a failed sweep, and logs counts or a SQLSTATE only), its registration by serve, and
/// <c>admin energy reconcile</c>.
/// </summary>
[Trait("Requires", "Postgres")]
public class EnergyReconcilerTests : IAsyncLifetime
{
    const string InvariantFile = "0006_energy_ledger.sql";
    const string Kind = "energy_ledger_mismatch";
    TestDatabase db = null!;
    EnergyReconciler reconciler = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        reconciler = new EnergyReconciler(db.AsApp());
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    // ---- fixtures ----

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    /// <summary>A wallet opened as the app would (E1): the wallet and its <c>open</c> entry in one transaction.</summary>
    async Task<long> NewWalletAsync(int balance = 50)
    {
        var account = await NewAccountAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(
            "insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@account, @balance, clock_timestamp(), 0, 1)",
            new { account, balance }, tx);
        await conn.ExecuteAsync(
            "insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@account, 'open', @balance, @balance, 'open')",
            new { account, balance }, tx);
        await tx.CommitAsync();
        return account;
    }

    /// <summary>A development grant as the app writes it: the wallet and its entry in one transaction.</summary>
    async Task GrantAsync(long account, int amount)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var after = await conn.ExecuteScalarAsync<int>(
            "update energy_wallets set balance = balance + @amount where account_id = @account returning balance", new { account, amount }, tx);
        await conn.ExecuteAsync(
            "insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@account, 'dev_grant', @amount, @after, @key)",
            new { account, amount, after, key = "dev_grant:" + Guid.NewGuid() }, tx);
        await tx.CommitAsync();
    }

    async Task AsReplicaAsync(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
        await conn.ExecuteAsync(sql, args, tx);
        await tx.CommitAsync();
    }

    /// <summary>A wallet whose balance (64) no longer matches its ledger (open 50, grants 10 and 5): two findings,
    /// balance_vs_last_entry and balance_vs_sum.</summary>
    async Task<long> CorruptedBalanceAsync()
    {
        var account = await NewWalletAsync();
        await GrantAsync(account, 10);
        await GrantAsync(account, 5);
        await AsReplicaAsync("update energy_wallets set balance = 64 where account_id = @account", new { account });
        return account;
    }

    /// <summary>A wallet whose progress reached its policy's interval: exactly one finding, progress_not_below_interval.</summary>
    async Task<long> OneFindingAsync()
    {
        var account = await NewWalletAsync();
        await AsReplicaAsync("update energy_wallets set regen_progress_us = 600000000 where account_id = @account", new { account });
        return account;
    }

    /// <summary>Every wallet and ledger row, whole, as text: the reconciliation must leave both exactly as they were.</summary>
    async Task<(string Wallets, string Ledger)> StoredAsync()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<(string, string)>(
            """
            select coalesce((select string_agg(w::text || ' ' || w.xmin::text, '|' order by account_id) from energy_wallets w), ''),
                   coalesce((select string_agg(e::text || ' ' || e.xmin::text, '|' order by entry_id) from energy_ledger e), '')
            """);
    }

    sealed record Anomaly(string Kind, long? AccountId, string DedupeKey, string Rule, int Balance, long LedgerSum, int? LastBalanceAfter, string Keys, bool Reviewed,
        bool HasHostOrAttempt);

    async Task<List<Anomaly>> AnomaliesAsync()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<Anomaly>(
            """
            select kind as Kind, account_id as AccountId, dedupe_key as DedupeKey, detail->>'rule' as Rule, (detail->>'balance')::int as Balance,
              (detail->>'ledgerSum')::bigint as LedgerSum, (detail->>'lastBalanceAfter')::int as LastBalanceAfter,
              (select string_agg(k, ',' order by k) from jsonb_object_keys(detail) k) as Keys, reviewed_at is not null as Reviewed,
              (host_id is not null or build_revision is not null or attempt_id is not null) as HasHostOrAttempt
            from anomaly_events order by id
            """)).ToList();
    }

    /// <summary>The distinct rule kinds the invariant file reports, so each test pins the reconciler's agreement with it.</summary>
    async Task<List<string>> InvariantRulesAsync()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var sql = await File.ReadAllTextAsync(Path.Combine(InvariantRunner.RealDirectory, InvariantFile));
        return (await conn.QueryAsync<string>($"select kind from ({sql.TrimEnd().TrimEnd(';')}\n) v group by kind order by kind collate \"C\"")).ToList();
    }

    static string[] Rules(ReconcileReport report) => report.Mismatches.Select(m => m.Rule).Distinct().Order(StringComparer.Ordinal).ToArray();

    NpgsqlDataSource Wide(int connections)
    {
        var port = int.Parse(db.EnvFor("app")[DbSettings.PortVar]!, System.Globalization.CultureInfo.InvariantCulture);
        return new DbSettings(db.Host, port, db.Name, "accounts_app", db.Password("app")).CreateDataSource("blackglass-tests", maxPoolSize: connections);
    }

    /// <summary>Returns once at least <paramref name="count"/> backends of this database wait on a lock (polls every 20 ms).</summary>
    async Task WaitForLockWaitersAsync(int count)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await su.ExecuteScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Fewer than {count} backends waited on a lock.");
            await Task.Delay(20);
        }
    }

    /// <summary>Polls until <paramref name="condition"/> holds (every 20 ms, at most 30 s).</summary>
    static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(20);
        }
    }

    static int Occurrences(string text, string what) => Regex.Matches(text, Regex.Escape(what)).Count;

    /// <summary>A test-only trigger that fails every anomaly insert (optionally only for one rule) with a server message and
    /// detail that must never reach a log or the CLI's output.</summary>
    const string LeakyMessage = "leaky-server-message-q7z";
    Task FailAnomalyWritesAsync(string? rule = null) => ExecuteAsSuperuserAsync(db,
        $$"""
        create function fail_anomaly() returns trigger language plpgsql as $$ begin
          raise exception '{{LeakyMessage}} %', new.detail using detail = '{{LeakyMessage}} detail', errcode = 'P0001'; end $$;
        create trigger fail_anomaly before insert on anomaly_events for each row {{(rule is null ? "" : $"when (new.dedupe_key like '%:{rule}')")}} execute function fail_anomaly();
        """);
    Task AllowAnomalyWritesAsync() => ExecuteAsSuperuserAsync(db, "drop trigger fail_anomaly on anomaly_events; drop function fail_anomaly()");

    // ---- reports ----

    [Fact]
    public async Task A_clean_database_reports_no_mismatch()
    {
        var empty = await reconciler.RunAsync(); // no wallet at all
        Assert.Equal((0, 0, 0), (empty.WalletsChecked, empty.Mismatches.Count, empty.AnomaliesRecorded));
        var first = await NewWalletAsync();
        await GrantAsync(first, 10);
        var second = await NewWalletAsync(30);
        await GrantAsync(second, 5);
        await GrantAsync(second, 7);
        await NewAccountAsync(); // an account without a wallet (E3) is not a mismatch
        var stored = await StoredAsync();

        var report = await reconciler.RunAsync();

        Assert.Equal(2, report.WalletsChecked);
        Assert.Empty(report.Mismatches);
        Assert.Equal(0, report.AnomaliesRecorded);
        Assert.Empty(await AnomaliesAsync());
        Assert.Empty(await InvariantRulesAsync());
        Assert.Equal(stored, await StoredAsync());
        Assert.Empty(await InvariantFailuresAsync(db));
    }

    [Fact]
    public async Task A_corrupted_balance_raises_one_anomaly_and_is_left_as_it_is()
    {
        var clean = await NewWalletAsync();
        var account = await CorruptedBalanceAsync();
        var stored = await StoredAsync();

        var report = await reconciler.RunAsync();

        Assert.Equal(2, report.WalletsChecked);
        Assert.Equal(
            [new EnergyMismatch(account, "balance_vs_last_entry", 64, 65, 65), new EnergyMismatch(account, "balance_vs_sum", 64, 65, 65)],
            report.Mismatches);
        Assert.Equal(2, report.AnomaliesRecorded);
        var id = AccountId.Format(account);
        Assert.Equal(
            [
                new Anomaly(Kind, account, $"{id}:balance_vs_last_entry", "balance_vs_last_entry", 64, 65, 65, "balance,lastBalanceAfter,ledgerSum,rule", false, false),
                new Anomaly(Kind, account, $"{id}:balance_vs_sum", "balance_vs_sum", 64, 65, 65, "balance,lastBalanceAfter,ledgerSum,rule", false, false),
            ],
            await AnomaliesAsync());
        Assert.DoesNotContain(await AnomaliesAsync(), a => a.AccountId == clean);
        // Nothing is corrected: every wallet and ledger row is exactly as it was, down to its row version.
        Assert.Equal(stored, await StoredAsync());
        Assert.Equal(64, await ScalarAsSuperuserAsync<int>(db, "select balance from energy_wallets where account_id = @account", new { account }));
        Assert.Equal(Rules(report), await InvariantRulesAsync());
    }

    [Fact]
    public async Task A_second_run_does_not_duplicate_an_outstanding_anomaly()
    {
        var account = await CorruptedBalanceAsync();
        Assert.Equal(2, (await reconciler.RunAsync()).AnomaliesRecorded);
        // The balance moves again: the finding is the same (the numbers are in the detail, not the key), so no new anomaly.
        await AsReplicaAsync("update energy_wallets set balance = 70 where account_id = @account", new { account });

        var second = await reconciler.RunAsync();
        var third = await reconciler.RunAsync();

        Assert.Equal(2, second.Mismatches.Count);
        Assert.Equal(70, second.Mismatches[0].Balance);
        Assert.Equal((0, 0), (second.AnomaliesRecorded, third.AnomaliesRecorded));
        var anomalies = await AnomaliesAsync();
        Assert.Equal(2, anomalies.Count);
        Assert.All(anomalies, a => Assert.Equal(64, a.Balance)); // the first sighting's numbers stay
    }

    [Fact]
    public async Task Two_concurrent_sweeps_record_one_anomaly()
    {
        var account = await OneFindingAsync();
        // A test-only gate: every anomaly insert waits on advisory lock 43 until the test releases it, so both sweeps have
        // read their snapshot and reached their insert before either commits.
        await ExecuteAsSuperuserAsync(db,
            """
            create function gate_anomaly() returns trigger language plpgsql as $$ begin perform pg_advisory_xact_lock_shared(43); return new; end $$;
            create trigger gate_anomaly before insert on anomaly_events for each row execute function gate_anomaly();
            """);
        await using var wide = Wide(4);
        var racing = new EnergyReconciler(wide);
        ReconcileReport[] reports;
        await using (var gate = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var gateTx = await gate.BeginTransactionAsync();
            await gate.ExecuteAsync("select pg_advisory_xact_lock(43)", transaction: gateTx);
            var sweeps = new[] { Task.Run(() => racing.RunAsync()), Task.Run(() => racing.RunAsync()) };
            await WaitForLockWaitersAsync(2);
            await gateTx.CommitAsync();
            reports = await Task.WhenAll(sweeps).WaitAsync(TimeSpan.FromSeconds(60));
        }
        await ExecuteAsSuperuserAsync(db, "drop trigger gate_anomaly on anomaly_events; drop function gate_anomaly()");

        Assert.All(reports, r => Assert.Equal([new EnergyMismatch(account, "progress_not_below_interval", 50, 50, 50)], r.Mismatches));
        Assert.Equal([0, 1], reports.Select(r => r.AnomaliesRecorded).Order());
        Assert.Equal([$"{AccountId.Format(account)}:progress_not_below_interval"], (await AnomaliesAsync()).Select(a => a.DedupeKey));
    }

    [Fact]
    public async Task A_reviewed_anomaly_is_raised_again_if_the_mismatch_persists()
    {
        var account = await OneFindingAsync();
        Assert.Equal(1, (await reconciler.RunAsync()).AnomaliesRecorded);
        await ExecuteAsSuperuserAsync(db, "update anomaly_events set reviewed_at = now(), reviewed_note = 'looked at it'");

        var again = await reconciler.RunAsync();

        Assert.Equal(1, again.AnomaliesRecorded);
        var key = $"{AccountId.Format(account)}:progress_not_below_interval";
        Assert.Equal([(key, true), (key, false)], (await AnomaliesAsync()).Select(a => (a.DedupeKey, a.Reviewed)));
        Assert.Equal(0, (await reconciler.RunAsync()).AnomaliesRecorded); // and the new one is outstanding: not raised a third time
    }

    [Theory]
    [InlineData("balance", new[] { "balance_vs_last_entry", "balance_vs_sum" })]
    [InlineData("delta", new[] { "balance_vs_sum", "entry_chain" })]
    [InlineData("chain", new[] { "entry_chain" })]
    [InlineData("no_open", new[] { "open_missing" })]
    [InlineData("open_not_first", new[] { "open_not_first" })]
    [InlineData("no_entries", new[] { "balance_vs_last_entry", "open_missing" })]
    [InlineData("progress", new[] { "progress_not_below_interval" })]
    public async Task Each_rule_is_reported(string fixture, string[] expectedRules)
    {
        // The fixtures of EnergySchemaTests.Each_energy_invariant_reports_its_violation: a consistent history (open 50, then
        // grants of 10 and 5; progress just below the interval), then one corruption.
        var account = await NewWalletAsync();
        await GrantAsync(account, 10);
        await GrantAsync(account, 5);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("update energy_wallets set regen_progress_us = 599999999 where account_id = @account", new { account });
        Assert.Empty((await reconciler.RunAsync()).Mismatches);

        var middle = await ScalarAsSuperuserAsync<long>(db, "select entry_id from energy_ledger where account_id = @account order by entry_id offset 1 limit 1", new { account });
        var other = fixture is "no_open" or "open_not_first" or "no_entries" ? await NewAccountAsync() : 0;
        const string Wallet = "insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@other, @balance, clock_timestamp(), 0, 1)";
        const string Entry = "insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@other, ";
        switch (fixture)
        {
            case "balance": await AsReplicaAsync("update energy_wallets set balance = 64 where account_id = @account", new { account }); break;
            case "delta": await AsReplicaAsync("update energy_ledger set delta = 11 where entry_id = @middle", new { middle }); break;
            case "chain": await AsReplicaAsync("update energy_ledger set balance_after = 61 where entry_id = @middle", new { middle }); break;
            case "no_open":
                await AsReplicaAsync(Wallet + "; " + Entry + "'dev_grant', 10, 10, @key)", new { other, balance = 10, key = "dev_grant:" + Guid.NewGuid() });
                break;
            case "open_not_first":
                await AsReplicaAsync(Wallet + "; " + Entry + "'dev_grant', 10, 10, @key); " + Entry + "'open', 0, 10, 'open')",
                    new { other, balance = 10, key = "dev_grant:" + Guid.NewGuid() });
                break;
            case "no_entries": await AsReplicaAsync(Wallet, new { other, balance = 0 }); break;
            case "progress": await AsReplicaAsync("update energy_wallets set regen_progress_us = 600000000 where account_id = @account", new { account }); break;
        }
        var stored = await StoredAsync();
        var target = other != 0 ? other : account;

        var report = await reconciler.RunAsync();

        Assert.Equal(other != 0 ? 2 : 1, report.WalletsChecked);
        Assert.All(report.Mismatches, m => Assert.Equal(target, m.AccountId));
        Assert.Equal(expectedRules, report.Mismatches.Select(m => m.Rule));
        Assert.Equal(expectedRules, await InvariantRulesAsync()); // the same findings as the invariant file
        Assert.Equal(expectedRules.Length, report.AnomaliesRecorded);
        Assert.Equal(expectedRules.Select(r => $"{AccountId.Format(target)}:{r}"), (await AnomaliesAsync()).Select(a => a.DedupeKey));
        // The detail carries the wallet's numbers: its balance, the ledger's sum and its latest balance_after.
        var (balance, sum, last) = fixture switch
        {
            "balance" => (64, 65L, (int?)65),
            "delta" => (65, 66L, 65),
            "chain" => (65, 65L, 65),
            "no_open" or "open_not_first" => (10, 10L, 10),
            "no_entries" => (0, 0L, null),
            _ => (65, 65L, 65),
        };
        Assert.All(report.Mismatches, m => Assert.Equal((balance, sum, last), (m.Balance, m.LedgerSum, m.LastBalanceAfter)));
        Assert.All(await AnomaliesAsync(), a => Assert.Equal((target, balance, sum, last), (a.AccountId!.Value, a.Balance, a.LedgerSum, a.LastBalanceAfter)));
        Assert.Equal(stored, await StoredAsync());
    }

    [Fact]
    public async Task The_reconciler_checks_exactly_the_invariant_files_rules()
    {
        // A rule added to (or removed from) the invariant file must be added to the reconciliation too, and the reverse.
        var sql = await File.ReadAllTextAsync(Path.Combine(InvariantRunner.RealDirectory, InvariantFile));
        var fileRules = Regex.Matches(sql, @"(?m)^select '([a-z_]+)'").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(6, fileRules.Length);
        Assert.Equal(fileRules, EnergyReconciler.Rules.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Reconciliation_during_concurrent_grants_reports_nothing()
    {
        const int Callers = 20;
        var accounts = new[] { await NewWalletAsync(10), await NewWalletAsync(20), await NewWalletAsync(30), await NewWalletAsync(40) };
        await using var wide = Wide(Callers + 4);
        var granting = new EnergyAdminStore(wide, 1000);
        var sweeping = new EnergyReconciler(wide);
        // A test-only gate: every grant's audit insert waits on advisory lock 42, so the grants hold their wallet updates and
        // ledger entries uncommitted while a sweep reads.
        await ExecuteAsSuperuserAsync(db,
            """
            create function gate_grant_audit() returns trigger language plpgsql as $$ begin perform pg_advisory_xact_lock_shared(42); return new; end $$;
            create trigger gate_grant_audit before insert on operator_actions for each row when (new.action = 'energy_grant') execute function gate_grant_audit();
            """);
        var reports = new List<ReconcileReport>();
        await using (var gate = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var gateTx = await gate.BeginTransactionAsync();
            await gate.ExecuteAsync("select pg_advisory_xact_lock(42)", transaction: gateTx);
            var grants = Enumerable.Range(0, Callers)
                .Select(i => Task.Run(() => granting.GrantAsync(accounts[i % accounts.Length], 1 + i, Guid.CreateVersion7(), Op))).ToArray();
            await WaitForLockWaitersAsync(Callers);
            reports.Add(await sweeping.RunAsync()); // every grant is in flight, its entries written but not committed

            // Then sweeps race the commits.
            var sweeps = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                var mine = new List<ReconcileReport>();
                while (!grants.All(g => g.IsCompleted)) mine.Add(await sweeping.RunAsync());
                return mine;
            })).ToArray();
            await gateTx.CommitAsync();
            await Task.WhenAll(grants).WaitAsync(TimeSpan.FromSeconds(60));
            foreach (var mine in await Task.WhenAll(sweeps)) reports.AddRange(mine);
        }
        await ExecuteAsSuperuserAsync(db, "drop trigger gate_grant_audit on operator_actions; drop function gate_grant_audit()");
        reports.Add(await reconciler.RunAsync());

        Assert.True(reports.Count >= 2, $"{reports.Count} sweeps");
        Assert.All(reports, r => Assert.Equal((4, 0, 0), (r.WalletsChecked, r.Mismatches.Count, r.AnomaliesRecorded)));
        Assert.Empty(await AnomaliesAsync());
        Assert.Equal(Callers, await ScalarAsSuperuserAsync<long>(db, "select count(*) from energy_ledger where kind = 'dev_grant'"));
        Assert.Empty(await InvariantFailuresAsync(db));
    }

    [Fact]
    public async Task A_failed_anomaly_write_changes_no_balance_and_reports_the_failure()
    {
        var account = await CorruptedBalanceAsync();
        var stored = await StoredAsync();
        // The second of the two inserts fails: the first is rolled back with it.
        await FailAnomalyWritesAsync("balance_vs_sum");

        var e = await Assert.ThrowsAsync<PostgresException>(() => reconciler.RunAsync());

        Assert.Equal("P0001", e.SqlState);
        Assert.Equal(DbFailureKind.Unexpected, DbFailure.Classify(e));
        Assert.Empty(await AnomaliesAsync());
        Assert.Equal(stored, await StoredAsync());
        Assert.Equal(64, await ScalarAsSuperuserAsync<int>(db, "select balance from energy_wallets where account_id = @account", new { account }));

        // Once the write works again, the next run records both.
        await AllowAnomalyWritesAsync();
        Assert.Equal(2, (await reconciler.RunAsync()).AnomaliesRecorded);
    }

    // ---- the hosted service ----

    [Fact]
    public async Task The_service_sweeps_on_its_interval_and_logs_counts_without_secrets()
    {
        await NewWalletAsync();
        var account = await CorruptedBalanceAsync();
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var sweeps = 0;
        var service = new EnergyReconcileService(ct =>
        {
            Interlocked.Increment(ref sweeps); // on entry, before any database work
            return reconciler.RunAsync(ct);
        }, TimeSpan.FromMilliseconds(200), factory);

        await service.StartAsync(default);
        Assert.Equal(0, Volatile.Read(ref sweeps)); // never during startup (The_first_sweep_waits_one_whole_interval pins the wait itself)
        await WaitUntilAsync(() => Occurrences(logs.Text, "Energy reconciliation checked") >= 2, "two sweeps");
        await service.StopAsync(default);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully); // cancellation ends it cleanly

        var text = logs.Text;
        Assert.Contains($"{EnergyReconcileService.LogCategory}: Energy reconciliation checked 2 wallets: 2 mismatches, 2 anomalies recorded", text);
        Assert.Contains($"{EnergyReconcileService.LogCategory}: Energy reconciliation checked 2 wallets: 2 mismatches, 0 anomalies recorded", text);
        Assert.Equal(2, (await AnomaliesAsync()).Count);
        Assert.Equal(64, await ScalarAsSuperuserAsync<int>(db, "select balance from energy_wallets where account_id = @account", new { account }));
        Assert.DoesNotContain(db.Password("app"), text);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(db.Name, text); // no connection details at all
    }

    [Fact]
    public async Task The_first_sweep_waits_one_whole_interval()
    {
        // Counted on entry, so a sweep started at startup is seen at once, before any database round trip; the interval is
        // long enough that no tick can fall inside the settle.
        var sweeps = 0;
        using var factory = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider()));
        var service = new EnergyReconcileService(_ =>
        {
            Interlocked.Increment(ref sweeps);
            return Task.FromResult(new ReconcileReport(0, [], 0));
        }, TimeSpan.FromSeconds(10), factory);

        await service.StartAsync(default);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        var early = Volatile.Read(ref sweeps);
        await service.StopAsync(default);

        Assert.Equal(0, early);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_database_failure_in_one_sweep_is_logged_and_the_next_sweep_still_happens()
    {
        await CorruptedBalanceAsync();
        await FailAnomalyWritesAsync();
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var service = new EnergyReconcileService(reconciler.RunAsync, TimeSpan.FromMilliseconds(200), factory);

        await service.StartAsync(default);
        await WaitUntilAsync(() => logs.Text.Contains("Energy reconciliation failed"), "a failed sweep");
        await AllowAnomalyWritesAsync();
        await WaitUntilAsync(() => logs.Text.Contains("2 anomalies recorded"), "a sweep after the failure");
        await service.StopAsync(default);

        var text = logs.Text;
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Contains($"Error {EnergyReconcileService.LogCategory}: Energy reconciliation failed: SQLSTATE P0001", text);
        Assert.DoesNotContain(LeakyMessage, text); // the server's message and detail can quote values: never logged
        Assert.DoesNotContain("\"balance\"", text);
        Assert.DoesNotContain(db.Password("app"), text);
        Assert.Equal(2, (await AnomaliesAsync()).Count);
    }

    [Fact]
    public async Task Serve_starts_and_answers_health_while_the_first_sweep_is_held()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ReconcileReport> Held(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new ReconcileReport(0, [], 0);
        }

        // serve registers the service (the default interval is on); the test swaps in one whose sweep blocks until released.
        await using (var api = await ApiServer.StartAsync(db, configure: b =>
                         b.Services.AddSingleton(sp => new EnergyReconcileService(Held, TimeSpan.FromMilliseconds(50), sp.GetRequiredService<ILoggerFactory>()))))
        {
            Assert.Same(api.App.Services.GetRequiredService<EnergyReconcileService>(), api.App.Services.GetServices<IHostedService>().OfType<EnergyReconcileService>().Single());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var health = await api.Http.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.False(release.Task.IsCompleted); // still held
            Assert.Equal(1, Volatile.Read(ref calls)); // and the timer doesn't start another while it is
        } // stopping cancels the held sweep: shutdown never waits for it either
        release.TrySetResult();
    }

    [Fact]
    public async Task Zero_disables_the_service()
    {
        await using (var off = await ApiServer.StartAsync(db, new() { [EnergySettings.ReconcileSecondsVar] = "0" }))
        {
            Assert.Null(off.App.Services.GetService<EnergyReconcileService>());
            Assert.Empty(off.App.Services.GetServices<IHostedService>().OfType<EnergyReconcileService>());
            Assert.Equal(HttpStatusCode.OK, (await off.Http.GetAsync("/health")).StatusCode);
        }
        foreach (var on in new Dictionary<string, string?>[] { new(), new() { [EnergySettings.ReconcileSecondsVar] = "60" } })
        {
            await using var api = await ApiServer.StartAsync(db, on);
            Assert.Single(api.App.Services.GetServices<IHostedService>().OfType<EnergyReconcileService>());
        }
    }

    [Theory]
    [InlineData("59")]
    [InlineData("86401")]
    [InlineData("-1")]
    [InlineData("hourly-xyz")]
    public async Task Serve_refuses_an_invalid_reconcile_interval(string value)
    {
        var env = db.EnvFor("app");
        env["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        env[EnergySettings.ReconcileSecondsVar] = value;
        var error = new StringWriter();

        var start = await ServeCommand.StartAsync(env, error, default);

        Assert.Equal((ExitCodes.Failure, null), (start.ExitCode, start.App));
        Assert.Contains("Configuration error", error.ToString());
        Assert.Contains(EnergySettings.ReconcileSecondsVar, error.ToString());
        Assert.DoesNotContain(value, error.ToString()); // a refusal never echoes the value
    }

    // ---- the CLI ----

    [Fact]
    public async Task Energy_reconcile_prints_counts_and_exits_zero_with_mismatches()
    {
        var loads = 0;
        async Task<(int Code, string Out, string Err)> ReconcileAsync(Dictionary<string, string?>? env = null)
        {
            var (output, error) = (new StringWriter(), new StringWriter());
            var code = await AdminCommand.RunAsync(["energy", "reconcile", "--operator", "ops", "--reason", "nightly check"], env ?? db.EnvFor("app"), output, error, default,
                loadCatalogue: () => { loads++; throw new InvalidOperationException("no catalogue"); });
            return (code, output.ToString(), error.ToString());
        }
        static string Field(string output, string name) => Regex.Match(output, $"(?m)^{Regex.Escape(name)}=(.*)$").Groups[1].Value.TrimEnd('\r');
        static string[] Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        static string[] Names(string output) => Lines(output).Select(l => l.Split('=')[0]).ToArray();
        static string[] MismatchLines(string output) => Lines(output).Where(l => l.StartsWith("mismatch=", StringComparison.Ordinal)).ToArray();

        // A clean database: the counts, and no mismatch line at all.
        await NewWalletAsync();
        var clean = await ReconcileAsync();
        Assert.Equal(ExitCodes.Ok, clean.Code);
        Assert.Equal(["walletsChecked", "mismatches", "anomaliesRecorded", "correlationId"], Names(clean.Out));
        Assert.Equal(("1", "0", "0"), (Field(clean.Out, "walletsChecked"), Field(clean.Out, "mismatches"), Field(clean.Out, "anomaliesRecorded")));
        Assert.Empty(MismatchLines(clean.Out));

        // Two corrupted wallets: two findings on the first, one on the second.
        var account = await CorruptedBalanceAsync();
        var later = await OneFindingAsync();
        var stored = await StoredAsync();
        var audits = await ScalarAsSuperuserAsync<long>(db, "select count(*) from operator_actions");

        var first = await ReconcileAsync();
        var second = await ReconcileAsync();

        Assert.Equal(ExitCodes.Ok, first.Code); // the anomaly is the signal, not the exit code
        // One line per finding, after the counts and before the correlation id, in the reconciler's order (account, then rule).
        Assert.Equal(["walletsChecked", "mismatches", "anomaliesRecorded", "mismatch", "mismatch", "mismatch", "correlationId"], Names(first.Out));
        string[] expected =
            [$"mismatch={AccountId.Format(account)}:balance_vs_last_entry", $"mismatch={AccountId.Format(account)}:balance_vs_sum",
                $"mismatch={AccountId.Format(later)}:progress_not_below_interval"];
        Assert.Equal(expected, MismatchLines(first.Out));
        Assert.Equal(("3", "3", "3"), (Field(first.Out, "walletsChecked"), Field(first.Out, "mismatches"), Field(first.Out, "anomaliesRecorded")));
        Assert.True(Guid.TryParseExact(Field(first.Out, "correlationId"), "D", out _));
        Assert.Equal((ExitCodes.Ok, "3", "0"), (second.Code, Field(second.Out, "mismatches"), Field(second.Out, "anomaliesRecorded")));
        Assert.Equal(expected, MismatchLines(second.Out)); // every finding is named, whether this run recorded it or not
        Assert.NotEqual(Field(first.Out, "correlationId"), Field(second.Out, "correlationId"));
        Assert.Equal(3, (await AnomaliesAsync()).Count);
        Assert.Equal(stored, await StoredAsync());
        Assert.Equal(audits, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operator_actions")); // it changes no account: nothing is audited
        Assert.Equal(0, loads);

        // A failed anomaly write: the existing classification (exit 1), the SQLSTATE only, and no counts.
        await AsReplicaAsync("update energy_wallets set regen_progress_us = 600000000 where account_id = @account", new { account });
        await FailAnomalyWritesAsync();
        var failed = await ReconcileAsync();
        await AllowAnomalyWritesAsync();
        Assert.Equal((ExitCodes.Failure, ""), (failed.Code, failed.Out));
        Assert.Contains("Unexpected database error: SQLSTATE P0001", failed.Err);
        Assert.DoesNotContain(LeakyMessage, failed.Err);
        // And an unreachable database: 69.
        var unreachable = db.EnvFor("app");
        unreachable[DbSettings.PortVar] = "1";
        Assert.Equal(ExitCodes.Unavailable, (await ReconcileAsync(unreachable)).Code);
        Assert.Equal(3, (await AnomaliesAsync()).Count);
    }
}
