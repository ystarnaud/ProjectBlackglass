using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Migration 0007 (Stage 7; owner rulings S7-D1-S7-D9, spec §7.1-§7.3, §15.1-§15.2) against a real PostgreSQL 17:
/// every CHECK of the templates, hosts, attempts, participants, holds and deploy requests; the forward changes to the
/// energy ledger (attempt-bound kinds and keys); the guarded state transitions (exactly the §7.2 edges); the immutable,
/// permanent and untruncatable rows; one active participation per account; a co-op-shaped attempt; the seeds; the upgrade
/// from Stage 6; and the attempt invariant file.</summary>
[Trait("Requires", "Postgres")]
public class AttemptSchemaTests : IAsyncLifetime
{
    TestDatabase db = null!;
    const string Template = "procedural-standard";
    const string InvariantFile = "0007_attempts.sql";
    const string Snapshot = """{"accountId":"2","operatives":[]}""";

    internal const string StandardTerms = """
        {
          "covers": ["defeat", "abandoned", "disconnected", "expired"],
          "consumableRefund": { "percent": 50, "rounding": "floor", "destination": "stash" },
          "lootRetention": {
            "maxEntries": 2,
            "maxUnitsPerEntry": 3,
            "eligibleCategories": ["Consumable", "Armor", "Utility", "Weapon"],
            "rank": ["categoryPriority", "saltedHash"],
            "categoryPriority": ["Weapon", "Armor", "Utility", "Consumable"]
          },
          "progressXp": { "percent": 40, "basis": "requiredObjectivesCompleted" }
        }
        """;

    static readonly string[] AttemptStates = ["admitted", "assigned", "launching", "ready", "running", "ending", "unresponsive", "cancelled", "settled"];
    static readonly string[] ParticipantStates = ["admitted", "joined", "left", "result_pending", "unresponsive", "quarantined", "released", "settled"];
    static readonly string[] HoldStates = ["reserved", "charged", "released", "settled", "refunded"];

    public async Task InitializeAsync()
    {
        db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    // ---- helpers ----

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

    async Task SuperExecuteAsync(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await conn.ExecuteAsync(sql, args);
    }

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    /// <summary>An account with an open wallet, as Stage 6 writes it.</summary>
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

    static byte[] Bytes(int length, byte fill = 0xab) => Enumerable.Repeat(fill, length).ToArray();

    const string InsertAttempt = """
        insert into mission_attempts (attempt_id, created_by, template_id, template_version, seed, retention_salt, assign_deadline)
        values (@attempt, @account, @template, 1, 12345, @salt, clock_timestamp() + interval '60 seconds')
        """;
    const string InsertParticipant = """
        insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
        values (@attempt, @account, @template, 1, @snapshot::jsonb, 1, @amount)
        """;
    const string InsertHold = "insert into energy_holds (attempt_id, account_id, amount) values (@attempt, @account, @amount)";

    /// <summary>Writes the participant, its hold and (for a positive amount) the reservation as admission will, in the
    /// caller's transaction: the wallet debit and its <c>reserve:&lt;attempt&gt;</c> entry under the wallet's write.</summary>
    static async Task AddParticipantAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid attempt, long account, int amount, string snapshot = Snapshot)
    {
        await conn.ExecuteAsync(InsertParticipant, new { attempt, account, template = Template, snapshot, amount }, tx);
        await conn.ExecuteAsync(InsertHold, new { attempt, account, amount }, tx);
        if (amount > 0)
        {
            var after = await conn.ExecuteScalarAsync<int>(
                "update energy_wallets set balance = balance - @amount where account_id = @account returning balance", new { account, amount }, tx);
            await conn.ExecuteAsync(
                "insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key) values (@account, @attempt, 'reserve', -@amount, @after, @key)",
                new { account, attempt, amount, after, key = $"reserve:{attempt}" }, tx);
        }
    }

    /// <summary>A solo admission as the app writes it: attempt, participant, hold, reservation and deploy request.</summary>
    async Task<Guid> AdmitAsync(long account, int amount = 10)
    {
        var attempt = Guid.CreateVersion7();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(InsertAttempt, new { attempt, account, template = Template, salt = Bytes(32) }, tx);
        await AddParticipantAsync(conn, tx, attempt, account, amount);
        await conn.ExecuteAsync(
            "insert into deploy_requests (account_id, idempotency_key, request_hash, attempt_id, response) values (@account, @key, @hash, @attempt, @response::jsonb)",
            new { account, key = Guid.NewGuid(), hash = Bytes(32, 0x01), attempt, response = $$"""{"attemptId":"{{attempt}}"}""" }, tx);
        await tx.CommitAsync();
        return attempt;
    }

