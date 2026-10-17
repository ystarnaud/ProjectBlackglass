using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Energy;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Energy;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The operator energy verbs (owner rulings E2, E3, E5, E6) against a real PostgreSQL 17: opening a wallet, idempotent
/// development grants and policy publication. Each is one READ COMMITTED transaction that locks the account row, then the
/// wallet (grants), then takes the shared policy lock; a publication takes only the exclusive one. Every time comes from the
/// database clock; fixtures move anchors as the superuser under <c>session_replication_role = replica</c>. The 0000-0006
/// invariants hold after every test.
/// </summary>
[Trait("Requires", "Postgres")]
public class EnergyAdminStoreTests : IAsyncLifetime
{
    const int GrantMax = 1000;
    static readonly DbTimeouts Short = new(LockTimeoutMs: 300, StatementTimeoutMs: 10_000);
    TestDatabase db = null!;
    EnergyAdminStore energy = null!;
    AccountAdminStore accounts = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        energy = new EnergyAdminStore(db.AsApp(), GrantMax);
        accounts = new AccountAdminStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    sealed record Entry(string Kind, int Delta, int BalanceAfter, string Key);
    sealed record Audit(string Action, long? AccountId, Guid CorrelationId, string Operator, string Reason, JsonElement Result);

    static string GrantKey(Guid key) => "dev_grant:" + key.ToString("D");

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    async Task<WalletState> WalletAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<WalletState>(
            "select balance as Balance, regen_anchor as RegenAnchor, regen_progress_us as ProgressMicros, policy_version as PolicyVersion from energy_wallets where account_id = @account",
            new { account });
    }

    Task<string> WalletRowVersionAsync(long account) =>
        ScalarAsync<string>("select xmin::text from energy_wallets where account_id = @account", new { account });

    async Task<List<Entry>> EntriesAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<Entry>(
            "select kind as Kind, delta as Delta, balance_after as BalanceAfter, idempotency_key as Key from energy_ledger where account_id = @account order by entry_id",
            new { account })).ToList();
    }

    async Task<List<Audit>> AuditsAsync(string action)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var rows = await conn.QueryAsync<(string Action, long? AccountId, Guid CorrelationId, string Operator, string Reason, string Result)>(
            "select action, account_id, correlation_id, operator, reason, result::text from operator_actions where action = @action order by id", new { action });
        return rows.Select(r => new Audit(r.Action, r.AccountId, r.CorrelationId, r.Operator, r.Reason, JsonDocument.Parse(r.Result).RootElement.Clone())).ToList();
    }

    /// <summary>Everything a grant could write for this account: its wallet (state and row version) and ledger, and the
    /// operator_actions table as a whole (its row count and last id).</summary>
    async Task<(WalletState Wallet, string Row, List<Entry> Entries, long Audits, long LastAudit)> StateAsync(long account) =>
        (await WalletAsync(account), await WalletRowVersionAsync(account), await EntriesAsync(account),
            await ScalarAsync<long>("select count(*) from operator_actions"), await ScalarAsync<long>("select coalesce(max(id), 0)::bigint from operator_actions"));

    async Task AssertUnchangedAsync(long account, (WalletState Wallet, string Row, List<Entry> Entries, long Audits, long LastAudit) before)
    {
        var after = await StateAsync(account);
        Assert.Equal(before.Wallet, after.Wallet);
        Assert.Equal(before.Row, after.Row); // not even materialized: the row was never rewritten
        Assert.Equal(before.Entries, after.Entries);
        Assert.Equal((before.Audits, before.LastAudit), (after.Audits, after.LastAudit));
    }

    /// <summary>The store after an operator lowered BLACKGLASS_ENERGY_GRANT_MAX from 1000 to 100.</summary>
    EnergyAdminStore Lowered(NpgsqlDataSource? source = null) => new(source ?? db.AsApp(), grantMax: 100);

    /// <summary>A jsonb object's keys, sorted (jsonb keeps its own key order, not the writer's).</summary>
    static string[] Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
    static string[] Keys(params string[] names) => names.Order(StringComparer.Ordinal).ToArray();

    async Task AsReplicaAsync(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
        await conn.ExecuteAsync(sql, args, tx);
        await tx.CommitAsync();
    }

    /// <summary>An account with a wallet as the app would open it, but anchored <paramref name="ago"/> before the database clock.</summary>
    async Task<long> NewWalletAsync(int balance, TimeSpan ago, long progress = 0)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id", transaction: tx);
        await conn.ExecuteAsync(
            "insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@account, @balance, clock_timestamp() - @ago, @progress, 1)",
            new { account, balance, ago, progress }, tx);
        await conn.ExecuteAsync(
            "insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@account, 'open', @balance, @balance, 'open')",
            new { account, balance }, tx);
        await tx.CommitAsync();
        return account;
    }

    /// <summary>An account without a wallet (as one created before Stage 6 is).</summary>
    async Task<long> BareAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    /// <summary>The app role with a pool wide enough for every racer, and no lock_timeout (a waiter waits as long as its holder).</summary>
    NpgsqlDataSource Wide(int connections)
    {
        var port = int.Parse(db.EnvFor("app")[DbSettings.PortVar]!, System.Globalization.CultureInfo.InvariantCulture);
        return new DbSettings("127.0.0.1", port, db.Name, "accounts_app", db.Password("app")).CreateDataSource("blackglass-tests", maxPoolSize: connections);
    }

    /// <summary>Returns once at least <paramref name="count"/> backends of this database wait on a lock (polls every 20 ms).</summary>
    async Task WaitForLockWaitersAsync(int count = 1)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await su.ExecuteScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Fewer than {count} backends waited on a lock.");
            await Task.Delay(20);
        }
    }

    /// <summary>A test-only gate: every energy_grant audit insert waits on advisory lock 42 until the test releases it, so a
    /// grant can be held after its wallet update and ledger entries, just before its audit and commit.</summary>
    const long GateKey = 42;
    Task InstallAuditGateAsync() => ExecuteAsSuperuserAsync(db,
        """
        create function gate_grant_audit() returns trigger language plpgsql as $$ begin perform pg_advisory_xact_lock(42); return new; end $$;
        create trigger gate_grant_audit before insert on operator_actions for each row when (new.action = 'energy_grant') execute function gate_grant_audit();
        """);
    Task RemoveAuditGateAsync() => ExecuteAsSuperuserAsync(db, "drop trigger gate_grant_audit on operator_actions; drop function gate_grant_audit()");

    // ---- grants ----

    [Fact]
    public async Task A_grant_materializes_first_then_credits_and_writes_entries_in_order()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var before = await WalletAsync(account);
        var key = Guid.CreateVersion7();
        var op = OperatorContext.Create("granter", "grant test");

        var result = await energy.GrantAsync(account, 5, key, op);

        Assert.Equal(new GrantResult(Amount: 5, BalanceAfter: 18, Regenerated: 3, Replayed: false), result);
        Assert.Equal(
            [new Entry("open", 10, 10, "open"), new Entry("regen", 3, 13, EnergyMath.RegenKey(before.RegenAnchor)), new Entry("dev_grant", 5, 18, GrantKey(key))],
            await EntriesAsync(account));
        var wallet = await WalletAsync(account);
        Assert.Equal((18, 1), (wallet.Balance, wallet.PolicyVersion));
        Assert.True(wallet.RegenAnchor > before.RegenAnchor);
        Assert.InRange(wallet.ProgressMicros, (long)TimeSpan.FromMinutes(5).TotalMicroseconds, (long)TimeSpan.FromMinutes(10).TotalMicroseconds - 1); // kept

        var audit = Assert.Single(await AuditsAsync("energy_grant"));
        Assert.Equal((account, op.CorrelationId, "granter", "grant test"), (audit.AccountId!.Value, audit.CorrelationId, audit.Operator, audit.Reason));
        Assert.Equal(Keys("amount", "balanceAfter", "regenerated", "replayed", "idempotencyKey"), Names(audit.Result));
        Assert.Equal(key.ToString("D"), audit.Result.GetProperty("idempotencyKey").GetString());
        Assert.Equal((5, 18, 3, false), (audit.Result.GetProperty("amount").GetInt32(), audit.Result.GetProperty("balanceAfter").GetInt32(),
            audit.Result.GetProperty("regenerated").GetInt32(), audit.Result.GetProperty("replayed").GetBoolean()));
    }

    [Fact]
    public async Task A_replayed_key_returns_the_stored_result_and_writes_no_entry()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var key = Guid.CreateVersion7();
        var first = await energy.GrantAsync(account, 5, key, Op);
        // Time to regenerate again: a replay that materialized would write a regen entry and move the wallet.
        await AsReplicaAsync("update energy_wallets set regen_anchor = regen_anchor - interval '35 minutes' where account_id = @account", new { account });
        var before = await StateAsync(account);

        var replay = await energy.GrantAsync(account, 5, key, Op);

        Assert.Equal(new GrantResult(5, first.BalanceAfter, 0, true), replay);
        await AssertUnchangedAsync(account, before); // the wallet not even rewritten, no entry, and no audit row: a replay writes nothing
        var audit = Assert.Single(await AuditsAsync("energy_grant")); // the first grant's only
        Assert.Equal(key.ToString("D"), audit.Result.GetProperty("idempotencyKey").GetString());
        Assert.Equal((5, first.BalanceAfter, false), (audit.Result.GetProperty("amount").GetInt32(), audit.Result.GetProperty("balanceAfter").GetInt32(),
            audit.Result.GetProperty("replayed").GetBoolean()));
    }

    [Fact]
    public async Task A_reused_key_with_another_amount_is_refused_and_writes_nothing()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var key = Guid.CreateVersion7();
        await energy.GrantAsync(account, 5, key, Op);
        await AsReplicaAsync("update energy_wallets set regen_anchor = regen_anchor - interval '35 minutes' where account_id = @account", new { account });
        var (row, entries) = (await WalletRowVersionAsync(account), await EntriesAsync(account));

        var e = await Assert.ThrowsAsync<EnergyException>(() => energy.GrantAsync(account, 6, key, Op));

        Assert.Equal(EnergyError.IdempotencyKeyReused, e.Error);
        Assert.Equal(entries, await EntriesAsync(account));
        Assert.Equal(row, await WalletRowVersionAsync(account));
        Assert.Single(await AuditsAsync("energy_grant"));
    }

    [Fact]
    public async Task A_reused_key_for_another_account_is_refused_and_writes_nothing()
    {
        var first = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var second = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var secondRow = await WalletRowVersionAsync(second);

        // Sequential: the key is looked up across every account.
        var key = Guid.CreateVersion7();
        await energy.GrantAsync(first, 5, key, Op);
        var sequential = await Assert.ThrowsAsync<EnergyException>(() => energy.GrantAsync(second, 5, key, Op));
        Assert.Equal(EnergyError.IdempotencyKeyReused, sequential.Error);
        Assert.Equal([new Entry("open", 10, 10, "open")], await EntriesAsync(second));
        Assert.Equal(secondRow, await WalletRowVersionAsync(second));

        // Concurrent first use: the first grant is held after its entry, before its audit; the second cannot see that entry,
        // so it reaches its own insert and waits on the global unique index, then fails once the first commits.
        var concurrentKey = Guid.CreateVersion7();
        await InstallAuditGateAsync();
        await using (var gate = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var gateTx = await gate.BeginTransactionAsync();
            await gate.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = GateKey }, gateTx);
            var winner = Task.Run(() => energy.GrantAsync(first, 7, concurrentKey, Op));
            await WaitForLockWaitersAsync(1);
            var loser = Task.Run(() => energy.GrantAsync(second, 7, concurrentKey, Op));
            await WaitForLockWaitersAsync(2);
            Assert.Equal(1L, await ScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event = 'transactionid'"));
            await gateTx.CommitAsync();

            Assert.False((await winner).Replayed);
            var concurrent = await Assert.ThrowsAsync<EnergyException>(() => loser);
            Assert.Equal(EnergyError.IdempotencyKeyReused, concurrent.Error);
        }
        await RemoveAuditGateAsync();

        Assert.Equal([new Entry("open", 10, 10, "open")], await EntriesAsync(second));
        Assert.Equal(secondRow, await WalletRowVersionAsync(second)); // its materialization rolled back with it
        Assert.Equal(2L, await ScalarAsync<long>("select count(*) from energy_ledger where kind = 'dev_grant' and account_id = @first", new { first }));
        Assert.All(await AuditsAsync("energy_grant"), a => Assert.Equal(first, a.AccountId));
    }

    [Fact]
    public async Task Twenty_concurrent_grants_with_one_key_credit_once()
    {
        const int Callers = 20;
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        await using var wide = Wide(Callers + 1);
        var racing = new EnergyAdminStore(wide, GrantMax);
        var key = Guid.CreateVersion7();

        // The start barrier: the account row is held, so all twenty reach its lock on distinct connections before any proceeds.
        await using var holder = await db.AsApp().OpenConnectionAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from accounts where account_id = @account for update", new { account }, holderTx);
        var callers = Enumerable.Range(0, Callers).Select(i => Task.Run(() => racing.GrantAsync(account, 5, key, OperatorContext.Create("racer", $"race {i}")))).ToArray();
        await WaitForLockWaitersAsync(Callers);
        await holderTx.CommitAsync();

        var results = await Task.WhenAll(callers);
        Assert.Single(results, r => !r.Replayed);
        Assert.All(results, r => Assert.Equal((5, 18), (r.Amount, r.BalanceAfter)));
        Assert.Equal(3, results.Sum(r => r.Regenerated));
        Assert.Equal(["open", "regen", "dev_grant"], (await EntriesAsync(account)).Select(e => e.Kind));
        Assert.Equal(18, (await WalletAsync(account)).Balance);
        var audit = Assert.Single(await AuditsAsync("energy_grant")); // the one grant's: the nineteen replays wrote nothing
        Assert.False(audit.Result.GetProperty("replayed").GetBoolean());
    }

    [Fact]
    public async Task Twenty_concurrent_grants_with_distinct_keys_all_credit_and_the_chain_holds()
    {
        const int Callers = 20;
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        await using var wide = Wide(Callers + 1);
        var racing = new EnergyAdminStore(wide, GrantMax);

        await using var holder = await db.AsApp().OpenConnectionAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from accounts where account_id = @account for update", new { account }, holderTx);
        var callers = Enumerable.Range(1, Callers).Select(i => Task.Run(() => racing.GrantAsync(account, i, Guid.CreateVersion7(), Op))).ToArray();
        await WaitForLockWaitersAsync(Callers);
        await holderTx.CommitAsync();

        var results = await Task.WhenAll(callers);
        Assert.All(results, r => Assert.False(r.Replayed));
        var granted = Enumerable.Range(1, Callers).Sum();
        var regenerated = results.Sum(r => r.Regenerated);
        Assert.True(regenerated >= 3, $"regenerated {regenerated}");
        var entries = await EntriesAsync(account);
        Assert.Equal(Callers, entries.Count(e => e.Kind == "dev_grant"));
        var running = 0;
        foreach (var e in entries) Assert.Equal(running += e.Delta, e.BalanceAfter); // the chain, entry by entry
        Assert.Equal(10 + regenerated + granted, (await WalletAsync(account)).Balance);
        Assert.Equal(entries[^1].BalanceAfter, (await WalletAsync(account)).Balance);
        Assert.Equal(entries[^1].BalanceAfter, results.Max(r => r.BalanceAfter));
    }

    [Fact]
    public async Task A_grant_may_exceed_the_cap_and_regeneration_then_pauses()
    {
        var account = await NewWalletAsync(45, TimeSpan.FromMinutes(35));
        var above = await energy.GrantAsync(account, 10, Guid.CreateVersion7(), Op);
        Assert.Equal(new GrantResult(10, 58, 3, false), above); // 45 + 3 regenerated, then 10 past the cap of 50 (E6)

        // An hour later, regeneration is paused at or above the cap: nothing is gained and nothing is taken back.
        await AsReplicaAsync("update energy_wallets set regen_anchor = regen_anchor - interval '1 hour' where account_id = @account", new { account });
        var later = await energy.GrantAsync(account, 1, Guid.CreateVersion7(), Op);
        Assert.Equal(new GrantResult(1, 59, 0, false), later);
        Assert.Equal(["open", "regen", "dev_grant", "dev_grant"], (await EntriesAsync(account)).Select(e => e.Kind));
        var wallet = await WalletAsync(account);
        Assert.Equal((59, 0L), (wallet.Balance, wallet.ProgressMicros));
    }

    [Fact]
    public async Task A_grant_past_int_MaxValue_is_BalanceLimit_and_writes_nothing()
    {
        var account = await NewWalletAsync(int.MaxValue - 5, TimeSpan.FromMinutes(35)); // far above the cap: no regeneration
        var (row, entries) = (await WalletRowVersionAsync(account), await EntriesAsync(account));

        var e = await Assert.ThrowsAsync<EnergyException>(() => energy.GrantAsync(account, 6, Guid.CreateVersion7(), Op));

        Assert.Equal(EnergyError.BalanceLimit, e.Error);
        Assert.Equal(row, await WalletRowVersionAsync(account)); // the materialization (its anchor move) rolled back too
        Assert.Equal(entries, await EntriesAsync(account));
        Assert.Empty(await AuditsAsync("energy_grant"));
        Assert.Equal(new GrantResult(5, int.MaxValue, 0, false), await energy.GrantAsync(account, 5, Guid.CreateVersion7(), Op)); // exactly to the limit
    }

    [Fact]
    public async Task A_grant_above_the_configured_maximum_is_InvalidAmount()
    {
        var account = await NewWalletAsync(10, TimeSpan.Zero);
        var bounded = new EnergyAdminStore(db.AsApp(), grantMax: 100);
        var before = await StateAsync(account);
        foreach (var amount in new[] { 101, 0, -1, int.MaxValue, int.MinValue })
            Assert.Equal(EnergyError.InvalidAmount, (await Assert.ThrowsAsync<EnergyException>(() => bounded.GrantAsync(account, amount, Guid.CreateVersion7(), Op))).Error);
        await AssertUnchangedAsync(account, before);

        // Below 1 is refused before any connection: a store that cannot reach a database refuses the same way. The configured
        // maximum is checked only inside the transaction, after the key lookup (a replay is matched before it), so above it an
        // unreachable store fails to connect instead.
        await using var nowhere = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");
        var unreachable = new EnergyAdminStore(nowhere, grantMax: 100);
        foreach (var amount in new[] { 0, -1, int.MinValue })
            Assert.Equal(EnergyError.InvalidAmount, (await Assert.ThrowsAsync<EnergyException>(() => unreachable.GrantAsync(account, amount, Guid.CreateVersion7(), Op))).Error);
        foreach (var amount in new[] { 101, int.MaxValue })
            await Assert.ThrowsAsync<DatabaseUnavailableException>(() => unreachable.GrantAsync(account, amount, Guid.CreateVersion7(), Op));

        Assert.Equal(110, (await bounded.GrantAsync(account, 100, Guid.CreateVersion7(), Op)).BalanceAfter); // the maximum itself is allowed
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnergyAdminStore(db.AsApp(), grantMax: 0));
    }

    // ---- a lowered BLACKGLASS_ENERGY_GRANT_MAX: the key is matched before the limit for new grants ----

    [Fact]
    public async Task A_replay_after_the_maximum_is_lowered_returns_the_original_result()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var key = Guid.CreateVersion7();
        var first = await energy.GrantAsync(account, 500, key, Op); // under the maximum of 1000
        Assert.Equal(new GrantResult(500, 513, 3, false), first);
        // An hour later (a replay that materialized would rewrite the wallet's anchor), the operator lowers the maximum to 100.
        await AsReplicaAsync("update energy_wallets set regen_anchor = regen_anchor - interval '1 hour' where account_id = @account", new { account });
        var before = await StateAsync(account);

        var replay = await Lowered().GrantAsync(account, 500, key, OperatorContext.Create("retrier", "retry after lowering"));

        Assert.Equal(new GrantResult(500, first.BalanceAfter, 0, true), replay);
        await AssertUnchangedAsync(account, before); // wallet, ledger and audit records
        Assert.Single(await AuditsAsync("energy_grant"));
    }

    [Fact]
    public async Task A_new_grant_over_the_lowered_maximum_is_refused_and_writes_nothing()
    {
        // Regeneration is due: a grant that materialized before checking the maximum would leave a regen entry behind.
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var before = await StateAsync(account);
        var key = Guid.CreateVersion7();

        var e = await Assert.ThrowsAsync<EnergyException>(() => Lowered().GrantAsync(account, 101, key, Op));

        Assert.Equal(EnergyError.InvalidAmount, e.Error);
        Assert.Contains("1 to 100", e.Message);
        Assert.Contains("BLACKGLASS_ENERGY_GRANT_MAX", e.Message);
        await AssertUnchangedAsync(account, before); // not even materialized: the row version is the same
        Assert.Equal(new GrantResult(100, 113, 3, false), await Lowered().GrantAsync(account, 100, key, Op)); // the key was never recorded
    }

    [Fact]
    public async Task A_conflicting_reuse_of_the_original_key_is_refused_after_the_maximum_is_lowered()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var other = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var key = Guid.CreateVersion7();
        await energy.GrantAsync(account, 500, key, Op);
        await AsReplicaAsync("update energy_wallets set regen_anchor = regen_anchor - interval '1 hour' where account_id = @account", new { account });
        var (beforeAccount, beforeOther) = (await StateAsync(account), await StateAsync(other));
        var lowered = Lowered();

        // Another amount above and below the new maximum, and another account with the original and an in-limit amount: the key
        // is matched first, so each is IdempotencyKeyReused (never InvalidAmount).
        foreach (var (target, amount) in new[] { (account, 501), (account, 50), (account, 100), (other, 500), (other, 50) })
            Assert.Equal(EnergyError.IdempotencyKeyReused, (await Assert.ThrowsAsync<EnergyException>(() => lowered.GrantAsync(target, amount, key, Op))).Error);

        await AssertUnchangedAsync(account, beforeAccount);
        await AssertUnchangedAsync(other, beforeOther);
    }

    [Fact]
    public async Task Concurrent_replays_after_the_maximum_is_lowered_change_nothing()
    {
        const int Callers = 20;
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var key = Guid.CreateVersion7();
        var first = await energy.GrantAsync(account, 500, key, Op);
        await AsReplicaAsync("update energy_wallets set regen_anchor = regen_anchor - interval '1 hour' where account_id = @account", new { account });
        var before = await StateAsync(account);
        await using var wide = Wide(Callers + 1);
        var racing = Lowered(wide);

        // The start barrier: the account row is held, so all twenty reach its lock on distinct connections before any proceeds.
        await using (var holder = await db.AsApp().OpenConnectionAsync())
        {
            await using var holderTx = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select 1 from accounts where account_id = @account for update", new { account }, holderTx);
            var callers = Enumerable.Range(0, Callers).Select(i => Task.Run(() => racing.GrantAsync(account, 500, key, OperatorContext.Create("racer", $"replay {i}")))).ToArray();
            await WaitForLockWaitersAsync(Callers);
            await holderTx.CommitAsync();

            var results = await Task.WhenAll(callers);
            Assert.All(results, r => Assert.Equal(new GrantResult(500, first.BalanceAfter, 0, true), r));
        }

        await AssertUnchangedAsync(account, before);
    }

    [Fact]
    public async Task A_failure_during_a_new_grant_under_the_lowered_maximum_rolls_back()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var before = await StateAsync(account);
        await ExecuteAsSuperuserAsync(db,
            """
            create function refuse_grant_audit() returns trigger language plpgsql as $$ begin raise exception 'audit failed on purpose'; end $$;
            create trigger refuse_grant_audit before insert on operator_actions for each row when (new.action = 'energy_grant') execute function refuse_grant_audit();
            """);
        var key = Guid.CreateVersion7();

        var failure = await Assert.ThrowsAsync<PostgresException>(() => Lowered().GrantAsync(account, 50, key, Op)); // within the new maximum

        Assert.Contains("audit failed on purpose", failure.MessageText);
        await AssertUnchangedAsync(account, before); // the materialization, the credit and both entries rolled back
        await ExecuteAsSuperuserAsync(db, "drop trigger refuse_grant_audit on operator_actions; drop function refuse_grant_audit()");
        Assert.Equal(new GrantResult(50, 63, 3, false), await Lowered().GrantAsync(account, 50, key, Op)); // the retry grants once
        Assert.Equal(new GrantResult(50, 63, 0, true), await Lowered().GrantAsync(account, 50, key, Op)); // and then replays
        Assert.Equal(["open", "regen", "dev_grant"], (await EntriesAsync(account)).Select(e => e.Kind));
        Assert.Single(await AuditsAsync("energy_grant"));
    }

    [Fact]
    public async Task A_grant_without_a_wallet_is_NoWallet()
    {
        var account = await BareAccountAsync();
        var e = await Assert.ThrowsAsync<EnergyException>(() => energy.GrantAsync(account, 5, Guid.CreateVersion7(), Op));
        Assert.Equal(EnergyError.NoWallet, e.Error);
        Assert.Contains("energy open", e.Message);
        Assert.Equal(0L, await ScalarAsync<long>("select (select count(*) from energy_wallets) + (select count(*) from energy_ledger) + (select count(*) from operator_actions)"));
    }

    [Fact]
    public async Task A_grant_to_an_unknown_account_is_NotFound()
    {
        var e = await Assert.ThrowsAsync<AccountAdminException>(() => energy.GrantAsync(987654, 5, Guid.CreateVersion7(), Op));
        Assert.Equal(AccountAdminError.NotFound, e.Error);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from operator_actions"));
    }

    [Fact]
    public async Task A_unique_violation_from_another_constraint_propagates_as_is()
    {
        // Only a 23505 on energy_dev_grant_key_global means "the key was used elsewhere"; one from any other constraint (here
        // the per-account key constraint, raised by a fixture trigger) is a bug and propagates unmapped, with nothing written.
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var (row, entries) = (await WalletRowVersionAsync(account), await EntriesAsync(account));
        await ExecuteAsSuperuserAsync(db,
            """
            create function fake_duplicate() returns trigger language plpgsql as $$ begin
              raise exception 'duplicate on purpose' using errcode = 'unique_violation', constraint = 'energy_ledger_account_id_idempotency_key_key'; end $$;
            create trigger fake_duplicate before insert on energy_ledger for each row when (new.kind = 'dev_grant') execute function fake_duplicate();
            """);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from pg_constraint where conname = 'energy_ledger_account_id_idempotency_key_key'")); // a real name

        var e = await Assert.ThrowsAsync<PostgresException>(() => energy.GrantAsync(account, 5, Guid.CreateVersion7(), Op));

        Assert.Equal((PostgresErrorCodes.UniqueViolation, "energy_ledger_account_id_idempotency_key_key"), (e.SqlState, e.ConstraintName));
        Assert.Equal(DbFailureKind.Unexpected, DbFailure.Classify(e));
        Assert.Equal(row, await WalletRowVersionAsync(account));
        Assert.Equal(entries, await EntriesAsync(account));
        Assert.Empty(await AuditsAsync("energy_grant"));
        await ExecuteAsSuperuserAsync(db, "drop trigger fake_duplicate on energy_ledger; drop function fake_duplicate()");
    }

    [Fact]
    public async Task A_grant_to_a_suspended_account_is_allowed()
    {
        var issued = await accounts.CreateAccountAsync(null, null, Op);
        await accounts.SuspendAsync(issued.AccountId, Op);
        var result = await energy.GrantAsync(issued.AccountId, 5, Guid.CreateVersion7(), Op); // energy is property, not access
        Assert.Equal((55, false), (result.BalanceAfter, result.Replayed));
        Assert.Equal("suspended", await ScalarAsync<string>("select status from accounts where account_id = @a", new { a = issued.AccountId }));
    }

    // ---- failure injection and locks ----

    [Fact]
    public async Task A_failure_writing_the_audit_rolls_back_the_grant_and_its_regen_entry()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var (wallet, row) = (await WalletAsync(account), await WalletRowVersionAsync(account));
        await ExecuteAsSuperuserAsync(db,
            """
            create function refuse_grant_audit() returns trigger language plpgsql as $$ begin raise exception 'audit failed on purpose'; end $$;
            create trigger refuse_grant_audit before insert on operator_actions for each row when (new.action = 'energy_grant') execute function refuse_grant_audit();
            """);
        var key = Guid.CreateVersion7();

        var failure = await Assert.ThrowsAsync<PostgresException>(() => energy.GrantAsync(account, 5, key, Op));

        Assert.Contains("audit failed on purpose", failure.MessageText);
        Assert.Equal(wallet, await WalletAsync(account));
        Assert.Equal(row, await WalletRowVersionAsync(account));
        Assert.Equal([new Entry("open", 10, 10, "open")], await EntriesAsync(account));
        await ExecuteAsSuperuserAsync(db, "drop trigger refuse_grant_audit on operator_actions");
        Assert.Equal(new GrantResult(5, 18, 3, false), await energy.GrantAsync(account, 5, key, Op)); // the key was never used
    }

    [Fact]
    public async Task A_grant_waiting_on_a_held_wallet_lock_times_out_busy_and_writes_nothing()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var (row, entries) = (await WalletRowVersionAsync(account), await EntriesAsync(account));
        var bounded = new EnergyAdminStore(db.AsAppWith(Short), GrantMax);
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from energy_wallets where account_id = @account for update", new { account }, holderTx);

        var clock = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<PostgresException>(() => bounded.GrantAsync(account, 5, Guid.CreateVersion7(), Op).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the grant waited {clock.Elapsed}");
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
        Assert.Equal(DbFailureKind.Busy, DbFailure.Classify(e));

        // The CLI reports it as busy (exit 75), after its unlocked preview, and changes nothing.
        var env = db.EnvFor("app");
        env[DbTimeouts.LockTimeoutVar] = "300";
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Cli.RunAsync(["admin", "energy", "grant", "--account-base58", AccountId.Format(account), "--amount", "5", "--idempotency-key", Guid.CreateVersion7().ToString(),
            "--operator", "ops", "--reason", "busy", "--yes"], env, output, error).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ExitCodes.Busy, code);
        Assert.Contains("nothing was changed", error.ToString());
        Assert.Equal("10", Regex.Match(output.ToString(), "(?m)^target.energyBalance=(.*)$").Groups[1].Value.TrimEnd('\r'));
        Assert.DoesNotContain("correlationId=", output.ToString());
        await holderTx.RollbackAsync();

        Assert.Equal(row, await WalletRowVersionAsync(account));
        Assert.Equal(entries, await EntriesAsync(account));
        Assert.Empty(await AuditsAsync("energy_grant"));
    }

    [Fact]
    public async Task A_grant_whose_connection_is_terminated_mid_transaction_writes_nothing()
    {
        var account = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var (wallet, row) = (await WalletAsync(account), await WalletRowVersionAsync(account));
        var key = Guid.CreateVersion7();
        await InstallAuditGateAsync();
        await using (var gate = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var gateTx = await gate.BeginTransactionAsync();
            await gate.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = GateKey }, gateTx);
            // Held at its audit: the wallet is materialized and credited, and the regen and dev_grant entries are inserted.
            var grant = Task.Run(() => energy.GrantAsync(account, 5, key, Op));
            await WaitForLockWaitersAsync(1);
            Assert.True(await gate.ExecuteScalarAsync<bool>(
                "select pg_terminate_backend(pid) from pg_stat_activity where datname = current_database() and wait_event = 'advisory'", transaction: gateTx));
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => grant);
            await gateTx.RollbackAsync();
        }
        await RemoveAuditGateAsync();

        Assert.Equal(wallet, await WalletAsync(account));
        Assert.Equal(row, await WalletRowVersionAsync(account));
        Assert.Equal([new Entry("open", 10, 10, "open")], await EntriesAsync(account));
        Assert.Empty(await AuditsAsync("energy_grant"));
        Assert.Equal(new GrantResult(5, 18, 3, false), await energy.GrantAsync(account, 5, key, Op)); // the operator's retry with the key grants once
    }

    [Fact]
    public async Task Grants_opens_and_account_lifecycle_commands_on_one_account_never_deadlock()
    {
        const int Rounds = 4;
        var issued = await accounts.CreateAccountAsync("busy-one", null, Op);
        var account = issued.AccountId;
        await using var wide = Wide(Rounds * 7 + 2);
        var racingEnergy = new EnergyAdminStore(wide, GrantMax);
        var racingAccounts = new AccountAdminStore(wide);

        await using var holder = await db.AsApp().OpenConnectionAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from accounts where account_id = @account for update", new { account }, holderTx);
        var calls = new List<Task>();
        for (var i = 0; i < Rounds; i++)
        {
            calls.Add(Task.Run(() => racingEnergy.GrantAsync(account, 2, Guid.CreateVersion7(), Op)));
            calls.Add(Task.Run(() => racingEnergy.OpenAsync(account, Op)));
            calls.Add(Task.Run(() => racingAccounts.SuspendAsync(account, Op)));
            calls.Add(Task.Run(() => racingAccounts.ReactivateAsync(account, Op)));
            calls.Add(Task.Run(() => racingAccounts.BackfillStarterAsync(account, Op)));
            calls.Add(Task.Run(() => racingAccounts.RotateCredentialAsync(account, Op)));
        }
        await WaitForLockWaitersAsync(calls.Count);
        // Unrelated writers that share the policy lock: new accounts (row, inventories, shared lock, wallet) and a publication.
        for (var i = 0; i < Rounds; i++) calls.Add(Task.Run(() => racingAccounts.CreateAccountAsync(null, null, Op)));
        calls.Add(Task.Run(() => racingEnergy.PublishPolicyAsync(50, 50, TimeSpan.FromMinutes(10), 1, 100, Op)));
        await holderTx.CommitAsync();

        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(60)); // no deadlock (40P01), no failure, no hang
        Assert.Equal(Rounds, (await EntriesAsync(account)).Count(e => e.Kind == "dev_grant"));
        Assert.Equal(1 + Rounds, await ScalarAsync<long>("select count(*) from energy_wallets"));
        Assert.Equal(2, await ScalarAsync<int>("select max(version) from energy_policies"));
    }

    [Fact]
    public async Task A_queued_publication_drains_with_shared_holders_and_new_requesters_without_deadlock()
    {
        // The owner's question 1, built deterministically: shared holder H is in flight, publisher P is queued behind it, and
        // new shared requesters (a grant on another account, an account creation, a /v1/me read) queue behind P while holding
        // their own row locks. Then H needs more (its audit) and everything must drain. The pool has no lock_timeout, so a
        // real cycle surfaces as 40P01, not as a timeout.
        var held = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var other = await NewWalletAsync(10, TimeSpan.FromMinutes(35));
        var reader = await accounts.CreateAccountAsync(null, null, Op);
        await using var wide = Wide(8);
        var racingEnergy = new EnergyAdminStore(wide, GrantMax);
        var racingAccounts = new AccountAdminStore(wide);
        var reads = new PlayerReadStore(wide, new ParticipationLock());
        const string AdvisoryWaiters = "select count(*) from pg_stat_activity where datname = current_database() and wait_event = 'advisory'";
        const string SharedPolicyHolders =
            "select count(*) from pg_locks where locktype = 'advisory' and mode = 'ShareLock' and granted and database = (select oid from pg_database where datname = current_database())";

        await InstallAuditGateAsync();
        Task<GrantResult> h, r;
        Task<EnergyPolicy> p;
        Task<IssuedCredential> created;
        Task<MeView> me;
        await using (var gate = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var gateTx = await gate.BeginTransactionAsync();
            await gate.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = GateKey }, gateTx);

            // H: materialized and credited under the shared lock, its entries written, then held at its audit.
            h = Task.Run(() => racingEnergy.GrantAsync(held, 5, Guid.CreateVersion7(), Op));
            await WaitForLockWaitersAsync(1);
            Assert.Equal(1L, await ScalarAsync<long>(SharedPolicyHolders));

            // P: queued for the exclusive lock behind H.
            p = Task.Run(() => racingEnergy.PublishPolicyAsync(40, 40, TimeSpan.FromMinutes(5), 2, 100, Op));
            await WaitForLockWaitersAsync(2);
            Assert.Equal(2L, await ScalarAsync<long>(AdvisoryWaiters)); // H at the gate, P on the policy lock

            // New shared requesters: each queues behind P (a shared request never jumps a queued exclusive one).
            r = Task.Run(() => racingEnergy.GrantAsync(other, 3, Guid.CreateVersion7(), Op));
            created = Task.Run(() => racingAccounts.CreateAccountAsync(null, null, Op));
            me = Task.Run(() => reads.GetMeAsync(reader.AccountId));
            await WaitForLockWaitersAsync(5);
            Assert.Equal(5L, await ScalarAsync<long>(AdvisoryWaiters));
            Assert.Equal(1L, await ScalarAsync<long>(SharedPolicyHolders)); // still H alone
            Assert.False(p.IsCompleted || r.IsCompleted || created.IsCompleted || me.IsCompleted);

            await gateTx.CommitAsync();
            await Task.WhenAll(h, p, r, created, me).WaitAsync(TimeSpan.FromSeconds(60)); // no 40P01, no other failure, no hang
        }
        await RemoveAuditGateAsync();

        var policy = await p;
        Assert.Equal(2, policy.Version);
        // H read its clock before P was granted; its materialization moved its anchor to that clock and stayed on version 1.
        var heldWallet = await WalletAsync(held);
        Assert.Equal((false, 18, 1), ((await h).Replayed, (await h).BalanceAfter, heldWallet.PolicyVersion));
        Assert.True(policy.EffectiveAt > heldWallet.RegenAnchor, $"{policy.EffectiveAt:O} should be after H's clock {heldWallet.RegenAnchor:O}");
        // The requesters queued behind P all saw version 2: the grant's materialization lands on it, the new wallet opens
        // under it, and /v1/me projects with its cap.
        var otherWallet = await WalletAsync(other);
        Assert.Equal(2, otherWallet.PolicyVersion);
        Assert.True(otherWallet.RegenAnchor > policy.EffectiveAt);
        Assert.False((await r).Replayed);
        Assert.Equal(new WalletOpen(true, 40, 2), (await created).Energy);
        Assert.Equal(40, (await me).Energy!.Cap);
    }

    [Fact]
    public async Task Account_creation_behind_a_held_publication_lock_times_out_busy_and_writes_nothing()
    {
        string[] tables =
            ["accounts", "account_identities", "dev_credentials", "inventories", "operatives", "item_instances", "item_stacks", "energy_wallets", "energy_ledger", "operator_actions"];
        async Task<string> CountsAsync()
        {
            var counts = new List<string>();
            foreach (var table in tables) counts.Add($"{table}={await ScalarAsync<long>($"select count(*) from {table}")}");
            return string.Join(' ', counts);
        }
        await accounts.CreateAccountAsync("before", null, Op); // a starting point that is not empty
        var before = await CountsAsync();
        var bounded = new AccountAdminStore(db.AsAppWith(Short));
        var env = db.EnvFor("app");
        env[DbTimeouts.LockTimeoutVar] = "300";

        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var holderTx = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = EnergyPolicies.PublicationLockKey }, holderTx);

            // The store: the account, identity, credential and starter are written, then the wallet waits on the policy lock.
            var clock = Stopwatch.StartNew();
            var e = await Assert.ThrowsAsync<PostgresException>(() => bounded.CreateAccountAsync("blocked", "Blocked", Op).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the creation waited {clock.Elapsed}");
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
            Assert.Equal(DbFailureKind.Busy, DbFailure.Classify(e));
            Assert.Equal(before, await CountsAsync());

            // The CLI: busy (exit 75), and no key or account printed.
            var (output, error) = (new StringWriter(), new StringWriter());
            var code = await Cli.RunAsync(["admin", "account", "create", "--handle", "blocked-cli", "--operator", "ops", "--reason", "busy"], env, output, error)
                .WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(ExitCodes.Busy, code);
            Assert.Contains("nothing was changed", error.ToString());
            Assert.Equal("", output.ToString());
            Assert.Equal(before, await CountsAsync());
            await holderTx.RollbackAsync();
        }

        Assert.Equal(before, await CountsAsync());
        // Nothing was kept: both handles are still free once the lock is gone.
        Assert.Equal(new WalletOpen(true, 50, 1), (await bounded.CreateAccountAsync("blocked", "Blocked", Op)).Energy);
        Assert.NotNull((await bounded.CreateAccountAsync("blocked-cli", null, Op)).Energy);
    }

    [Fact]
    public async Task A_publication_behind_a_held_shared_lock_times_out_busy_and_publishes_nothing()
    {
        var bounded = new EnergyAdminStore(db.AsAppWith(Short), GrantMax);
        var env = db.EnvFor("app");
        env[DbTimeouts.LockTimeoutVar] = "300";

        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var holderTx = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select pg_advisory_xact_lock_shared(@key)", new { key = EnergyPolicies.PublicationLockKey }, holderTx);

            var clock = Stopwatch.StartNew();
            var e = await Assert.ThrowsAsync<PostgresException>(() =>
                bounded.PublishPolicyAsync(60, 60, TimeSpan.FromMinutes(10), 1, 100, Op).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the publication waited {clock.Elapsed}");
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
            Assert.Equal(DbFailureKind.Busy, DbFailure.Classify(e));

            // The CLI, confirmed: its unlocked preview, then busy (exit 75), with no version printed.
            var (output, error) = (new StringWriter(), new StringWriter());
            var code = await Cli.RunAsync(["admin", "energy", "policy", "publish", "--cap", "60", "--opening-balance", "60", "--interval-seconds", "600", "--amount", "1",
                "--void-refund-pct", "100", "--operator", "ops", "--reason", "busy", "--yes"], env, output, error).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(ExitCodes.Busy, code);
            Assert.Contains("nothing was changed", error.ToString());
            Assert.DoesNotContain("policyVersion=", output.ToString());
            Assert.DoesNotContain("correlationId=", output.ToString());
            await holderTx.RollbackAsync();
        }

        Assert.Equal(1, await ScalarAsync<int>("select max(version) from energy_policies"));
        Assert.Equal(1L, await ScalarAsync<long>("select last_value from energy_policies_version_seq")); // the insert was never reached
        Assert.Empty(await AuditsAsync("energy_policy_publish"));
    }

    // ---- opening and policies ----

    [Fact]
    public async Task Opening_an_account_without_a_wallet_opens_it_full_and_audits_it()
    {
        var account = await BareAccountAsync();
        var op = OperatorContext.Create("opener", "open test");
        Assert.Equal(new WalletOpen(true, 50, 1), await energy.OpenAsync(account, op));
        Assert.Equal([new Entry("open", 50, 50, "open")], await EntriesAsync(account));
        var audit = Assert.Single(await AuditsAsync("energy_open"));
        Assert.Equal((account, op.CorrelationId), (audit.AccountId!.Value, audit.CorrelationId));
        Assert.Equal(Keys("opened", "balance", "policyVersion"), Names(audit.Result));
        Assert.Equal((true, 50, 1), (audit.Result.GetProperty("opened").GetBoolean(), audit.Result.GetProperty("balance").GetInt32(), audit.Result.GetProperty("policyVersion").GetInt32()));
        Assert.Equal(AccountAdminError.NotFound, (await Assert.ThrowsAsync<AccountAdminException>(() => energy.OpenAsync(987654, Op))).Error);
    }

    [Fact]
    public async Task Concurrent_opens_of_one_account_open_one_wallet()
    {
        const int Callers = 10;
        var account = await BareAccountAsync();
        await using var wide = Wide(Callers + 1);
        var racing = new EnergyAdminStore(wide, GrantMax);

        await using var holder = await db.AsApp().OpenConnectionAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from accounts where account_id = @account for update", new { account }, holderTx);
        var callers = Enumerable.Range(0, Callers).Select(_ => Task.Run(() => racing.OpenAsync(account, Op))).ToArray();
        await WaitForLockWaitersAsync(Callers);
        await holderTx.CommitAsync();

        var results = await Task.WhenAll(callers);
        Assert.Single(results, r => r.Opened);
        Assert.All(results, r => Assert.Equal((50, 1), (r.Balance, r.PolicyVersion)));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from energy_wallets"));
        Assert.Equal([new Entry("open", 50, 50, "open")], await EntriesAsync(account));
        var audits = await AuditsAsync("energy_open");
        Assert.Equal(Callers, audits.Count);
        Assert.Single(audits, a => a.Result.GetProperty("opened").GetBoolean());
    }

    [Fact]
    public async Task Opening_an_existing_wallet_reports_already_and_changes_nothing()
    {
        var issued = await accounts.CreateAccountAsync(null, null, Op);
        var (wallet, row, entries) = (await WalletAsync(issued.AccountId), await WalletRowVersionAsync(issued.AccountId), await EntriesAsync(issued.AccountId));

        Assert.Equal(new WalletOpen(false, 50, 1), await energy.OpenAsync(issued.AccountId, Op));

        Assert.Equal(wallet, await WalletAsync(issued.AccountId));
        Assert.Equal(row, await WalletRowVersionAsync(issued.AccountId));
        Assert.Equal(entries, await EntriesAsync(issued.AccountId));
        // Task 3 M1: the numbers read for an existing wallet are advisory (unlocked), so the audit never records them as its state.
        var audit = Assert.Single(await AuditsAsync("energy_open"));
        Assert.Equal(Keys("opened", "advisory"), Names(audit.Result));
        Assert.False(audit.Result.GetProperty("opened").GetBoolean());
        Assert.Equal((50, 1), (audit.Result.GetProperty("advisory").GetProperty("balance").GetInt32(), audit.Result.GetProperty("advisory").GetProperty("policyVersion").GetInt32()));
    }

    [Fact]
    public async Task Publishing_a_policy_mints_the_next_version_with_a_database_effective_time_and_audits_it()
    {
        var first = await ScalarAsync<DateTime>("select effective_at from energy_policies where version = 1");
        var before = await ScalarAsync<DateTime>("select clock_timestamp()");
        var op = OperatorContext.Create("publisher", "publish test");

        var policy = await energy.PublishPolicyAsync(80, 40, TimeSpan.FromMinutes(5), 2, 50, op);

        var after = await ScalarAsync<DateTime>("select clock_timestamp()");
        Assert.Equal((2, 80, 40, TimeSpan.FromMinutes(5), 2, 50), (policy.Version, policy.Cap, policy.OpeningBalance, policy.RegenInterval, policy.RegenAmount, policy.VoidRefundPct));
        Assert.Equal(DateTimeKind.Utc, policy.EffectiveAt.Kind);
        Assert.InRange(policy.EffectiveAt, before, after);
        Assert.True(policy.EffectiveAt > first);
        Assert.Equal(policy.EffectiveAt, await ScalarAsync<DateTime>("select effective_at from energy_policies where version = 2"));

        var audit = Assert.Single(await AuditsAsync("energy_policy_publish"));
        Assert.Equal((null, op.CorrelationId, "publisher"), (audit.AccountId, audit.CorrelationId, audit.Operator));
        Assert.Equal(Keys("version", "effectiveAt", "cap", "openingBalance", "intervalSeconds", "amount", "voidRefundPct"), Names(audit.Result));
        Assert.Equal((2, 80, 40, 300L, 2, 50), (audit.Result.GetProperty("version").GetInt32(), audit.Result.GetProperty("cap").GetInt32(),
            audit.Result.GetProperty("openingBalance").GetInt32(), audit.Result.GetProperty("intervalSeconds").GetInt64(), audit.Result.GetProperty("amount").GetInt32(),
            audit.Result.GetProperty("voidRefundPct").GetInt32()));
        Assert.Equal(policy.EffectiveAt, audit.Result.GetProperty("effectiveAt").GetDateTime().ToUniversalTime());

        // The limits of every CHECK are accepted: 1 s and 7 days, an opening balance of 0 or the cap, refunds of 0 % and 100 %.
        Assert.Equal(3, (await energy.PublishPolicyAsync(1, 0, TimeSpan.FromSeconds(1), 1, 0, Op)).Version);
        Assert.Equal(4, (await energy.PublishPolicyAsync(int.MaxValue, int.MaxValue, TimeSpan.FromDays(7), int.MaxValue, 100, Op)).Version);
        Assert.Equal(new WalletOpen(true, int.MaxValue, 4), (await accounts.CreateAccountAsync(null, null, Op)).Energy); // the latest applies to new wallets
    }

    [Fact]
    public async Task A_publication_takes_the_exclusive_lock_and_waits_for_a_shared_holder()
    {
        await using var reader = await db.AsApp().OpenConnectionAsync();
        await using var readerTx = await reader.BeginTransactionAsync();
        await reader.ExecuteAsync("select pg_advisory_xact_lock_shared(@key)", new { key = EnergyPolicies.PublicationLockKey }, readerTx);

        var publication = Task.Run(() => energy.PublishPolicyAsync(60, 60, TimeSpan.FromMinutes(10), 1, 100, Op));
        await WaitForLockWaitersAsync();
        Assert.False(publication.IsCompleted);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event = 'advisory'"));
        var released = await reader.ExecuteScalarAsync<DateTime>("select clock_timestamp()", transaction: readerTx);
        await readerTx.CommitAsync();

        var policy = await publication;
        Assert.True(policy.EffectiveAt > released, $"{policy.EffectiveAt:O} should be after the release at {released:O}");
    }

    [Fact]
    public async Task A_lower_cap_never_reduces_a_balance()
    {
        var account = await NewWalletAsync(45, TimeSpan.FromMinutes(5));
        await energy.PublishPolicyAsync(20, 20, TimeSpan.FromMinutes(10), 1, 100, Op);

        var result = await energy.GrantAsync(account, 1, Guid.CreateVersion7(), Op);

        Assert.Equal(new GrantResult(1, 46, 0, false), result); // 45 is kept above the new cap of 20 (E2, E6), and the grant adds to it
        var wallet = await WalletAsync(account);
        Assert.Equal((46, 2, 0L), (wallet.Balance, wallet.PolicyVersion, wallet.ProgressMicros));
        Assert.Equal(["open", "dev_grant"], (await EntriesAsync(account)).Select(e => e.Kind));
    }

    [Theory]
    [InlineData(0, 0, 600_000, 1, 100)]          // cap > 0
    [InlineData(-5, 0, 600_000, 1, 100)]
    [InlineData(50, -1, 600_000, 1, 100)]        // 0 <= opening
    [InlineData(50, 51, 600_000, 1, 100)]        // opening <= cap
    [InlineData(50, 50, 0, 1, 100)]              // interval at least 1 s
    [InlineData(50, 50, -1000, 1, 100)]
    [InlineData(50, 50, 1_500, 1, 100)]          // whole seconds only
    [InlineData(50, 50, 999, 1, 100)]
    [InlineData(50, 50, 604_801_000, 1, 100)]    // at most 7 days
    [InlineData(50, 50, 600_000, 0, 100)]        // amount >= 1
    [InlineData(50, 50, 600_000, 1, -1)]         // refund 0..100
    [InlineData(50, 50, 600_000, 1, 101)]
    public async Task An_invalid_policy_is_refused_before_any_write(int cap, int opening, long intervalMs, int amount, int refund)
    {
        var interval = TimeSpan.FromMilliseconds(intervalMs);
        var e = await Assert.ThrowsAsync<EnergyException>(() => energy.PublishPolicyAsync(cap, opening, interval, amount, refund, Op));
        Assert.Equal(EnergyError.InvalidPolicy, e.Error);
        await using var nowhere = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");
        Assert.Equal(EnergyError.InvalidPolicy,
            (await Assert.ThrowsAsync<EnergyException>(() => new EnergyAdminStore(nowhere, GrantMax).PublishPolicyAsync(cap, opening, interval, amount, refund, Op))).Error);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from energy_policies"));
        Assert.Equal(1L, await ScalarAsync<long>("select last_value from energy_policies_version_seq")); // no insert was even attempted
        Assert.Empty(await AuditsAsync("energy_policy_publish"));
    }
}
