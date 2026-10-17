using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Migration 0001 against a real PostgreSQL 17: bigint account ids minted only by PostgreSQL, the constraints,
/// the application role's exact privileges, the function EXECUTE revoke, and the invariants.</summary>
[Trait("Requires", "Postgres")]
public class AccountsSchemaTests : IAsyncLifetime
{
    TestDatabase db = null!;
    const string Hash32 = "decode(repeat('ab', 32), 'hex')";

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

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    async Task<(long Account, Guid Credential)> SeedAsync(string? handle = null)
    {
        var account = await NewAccountAsync();
        var credential = Guid.CreateVersion7();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        if (handle is not null)
            await conn.ExecuteAsync("insert into account_identities (provider, subject, account_id) values ('dev', @handle, @account)", new { handle, account });
        await conn.ExecuteAsync("insert into dev_credentials (credential_id, account_id, secret_hash) values (@credential, @account, decode(md5(@seed) || md5(@seed), 'hex'))",
            new { credential, account, seed = credential.ToString() });
        return (account, credential);
    }

    async Task<T> SuperScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    [Fact]
    public void The_schema_is_at_version_1_or_later() => Assert.True(MigrationCatalog.Embedded.LatestVersion >= 1);

    [Fact]
    public async Task PostgreSQL_generates_sequential_bigint_account_ids()
    {
        var ids = new List<long> { await NewAccountAsync(), await NewAccountAsync(), await NewAccountAsync() };
        Assert.Equal(new long[] { 1, 2, 3 }, ids);
        Assert.Equal("bigint", await SuperScalarAsync<string>("select data_type from information_schema.columns where table_name = 'accounts' and column_name = 'account_id'"));
        Assert.Equal("ALWAYS", await SuperScalarAsync<string>("select identity_generation from information_schema.columns where table_name = 'accounts' and column_name = 'account_id'"));
    }