    /// <summary>Releases one participant as the cancel will, in the caller's transaction: participant and hold to
    /// released and, for a positive amount, the credit and its <c>release:&lt;attempt&gt;</c> entry.</summary>
    static async Task ReleaseParticipantAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid attempt, long account)
    {
        Assert.Equal(1, await conn.ExecuteAsync("update attempt_participants set state = 'released' where attempt_id = @attempt and account_id = @account and state = 'admitted'", new { attempt, account }, tx));
        var amount = await conn.ExecuteScalarAsync<int>(
            "update energy_holds set state = 'released' where attempt_id = @attempt and account_id = @account and state = 'reserved' returning amount", new { attempt, account }, tx);
        if (amount > 0)
        {
            var after = await conn.ExecuteScalarAsync<int>(
                "update energy_wallets set balance = balance + @amount where account_id = @account returning balance", new { account, amount }, tx);
            await conn.ExecuteAsync(
                "insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key) values (@account, @attempt, 'release', @amount, @after, @key)",
                new { account, attempt, amount, after, key = $"release:{attempt}" }, tx);
        }
    }

    /// <summary>A solo player cancel: the attempt to cancelled, then the participant release.</summary>
    async Task CancelAsync(Guid attempt, long account)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        Assert.Equal(1, await conn.ExecuteAsync(
            "update mission_attempts set state = 'cancelled', cancel_reason = 'player_cancel', ended_at = clock_timestamp() where attempt_id = @attempt and state = 'admitted'", new { attempt }, tx));
        await ReleaseParticipantAsync(conn, tx, attempt, account);
        await tx.CommitAsync();
    }

    /// <summary>Runs <paramref name="sql"/> as the superuser in an always rolled-back transaction with the user triggers of
    /// <paramref name="table"/> disabled, so a CHECK is seen on its own, and returns the failure.</summary>
    async Task<PostgresException> RolledBackFailureAsync(string table, string sql, object? args)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync($"alter table {table} disable trigger user", transaction: tx);
        var e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, args, tx));
        await tx.RollbackAsync();
        return e;
    }

    // ---- templates ----

    const string InsertTemplate = "insert into mission_templates (template_id, version, energy_cost, min_operatives, max_operatives) values ";

    [Theory]
    [InlineData("('', 1, 10, 1, 3)", "mission_templates_template_id_check")]
    [InlineData("('Procedural', 1, 10, 1, 3)", "mission_templates_template_id_check")]
    [InlineData("('a_b', 1, 10, 1, 3)", "mission_templates_template_id_check")]
    [InlineData("('a--b', 1, 10, 1, 3)", "mission_templates_template_id_check")]
    [InlineData("(repeat('a', 65), 1, 10, 1, 3)", "mission_templates_template_id_check")]
    [InlineData("('t', 0, 10, 1, 3)", "mission_templates_version_check")]
    [InlineData("('t', 1, -1, 1, 3)", "mission_templates_energy_cost_check")]
    [InlineData("('t', 1, 10, 0, 3)", "mission_templates_min_operatives_check")]
    [InlineData("('t', 1, 10, 3, 2)", "mission_template_operative_range")]
    public async Task A_template_check_is_enforced(string values, string constraint)
    {
        var e = await FailureOf(db.AsApp(), InsertTemplate + values);
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
        Assert.Equal(1L, await SuperScalarAsync<long>("select count(*) from mission_templates"));
    }

    [Fact]
    public async Task Zero_cost_and_boundary_templates_are_accepted()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync(InsertTemplate + "('zero-cost', 1, 0, 1, 1)");
        await conn.ExecuteAsync(InsertTemplate + "(repeat('a', 64), 2147483647, 2147483647, 1, 2147483647)");
        await conn.ExecuteAsync(InsertTemplate + "('procedural-standard', 2, 12, 2, 4)");   // a rebalance is a new version
        var e = await FailureOf(db.AsApp(), InsertTemplate + "('procedural-standard', 1, 12, 1, 3)");
        Assert.Equal(("23505", "mission_templates_pkey"), (e.SqlState, e.ConstraintName));
        Assert.Equal(4L, await conn.ExecuteScalarAsync<long>("select count(*) from mission_templates"));
    }

    [Fact]
    public async Task Templates_cannot_be_updated_deleted_or_truncated_even_by_the_owner()
    {
        foreach (var ds in new[] { db.AsMigrator(), db.AsSuperuser() })
        {
            foreach (var sql in new[]
            {
                "update mission_templates set energy_cost = 0",
                "update mission_templates set created_at = created_at",   // even a no-op
                "delete from mission_templates",
                "truncate mission_templates cascade",
            })
            {
                var e = await FailureOf(ds, sql);
                Assert.Equal(("23514", "mission_template_is_immutable"), (e.SqlState, e.ConstraintName));
            }
        }
        Assert.Equal(10, await SuperScalarAsync<int>("select energy_cost from mission_templates where template_id = @Template and version = 1", new { Template }));
    }

    // ---- hosts ----

    const string InsertHost = "insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled) values (@host, @kind, @secret, @capacity, true)";

    [Theory]
    [InlineData("", "dev_fixture", 32, 1, "mission_hosts_host_id_check")]
    [InlineData("Host", "dev_fixture", 32, 1, "mission_hosts_host_id_check")]
    [InlineData("a b", "dev_fixture", 32, 1, "mission_hosts_host_id_check")]
    [InlineData("h", "mission", 32, 1, "mission_hosts_kind_check")]
    [InlineData("h", "dev_fixture", 31, 1, "mission_hosts_secret_hash_check")]
    [InlineData("h", "agent", 33, 1, "mission_hosts_secret_hash_check")]
    [InlineData("h", "agent", 32, -1, "mission_hosts_capacity_check")]
    public async Task A_host_check_is_enforced(string host, string kind, int secretLength, int capacity, string constraint)
    {
        var e = await FailureOf(db.AsApp(), InsertHost, new { host, kind, secret = Bytes(secretLength), capacity });
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task Hosts_of_both_kinds_are_accepted_and_a_host_defaults_to_disabled_and_undrained()
    {
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync(InsertHost, new { host = "bg-dev-fixture.1", kind = "dev_fixture", secret = Bytes(32, 1), capacity = 0 });
            await conn.ExecuteAsync(InsertHost, new { host = "bg-dev-mission01", kind = "agent", secret = Bytes(32, 2), capacity = 4 });
            var e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(InsertHost, new { host = "other", kind = "agent", secret = Bytes(32, 2), capacity = 1 }));
            Assert.Equal(("23505", "mission_hosts_secret_hash_key"), (e.SqlState, e.ConstraintName));
        }
        await SuperExecuteAsync("insert into mission_hosts (host_id, kind, secret_hash, capacity) values ('bare', 'agent', @s, 1)", new { s = Bytes(32, 3) });
        Assert.Equal((false, false, (DateTime?)null), await SuperScalarTupleAsync("select enabled, drained, last_heartbeat_at from mission_hosts where host_id = 'bare'"));
    }

    async Task<(bool, bool, DateTime?)> SuperScalarTupleAsync(string sql)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<(bool, bool, DateTime?)>(sql);
    }

    // ---- attempts, participants, holds, deploy requests: CHECKs and keys ----

    [Theory]
    [InlineData("seed = -1", "mission_attempts_seed_check")]
    [InlineData("retention_salt = '\\x00'::bytea", "mission_attempts_retention_salt_check")]
    [InlineData("state = 'paused'", "mission_attempts_state_check")]
    [InlineData("state = 'cancelled', cancel_reason = 'launch_failed', ended_at = clock_timestamp()", "mission_attempts_cancel_reason_check")]
    [InlineData("state = 'cancelled', ended_at = clock_timestamp()", "mission_attempt_cancel_reason_iff_cancelled")]
    [InlineData("cancel_reason = 'player_cancel'", "mission_attempt_cancel_reason_iff_cancelled")]
    [InlineData("state = 'cancelled', cancel_reason = 'no_capacity'", "mission_attempt_ended_at_iff_ended")]
    [InlineData("state = 'settled'", "mission_attempt_ended_at_iff_ended")]
    [InlineData("ended_at = clock_timestamp()", "mission_attempt_ended_at_iff_ended")]
    public async Task An_attempt_check_is_enforced(string set, string constraint)
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var e = await RolledBackFailureAsync("mission_attempts", $"update mission_attempts set {set} where attempt_id = @attempt", new { attempt });
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task An_attempt_needs_a_known_account_and_template()
    {
        var account = await NewAccountAsync();
        var e = await FailureOf(db.AsApp(), InsertAttempt, new { attempt = Guid.CreateVersion7(), account, template = "no-such-template", salt = Bytes(32) });
        Assert.Equal(("23503", "mission_attempts_template_fkey"), (e.SqlState, e.ConstraintName));
        e = await FailureOf(db.AsApp(), InsertAttempt, new { attempt = Guid.CreateVersion7(), account = account + 1000, template = Template, salt = Bytes(32) });
        Assert.Equal(("23503", "mission_attempts_created_by_fkey"), (e.SqlState, e.ConstraintName));
        Assert.Equal(0L, await SuperScalarAsync<long>("select count(*) from mission_attempts"));
    }

    [Theory]
    [InlineData("loadout_snapshot = '[]'::jsonb", "attempt_participants_loadout_snapshot_check")]
    [InlineData("inventory_version = 0", "attempt_participants_inventory_version_check")]
    [InlineData("energy_cost = -1", "attempt_participants_energy_cost_check")]
    [InlineData("state = 'paused'", "attempt_participants_state_check")]
    public async Task A_participant_check_is_enforced(string set, string constraint)
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var e = await RolledBackFailureAsync("attempt_participants", $"update attempt_participants set {set} where attempt_id = @attempt", new { attempt });
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task A_participant_belongs_to_a_known_attempt_with_the_attempts_template()
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var other = await NewAccountAsync();
        await SuperExecuteAsync(InsertTemplate + "('procedural-standard', 2, 10, 1, 3)");
        // Another version of the template than the attempt's.
        var e = await FailureOf(db.AsApp(),
            "insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost) values (@attempt, @other, @Template, 2, '{}', 1, 0)",
            new { attempt, other, Template });
        Assert.Equal(("23503", "attempt_participants_attempt_template_fkey"), (e.SqlState, e.ConstraintName));
        // An unknown attempt.
        e = await FailureOf(db.AsApp(), InsertParticipant, new { attempt = Guid.CreateVersion7(), account = other, template = Template, snapshot = Snapshot, amount = 0 });
        Assert.Equal(("23503", "attempt_participants_attempt_template_fkey"), (e.SqlState, e.ConstraintName));
        // An unknown account.
        e = await FailureOf(db.AsApp(), InsertParticipant, new { attempt, account = other + 1000, template = Template, snapshot = Snapshot, amount = 0 });
        Assert.Equal(("23503", "attempt_participants_account_id_fkey"), (e.SqlState, e.ConstraintName));
        // The same account twice in one attempt.
        e = await FailureOf(db.AsApp(), InsertParticipant, new { attempt, account, template = Template, snapshot = Snapshot, amount = 0 });
        Assert.Equal(("23505", "attempt_participants_pkey"), (e.SqlState, e.ConstraintName));
    }

    [Theory]
    [InlineData("amount = -1", "energy_holds_amount_check")]
    [InlineData("state = 'held'", "energy_holds_state_check")]
    public async Task A_hold_check_is_enforced(string set, string constraint)
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var e = await RolledBackFailureAsync("energy_holds", $"update energy_holds set {set} where attempt_id = @attempt", new { attempt });
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task A_hold_needs_its_participant_and_is_one_per_participant()
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var other = await NewAccountAsync();
        var e = await FailureOf(db.AsApp(), InsertHold, new { attempt, account = other, amount = 0 });
        Assert.Equal(("23503", "energy_holds_participant_fkey"), (e.SqlState, e.ConstraintName));
        e = await FailureOf(db.AsApp(), InsertHold, new { attempt, account, amount = 0 });
        Assert.Equal(("23505", "energy_holds_pkey"), (e.SqlState, e.ConstraintName));
    }

    const string InsertDeployRequest = "insert into deploy_requests (account_id, idempotency_key, request_hash, attempt_id, response) values (@account, @key, @hash, @attempt, @response::jsonb)";

    [Fact]
    public async Task A_deploy_request_check_is_enforced()
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var key = Guid.NewGuid();
        var e = await FailureOf(db.AsApp(), InsertDeployRequest, new { account, key, hash = Bytes(31), attempt, response = "{}" });
        Assert.Equal(("23514", "deploy_requests_request_hash_check"), (e.SqlState, e.ConstraintName));
        e = await FailureOf(db.AsApp(), InsertDeployRequest, new { account, key, hash = Bytes(32), attempt, response = "\"text\"" });
        Assert.Equal(("23514", "deploy_requests_response_check"), (e.SqlState, e.ConstraintName));
        // A request names an attempt its account takes part in; the attempt is required.
        var other = await NewAccountAsync();
        e = await FailureOf(db.AsApp(), InsertDeployRequest, new { account = other, key, hash = Bytes(32), attempt, response = "{}" });
        Assert.Equal(("23503", "deploy_requests_participant_fkey"), (e.SqlState, e.ConstraintName));
        e = await FailureOf(db.AsApp(), InsertDeployRequest, new { account, key, hash = Bytes(32), attempt = (Guid?)null, response = "{}" });
        Assert.Equal("23502", e.SqlState);
        // One row per (account, key).
        var existing = await SuperScalarAsync<Guid>("select idempotency_key from deploy_requests where account_id = @account", new { account });
        e = await FailureOf(db.AsApp(), InsertDeployRequest, new { account, key = existing, hash = Bytes(32), attempt, response = "{}" });
        Assert.Equal(("23505", "deploy_requests_pkey"), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task Deploy_requests_cannot_be_updated_deleted_or_truncated_even_by_the_owner()
    {
        var account = await NewWalletAsync();
        await AdmitAsync(account);
        foreach (var ds in new[] { db.AsMigrator(), db.AsSuperuser() })
        {
            foreach (var sql in new[]
            {
                "update deploy_requests set response = '{}'",
                "update deploy_requests set created_at = created_at",
                "delete from deploy_requests",
                "truncate deploy_requests",
            })
            {
                var e = await FailureOf(ds, sql);
                Assert.Equal(("23514", "deploy_request_is_immutable"), (e.SqlState, e.ConstraintName));
            }
        }
        Assert.Equal(1L, await SuperScalarAsync<long>("select count(*) from deploy_requests"));
    }

    // ---- energy ledger: the forward changes ----

    const string InsertEntry = "insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key) values (@account, @attempt, ";

    [Theory]
    // The attempt-bound kinds need an attempt; reserve and release keys then can't name one either.
    [InlineData("charge", 0, 40, "'charge:x'", false, "energy_ledger_kind_attempt_check")]
    [InlineData("settle", 0, 40, "'settle:x'", false, "energy_ledger_kind_attempt_check")]
    [InlineData("void_refund", 1, 41, "'void_refund:x'", false, "energy_ledger_kind_attempt_check")]
    [InlineData("reserve", -1, 39, "'reserve:' || @other", false, "energy_ledger_idempotency_key_check")]
    [InlineData("release", 1, 41, "'release:' || @other", false, "energy_ledger_idempotency_key_check")]
    // The account kinds must not carry one.
    [InlineData("open", 0, 40, "'open'", true, "energy_ledger_kind_attempt_check")]
    [InlineData("regen", 1, 41, "'regen:1'", true, "energy_ledger_kind_attempt_check")]
    [InlineData("dev_grant", 1, 41, "'dev_grant:0f8fad5b-d9cb-469f-a165-70867728950e'", true, "energy_ledger_kind_attempt_check")]
    // Reserve and release keys are exactly '<kind>:<the entry's attempt, canonical lower case>'.
    [InlineData("reserve", -1, 39, "'reserve:' || @other", true, "energy_ledger_idempotency_key_check")]      // another attempt
    [InlineData("reserve", -1, 39, "'reserve:' || upper(@attempt::text)", true, "energy_ledger_idempotency_key_check")]
    [InlineData("reserve", -1, 39, "'reserve:' || replace(@attempt::text, '-', '')", true, "energy_ledger_idempotency_key_check")]
    [InlineData("reserve", -1, 39, "'reserve'", true, "energy_ledger_idempotency_key_check")]
    [InlineData("reserve", -1, 39, "'reserve:x'", true, "energy_ledger_idempotency_key_check")]
    [InlineData("reserve", -1, 39, "'release:' || @attempt", true, "energy_ledger_idempotency_key_check")]
    [InlineData("reserve", -1, 39, "'reserve:' || @attempt || 'x'", true, "energy_ledger_idempotency_key_check")]
    [InlineData("release", 1, 41, "'release:' || @attempt || ':2'", true, "energy_ledger_idempotency_key_check")]
    [InlineData("release", 1, 41, "'reserve:' || @attempt", true, "energy_ledger_idempotency_key_check")]
    [InlineData("release", 1, 41, "'release'", true, "energy_ledger_idempotency_key_check")]
    // 0006's other rules are unchanged.
    [InlineData("reserve", 0, 40, "'reserve:' || @attempt", true, "energy_ledger_delta_sign_check")]
    [InlineData("release", -1, 39, "'release:' || @attempt", true, "energy_ledger_delta_sign_check")]
    [InlineData("charge", 0, 40, "'charge'", true, null)]
    public async Task The_attempt_kinds_carry_their_attempt_and_reserve_and_release_keys_name_it(string kind, int delta, int after, string key, bool withAttempt, string? constraint)
    {
        var account = await NewWalletAsync(40);
        var attempt = await AdmitAsync(account, 0);
        var other = Guid.CreateVersion7().ToString();
        var sql = InsertEntry + $"'{kind}', {delta}, {after}, {key})";
        var args = new { account, attempt = withAttempt ? attempt : (Guid?)null, other };
        if (constraint is null)
        {
            // Accepted by every CHECK (the commit check, not under test here, is satisfied by the wallet write).
            await using var conn = await db.AsApp().OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(sql, args, tx);
            await tx.RollbackAsync();
            return;
        }
        var e = await RolledBackFailureAsync("energy_ledger", sql, args);
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task A_ledger_entry_must_name_an_existing_attempt()
    {
        var account = await NewWalletAsync();
        var attempt = Guid.CreateVersion7();
        var e = await RolledBackFailureAsync("energy_ledger", InsertEntry + "'reserve', -10, 40, 'reserve:' || @attempt)", new { account, attempt });
        Assert.Equal(("23503", "energy_ledger_attempt_id_fkey"), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task A_reservation_and_its_release_commit_as_admission_and_cancel_write_them()
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        Assert.Equal((40, "reserve:" + attempt), await SingleAsync<(int, string)>(
            "select balance_after, idempotency_key from energy_ledger where account_id = @account and kind = 'reserve'", new { account }));
        await CancelAsync(attempt, account);
        Assert.Equal((50, "release:" + attempt), await SingleAsync<(int, string)>(
            "select balance_after, idempotency_key from energy_ledger where account_id = @account and kind = 'release'", new { account }));
        // A second release of the same attempt is refused by the ledger key.
        var e = await FailureOf(db.AsApp(), InsertEntry + "'release', 10, 60, 'release:' || @attempt)", new { account, attempt });
        Assert.Equal(("23505", "energy_ledger_account_id_idempotency_key_key"), (e.SqlState, e.ConstraintName));
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
    }

    async Task<T> SingleAsync<T>(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<T>(sql, args);
    }

    // ---- guarded transitions: exactly the §7.2 edges ----

    static readonly (string From, string To)[] AttemptEdges =
    [
        ("admitted", "assigned"), ("assigned", "launching"), ("launching", "ready"), ("ready", "running"), ("running", "ending"),
        ("ending", "settled"), ("admitted", "cancelled"), ("assigned", "cancelled"), ("launching", "cancelled"), ("ready", "cancelled"),
        ("running", "unresponsive"), ("unresponsive", "running"), ("unresponsive", "ending"), ("unresponsive", "settled"),
    ];

    static readonly (string From, string To)[] ParticipantEdges =
    [
        ("admitted", "joined"), ("admitted", "released"), ("joined", "left"), ("left", "joined"), ("joined", "result_pending"),
        ("left", "result_pending"), ("result_pending", "settled"), ("joined", "quarantined"), ("left", "quarantined"),
        ("result_pending", "quarantined"), ("quarantined", "settled"),
    ];

    static readonly (string From, string To)[] HoldEdges = [("reserved", "released"), ("reserved", "charged")];

    static string AttemptSet(string state) =>
        $"state = '{state}', cancel_reason = {(state == "cancelled" ? "'no_capacity'" : "null")}, ended_at = {(state is "cancelled" or "settled" ? "clock_timestamp()" : "null")}";

    /// <summary>For every ordered pair of distinct states: puts the row in the first (replica mode, as the superuser,
    /// rolled back afterwards) and tries the second as the owner. Returns the pairs that passed.</summary>
    async Task<List<(string, string)>> PassingTransitionsAsync(string table, string[] states, Func<string, string> set, string where, object args)
    {
        var passed = new List<(string, string)>();
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        foreach (var from in states)
            foreach (var to in states.Where(s => s != from))
            {
                await using var tx = await su.BeginTransactionAsync();
                await su.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
                await su.ExecuteAsync($"update {table} set {set(from)} where {where}", args, tx);
                await su.ExecuteAsync("set local session_replication_role = origin", transaction: tx);
                await su.ExecuteAsync("savepoint s", transaction: tx);
                try
                {
                    Assert.Equal(1, await su.ExecuteAsync($"update {table} set {set(to)} where {where}", args, tx));
                    passed.Add((from, to));
                }
                catch (PostgresException e)
                {
                    Assert.Equal("23514", e.SqlState);
                    Assert.EndsWith("_transition", e.ConstraintName);
                }
                await tx.RollbackAsync();
            }
        return passed;
    }

    [Fact]
    public async Task Attempts_take_exactly_the_section_7_2_edges()
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var passed = await PassingTransitionsAsync("mission_attempts", AttemptStates, AttemptSet, "attempt_id = @attempt", new { attempt });
        Assert.Equal(AttemptEdges.Order(), passed.Order());
    }

    [Fact]
    public async Task Participants_take_exactly_the_section_7_2_edges()
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var passed = await PassingTransitionsAsync("attempt_participants", ParticipantStates, s => $"state = '{s}'", "attempt_id = @attempt", new { attempt });
        Assert.Equal(ParticipantEdges.Order(), passed.Order());
    }

    [Fact]
    public async Task Holds_take_only_release_and_charge_from_reserved()
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var passed = await PassingTransitionsAsync("energy_holds", HoldStates, s => $"state = '{s}'", "attempt_id = @attempt", new { attempt });
        Assert.Equal(HoldEdges.Order(), passed.Order());
    }

    [Fact]
    public async Task An_unchanged_state_may_be_written_again_as_a_no_op()
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        await using var conn = await db.AsApp().OpenConnectionAsync();
        Assert.Equal(1, await conn.ExecuteAsync("update mission_attempts set state = state, cancel_reason = cancel_reason, ended_at = ended_at where attempt_id = @attempt", new { attempt }));
        Assert.Equal(1, await conn.ExecuteAsync("update attempt_participants set state = 'admitted' where attempt_id = @attempt", new { attempt }));
        Assert.Equal(1, await conn.ExecuteAsync("update energy_holds set state = 'reserved' where attempt_id = @attempt", new { attempt }));
    }

    [Theory]
    [InlineData("seed = seed + 1")]
    [InlineData("retention_salt = decode(repeat('cd', 32), 'hex')")]
    [InlineData("template_version = 2")]
    [InlineData("template_id = 'zero-cost'")]
    [InlineData("created_by = created_by + 0 + @other")]
    [InlineData("assign_deadline = assign_deadline + interval '1 second'")]
    [InlineData("created_at = created_at - interval '1 second'")]
    [InlineData("attempt_id = gen_random_uuid()")]
    // cancel_reason and ended_at move only with a state change.
    [InlineData("cancel_reason = 'player_cancel'")]
    [InlineData("ended_at = clock_timestamp()")]
    public async Task An_attempts_fixed_columns_never_change(string set)
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        await SuperExecuteAsync(InsertTemplate + "('procedural-standard', 2, 10, 1, 3), ('zero-cost', 1, 0, 1, 1)");
        var other = await NewAccountAsync() - account;
        var e = await FailureOf(db.AsMigrator(), $"update mission_attempts set {set} where attempt_id = @attempt", new { attempt, other });
        Assert.Equal(("23514", "mission_attempt_transition"), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task A_cancelled_attempts_reason_and_end_are_final()
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        await CancelAsync(attempt, account);
        foreach (var set in new[] { "cancel_reason = 'no_capacity'", "ended_at = ended_at + interval '1 second'" })
        {
            var e = await FailureOf(db.AsMigrator(), $"update mission_attempts set {set} where attempt_id = @attempt", new { attempt });
            Assert.Equal(("23514", "mission_attempt_transition"), (e.SqlState, e.ConstraintName));
        }
    }

    [Theory]
    [InlineData("loadout_snapshot = '{\"accountId\":\"3\"}'")]
    [InlineData("inventory_version = inventory_version + 1")]
    [InlineData("energy_cost = energy_cost + 1")]
    [InlineData("template_version = 2")]
    [InlineData("account_id = account_id + 0 + @other")]
    [InlineData("attempt_id = gen_random_uuid()")]
    public async Task A_participants_snapshot_and_keys_never_change(string set)
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        await SuperExecuteAsync(InsertTemplate + "('procedural-standard', 2, 10, 1, 3)");
        var other = await NewAccountAsync() - account;
        var e = await FailureOf(db.AsMigrator(), $"update attempt_participants set {set} where attempt_id = @attempt", new { attempt, other });
        Assert.Equal(("23514", "attempt_participant_transition"), (e.SqlState, e.ConstraintName));
        Assert.True(await SuperScalarAsync<bool>("select loadout_snapshot = @Snapshot::jsonb from attempt_participants where attempt_id = @attempt", new { attempt, Snapshot }));
    }

    [Theory]
    [InlineData("amount = amount + 1")]
    [InlineData("amount = 0, state = 'released'")]
    [InlineData("account_id = account_id + 0 + @other")]
    [InlineData("attempt_id = gen_random_uuid()")]
    [InlineData("updated_at = updated_at - interval '1 second'")]
    public async Task A_holds_amount_and_keys_never_change(string set)
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var other = await NewAccountAsync() - account;
        var e = await FailureOf(db.AsMigrator(), $"update energy_holds set {set} where attempt_id = @attempt", new { attempt, other });
        Assert.Equal(("23514", "energy_hold_transition"), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task A_hold_state_change_is_stamped_by_the_database()
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var admitted = await SuperScalarAsync<DateTime>("select updated_at from energy_holds where attempt_id = @attempt", new { attempt });
        await CancelAsync(attempt, account);
        var released = await SuperScalarAsync<DateTime>("select updated_at from energy_holds where attempt_id = @attempt", new { attempt });
        Assert.True(released > admitted, $"{released:O} should follow {admitted:O}");
    }

    [Theory]
    [InlineData("mission_attempts", "insert into mission_attempts (attempt_id, created_by, template_id, template_version, seed, retention_salt, assign_deadline, state) values (@attempt2, @account, 'procedural-standard', 1, 1, @salt, now(), 'assigned')", "mission_attempt_transition")]
    [InlineData("attempt_participants", "insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost, state) values (@attempt, @other, 'procedural-standard', 1, '{}', 1, 0, 'joined')", "attempt_participant_transition")]
    [InlineData("energy_holds", "insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost) values (@attempt, @other, 'procedural-standard', 1, '{}', 1, 0); insert into energy_holds (attempt_id, account_id, amount, state) values (@attempt, @other, 0, 'charged')", "energy_hold_transition")]
    public async Task Rows_start_in_their_initial_state(string table, string sql, string constraint)
    {
        Assert.NotEmpty(table);
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        var other = await NewAccountAsync();
        var e = await FailureOf(db.AsMigrator(), sql, new { attempt, attempt2 = Guid.CreateVersion7(), account, other, salt = Bytes(32) });
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task Attempts_participants_and_holds_are_never_deleted_or_truncated_even_by_the_owner()
    {
        var account = await NewWalletAsync();
        var attempt = await AdmitAsync(account);
        foreach (var ds in new[] { db.AsMigrator(), db.AsSuperuser() })
        {
            foreach (var (sql, constraint) in new[]
            {
                ("delete from energy_holds", "energy_hold_is_permanent"),
                ("truncate energy_holds", "energy_hold_is_permanent"),
                ("delete from attempt_participants", "attempt_participant_is_permanent"),
                ("truncate attempt_participants cascade", "attempt_participant_is_permanent"),
                ("delete from mission_attempts", "mission_attempt_is_permanent"),
                ("truncate mission_attempts cascade", "mission_attempt_is_permanent"),
                ("truncate mission_templates cascade", "mission_template_is_immutable"),
            })
            {
                var e = await FailureOf(ds, sql);
                Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
            }
        }
        Assert.Equal((1L, 1L, 1L), await SingleAsync<(long, long, long)>(
            "select (select count(*) from mission_attempts), (select count(*) from attempt_participants), (select count(*) from energy_holds)"));
        _ = attempt;
    }

    // ---- one active participation ----

    [Fact]
    public async Task A_direct_insert_of_a_second_active_participation_fails_on_the_index()
    {
        var account = await NewWalletAsync();
        var first = await AdmitAsync(account);
        var second = Guid.CreateVersion7();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(InsertAttempt, new { attempt = second, account, template = Template, salt = Bytes(32) }, tx);
            var e = await Assert.ThrowsAsync<PostgresException>(() =>
                conn.ExecuteAsync(InsertParticipant, new { attempt = second, account, template = Template, snapshot = Snapshot, amount = 0 }, tx));
            Assert.Equal(("23505", "one_active_participation"), (e.SqlState, e.ConstraintName));
        }
        // The owner and the superuser are held to it too (it is an index, not a trigger: replica mode doesn't skip it).
        foreach (var ds in new[] { db.AsMigrator(), db.AsSuperuser() })
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            if (ds == db.AsSuperuser()) await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
            await conn.ExecuteAsync(InsertAttempt, new { attempt = second, account, template = Template, salt = Bytes(32) }, tx);
            var e = await Assert.ThrowsAsync<PostgresException>(() =>
                conn.ExecuteAsync(InsertParticipant, new { attempt = second, account, template = Template, snapshot = Snapshot, amount = 0 }, tx));
            Assert.Equal(("23505", "one_active_participation"), (e.SqlState, e.ConstraintName));
        }
        Assert.Equal(1L, await SuperScalarAsync<long>("select count(*) from attempt_participants where account_id = @account", new { account }));

        // After the release (a terminal state), the account may take part again.
        await CancelAsync(first, account);
        var third = await AdmitAsync(account);
        Assert.Equal(new[] { "admitted", "released" }, (await QueryAsync<string>(
            "select state from attempt_participants where account_id = @account order by state", new { account })).ToArray());
        Assert.NotEqual(first, third);
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
    }

    async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<T>(sql, args)).ToList();
    }

    [Fact]
    public async Task The_active_states_are_exactly_the_indexed_ones()
    {
        var predicate = await SuperScalarAsync<string>("select pg_get_expr(indpred, indrelid) from pg_index where indexrelid = 'one_active_participation'::regclass");
        foreach (var state in new[] { "admitted", "joined", "left", "result_pending", "unresponsive", "quarantined" })
            Assert.Contains($"'{state}'", predicate, StringComparison.Ordinal);
        foreach (var state in new[] { "released", "settled" })
            Assert.DoesNotContain($"'{state}'", predicate, StringComparison.Ordinal);
        Assert.True(await SuperScalarAsync<bool>("select indisunique from pg_index where indexrelid = 'one_active_participation'::regclass"));
    }

    // ---- co-op shape ----

    [Fact]
    public async Task Co_op_shaped_data_keeps_holds_snapshots_and_insurance_independent()
    {
        var host = await NewWalletAsync();
        var guest = await NewWalletAsync(30);
        // The host's account holds an active standard coverage.
        var activation = Guid.CreateVersion7();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("""
                insert into insurance_activations (activation_id, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source, idempotency_key)
                values (@activation, @host, 'standard', 1, interval '1 day', null, clock_timestamp() + interval '1 day', 'dev_grant', gen_random_uuid())
                """, new { activation, host }, tx);
            await conn.ExecuteAsync("""
                insert into insurance_coverages (account_id, policy_id, policy_version, activation_id, starts_at, expires_at)
                select account_id, policy_id, policy_version, activation_id, created_at, resulting_expires_at from insurance_activations where activation_id = @activation
                """, new { activation }, tx);
            await tx.CommitAsync();
        }

        // Two participants by direct SQL (admission itself refuses a second until co-op is authorized).
        var attempt = Guid.CreateVersion7();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(InsertAttempt, new { attempt, account = host, template = Template, salt = Bytes(32) }, tx);
            await AddParticipantAsync(conn, tx, attempt, host, 10, """{"accountId":"host","operatives":["a"]}""");
            await AddParticipantAsync(conn, tx, attempt, guest, 0, """{"accountId":"guest","operatives":["b","c"]}""");
            await conn.ExecuteAsync("""
                insert into attempt_insurance (attempt_id, account_id, activation_id, policy_id, policy_version, terms)
                select @attempt, c.account_id, c.activation_id, c.policy_id, c.policy_version, p.terms
                from insurance_coverages c join insurance_policies p on p.policy_id = c.policy_id and p.version = c.policy_version
                where c.account_id = @host
                """, new { attempt, host }, tx);
            await tx.CommitAsync();
        }
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));

        // The guest leaves before the start: only its participant and hold move; the host's stay, and so does its insurance.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await ReleaseParticipantAsync(conn, tx, attempt, guest);
            await tx.CommitAsync();
        }
        var rows = (await QueryAsync<(long Account, string Participant, string Hold, int Amount, string Snapshot, bool Insured)>("""
            select p.account_id, p.state, h.state, h.amount, p.loadout_snapshot->>'accountId',
                   exists (select 1 from attempt_insurance i where i.attempt_id = p.attempt_id and i.account_id = p.account_id)
            from attempt_participants p join energy_holds h using (attempt_id, account_id)
            where p.attempt_id = @attempt order by p.account_id
            """, new { attempt })).ToList();
        Assert.Equal(new[]
        {
            (host, "admitted", "reserved", 10, "host", true),
            (guest, "released", "released", 0, "guest", false),
        }, rows);
        Assert.Equal((40, 30), await SingleAsync<(int, int)>(
            "select (select balance from energy_wallets where account_id = @host), (select balance from energy_wallets where account_id = @guest)", new { host, guest }));
        Assert.Equal("admitted", await SuperScalarAsync<string>("select state from mission_attempts where attempt_id = @attempt", new { attempt }));
        // The guest is free to join another attempt; the host is not.
        await AdmitAsync(guest, 0);
        var e = await Assert.ThrowsAsync<PostgresException>(() => AdmitAsync(host, 0));
        Assert.Equal(("23505", "one_active_participation"), (e.SqlState, e.ConstraintName));
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
    }

    // ---- seeds ----

    [Fact]
    public async Task Seeds_are_the_provisional_values()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        var template = await conn.QuerySingleAsync<(string Id, int Version, int Cost, int Min, int Max, bool Past)>(
            "select template_id, version, energy_cost, min_operatives, max_operatives, created_at <= clock_timestamp() from mission_templates");
        Assert.Equal(("procedural-standard", 1, 10, 1, 3, true), template);
        var policy = await conn.QuerySingleAsync<(string Id, int Version, bool Terms, bool Past)>(
            "select policy_id, version, terms = @StandardTerms::jsonb, created_at <= clock_timestamp() from insurance_policies", new { StandardTerms });
        Assert.Equal(("standard", 1, true, true), policy);
        // Spot checks of the terms as stored (the JSON comparison above is exact on every member and value).
        Assert.Equal((50, 2, 3, 40), await conn.QuerySingleAsync<(int, int, int, int)>("""
            select (terms->'consumableRefund'->>'percent')::int, (terms->'lootRetention'->>'maxEntries')::int,
                   (terms->'lootRetention'->>'maxUnitsPerEntry')::int, (terms->'progressXp'->>'percent')::int from insurance_policies
            """));
        Assert.Equal(0L, await conn.ExecuteScalarAsync<long>(
            "select (select count(*) from mission_hosts) + (select count(*) from mission_attempts) + (select count(*) from insurance_coverages) + (select count(*) from insurance_activations)"));
    }

    // ---- audit ----

    [Theory]
    [InlineData("insurance_policy_publish")]
    [InlineData("insurance_activate")]
    [InlineData("mission_template_publish")]
    [InlineData("host_register_dev")]
    [InlineData("host_heartbeat_dev")]
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
    public async Task The_stage7_audit_actions_are_accepted_with_every_earlier_action(string action)
    {
        var account = await NewAccountAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into operator_actions (correlation_id, operator, action, account_id, reason, checks, result) values (gen_random_uuid(), 'op', @action, @account, 'r', '{}', '{}')", new { account, action });
        var e = await FailureOf(db.AsApp(),
            "insert into operator_actions (correlation_id, operator, action, reason, checks, result) values (gen_random_uuid(), 'op', 'host_register', 'r', '{}', '{}')");
        Assert.Equal(("23514", "operator_actions_action_check"), (e.SqlState, e.ConstraintName));
    }

    // ---- upgrade ----

    [Fact]
    public async Task Upgrading_a_stage6_database_to_0007_keeps_every_row()
    {
        await using var upgrade = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(upgrade.AsMigrator(), MigrationCatalog.Embedded);
        await migrator.MigrateAsync(6);
        Assert.Equal(0L, await AccountTestKit.ScalarAsSuperuserAsync<long>(upgrade,
            "select count(*) from pg_class where relname in ('mission_attempts', 'insurance_policies', 'mission_templates')"));

        // Stage 6 rows: two provisioned accounts with operatives, items and stacks; wallets with open, grant and regen entries
        // (all attempt-free); an anomaly and an audit row; and an unprovisioned account without a wallet.
        await using (var conn = await upgrade.AsApp().OpenConnectionAsync())
        {
            for (var i = 0; i < 2; i++)
            {
                var account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
                await conn.ExecuteAsync("insert into inventories (account_id) values (@account)", new { account });
                await conn.ExecuteAsync(
                    "update inventories set starter_granted = true, starter_catalogue_version = 'sha256:' || repeat('a', 64), starter_granted_at = now(), version = version + 1 where account_id = @account",
                    new { account });
                var operative = await conn.ExecuteScalarAsync<long>(
                    "insert into operatives (account_id, definition_id, origin, origin_ref) values (@account, '00000000-0000-4000-8000-000000000001', 'starter', @r) returning operative_id",
                    new { account, r = $"starter:op{i}" });
                await conn.ExecuteAsync(
                    "insert into item_instances (account_id, definition_id, operative_id, equipped_slot, origin, origin_ref) values (@account, 'item.rifle', @operative, 'weapon', 'starter', @r)",
                    new { account, operative, r = $"starter:item{i}" });
                await conn.ExecuteAsync("insert into item_stacks (account_id, definition_id, quantity) values (@account, 'item.kit', 5)", new { account });
                await using var tx = await conn.BeginTransactionAsync();
                await conn.ExecuteAsync("insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@account, 63, clock_timestamp(), 0, 1)", new { account }, tx);
                await conn.ExecuteAsync("insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@account, 'open', 50, 50, 'open')", new { account }, tx);
                await conn.ExecuteAsync("insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@account, 'dev_grant', 10, 60, @key)", new { account, key = "dev_grant:" + Guid.NewGuid() }, tx);
                await conn.ExecuteAsync("insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@account, 'regen', 3, 63, 'regen:1')", new { account }, tx);
                await conn.ExecuteAsync("insert into anomaly_events (kind, account_id, dedupe_key, detail) values ('probe', @account, 'd' || @account, '{}')", new { account }, tx);
                await conn.ExecuteAsync("insert into operator_actions (correlation_id, operator, action, account_id, reason, checks, result) values (gen_random_uuid(), 'op', 'energy_grant', @account, 'r', '{}', '{}')", new { account }, tx);
                await tx.CommitAsync();
            }
            await conn.ExecuteAsync("insert into accounts (status) values ('active')");
        }

        const string Counts = """
            select array[(select count(*) from accounts), (select count(*) from inventories), (select count(*) from operatives),
                   (select count(*) from item_instances), (select count(*) from item_stacks), (select count(*) from energy_wallets),
                   (select count(*) from energy_ledger), (select coalesce(sum(balance), 0) from energy_wallets),
                   (select count(*) from anomaly_events), (select count(*) from operator_actions), (select count(*) from energy_policies)]::bigint[]
            """;
        await using var su = await upgrade.AsSuperuser().OpenConnectionAsync();
        var beforeValues = await su.ExecuteScalarAsync<long[]>(Counts);
        Assert.Equal(new long[] { 3, 2, 2, 2, 2, 2, 6, 126, 2, 2, 1 }, beforeValues);
        var ledgerBefore = (await su.QueryAsync<string>("select entry_id || ':' || kind || ':' || delta || ':' || balance_after || ':' || idempotency_key from energy_ledger order by entry_id")).ToList();

        Assert.Equal([7], (await migrator.MigrateAsync(7)).Applied);

        Assert.Equal(beforeValues, await su.ExecuteScalarAsync<long[]>(Counts));
        Assert.Equal(ledgerBefore, (await su.QueryAsync<string>("select entry_id || ':' || kind || ':' || delta || ':' || balance_after || ':' || idempotency_key from energy_ledger order by entry_id")).ToList());
        Assert.Equal((1L, 1L, 0L, 0L, 0L, 0L, 0L), await su.QuerySingleAsync<(long, long, long, long, long, long, long)>("""
            select (select count(*) from insurance_policies), (select count(*) from mission_templates), (select count(*) from mission_hosts),
                   (select count(*) from mission_attempts), (select count(*) from insurance_coverages), (select count(*) from insurance_activations),
                   (select count(*) from deploy_requests)
            """));
        Assert.Empty(await InvariantRunner.RunAsync(upgrade.AsSuperuser(), 7, InvariantRunner.RealDirectory));

        // The upgraded database admits and releases like a fresh one.
        var first = await su.ExecuteScalarAsync<long>("select min(account_id) from energy_wallets");
        var attempt = Guid.CreateVersion7();
        await using (var conn = await upgrade.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(InsertAttempt, new { attempt, account = first, template = Template, salt = Bytes(32) }, tx);
            await AddParticipantAsync(conn, tx, attempt, first, 10);
            await tx.CommitAsync();
        }
        Assert.Equal(53, await su.ExecuteScalarAsync<int>("select balance from energy_wallets where account_id = @first", new { first }));
        Assert.Empty(await InvariantRunner.RunAsync(upgrade.AsSuperuser(), 7, InvariantRunner.RealDirectory));
    }

    // ---- invariants ----

    async Task<List<string>> InvariantKindsAsync(NpgsqlConnection? conn = null, NpgsqlTransaction? tx = null)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(InvariantRunner.RealDirectory, InvariantFile));
        var query = $"select kind from ({sql.TrimEnd().TrimEnd(';')}) v order by kind";
        if (conn is not null) return (await conn.QueryAsync<string>(query, transaction: tx)).ToList();
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        return (await su.QueryAsync<string>(query)).ToList();
    }

    /// <summary>Each fixture corrupts a consistent history as the superuser, in replica mode (no user or foreign-key
    /// trigger fires) and, where a CHECK or an index would refuse the row, after dropping it, all in one transaction that
    /// is rolled back after the invariant query has run inside it.</summary>
    [Theory]
    [InlineData("hold_state", new[] { "hold_state" })]
    [InlineData("hold_missing", new[] { "hold_state" })]
    [InlineData("reserve_missing", new[] { "hold_reserve" })]
    [InlineData("reserve_twice", new[] { "hold_reserve" })]
    [InlineData("reserve_amount", new[] { "hold_reserve" })]
    [InlineData("release_missing", new[] { "hold_release" })]
    [InlineData("release_unreleased", new[] { "hold_release" })]
    [InlineData("zero_with_entry", new[] { "hold_reserve" })]
    [InlineData("entry_without_hold", new[] { "entry_without_hold" })]
    [InlineData("two_active", new[] { "two_active_participations" })]
    [InlineData("template", new[] { "participant_template" })]
    [InlineData("terms", new[] { "insurance_terms" })]
    [InlineData("ended_active", new[] { "ended_attempt_active_participant" })]
    [InlineData("hold_amount", new[] { "hold_amount" })]
    public async Task Each_attempt_invariant_reports_its_violation(string fixture, string[] expectedKinds)
    {
        // A consistent history: a released positive attempt, an active positive attempt with insurance, an active zero-cost one.
        var released = await NewWalletAsync();
        var releasedAttempt = await AdmitAsync(released);
        await CancelAsync(releasedAttempt, released);
        var active = await NewWalletAsync();
        var activeAttempt = await AdmitAsync(active);
        var zero = await NewAccountAsync();
        var zeroAttempt = await AdmitAsync(zero, 0);
        var activation = Guid.CreateVersion7();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("""
                insert into insurance_activations (activation_id, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source, idempotency_key)
                values (@activation, @active, 'standard', 1, interval '1 day', null, clock_timestamp() + interval '1 day', 'dev_grant', gen_random_uuid())
                """, new { activation, active });
            await conn.ExecuteAsync("""
                insert into attempt_insurance (attempt_id, account_id, activation_id, policy_id, policy_version, terms)
                select @activeAttempt, @active, @activation, 'standard', 1, terms from insurance_policies where policy_id = 'standard' and version = 1
                """, new { activeAttempt, active, activation });
        }
        Assert.Empty(await InvariantKindsAsync());
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));

        const string Entry = "insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key) values ";
        var sql = fixture switch
        {
            // the participant was released, its hold wasn't
            "hold_state" => "update attempt_participants set state = 'released' where attempt_id = @zeroAttempt",
            "hold_missing" => "delete from energy_holds where attempt_id = @zeroAttempt",
            "reserve_missing" => "delete from energy_ledger where attempt_id = @activeAttempt and kind = 'reserve'",
            "reserve_twice" => "alter table energy_ledger drop constraint energy_ledger_idempotency_key_check; "
                + Entry + "(@active, @activeAttempt, 'reserve', -10, 30, 'reserve:' || @activeAttempt || ':2')",
            // the hold and its participant agree on 9, the reserve entry says 10
            "reserve_amount" => "update energy_holds set amount = 9 where attempt_id = @activeAttempt; "
                + "update attempt_participants set energy_cost = 9 where attempt_id = @activeAttempt",
            "release_missing" => "delete from energy_ledger where attempt_id = @releasedAttempt and kind = 'release'",
            // a release entry for a hold that is still reserved
            "release_unreleased" => Entry + "(@active, @activeAttempt, 'release', 10, 50, 'release:' || @activeAttempt)",
            "zero_with_entry" => Entry + "(@zero, @zeroAttempt, 'reserve', -1, 0, 'reserve:' || @zeroAttempt)",
            // the released account reserving against an attempt it doesn't take part in
            "entry_without_hold" => Entry + "(@released, @activeAttempt, 'reserve', -1, 49, 'reserve:' || @activeAttempt)",
            "two_active" => "drop index one_active_participation; "
                + InsertAttempt.Replace("@account", "@zero").Replace("@attempt", "@second") + "; "
                + InsertParticipant.Replace("@account", "@zero").Replace("@attempt", "@second").Replace("@amount", "0") + "; "
                + InsertHold.Replace("@account", "@zero").Replace("@attempt", "@second").Replace("@amount", "0"),
            "template" => InsertTemplate + "('procedural-standard', 2, 10, 1, 3); update attempt_participants set template_version = 2 where attempt_id = @zeroAttempt",
            "terms" => "update attempt_insurance set terms = jsonb_set(terms, '{progressXp,percent}', '41') where attempt_id = @activeAttempt",
            // a partial release: the attempt was cancelled, its participant (and hold) were not, so the inventory stays locked
            "ended_active" => "update mission_attempts set state = 'cancelled', cancel_reason = 'player_cancel', ended_at = clock_timestamp() where attempt_id = @activeAttempt",
            // the participant's recorded cost and its hold disagree (the reserve entry still matches the hold)
            "hold_amount" => "update attempt_participants set energy_cost = 9 where attempt_id = @activeAttempt",
            _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
        };

        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var tx = await su.BeginTransactionAsync();
            await su.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
            await su.ExecuteAsync(sql, new
            {
                released, releasedAttempt, active, activeAttempt, zero, zeroAttempt, second = Guid.CreateVersion7(),
                template = Template, salt = Bytes(32), snapshot = Snapshot,
            }, tx);
            Assert.Equal(expectedKinds, await InvariantKindsAsync(su, tx));
            await tx.RollbackAsync();
        }
        Assert.Empty(await InvariantKindsAsync());
    }

    [Fact]
    public async Task A_zero_amount_hold_needs_no_ledger_entry_and_a_positive_one_needs_exactly_one()
    {
        // Zero: admitted and released with no ledger entry at all, and no wallet.
        var zero = await NewAccountAsync();
        var zeroAttempt = await AdmitAsync(zero, 0);
        Assert.Empty(await InvariantKindsAsync());
        await CancelAsync(zeroAttempt, zero);
        Assert.Equal((0L, 0L), await SingleAsync<(long, long)>(
            "select (select count(*) from energy_ledger where account_id = @zero), (select count(*) from energy_wallets where account_id = @zero)", new { zero }));
        Assert.Empty(await InvariantKindsAsync());

        // Positive: exactly one reserve entry while reserved, and exactly one release entry once released.
        var positive = await NewWalletAsync();
        var attempt = await AdmitAsync(positive, 7);
        Assert.Equal(new[] { "open:50", "reserve:-7" }, (await QueryAsync<string>(
            "select kind || ':' || delta from energy_ledger where account_id = @positive order by entry_id", new { positive })).ToArray());
        Assert.Empty(await InvariantKindsAsync());
        await CancelAsync(attempt, positive);
        Assert.Equal(new[] { "open:50", "reserve:-7", "release:7" }, (await QueryAsync<string>(
            "select kind || ':' || delta from energy_ledger where account_id = @positive order by entry_id", new { positive })).ToArray());
        Assert.Empty(await InvariantKindsAsync());
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));

        // A positive hold written without its reservation (the commit checks are satisfied: no wallet write, no entry) is
        // reported, and so is a zero hold given an entry.
        var bare = await NewWalletAsync();
        var bareAttempt = Guid.CreateVersion7();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(InsertAttempt, new { attempt = bareAttempt, account = bare, template = Template, salt = Bytes(32) }, tx);
            await conn.ExecuteAsync(InsertParticipant, new { attempt = bareAttempt, account = bare, template = Template, snapshot = Snapshot, amount = 5 }, tx);
            await conn.ExecuteAsync(InsertHold, new { attempt = bareAttempt, account = bare, amount = 5 }, tx);
            await tx.CommitAsync();
        }
        Assert.Equal(["hold_reserve"], await InvariantKindsAsync());
        Assert.Equal([$"{InvariantFile}: 1 violating row(s)"], await AccountTestKit.InvariantFailuresAsync(db));
    }
}
