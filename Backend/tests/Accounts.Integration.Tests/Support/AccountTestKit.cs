using Dapper;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Support;

public static class AccountTestKit
{
    public static OperatorContext Op => OperatorContext.Create("test-operator", "integration test");

    /// <summary>A random admin key for one test class: no test credential is a constant in source or survives the run.</summary>
    public static string NewAdminKey() => "test-admin-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

    public static async Task<TestDatabase> MigratedAsync()
    {
        var db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();
        return db;
    }

    /// <summary>An account as Stage 3 created it: inserted as the app role while the schema is at version 2 (the database must
    /// have been migrated to exactly 2), then migrated to the latest version, so it has no inventories row (unprovisioned).</summary>
    public static async Task<long> PreStage4AccountAsync(TestDatabase db, string status = "active")
    {
        long account;
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values (@status) returning account_id", new { status });
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from inventories where account_id = @account", new { account }));
        return account;
    }

    /// <summary>Backdates or corrupts rows as the cluster superuser: time is never mocked (spec §18.1).</summary>
    public static async Task ExecuteAsSuperuserAsync(TestDatabase db, string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await conn.ExecuteAsync(sql, args);
    }

    public static async Task<T> ScalarAsSuperuserAsync<T>(TestDatabase db, string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    /// <summary>One row as the superuser, on a connection disposed afterwards (never an inline open, which leaks the pool's five).</summary>
    public static async Task<T> RowAsSuperuserAsync<T>(TestDatabase db, string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<T>(sql, args);
    }

    /// <summary>Rows as the superuser, on a connection disposed afterwards.</summary>
    public static async Task<List<T>> RowsAsSuperuserAsync<T>(TestDatabase db, string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<T>(sql, args)).ToList();
    }

    /// <summary>A live session row for a credential, written directly (for store tests that precede SessionStore).</summary>
    public static async Task<Guid> InsertSessionAsync(TestDatabase db, long accountId, Guid credentialId)
    {
        var sessionId = Guid.CreateVersion7();
        await ExecuteAsSuperuserAsync(db,
            "insert into auth_sessions (session_id, account_id, credential_id, token_hash, expires_at) values (@sessionId, @accountId, @credentialId, @hash, now() + interval '1 hour')",
            new { sessionId, accountId, credentialId, hash = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32) });
        return sessionId;
    }

    /// <summary>Returns once some backend of this database waits on a row lock (polls pg_stat_activity; never sleeps).</summary>
    public static async Task WaitUntilABackendWaitsOnALockAsync(TestDatabase db)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await su.ExecuteScalarAsync<long>(
                   "select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") == 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Nothing waited on the lock: the call under test takes no lock.");
            await Task.Yield();
        }
    }

    public static Task<IReadOnlyList<string>> InvariantFailuresAsync(TestDatabase db) =>
        InvariantRunner.RunAsync(db.AsSuperuser(), MigrationCatalog.Embedded.LatestVersion, InvariantRunner.RealDirectory);
}
