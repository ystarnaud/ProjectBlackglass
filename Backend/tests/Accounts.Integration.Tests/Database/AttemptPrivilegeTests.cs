using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Migration 0007's privileges (Stage 7; spec §15.1) against a real PostgreSQL 17. The app role reads the nine
/// Stage 7 tables; it inserts through the listed columns only (never a creation time, a stamp or an initial state, which
/// the database sets); it updates only an attempt's state, cancel reason and end, a participant's or a hold's state, a
/// coverage's policy, version, activation and dates, and a host's heartbeat only (Stage 8 adds the switches and capacity
/// with their audited verbs); it never deletes or
/// truncates, and executes no trigger function. The outsider role and PUBLIC hold nothing.</summary>
[Trait("Requires", "Postgres")]
public class AttemptPrivilegeTests : IAsyncLifetime
{
    TestDatabase db = null!;
    static readonly string[] Stage7Tables =
        ["attempt_insurance", "attempt_participants", "deploy_requests", "energy_holds", "insurance_activations", "insurance_coverages",
         "insurance_policies", "mission_attempts", "mission_hosts", "mission_templates"];
    static readonly string[] TriggerFunctions =
        ["attempt_participant_transition", "energy_hold_transition", "insurance_coverage_forward_only", "mission_attempt_transition", "refuse_row_change"];

