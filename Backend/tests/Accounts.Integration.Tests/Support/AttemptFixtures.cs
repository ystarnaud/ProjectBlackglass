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
}
