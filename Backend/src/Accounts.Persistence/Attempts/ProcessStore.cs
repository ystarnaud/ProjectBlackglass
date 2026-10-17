using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;

namespace Blackglass.Accounts.Persistence.Attempts;

public sealed record AuthenticatedProcess(Guid AttemptId);

/// <summary>One participant as the registered process receives it: the account and its admission snapshot (the stored JSON).</summary>
public sealed record RegisteredParticipant(long AccountId, string LoadoutJson);

public abstract record RegisterResult
{
    /// <summary>The session and the attempt's configuration: the only response that carries the seed; never the salt.</summary>
    public sealed record Registered(Guid ProcessSessionId, int Seed, string TemplateId, int TemplateVersion, string CatalogueVersion,
        IReadOnlyList<RegisteredParticipant> Participants, DateTime LaunchDeadline) : RegisterResult;
    /// <summary>Not exactly the claimed build, or that build is no longer enabled or allowlisted for the template: nothing written.</summary>
    public sealed record BuildNotAllowed : RegisterResult;
    public sealed record InvalidState : RegisterResult;
    /// <summary>The presenting credential is no longer the attempt's (a recovery replaced it after authentication): the uniform 401.</summary>
    public sealed record Unauthenticated : RegisterResult;
}

public abstract record ReadyResult
{
    public sealed record Ready(DateTime JoinDeadline) : ReadyResult;
    /// <summary>The pool is outside the catalogue's table or its hash is wrong: the attempt was cancelled (invalid_pool).</summary>
    public sealed record InvalidLootPool(IReadOnlyList<string> Errors) : ReadyResult;
    /// <summary>The objectives break the rules: nothing written.</summary>
    public sealed record InvalidObjectives(IReadOnlyList<string> Errors) : ReadyResult;
    public sealed record WrongSession : ReadyResult;
    /// <summary>The attempt was admitted under another catalogue than the one judging its pool: nothing written, nothing cancelled.</summary>
    public sealed record CatalogueMismatch : ReadyResult;
    /// <summary>The attempt's process credential was revoked (its host was disabled, R8): the uniform 401, nothing written.</summary>
    public sealed record Revoked : ReadyResult;
    /// <summary>Not launching, past its launch deadline, or already ready with a different report: nothing written.</summary>
    public sealed record InvalidState : ReadyResult;
}

public abstract record ProcessHeartbeatResult
{
    public sealed record Ok : ProcessHeartbeatResult;
    public sealed record WrongSession : ProcessHeartbeatResult;
    /// <summary>A listed account is not a participant of the attempt: nothing written.</summary>
    public sealed record UnknownParticipant : ProcessHeartbeatResult;
    /// <summary>The attempt's process credential was revoked (R8): the uniform 401, nothing written.</summary>
    public sealed record Revoked : ProcessHeartbeatResult;
}

