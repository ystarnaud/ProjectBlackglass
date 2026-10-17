using Dapper;
using Npgsql;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>
/// Test-only fault injection (plan Task 8), installed by the superuser in a test database only (no production seam): a raising
/// trigger that fails a named table's operation with a chosen SQLSTATE, and a pausing trigger that makes the operation wait on
/// an advisory lock the test holds. Triggers fire whatever the invoker's privileges, so the app role meets them as it would a
/// real failure.
/// </summary>
public static class FaultTriggers
{
    const string Functions = """
        create or replace function bg_test_fault() returns trigger language plpgsql as $$
        begin raise exception 'injected fault' using errcode = tg_argv[0]; end $$;
        create or replace function bg_test_pause() returns trigger language plpgsql as $$
        begin perform pg_advisory_xact_lock(tg_argv[0]::bigint); return coalesce(new, old); end $$;
        """;

    public static async Task InstallRaiseAsync(TestDatabase db, string table, string operation, string sqlState = "P0001")
    {
        await AccountTestKit.ExecuteAsSuperuserAsync(db, Functions);
        await AccountTestKit.ExecuteAsSuperuserAsync(db, $"create trigger bg_test_fault before {operation} on {table} for each row execute function bg_test_fault('{sqlState}')");
    }

    public static async Task InstallPauseAsync(TestDatabase db, string table, string operation, long key)
    {
        await AccountTestKit.ExecuteAsSuperuserAsync(db, Functions);
        await AccountTestKit.ExecuteAsSuperuserAsync(db, $"create trigger bg_test_pause before {operation} on {table} for each row execute function bg_test_pause('{key}')");
    }

    public static Task RemoveAsync(TestDatabase db, string table) =>
        AccountTestKit.ExecuteAsSuperuserAsync(db, $"drop trigger if exists bg_test_fault on {table}; drop trigger if exists bg_test_pause on {table}");

    /// <summary>Holds advisory lock <paramref name="key"/> on its own connection until disposed: a paused operation waits on it.</summary>
    public static async Task<IAsyncDisposable> HoldAsync(TestDatabase db, long key)
    {
        var conn = await db.AsSuperuser().OpenConnectionAsync();
        await conn.ExecuteAsync("select pg_advisory_lock(@key)", new { key });
        return new Held(conn, key);
    }

    sealed class Held(NpgsqlConnection conn, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await conn.ExecuteAsync("select pg_advisory_unlock(@key)", new { key });
            await conn.DisposeAsync();
        }
    }

    /// <summary>Returns once a backend of this database waits on advisory lock <paramref name="key"/>, with its pid.</summary>
    public static async Task<int> WaitForPausedAsync(TestDatabase db, long key)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var pid = await su.ExecuteScalarAsync<int?>("""
                select a.pid from pg_locks l join pg_stat_activity a on a.pid = l.pid
                where l.locktype = 'advisory' and not l.granted and a.datname = current_database() and l.objid = (@key & 4294967295)::oid limit 1
                """, new { key });
            if (pid is { } p) return p;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("No backend paused on the advisory lock.");
            await Task.Delay(20);
        }
    }

    /// <summary>A digest of every table a settlement, quarantine or void writes (row contents in key order).</summary>
    public static Task<string> DigestAsync(TestDatabase db) => AccountTestKit.ScalarAsSuperuserAsync<string>(db, """
        select md5(string_agg(t, '|' order by t)) from (
          select 'stacks' || coalesce((select string_agg(row_to_json(x)::text, ',' order by x.account_id, x.operative_id, x.definition_id) from item_stacks x), '') as t
          union all select 'items' || coalesce((select string_agg(row_to_json(x)::text, ',' order by x.item_id) from item_instances x), '')
          union all select 'operatives' || coalesce((select string_agg(x.operative_id || ':' || x.experience, ',' order by x.operative_id) from operatives x), '')
          union all select 'pool' || coalesce((select string_agg(row_to_json(x)::text, ',' order by x.attempt_id, x.container_index, x.line) from pool_collections x), '')
          union all select 'holds' || coalesce((select string_agg(x.attempt_id || x.account_id::text || x.state, ',' order by x.attempt_id, x.account_id) from energy_holds x), '')
          union all select 'ledger' || coalesce((select string_agg(x.entry_id::text || x.kind || x.delta, ',' order by x.entry_id) from energy_ledger x), '')
          union all select 'wallets' || coalesce((select string_agg(x.account_id || ':' || x.balance, ',' order by x.account_id) from energy_wallets x), '')
          union all select 'inventories' || coalesce((select string_agg(x.account_id || ':' || x.version, ',' order by x.account_id) from inventories x), '')
          union all select 'submissions' || coalesce((select string_agg(x.submission_id || x.disposition || x.receive_count, ',' order by x.submission_id) from result_submissions x), '')
          union all select 'settlements' || coalesce((select string_agg(x.attempt_id || x.account_id::text || x.outcome, ',' order by x.attempt_id, x.account_id) from participant_settlements x), '')
          union all select 'participants' || coalesce((select string_agg(x.attempt_id || x.account_id::text || x.state || coalesce(x.quarantine_reason, ''), ',' order by x.attempt_id, x.account_id) from attempt_participants x), '')
          union all select 'attempts' || coalesce((select string_agg(x.attempt_id || x.state, ',' order by x.attempt_id) from mission_attempts x), '')
          union all select 'anomalies' || coalesce((select string_agg(x.id || x.kind, ',' order by x.id) from anomaly_events x), '')) d
        """);
}
