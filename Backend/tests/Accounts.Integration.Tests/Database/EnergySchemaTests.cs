using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Migration 0006 (Stage 6; owner rulings E1-E7, spec §15.1-§15.2) against a real PostgreSQL 17: every CHECK of
/// the versioned policies, the wallets, the append-only ledger and the anomalies; the ledger keys; the triggers (immutable
/// policies with a database-set, strictly increasing effective_at; an append-only ledger; a forward-only wallet; a wallet
/// whose balance must match its latest ledger entry at commit); the version-1 seed; the upgrade from Stage 5 (E3: no
/// wallet is opened); and the ledger invariant file.</summary>
[Trait("Requires", "Postgres")]
public class EnergySchemaTests : IAsyncLifetime
{
    TestDatabase db = null!;
    const string Operative = "00000000-0000-4000-8000-000000000001";
    const string Rifle = "item.rifle";
    const string Kit = "item.kit";
    const string Catalogue = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string InvariantFile = "0006_energy_ledger.sql";

    public async Task InitializeAsync()
    {
        db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    static async Task<PostgresException> FailureOf(NpgsqlDataSource ds, string sql, object? args = null)
    {
        await using var conn = await ds.OpenConnectionAsync();
        return await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, args));
    }

    async Task<T> SuperScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    static async Task<T> SingleAsync<T>(NpgsqlDataSource ds, string sql, object? args = null)
    {
        await using var conn = await ds.OpenConnectionAsync();
        return await conn.QuerySingleAsync<T>(sql, args);
    }

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    /// <summary>Opens a wallet as the app would (E1): the wallet and its <c>open</c> entry in one transaction.</summary>
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

    /// <summary>A development grant as the app would write it: the wallet and its entry in one transaction.</summary>
    Task GrantAsync(long account, int amount) => CreditAsync(account, "dev_grant", amount, "dev_grant:" + Guid.NewGuid());

    /// <summary>A credit of any kind as the app would write it: the wallet and its entry in one transaction.</summary>
    async Task CreditAsync(long account, string kind, int amount, string key, Guid? attempt = null)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var after = await conn.ExecuteScalarAsync<int>(
            "update energy_wallets set balance = balance + @amount where account_id = @account returning balance", new { account, amount }, tx);
        await conn.ExecuteAsync(
            "insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key) values (@account, @attempt, @kind, @amount, @after, @key)",
            new { account, attempt, kind, amount, after, key }, tx);
        await tx.CommitAsync();
    }

    /// <summary>A bare mission attempt (0007) for the attempt-bound kinds' entries to name.</summary>
    async Task<Guid> NewAttemptAsync(long account)
    {
        var attempt = Guid.CreateVersion7();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("""
            insert into mission_attempts (attempt_id, created_by, template_id, template_version, seed, retention_salt, assign_deadline)
            values (@attempt, @account, 'procedural-standard', 1, 1, decode(repeat('ab', 32), 'hex'), clock_timestamp() + interval '60 seconds')
            """, new { attempt, account });
        return attempt;
    }

    /// <summary>Runs <paramref name="sql"/> as the superuser in a transaction that is always rolled back, with the user
    /// triggers of <paramref name="table"/> disabled (so a CHECK is seen on its own, not behind the chain or progress
    /// triggers, which have tests of their own), optionally after dropping an overlapping CHECK (in the same transaction,
    /// so both come back), and returns the failure.</summary>
    async Task<PostgresException> RolledBackFailureAsync(string table, string sql, object? args, string? dropFirst = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync($"alter table {table} disable trigger user", transaction: tx);
        if (dropFirst is not null) await conn.ExecuteAsync($"alter table {table} drop constraint {dropFirst}", transaction: tx);
        var e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, args, tx));
        await tx.RollbackAsync();
        return e;
    }

    // ---- CHECKs, one per constraint (and per side of a range) ----

    const string InsertPolicy = "insert into energy_policies (cap, opening_balance, regen_interval, regen_amount, void_refund_pct) values ";

    [Theory]
    [InlineData("(0, 0, interval '10 minutes', 1, 100)", "energy_policies_cap_check")]
    [InlineData("(50, -1, interval '10 minutes', 1, 100)", "energy_policies_opening_balance_check")]
    [InlineData("(50, 50, interval '0 seconds', 1, 100)", "energy_policies_regen_interval_check")]
    [InlineData("(50, 50, interval '-1 minute', 1, 100)", "energy_policies_regen_interval_check")]
    [InlineData("(50, 50, interval '7 days 1 microsecond', 1, 100)", "energy_policies_regen_interval_check")]
    [InlineData("(50, 50, interval '1 month', 1, 100)", "energy_policies_regen_interval_check")]
    [InlineData("(50, 50, interval '1 month -23 days', 1, 100)", "energy_policies_regen_interval_check")]   // compares equal to 7 days, but carries a month
    [InlineData("(50, 50, interval '1 year -359 days', 1, 100)", "energy_policies_regen_interval_check")]    // one day, carried as a year
    [InlineData("(50, 50, interval '10 minutes', 0, 100)", "energy_policies_regen_amount_check")]
    [InlineData("(50, 50, interval '10 minutes', 1, -1)", "energy_policies_void_refund_pct_check")]
    [InlineData("(50, 50, interval '10 minutes', 1, 101)", "energy_policies_void_refund_pct_check")]
    [InlineData("(50, 51, interval '10 minutes', 1, 100)", "energy_policies_check")]
    public async Task A_policy_check_is_enforced(string values, string constraint)
    {
        var e = await FailureOf(db.AsApp(), InsertPolicy + values);
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
        Assert.Equal(1L, await SuperScalarAsync<long>("select count(*) from energy_policies"));
    }

    [Fact]
    public async Task The_policy_boundaries_are_accepted()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync(InsertPolicy + "(1, 0, interval '7 days', 1, 0)");
        await conn.ExecuteAsync(InsertPolicy + "(1, 1, interval '1 microsecond', 2147483647, 100)");
        Assert.Equal(3L, await conn.ExecuteScalarAsync<long>("select count(*) from energy_policies"));
    }

    [Theory]
    [InlineData("-1, 0", "energy_wallets_balance_check")]
    [InlineData("0, -1", "energy_wallets_regen_progress_us_check")]
    public async Task A_wallet_check_is_enforced(string balanceAndProgress, string constraint)
    {
        var account = await NewAccountAsync();
        var e = await RolledBackFailureAsync("energy_wallets",
            $"insert into energy_wallets (account_id, balance, regen_progress_us, regen_anchor, policy_version) values (@account, {balanceAndProgress}, clock_timestamp(), 1)",
            new { account });
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
    }

    const string InsertEntry = "insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@account, ";

    const string Uuid = "0f8fad5b-d9cb-469f-a165-70867728950e";

    [Theory]
    [InlineData("'adjust', -51, -1, 'adjust:a'", "energy_ledger_balance_after_check", null)]
    // Every kind: at most 200 characters, no whitespace or control character.
    [InlineData("'regen', 1, 51, 'regen:' || repeat('1', 195)", "energy_ledger_idempotency_key_check", null)]   // 201 characters
    [InlineData("'release', 1, 51, 'release:' || repeat('k', 193)", "energy_ledger_idempotency_key_check", null)]   // 201 characters
    [InlineData("'release', 1, 51, 'release:1 2'", "energy_ledger_idempotency_key_check", null)]                // whitespace
    [InlineData("'release', 1, 51, 'release:1' || chr(1)", "energy_ledger_idempotency_key_check", null)]        // a control character
    [InlineData("'regen', 1, 51, ''", "energy_ledger_idempotency_key_check", null)]
    // open: exactly 'open'.
    [InlineData("'open', 0, 50, 'open:x'", "energy_ledger_idempotency_key_check", null)]
    [InlineData("'open', 0, 50, 'opening'", "energy_ledger_idempotency_key_check", null)]
    // regen: 'regen:' and a signed decimal integer (the consumed anchor in microseconds).
    [InlineData("'regen', 1, 51, 'regen'", "energy_ledger_idempotency_key_check", null)]
    [InlineData("'regen', 1, 51, 'regen:'", "energy_ledger_idempotency_key_check", null)]
    [InlineData("'regen', 1, 51, 'regen:abc'", "energy_ledger_idempotency_key_check", null)]
    [InlineData("'regen', 1, 51, 'regen:1.5'", "energy_ledger_idempotency_key_check", null)]
    [InlineData("'regen', 1, 51, 'regen:+1'", "energy_ledger_idempotency_key_check", null)]
    [InlineData("'regen', 1, 51, 'regen:-'", "energy_ledger_idempotency_key_check", null)]
    [InlineData("'regen', 1, 51, 'regen1'", "energy_ledger_idempotency_key_check", null)]                       // no separator
    [InlineData("'regen', 1, 51, 'dev_grant:1'", "energy_ledger_idempotency_key_check", null)]                  // another kind's prefix
    // dev_grant: 'dev_grant:' and a canonical lower-case UUID.
    [InlineData("'dev_grant', 1, 51, 'dev_grant'", "energy_ledger_idempotency_key_check", null)]
    [InlineData("'dev_grant', 1, 51, 'dev_grant:x'", "energy_ledger_idempotency_key_check", null)]
    [InlineData("'dev_grant', 1, 51, 'dev_grant:0F8FAD5B-D9CB-469F-A165-70867728950E'", "energy_ledger_idempotency_key_check", null)]   // upper case
    [InlineData("'dev_grant', 1, 51, 'dev_grant:0f8fad5bd9cb469fa16570867728950e'", "energy_ledger_idempotency_key_check", null)]       // no hyphens
    [InlineData("'dev_grant', 1, 51, 'dev_grant:{0f8fad5b-d9cb-469f-a165-70867728950e}'", "energy_ledger_idempotency_key_check", null)] // braces
    [InlineData("'dev_grant', 1, 51, 'dev_grant:0f8fad5b-d9cb-469f-a165-70867728950e0'", "energy_ledger_idempotency_key_check", null)]  // too long
    [InlineData("'dev_grant', 1, 51, 'devXgrant:0f8fad5b-d9cb-469f-a165-70867728950e'", "energy_ledger_idempotency_key_check", null)]   // '_' is not a wildcard
    // The later kinds: the bare kind, or the kind, ':' and a non-empty suffix.
    [InlineData("'void_refund', 1, 51, 'voidXrefund:1'", "energy_ledger_idempotency_key_check", null)]          // '_' is not a wildcard
    [InlineData("'release', 1, 51, 'release:'", "energy_ledger_idempotency_key_check", null)]                   // an empty suffix
    [InlineData("'release', 1, 51, 'released'", "energy_ledger_idempotency_key_check", null)]
    // Every kind outside the list also fails the sign CHECK (which names the kinds); dropping it in the rolled-back
    // transaction shows the kind CHECK on its own.
    [InlineData("'bonus', 1, 51, 'bonus'", "energy_ledger_kind_check", "energy_ledger_delta_sign_check")]
    [InlineData("'open', -1, 49, 'open'", "energy_ledger_delta_sign_check", null)]
    [InlineData("'regen', 0, 50, 'regen:1'", "energy_ledger_delta_sign_check", null)]
    [InlineData("'dev_grant', 0, 50, 'dev_grant:" + Uuid + "'", "energy_ledger_delta_sign_check", null)]
    [InlineData("'release', 0, 50, 'release:x'", "energy_ledger_delta_sign_check", null)]
    [InlineData("'void_refund', -1, 49, 'void_refund:x'", "energy_ledger_delta_sign_check", null)]
    [InlineData("'reserve', 0, 50, 'reserve:x'", "energy_ledger_delta_sign_check", null)]
    [InlineData("'reserve', 1, 51, 'reserve:x'", "energy_ledger_delta_sign_check", null)]
    [InlineData("'charge', 1, 51, 'charge:x'", "energy_ledger_delta_sign_check", null)]
    [InlineData("'charge', -1, 49, 'charge:x'", "energy_ledger_delta_sign_check", null)]
    [InlineData("'settle', 1, 51, 'settle:x'", "energy_ledger_delta_sign_check", null)]
    [InlineData("'adjust', 0, 50, 'adjust:x'", "energy_ledger_delta_sign_check", null)]
    public async Task A_ledger_check_is_enforced(string values, string constraint, string? dropFirst)
    {
        var account = await NewWalletAsync();
        var e = await RolledBackFailureAsync("energy_ledger", InsertEntry + values + ")", new { account }, dropFirst);
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
        Assert.Equal(1L, await SuperScalarAsync<long>("select count(*) from energy_ledger"));
    }

    /// <summary>Each kind with a delta of its sign and a key of its form commits as a real write: the wallet moves by the
    /// delta and the entry extends the chain. "MAX" is the longest (200-character) key of that kind's form.
    /// Since 0007 the attempt-bound kinds (charge, settle, void_refund here) name an existing attempt; reserve and release,
    /// whose keys 0007 ties to their attempt and whose holds its invariants check, are covered by AttemptSchemaTests.</summary>
    [Theory]
    [InlineData("open", 0, "open")]
    [InlineData("open", 50, "open")]
    [InlineData("regen", 1, "regen:1700000000000000")]
    [InlineData("regen", 1, "regen:-1")]
    [InlineData("regen", 1, "MAX")]
    [InlineData("dev_grant", 1, "dev_grant:" + Uuid)]
    [InlineData("void_refund", 1, "void_refund:a")]
    [InlineData("void_refund", 1, "MAX")]
    [InlineData("charge", 0, "charge:a")]
    [InlineData("settle", 0, "settle:a")]
    [InlineData("adjust", -1, "adjust:a")]
    [InlineData("adjust", 1, "adjust")]
    public async Task Each_kind_accepts_a_delta_of_its_sign(string kind, int delta, string key)
    {
        if (key == "MAX") key = kind + ":" + new string(kind == "regen" ? '7' : 'k', 199 - kind.Length);
        Assert.True(key.Length <= 200);
        long account;
        if (kind == "open")
            account = await NewWalletAsync(delta);
        else
        {
            account = await NewWalletAsync();
            var attempt = kind is "void_refund" or "charge" or "settle" ? await NewAttemptAsync(account) : (Guid?)null;
            await CreditAsync(account, kind, delta, key, attempt);
        }
        Assert.Equal((kind == "open" ? 1L : 2L, (kind == "open" ? 0 : 50) + delta), await SingleAsync<(long, int)>(db.AsSuperuser(),
            "select (select count(*) from energy_ledger where account_id = @account), (select balance from energy_wallets where account_id = @account)", new { account }));
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
    }

    [Theory]
    [InlineData("''", "'d'", "anomaly_events_kind_check")]
    [InlineData("'Bad_kind'", "'d'", "anomaly_events_kind_check")]
    [InlineData("'bad-kind'", "'d'", "anomaly_events_kind_check")]
    [InlineData("repeat('k', 101)", "'d'", "anomaly_events_kind_check")]
    [InlineData("'k'", "''", "anomaly_events_dedupe_key_check")]
    [InlineData("'k'", "repeat('d', 501)", "anomaly_events_dedupe_key_check")]
    public async Task An_anomaly_check_is_enforced(string kind, string dedupeKey, string constraint)
    {
        var e = await FailureOf(db.AsApp(), $"insert into anomaly_events (kind, dedupe_key, detail) values ({kind}, {dedupeKey}, '{{}}')");
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync($"insert into anomaly_events (kind, dedupe_key, detail) values (repeat('k', 100), repeat('d', 500), '{{}}')");   // the longest are accepted
    }

    // ---- keys ----

    [Fact]
    public async Task A_duplicate_ledger_key_fails_on_the_unique_constraint()
    {
        var account = await NewWalletAsync();
        await CreditAsync(account, "regen", 1, "regen:1");
        var e = await FailureOf(db.AsApp(), InsertEntry + "'regen', 2, 53, 'regen:1')", new { account });   // a correct chain, a reused key
        Assert.Equal(("23505", "energy_ledger_account_id_idempotency_key_key"), (e.SqlState, e.ConstraintName));
        // open is once per account: the chain refuses a second one first, and the unique key would too.
        e = await FailureOf(db.AsApp(), InsertEntry + "'open', 0, 51, 'open')", new { account });
        Assert.Equal(("23514", "energy_ledger_chain"), (e.SqlState, e.ConstraintName));
        e = await RolledBackFailureAsync("energy_ledger", InsertEntry + "'open', 0, 51, 'open')", new { account });
        Assert.Equal(("23505", "energy_ledger_account_id_idempotency_key_key"), (e.SqlState, e.ConstraintName));
        // Another account may use the same regen key.
        var other = await NewWalletAsync();
        await CreditAsync(other, "regen", 1, "regen:1");
    }

    [Fact]
    public async Task A_dev_grant_key_is_unique_across_accounts()
    {
        var first = await NewWalletAsync();
        var second = await NewWalletAsync();
        var key = "dev_grant:" + Guid.NewGuid();
        await CreditAsync(first, "dev_grant", 1, key);
        var e = await FailureOf(db.AsApp(), InsertEntry + "'dev_grant', 1, 51, @key)", new { account = second, key });
        Assert.Equal(("23505", "energy_dev_grant_key_global"), (e.SqlState, e.ConstraintName));
        Assert.Equal(1L, await SuperScalarAsync<long>("select count(*) from energy_ledger where kind = 'dev_grant'"));
    }

    // ---- triggers ----

    [Fact]
    public async Task Ledger_rows_cannot_be_updated_or_deleted_even_by_the_owner()
    {
        var account = await NewWalletAsync();
        foreach (var ds in new[] { db.AsMigrator(), db.AsSuperuser() })
        {
            foreach (var sql in new[]
            {
                "update energy_ledger set delta = 49, balance_after = 49 where account_id = @account",
                "update energy_ledger set created_at = created_at where account_id = @account",   // even a no-op
                "delete from energy_ledger where account_id = @account",
            })
            {
                var e = await FailureOf(ds, sql, new { account });
                Assert.Equal(("23514", "energy_ledger_is_append_only"), (e.SqlState, e.ConstraintName));
            }
        }
        Assert.Equal((50, 50), await SingleAsync<(int, int)>(db.AsSuperuser(),
            "select delta, balance_after from energy_ledger where account_id = @account", new { account }));
    }

    [Fact]
    public async Task Policies_cannot_be_updated_or_deleted()
    {
        foreach (var ds in new[] { db.AsMigrator(), db.AsSuperuser() })
        {
            foreach (var sql in new[]
            {
                "update energy_policies set cap = 60, opening_balance = 60",
                "update energy_policies set effective_at = effective_at - interval '1 day'",
                "delete from energy_policies",
            })
            {
                var e = await FailureOf(ds, sql);
                Assert.Equal(("23514", "energy_policy_is_immutable"), (e.SqlState, e.ConstraintName));
            }
        }
        Assert.Equal(50, await SuperScalarAsync<int>("select cap from energy_policies where version = 1"));
    }

    [Fact]
    public async Task Effective_at_is_set_by_the_database_and_strictly_increases()
    {
        var v1 = await SuperScalarAsync<DateTime>("select effective_at from energy_policies where version = 1");

        // An explicit value from the caller (the owner can name the column; the app can't) is overridden, past or future.
        DateTime before, past, future;
        await using (var conn = await db.AsMigrator().OpenConnectionAsync())
        {
            before = await conn.ExecuteScalarAsync<DateTime>("select clock_timestamp()");
            past = await conn.ExecuteScalarAsync<DateTime>(
                "insert into energy_policies (cap, opening_balance, regen_interval, regen_amount, void_refund_pct, effective_at) values (50, 50, interval '10 minutes', 1, 100, '2000-01-01') returning effective_at");
            future = await conn.ExecuteScalarAsync<DateTime>(
                "insert into energy_policies (cap, opening_balance, regen_interval, regen_amount, void_refund_pct, effective_at) values (50, 50, interval '10 minutes', 1, 100, '2999-01-01') returning effective_at");
        }
        Assert.True(past >= before && past > v1, $"{past:O} should be at or after {before:O} and after {v1:O}");
        Assert.True(future > past && future.Year < 2999, $"{future:O} should follow {past:O} and not be the caller's 2999");

        // The app supplies no time at all.
        DateTime fromApp;
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            fromApp = await conn.ExecuteScalarAsync<DateTime>(InsertPolicy + "(50, 50, interval '10 minutes', 1, 100) returning effective_at");
        Assert.True(fromApp > future);

        // With the latest effective_at ahead of the clock (written ahead, or the clock stepped back), the next is exactly 1 µs later.
        await using (var conn = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
            await conn.ExecuteAsync("update energy_policies set effective_at = clock_timestamp() + interval '1 hour' where version = 4", transaction: tx);
            await tx.CommitAsync();
        }
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync(InsertPolicy + "(50, 50, interval '10 minutes', 1, 100)");
        Assert.True(await SuperScalarAsync<bool>(
            "select (select effective_at from energy_policies where version = 5) - (select effective_at from energy_policies where version = 4) = interval '1 microsecond'"));
        Assert.True(await SuperScalarAsync<bool>(
            "select bool_and(effective_at > prev) from (select effective_at, lag(effective_at) over (order by version) as prev from energy_policies) p where prev is not null"));
    }

    [Fact]
    public async Task The_anchor_and_policy_version_never_move_back()
    {
        var account = await NewWalletAsync();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync(InsertPolicy + "(60, 60, interval '5 minutes', 1, 100)");
            // Forward, or unchanged, is fine (no balance change, so no entry is needed).
            await conn.ExecuteAsync("update energy_wallets set regen_anchor = regen_anchor where account_id = @account", new { account });
            await conn.ExecuteAsync("update energy_wallets set regen_anchor = regen_anchor + interval '1 microsecond', policy_version = 2 where account_id = @account", new { account });
        }

        foreach (var sql in new[]
        {
            "update energy_wallets set regen_anchor = regen_anchor - interval '1 microsecond' where account_id = @account",
            "update energy_wallets set policy_version = 1 where account_id = @account",
            "update energy_wallets set regen_anchor = regen_anchor + interval '1 hour', policy_version = 1 where account_id = @account",
        })
        {
            var e = await FailureOf(db.AsApp(), sql, new { account });
            Assert.Equal(("23514", "energy_wallet_forward_only"), (e.SqlState, e.ConstraintName));
        }
        // The owner and the superuser are held to it too.
        var owner = await FailureOf(db.AsMigrator(), "update energy_wallets set policy_version = 1 where account_id = @account", new { account });
        Assert.Equal("energy_wallet_forward_only", owner.ConstraintName);
        Assert.Equal(2, await SuperScalarAsync<int>("select policy_version from energy_wallets where account_id = @account", new { account }));
    }

    [Fact]
    public async Task A_wallet_balance_without_its_ledger_entry_fails_at_commit()
    {
        // A new wallet with no entry.
        var bare = await NewAccountAsync();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@bare, 50, clock_timestamp(), 0, 1)", new { bare }, tx);
            var e = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
            Assert.Equal(("23514", "energy_wallet_matches_ledger"), (e.SqlState, e.ConstraintName));
        }
        Assert.Equal(0L, await SuperScalarAsync<long>("select count(*) from energy_wallets where account_id = @bare", new { bare }));

        // A balance change with no entry, or with an entry that disagrees.
        var account = await NewWalletAsync();
        foreach (var entry in new[] { null, InsertEntry + "'dev_grant', 4, 54, @key)" })   // the entry's chain is right; the wallet isn't
        {
            await using var conn = await db.AsApp().OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("update energy_wallets set balance = 55 where account_id = @account", new { account }, tx);
            if (entry is not null) await conn.ExecuteAsync(entry, new { account, key = "dev_grant:" + Guid.NewGuid() }, tx);
            var e = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
            Assert.Equal(("23514", "energy_wallet_matches_ledger"), (e.SqlState, e.ConstraintName));
        }
        Assert.Equal((50, 1L), await SingleAsync<(int, long)>(db.AsSuperuser(),
            "select (select balance from energy_wallets where account_id = @account), (select count(*) from energy_ledger where account_id = @account)", new { account }));

        // Several updates in one transaction are judged on the final row: an intermediate mismatch is fine.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("update energy_wallets set balance = 99 where account_id = @account", new { account }, tx);
            await conn.ExecuteAsync("update energy_wallets set balance = 55 where account_id = @account", new { account }, tx);
            await conn.ExecuteAsync(InsertEntry + "'dev_grant', 5, 55, @key)", new { account, key = "dev_grant:" + Guid.NewGuid() }, tx);
            await tx.CommitAsync();
        }
        // ... and a final mismatch after an intermediate match is not.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("update energy_wallets set balance = 60 where account_id = @account", new { account }, tx);
            await conn.ExecuteAsync(InsertEntry + "'dev_grant', 5, 60, @key)", new { account, key = "dev_grant:" + Guid.NewGuid() }, tx);
            await conn.ExecuteAsync("update energy_wallets set balance = 61 where account_id = @account", new { account }, tx);
            var e = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
            Assert.Equal("energy_wallet_matches_ledger", e.ConstraintName);
        }
        Assert.Equal(55, await SuperScalarAsync<int>("select balance from energy_wallets where account_id = @account", new { account }));
    }

    [Fact]
    public async Task A_ledger_entry_without_its_wallet_balance_fails_at_commit()
    {
        // An entry whose balance_after the wallet never reaches, with the wallet untouched.
        var account = await NewWalletAsync();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(InsertEntry + "'dev_grant', 5, 55, @key)", new { account, key = "dev_grant:" + Guid.NewGuid() }, tx);
            var e = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
            Assert.Equal(("23514", "energy_ledger_matches_wallet"), (e.SqlState, e.ConstraintName));
        }
        // The entry first, then a wallet update that disagrees: the entry's check (queued first) refuses it.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(InsertEntry + "'dev_grant', 5, 55, @key)", new { account, key = "dev_grant:" + Guid.NewGuid() }, tx);
            await conn.ExecuteAsync("update energy_wallets set balance = 56 where account_id = @account", new { account }, tx);
            var e = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
            Assert.Equal(("23514", "energy_ledger_matches_wallet"), (e.SqlState, e.ConstraintName));
        }
        // An autocommitted entry alone fails the same way.
        var alone = await FailureOf(db.AsApp(), InsertEntry + "'regen', 1, 51, 'regen:1')", new { account });
        Assert.Equal(("23514", "energy_ledger_matches_wallet"), (alone.SqlState, alone.ConstraintName));
        Assert.Equal((50, 1L), await SingleAsync<(int, long)>(db.AsSuperuser(),
            "select (select balance from energy_wallets where account_id = @account), (select count(*) from energy_ledger where account_id = @account)", new { account }));

        // In either order, an entry and the wallet reaching its balance_after commit together.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(InsertEntry + "'dev_grant', 5, 55, @key)", new { account, key = "dev_grant:" + Guid.NewGuid() }, tx);
            await conn.ExecuteAsync("update energy_wallets set balance = 55 where account_id = @account", new { account }, tx);
            await tx.CommitAsync();
        }
        Assert.Equal(55, await SuperScalarAsync<int>("select balance from energy_wallets where account_id = @account", new { account }));
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
    }

    /// <summary>Each refused insert is refused at the statement (the chain trigger is immediate), in a transaction whose
    /// wallet write alone would pass the commit check.</summary>
    async Task<PostgresException> ChainFailureAsync(long account, int walletBalance, string entry, object? args = null)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("update energy_wallets set balance = @walletBalance where account_id = @account", new { account, walletBalance }, tx);
        return await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(InsertEntry + entry, args ?? new { account }, tx));
    }

    [Fact]
    public async Task A_ledger_entry_must_extend_its_account_chain()
    {
        var account = await NewWalletAsync();   // open 50

        // A wrong delta with a correct balance_after (the wallet and balance_after agree at 60, but 50 + 7 is 57).
        var e = await ChainFailureAsync(account, 60, "'dev_grant', 7, 60, @key)", new { account, key = "dev_grant:" + Guid.NewGuid() });
        Assert.Equal(("23514", "energy_ledger_chain"), (e.SqlState, e.ConstraintName));
        // A delta-0 charge masking a balance change.
        e = await ChainFailureAsync(account, 40, "'charge', 0, 40, 'charge:a')");
        Assert.Equal(("23514", "energy_ledger_chain"), (e.SqlState, e.ConstraintName));
        // The review's scenario: regeneration materialized (+3, to 53), then a grant computed from the stale balance (50 + 10).
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("update energy_wallets set balance = 53 where account_id = @account", new { account }, tx);
            await conn.ExecuteAsync(InsertEntry + "'regen', 3, 53, 'regen:1')", new { account }, tx);
            await conn.ExecuteAsync("update energy_wallets set balance = 60 where account_id = @account", new { account }, tx);
            e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(InsertEntry + "'dev_grant', 10, 60, @key)", new { account, key = "dev_grant:" + Guid.NewGuid() }, tx));
            Assert.Equal(("23514", "energy_ledger_chain"), (e.SqlState, e.ConstraintName));
        }
        // An open entry that is not the first.
        e = await ChainFailureAsync(account, 50, "'open', 0, 50, 'open')");
        Assert.Equal(("23514", "energy_ledger_chain"), (e.SqlState, e.ConstraintName));
        // A first entry that is not an open.
        var fresh = await NewAccountAsync();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@fresh, 10, clock_timestamp(), 0, 1)", new { fresh }, tx);
            e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(InsertEntry + "'dev_grant', 10, 10, @key)", new { account = fresh, key = "dev_grant:" + Guid.NewGuid() }, tx));
            Assert.Equal(("23514", "energy_ledger_chain"), (e.SqlState, e.ConstraintName));
        }
        // An open whose balance_after isn't its delta.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@fresh, 50, clock_timestamp(), 0, 1)", new { fresh }, tx);
            e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(InsertEntry + "'open', 40, 50, 'open')", new { account = fresh }, tx));
            Assert.Equal(("23514", "energy_ledger_chain"), (e.SqlState, e.ConstraintName));
        }

        Assert.Equal((50, 1L), await SingleAsync<(int, long)>(db.AsSuperuser(),
            "select (select balance from energy_wallets where account_id = @account), (select count(*) from energy_ledger)", new { account }));
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
    }

    [Fact]
    public async Task The_grant_shape_commits_in_one_transaction()
    {
        // Task 4's grant: materialize (wallet update, regen entry), then credit (wallet update, dev_grant entry).
        var account = await NewWalletAsync(30);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            Assert.Equal(30, await conn.ExecuteScalarAsync<int>("select balance from energy_wallets where account_id = @account for update", new { account }, tx));
            var consumed = await conn.ExecuteScalarAsync<long>(
                "select (extract(epoch from regen_anchor) * 1000000)::bigint from energy_wallets where account_id = @account", new { account }, tx);
            await conn.ExecuteAsync("update energy_wallets set balance = 33, regen_anchor = clock_timestamp(), regen_progress_us = 1234 where account_id = @account", new { account }, tx);
            await conn.ExecuteAsync(InsertEntry + "'regen', 3, 33, @key)", new { account, key = $"regen:{consumed}" }, tx);
            await conn.ExecuteAsync("update energy_wallets set balance = 43 where account_id = @account", new { account }, tx);
            await conn.ExecuteAsync(InsertEntry + "'dev_grant', 10, 43, @key)", new { account, key = "dev_grant:" + Guid.NewGuid() }, tx);
            await tx.CommitAsync();
        }
        Assert.Equal((43, 3L), await SingleAsync<(int, long)>(db.AsSuperuser(),
            "select (select balance from energy_wallets where account_id = @account), (select count(*) from energy_ledger where account_id = @account)", new { account }));
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
    }

    [Fact]
    public async Task Progress_must_stay_below_its_policy_interval()
    {
        // Version 1 runs at 10 minutes: 600,000,000 µs.
        var account = await NewAccountAsync();
        const string Insert = "insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@account, 50, clock_timestamp(), @progress, 1)";
        var e = await FailureOf(db.AsApp(), Insert, new { account, progress = 600_000_000L });
        Assert.Equal(("23514", "energy_wallet_progress_below_interval"), (e.SqlState, e.ConstraintName));

        account = await NewWalletAsync();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("update energy_wallets set regen_progress_us = 599999999 where account_id = @account", new { account });
            await conn.ExecuteAsync(InsertPolicy + "(50, 50, interval '5 minutes', 1, 100)");
        }
        foreach (var sql in new[]
        {
            "update energy_wallets set regen_progress_us = 600000000 where account_id = @account",
            "update energy_wallets set regen_progress_us = 9223372036854775807 where account_id = @account",
            // Switching to the 5-minute version without rescaling leaves the progress out of range.
            "update energy_wallets set policy_version = 2 where account_id = @account",
        })
        {
            e = await FailureOf(db.AsApp(), sql, new { account });
            Assert.Equal(("23514", "energy_wallet_progress_below_interval"), (e.SqlState, e.ConstraintName));
        }
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("update energy_wallets set policy_version = 2, regen_progress_us = 299999999 where account_id = @account", new { account });
        Assert.Equal((2, 299999999L), await SingleAsync<(int, long)>(db.AsSuperuser(),
            "select policy_version, regen_progress_us from energy_wallets where account_id = @account", new { account }));
    }

    [Fact]
    public async Task Neither_the_ledger_nor_the_policies_can_be_truncated_even_by_the_owner()
    {
        var account = await NewWalletAsync();
        foreach (var (sql, constraint) in new[]
        {
            ("truncate energy_ledger", "energy_ledger_is_append_only"),
            ("truncate energy_wallets cascade", "energy_ledger_is_append_only"),   // the cascade reaches the ledger
            ("truncate energy_policies cascade", "energy_policy_is_immutable"),
        })
        {
            var e = await FailureOf(db.AsMigrator(), sql);
            Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
        }
        Assert.Equal((1L, 1L, 1L), await SingleAsync<(long, long, long)>(db.AsSuperuser(),
            "select (select count(*) from energy_ledger), (select count(*) from energy_wallets), (select count(*) from energy_policies)"));
        Assert.Equal(account, await SuperScalarAsync<long>("select account_id from energy_wallets"));
    }

    [Fact]
    public async Task Inserting_a_policy_waits_for_a_shared_holder_of_the_publication_lock()
    {
        // The effective_at trigger takes the publication lock itself (0x426C6B456E506F6C, EnergyPolicies.PublicationLockKey),
        // so even a direct insert waits for every in-flight materialization and takes effect after its clock.
        const long PublicationLockKey = 4786318449935675244;
        await using var reader = await db.AsApp().OpenConnectionAsync();
        await using var readerTx = await reader.BeginTransactionAsync();
        await reader.ExecuteAsync("select pg_advisory_xact_lock_shared(@PublicationLockKey)", new { PublicationLockKey }, readerTx);

        var insert = Task.Run(async () =>
        {
            await using var conn = await db.AsApp().OpenConnectionAsync();
            return await conn.ExecuteScalarAsync<DateTime>(InsertPolicy + "(60, 60, interval '5 minutes', 1, 100) returning effective_at");
        });
        await AccountTestKit.WaitUntilABackendWaitsOnALockAsync(db);
        Assert.False(insert.IsCompleted);
        Assert.Equal(1L, await SuperScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event = 'advisory'"));

        var readerNow = await reader.ExecuteScalarAsync<DateTime>("select clock_timestamp()", transaction: readerTx);
        await readerTx.CommitAsync();
        var effectiveAt = await insert;
        Assert.True(effectiveAt > readerNow, $"{effectiveAt:O} should be after the reader's clock {readerNow:O}");
    }

    // ---- seed ----

    [Fact]
    public async Task Version_1_is_seeded_with_the_provisional_values()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        var v1 = await conn.QuerySingleAsync<(int Version, int Cap, int Opening, TimeSpan Interval, int Amount, int Refund, bool Effective)>(
            "select version, cap, opening_balance, regen_interval, regen_amount, void_refund_pct, effective_at <= clock_timestamp() from energy_policies");
        Assert.Equal((1, 50, 50, TimeSpan.FromMinutes(10), 1, 100, true), v1);
        // The identity continues after the seed.
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>(InsertPolicy + "(50, 50, interval '10 minutes', 1, 100) returning version"));
    }

    // ---- anomalies ----

    [Fact]
    public async Task Only_one_outstanding_anomaly_per_kind_and_dedupe_key()
    {
        var account = await NewAccountAsync();
        const string Insert = "insert into anomaly_events (kind, account_id, dedupe_key, detail) values (@kind, @account, @dedupe, '{}')";
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync(Insert, new { kind = "energy_balance_mismatch", account, dedupe = "a" });
        var e = await FailureOf(db.AsApp(), Insert, new { kind = "energy_balance_mismatch", account, dedupe = "a" });
        Assert.Equal(("23505", "anomaly_one_outstanding"), (e.SqlState, e.ConstraintName));

        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            // The reconciliation's path: an identical outstanding finding writes nothing.
            Assert.Equal(0, await conn.ExecuteAsync(Insert + " on conflict do nothing", new { kind = "energy_balance_mismatch", account, dedupe = "a" }));
            // Another dedupe key or another kind is another finding.
            await conn.ExecuteAsync(Insert, new { kind = "energy_balance_mismatch", account, dedupe = "b" });
            await conn.ExecuteAsync(Insert, new { kind = "energy_chain_break", account, dedupe = "a" });
        }

        // Once reviewed, the same finding may be recorded again.
        await using (var conn = await db.AsSuperuser().OpenConnectionAsync())
            await conn.ExecuteAsync("update anomaly_events set reviewed_at = now(), reviewed_note = 'checked' where kind = 'energy_balance_mismatch' and dedupe_key = 'a'");
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            Assert.Equal(1, await conn.ExecuteAsync(Insert + " on conflict do nothing", new { kind = "energy_balance_mismatch", account, dedupe = "a" }));
        Assert.Equal(4L, await SuperScalarAsync<long>("select count(*) from anomaly_events"));
    }

    // ---- audit ----

    [Theory]
    [InlineData("energy_open")]
    [InlineData("energy_grant")]
    [InlineData("energy_policy_publish")]
    [InlineData("account_create")]
    [InlineData("credential_rotate")]
    [InlineData("credential_revoke")]
    [InlineData("sessions_revoke")]
    [InlineData("account_suspend")]
    [InlineData("account_reactivate")]
    [InlineData("starter_backfill")]
    [InlineData("revalidate")]
    [InlineData("participant_void")]
    [InlineData("host_declared_lost")]
    [InlineData("host_drain")]
    [InlineData("host_enable")]
    [InlineData("build_disable")]
    [InlineData("energy_adjust")]
    public async Task The_energy_audit_actions_are_accepted_with_every_earlier_action(string action)
    {
        var account = await NewAccountAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into operator_actions (correlation_id, operator, action, account_id, reason, checks, result) values (gen_random_uuid(), 'op', @action, @account, 'r', '{}', '{}')", new { account, action });
        var e = await FailureOf(db.AsApp(),
            "insert into operator_actions (correlation_id, operator, action, reason, checks, result) values (gen_random_uuid(), 'op', 'energy_reset', 'r', '{}', '{}')");
        Assert.Equal(("23514", "operator_actions_action_check"), (e.SqlState, e.ConstraintName));
    }

    // ---- upgrade (E3) ----

    [Fact]
    public async Task Upgrading_a_stage5_database_to_0006_keeps_every_row_and_opens_no_wallet()
    {
        await using var upgrade = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(upgrade.AsMigrator(), MigrationCatalog.Embedded);
        await migrator.MigrateAsync(5);
        Assert.Equal(0L, await AccountTestKit.ScalarAsSuperuserAsync<long>(upgrade, "select count(*) from pg_class where relname like 'energy%'"));

        // Two provisioned accounts as Stage 5 leaves them: a granted starter, operatives, items, a stack, and an inventory edit.
        var accounts = new List<long>();
        await using (var conn = await upgrade.AsApp().OpenConnectionAsync())
        {
            for (var i = 0; i < 2; i++)
            {
                var account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
                accounts.Add(account);
                await conn.ExecuteAsync("insert into inventories (account_id) values (@account)", new { account });
                await conn.ExecuteAsync(
                    "update inventories set starter_granted = true, starter_catalogue_version = @Catalogue, starter_granted_at = now(), version = version + 1 where account_id = @account",
                    new { account, Catalogue });
                var operative = await conn.ExecuteScalarAsync<long>(
                    "insert into operatives (account_id, definition_id, origin, origin_ref) values (@account, @Operative, 'starter', @r) returning operative_id",
                    new { account, Operative, r = $"starter:op{i}" });
                var item = await conn.ExecuteScalarAsync<long>(
                    "insert into item_instances (account_id, definition_id, origin, origin_ref) values (@account, @Rifle, 'starter', @r) returning item_id",
                    new { account, Rifle, r = $"starter:item{i}" });
                await conn.ExecuteAsync("insert into item_stacks (account_id, definition_id, quantity) values (@account, @Kit, 5)", new { account, Kit });
                // A Stage 5 edit: equip the stash rifle, split the stack, bump the version.
                await conn.ExecuteAsync("update item_instances set operative_id = @operative, equipped_slot = 'weapon' where item_id = @item", new { operative, item });
                await conn.ExecuteAsync("update item_stacks set quantity = 3 where account_id = @account", new { account });
                await conn.ExecuteAsync("insert into item_stacks (account_id, operative_id, definition_id, quantity) values (@account, @operative, @Kit, 2)", new { account, operative, Kit });
                await conn.ExecuteAsync("update inventories set version = version + 1 where account_id = @account", new { account });
            }
        }
        // An unprovisioned account too (Stage 3 shape): still no wallet after the upgrade.
        await using (var conn = await upgrade.AsApp().OpenConnectionAsync())
            accounts.Add(await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id"));

        const string Counts = """
            select (select count(*) from accounts), (select count(*) from inventories), (select count(*) from operatives),
                   (select count(*) from item_instances), (select count(*) from item_stacks), (select coalesce(sum(version), 0) from inventories)
            """;
        var before = await SingleAsync<(long, long, long, long, long, long)>(upgrade.AsSuperuser(), Counts);
        Assert.Equal((3L, 2L, 2L, 2L, 4L, 6L), before);

        Assert.Equal([6], (await migrator.MigrateAsync(6)).Applied);

        await using (var conn = await upgrade.AsSuperuser().OpenConnectionAsync())
        {
            Assert.Equal(before, await conn.QuerySingleAsync<(long, long, long, long, long, long)>(Counts));
            Assert.Equal((0L, 0L, 0L, 1L), await conn.QuerySingleAsync<(long, long, long, long)>(
                "select (select count(*) from energy_wallets), (select count(*) from energy_ledger), (select count(*) from anomaly_events), (select count(*) from energy_policies)"));
            Assert.Equal(2L, await conn.ExecuteScalarAsync<long>("select count(*) from item_instances where equipped_slot = 'weapon'"));
        }
        // The database is at version 6: the invariants of 6 and earlier apply (later ones name tables it doesn't have yet).
        Assert.Empty(await InvariantRunner.RunAsync(upgrade.AsSuperuser(), 6, InvariantRunner.RealDirectory));
    }

    // ---- invariants ----

    async Task<List<string>> InvariantKindsAsync()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var sql = await File.ReadAllTextAsync(Path.Combine(InvariantRunner.RealDirectory, InvariantFile));
        return (await conn.QueryAsync<string>($"select kind from ({sql.TrimEnd().TrimEnd(';')}) v order by kind")).ToList();
    }

    async Task AsReplicaAsync(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
        await conn.ExecuteAsync(sql, args, tx);
        await tx.CommitAsync();
    }

    [Theory]
    [InlineData("balance", new[] { "balance_vs_last_entry", "balance_vs_sum" })]
    [InlineData("delta", new[] { "balance_vs_sum", "entry_chain" })]
    [InlineData("chain", new[] { "entry_chain", "entry_chain" })]
    [InlineData("no_open", new[] { "open_missing" })]
    [InlineData("open_not_first", new[] { "open_not_first" })]
    [InlineData("no_entries", new[] { "balance_vs_last_entry", "open_missing" })]
    [InlineData("progress", new[] { "progress_not_below_interval" })]
    public async Task Each_energy_invariant_reports_its_violation(string fixture, string[] expectedKinds)
    {
        // A consistent history: open 50, then grants of 10 and 5; progress just below the interval.
        var account = await NewWalletAsync();
        await GrantAsync(account, 10);
        await GrantAsync(account, 5);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("update energy_wallets set regen_progress_us = 599999999 where account_id = @account", new { account });
        Assert.Empty(await InvariantKindsAsync());
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));

        var middle = await SuperScalarAsync<long>("select entry_id from energy_ledger where account_id = @account order by entry_id offset 1 limit 1", new { account });
        var other = fixture is "no_open" or "open_not_first" or "no_entries" ? await NewAccountAsync() : 0;
        const string Wallet = "insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@other, @balance, clock_timestamp(), 0, 1)";
        switch (fixture)
        {
            case "balance":
                await AsReplicaAsync("update energy_wallets set balance = 64 where account_id = @account", new { account });
                break;
            case "delta":
                await AsReplicaAsync("update energy_ledger set delta = 11 where entry_id = @middle", new { middle });
                break;
            case "chain":
                await AsReplicaAsync("update energy_ledger set balance_after = 61 where entry_id = @middle", new { middle });
                break;
            case "no_open":
                await AsReplicaAsync(Wallet + "; " + InsertEntry.Replace("@account", "@other") + "'dev_grant', 10, 10, @key)",
                    new { other, balance = 10, key = "dev_grant:" + Guid.NewGuid() });
                break;
            case "open_not_first":
                await AsReplicaAsync(Wallet + "; " + InsertEntry.Replace("@account", "@other") + "'dev_grant', 10, 10, @key); "
                    + InsertEntry.Replace("@account", "@other") + "'open', 0, 10, 'open')",
                    new { other, balance = 10, key = "dev_grant:" + Guid.NewGuid() });
                break;
            case "no_entries":
                await AsReplicaAsync(Wallet, new { other, balance = 0 });
                break;
            case "progress":
                await AsReplicaAsync("update energy_wallets set regen_progress_us = 600000000 where account_id = @account", new { account });
                break;
        }

        Assert.Equal(expectedKinds, await InvariantKindsAsync());
        Assert.Equal([$"{InvariantFile}: {expectedKinds.Length} violating row(s)"], await AccountTestKit.InvariantFailuresAsync(db));
    }
}
