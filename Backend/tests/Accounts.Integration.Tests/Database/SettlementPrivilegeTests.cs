using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>0009's privileges (plan §D item 11): the app role holds exactly the columns each Stage 9 path writes, SELECT on the
/// rest, no DELETE or TRUNCATE, no sequence and no trigger function; the outsider role holds nothing.</summary>
[Trait("Requires", "Postgres")]
public class SettlementPrivilegeTests : IAsyncLifetime
{
    TestDatabase db = null!;

    static readonly string[] Stage9Tables = ["attempt_outbox_observation_results", "attempt_outbox_observations", "catalogue_versions", "participant_settlements",
        "pool_collections", "result_submission_payloads", "result_submissions"];

    public async Task InitializeAsync()
    {
        db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    async Task<List<string>> ColumnPrivilegesAsync(string role, string[] tables, string[]? columns = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<string>("""
            select c.relname || '.' || a.attname || ':' || string_agg(p.priv, ',' order by p.priv)
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace and n.nspname = 'public'
            join pg_attribute a on a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped
            cross join (values ('SELECT'), ('INSERT'), ('UPDATE'), ('REFERENCES')) as p(priv)
            where c.relname = any(@tables) and c.relkind = 'r' and (@columns::text[] is null or c.relname || '.' || a.attname = any(@columns))
              and has_column_privilege(@role, c.oid, a.attnum, p.priv)
            group by c.relname, a.attname
            """, new { role, tables, columns })).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task The_app_role_has_exactly_the_stage9_column_privileges()
    {
        string I(string t, params string[] cols) => string.Join('\n', cols.Select(c => $"{t}.{c}:INSERT,SELECT"));
        var expected = string.Join('\n',
            I("attempt_outbox_observation_results", "account_id", "attempt_id", "present"),
            I("attempt_outbox_observations", "attempt_id", "attempt_revoked", "host_id", "outcome"),
            "attempt_outbox_observations.received_at:SELECT", "attempt_outbox_observations.source:SELECT",
            I("catalogue_versions", "bytes"), "catalogue_versions.registered_at:SELECT", I("catalogue_versions", "version"),
            I("participant_settlements", "account_id", "attempt_id", "catalogue_version", "manifest_hash", "outcome", "receipt", "recorded_hash",
                "retention_selection", "settled_at", "submission_id", "void_basis"),
            I("pool_collections", "attempt_id"), "pool_collections.collected:INSERT,SELECT,UPDATE", I("pool_collections", "container_index", "line"),
            I("result_submission_payloads", "canonical_bytes", "raw_bytes", "submission_id"),
            I("result_submissions", "account_id", "attempt_id", "attempt_revoked", "claimed_account_id", "claimed_attempt_id", "codes", "disposition"),
            "result_submissions.first_received_at:SELECT", I("result_submissions", "host_id"), "result_submissions.last_received_at:SELECT",
            I("result_submissions", "manifest_hash", "process_session_id", "raw_hash", "raw_length", "raw_too_large"),
            "result_submissions.receive_count:SELECT,UPDATE", I("result_submissions", "same_as_submission_id", "source"),
            "result_submissions.submission_id:SELECT").Split('\n');
        Assert.Equal(expected.Order(StringComparer.Ordinal), await ColumnPrivilegesAsync("accounts_app", Stage9Tables));
    }

    [Fact]
    public async Task The_columns_0009_adds_to_existing_tables_are_writable_only_as_their_paths_need()
    {
        var columns = new[] { "attempt_participants.quarantined_at", "attempt_participants.escalate_at", "attempt_participants.quarantine_reason",
            "attempt_participants.recorded_manifest_hash", "mission_hosts.declared_lost_at", "attempt_exit_reports.outbox_complete",
            "attempt_exit_reports.attempt_revoked", "operatives.experience", "anomaly_events.reviewed_at", "anomaly_events.reviewed_note" };
        Assert.Equal(new[]
        {
            "anomaly_events.reviewed_at:SELECT,UPDATE", "anomaly_events.reviewed_note:SELECT,UPDATE",
            "attempt_exit_reports.attempt_revoked:INSERT,SELECT", "attempt_exit_reports.outbox_complete:INSERT,SELECT",
            "attempt_participants.escalate_at:SELECT,UPDATE", "attempt_participants.quarantine_reason:SELECT,UPDATE",
            "attempt_participants.quarantined_at:SELECT,UPDATE", "attempt_participants.recorded_manifest_hash:SELECT,UPDATE",
            "mission_hosts.declared_lost_at:SELECT,UPDATE", "operatives.experience:SELECT,UPDATE",
        }, await ColumnPrivilegesAsync("accounts_app", ["anomaly_events", "attempt_exit_reports", "attempt_participants", "mission_hosts", "operatives"], columns));
    }

    [Fact]
    public async Task The_app_role_never_deletes_truncates_or_touches_a_sequence_and_the_outsider_holds_nothing()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        Assert.Equal(Stage9Tables.Select(t => t + ":SELECT"), (await conn.QueryAsync<string>("""
            select table_name || ':' || string_agg(privilege_type, ',' order by privilege_type)
            from information_schema.role_table_grants where grantee = 'accounts_app' and table_name = any(@Stage9Tables)
            group by table_name
            """, new { Stage9Tables })).Order(StringComparer.Ordinal).ToList());
        Assert.False(await conn.ExecuteScalarAsync<bool>("select has_sequence_privilege('accounts_app', 'result_submissions_submission_id_seq', 'USAGE,SELECT,UPDATE')"));
        foreach (var function in new[] { "result_submission_change()", "result_submission_payload_matches()", "pool_collection_change()", "mission_host_declared_lost()",
                     "attempt_outbox_observation_follows()", "anomaly_event_review()", "operative_experience_never_decreases()" })
        {
            Assert.False(await conn.ExecuteScalarAsync<bool>("select has_function_privilege('accounts_app', @function, 'EXECUTE')", new { function }), function);
            Assert.False(await conn.ExecuteScalarAsync<bool>("select has_function_privilege('bg_test_outsider', @function, 'EXECUTE')", new { function }), function);
        }
        Assert.Empty(await ColumnPrivilegesAsync("bg_test_outsider", Stage9Tables));
        await using var app = await db.AsApp().OpenConnectionAsync();
        foreach (var t in Stage9Tables)
        {
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync($"delete from {t}"))).SqlState);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync($"truncate {t} cascade"))).SqlState);
        }
    }
}