/// <summary>
/// The mission process side of an attempt (spec §16.3; plan revision 2 R1, R4, R6). A process authenticates with its attempt's
/// <c>pc1_</c> credential: one indexed lookup of the SHA-256 per request (no cache), valid only while the attempt is neither
/// cancelled nor settled. The route's attempt must be the credential's (the authentication handler compares them). There is
/// no exception after cancellation: stop instructions reach the process through its host agent only. No host row is ever
/// locked here.
/// </summary>
public sealed class ProcessStore(NpgsqlDataSource db)
{
    public async Task<AuthenticatedProcess?> AuthenticateAsync(byte[] credentialHash, CancellationToken ct)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        var attempt = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select attempt_id from mission_attempts where process_credential_hash = @credentialHash and state not in ('cancelled', 'settled') and credential_revoked_at is null",
            new { credentialHash }, cancellationToken: ct));
        return attempt is { } id ? new AuthenticatedProcess(id) : null;
    }

    /// <summary>
    /// The process session (spec §7.4; B4; R1): the attempt FOR UPDATE, then the clock. Accepted while assigned (moving it to
    /// launching: the agent's launching report was lost, and the credential is now frozen) or launching. The body's build must
    /// be exactly the claimed one, still enabled and allowlisted for the attempt's template version (BuildNotAllowed otherwise,
    /// nothing written). The first call writes a new UUIDv7 session; a repeat while launching returns the same session and
    /// configuration. The seed is read from attempt_secrets for this response only; the salt is never read here.
    /// </summary>
    /// <param name="credentialHash">The SHA-256 of the credential this request presented: it must still be the attempt's under the
    /// attempt lock (a recovery that committed after authentication makes it Unauthenticated, nothing written; R1).</param>
    public async Task<RegisterResult> RegisterAsync(Guid attemptId, byte[] credentialHash, RegisterRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentialHash);
        ArgumentNullException.ThrowIfNull(request);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var attempt = await conn.QuerySingleOrDefaultAsync<(string State, string? BuildRevision, string CatalogueVersion, string TemplateId, int TemplateVersion,
            Guid? Session, DateTime? LaunchDeadline)?>(new CommandDefinition(
            """
            select state, build_revision, catalogue_version, template_id, template_version, process_session_id, launch_deadline
            from mission_attempts where attempt_id = @attemptId and process_credential_hash = @credentialHash and credential_revoked_at is null for update
            """, new { attemptId, credentialHash }, tx, cancellationToken: ct));
        if (attempt is not { } a) return new RegisterResult.Unauthenticated();
        if (a.State is not ("assigned" or "launching")) return new RegisterResult.InvalidState();
        var allowed = request.Build.Revision == a.BuildRevision && request.Build.CatalogueVersion == a.CatalogueVersion
            && await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                select exists (select 1 from allowed_builds b join allowed_build_templates t using (build_revision, catalogue_version)
                               where b.enabled and b.build_revision = @Revision and b.catalogue_version = @CatalogueVersion
                                 and t.template_id = @TemplateId and t.template_version = @TemplateVersion)
                """, new { request.Build.Revision, request.Build.CatalogueVersion, a.TemplateId, a.TemplateVersion }, tx, cancellationToken: ct));
        if (!allowed) return new RegisterResult.BuildNotAllowed();

        var session = a.Session ?? Guid.CreateVersion7();
        if (a.Session is null)
            await conn.ExecuteAsync(new CommandDefinition(
                "update mission_attempts set state = 'launching', process_session_id = @session where attempt_id = @attemptId and state in ('assigned', 'launching')",
                new { attemptId, session }, tx, cancellationToken: ct));
        var seed = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "select seed from attempt_secrets where attempt_id = @attemptId", new { attemptId }, tx, cancellationToken: ct));
        var participants = (await conn.QueryAsync<(long AccountId, string Loadout)>(new CommandDefinition(
            "select account_id, loadout_snapshot::text from attempt_participants where attempt_id = @attemptId order by account_id",
            new { attemptId }, tx, cancellationToken: ct))).Select(p => new RegisteredParticipant(p.AccountId, p.Loadout)).ToList();
        await tx.CommitAsync(ct);
        return new RegisterResult.Registered(session, seed, a.TemplateId, a.TemplateVersion, a.CatalogueVersion, participants, Utc(a.LaunchDeadline!.Value));
    }

    /// <summary>
    /// The ready report (spec §7.4, §8.4; R3). Validated purely first: objectives (<see cref="ObjectiveList"/>; InvalidObjectives,
    /// nothing written), then the pool against <paramref name="table"/> (<see cref="LootPoolValidator"/>) and its canonical
    /// <see cref="LootHash"/>. The session is checked before anything is written (WrongSession never cancels). An invalid pool
    /// cancels the launching attempt with <c>invalid_pool</c>, releasing every participant (the release lock order). A valid
    /// report: the attempt FOR UPDATE, launching → ready with ready_at, the join deadline (database clock), the layout hash, the
    /// table version and the loot hash, and the objectives and pool inserted (all written once). A repeat while ready with the
    /// identical report answers the stored join deadline; any difference is InvalidState.
    /// </summary>
    /// <param name="tableCatalogueVersion">The version of the catalogue <paramref name="table"/> comes from: an attempt admitted under
    /// another is CatalogueMismatch (its pool is never judged against the wrong table, and nothing is cancelled).</param>
    public async Task<ReadyResult> ReadyAsync(Guid attemptId, ReadyReport report, Blackglass.Accounts.Domain.Catalogue.LootTable table, string tableCatalogueVersion,
        int joinDeadlineSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(table);
        var objectiveErrors = ObjectiveList.Validate(report.Objectives);
        if (objectiveErrors.Count > 0) return new ReadyResult.InvalidObjectives(objectiveErrors);
        var poolErrors = LootPoolValidator.Validate(table, report.LootTableVersion, report.LootPool).ToList();
        if (LootHash.Compute(report.LootTableVersion, report.LootPool) != report.LootHash) poolErrors.Add("lootHash is not the pool's canonical hash");

        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // The session and the catalogue are written once: an unlocked read decides WrongSession and CatalogueMismatch before any
        // lock or write.
        var identity = await conn.QuerySingleOrDefaultAsync<(Guid? Session, string Catalogue, bool Revoked)?>(new CommandDefinition(
            "select process_session_id, catalogue_version, credential_revoked_at is not null from mission_attempts where attempt_id = @attemptId",
            new { attemptId }, tx, cancellationToken: ct));
        if (identity is { Revoked: true }) return new ReadyResult.Revoked();
        if (identity is not { } id || id.Session != report.ProcessSessionId) return new ReadyResult.WrongSession();
        if (id.Catalogue != tableCatalogueVersion) return new ReadyResult.CatalogueMismatch();
        if (poolErrors.Count > 0)
        {
            var batch = await AttemptStore.ReleaseParticipantsLockedAsync(conn, tx, attemptId, null, CancelReasons.InvalidPool, ["launching"], ct)
                ?? throw new InvalidOperationException("An attempt has no participants.");
            if (batch.AttemptState != "cancelled") return new ReadyResult.InvalidState(); // not launching: nothing written
            if (await RevokedLockedAsync(conn, tx, attemptId, ct)) return new ReadyResult.Revoked(); // a disable committed first: rolled back
            await tx.CommitAsync(ct);
            return new ReadyResult.InvalidLootPool(poolErrors);
        }

        var stored = await conn.QuerySingleAsync<(string State, string? LayoutHash, int? TableVersion, string? LootHash, DateTime? JoinDeadline, bool LaunchExpired, bool Revoked)>(new CommandDefinition(
            """
            select state, layout_hash, loot_table_version, loot_hash, join_deadline, coalesce(launch_deadline <= clock_timestamp(), false), credential_revoked_at is not null
            from mission_attempts where attempt_id = @attemptId for update
            """,
            new { attemptId }, tx, cancellationToken: ct));
        if (stored.Revoked) return new ReadyResult.Revoked();
        if (stored.State == "ready")
        {
            var objectives = (await conn.QueryAsync<(string Id, bool Required)>(new CommandDefinition(
                "select objective_id, required from attempt_objectives where attempt_id = @attemptId order by ordinal", new { attemptId }, tx, cancellationToken: ct)))
                .Select(o => new Objective(o.Id, o.Required)).ToList();
            var same = stored.LayoutHash == report.LayoutHash && stored.TableVersion == report.LootTableVersion && stored.LootHash == report.LootHash
                && objectives.SequenceEqual(report.Objectives);
            return same ? new ReadyResult.Ready(Utc(stored.JoinDeadline!.Value)) : new ReadyResult.InvalidState();
        }
        // The launch deadline is authoritative (database clock, after the lock): past it, ready is refused and the worker cancels.
        if (stored.State != "launching" || stored.LaunchExpired) return new ReadyResult.InvalidState();

        var joinDeadline = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            """
            update mission_attempts set state = 'ready', ready_at = clock_timestamp(), join_deadline = clock_timestamp() + make_interval(secs => @joinDeadlineSeconds),
                   layout_hash = @LayoutHash, loot_table_version = @LootTableVersion, loot_hash = @LootHash
            where attempt_id = @attemptId and state = 'launching' returning join_deadline
            """, new { attemptId, joinDeadlineSeconds = (double)joinDeadlineSeconds, report.LayoutHash, report.LootTableVersion, report.LootHash }, tx, cancellationToken: ct));
        for (var i = 0; i < report.Objectives.Count; i++)
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into attempt_objectives (attempt_id, objective_id, required, ordinal) values (@attemptId, @ObjectiveId, @Required, @i)",
                new { attemptId, report.Objectives[i].ObjectiveId, report.Objectives[i].Required, i }, tx, cancellationToken: ct));
        foreach (var line in report.LootPool)
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into attempt_loot_pool (attempt_id, container_index, line, definition_id, quantity) values (@attemptId, @ContainerIndex, @Line, @DefinitionId, @Quantity)",
                new { attemptId, line.ContainerIndex, line.Line, line.DefinitionId, line.Quantity }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new ReadyResult.Ready(Utc(joinDeadline));
    }

    /// <summary>
    /// The process heartbeat (spec §11.2; B10; R4): the listed accounts must all be participants (UnknownParticipant otherwise,
    /// nothing written; duplicates are refused by the parser). Then the attempt FOR UPDATE (its session must match), its
    /// participants FOR UPDATE in account order, the clock: last_process_heartbeat_at stamped, unresponsive → running, and each
    /// listed started participant joined → left when not connected, left → joined when connected. An admitted participant is
    /// never made joined here (only the start commit does that).
    /// </summary>
    public async Task<ProcessHeartbeatResult> HeartbeatAsync(Guid attemptId, ProcessHeartbeatRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var row = await conn.QuerySingleOrDefaultAsync<(Guid? Session, bool Revoked)?>(new CommandDefinition(
            "select process_session_id, credential_revoked_at is not null from mission_attempts where attempt_id = @attemptId and state not in ('cancelled', 'settled') for update",
            new { attemptId }, tx, cancellationToken: ct));
        if (row is { Revoked: true }) return new ProcessHeartbeatResult.Revoked();
        if (row?.Session is not { } session || session != request.ProcessSessionId) return new ProcessHeartbeatResult.WrongSession(); // an ended attempt has no live session
        var participants = (await conn.QueryAsync<(long AccountId, string State)>(new CommandDefinition(
            "select account_id, state from attempt_participants where attempt_id = @attemptId order by account_id for update",
            new { attemptId }, tx, cancellationToken: ct))).ToDictionary(p => p.AccountId, p => p.State);
        if (request.Participants.Any(p => !participants.ContainsKey(p.AccountId))) return new ProcessHeartbeatResult.UnknownParticipant();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            update mission_attempts set last_process_heartbeat_at = clock_timestamp(),
                   state = case when state = 'unresponsive' then 'running' else state end
            where attempt_id = @attemptId
            """, new { attemptId }, tx, cancellationToken: ct));
        foreach (var p in request.Participants.OrderBy(p => p.AccountId))
        {
            var (from, to) = participants[p.AccountId] switch
            {
                "joined" when !p.Connected => ("joined", "left"),
                "left" when p.Connected => ("left", "joined"),
                _ => (null, null),
            };
            if (from is not null)
                await conn.ExecuteAsync(new CommandDefinition(
                    "update attempt_participants set state = @to where attempt_id = @attemptId and account_id = @AccountId and state = @from",
                    new { attemptId, p.AccountId, from, to }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return new ProcessHeartbeatResult.Ok();
    }

    static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");

    /// <summary>Whether the attempt's process credential was revoked, read while the caller holds the attempt row lock.</summary>
    static Task<bool> RevokedLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid attemptId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select credential_revoked_at is not null from mission_attempts where attempt_id = @attemptId", new { attemptId }, tx, cancellationToken: ct));

    /// <summary>
    /// The process's failure report (B11 revised, R6), in the release lock order: before any start, every admitted participant
    /// is released with <c>process_failed</c> (the attempt is cancelled; its slot stays until the agent confirms the unit gone);
    /// after a start nothing is cancelled, released or refunded. Either way the report (session and reason) is stored once as
    /// evidence. The body's session must be the attempt's (WrongSession otherwise, nothing written). An identical repeat answers
    /// the stored outcome; a different reason is Conflicting. (Over HTTP a repeat after the cancellation meets the uniform 401:
    /// the credential ended with its attempt, R4.)
    /// </summary>
    public async Task<ExitReportResult> FailedAsync(Guid attemptId, FailedRequest report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var session = await conn.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "select process_session_id from mission_attempts where attempt_id = @attemptId", new { attemptId }, tx, cancellationToken: ct));
        if (session != report.ProcessSessionId) return new ExitReportResult.WrongSession();

        async Task<ExitReportResult?> StoredAsync()
        {
            var stored = await conn.QuerySingleOrDefaultAsync<(string Reason, string Outcome)?>(new CommandDefinition(
                "select reason, outcome from attempt_exit_reports where attempt_id = @attemptId and source = 'process'", new { attemptId }, tx, cancellationToken: ct));
            return stored is { } s ? s.Reason == report.Reason ? new ExitReportResult.Applied(s.Outcome) : new ExitReportResult.Conflicting() : null;
        }
        if (await StoredAsync() is { } early) return early;
        var batch = await AttemptStore.ReleaseParticipantsLockedAsync(conn, tx, attemptId, null, CancelReasons.ProcessFailed, AttemptStore.PreStartStates, ct)
            ?? throw new InvalidOperationException("An attempt has no participants.");
        if (await StoredAsync() is { } raced) return raced;
        // An attempt that ended, or whose credential was revoked (R8), before this report: the uniform 401, nothing stored
        // (R4; Final M1). Checked under the attempt lock the release took; anything it wrote rolls back.
        if (batch.StateBefore is "cancelled" or "settled" || await RevokedLockedAsync(conn, tx, attemptId, ct)) return new ExitReportResult.Ended();
        var outcome = batch.ReleasedAny ? "cancelled" : "none";
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into attempt_exit_reports (attempt_id, source, process_session_id, attempt_state, any_started, reason, outcome)
            values (@attemptId, 'process', @ProcessSessionId, @StateBefore, @AnyStarted, @Reason, @outcome)
            """, new { attemptId, report.ProcessSessionId, batch.StateBefore, batch.AnyStarted, report.Reason, outcome }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new ExitReportResult.Applied(outcome);
    }
}
