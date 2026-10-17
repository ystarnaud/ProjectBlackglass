using System.Security.Cryptography;
using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Migration 0008 (Stage 8; owner rulings B1-B13, plan revision 2 R1-R7) against a real PostgreSQL 17: the upgrade
/// from a populated schema 7, insert-only attempt secrets and their absence from constraint DETAILs, written-once and
/// recovery-only columns, the live-slot port index, the build allowlist, join tickets, the registered pool and objectives, the
/// exit evidence, the 0008 invariants, and 0000-0007 unchanged.</summary>
[Trait("Requires", "Postgres")]
public class SessionSchemaTests : IAsyncLifetime
{
    TestDatabase db = null!;
    const int Seed = 987654321;
    static readonly byte[] Salt = Enumerable.Range(0, 32).Select(i => (byte)(0xa0 + i % 16)).ToArray();

    public async Task InitializeAsync()
    {
        db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    // ---- helpers ----

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    /// <summary>An admitted solo attempt as admission writes it (zero cost), with known secrets.</summary>
    async Task<(long Account, Guid Attempt)> AdmitAsync(int seed = Seed, byte[]? salt = null)
    {
        var account = await NewAccountAsync();
        var attempt = Guid.CreateVersion7();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("""
            insert into mission_attempts (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline)
            values (@attempt, @account, 'procedural-standard', 1, 'sha256:' || repeat('ab', 32), clock_timestamp() + interval '60 seconds');
            insert into attempt_secrets (attempt_id, seed, retention_salt) values (@attempt, @seed, @salt);
            insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
            values (@attempt, @account, 'procedural-standard', 1, '{}', 1, 0);
            insert into energy_holds (attempt_id, account_id, amount) values (@attempt, @account, 0);
            """, new { attempt, account, seed, salt = salt ?? Salt }, tx);
        await tx.CommitAsync();
        return (account, attempt);
    }

    /// <summary>The 0008 invariant file's violations as kind:id.</summary>
    async Task<List<string>> SessionInvariantsAsync()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(InvariantRunner.RealDirectory, "0008_sessions.sql"));
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        return (await su.QueryAsync<string>($"select kind || ':' || id from ({sql.TrimEnd().TrimEnd(';')}) v order by 1")).ToList();
    }

    async Task<T> SuperScalarAsync<T>(string sql, object? args = null) => await AccountTestKit.ScalarAsSuperuserAsync<T>(db, sql, args);

    Task SuperExecuteAsync(string sql, object? args = null) => AccountTestKit.ExecuteAsSuperuserAsync(db, sql, args);

    static async Task<PostgresException> FailureOf(NpgsqlDataSource ds, string sql, object? args = null)
    {
        await using var conn = await ds.OpenConnectionAsync();
        return await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, args));
    }

    /// <summary>The app role on connections that receive the server's error DETAIL, which is exactly what the server log
    /// prints for the error at the default verbosity.</summary>
    NpgsqlDataSource AppWithDetail() => NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
    {
        Host = db.Host, Port = int.Parse(db.EnvFor("app")["BLACKGLASS_DB_PORT"]!, System.Globalization.CultureInfo.InvariantCulture),
        Database = db.Name, Username = "accounts_app", Password = db.Password("app"), IncludeErrorDetail = true, MaxPoolSize = 2,
    });

    static void AssertNoSecret(string? text)
    {
        Assert.False(string.IsNullOrEmpty(text));
        Assert.DoesNotContain(Seed.ToString(System.Globalization.CultureInfo.InvariantCulture), text);
        Assert.DoesNotContain(Convert.ToHexStringLower(Salt), text);
        Assert.DoesNotContain(Convert.ToBase64String(Salt), text);
    }

    // ---- upgrade and immutability of 0000-0007 ----

    [Fact]
    public async Task Migration_0008_applies_to_a_populated_0007_database_and_copies_every_secret()
    {
        await using var upgrade = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(upgrade.AsMigrator(), MigrationCatalog.Embedded);
        await migrator.MigrateAsync(7);
        await using (var conn = await upgrade.AsMigrator().OpenConnectionAsync())
            await conn.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Seeds", "0008.sql")));
        const string Secrets = "select attempt_id::text || ':' || seed || ':' || encode(retention_salt, 'hex') from {0} order by attempt_id";
        var before = (await (await upgrade.AsSuperuser().OpenConnectionAsync()).QueryAsync<string>(string.Format(Secrets, "mission_attempts"))).ToList();
        Assert.Equal(3, before.Count);

        Assert.Equal([8], (await migrator.MigrateAsync(8)).Applied);

        await using var su = await upgrade.AsSuperuser().OpenConnectionAsync();
        Assert.Equal(before, (await su.QueryAsync<string>(string.Format(Secrets, "attempt_secrets"))).ToList());
        Assert.Equal(0L, await su.ExecuteScalarAsync<long>(
            "select count(*) from information_schema.columns where table_name = 'mission_attempts' and column_name in ('seed', 'retention_salt')"));
        Assert.Equal(3L, await su.ExecuteScalarAsync<long>("select count(*) from mission_attempts where catalogue_version = 'sha256:' || repeat('ab', 32)"));
        Assert.Equal(["no_capacity", "player_cancel"], (await su.QueryAsync<string>(
            "select release_reason from attempt_participants where state = 'released' order by 1")).ToList());
        Assert.Empty(await InvariantRunner.RunAsync(upgrade.AsSuperuser(), 8, InvariantRunner.RealDirectory));
    }

    [Fact]
    public void Migrations_0000_to_0007_are_byte_identical()
    {
        string[] pinned =
        [
            "c8db82787c75b6d6c30c0dc891e95ddc41c3982cce2cb5f69541f1ec00f595e6", "7b3c8e63114a830bd6cdaf210ee0a38c1679ace021c4794c332d69cb9312358d",
            "ccae5c3664ca90539b2eb0ccbc3e653c5b721be8ae47486777b537f494e48d53", "8db2b1336f99ebcd85a58ac4f5a54f4e3739858d974d86a81eba32f48667e669",
            "20185b186bc0486f49566994e94cb5ff0fe1c044ba1e68e6332f24b62489624e", "55bc5edf9b8d99264b244d99106555b6f7ef0e00c639d7eee98e0148a07b11b9",
            "7a2bfbb3de2664bdafee878dd334c43696f89ce9c6c9fb10fafc64241ea400ff", "fe9f01840312c97e13ae5e79dcd7a6fb222b4aad6f456d8447d5475ed9773773",
        ];
        Assert.Equal(pinned, MigrationCatalog.Embedded.Scripts.Take(8).Select(s => Convert.ToHexStringLower(s.Checksum)));
        Assert.Equal(8, MigrationCatalog.Embedded.LatestVersion);
    }

    // ---- attempt secrets (B1) ----

    [Fact]
    public async Task Attempt_secrets_is_insert_only_for_the_app_and_for_its_owner()
    {
        var (_, attempt) = await AdmitAsync();
        foreach (var sql in new[] { "update attempt_secrets set seed = 1", "delete from attempt_secrets", "truncate attempt_secrets" })
            Assert.Equal("42501", (await FailureOf(db.AsApp(), sql)).SqlState);
        foreach (var sql in new[] { "update attempt_secrets set seed = 1", "delete from attempt_secrets", "truncate attempt_secrets" })
            Assert.Equal(("23514", "attempt_secret_is_immutable"), ((await FailureOf(db.AsMigrator(), sql)).SqlState, (await FailureOf(db.AsMigrator(), sql)).ConstraintName));
        Assert.Equal(Seed, await SuperScalarAsync<int>("select seed from attempt_secrets where attempt_id = @attempt", new { attempt }));
    }

    [Fact]
    public async Task A_check_violation_on_mission_attempts_does_not_disclose_the_secrets()
    {
        var (_, attempt) = await AdmitAsync();
        await using var app = AppWithDetail();
        var e = await FailureOf(app, "update mission_attempts set state = 'cancelled', ended_at = clock_timestamp() where attempt_id = @attempt", new { attempt });
        Assert.Equal(("23514", "mission_attempt_cancel_reason_iff_cancelled"), (e.SqlState, e.ConstraintName));
        Assert.Contains(attempt.ToString(), e.Detail); // the DETAIL is the failing row (what the server log prints)...
        AssertNoSecret(e.Detail);                      // ...which no longer holds the seed or the salt
        AssertNoSecret(e.Message + e.MessageText);
    }

    [Fact]
    public async Task A_check_violation_on_attempt_secrets_discloses_only_the_rejected_values()
    {
        await AdmitAsync();
        var (_, other) = await AdmitAsync(seed: 1, salt: new byte[32]);
        await using var app = AppWithDetail();
        // A second row for an attempt (the primary key) and a malformed salt (the CHECK): neither DETAIL names another attempt's secrets.
        var duplicate = await FailureOf(app, "insert into attempt_secrets (attempt_id, seed, retention_salt) values (@other, 5, decode(repeat('00', 32), 'hex'))", new { other });
        Assert.Equal("23505", duplicate.SqlState);
        AssertNoSecret(duplicate.Detail);
        var malformed = await FailureOf(app, "insert into attempt_secrets (attempt_id, seed, retention_salt) values (@other, 5, '\\x00')", new { other });
        Assert.Equal(("23514", "attempt_secrets_retention_salt_check"), (malformed.SqlState, malformed.ConstraintName));
        AssertNoSecret(malformed.Detail);
    }

    [Fact]
    public async Task Every_attempt_needs_its_secrets_row_and_the_invariant_reports_one_missing()
    {
        var account = await NewAccountAsync();
        var attempt = Guid.CreateVersion7();
        await SuperExecuteAsync("""
            insert into mission_attempts (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline)
            values (@attempt, @account, 'procedural-standard', 1, 'sha256:' || repeat('ab', 32), clock_timestamp() + interval '60 seconds')
            """, new { attempt, account });
        Assert.Contains($"attempt_secrets_missing:{attempt}", await SessionInvariantsAsync());
    }

    // ---- attempt columns: written once, recovery-only credential, state CHECKs ----

    public static TheoryData<string, string> WrittenOnce => new()
    {
        { "ready", "host_id = 'other-host'" },
        { "ready", "mission_port = mission_port + 1" },
        { "ready", "build_revision = 'other'" },
        { "ready", "claim_key = gen_random_uuid()" },
        { "ready", "launch_deadline = launch_deadline + interval '1 second'" },
        { "ready", "process_session_id = gen_random_uuid()" },
        { "ready", "ready_at = ready_at + interval '1 second'" },
        { "ready", "join_deadline = join_deadline + interval '1 second'" },
        { "ready", "layout_hash = 'ffffffffffffffff'" },
        { "ready", "loot_table_version = 2" },
        { "ready", "loot_hash = 'sha256:' || repeat('f', 64)" },
        { "running", "started_at = started_at + interval '1 second'" },
        { "ready", "catalogue_version = 'sha256:' || repeat('cd', 32)" },
        // The credential: replaced only by a recovery while assigned (R1), never once launching.
        { "launching", "process_credential_hash = decode(repeat('ee', 32), 'hex'), credential_generation = credential_generation + 1" },
        { "ready", "process_credential_hash = null, credential_generation = null" },
    };

    [Theory]
    [MemberData(nameof(WrittenOnce))]
    public async Task Written_once_columns_never_change(string state, string set)
    {
        var (_, attempt) = await AdmitAsync();
        await SuperExecuteAsync("insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, port_base) values ('other-host', 'agent', decode(repeat('09', 32), 'hex'), 1, true, 9000)");
        foreach (var s in new[] { "assigned", "launching", "ready", "running" }.TakeWhile((_, i) => i <= Array.IndexOf(new[] { "assigned", "launching", "ready", "running" }, state)))
            await AttemptFixtures.MoveAsync(db, attempt, s);
        var e = await FailureOf(db.AsMigrator(), $"update mission_attempts set {set} where attempt_id = @attempt", new { attempt }); // the owner: the trigger, not a grant
        Assert.Equal(("23514", "mission_attempt_transition"), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task A_recovery_replaces_the_credential_while_assigned_with_the_next_generation_only()
    {
        var (_, attempt) = await AdmitAsync();
        await AttemptFixtures.MoveAsync(db, attempt, "assigned");
        const string Rotate = "update mission_attempts set process_credential_hash = @hash, credential_generation = credential_generation + @step where attempt_id = @attempt";
        var skip = await FailureOf(db.AsApp(), Rotate, new { attempt, hash = RandomNumberGenerator.GetBytes(32), step = 2 });
        Assert.Equal("mission_attempt_transition", skip.ConstraintName);
        var same = await FailureOf(db.AsApp(), Rotate, new { attempt, hash = RandomNumberGenerator.GetBytes(32), step = 0 });
        Assert.Equal("mission_attempt_transition", same.ConstraintName);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            Assert.Equal(1, await conn.ExecuteAsync(Rotate, new { attempt, hash = RandomNumberGenerator.GetBytes(32), step = 1 }));
        Assert.Equal(2, await SuperScalarAsync<int>("select credential_generation from mission_attempts where attempt_id = @attempt", new { attempt }));
        // Rotating while moving to launching is not a recovery either.
        var moving = await FailureOf(db.AsApp(),
            "update mission_attempts set state = 'launching', process_credential_hash = @hash, credential_generation = 3 where attempt_id = @attempt",
            new { attempt, hash = RandomNumberGenerator.GetBytes(32) });
        Assert.Equal("mission_attempt_transition", moving.ConstraintName);
    }

    public static TheoryData<string, string> StateChecks => new()
    {
        // The claim's columns together, exactly from assigned on.
        { "admitted", "host_id = 'fixture-host'" },
        { "assigned", "claim_key = null" },
        { "assigned", "launch_deadline = null" },
        // A session only from launching on; ready's columns together, exactly from ready on.
        { "assigned", "process_session_id = gen_random_uuid()" },
        { "ready", "loot_hash = null" },
        { "launching", "ready_at = clock_timestamp()" },
        // started_at exactly from running on; a process heartbeat only with a session.
        { "ready", "started_at = clock_timestamp()" },
        { "assigned", "last_process_heartbeat_at = clock_timestamp()" },
        { "admitted", "slot_released_at = clock_timestamp()" },
        { "assigned", "mission_port = 80" },
        { "ready", "layout_hash = 'CF2E14DECE9CC2A6'" },
    };

    [Theory]
    [MemberData(nameof(StateChecks))]
    public async Task The_attempt_state_checks_hold_even_without_triggers(string state, string set)
    {
        var (_, attempt) = await AdmitAsync();
        foreach (var s in new[] { "assigned", "launching", "ready" }.TakeWhile((_, i) => i <= Array.IndexOf(new[] { "assigned", "launching", "ready" }, state)))
            await AttemptFixtures.MoveAsync(db, attempt, s);
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await su.BeginTransactionAsync();
        await su.ExecuteAsync("set local session_replication_role = replica", transaction: tx); // no trigger: the CHECKs alone
        var e = await Assert.ThrowsAsync<PostgresException>(() => su.ExecuteAsync($"update mission_attempts set {set} where attempt_id = @attempt", new { attempt }, tx));
        Assert.Equal("23514", e.SqlState);
        Assert.StartsWith("mission_attempt", e.ConstraintName);
    }

    [Fact]
    public async Task Cancelled_attempts_take_the_new_reasons_and_running_never_cancels()
    {
        foreach (var reason in new[] { "launch_timeout", "join_timeout", "process_exited", "process_failed", "invalid_pool" })
        {
            var (_, attempt) = await AdmitAsync();
            await AttemptFixtures.MoveAsync(db, attempt, "assigned");
            await using var conn = await db.AsApp().OpenConnectionAsync();
            Assert.Equal(1, await conn.ExecuteAsync("update mission_attempts set state = 'cancelled', cancel_reason = @reason, ended_at = clock_timestamp() where attempt_id = @attempt", new { attempt, reason }));
        }
        var (_, running) = await AdmitAsync();
        foreach (var s in new[] { "assigned", "launching", "ready", "running" }) await AttemptFixtures.MoveAsync(db, running, s);
        var e = await FailureOf(db.AsApp(), "update mission_attempts set state = 'cancelled', cancel_reason = 'process_exited', ended_at = clock_timestamp() where attempt_id = @running", new { running });
        Assert.Equal("mission_attempt_transition", e.ConstraintName);
        var (_, admitted) = await AdmitAsync();
        var bad = await FailureOf(db.AsApp(), "update mission_attempts set state = 'cancelled', cancel_reason = 'launch_failed', ended_at = clock_timestamp() where attempt_id = @admitted", new { admitted });
        Assert.Equal("23514", bad.SqlState);
    }

    // ---- slots and builds ----

    [Fact]
    public async Task One_port_per_live_slot_refuses_a_clash_and_allows_reuse_after_release()
    {
        var (_, first) = await AdmitAsync();
        var (_, second) = await AdmitAsync();
        await AttemptFixtures.MoveAsync(db, first, "assigned");
        await AttemptFixtures.MoveAsync(db, second, "assigned");
        var port = await SuperScalarAsync<int>("select mission_port from mission_attempts where attempt_id = @first", new { first });
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        // The second attempt's slot can't take the first's port while both are live (replica mode: the index alone, no trigger),
        // even once the second attempt is cancelled: a cancelled attempt's slot stays live until its release is confirmed (B6).
        await AttemptFixtures.MoveAsync(db, second, "cancelled");
        var e = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var tx = await su.BeginTransactionAsync();
            await su.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
            await su.ExecuteAsync("update mission_attempts set mission_port = @port where attempt_id = @second", new { port, second }, tx);
        });
        Assert.Equal(("23505", "one_port_per_live_slot"), (e.SqlState, e.ConstraintName));
        // Released once (written once), the port is free for another live slot.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            Assert.Equal(1, await conn.ExecuteAsync("update mission_attempts set slot_released_at = clock_timestamp() where attempt_id = @first", new { first }));
            var again = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("update mission_attempts set slot_released_at = clock_timestamp() where attempt_id = @first", new { first }));
            Assert.Equal("mission_attempt_transition", again.ConstraintName);
        }
        await using (var tx = await su.BeginTransactionAsync())
        {
            await su.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
            Assert.Equal(1, await su.ExecuteAsync("update mission_attempts set mission_port = @port where attempt_id = @second", new { port, second }, tx));
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task Builds_are_permanent_with_immutable_identities_and_insert_only_templates()
    {
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("""
                insert into allowed_builds (build_revision, catalogue_version) values ('r1', 'sha256:' || repeat('ab', 32));
                insert into allowed_build_templates (build_revision, catalogue_version, template_id, template_version) values ('r1', 'sha256:' || repeat('ab', 32), 'procedural-standard', 1);
                update allowed_builds set enabled = false;
                """);
        }
        Assert.False(await SuperScalarAsync<bool>("select enabled from allowed_builds"));
        foreach (var (sql, constraint) in new[]
                 {
                     ("update allowed_builds set build_revision = 'r2'", "allowed_build_change"),
                     ("delete from allowed_builds", "allowed_build_is_permanent"),
                     ("truncate allowed_builds cascade", "allowed_build_is_permanent"),
                     ("update allowed_build_templates set template_version = 2", "allowed_build_template_is_immutable"),
                     ("delete from allowed_build_templates", "allowed_build_template_is_immutable"),
                 })
            Assert.Equal(("23514", constraint), ((await FailureOf(db.AsMigrator(), sql)).SqlState, (await FailureOf(db.AsMigrator(), sql)).ConstraintName));
        Assert.Equal("23514", (await FailureOf(db.AsApp(), "insert into allowed_builds (build_revision, catalogue_version) values ('-bad', 'sha256:' || repeat('ab', 32))")).SqlState);
        Assert.Equal("23514", (await FailureOf(db.AsApp(), "insert into allowed_builds (build_revision, catalogue_version) values ('r3', 'sha256:AB')")).SqlState);
        Assert.Equal(("23503", "allowed_build_templates_template_fkey"), ((await FailureOf(db.AsApp(),
            "insert into allowed_build_templates (build_revision, catalogue_version, template_id, template_version) values ('r1', 'sha256:' || repeat('ab', 32), 'procedural-standard', 9)")) is var t ? (t.SqlState, t.ConstraintName) : default));
        // An attempt's build must be allowlisted (the foreign key), for its own catalogue.
        var (_, attempt) = await AdmitAsync();
        var e = await FailureOf(db.AsSuperuser(), $"update mission_attempts set {AttemptFixtures.StateSet("assigned").Replace(AttemptFixtures.FixtureBuild, "unlisted")} where attempt_id = @attempt", new { attempt });
        Assert.Equal("23503", e.SqlState);
    }

    [Fact]
    public async Task An_agent_host_has_a_port_range_inside_the_port_space()
    {
        const string Insert = "insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, port_base) values (@host, 'agent', @hash, @capacity, true, @port)";
        foreach (var (port, capacity, constraint) in new (int?, int, string)[] { (null, 1, "mission_host_agent_ports"), (1023, 1, "mission_host_port_range"), (65535, 2, "mission_host_port_range") })
        {
            var e = await FailureOf(db.AsApp(), Insert, new { host = "h", hash = RandomNumberGenerator.GetBytes(32), capacity, port });
            Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
        }
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync(Insert, new { host = "h", hash = RandomNumberGenerator.GetBytes(32), capacity = 1, port = 65535 });
        var reported = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("update mission_hosts set build_revision = 'r1'"));
        Assert.Equal("mission_host_reported_build", reported.ConstraintName);
    }

    // ---- tickets, pool, objectives, exit evidence ----

    [Fact]
    public async Task Join_tickets_redeem_once_by_their_own_attempts_session_and_never_delete()
    {
        var (account, attempt) = await AdmitAsync();
        var (_, other) = await AdmitAsync();
        foreach (var s in new[] { "assigned", "launching", "ready" }) { await AttemptFixtures.MoveAsync(db, attempt, s); await AttemptFixtures.MoveAsync(db, other, s); }
        var session = await SuperScalarAsync<Guid>("select process_session_id from mission_attempts where attempt_id = @attempt", new { attempt });
        var foreign = await SuperScalarAsync<Guid>("select process_session_id from mission_attempts where attempt_id = @other", new { other });
        var hash = RandomNumberGenerator.GetBytes(32);
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into join_tickets (ticket_hash, attempt_id, account_id, expires_at) values (@hash, @attempt, @account, clock_timestamp() + interval '120 seconds')", new { hash, attempt, account });
        var wrong = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "update join_tickets set redeemed_at = clock_timestamp(), redeemed_by_session = @foreign where ticket_hash = @hash", new { hash, foreign }));
        Assert.Equal(("23503", "join_tickets_session_fkey"), (wrong.SqlState, wrong.ConstraintName));
        Assert.Equal(1, await conn.ExecuteAsync("update join_tickets set redeemed_at = clock_timestamp(), redeemed_by_session = @session where ticket_hash = @hash", new { hash, session }));
        foreach (var set in new[] { "redeemed_at = clock_timestamp()", "revoked_at = clock_timestamp()", "expires_at = expires_at + interval '1 second'" })
        {
            var e = await FailureOf(db.AsMigrator(), $"update join_tickets set {set} where ticket_hash = @hash", new { hash });
            Assert.Equal("23514", e.SqlState);
            Assert.Contains(e.ConstraintName, new[] { "join_ticket_change", "join_ticket_single_outcome" }); // redeemed once; never also revoked
        }
        Assert.Equal("42501", (await FailureOf(db.AsApp(), "delete from join_tickets")).SqlState);
        Assert.Equal("join_ticket_is_permanent", (await FailureOf(db.AsMigrator(), "delete from join_tickets")).ConstraintName);
        var expired = await FailureOf(db.AsApp(), "insert into join_tickets (ticket_hash, attempt_id, account_id, expires_at) values (@h, @attempt, @account, statement_timestamp())", new { h = RandomNumberGenerator.GetBytes(32), attempt, account });
        Assert.Equal("join_ticket_expiry", expired.ConstraintName);
    }

    [Fact]
    public async Task Objectives_and_pool_are_insert_only()
    {
        var (_, attempt) = await AdmitAsync();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("""
                insert into attempt_objectives (attempt_id, objective_id, required, ordinal) values (@attempt, 'extract', true, 0);
                insert into attempt_loot_pool (attempt_id, container_index, line, definition_id, quantity) values (@attempt, 0, 0, 'item.kit', 2);
                """, new { attempt });
        foreach (var sql in new[] { "update attempt_objectives set required = false", "delete from attempt_loot_pool", "update attempt_loot_pool set quantity = 9" })
            Assert.Equal("42501", (await FailureOf(db.AsApp(), sql)).SqlState);
        foreach (var (sql, constraint) in new[]
                 {
                     ("update attempt_objectives set required = false", "attempt_objective_is_immutable"),
                     ("truncate attempt_objectives", "attempt_objective_is_immutable"),
                     ("update attempt_loot_pool set quantity = 9", "attempt_loot_pool_is_immutable"),
                     ("truncate attempt_loot_pool", "attempt_loot_pool_is_immutable"),
                 })
            Assert.Equal(constraint, (await FailureOf(db.AsMigrator(), sql)).ConstraintName);
        Assert.Equal("23514", (await FailureOf(db.AsApp(), "insert into attempt_loot_pool (attempt_id, container_index, line, definition_id, quantity) values (@attempt, 0, 1, 'item.kit', 0)", new { attempt })).SqlState);
    }

    [Fact]
    public async Task Exit_reports_are_insert_only_and_results_name_participants()
    {
        var (account, attempt) = await AdmitAsync();
        foreach (var s in new[] { "assigned", "launching", "ready" }) await AttemptFixtures.MoveAsync(db, attempt, s);
        var stranger = await NewAccountAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("""
            insert into attempt_exit_reports (attempt_id, source, host_id, attempt_state, any_started, exit_code, signal, outcome)
            values (@attempt, 'agent', 'fixture-host', 'ready', false, null, 'SIGKILL', 'cancelled');
            insert into attempt_exit_results (attempt_id, account_id, present) values (@attempt, @account, false);
            """, new { attempt, account });
        var foreign = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "insert into attempt_exit_results (attempt_id, account_id, present) values (@attempt, @stranger, true)", new { attempt, stranger }));
        Assert.Equal(("23503", "attempt_exit_results_participant_fkey"), (foreign.SqlState, foreign.ConstraintName));
        var second = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "insert into attempt_exit_reports (attempt_id, source, host_id, attempt_state, any_started, exit_code, outcome) values (@attempt, 'agent', 'fixture-host', 'ready', false, 0, 'none')", new { attempt }));
        Assert.Equal("23505", second.SqlState);
        var shape = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "insert into attempt_exit_reports (attempt_id, source, host_id, attempt_state, any_started, reason, outcome) values (@attempt, 'process', 'fixture-host', 'ready', false, 'oom', 'none')", new { attempt }));
        Assert.Equal("attempt_exit_report_source_shape", shape.ConstraintName);
        foreach (var sql in new[] { "update attempt_exit_reports set outcome = 'none'", "delete from attempt_exit_results" })
            Assert.Equal("42501", (await FailureOf(db.AsApp(), sql)).SqlState);
        Assert.Equal("attempt_exit_report_is_immutable", (await FailureOf(db.AsMigrator(), "update attempt_exit_reports set outcome = 'none'")).ConstraintName);
    }

    // ---- the charge key and the invariants ----

    [Fact]
    public async Task A_charged_positive_hold_needs_exactly_one_charge_entry_and_a_started_participant_a_charged_hold()
    {
        var (account, attempt) = await AdmitAsync();
        foreach (var s in new[] { "assigned", "launching", "ready", "running" }) await AttemptFixtures.MoveAsync(db, attempt, s);
        await SuperExecuteAsync("update attempt_participants set state = 'joined', started_at = clock_timestamp() where attempt_id = @attempt", new { attempt });
        Assert.Contains($"started_participant:{attempt}/{account}", await SessionInvariantsAsync());
        await SuperExecuteAsync("update energy_holds set state = 'charged' where attempt_id = @attempt", new { attempt });
        Assert.Empty(await SessionInvariantsAsync()); // a zero-amount hold charges with no entry (B9)
    }

    [Fact]
    public async Task The_invariants_catch_a_wrong_loot_hash_a_missing_objective_and_an_unlisted_build()
    {
        var (_, attempt) = await AdmitAsync();
        foreach (var s in new[] { "assigned", "launching", "ready" }) await AttemptFixtures.MoveAsync(db, attempt, s);
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await su.BeginTransactionAsync();
        await su.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
        await su.ExecuteAsync("insert into attempt_loot_pool (attempt_id, container_index, line, definition_id, quantity) values (@attempt, 0, 0, 'item.kit', 1)", new { attempt }, tx);
        await su.ExecuteAsync("delete from allowed_build_templates", transaction: tx);
        var sql = await File.ReadAllTextAsync(Path.Combine(InvariantRunner.RealDirectory, "0008_sessions.sql"));
        var kinds = (await su.QueryAsync<string>($"select kind || ':' || id from ({sql.TrimEnd().TrimEnd(';')}) v order by 1", transaction: tx)).ToList();
        Assert.Equal([$"build_not_allowed:{attempt}", $"loot_hash:{attempt}"], kinds);
        await tx.RollbackAsync();
    }
}
