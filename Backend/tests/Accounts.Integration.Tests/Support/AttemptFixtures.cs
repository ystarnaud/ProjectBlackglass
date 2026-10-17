using Dapper;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>
/// Real mission participations for tests, written as the cluster superuser through 0007's triggers and foreign keys (no
/// trigger is bypassed): an attempt of the seeded template <c>procedural-standard</c> v1, its one participant and a hold,
/// each in its initial state. The hold's amount is 0 (the uniform zero-cost model), so no wallet or ledger entry is needed
/// and the 0007 invariants hold. Release follows the allowed edges, as a pre-start cancel does.
/// </summary>
public static class AttemptFixtures
{
    public const string Template = "procedural-standard";
    public const int TemplateVersion = 1;

    /// <summary>An active (admitted) solo participation of <paramref name="accountId"/>, with its inventory version at that
    /// moment in the participant row. Returns the attempt id (UUIDv7).</summary>
    public static async Task<Guid> AdmitAsync(TestDatabase db, long accountId)
    {
        var attempt = Guid.CreateVersion7();
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(
            """
            insert into mission_attempts (attempt_id, created_by, template_id, template_version, seed, retention_salt, assign_deadline)
            values (@attempt, @accountId, @Template, @TemplateVersion, 12345, @salt, clock_timestamp() + interval '60 seconds')
            """, new { attempt, accountId, Template, TemplateVersion, salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32) }, tx);
        await conn.ExecuteAsync(
            """
            insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
            values (@attempt, @accountId, @Template, @TemplateVersion, '{"operatives":[]}'::jsonb,
                    coalesce((select version from inventories where account_id = @accountId), 1), 0)
            """, new { attempt, accountId, Template, TemplateVersion }, tx);
        await conn.ExecuteAsync("insert into energy_holds (attempt_id, account_id, amount) values (@attempt, @accountId, 0)", new { attempt, accountId }, tx);
        await tx.CommitAsync();
        return attempt;
    }

    /// <summary>
    /// Ends an admitted attempt as a pre-start player cancel does, in the release lock order (attempts, participants, holds):
    /// attempt <c>admitted → cancelled</c>, every participant <c>admitted → released</c>, every hold
    /// <c>reserved → released</c>. Each update is guarded by its source state and must touch its row.
    /// </summary>
    public static async Task ReleaseAsync(TestDatabase db, Guid attempt)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        Assert.Equal(1, await conn.ExecuteAsync(
            "update mission_attempts set state = 'cancelled', cancel_reason = 'player_cancel', ended_at = clock_timestamp() where attempt_id = @attempt and state = 'admitted'",
            new { attempt }, tx));
        Assert.Equal(1, await conn.ExecuteAsync(
            "update attempt_participants set state = 'released' where attempt_id = @attempt and state = 'admitted'", new { attempt }, tx));
        Assert.Equal(1, await conn.ExecuteAsync(
            "update energy_holds set state = 'released' where attempt_id = @attempt and state = 'reserved'", new { attempt }, tx));
        await tx.CommitAsync();
    }

    /// <summary>
    /// Sets an attempt's <c>assign_deadline</c> to exactly <paramref name="deadline"/>, as the superuser under
    /// <c>session_replication_role = replica</c>: the column is immutable through the transition trigger, so a test backdates
    /// the row instead of mocking time (spec §18.1). Nothing else about the attempt changes.
    /// </summary>
    public static async Task SetDeadlineAsync(TestDatabase db, Guid attempt, DateTime deadline)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
        Assert.Equal(1, await conn.ExecuteAsync("update mission_attempts set assign_deadline = @deadline where attempt_id = @attempt", new { attempt, deadline }, tx));
        await tx.CommitAsync();
    }

    /// <summary>Backdates an attempt's assignment deadline to <paramref name="ago"/> before the database clock, so it is due.</summary>
    public static async Task ExpireDeadlineAsync(TestDatabase db, Guid attempt, TimeSpan? ago = null)
    {
        var now = await AccountTestKit.ScalarAsSuperuserAsync<DateTime>(db, "select clock_timestamp()");
        await SetDeadlineAsync(db, attempt, now - (ago ?? TimeSpan.FromSeconds(1)));
    }

    /// <summary>Returns once at least <paramref name="count"/> backends of this database wait on a lock (row or advisory; polls
    /// every 20 ms, for at most 30 s).</summary>
    public static async Task WaitForLockWaitersAsync(TestDatabase db, int count)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await su.ExecuteScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Fewer than {count} backends waited on a lock.");
            await Task.Delay(20);
        }
    }
}
