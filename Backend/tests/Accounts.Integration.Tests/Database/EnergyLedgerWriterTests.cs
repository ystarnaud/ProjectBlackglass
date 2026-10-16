using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Energy;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Energy;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Opening wallets (owner ruling E1) and materializing regeneration under the wallet and policy locks (E2, E6),
/// against a real PostgreSQL 17. Every time comes from the database clock; fixtures move anchors and effective times as the
/// app would (an account's wallet inserted with a past or future anchor) or as the superuser under
/// <c>session_replication_role = replica</c>. The 0000-0006 invariants hold after every test.</summary>
[Trait("Requires", "Postgres")]
public class EnergyLedgerWriterTests : IAsyncLifetime
{
    TestDatabase db = null!;
    AccountAdminStore store = null!;

    public async Task InitializeAsync() { db = await MigratedAsync(); store = new AccountAdminStore(db.AsApp()); }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    const string InsertPolicy = "insert into energy_policies (cap, opening_balance, regen_interval, regen_amount, void_refund_pct) values ";
    static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    sealed record Entry(string Kind, int Delta, int BalanceAfter, string Key);

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    async Task<WalletState> WalletAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<WalletState>(
            "select balance as Balance, regen_anchor as RegenAnchor, regen_progress_us as ProgressMicros, policy_version as PolicyVersion from energy_wallets where account_id = @account",
            new { account });
    }

    async Task<List<Entry>> EntriesAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<Entry>(
            "select kind as Kind, delta as Delta, balance_after as BalanceAfter, idempotency_key as Key from energy_ledger where account_id = @account order by entry_id",
            new { account })).ToList();
    }

    async Task<List<EnergyPolicy>> PoliciesAsync()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<EnergyPolicy>(
            """
            select version as Version, cap as Cap, opening_balance as OpeningBalance, regen_interval as RegenInterval, regen_amount as RegenAmount,
              void_refund_pct as VoidRefundPct, effective_at as EffectiveAt
            from energy_policies order by version
            """)).ToList();
    }

    async Task AsReplicaAsync(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
        await conn.ExecuteAsync(sql, args, tx);
        await tx.CommitAsync();
    }

    /// <summary>Publishes a version as the app could (only the granted columns; the trigger takes the exclusive publication
    /// lock and sets effective_at). Task 4 builds the audited verb.</summary>
    static Task<(int Version, DateTime EffectiveAt)> PublishAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, int cap, int opening, string interval) =>
        conn.QuerySingleAsync<(int, DateTime)>(
            InsertPolicy + $"({cap}, {opening}, interval '{interval}', 1, 100) returning version, effective_at", transaction: tx);

    /// <summary>An account with a wallet as the app would open it (the wallet and its open entry in one transaction), but
    /// anchored <paramref name="ago"/> before the database clock (negative: ahead of it).</summary>
    async Task<long> NewWalletAsync(int balance, TimeSpan ago, long progress = 0, int version = 1)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id", transaction: tx);
        await conn.ExecuteAsync(
            "insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@account, @balance, clock_timestamp() - @ago, @progress, @version)",
            new { account, balance, ago, progress, version }, tx);
        await conn.ExecuteAsync(
            "insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@account, 'open', @balance, @balance, 'open')",
            new { account, balance }, tx);
        await tx.CommitAsync();
        return account;
    }

    async Task<Materialized?> MaterializeAsync(long account, NpgsqlDataSource? source = null)
    {
        await using var conn = await (source ?? db.AsApp()).OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var result = await EnergyLedgerWriter.MaterializeLockedAsync(conn, tx, account, default);
        await tx.CommitAsync();
        return result;
    }

    static void AssertUtc(Materialized m)
    {
        Assert.Equal(DateTimeKind.Utc, m.Now.Kind);
        Assert.Equal(DateTimeKind.Utc, m.Current.EffectiveAt.Kind);
        Assert.Equal(DateTimeKind.Utc, m.Wallet.RegenAnchor.Kind);
    }

    // ---- opening ----

    [Fact]
    public async Task Account_creation_opens_a_full_wallet_with_one_open_entry()
    {
        var issued = await store.CreateAccountAsync("energy-1", null, Op);
        Assert.Equal(new WalletOpen(true, 50, 1), issued.Energy);

        var wallet = await WalletAsync(issued.AccountId);
        Assert.Equal((50, 0L, 1), (wallet.Balance, wallet.ProgressMicros, wallet.PolicyVersion));
        Assert.True(wallet.RegenAnchor >= await ScalarAsync<DateTime>("select created_at from accounts where account_id = @a", new { a = issued.AccountId }));
        Assert.True(wallet.RegenAnchor <= await ScalarAsync<DateTime>("select clock_timestamp()"));
        Assert.Equal([new Entry("open", 50, 50, "open")], await EntriesAsync(issued.AccountId));

        var energy = JsonDocument.Parse(await ScalarAsync<string>("select result::text from operator_actions where action = 'account_create'")).RootElement.GetProperty("energy");
        Assert.Equal((true, 50, 1), (energy.GetProperty("opened").GetBoolean(), energy.GetProperty("balance").GetInt32(), energy.GetProperty("policyVersion").GetInt32()));
        Assert.Equal(["opened", "balance", "policyVersion"], energy.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Account_creation_opens_under_the_latest_effective_policy()
    {
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await PublishAsync(conn, null, cap: 80, opening: 30, interval: "5 minutes");
        var issued = await store.CreateAccountAsync(null, null, Op);
        Assert.Equal(new WalletOpen(true, 30, 2), issued.Energy);
        var wallet = await WalletAsync(issued.AccountId);
        Assert.Equal((30, 2), (wallet.Balance, wallet.PolicyVersion));
        Assert.Equal([new Entry("open", 30, 30, "open")], await EntriesAsync(issued.AccountId));
    }

    [Fact]
    public async Task A_failure_opening_the_wallet_rolls_back_the_account_and_its_starter()
    {
        await ExecuteAsSuperuserAsync(db,
            """
            create function refuse_wallets() returns trigger language plpgsql as $$ begin raise exception 'opening failed on purpose'; end $$;
            create trigger refuse_wallets before insert on energy_wallets for each row execute function refuse_wallets();
            """);
        var failure = await Assert.ThrowsAnyAsync<PostgresException>(() => store.CreateAccountAsync("energy-fail", "Energy Fail", Op));
        Assert.Contains("opening failed on purpose", failure.MessageText);
        Assert.Equal(0L, await ScalarAsync<long>(
            """
            select (select count(*) from accounts) + (select count(*) from dev_credentials) + (select count(*) from account_identities)
              + (select count(*) from operator_actions) + (select count(*) from operatives) + (select count(*) from item_instances)
              + (select count(*) from item_stacks) + (select count(*) from inventories) + (select count(*) from energy_wallets)
              + (select count(*) from energy_ledger)
            """));

        await ExecuteAsSuperuserAsync(db, "drop trigger refuse_wallets on energy_wallets");
        var issued = await store.CreateAccountAsync("energy-fail", "Energy Fail", Op); // the handle was never taken
        Assert.Equal(new WalletOpen(true, 50, 1), issued.Energy);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from energy_wallets"));
    }

    [Fact]
    public async Task Opening_twice_opens_once()
    {
        var issued = await store.CreateAccountAsync(null, null, Op);
        var before = await WalletAsync(issued.AccountId);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            await conn.ExecuteAsync("select 1 from accounts where account_id = @a for update", new { a = issued.AccountId }, tx);
            Assert.Equal(new WalletOpen(false, 50, 1), await EnergyProvisioner.OpenAsync(conn, tx, issued.AccountId, default));
            await tx.CommitAsync();
        }
        Assert.Equal(before, await WalletAsync(issued.AccountId));
        Assert.Equal([new Entry("open", 50, 50, "open")], await EntriesAsync(issued.AccountId));
    }

    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task Opening_and_materializing_require_read_committed(IsolationLevel level)
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        long bare;
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            bare = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync(level))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => EnergyLedgerWriter.MaterializeLockedAsync(conn, tx, account, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => EnergyProvisioner.OpenAsync(conn, tx, bare, default));
            await tx.CommitAsync();
        }
        Assert.Single(await EntriesAsync(account));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from energy_wallets where account_id = @bare", new { bare }));
    }

    [Fact]
    public async Task The_publication_lock_key_matches_the_migration()
    {
        Assert.Equal(4786318449935675244L, EnergyPolicies.PublicationLockKey);
        var source = await ScalarAsync<string>("select pg_get_functiondef('energy_policy_effective_at'::regproc)");
        Assert.Contains($"pg_advisory_xact_lock({EnergyPolicies.PublicationLockKey.ToString(System.Globalization.CultureInfo.InvariantCulture)})", source);
    }

    // ---- materialization ----

    [Fact]
    public async Task Materializing_an_account_without_a_wallet_returns_null_and_writes_nothing()
    {
        long bare;
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            bare = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
        Assert.Null(await MaterializeAsync(bare));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from energy_wallets"));
    }

    [Fact]
    public async Task A_backdated_anchor_writes_one_regen_entry_keyed_by_the_consumed_anchor()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var before = await WalletAsync(account);
        var result = (await MaterializeAsync(account))!;
        AssertUtc(result);

        Assert.Equal(3, result.Gained);
        Assert.Equal(1, result.Current.Version);
        var stored = await WalletAsync(account);
        Assert.Equal(result.Wallet, stored);
        Assert.Equal((13, result.Now, 1), (stored.Balance, stored.RegenAnchor, stored.PolicyVersion));
        Assert.InRange(stored.ProgressMicros, (long)TimeSpan.FromMinutes(5).TotalMicroseconds, (long)TenMinutes.TotalMicroseconds - 1);
        Assert.Equal([new Entry("open", 10, 10, "open"), new Entry("regen", 3, 13, EnergyMath.RegenKey(before.RegenAnchor))], await EntriesAsync(account));
    }

    [Fact]
    public async Task Materializing_twice_in_a_row_writes_one_entry()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var first = (await MaterializeAsync(account))!;
        var second = (await MaterializeAsync(account))!;
        Assert.Equal((3, 0), (first.Gained, second.Gained));
        Assert.Equal(13, second.Wallet.Balance);
        Assert.True(second.Now > first.Now);
        Assert.Equal(["open", "regen"], (await EntriesAsync(account)).Select(e => e.Kind));
        Assert.Equal(second.Wallet, await WalletAsync(account));
    }

    [Fact]
    public async Task Ten_concurrent_materializations_write_one_entry()
    {
        const int Callers = 10;
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var env = db.EnvFor("app");
        await using var wide = new DbSettings("127.0.0.1", int.Parse(env[DbSettings.PortVar]!, System.Globalization.CultureInfo.InvariantCulture), db.Name, "accounts_app", db.Password("app"))
            .CreateDataSource("blackglass-tests", maxPoolSize: Callers + 1);

        // The start barrier: the wallet is held, so all ten reach its lock on distinct connections before any proceeds.
        await using var holder = await db.AsApp().OpenConnectionAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from energy_wallets where account_id = @account for update", new { account }, holderTx);
        var gate = new TaskCompletionSource();
        var callers = Enumerable.Range(0, Callers).Select(_ => Task.Run(async () => { await gate.Task; return (await MaterializeAsync(account, wide))!; })).ToArray();
        gate.SetResult();
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (await su.ExecuteScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") < Callers)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Fewer than ten materializations waited on the wallet lock.");
                await Task.Yield();
            }
        }
        await holderTx.CommitAsync();

        var results = await Task.WhenAll(callers);
        Assert.Equal(3, results.Sum(r => r.Gained));
        Assert.Single(results, r => r.Gained == 3);
        Assert.All(results, r => Assert.Equal(13, r.Wallet.Balance));
        Assert.Equal(["open", "regen"], (await EntriesAsync(account)).Select(e => e.Kind));
        Assert.Equal(results.MaxBy(r => r.Now)!.Wallet, await WalletAsync(account));
    }

    [Fact]
    public async Task A_wallet_at_the_cap_moves_its_anchor_and_writes_no_entry()
    {
        var account = await NewWalletAsync(50, TimeSpan.FromMinutes(35), progress: 1000);
        var before = await WalletAsync(account);
        var result = (await MaterializeAsync(account))!;
        Assert.Equal(0, result.Gained);
        Assert.Equal(new WalletState(50, result.Now, 0, 1), result.Wallet);
        Assert.True(result.Now > before.RegenAnchor);
        Assert.Equal(result.Wallet, await WalletAsync(account));
        Assert.Single(await EntriesAsync(account));
    }

    [Fact]
    public async Task A_future_anchor_materializes_to_no_change()
    {
        var account = await NewWalletAsync(10, TimeSpan.Zero, progress: 5000);
        await AsReplicaAsync("update energy_wallets set regen_anchor = clock_timestamp() + interval '1 hour' where account_id = @account", new { account });
        var before = await WalletAsync(account);
        var version = await ScalarAsync<string>("select xmin::text from energy_wallets where account_id = @account", new { account });

        var result = (await MaterializeAsync(account))!;
        Assert.Equal(0, result.Gained);
        Assert.True(result.Now < before.RegenAnchor);
        Assert.Equal(before, result.Wallet);
        Assert.Equal(before, await WalletAsync(account));
        Assert.Equal(version, await ScalarAsync<string>("select xmin::text from energy_wallets where account_id = @account", new { account })); // not even rewritten
        Assert.Single(await EntriesAsync(account));
    }

    [Fact]
    public async Task A_backdated_anchor_by_a_year_writes_one_entry_to_the_cap()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromDays(365), progress: 12345);
        var before = await WalletAsync(account);
        var result = (await MaterializeAsync(account))!;
        Assert.Equal(40, result.Gained);
        Assert.Equal(new WalletState(50, result.Now, 0, 1), await WalletAsync(account));
        Assert.Equal([new Entry("open", 10, 10, "open"), new Entry("regen", 40, 50, EnergyMath.RegenKey(before.RegenAnchor))], await EntriesAsync(account));
    }

    [Fact]
    public async Task A_failure_inserting_the_regen_entry_leaves_the_wallet_unchanged()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var before = await WalletAsync(account);
        await ExecuteAsSuperuserAsync(db,
            """
            create function refuse_regen() returns trigger language plpgsql as $$ begin raise exception 'regen failed on purpose'; end $$;
            create trigger refuse_regen before insert on energy_ledger for each row when (new.kind = 'regen') execute function refuse_regen();
            """);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => MaterializeAsync(account));
        Assert.Contains("regen failed on purpose", failure.MessageText);
        Assert.Equal(before, await WalletAsync(account));
        Assert.Single(await EntriesAsync(account));
        await ExecuteAsSuperuserAsync(db, "drop trigger refuse_regen on energy_ledger");
    }

    [Fact]
    public async Task An_energy_check_violation_propagates_as_is()
    {
        // F2: a 23514 from an energy constraint is a bug, never busy: the writer neither maps nor retries it.
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var before = await WalletAsync(account);
        await ExecuteAsSuperuserAsync(db,
            """
            create function fake_chain() returns trigger language plpgsql as $$ begin
              raise exception 'chain broken on purpose' using errcode = 'check_violation', constraint = 'energy_ledger_chain'; end $$;
            create trigger fake_chain before insert on energy_ledger for each row when (new.kind = 'regen') execute function fake_chain();
            """);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => MaterializeAsync(account));
        Assert.Equal((PostgresErrorCodes.CheckViolation, "energy_ledger_chain"), (failure.SqlState, failure.ConstraintName));
        Assert.Equal(DbFailureKind.Unexpected, DbFailure.Classify(failure));
        Assert.Equal(before, await WalletAsync(account));
        await ExecuteAsSuperuserAsync(db, "drop trigger fake_chain on energy_ledger");
    }

    [Fact]
    public async Task The_clock_is_read_after_the_wallet_lock()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        await using var holder = await db.AsApp().OpenConnectionAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from energy_wallets where account_id = @account for update", new { account }, holderTx);

        var waiting = MaterializeAsync(account);
        await WaitUntilABackendWaitsOnALockAsync(db);
        Assert.False(waiting.IsCompleted);
        var released = await holder.ExecuteScalarAsync<DateTime>("select clock_timestamp()", transaction: holderTx);
        await holderTx.CommitAsync();

        var result = (await waiting)!;
        Assert.True(result.Now > released, $"{result.Now:O} should be after the release at {released:O}");
        Assert.Equal(result.Now, (await WalletAsync(account)).RegenAnchor);
    }

    [Fact]
    public async Task Regeneration_matches_the_domain_replay_of_the_database_clock()
    {
        var account = await NewWalletAsync(7, TimeSpan.FromMinutes(95), progress: 123_456_789);
        var before = await WalletAsync(account);
        var result = (await MaterializeAsync(account))!;

        var replay = EnergyMath.Materialize(before, await PoliciesAsync(), result.Now);
        Assert.Equal(replay.After, result.Wallet);
        Assert.Equal(replay.After, await WalletAsync(account));
        Assert.Equal(replay.Gained, result.Gained);
        Assert.True(result.Gained > 0);
        Assert.Equal(new Entry("regen", replay.Gained, replay.After.Balance, EnergyMath.RegenKey(replay.ConsumedAnchor!.Value)), (await EntriesAsync(account))[^1]);
    }

    // ---- policy transitions ----

    [Fact]
    public async Task A_transition_is_non_retroactive_against_the_database()
    {
        // Version 2 halves the interval (10 → 5 minutes) and takes effect 60 minutes after the wallet's anchor, which is 2 hours
        // old: 60 minutes under version 1 (6 units), then 60-odd minutes under version 2 (12 units). Retroactive would be 24.
        var account = await NewWalletAsync(10, TimeSpan.FromHours(2));
        var before = await WalletAsync(account);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await PublishAsync(conn, null, cap: 50, opening: 50, interval: "5 minutes");
        await AsReplicaAsync(
            """
            update energy_policies set effective_at = '2000-01-01T00:00:00Z' where version = 1;
            update energy_policies set effective_at = @at where version = 2;
            """, new { at = before.RegenAnchor.AddMinutes(60) });

        var result = (await MaterializeAsync(account))!;
        Assert.Equal(18, result.Gained);
        Assert.Equal(2, result.Current.Version);
        Assert.Equal(TimeSpan.FromMinutes(5), result.Current.RegenInterval);
        var stored = await WalletAsync(account);
        Assert.Equal((28, 2, result.Now), (stored.Balance, stored.PolicyVersion, stored.RegenAnchor));
        Assert.InRange(stored.ProgressMicros, 0, (long)TimeSpan.FromMinutes(5).TotalMicroseconds - 1);
        Assert.Equal(EnergyMath.Materialize(before, await PoliciesAsync(), result.Now).After, stored);
        Assert.Equal(new Entry("regen", 18, 28, EnergyMath.RegenKey(before.RegenAnchor)), (await EntriesAsync(account))[^1]);
    }

    [Fact]
    public async Task A_publication_waits_for_an_in_flight_materialization_and_takes_effect_after_its_clock()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var materialized = (await EnergyLedgerWriter.MaterializeLockedAsync(conn, tx, account, default))!;

        var publication = Task.Run(async () =>
        {
            await using var publisher = await db.AsApp().OpenConnectionAsync();
            return await PublishAsync(publisher, null, cap: 50, opening: 50, interval: "5 minutes");
        });
        await WaitUntilABackendWaitsOnALockAsync(db);
        Assert.False(publication.IsCompleted);
        await tx.CommitAsync();

        var (version, effectiveAt) = await publication;
        Assert.Equal(2, version);
        Assert.True(effectiveAt > materialized.Now, $"{effectiveAt:O} should be after the materialization's clock {materialized.Now:O}");
        Assert.Equal(1, (await WalletAsync(account)).PolicyVersion);
    }

    [Fact]
    public async Task A_materialization_waits_for_an_in_flight_publication_and_sees_it()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35), progress: 1_000_000);
        var before = await WalletAsync(account);
        await using var publisher = await db.AsApp().OpenConnectionAsync();
        await using var publisherTx = await publisher.BeginTransactionAsync();
        var (version, effectiveAt) = await PublishAsync(publisher, publisherTx, cap: 50, opening: 50, interval: "5 minutes");

        var waiting = MaterializeAsync(account);
        await WaitUntilABackendWaitsOnALockAsync(db);
        Assert.False(waiting.IsCompleted);
        await publisherTx.CommitAsync();

        var result = (await waiting)!;
        Assert.Equal(2, version);
        Assert.Equal((2, 2), (result.Current.Version, result.Wallet.PolicyVersion));
        Assert.Equal(effectiveAt, result.Current.EffectiveAt);
        Assert.True(result.Now > effectiveAt);
        var stored = await WalletAsync(account);
        Assert.Equal(result.Wallet, stored);
        Assert.Equal(EnergyMath.Materialize(before, await PoliciesAsync(), result.Now).After, stored);
    }
}
