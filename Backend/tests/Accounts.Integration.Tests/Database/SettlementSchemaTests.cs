using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Migration 0009 (Stage 9; plan revision 3 §D) against a real PostgreSQL 17: the catalogue history and its foreign
/// key, insert-only submissions and payloads, immutable settlements, growing pool collections, the participant and hold edges,
/// permanent host loss, exit evidence marks and outbox observations, the exact settle and refund keys, anomaly reviews, the
/// 0009 invariants, secrecy of constraint DETAILs, and the upgrade from a populated schema 8.</summary>
[Trait("Requires", "Postgres")]
public class SettlementSchemaTests : IAsyncLifetime
{
    TestDatabase db = null!;

    public async Task InitializeAsync()
    {
        db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();
        await AttemptFixtures.RegisterTestCatalogueAsync(db);
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    // ---- helpers ----

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    /// <summary>A running attempt with one joined participant (zero-cost hold, charged).</summary>
    async Task<(long Account, Guid Attempt)> StartedAsync()
    {
        var account = await NewAccountAsync();
        var attempt = await AttemptFixtures.AdmitAsync(db, account);
        await AttemptFixtures.StartAsync(db, attempt, account);
        return (account, attempt);
    }

    Task Super(string sql, object? args = null) => AccountTestKit.ExecuteAsSuperuserAsync(db, sql, args);

    Task<T> SuperScalar<T>(string sql, object? args = null) => AccountTestKit.ScalarAsSuperuserAsync<T>(db, sql, args);

    static async Task<PostgresException> FailureOf(NpgsqlDataSource ds, string sql, object? args = null)
    {
        await using var conn = await ds.OpenConnectionAsync();
        return await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, args));
    }

    static byte[] Sha(byte[] b) => SHA256.HashData(b);

    static string Hash(byte[] b) => "sha256:" + Convert.ToHexStringLower(Sha(b));

    /// <summary>Inserts a submission and its payload as the app writes them; returns the id.</summary>
    async Task<long> SubmitAsync(Guid attempt, long account, byte[] raw, string disposition = "quarantined", byte[]? canonical = null)
    {
        canonical ??= raw;
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var id = await conn.ExecuteScalarAsync<long>("""
            insert into result_submissions (source, host_id, attempt_id, account_id, attempt_revoked, raw_too_large, raw_length, raw_hash, manifest_hash, disposition)
            values ('agent', 'fixture-host', @attempt, @account, false, false, @length, @rawHash, @manifestHash, @disposition) returning submission_id
            """, new { attempt, account, length = raw.Length, rawHash = Sha(raw), manifestHash = Hash(canonical), disposition }, tx);
        await conn.ExecuteAsync("insert into result_submission_payloads (submission_id, raw_bytes, canonical_bytes) values (@id, @raw, @canonical)",
            new { id, raw, canonical }, tx);
        await tx.CommitAsync();
        return id;
    }

    async Task<List<string>> SettlementInvariantsAsync()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(InvariantRunner.RealDirectory, "0009_settlement.sql"));
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        return (await su.QueryAsync<string>($"select kind || ':' || id from ({sql.TrimEnd().TrimEnd(';')}) v order by 1")).ToList();
    }

    // ---- objects ----

    [Fact]
    public async Task Every_0009_object_exists()
    {
        foreach (var table in new[] { "catalogue_versions", "result_submissions", "result_submission_payloads", "participant_settlements", "pool_collections",
                     "attempt_outbox_observations", "attempt_outbox_observation_results" })
            Assert.True(await SuperScalar<bool>("select to_regclass(@table) is not null", new { table }), table);
        foreach (var (table, column) in new[] { ("attempt_participants", "quarantined_at"), ("attempt_participants", "escalate_at"), ("attempt_participants", "quarantine_reason"),
                     ("attempt_participants", "recorded_manifest_hash"), ("mission_hosts", "declared_lost_at"), ("attempt_exit_reports", "outbox_complete"),
                     ("attempt_exit_reports", "attempt_revoked") })
            Assert.True(await SuperScalar<bool>("select exists (select 1 from information_schema.columns where table_name = @table and column_name = @column)", new { table, column }), $"{table}.{column}");
        Assert.Equal(9, MigrationCatalog.Embedded.LatestVersion);
    }

    [Fact]
    public void Migrations_0000_to_0008_are_byte_identical()
    {
        string[] pinned =
        [
            "c8db82787c75b6d6c30c0dc891e95ddc41c3982cce2cb5f69541f1ec00f595e6", "7b3c8e63114a830bd6cdaf210ee0a38c1679ace021c4794c332d69cb9312358d",
            "ccae5c3664ca90539b2eb0ccbc3e653c5b721be8ae47486777b537f494e48d53", "8db2b1336f99ebcd85a58ac4f5a54f4e3739858d974d86a81eba32f48667e669",
            "20185b186bc0486f49566994e94cb5ff0fe1c044ba1e68e6332f24b62489624e", "55bc5edf9b8d99264b244d99106555b6f7ef0e00c639d7eee98e0148a07b11b9",
            "7a2bfbb3de2664bdafee878dd334c43696f89ce9c6c9fb10fafc64241ea400ff", "fe9f01840312c97e13ae5e79dcd7a6fb222b4aad6f456d8447d5475ed9773773",
            "c56bfd8cae6e4fdc3f65bd3517b1f4a42791147e6ba7383f468da2ac1b02184a",
        ];
        Assert.Equal(pinned, MigrationCatalog.Embedded.Scripts.Take(9).Select(s => Convert.ToHexStringLower(s.Checksum)));
    }

    // ---- catalogue history ----

    [Fact]
    public async Task The_catalogue_history_proves_its_hash_and_never_changes()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"x\":1}\n");
        Assert.Equal("catalogue_version_is_hash", (await FailureOf(db.AsApp(), "insert into catalogue_versions (version, bytes) values (@v, @bytes)",
            new { v = "sha256:" + new string('0', 64), bytes })).ConstraintName);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("insert into catalogue_versions (version, bytes) values (@v, @bytes)", new { v = Hash(bytes), bytes });
        foreach (var sql in new[] { "update catalogue_versions set registered_at = now() where version = @v", "delete from catalogue_versions where version = @v" })
            Assert.Equal("catalogue_version_is_immutable", (await FailureOf(db.AsSuperuser(), sql, new { v = Hash(bytes) })).ConstraintName);
        Assert.Equal("catalogue_version_is_immutable", (await FailureOf(db.AsSuperuser(), "truncate catalogue_versions cascade")).ConstraintName);
    }

    [Fact]
    public async Task A_new_attempt_under_an_unregistered_catalogue_is_refused()
    {
        var account = await NewAccountAsync();
        var e = await FailureOf(db.AsApp(), """
            insert into mission_attempts (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline)
            values (@attempt, @account, 'procedural-standard', 1, @v, clock_timestamp() + interval '60 seconds')
            """, new { attempt = Guid.CreateVersion7(), account, v = "sha256:" + new string('e', 64) });
        Assert.Equal(("23503", "mission_attempts_catalogue_version_fkey"), (e.SqlState, e.ConstraintName));
    }

    // ---- submissions ----

    [Fact]
    public async Task Submissions_only_count_repeats_and_distinct_payloads_get_their_own_rows()
    {
        var (account, attempt) = await StartedAsync();
        await Super("insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, port_base) values ('fixture-host', 'agent', sha256('x'::bytea), 4, true, 7000) on conflict do nothing");
        var first = await SubmitAsync(attempt, account, Encoding.UTF8.GetBytes("{\"a\":1}"));
        var second = await SubmitAsync(attempt, account, Encoding.UTF8.GetBytes("{ \"a\" : 1 }"), canonical: Encoding.UTF8.GetBytes("{\"a\":1}"));
        Assert.NotEqual(first, second);
        Assert.Equal("23505", (await FailureOf(db.AsApp(), """
            insert into result_submissions (source, host_id, attempt_id, account_id, attempt_revoked, raw_too_large, raw_length, raw_hash, manifest_hash, disposition)
            values ('agent', 'fixture-host', @attempt, @account, false, false, 7, @h, null, 'malformed')
            """, new { attempt, account, h = Sha(Encoding.UTF8.GetBytes("{\"a\":1}")) })).SqlState);

        await using (var conn = await db.AsApp().OpenConnectionAsync())
            Assert.Equal(1, await conn.ExecuteAsync("update result_submissions set receive_count = receive_count + 1 where submission_id = @first", new { first }));
        Assert.Equal(2, await SuperScalar<int>("select receive_count from result_submissions where submission_id = @first", new { first }));
        Assert.True(await SuperScalar<bool>("select last_received_at >= first_received_at from result_submissions where submission_id = @first", new { first }));
        foreach (var sql in new[] { "update result_submissions set receive_count = 1 where submission_id = @first", "update result_submissions set disposition = 'settled' where submission_id = @first",
                     "update result_submissions set codes = '{V4.build}' where submission_id = @first", "delete from result_submissions where submission_id = @first",
                     "update result_submission_payloads set raw_bytes = 'x'::bytea where submission_id = @first", "delete from result_submission_payloads where submission_id = @first" })
            Assert.Equal("23514", (await FailureOf(db.AsSuperuser(), sql, new { first })).SqlState);
        Assert.Equal("42501", (await FailureOf(db.AsApp(), "update result_submissions set disposition = 'settled' where submission_id = @first", new { first })).SqlState);
    }

    [Fact]
    public async Task A_payload_must_match_its_submissions_length_and_hashes()
    {
        var (account, attempt) = await StartedAsync();
        await Super("insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, port_base) values ('fixture-host', 'agent', sha256('x'::bytea), 4, true, 7000) on conflict do nothing");
        var raw = Encoding.UTF8.GetBytes("{\"b\":2}");
        await using var conn = await db.AsApp().OpenConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>("""
            insert into result_submissions (source, host_id, attempt_id, account_id, attempt_revoked, raw_too_large, raw_length, raw_hash, manifest_hash, disposition)
            values ('agent', 'fixture-host', @attempt, @account, false, false, @length, @h, @m, 'quarantined') returning submission_id
            """, new { attempt, account, length = raw.Length, h = Sha(raw), m = Hash(raw) });
        var e = await FailureOf(db.AsApp(), "insert into result_submission_payloads (submission_id, raw_bytes, canonical_bytes) values (@id, @other, @raw)",
            new { id, other = Encoding.UTF8.GetBytes("{\"b\":3}"), raw });
        Assert.Equal("result_submission_payload_matches", e.ConstraintName);
        Assert.DoesNotContain("\"b\"", e.Message + e.Detail, StringComparison.Ordinal);
        Assert.Equal("result_submission_payload_matches", (await FailureOf(db.AsApp(),
            "insert into result_submission_payloads (submission_id, raw_bytes, canonical_bytes) values (@id, @raw, null)", new { id, raw })).ConstraintName);
    }

    // ---- settlements, pool collections ----

    [Fact]
    public async Task A_settlement_row_is_immutable_and_shaped_by_its_outcome()
    {
        var (account, attempt) = await StartedAsync();
        var e = await FailureOf(db.AsSuperuser(), """
            insert into participant_settlements (attempt_id, account_id, outcome, catalogue_version, receipt, settled_at)
            values (@attempt, @account, 'success', @v, '{}', now())
            """, new { attempt, account, v = AttemptFixtures.TestCatalogueVersion });
        Assert.Equal("participant_settlement_shape", e.ConstraintName);
        await Super("""
            insert into participant_settlements (attempt_id, account_id, outcome, void_basis, catalogue_version, receipt, settled_at)
            values (@attempt, @account, 'infra_void', 'exit_evidence', @v, '{}', now())
            """, new { attempt, account, v = AttemptFixtures.TestCatalogueVersion });
        foreach (var sql in new[] { "update participant_settlements set receipt = '{ }'", "delete from participant_settlements", "truncate participant_settlements" })
            Assert.Equal("participant_settlement_is_immutable", (await FailureOf(db.AsSuperuser(), sql)).ConstraintName);
    }

    [Fact]
    public async Task Pool_collections_only_grow_and_never_pass_the_pool_line()
    {
        var (_, attempt) = await StartedAsync();
        await Super("insert into attempt_loot_pool (attempt_id, container_index, line, definition_id, quantity) values (@attempt, 0, 0, 'item.medkit', 2)", new { attempt });
        Assert.Equal("pool_collection_change", (await FailureOf(db.AsApp(), "insert into pool_collections (attempt_id, container_index, line, collected) values (@attempt, 0, 0, 3)", new { attempt })).ConstraintName);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("insert into pool_collections (attempt_id, container_index, line, collected) values (@attempt, 0, 0, 1)", new { attempt });
            await conn.ExecuteAsync("update pool_collections set collected = 2 where attempt_id = @attempt", new { attempt });
        }
        Assert.Equal("pool_collection_change", (await FailureOf(db.AsApp(), "update pool_collections set collected = 1 where attempt_id = @attempt", new { attempt })).ConstraintName);
        Assert.Equal("23503", (await FailureOf(db.AsApp(), "insert into pool_collections (attempt_id, container_index, line, collected) values (@attempt, 5, 0, 1)", new { attempt })).SqlState);
        Assert.Equal("pool_collection_is_permanent", (await FailureOf(db.AsSuperuser(), "delete from pool_collections")).ConstraintName);
    }

    // ---- edges ----

    [Fact]
    public async Task Holds_settle_or_refund_only_from_charged()
    {
        var account = await NewAccountAsync();
        var reserved = await AttemptFixtures.AdmitAsync(db, account);
        Assert.Equal("energy_hold_transition", (await FailureOf(db.AsSuperuser(), "update energy_holds set state = 'settled' where attempt_id = @reserved", new { reserved })).ConstraintName);
        await AttemptFixtures.ReleaseAsync(db, reserved);
        Assert.Equal("energy_hold_transition", (await FailureOf(db.AsSuperuser(), "update energy_holds set state = 'refunded' where attempt_id = @reserved", new { reserved })).ConstraintName);
        var (_, a1) = await StartedAsync();
        var (_, a2) = await StartedAsync();
        await Super("update energy_holds set state = 'settled' where attempt_id = @a1; update energy_holds set state = 'refunded' where attempt_id = @a2", new { a1, a2 });
        Assert.Equal("energy_hold_transition", (await FailureOf(db.AsSuperuser(), "update energy_holds set state = 'refunded' where attempt_id = @a1", new { a1 })).ConstraintName);
    }

    [Fact]
    public async Task Participants_settle_directly_from_joined_or_left_but_never_from_admitted()
    {
        var account = await NewAccountAsync();
        var admitted = await AttemptFixtures.AdmitAsync(db, account);
        Assert.Equal("attempt_participant_transition", (await FailureOf(db.AsSuperuser(), "update attempt_participants set state = 'settled' where attempt_id = @admitted", new { admitted })).ConstraintName);
        await AttemptFixtures.ReleaseAsync(db, admitted);
        var (_, joined) = await StartedAsync();
        await Super("update attempt_participants set state = 'settled' where attempt_id = @joined", new { joined });
        var (_, left) = await StartedAsync();
        await Super("update attempt_participants set state = 'left' where attempt_id = @left; update attempt_participants set state = 'settled' where attempt_id = @left", new { left });
    }

    [Fact]
    public async Task Quarantine_columns_follow_the_state_and_are_written_once()
    {
        var (_, attempt) = await StartedAsync();
        Assert.Equal("attempt_participant_quarantine", (await FailureOf(db.AsSuperuser(),
            "update attempt_participants set state = 'quarantined' where attempt_id = @attempt", new { attempt })).ConstraintName);
        await Super("""
            update attempt_participants set state = 'quarantined', quarantined_at = now(), escalate_at = now() + interval '24 hours',
              quarantine_reason = 'invalid_manifest' where attempt_id = @attempt
            """, new { attempt });
        await Super("update attempt_participants set escalate_at = escalate_at + interval '24 hours' where attempt_id = @attempt", new { attempt });
        Assert.Equal("attempt_participant_transition", (await FailureOf(db.AsSuperuser(),
            "update attempt_participants set quarantine_reason = 'host_revoked' where attempt_id = @attempt", new { attempt })).ConstraintName);
        Assert.Equal("attempt_participant_quarantine", (await FailureOf(db.AsSuperuser(),
            "update attempt_participants set state = 'settled' where attempt_id = @attempt", new { attempt })).ConstraintName); // escalate_at must clear
        await Super("update attempt_participants set state = 'settled', escalate_at = null where attempt_id = @attempt", new { attempt });
        Assert.Equal("invalid_manifest", await SuperScalar<string>("select quarantine_reason from attempt_participants where attempt_id = @attempt", new { attempt }));

        var (_, other) = await StartedAsync();
        await Super("update attempt_participants set recorded_manifest_hash = @h where attempt_id = @other", new { other, h = "sha256:" + new string('1', 64) });
        Assert.Equal("attempt_participant_transition", (await FailureOf(db.AsSuperuser(), "update attempt_participants set recorded_manifest_hash = @h where attempt_id = @other",
            new { other, h = "sha256:" + new string('2', 64) })).ConstraintName);
    }

    [Fact]
    public async Task A_lost_host_stays_lost_disabled_and_unrotated()
    {
        await Super("insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, port_base) values ('lost-host', 'agent', sha256('y'::bytea), 4, true, 7100)");
        Assert.Equal("mission_host_declared_lost", (await FailureOf(db.AsSuperuser(), "update mission_hosts set declared_lost_at = now() where host_id = 'lost-host'")).ConstraintName);
        await Super("update mission_hosts set enabled = false, secret_revoked = true, secret_hash = sha256('z'::bytea) where host_id = 'lost-host'");
        await Super("update mission_hosts set declared_lost_at = now() where host_id = 'lost-host'");
        foreach (var sql in new[] { "update mission_hosts set enabled = true where host_id = 'lost-host'",
                     "update mission_hosts set secret_hash = sha256('w'::bytea), secret_revoked = false where host_id = 'lost-host'",
                     "update mission_hosts set declared_lost_at = null where host_id = 'lost-host'" })
            Assert.Equal("mission_host_declared_lost", (await FailureOf(db.AsSuperuser(), sql)).ConstraintName);
        await Super("update mission_hosts set drained = true where host_id = 'lost-host'"); // other switches stay usable
    }

    [Fact]
    public async Task Settle_and_refund_keys_name_their_own_attempt()
    {
        var account = await NewAccountAsync();
        await Super("""
            insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@account, 40, now(), 0, 1);
            insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@account, 'open', 40, 40, 'open');
            """, new { account });
        var attempt = await AttemptFixtures.AdmitAsync(db, account);
        foreach (var (kind, delta) in new[] { ("settle", 0), ("void_refund", 5) })
        {
            var e = await FailureOf(db.AsSuperuser(), "insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key) values (@account, @attempt, @kind, @delta, 40 + @delta, @key)",
                new { account, attempt, kind, delta, key = kind + ":" + Guid.CreateVersion7() });
            Assert.Equal("energy_ledger_idempotency_key_check", e.ConstraintName);
        }
    }

    // ---- exit evidence ----

    [Fact]
    public async Task Agent_reports_carry_the_stage9_marks_and_may_record_a_void()
    {
        var (account, attempt) = await StartedAsync();
        var e = await FailureOf(db.AsSuperuser(), """
            insert into attempt_exit_reports (attempt_id, source, host_id, attempt_state, any_started, exit_code, outcome)
            values (@attempt, 'agent', 'fixture-host', 'running', true, 1, 'none')
            """, new { attempt });
        Assert.Equal("attempt_exit_report_stage9_marks", e.ConstraintName);
        await Super("""
            insert into attempt_exit_reports (attempt_id, source, host_id, attempt_state, any_started, exit_code, outcome, outbox_complete, attempt_revoked)
            values (@attempt, 'agent', 'fixture-host', 'running', true, 1, 'voided', false, false);
            insert into attempt_exit_results (attempt_id, account_id, present) values (@attempt, @account, false);
            insert into attempt_outbox_observations (attempt_id, host_id, attempt_revoked, outcome) values (@attempt, 'fixture-host', false, 'none');
            insert into attempt_outbox_observation_results (attempt_id, account_id, present) values (@attempt, @account, true);
            """, new { attempt, account });
        Assert.Equal("attempt_outbox_observation_is_immutable", (await FailureOf(db.AsSuperuser(), "update attempt_outbox_observations set outcome = 'voided'")).ConstraintName);
        Assert.Equal("23505", (await FailureOf(db.AsSuperuser(), "insert into attempt_outbox_observations (attempt_id, host_id, attempt_revoked, outcome) values (@attempt, 'fixture-host', false, 'none')", new { attempt })).SqlState);

        // An observation needs an earlier agent report of an incomplete scan.
        var (_, complete) = await StartedAsync();
        await Super("""
            insert into attempt_exit_reports (attempt_id, source, host_id, attempt_state, any_started, exit_code, outcome, outbox_complete, attempt_revoked)
            values (@complete, 'agent', 'fixture-host', 'running', true, 0, 'none', true, false)
            """, new { complete });
        Assert.Equal("attempt_outbox_observation_follows", (await FailureOf(db.AsSuperuser(),
            "insert into attempt_outbox_observations (attempt_id, host_id, attempt_revoked, outcome) values (@complete, 'fixture-host', false, 'none')", new { complete })).ConstraintName);
    }

    // ---- anomalies ----

    [Fact]
    public async Task An_anomaly_is_reviewed_once_with_a_note()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>("insert into anomaly_events (kind, dedupe_key, detail) values ('result_malformed', 'k', '{}') returning id");
        Assert.Equal("anomaly_event_review", (await FailureOf(db.AsApp(), "update anomaly_events set reviewed_at = now() where id = @id", new { id })).ConstraintName);
        await conn.ExecuteAsync("update anomaly_events set reviewed_at = now(), reviewed_note = 'looked' where id = @id", new { id });
        Assert.Equal("anomaly_event_review", (await FailureOf(db.AsApp(), "update anomaly_events set reviewed_note = 'again' where id = @id", new { id })).ConstraintName);
        Assert.Equal("42501", (await FailureOf(db.AsApp(), "update anomaly_events set kind = 'x' where id = @id", new { id })).SqlState);
        Assert.Equal("anomaly_event_is_permanent", (await FailureOf(db.AsSuperuser(), "delete from anomaly_events where id = @id", new { id })).ConstraintName);
    }

    // ---- invariants ----

    [Fact]
    public async Task The_0009_invariants_hold_on_a_fresh_database_and_catch_a_settled_participant_without_a_row()
    {
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
        var (account, attempt) = await StartedAsync();
        await Super("update attempt_participants set state = 'settled' where attempt_id = @attempt", new { attempt });
        var found = await SettlementInvariantsAsync();
        Assert.Contains($"settled_without_row:{attempt}/{account}", found);
        Assert.DoesNotContain(found, f => f.StartsWith("settled_attempt_open", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_0009_invariants_catch_an_ending_attempt_without_its_entry_time()
    {
        var (_, stamped) = await StartedAsync();
        await Super("update mission_attempts set state = 'ending' where attempt_id = @stamped", new { stamped }); // the trigger stamps ending_at
        var (_, forced) = await StartedAsync();
        await Super("begin; set local session_replication_role = replica; update mission_attempts set state = 'ending' where attempt_id = @forced; commit", new { forced });
        var found = await SettlementInvariantsAsync();
        Assert.Contains($"ending_without_time:{forced}", found);
        Assert.DoesNotContain($"ending_without_time:{stamped}", found);
    }

    // ---- upgrade ----

    [Fact]
    public async Task A_populated_schema_8_database_upgrades_and_keeps_its_rows()
    {
        await using var upgrade = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(upgrade.AsMigrator(), MigrationCatalog.Embedded);
        await migrator.MigrateAsync(8);
        long account;
        var attempt = Guid.CreateVersion7();
        await using (var conn = await upgrade.AsSuperuser().OpenConnectionAsync())
        {
            account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
            await conn.ExecuteAsync("""
                insert into mission_attempts (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline)
                values (@attempt, @account, 'procedural-standard', 1, 'sha256:' || repeat('cd', 32), clock_timestamp() + interval '60 seconds');
                insert into attempt_secrets (attempt_id, seed, retention_salt) values (@attempt, 1, decode(repeat('ab', 32), 'hex'));
                insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
                values (@attempt, @account, 'procedural-standard', 1, '{}', 1, 0);
                insert into energy_holds (attempt_id, account_id, amount) values (@attempt, @account, 0);
                insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, port_base) values ('h8', 'agent', sha256('h8'::bytea), 2, true, 7000);
                insert into allowed_builds (build_revision, catalogue_version) values ('b8', 'sha256:' || repeat('cd', 32));
                insert into allowed_build_templates (build_revision, catalogue_version, template_id, template_version) values ('b8', 'sha256:' || repeat('cd', 32), 'procedural-standard', 1);
                """, new { attempt, account });
            await conn.ExecuteAsync("update mission_attempts set " + AttemptFixtures.StateSet("assigned", "h8").Replace(AttemptFixtures.FixtureBuild, "b8") + " where attempt_id = @attempt", new { attempt });
            await conn.ExecuteAsync("""
                insert into attempt_exit_reports (attempt_id, source, host_id, attempt_state, any_started, exit_code, outcome)
                values (@attempt, 'agent', 'h8', 'assigned', false, 0, 'cancelled')
                """, new { attempt });
        }
        Assert.Equal([9], (await migrator.MigrateAsync()).Applied);
        Assert.Equal(1L, await AccountTestKit.ScalarAsSuperuserAsync<long>(upgrade, "select count(*) from mission_attempts where attempt_id = @attempt", new { attempt }));
        Assert.Null(await AccountTestKit.ScalarAsSuperuserAsync<bool?>(upgrade, "select outbox_complete from attempt_exit_reports where attempt_id = @attempt", new { attempt }));
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(upgrade));
        _ = account;
    }

    // ---- secrecy of constraint errors ----

    [Fact]
    public async Task A_settlement_constraint_error_names_no_salt_seed_or_manifest()
    {
        var (account, attempt) = await StartedAsync();
        var salt = await SuperScalar<byte[]>("select retention_salt from attempt_secrets where attempt_id = @attempt", new { attempt });
        var seed = await SuperScalar<int>("select seed from attempt_secrets where attempt_id = @attempt", new { attempt });
        await using var detail = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
        {
            Host = db.Host, Port = int.Parse(db.EnvFor("app")["BLACKGLASS_DB_PORT"]!, System.Globalization.CultureInfo.InvariantCulture),
            Database = db.Name, Username = "accounts_app", Password = db.Password("app"), IncludeErrorDetail = true, MaxPoolSize = 2,
        });
        var errors = new List<PostgresException>
        {
            await FailureOf(detail, "update attempt_participants set state = 'quarantined' where attempt_id = @attempt", new { attempt }),
            await FailureOf(detail, "update energy_holds set state = 'released' where attempt_id = @attempt", new { attempt }),
            await FailureOf(detail, "insert into participant_settlements (attempt_id, account_id, outcome, catalogue_version, receipt, settled_at) values (@attempt, @account, 'success', @v, '{}', now())",
                new { attempt, account, v = AttemptFixtures.TestCatalogueVersion }),
        };
        foreach (var e in errors)
        {
            var text = e.Message + " " + e.Detail;
            Assert.DoesNotContain(Convert.ToHexStringLower(salt), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(seed.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        }
    }
}
