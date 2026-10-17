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
            insert into mission_attempts (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline)
            values (@attempt, @accountId, @Template, @TemplateVersion, 'sha256:' || repeat('ab', 32), clock_timestamp() + interval '60 seconds');
            insert into attempt_secrets (attempt_id, seed, retention_salt) values (@attempt, 12345, @salt)
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
            "update attempt_participants set state = 'released', release_reason = 'player_cancel' where attempt_id = @attempt and state = 'admitted'", new { attempt }, tx));
        Assert.Equal(1, await conn.ExecuteAsync(
            "update energy_holds set state = 'released' where attempt_id = @attempt and state = 'reserved'", new { attempt }, tx));
        await tx.CommitAsync();
    }

    public const string FixtureHost = "fixture-host";
    public const string FixtureBuild = "fixture-build";

    /// <summary>
    /// The SET list that moves an attempt to <paramref name="state"/> with the columns 0008 requires of it: the claim's from
    /// assigned on (the fixture host, a port and claim key per attempt, a credential hash derived from the attempt id), ready's
    /// from ready on, the start from running on. Written-once columns already set are kept; cancelled touches none of them.
    /// </summary>
    public static string StateSet(string state, string hostId = FixtureHost)
    {
        var set = $"state = '{state}', cancel_reason = {(state == "cancelled" ? "coalesce(cancel_reason, 'no_capacity')" : "null")}, " +
                  $"ended_at = {(state is "cancelled" or "settled" ? "coalesce(ended_at, clock_timestamp())" : "null")}";
        var level = state switch { "assigned" or "launching" => 1, "ready" => 2, "running" or "ending" or "unresponsive" or "settled" => 3, _ => 0 };
        if (level >= 1)
            set += $", host_id = '{hostId}', build_revision = '{FixtureBuild}', credential_generation = coalesce(credential_generation, 1), " +
                   $"mission_port = coalesce(mission_port, (select port_base from mission_hosts where host_id = '{hostId}') + (select count(*) from mission_attempts m where m.host_id = '{hostId}')::int), " +
                   "process_credential_hash = coalesce(process_credential_hash, sha256(convert_to(attempt_id::text, 'UTF8'))), " +
                   "claim_key = coalesce(claim_key, attempt_id), launch_deadline = coalesce(launch_deadline, clock_timestamp() + interval '120 seconds')";
        if (level >= 2)
            set += ", process_session_id = coalesce(process_session_id, gen_random_uuid()), ready_at = coalesce(ready_at, clock_timestamp()), " +
                   "join_deadline = coalesce(join_deadline, clock_timestamp() + interval '120 seconds'), layout_hash = coalesce(layout_hash, '0123456789abcdef'), " +
                   "loot_table_version = coalesce(loot_table_version, 1), loot_hash = coalesce(loot_hash, 'sha256:' || encode(sha256(convert_to('blackglass-loot-pool/1' || E'\n' || 'table=1' || E'\n', 'UTF8')), 'hex'))";
        if (level >= 3) set += ", started_at = coalesce(started_at, clock_timestamp())";
        return set;
    }

    /// <summary>
    /// Moves an attempt along an allowed edge to <paramref name="state"/> as the superuser, through every trigger, with the
    /// columns <see cref="StateSet"/> writes (a move to cancelled also releases the admitted participants and their zero-amount
    /// holds, as a pre-start release does). Registers the fixture host (agent, ports from 7000, capacity 64) and allowlists
    /// the fixture build for the attempt's catalogue and template first; a move to ready also registers one required objective
    /// (the empty pool matches StateSet's loot hash).
    /// </summary>
    /// <param name="hostId">The host the claim binds (at the first move to assigned); it must exist unless it is the fixture host.</param>
    public static async Task MoveAsync(TestDatabase db, Guid attempt, string state, string hostId = FixtureHost)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await conn.ExecuteAsync(
            $"""
            insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled, port_base)
              values ('{FixtureHost}', 'agent', sha256(convert_to('{FixtureHost}', 'UTF8')), 64, true, 7000) on conflict do nothing;
            insert into allowed_builds (build_revision, catalogue_version)
              select '{FixtureBuild}', catalogue_version from mission_attempts where attempt_id = @attempt on conflict do nothing;
            insert into allowed_build_templates (build_revision, catalogue_version, template_id, template_version)
              select '{FixtureBuild}', catalogue_version, template_id, template_version from mission_attempts where attempt_id = @attempt on conflict do nothing;
            """, new { attempt });
        Assert.Equal(1, await conn.ExecuteAsync($"update mission_attempts set {StateSet(state, hostId)} where attempt_id = @attempt", new { attempt }));
        if (state == "ready")
            await conn.ExecuteAsync("insert into attempt_objectives (attempt_id, objective_id, required, ordinal) values (@attempt, 'extract', true, 0) on conflict do nothing", new { attempt });
        if (state == "cancelled") // as a pre-start release does: every admitted participant and its (zero-amount) hold released
            await conn.ExecuteAsync("""
                update attempt_participants p set state = 'released', release_reason = a.cancel_reason
                from mission_attempts a where a.attempt_id = p.attempt_id and p.attempt_id = @attempt and p.state = 'admitted';
                update energy_holds set state = 'released' where attempt_id = @attempt and state = 'reserved' and amount = 0;
                """, new { attempt });
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
