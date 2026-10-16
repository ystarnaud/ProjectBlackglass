using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Migration 0006's privileges (Stage 6; spec §15.1) against a real PostgreSQL 17. The app role reads the four
/// energy tables; it inserts policies, wallets, ledger entries and anomalies through the listed columns only, and updates
/// only a wallet's balance, anchor, progress and policy version. It can never delete or truncate anything, set an
/// effective time, touch an identity sequence or execute a trigger function; it can take the policy advisory lock. The
/// outsider role and PUBLIC hold nothing.</summary>
[Trait("Requires", "Postgres")]
public class EnergyPrivilegeTests : IAsyncLifetime
{
    TestDatabase db = null!;
    static readonly string[] EnergyTables = ["anomaly_events", "energy_ledger", "energy_policies", "energy_wallets"];
    static readonly string[] EnergySequences = ["anomaly_events_id_seq", "energy_ledger_entry_id_seq", "energy_policies_version_seq"];
    static readonly string[] TriggerFunctions =
        ["energy_ledger_is_append_only", "energy_policy_effective_at", "energy_policy_is_immutable", "energy_wallet_forward_only", "energy_wallet_matches_ledger"];

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

    /// <summary>An account with an open wallet (balance 50) and an anomaly, as the app writes them.</summary>
    async Task<long> SeedAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        var account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync(
                "insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@account, 50, clock_timestamp(), 0, 1)",
                new { account }, tx);
            await conn.ExecuteAsync(
                "insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key) values (@account, null, 'open', 50, 50, 'open')",
                new { account }, tx);
            await tx.CommitAsync();
        }
        await conn.ExecuteAsync(
            "insert into anomaly_events (kind, account_id, host_id, build_revision, attempt_id, dedupe_key, detail) values ('probe', @account, null, null, null, 'd', '{}')",
            new { account });
        return account;
    }

    [Fact]
    public async Task The_app_role_has_exactly_the_energy_privileges()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();

        // Table-level grants, as the catalogue lists them for the role itself.
        var tables = (await conn.QueryAsync<string>("""
            select table_name || ':' || string_agg(privilege_type, ',' order by privilege_type)
            from information_schema.role_table_grants
            where grantee = 'accounts_app' and table_name = any(@EnergyTables)
            group by table_name
            """, new { EnergyTables })).ToList();
        Assert.Equal(new[] { "anomaly_events:SELECT", "energy_ledger:SELECT", "energy_policies:SELECT", "energy_wallets:SELECT" },
            tables.OrderBy(t => t, StringComparer.Ordinal));

        // Column-level INSERT and UPDATE grants beyond the table-level ones.
        var granted = (await conn.QueryAsync<string>("""
            select table_name || '.' || column_name || ':' || string_agg(privilege_type, ',' order by privilege_type)
            from information_schema.column_privileges
            where grantee = 'accounts_app' and privilege_type in ('INSERT', 'UPDATE') and table_name = any(@EnergyTables)
            group by table_name, column_name
            """, new { EnergyTables })).ToList();
        Assert.Equal(new[]
        {
            "anomaly_events.account_id:INSERT",
            "anomaly_events.attempt_id:INSERT",
            "anomaly_events.build_revision:INSERT",
            "anomaly_events.dedupe_key:INSERT",
            "anomaly_events.detail:INSERT",
            "anomaly_events.host_id:INSERT",
            "anomaly_events.kind:INSERT",
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
        }, granted.OrderBy(c => c, StringComparer.Ordinal));

        // The effective privilege on every column (table-level or column-level, from any grant the role inherits or PUBLIC holds).
        var columns = (await conn.QueryAsync<string>("""
            select c.relname || '.' || a.attname || ':' || string_agg(p.priv, ',' order by p.priv)
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace and n.nspname = 'public'
            join pg_attribute a on a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped
            cross join (values ('SELECT'), ('INSERT'), ('UPDATE'), ('REFERENCES')) as p(priv)
            where c.relname = any(@EnergyTables) and c.relkind = 'r'
              and has_column_privilege('accounts_app', c.oid, a.attnum, p.priv)
            group by c.relname, a.attname
            """, new { EnergyTables })).ToList();
        Assert.Equal(new[]
        {
            "anomaly_events.account_id:INSERT,SELECT",
            "anomaly_events.attempt_id:INSERT,SELECT",
            "anomaly_events.build_revision:INSERT,SELECT",
            "anomaly_events.created_at:SELECT",
            "anomaly_events.dedupe_key:INSERT,SELECT",
            "anomaly_events.detail:INSERT,SELECT",
            "anomaly_events.host_id:INSERT,SELECT",
            "anomaly_events.id:SELECT",
            "anomaly_events.kind:INSERT,SELECT",
            "anomaly_events.reviewed_at:SELECT",
            "anomaly_events.reviewed_note:SELECT",
            "energy_ledger.account_id:INSERT,SELECT",
            "energy_ledger.attempt_id:INSERT,SELECT",
            "energy_ledger.balance_after:INSERT,SELECT",
            "energy_ledger.created_at:SELECT",
            "energy_ledger.delta:INSERT,SELECT",
            "energy_ledger.entry_id:SELECT",
            "energy_ledger.idempotency_key:INSERT,SELECT",
            "energy_ledger.kind:INSERT,SELECT",
            "energy_policies.cap:INSERT,SELECT",
            "energy_policies.created_at:SELECT",
            "energy_policies.effective_at:SELECT",
            "energy_policies.opening_balance:INSERT,SELECT",
            "energy_policies.regen_amount:INSERT,SELECT",
            "energy_policies.regen_interval:INSERT,SELECT",
            "energy_policies.version:SELECT",
            "energy_policies.void_refund_pct:INSERT,SELECT",
            "energy_wallets.account_id:INSERT,SELECT",
            "energy_wallets.balance:INSERT,SELECT,UPDATE",
            "energy_wallets.policy_version:INSERT,SELECT,UPDATE",
            "energy_wallets.regen_anchor:INSERT,SELECT,UPDATE",
            "energy_wallets.regen_progress_us:INSERT,SELECT,UPDATE",
        }, columns.OrderBy(c => c, StringComparer.Ordinal));

        // No other table privilege on any of the four, effective.
        Assert.Empty(await conn.QueryAsync<string>("""
            select c.relname || ':' || p.priv
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace and n.nspname = 'public'
            cross join (values ('DELETE'), ('TRUNCATE'), ('TRIGGER')) as p(priv)
            where c.relname = any(@EnergyTables) and c.relkind = 'r' and has_table_privilege('accounts_app', c.oid, p.priv)
            """, new { EnergyTables }));

        // Nothing on the three identity sequences (inserts need none), nor on any other.
        Assert.Equal(EnergySequences, (await conn.QueryAsync<string>(
            "select relname::text from pg_class where relkind = 'S' and relname = any(@EnergySequences) order by relname", new { EnergySequences })).ToArray());
        Assert.Empty(await conn.QueryAsync<string>(
            "select relname::text from pg_class where case when relkind = 'S' then has_sequence_privilege('accounts_app', oid, 'USAGE, SELECT, UPDATE') else false end"));
    }

    [Fact]
    public async Task The_app_role_cannot_delete_a_wallet_set_effective_at_or_touch_a_sequence()
    {
        var account = await SeedAsync();
        foreach (var sql in new[]
        {
            // wallets: no delete or truncate, no other column
            "delete from energy_wallets where account_id = @account",
            "truncate energy_wallets cascade",
            "update energy_wallets set account_id = account_id + 1 where account_id = @account",
            "update energy_wallets set balance = 49, account_id = account_id where account_id = @account",   // one forbidden column spoils the statement
            // policies: the database sets effective_at and created_at; no update, delete or truncate
            "insert into energy_policies (cap, opening_balance, regen_interval, regen_amount, void_refund_pct, effective_at) values (50, 50, interval '10 minutes', 1, 100, now())",
            "insert into energy_policies (cap, opening_balance, regen_interval, regen_amount, void_refund_pct, created_at) values (50, 50, interval '10 minutes', 1, 100, now())",
            "update energy_policies set cap = 60",
            "update energy_policies set effective_at = now()",
            "delete from energy_policies",
            "truncate energy_policies cascade",
            // ledger: append-only by privilege too
            "update energy_ledger set delta = 49 where account_id = @account",
            "delete from energy_ledger where account_id = @account",
            "truncate energy_ledger",
            "insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key, created_at) values (@account, 'regen', 1, 51, 'regen:1', now())",
            // anomalies: the app records, it never reviews or removes
            "update anomaly_events set reviewed_at = now(), reviewed_note = 'n'",
            "insert into anomaly_events (kind, dedupe_key, detail, reviewed_at) values ('probe', 'e', '{}', now())",
            "delete from anomaly_events",
            "truncate anomaly_events",
        })
            Assert.Equal("42501", await SqlStateOf(db.AsApp(), sql, new { account }));

        foreach (var sequence in EnergySequences)
        {
            Assert.Equal("42501", await SqlStateOf(db.AsApp(), "select nextval(@sequence::regclass)", new { sequence }));
            Assert.Equal("42501", await SqlStateOf(db.AsApp(), "select setval(@sequence::regclass, 1000)", new { sequence }));
            Assert.Equal("42501", await SqlStateOf(db.AsApp(), "select currval(@sequence::regclass)", new { sequence }));
        }
        // GENERATED ALWAYS identities: an explicit value is refused before privileges (428C9), even with OVERRIDING SYSTEM
        // VALUE the app holds no INSERT on the column (42501).
        Assert.Equal("428C9", await SqlStateOf(db.AsApp(),
            "insert into energy_ledger (entry_id, account_id, kind, delta, balance_after, idempotency_key) values (1, @account, 'regen', 1, 51, 'regen:1')", new { account }));
        Assert.Equal("42501", await SqlStateOf(db.AsApp(),
            "insert into energy_ledger (entry_id, account_id, kind, delta, balance_after, idempotency_key) overriding system value values (1000, @account, 'regen', 1, 51, 'regen:1')", new { account }));
        Assert.Equal("42501", await SqlStateOf(db.AsApp(),
            "insert into energy_policies (version, cap, opening_balance, regen_interval, regen_amount, void_refund_pct) overriding system value values (99, 50, 50, interval '10 minutes', 1, 100)"));

        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        Assert.Equal((1L, 1L, 1L, 1L, 50), await su.QuerySingleAsync<(long, long, long, long, int)>(
            "select (select count(*) from energy_wallets), (select count(*) from energy_ledger), (select count(*) from energy_policies), (select count(*) from anomaly_events), (select cap from energy_policies)"));
    }

    [Fact]
    public async Task The_app_role_can_write_what_it_is_granted()
    {
        var account = await SeedAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // The operator verbs' lock order: the account, then the wallet (FOR UPDATE needs one UPDATE column), then the policy lock.
        await conn.ExecuteAsync("select 1 from accounts where account_id = @account for update", new { account }, tx);
        Assert.Equal(50, await conn.ExecuteScalarAsync<int>("select balance from energy_wallets where account_id = @account for update", new { account }, tx));
        await conn.ExecuteAsync("select pg_advisory_xact_lock_shared(4242)", transaction: tx);
        await conn.ExecuteAsync(
            "insert into energy_policies (cap, opening_balance, regen_interval, regen_amount, void_refund_pct) values (60, 60, interval '5 minutes', 2, 50)", transaction: tx);
        await conn.ExecuteAsync(
            "update energy_wallets set balance = 52, regen_anchor = clock_timestamp(), regen_progress_us = 1, policy_version = 2 where account_id = @account", new { account }, tx);
        await conn.ExecuteAsync(
            "insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key) values (@account, null, 'regen', 2, 52, 'regen:1')", new { account }, tx);
        await conn.ExecuteAsync(
            "insert into anomaly_events (kind, account_id, dedupe_key, detail) values ('probe', @account, 'd', '{}') on conflict do nothing", new { account }, tx);
        await tx.CommitAsync();
        Assert.Equal(52, await conn.ExecuteScalarAsync<int>("select balance from energy_wallets where account_id = @account", new { account }));
    }

    [Fact]
    public async Task The_app_role_can_take_the_policy_advisory_lock()
    {
        const long Key = 0x426C_6745_6E50_6F6CL;
        await using var a = await db.AsApp().OpenConnectionAsync();
        await using var b = await db.AsApp().OpenConnectionAsync();
        await using var ta = await a.BeginTransactionAsync();
        await using var tb = await b.BeginTransactionAsync();
        // Shared by two readers at once (materializations and projections) ...
        await a.ExecuteAsync("select pg_advisory_xact_lock_shared(@Key)", new { Key }, ta);
        await b.ExecuteAsync("select pg_advisory_xact_lock_shared(@Key)", new { Key }, tb);
        await tb.CommitAsync();
        // ... and exclusive for a publication, which a shared holder blocks.
        await using var c = await db.AsApp().OpenConnectionAsync();
        await using (var tc = await c.BeginTransactionAsync())
        {
            Assert.False(await c.ExecuteScalarAsync<bool>("select pg_try_advisory_xact_lock(@Key)", new { Key }, tc));
            await ta.CommitAsync();
            Assert.True(await c.ExecuteScalarAsync<bool>("select pg_try_advisory_xact_lock(@Key)", new { Key }, tc));
            await c.ExecuteAsync("select pg_advisory_xact_lock(@Key)", new { Key }, tc);   // re-entrant within the transaction
            await tc.CommitAsync();
        }
    }

    [Fact]
    public async Task Every_energy_trigger_function_is_unexecutable_by_the_app_and_public()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var functions = (await conn.QueryAsync<(string Name, bool App, bool Public, bool Definer)>("""
            select p.proname::text, has_function_privilege('accounts_app', p.oid, 'execute'), has_function_privilege('public', p.oid, 'execute'), p.prosecdef
            from pg_proc p join pg_namespace n on n.oid = p.pronamespace and n.nspname = 'public'
            where p.proname = any(@TriggerFunctions) order by p.proname
            """, new { TriggerFunctions })).ToList();
        Assert.Equal(TriggerFunctions, functions.Select(f => f.Name));
        Assert.All(functions, f => Assert.Equal((false, false, false), (f.App, f.Public, f.Definer)));
        // Every trigger on the energy tables is enabled in the ordinary (origin) mode.
        var triggers = (await conn.QueryAsync<string>("""
            select c.relname || '.' || t.tgname || ':' || t.tgenabled::text || ':' || case when t.tgdeferrable then 'deferrable' else 'immediate' end
            from pg_trigger t join pg_class c on c.oid = t.tgrelid
            where not t.tgisinternal and c.relname = any(@EnergyTables) order by 1
            """, new { EnergyTables })).ToList();
        Assert.Equal(new[]
        {
            "energy_ledger.energy_ledger_is_append_only:O:immediate",
            "energy_ledger.energy_ledger_matches_wallet:O:deferrable",
            "energy_policies.energy_policy_effective_at:O:immediate",
            "energy_policies.energy_policy_is_immutable:O:immediate",
            "energy_wallets.energy_wallet_forward_only:O:immediate",
            "energy_wallets.energy_wallet_matches_ledger:O:deferrable",
        }, triggers);
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "select energy_policy_effective_at()"));
    }

    [Fact]
    public async Task The_outsider_role_reads_nothing()
    {
        await SeedAsync();
        await using (var conn = await db.AsSuperuser().OpenConnectionAsync())
        {
            var held = (await conn.QueryAsync<string>("""
                select r.role || ':' || t.name || ':' || p.priv
                from unnest(@EnergyTables) as t(name)
                cross join (values ('bg_test_outsider'), ('public')) as r(role)
                cross join (values ('SELECT'), ('INSERT'), ('UPDATE'), ('REFERENCES')) as p(priv)
                where has_any_column_privilege(r.role, t.name, p.priv)
                union all
                select r.role || ':' || t.name || ':' || p.priv
                from unnest(@EnergyTables) as t(name)
                cross join (values ('bg_test_outsider'), ('public')) as r(role)
                cross join (values ('DELETE'), ('TRUNCATE'), ('TRIGGER')) as p(priv)
                where has_table_privilege(r.role, t.name, p.priv)
                union all
                select r.role || ':' || s.name || ':sequence'
                from unnest(@EnergySequences) as s(name)
                cross join (values ('bg_test_outsider'), ('public')) as r(role)
                where has_sequence_privilege(r.role, s.name, 'USAGE, SELECT, UPDATE')
                """, new { EnergyTables, EnergySequences })).ToList();
            Assert.Empty(held);
        }

        // The outsider can't connect; with CONNECT granted for this throwaway database, it still reads nothing.
        var e = await Assert.ThrowsAsync<PostgresException>(async () => { await using var c = await db.AsOutsider().OpenConnectionAsync(); });
        Assert.Equal("42501", e.SqlState);
        await using (var conn = await db.AsSuperuser().OpenConnectionAsync())
            await conn.ExecuteAsync($"grant connect on database \"{db.Name}\" to bg_test_outsider");
        foreach (var table in EnergyTables)
            Assert.Equal("42501", await SqlStateOf(db.AsOutsider(), $"select count(*) from public.{table}"));
    }
}