    public async Task InitializeAsync()
    {
        db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    static async Task<string> SqlStateOf(NpgsqlDataSource ds, string sql, object? args = null)
    {
        await using var conn = await ds.OpenConnectionAsync();
        var e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, args));
        return e.SqlState;
    }

    [Fact]
    public async Task The_app_role_has_exactly_the_stage7_privileges()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();

        // Table-level grants, as the catalogue lists them for the role itself.
        var tables = (await conn.QueryAsync<string>("""
            select table_name || ':' || string_agg(privilege_type, ',' order by privilege_type)
            from information_schema.role_table_grants
            where grantee = 'accounts_app' and table_name = any(@Stage7Tables)
            group by table_name
            """, new { Stage7Tables })).ToList();
        Assert.Equal(Stage7Tables.Select(t => t + ":SELECT"), tables.OrderBy(t => t, StringComparer.Ordinal));

        // The effective privilege on every column (table-level or column-level, from any grant the role inherits or PUBLIC holds).
        var columns = (await conn.QueryAsync<string>("""
            select c.relname || '.' || a.attname || ':' || string_agg(p.priv, ',' order by p.priv)
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace and n.nspname = 'public'
            join pg_attribute a on a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped
            cross join (values ('SELECT'), ('INSERT'), ('UPDATE'), ('REFERENCES')) as p(priv)
            where c.relname = any(@Stage7Tables) and c.relkind = 'r'
              and has_column_privilege('accounts_app', c.oid, a.attnum, p.priv)
            group by c.relname, a.attname
            """, new { Stage7Tables })).ToList();
        Assert.Equal(new[]
        {
            "attempt_insurance.account_id:INSERT,SELECT",
            "attempt_insurance.activation_id:INSERT,SELECT",
            "attempt_insurance.attempt_id:INSERT,SELECT",
            "attempt_insurance.policy_id:INSERT,SELECT",
            "attempt_insurance.policy_version:INSERT,SELECT",
            "attempt_insurance.snapshotted_at:SELECT",
            "attempt_insurance.terms:INSERT,SELECT",
            "attempt_participants.account_id:INSERT,SELECT",
            "attempt_participants.attempt_id:INSERT,SELECT",
            "attempt_participants.energy_cost:INSERT,SELECT",
            "attempt_participants.inventory_version:INSERT,SELECT",
            "attempt_participants.loadout_snapshot:INSERT,SELECT",
            "attempt_participants.release_reason:SELECT,UPDATE",
            "attempt_participants.started_at:SELECT,UPDATE",
            "attempt_participants.state:SELECT,UPDATE",
            "attempt_participants.template_id:INSERT,SELECT",
            "attempt_participants.template_version:INSERT,SELECT",
            "deploy_requests.account_id:INSERT,SELECT",
            "deploy_requests.attempt_id:INSERT,SELECT",
            "deploy_requests.created_at:SELECT",
            "deploy_requests.idempotency_key:INSERT,SELECT",
            "deploy_requests.request_hash:INSERT,SELECT",
            "deploy_requests.response:INSERT,SELECT",
            "energy_holds.account_id:INSERT,SELECT",
            "energy_holds.amount:INSERT,SELECT",
            "energy_holds.attempt_id:INSERT,SELECT",
            "energy_holds.state:SELECT,UPDATE",
            "energy_holds.updated_at:SELECT",
            "insurance_activations.account_id:INSERT,SELECT",
            "insurance_activations.activation_id:INSERT,SELECT",
            "insurance_activations.created_at:SELECT",
            "insurance_activations.duration:INSERT,SELECT",
            "insurance_activations.idempotency_key:INSERT,SELECT",
            "insurance_activations.policy_id:INSERT,SELECT",
            "insurance_activations.policy_version:INSERT,SELECT",
            "insurance_activations.previous_expires_at:INSERT,SELECT",
            "insurance_activations.resulting_expires_at:INSERT,SELECT",
            "insurance_activations.source:INSERT,SELECT",
            "insurance_coverages.account_id:INSERT,SELECT",
            "insurance_coverages.activation_id:INSERT,SELECT,UPDATE",
            "insurance_coverages.expires_at:INSERT,SELECT,UPDATE",
            "insurance_coverages.policy_id:INSERT,SELECT,UPDATE",
            "insurance_coverages.policy_version:INSERT,SELECT,UPDATE",
            "insurance_coverages.starts_at:INSERT,SELECT,UPDATE",
            "insurance_policies.created_at:SELECT",
            "insurance_policies.policy_id:INSERT,SELECT",
            "insurance_policies.terms:INSERT,SELECT",
            "insurance_policies.version:INSERT,SELECT",
            "mission_attempts.assign_deadline:INSERT,SELECT",
            "mission_attempts.attempt_id:INSERT,SELECT",
            "mission_attempts.build_revision:SELECT,UPDATE",
            "mission_attempts.cancel_reason:SELECT,UPDATE",
            "mission_attempts.catalogue_version:INSERT,SELECT",
            "mission_attempts.claim_key:SELECT,UPDATE",
            "mission_attempts.created_at:SELECT",
            "mission_attempts.created_by:INSERT,SELECT",
            "mission_attempts.credential_generation:SELECT,UPDATE",
            "mission_attempts.credential_revoked_at:SELECT,UPDATE",
            "mission_attempts.ended_at:SELECT,UPDATE",
            "mission_attempts.host_id:SELECT,UPDATE",
            "mission_attempts.join_deadline:SELECT,UPDATE",
            "mission_attempts.last_process_heartbeat_at:SELECT,UPDATE",
            "mission_attempts.launch_deadline:SELECT,UPDATE",
            "mission_attempts.layout_hash:SELECT,UPDATE",
            "mission_attempts.loot_hash:SELECT,UPDATE",
            "mission_attempts.loot_table_version:SELECT,UPDATE",
            "mission_attempts.mission_port:SELECT,UPDATE",
            "mission_attempts.process_credential_hash:SELECT,UPDATE",
            "mission_attempts.process_session_id:SELECT,UPDATE",
            "mission_attempts.ready_at:SELECT,UPDATE",
            "mission_attempts.slot_released_at:SELECT,UPDATE",
            "mission_attempts.started_at:SELECT,UPDATE",
            "mission_attempts.state:SELECT,UPDATE",
            "mission_attempts.template_id:INSERT,SELECT",
            "mission_attempts.template_version:INSERT,SELECT",
            "mission_hosts.build_revision:SELECT,UPDATE",
            "mission_hosts.capacity:INSERT,SELECT,UPDATE",
            "mission_hosts.catalogue_version:SELECT,UPDATE",
            "mission_hosts.created_at:SELECT",
            "mission_hosts.drained:SELECT,UPDATE",
            "mission_hosts.enabled:INSERT,SELECT,UPDATE",
            "mission_hosts.host_id:INSERT,SELECT",
            "mission_hosts.kind:INSERT,SELECT",
            "mission_hosts.last_heartbeat_at:SELECT,UPDATE",
            "mission_hosts.port_base:INSERT,SELECT",
            "mission_hosts.secret_hash:INSERT,SELECT,UPDATE",
            "mission_hosts.secret_revoked:SELECT,UPDATE",
            "mission_hosts.secret_rotated_at:SELECT,UPDATE",
            "mission_templates.created_at:SELECT",
            "mission_templates.energy_cost:INSERT,SELECT",
            "mission_templates.max_operatives:INSERT,SELECT",
            "mission_templates.min_operatives:INSERT,SELECT",
            "mission_templates.template_id:INSERT,SELECT",
            "mission_templates.version:INSERT,SELECT",
        }, columns.OrderBy(c => c, StringComparer.Ordinal));

        // No other table privilege on any of them, effective.
        Assert.Empty(await conn.QueryAsync<string>("""
            select c.relname || ':' || p.priv
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace and n.nspname = 'public'
            cross join (values ('DELETE'), ('TRUNCATE'), ('TRIGGER')) as p(priv)
            where c.relname = any(@Stage7Tables) and c.relkind = 'r' and has_table_privilege('accounts_app', c.oid, p.priv)
            """, new { Stage7Tables }));

        // 0007 adds no sequence (no identity column), and the app holds nothing on any sequence.
        Assert.Empty(await conn.QueryAsync<string>("""
            select a.attrelid::regclass::text || '.' || a.attname from pg_attribute a join pg_class c on c.oid = a.attrelid
            where c.relname = any(@Stage7Tables) and (a.attidentity <> '' or a.atthasdef and pg_get_expr((select adbin from pg_attrdef d where d.adrelid = a.attrelid and d.adnum = a.attnum), a.attrelid) like 'nextval%')
            """, new { Stage7Tables }));
        Assert.Empty(await conn.QueryAsync<string>(
            "select relname::text from pg_class where case when relkind = 'S' then has_sequence_privilege('accounts_app', oid, 'USAGE, SELECT, UPDATE') else false end"));
    }

    /// <summary>An admitted solo attempt, as the app writes it (zero cost: no energy step).</summary>
    async Task<(long Account, Guid Attempt)> AdmittedAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        var account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
        var attempt = Guid.CreateVersion7();
        var activation = Guid.CreateVersion7();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("""
            insert into insurance_activations (activation_id, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source, idempotency_key)
            values (@activation, @account, 'standard', 1, interval '1 day', null, clock_timestamp() + interval '1 day', 'dev_grant', gen_random_uuid());
            insert into insurance_coverages (account_id, policy_id, policy_version, activation_id, starts_at, expires_at)
            values (@account, 'standard', 1, @activation, clock_timestamp(), clock_timestamp() + interval '1 day');
            insert into mission_attempts (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline)
            values (@attempt, @account, 'procedural-standard', 1, 'sha256:' || repeat('ab', 32), clock_timestamp() + interval '60 seconds');
            insert into attempt_secrets (attempt_id, seed, retention_salt) values (@attempt, 1, decode(repeat('ab', 32), 'hex'));
            insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
            values (@attempt, @account, 'procedural-standard', 1, '{}', 1, 0);
            insert into energy_holds (attempt_id, account_id, amount) values (@attempt, @account, 0);
            insert into attempt_insurance (attempt_id, account_id, activation_id, policy_id, policy_version, terms)
            select @attempt, @account, @activation, 'standard', 1, terms from insurance_policies where policy_id = 'standard' and version = 1;
            insert into deploy_requests (account_id, idempotency_key, request_hash, attempt_id, response)
            values (@account, gen_random_uuid(), decode(repeat('01', 32), 'hex'), @attempt, '{}');
            insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled) values ('fixture', 'dev_fixture', decode(repeat('02', 32), 'hex'), 1, true);
            """, new { account, attempt, activation }, tx);
        await tx.CommitAsync();
        return (account, attempt);
    }

    [Fact]
    public async Task The_app_role_can_write_an_admission_and_a_release_as_granted()
    {
        var (account, attempt) = await AdmittedAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using (var tx = await conn.BeginTransactionAsync())
        {
            // The release order (attempt, participant, hold, inventory lock), each lockable FOR UPDATE through its UPDATE grant.
            await conn.ExecuteAsync("select 1 from mission_attempts where attempt_id = @attempt for update", new { attempt }, tx);
            await conn.ExecuteAsync("select 1 from attempt_participants where attempt_id = @attempt for update", new { attempt }, tx);
            await conn.ExecuteAsync("select 1 from energy_holds where attempt_id = @attempt for update", new { attempt }, tx);
            await conn.ExecuteAsync("""
                update mission_attempts set state = 'cancelled', cancel_reason = 'player_cancel', ended_at = clock_timestamp() where attempt_id = @attempt;
                update attempt_participants set state = 'released', release_reason = 'player_cancel' where attempt_id = @attempt;
                update energy_holds set state = 'released' where attempt_id = @attempt;
                """, new { attempt }, tx);
            await tx.CommitAsync();
        }
        await using (var tx = await conn.BeginTransactionAsync())
        {
            // Activation's lock order (account, then coverage), an extension, and the host's heartbeat (its only UPDATE column).
            await conn.ExecuteAsync("select 1 from accounts where account_id = @account for update", new { account }, tx);
            await conn.ExecuteAsync("select 1 from insurance_coverages where account_id = @account for update", new { account }, tx);
            await conn.ExecuteAsync("update insurance_coverages set expires_at = expires_at + interval '1 day' where account_id = @account", new { account }, tx);
            await conn.ExecuteAsync("update mission_hosts set last_heartbeat_at = clock_timestamp()", transaction: tx);
            await conn.ExecuteAsync("insert into insurance_policies (policy_id, version, terms) values ('premium', 1, '{}')", transaction: tx);
            await conn.ExecuteAsync("insert into mission_templates (template_id, version, energy_cost, min_operatives, max_operatives) values ('procedural-standard', 2, 0, 1, 3)", transaction: tx);
            await tx.CommitAsync();
        }
        Assert.Equal(("cancelled", "released", "released"), await conn.QuerySingleAsync<(string, string, string)>("""
            select a.state, p.state, h.state from mission_attempts a join attempt_participants p using (attempt_id) join energy_holds h using (attempt_id, account_id)
            where a.attempt_id = @attempt
            """, new { attempt }));
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
    }

    [Fact]
    public async Task The_app_role_cannot_write_what_stage7_never_lets_it()
    {
        var (account, attempt) = await AdmittedAsync();
        foreach (var sql in new[]
        {
            // No delete or truncate anywhere.
            "delete from mission_attempts", "truncate mission_attempts cascade",
            "delete from attempt_participants", "truncate attempt_participants cascade",
            "delete from energy_holds", "truncate energy_holds",
            "delete from attempt_insurance", "truncate attempt_insurance",
            "delete from deploy_requests", "truncate deploy_requests",
            "delete from insurance_policies", "truncate insurance_policies cascade",
            "delete from insurance_activations", "truncate insurance_activations cascade",
            "delete from insurance_coverages", "truncate insurance_coverages",
            "delete from mission_templates", "truncate mission_templates cascade",
            "delete from mission_hosts", "truncate mission_hosts",
            // No update of an immutable table, or of a fixed column.
            "update insurance_policies set terms = '{}'",
            "update insurance_activations set resulting_expires_at = now()",
            "update attempt_insurance set terms = '{}'",
            "update deploy_requests set response = '{}'",
            "update mission_templates set energy_cost = 0",
            "update attempt_secrets set seed = 1",
            "update attempt_secrets set retention_salt = retention_salt",
            "delete from attempt_secrets", "truncate attempt_secrets",
            "update mission_attempts set assign_deadline = now()",
            "update mission_attempts set created_by = created_by",
            "update mission_attempts set template_version = 1",
            "update attempt_participants set loadout_snapshot = '{}'",
            "update attempt_participants set inventory_version = 2",
            "update attempt_participants set energy_cost = 1",
            "update energy_holds set amount = 1",
            "update energy_holds set updated_at = now()",
            "update insurance_coverages set account_id = account_id",
            "update mission_hosts set kind = 'agent'",
            "update mission_hosts set host_id = 'renamed'",
            // 0008 grants the audited verbs' switches, capacity and secret, and the heartbeat's build; never the port base or creation.
            "update mission_hosts set port_base = 7000",
            "update mission_hosts set created_at = now()",
            // The database sets times, stamps and initial states.
            "insert into mission_attempts (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline, state) values (gen_random_uuid(), @account, 'procedural-standard', 1, 'sha256:' || repeat('ab', 32), now(), 'admitted')",
            "insert into mission_attempts (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline, created_at) values (gen_random_uuid(), @account, 'procedural-standard', 1, 'sha256:' || repeat('ab', 32), now(), now())",
            "insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost, state) values (@attempt, @account, 'procedural-standard', 1, '{}', 1, 0, 'admitted')",
            "insert into energy_holds (attempt_id, account_id, amount, state) values (@attempt, @account, 0, 'reserved')",
            "insert into energy_holds (attempt_id, account_id, amount, updated_at) values (@attempt, @account, 0, now())",
            "insert into attempt_insurance (attempt_id, account_id, activation_id, policy_id, policy_version, terms, snapshotted_at) select @attempt, @account, activation_id, 'standard', 1, '{}', now() from insurance_activations",
            "insert into deploy_requests (account_id, idempotency_key, request_hash, attempt_id, response, created_at) values (@account, gen_random_uuid(), decode(repeat('01', 32), 'hex'), @attempt, '{}', now())",
            "insert into insurance_policies (policy_id, version, terms, created_at) values ('premium', 1, '{}', now())",
            "insert into insurance_activations (activation_id, account_id, policy_id, policy_version, duration, resulting_expires_at, source, idempotency_key, created_at) values (gen_random_uuid(), @account, 'standard', 1, interval '1 day', now() + interval '1 day', 'dev_grant', gen_random_uuid(), now())",
            "insert into mission_templates (template_id, version, energy_cost, min_operatives, max_operatives, created_at) values ('t', 1, 0, 1, 1, now())",
            "insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, drained) values ('h', 'agent', decode(repeat('03', 32), 'hex'), 1, true, false)",
            "insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, last_heartbeat_at) values ('h', 'agent', decode(repeat('03', 32), 'hex'), 1, true, now())",
            "insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, created_at) values ('h', 'agent', decode(repeat('03', 32), 'hex'), 1, true, now())",
            // A trigger function is not callable.
            "select refuse_row_change()",
            "select mission_attempt_transition()",
        })
            Assert.Equal("42501", await SqlStateOf(db.AsApp(), sql, new { account, attempt }));

        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        Assert.Equal((1L, 1L, 1L, 1L, 1L, 1L, 1L), await su.QuerySingleAsync<(long, long, long, long, long, long, long)>("""
            select (select count(*) from mission_attempts), (select count(*) from attempt_participants), (select count(*) from energy_holds),
                   (select count(*) from attempt_insurance), (select count(*) from deploy_requests), (select count(*) from insurance_coverages),
                   (select count(*) from mission_hosts)
            """));
    }

    [Fact]
    public async Task Every_stage7_trigger_function_is_unexecutable_by_the_app_and_public()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var functions = (await conn.QueryAsync<(string Name, bool App, bool Public, bool Definer)>("""
            select p.proname::text, has_function_privilege('accounts_app', p.oid, 'execute'), has_function_privilege('public', p.oid, 'execute'), p.prosecdef
            from pg_proc p join pg_namespace n on n.oid = p.pronamespace and n.nspname = 'public'
            where p.proname = any(@TriggerFunctions) order by p.proname
            """, new { TriggerFunctions })).ToList();
        Assert.Equal(TriggerFunctions, functions.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(functions, f => Assert.Equal((false, false, false), (f.App, f.Public, f.Definer)));

        // Every trigger on the Stage 7 tables, enabled in the ordinary (origin) mode.
        var triggers = (await conn.QueryAsync<string>("""
            select c.relname || '.' || t.tgname || ':' || t.tgenabled::text || ':' || case when t.tgtype & 1 = 1 then 'row' else 'statement' end
                   || ':' || p.proname || ':' || encode(t.tgargs, 'escape')
            from pg_trigger t join pg_class c on c.oid = t.tgrelid join pg_proc p on p.oid = t.tgfoid
            where not t.tgisinternal and c.relname = any(@Stage7Tables)
            """, new { Stage7Tables })).ToList();
        Assert.Equal(new[]
        {
            "attempt_insurance.attempt_insurance_is_immutable:O:row:refuse_row_change:attempt_insurance_is_immutable\\000",
            "attempt_insurance.attempt_insurance_no_truncate:O:statement:refuse_row_change:attempt_insurance_is_immutable\\000",
            "attempt_participants.attempt_participant_is_permanent:O:row:refuse_row_change:attempt_participant_is_permanent\\000",
            "attempt_participants.attempt_participant_transition:O:row:attempt_participant_transition:",
            "attempt_participants.attempt_participants_no_truncate:O:statement:refuse_row_change:attempt_participant_is_permanent\\000",
            "deploy_requests.deploy_request_is_immutable:O:row:refuse_row_change:deploy_request_is_immutable\\000",
            "deploy_requests.deploy_requests_no_truncate:O:statement:refuse_row_change:deploy_request_is_immutable\\000",
            "energy_holds.energy_hold_is_permanent:O:row:refuse_row_change:energy_hold_is_permanent\\000",
            "energy_holds.energy_hold_transition:O:row:energy_hold_transition:",
            "energy_holds.energy_holds_no_truncate:O:statement:refuse_row_change:energy_hold_is_permanent\\000",
            "insurance_activations.insurance_activation_is_append_only:O:row:refuse_row_change:insurance_activation_is_append_only\\000",
            "insurance_activations.insurance_activations_no_truncate:O:statement:refuse_row_change:insurance_activation_is_append_only\\000",
            "insurance_coverages.insurance_coverage_forward_only:O:row:insurance_coverage_forward_only:",
            "insurance_policies.insurance_policies_no_truncate:O:statement:refuse_row_change:insurance_policy_is_immutable\\000",
            "insurance_policies.insurance_policy_is_immutable:O:row:refuse_row_change:insurance_policy_is_immutable\\000",
            "mission_attempts.mission_attempt_is_permanent:O:row:refuse_row_change:mission_attempt_is_permanent\\000",
            "mission_attempts.mission_attempt_transition:O:row:mission_attempt_transition:",
            "mission_attempts.mission_attempts_no_truncate:O:statement:refuse_row_change:mission_attempt_is_permanent\\000",
            "mission_templates.mission_template_is_immutable:O:row:refuse_row_change:mission_template_is_immutable\\000",
            "mission_templates.mission_templates_no_truncate:O:statement:refuse_row_change:mission_template_is_immutable\\000",
        }, triggers.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_outsider_role_reads_nothing()
    {
        await AdmittedAsync();
        await using (var conn = await db.AsSuperuser().OpenConnectionAsync())
        {
            var held = (await conn.QueryAsync<string>("""
                select r.role || ':' || t.name || ':' || p.priv
                from unnest(@Stage7Tables) as t(name)
                cross join (values ('bg_test_outsider'), ('public')) as r(role)
                cross join (values ('SELECT'), ('INSERT'), ('UPDATE'), ('REFERENCES')) as p(priv)
                where has_any_column_privilege(r.role, t.name, p.priv)
                union all
                select r.role || ':' || t.name || ':' || p.priv
                from unnest(@Stage7Tables) as t(name)
                cross join (values ('bg_test_outsider'), ('public')) as r(role)
                cross join (values ('DELETE'), ('TRUNCATE'), ('TRIGGER')) as p(priv)
                where has_table_privilege(r.role, t.name, p.priv)
                union all
                select r.role || ':' || f.name || ':execute'
                from unnest(@TriggerFunctions) as f(name)
                cross join (values ('bg_test_outsider'), ('public')) as r(role)
                where has_function_privilege(r.role, f.name || '()', 'execute')
                """, new { Stage7Tables, TriggerFunctions })).ToList();
            Assert.Empty(held);
            await conn.ExecuteAsync($"grant connect on database \"{db.Name}\" to bg_test_outsider");
        }
        foreach (var table in Stage7Tables)
            Assert.Equal("42501", await SqlStateOf(db.AsOutsider(), $"select count(*) from public.{table}"));
    }
}