    [Fact]
    public async Task A_rolled_back_insert_leaves_a_gap_and_no_id_is_ever_reused()
    {
        var first = await NewAccountAsync();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id", transaction: tx);
            await tx.RollbackAsync();
        }
        var next = await NewAccountAsync();
        Assert.Equal(first + 2, next); // the rolled-back id is burned, never handed out
        Assert.Equal(2L, await SuperScalarAsync<long>("select count(distinct account_id) from accounts"));
    }

    [Theory]
    [InlineData("insert into accounts (account_id) values (999)", "428C9")]                              // generated always
    [InlineData("insert into accounts (account_id) overriding system value values (999)", "42501")]     // and no column privilege
    [InlineData("update accounts set account_id = 999", "428C9")]
    [InlineData("update accounts set account_id = default", "42501")]
    [InlineData("update accounts set created_at = now()", "42501")]
    [InlineData("delete from accounts", "42501")]
    [InlineData("truncate accounts cascade", "42501")]
    [InlineData("update account_identities set account_id = 1", "42501")]
    [InlineData("delete from account_identities", "42501")]
    [InlineData("update dev_credentials set account_id = 1", "42501")]
    [InlineData("update dev_credentials set secret_hash = decode(repeat('00', 32), 'hex')", "42501")]
    [InlineData("delete from dev_credentials", "42501")]
    [InlineData("update auth_sessions set account_id = 1", "42501")]
    [InlineData("update auth_sessions set token_hash = decode(repeat('00', 32), 'hex')", "42501")]
    [InlineData("update auth_sessions set expires_at = now() + interval '1 year'", "42501")]
    [InlineData("delete from auth_sessions", "42501")]
    [InlineData("update operator_actions set reason = 'x'", "42501")]
    [InlineData("delete from operator_actions", "42501")]
    [InlineData("insert into operator_actions (id, correlation_id, operator, action, reason, checks, result) overriding system value values (5000, gen_random_uuid(), 'op', 'account_create', 'r', '{}', '{}')", "42501")]
    [InlineData("insert into operator_actions (correlation_id, operator, action, reason, checks, result, created_at) values (gen_random_uuid(), 'op', 'account_create', 'r', '{}', '{}', '2000-01-01')", "42501")]
    [InlineData("insert into dev_credentials (credential_id, account_id, secret_hash, created_at) values (gen_random_uuid(), 1, decode(repeat('03', 32), 'hex'), '2000-01-01')", "42501")]
    [InlineData("insert into dev_credentials (credential_id, account_id, secret_hash, revoked_at) values (gen_random_uuid(), 1, decode(repeat('03', 32), 'hex'), now())", "42501")]
    [InlineData("insert into auth_sessions (session_id, account_id, credential_id, token_hash, expires_at, created_at) values (gen_random_uuid(), 1, gen_random_uuid(), decode(repeat('04', 32), 'hex'), now() + interval '1 hour', '2000-01-01')", "42501")]
    [InlineData("insert into auth_sessions (session_id, account_id, credential_id, token_hash, expires_at, revoked_at) values (gen_random_uuid(), 1, gen_random_uuid(), decode(repeat('04', 32), 'hex'), now() + interval '1 hour', now())", "42501")]
    [InlineData("insert into account_identities (provider, subject, account_id, created_at) values ('dev', 'backdated', 1, '2000-01-01')", "42501")]
    [InlineData("insert into accounts (status, created_at) values ('active', '2000-01-01')", "42501")]
    [InlineData("select nextval('accounts_account_id_seq')", "42501")]
    [InlineData("select nextval('operator_actions_id_seq')", "42501")]
    [InlineData("alter table accounts add column x int", "42501")]
    [InlineData("alter table accounts alter column account_id restart with 1", "42501")]
    [InlineData("select setval(pg_get_serial_sequence('accounts', 'account_id'), 1)", "42501")]
    public async Task The_app_cannot_choose_change_or_remove_ids_and_keys(string sql, string sqlState)
    {
        await SeedAsync("victim");
        Assert.Equal(sqlState, await SqlStateOf(db.AsApp(), sql));
    }

    [Fact]
    public async Task The_app_may_change_only_status_display_name_and_revocation_times()
    {
        var (account, credential) = await SeedAsync("allowed");
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("update accounts set status = 'suspended', display_name = 'Shown' where account_id = @account", new { account });
        await conn.ExecuteAsync("update dev_credentials set revoked_at = now() where credential_id = @credential", new { credential });
        await conn.ExecuteAsync("insert into accounts (display_name) values ('Second')");
    }

    [Fact]
    public async Task The_app_holds_exactly_the_expected_table_and_column_privileges()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var tables = (await conn.QueryAsync<string>("""
            select table_name || ':' || string_agg(privilege_type, ',' order by privilege_type)
            from information_schema.role_table_grants where grantee = 'accounts_app' group by table_name order by table_name
            """)).ToList();
        Assert.Equal(new[]
        {
            "account_identities:SELECT",
            "accounts:SELECT",
            "anomaly_events:SELECT",
            "attempt_insurance:SELECT",
            "attempt_participants:SELECT",
            "auth_sessions:SELECT",
            "deploy_requests:SELECT",
            "dev_credentials:SELECT",
            "energy_holds:SELECT",
            "energy_ledger:SELECT",
            "energy_policies:SELECT",
            "energy_wallets:SELECT",
            "insurance_activations:SELECT",
            "insurance_coverages:SELECT",
            "insurance_policies:SELECT",
            "inventories:SELECT",
            "item_instances:SELECT",
            "item_stacks:DELETE,SELECT",
            "mission_attempts:SELECT",
            "mission_hosts:SELECT",
            "mission_templates:SELECT",
            "operative_choices:SELECT",
            "operatives:SELECT",
            "operator_actions:SELECT",
            "schema_migrations:SELECT",
        }, tables);
        var columns = (await conn.QueryAsync<string>("""
            select table_name || '.' || column_name || ':' || string_agg(privilege_type, ',' order by privilege_type)
            from information_schema.column_privileges
            where grantee = 'accounts_app' and privilege_type in ('INSERT', 'UPDATE')
              and (table_name, column_name, privilege_type) not in (
                select table_name, column_name, privilege_type from information_schema.column_privileges c
                join information_schema.role_table_grants t using (table_name, privilege_type)
                where c.grantee = 'accounts_app' and t.grantee = 'accounts_app')
            group by table_name, column_name order by 1
            """)).ToList();
        Assert.Equal(new[]
        {
            "account_identities.account_id:INSERT",
            "account_identities.provider:INSERT",
            "account_identities.subject:INSERT",
            "accounts.display_name:INSERT,UPDATE",
            "accounts.status:INSERT,UPDATE",
            "anomaly_events.account_id:INSERT",
            "anomaly_events.attempt_id:INSERT",
            "anomaly_events.build_revision:INSERT",
            "anomaly_events.dedupe_key:INSERT",
            "anomaly_events.detail:INSERT",
            "anomaly_events.host_id:INSERT",
            "anomaly_events.kind:INSERT",
            "attempt_insurance.account_id:INSERT",
            "attempt_insurance.activation_id:INSERT",
            "attempt_insurance.attempt_id:INSERT",
            "attempt_insurance.policy_id:INSERT",
            "attempt_insurance.policy_version:INSERT",
            "attempt_insurance.terms:INSERT",
            "attempt_participants.account_id:INSERT",
            "attempt_participants.attempt_id:INSERT",
            "attempt_participants.energy_cost:INSERT",
            "attempt_participants.inventory_version:INSERT",
            "attempt_participants.loadout_snapshot:INSERT",
            "attempt_participants.state:UPDATE",
            "attempt_participants.template_id:INSERT",
            "attempt_participants.template_version:INSERT",
            "auth_sessions.account_id:INSERT",
            "auth_sessions.credential_id:INSERT",
            "auth_sessions.expires_at:INSERT",
            "auth_sessions.revoked_at:UPDATE",
            "auth_sessions.session_id:INSERT",
            "auth_sessions.token_hash:INSERT",
            "deploy_requests.account_id:INSERT",
            "deploy_requests.attempt_id:INSERT",
            "deploy_requests.idempotency_key:INSERT",
            "deploy_requests.request_hash:INSERT",
            "deploy_requests.response:INSERT",
            "dev_credentials.account_id:INSERT",
            "dev_credentials.credential_id:INSERT",
            "dev_credentials.revoked_at:UPDATE",
            "dev_credentials.secret_hash:INSERT",
            "energy_holds.account_id:INSERT",
            "energy_holds.amount:INSERT",
            "energy_holds.attempt_id:INSERT",
            "energy_holds.state:UPDATE",
            "energy_ledger.account_id:INSERT",
            "energy_ledger.attempt_id:INSERT",
            "energy_ledger.balance_after:INSERT",
            "energy_ledger.delta:INSERT",
            "energy_ledger.idempotency_key:INSERT",
            "energy_ledger.kind:INSERT",
            "energy_policies.cap:INSERT",
            "energy_policies.opening_balance:INSERT",
            "energy_policies.regen_amount:INSERT",
            "energy_policies.regen_interval:INSERT",
            "energy_policies.void_refund_pct:INSERT",
            "energy_wallets.account_id:INSERT",
            "energy_wallets.balance:INSERT,UPDATE",
            "energy_wallets.policy_version:INSERT,UPDATE",
            "energy_wallets.regen_anchor:INSERT,UPDATE",
            "energy_wallets.regen_progress_us:INSERT,UPDATE",
            "insurance_activations.account_id:INSERT",
            "insurance_activations.activation_id:INSERT",
            "insurance_activations.duration:INSERT",
            "insurance_activations.idempotency_key:INSERT",
            "insurance_activations.policy_id:INSERT",
            "insurance_activations.policy_version:INSERT",
            "insurance_activations.previous_expires_at:INSERT",
            "insurance_activations.resulting_expires_at:INSERT",
            "insurance_activations.source:INSERT",
            "insurance_coverages.account_id:INSERT",
            "insurance_coverages.activation_id:INSERT,UPDATE",
            "insurance_coverages.expires_at:INSERT,UPDATE",
            "insurance_coverages.policy_id:INSERT,UPDATE",
            "insurance_coverages.policy_version:INSERT,UPDATE",
            "insurance_coverages.starts_at:INSERT,UPDATE",
            "insurance_policies.policy_id:INSERT",
            "insurance_policies.terms:INSERT",
            "insurance_policies.version:INSERT",
            "inventories.account_id:INSERT",
            "inventories.starter_catalogue_version:UPDATE",
            "inventories.starter_granted:UPDATE",
            "inventories.starter_granted_at:UPDATE",
            "inventories.version:UPDATE",
            "item_instances.account_id:INSERT",
            "item_instances.definition_id:INSERT",
            "item_instances.equipped_slot:INSERT,UPDATE",
            "item_instances.operative_id:INSERT,UPDATE",
            "item_instances.origin:INSERT",
            "item_instances.origin_ref:INSERT",
            "item_stacks.account_id:INSERT",
            "item_stacks.definition_id:INSERT",
            "item_stacks.operative_id:INSERT",
            "item_stacks.quantity:INSERT,UPDATE",
            "mission_attempts.assign_deadline:INSERT",
            "mission_attempts.attempt_id:INSERT",
            "mission_attempts.cancel_reason:UPDATE",
            "mission_attempts.created_by:INSERT",
            "mission_attempts.ended_at:UPDATE",
            "mission_attempts.retention_salt:INSERT",
            "mission_attempts.seed:INSERT",
            "mission_attempts.state:UPDATE",
            "mission_attempts.template_id:INSERT",
            "mission_attempts.template_version:INSERT",
            "mission_hosts.capacity:INSERT",
            "mission_hosts.enabled:INSERT",
            "mission_hosts.host_id:INSERT",
            "mission_hosts.kind:INSERT",
            "mission_hosts.last_heartbeat_at:UPDATE",
            "mission_hosts.secret_hash:INSERT",
            "mission_templates.energy_cost:INSERT",
            "mission_templates.max_operatives:INSERT",
            "mission_templates.min_operatives:INSERT",
            "mission_templates.template_id:INSERT",
            "mission_templates.version:INSERT",
            "operative_choices.choice_id:INSERT",
            "operative_choices.operative_id:INSERT",
            "operative_choices.ordinal:INSERT",
            "operatives.account_id:INSERT",
            "operatives.definition_id:INSERT",
            "operatives.origin:INSERT",
            "operatives.origin_ref:INSERT",
            "operator_actions.account_id:INSERT",
            "operator_actions.action:INSERT",
            "operator_actions.attempt_id:INSERT",
            "operator_actions.checks:INSERT",
            "operator_actions.correlation_id:INSERT",
            "operator_actions.credential_id:INSERT",
            "operator_actions.host_id:INSERT",
            "operator_actions.operator:INSERT",
            "operator_actions.reason:INSERT",
            "operator_actions.result:INSERT",
        }, columns.OrderBy(c => c, StringComparer.Ordinal));
        // Identity sequences need no privilege for inserts; the app holds none, so it can't burn or reset ids.
        Assert.Empty(await conn.QueryAsync<string>(
            "select relname::text from pg_class where case when relkind = 'S' then has_sequence_privilege('accounts_app', oid, 'USAGE, SELECT, UPDATE') else false end"));
        Assert.Equal(7L, await conn.ExecuteScalarAsync<long>("select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'public' and c.relkind = 'S'")); // 0006 adds three; 0007 none
    }

    [Fact]
    public async Task The_app_may_lock_account_and_credential_rows_for_share_as_login_does()
    {
        var (account, credential) = await SeedAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        Assert.Equal("active", await conn.ExecuteScalarAsync<string>("select status from accounts where account_id = @account for share", new { account }, tx));
        Assert.Equal(credential, await conn.ExecuteScalarAsync<Guid>("select credential_id from dev_credentials where account_id = @account for share", new { account }, tx));
    }

    [Fact]
    public async Task Rows_are_stamped_when_their_statement_runs_not_when_their_transaction_began()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var defaults = (await conn.QueryAsync<string>(
            "select table_name || '=' || column_default from information_schema.columns where table_schema = 'public' and column_name = 'created_at' and table_name <> 'schema_migrations' order by 1")).ToList();
        Assert.Equal(new[]
        {
            "account_identities=statement_timestamp()", "accounts=statement_timestamp()", "anomaly_events=statement_timestamp()",
            "auth_sessions=statement_timestamp()", "deploy_requests=statement_timestamp()", "dev_credentials=statement_timestamp()",
            "energy_ledger=statement_timestamp()", "energy_policies=statement_timestamp()", "insurance_activations=statement_timestamp()",
            "insurance_policies=statement_timestamp()", "item_instances=statement_timestamp()", "mission_attempts=statement_timestamp()",
            "mission_hosts=statement_timestamp()", "mission_templates=statement_timestamp()", "operatives=statement_timestamp()",
            "operator_actions=statement_timestamp()",
        }, defaults.OrderBy(d => d, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Every_account_id_column_is_bigint_and_other_ids_stay_uuid()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var types = (await conn.QueryAsync<(string Table, string Column, string Type)>("""
            select table_name, column_name, data_type from information_schema.columns
            where table_schema = 'public' and column_name in ('account_id', 'credential_id', 'session_id', 'correlation_id', 'attempt_id', 'operative_id', 'item_id')
            order by table_name, column_name
            """)).ToList();
        string[] bigintColumns = ["account_id", "operative_id", "item_id"];
        Assert.All(types.Where(t => bigintColumns.Contains(t.Column)), t => Assert.Equal("bigint", t.Type));
        Assert.All(types.Where(t => !bigintColumns.Contains(t.Column)), t => Assert.Equal("uuid", t.Type));
        Assert.Equal(["account_identities", "accounts", "anomaly_events", "attempt_insurance", "attempt_participants", "auth_sessions", "deploy_requests", "dev_credentials",
                      "energy_holds", "energy_ledger", "energy_wallets", "insurance_activations", "insurance_coverages", "inventories", "item_instances", "item_stacks", "operatives", "operator_actions"],
            types.Where(t => t.Column == "account_id").Select(t => t.Table).Order(StringComparer.Ordinal).ToArray());
        // Operative and item ids are bigint identities; stack and choice rows carry them as bigint references.
        Assert.Equal(["item_instances.item_id", "item_instances.operative_id", "item_stacks.operative_id", "operative_choices.operative_id", "operatives.operative_id"],
            types.Where(t => t.Column is "operative_id" or "item_id").Select(t => $"{t.Table}.{t.Column}").Order(StringComparer.Ordinal).ToArray());
        // Mission attempt ids stay UUIDv7 wherever they appear (0007's attempt tables included).
        Assert.DoesNotContain(types, t => t.Column == "attempt_id" && t.Type != "uuid");
        Assert.Contains(types, t => t.Table == "mission_attempts" && t.Column == "attempt_id");
    }

    [Theory]
    [InlineData("insert into accounts (status) values ('deleted')")]
    [InlineData("insert into accounts (display_name) values ('')")]
    [InlineData("insert into accounts (display_name) values (E'two\\nlines')")]
    [InlineData("insert into accounts (display_name) values (repeat('n', 65))")]
    [InlineData("insert into account_identities (provider, subject, account_id) values ('google', 'x', @a)")]
    [InlineData("insert into account_identities (provider, subject, account_id) values ('dev', 'Upper', @a)")]
    [InlineData("insert into account_identities (provider, subject, account_id) values ('dev', E'tester\\n', @a)")]
    [InlineData("insert into account_identities (provider, subject, account_id) values ('dev', repeat('a', 65), @a)")]
    [InlineData("insert into dev_credentials (credential_id, account_id, secret_hash) values (gen_random_uuid(), @a, '\\x00')")]
    [InlineData("insert into operator_actions (correlation_id, operator, action, reason, checks, result) values (gen_random_uuid(), E'ops\\nforged', 'account_create', 'r', '{}', '{}')")]
    [InlineData("insert into operator_actions (correlation_id, operator, action, reason, checks, result) values (gen_random_uuid(), 'ops', 'account_create', E'r\\tx', '{}', '{}')")]
    public async Task Check_constraints_refuse_bad_rows(string sql)
    {
        var a = await NewAccountAsync();
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), sql, new { a }));
    }

    [Fact]
    public async Task A_non_positive_account_id_is_refused_even_for_the_superuser() =>
        Assert.Equal("23514", await SqlStateOf(db.AsSuperuser(), "insert into accounts (account_id) overriding system value values (0)"));

    [Theory]
    [InlineData("insert into account_identities (provider, subject, account_id) values ('dev', 'ghost', 999)")]
    [InlineData("insert into dev_credentials (credential_id, account_id, secret_hash) values (gen_random_uuid(), 999, decode(repeat('01', 32), 'hex'))")]
    [InlineData("insert into operator_actions (correlation_id, operator, action, account_id, reason, checks, result) values (gen_random_uuid(), 'op', 'account_suspend', 999, 'r', '{}', '{}')")]
    [InlineData("insert into operator_actions (correlation_id, operator, action, credential_id, reason, checks, result) values (gen_random_uuid(), 'op', 'credential_revoke', gen_random_uuid(), 'r', '{}', '{}')")]
    public async Task Foreign_keys_refuse_an_unknown_account_or_credential(string sql) =>
        Assert.Equal("23503", await SqlStateOf(db.AsApp(), sql));

    [Fact]
    public async Task A_session_cannot_name_a_credential_of_another_account()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        Assert.Equal("23503", await SqlStateOf(db.AsApp(),
            $"insert into auth_sessions (session_id, account_id, credential_id, token_hash, expires_at) values (gen_random_uuid(), @a, @c, {Hash32}, now() + interval '1 hour')",
            new { a = a.Account, c = b.Credential }));
    }

    [Fact]
    public async Task A_session_must_expire_after_it_starts()
    {
        var a = await SeedAsync();
        Assert.Equal("23514", await SqlStateOf(db.AsApp(),
            $"insert into auth_sessions (session_id, account_id, credential_id, token_hash, expires_at) values (gen_random_uuid(), @a, @c, {Hash32}, now())",
            new { a = a.Account, c = a.Credential }));
    }

    [Fact]
    public async Task An_account_has_at_most_one_dev_identity_and_a_handle_names_one_account()
    {
        var a = await SeedAsync("delta");
        var b = await SeedAsync();
        Assert.Equal("23505", await SqlStateOf(db.AsApp(), "insert into account_identities (provider, subject, account_id) values ('dev', 'delta-2', @a)", new { a = a.Account }));
        Assert.Equal("23505", await SqlStateOf(db.AsApp(), "insert into account_identities (provider, subject, account_id) values ('dev', 'delta', @b)", new { b = b.Account }));
    }

    [Fact]
    public async Task An_account_has_at_most_one_live_credential()
    {
        var a = await SeedAsync();
        const string insert = "insert into dev_credentials (credential_id, account_id, secret_hash) values (gen_random_uuid(), @a, decode(repeat('02', 32), 'hex'))";
        Assert.Equal("23505", await SqlStateOf(db.AsApp(), insert, new { a = a.Account }));
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("update dev_credentials set revoked_at = now() where credential_id = @c", new { c = a.Credential });
        await conn.ExecuteAsync(insert, new { a = a.Account }); // a replacement after revocation is fine
    }

    [Fact]
    public async Task The_app_can_append_operator_actions()
    {
        var a = await NewAccountAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into operator_actions (correlation_id, operator, action, account_id, reason, checks, result) values (gen_random_uuid(), 'op', 'account_create', @a, 'r', '{}', '{}')", new { a });
    }

    // Stage 1 carry-over: PostgreSQL grants EXECUTE to PUBLIC on every new function unless the global default is revoked.
    [Fact]
    public async Task Functions_the_migrator_creates_later_are_not_executable_by_the_app_or_public_until_granted()
    {
        await using (var conn = await db.AsMigrator().OpenConnectionAsync())
            await conn.ExecuteAsync("create function bg_probe() returns int language sql as 'select 1'");
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "select bg_probe()"));
        Assert.False(await SuperScalarAsync<bool>("select has_function_privilege('public', 'bg_probe()', 'execute')"));
        Assert.False(await SuperScalarAsync<bool>("select has_function_privilege('accounts_app', 'bg_probe()', 'execute')"));
        await using (var conn = await db.AsMigrator().OpenConnectionAsync())
            await conn.ExecuteAsync("grant execute on function bg_probe() to accounts_app"); // the explicit path a later migration uses
        await using var app = await db.AsApp().OpenConnectionAsync();
        Assert.Equal(1, await app.ExecuteScalarAsync<int>("select bg_probe()"));
    }

    [Fact]
    public async Task The_invariants_hold_on_consistent_data_and_catch_live_sessions_of_revoked_credentials_or_suspended_accounts()
    {
        var a = await SeedAsync("echo");
        var b = await SeedAsync("foxtrot");
        Assert.Empty(await InvariantRunner.RunAsync(db.AsSuperuser(), MigrationCatalog.Embedded.LatestVersion, InvariantRunner.RealDirectory));
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await conn.ExecuteAsync($"insert into auth_sessions (session_id, account_id, credential_id, token_hash, expires_at) values (gen_random_uuid(), @a, @c, {Hash32}, now() + interval '1 hour')", new { a = a.Account, c = a.Credential });
        await conn.ExecuteAsync("update dev_credentials set revoked_at = now() where credential_id = @c", new { c = a.Credential });
        var failures = await InvariantRunner.RunAsync(db.AsSuperuser(), MigrationCatalog.Embedded.LatestVersion, InvariantRunner.RealDirectory);
        Assert.Equal(["0001_revocations_cascade.sql: 1 violating row(s)"], failures);

        await conn.ExecuteAsync("update auth_sessions set revoked_at = now()");
        await conn.ExecuteAsync($"insert into auth_sessions (session_id, account_id, credential_id, token_hash, expires_at) values (gen_random_uuid(), @a, @c, decode(repeat('cd', 32), 'hex'), now() + interval '1 hour')", new { a = b.Account, c = b.Credential });
        await conn.ExecuteAsync("update accounts set status = 'suspended' where account_id = @a", new { a = b.Account });
        failures = await InvariantRunner.RunAsync(db.AsSuperuser(), MigrationCatalog.Embedded.LatestVersion, InvariantRunner.RealDirectory);
        Assert.Equal(["0001_revocations_cascade.sql: 1 violating row(s)"], failures);
    }
}
