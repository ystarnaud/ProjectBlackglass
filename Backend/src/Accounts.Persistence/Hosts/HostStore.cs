using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Persistence.Attempts;

namespace Blackglass.Accounts.Persistence.Hosts;

public sealed record AuthenticatedHost(string HostId);

/// <summary>A heartbeat's answer: whether the host is drained, and the ended attempts whose units it still reports.</summary>
public sealed record HeartbeatResult(bool Drained, IReadOnlyList<Guid> Stop);

/// <summary>A claim's outcome. <see cref="Claimed"/> carries the plaintext credential, returned once and never stored.</summary>
public abstract record ClaimResult
{
    /// <param name="Recovered">True for a retry of the claim's key while the attempt was still assigned (R1): the same attempt
    /// with a newly issued credential and the next generation, not a replay of the first response.</param>
    public sealed record Claimed(Guid AttemptId, string HostId, int MissionPort, OpaqueSecret Credential, int CredentialGeneration, DateTime LaunchDeadline, bool Recovered) : ClaimResult;
    /// <summary>No compatible attempt, no free slot or no free port, now.</summary>
    public sealed record NoCandidate : ClaimResult;
    /// <summary>Drained, lease lapsed, or no enabled allowlisted build reported: the agent heartbeats first.</summary>
    public sealed record HostUnavailable : ClaimResult;
    /// <summary>A retry of a key whose attempt is launching or later, or ended: nothing rotated.</summary>
    public sealed record InvalidState : ClaimResult;
    /// <summary>The host was disabled since its request authenticated.</summary>
    public sealed record Unauthenticated : ClaimResult;
}

public abstract record LaunchingResult
{
    public sealed record Accepted(string State) : LaunchingResult;
    public sealed record NotFound : LaunchingResult;
    public sealed record InvalidState : LaunchingResult;
    public sealed record CredentialSuperseded : LaunchingResult;
    /// <summary>The presented host secret is no longer current (rotated, or the host disabled): the uniform 401, nothing written.</summary>
    public sealed record Unauthenticated : LaunchingResult;
}

/// <summary>
/// The agent side of a mission host (spec §7.4, §16.2; owner rulings B3-B6; plan revision 2 R1, R2, R7). A host authenticates
/// with its <c>hs1_</c> secret: one indexed lookup of the SHA-256 per request, no cache, so a rotation or a disable applies to
/// the next request. Lock order (Global Constraints): the host's own <c>mission_hosts</c> row FOR UPDATE, then attempts (a
/// claim's candidate FOR UPDATE SKIP LOCKED; a recovery's own attempt FOR UPDATE), then the clock. Launching locks only its
/// attempt. Nothing here locks an attempt before a host, and nothing touches an account, inventory, wallet or the energy
/// policy. No retry.
/// </summary>
public sealed class HostStore
{
    /// <summary>The ended states whose units an agent must stop and whose slots a confirmation frees.</summary>
    static readonly string[] Ended = ["cancelled", "settled"];

    readonly NpgsqlDataSource db;
    readonly int leaseSeconds;

    /// <param name="leaseSeconds">How recent a host's heartbeat must be for it to claim (the health check's lease).</param>
    public HostStore(NpgsqlDataSource db, int leaseSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseSeconds, 1);
        (this.db, this.leaseSeconds) = (db ?? throw new ArgumentNullException(nameof(db)), leaseSeconds);
    }

    sealed record HostRow(bool Enabled, bool Drained, bool Live, int Capacity, int? PortBase, string? BuildRevision, string? CatalogueVersion, bool BuildAllowed);

    /// <summary>
    /// One claim try, one READ COMMITTED transaction (spec §7.4):
    /// the host row FOR UPDATE, which serializes this host's claims so the capacity and port checks can't race; a retry of the
    /// key (this host's attempt with that claim key) is a recovery while still assigned (a new credential, the next generation,
    /// in a guarded update) and InvalidState otherwise; else, with a free slot, the oldest admitted attempt before its assignment
    /// deadline whose template version the host's reported, enabled build lists under the same catalogue version, FOR UPDATE
    /// SKIP LOCKED (never waiting on an attempt another claimer holds); the lowest free port; a new <c>pc1_</c> credential; and
    /// one guarded update to assigned with the host, port, build, credential hash, generation 1, key and launch deadline
    /// (database clock). The plaintext credential is returned after commit, once.
    /// </summary>
    /// <param name="secretHash">The SHA-256 of the secret the request presented: it must still be the host's current, unrevoked
    /// secret under the host lock (a long poll that outlives a rotation or a disable claims nothing more).</param>
    public async Task<ClaimResult> ClaimAsync(string hostId, byte[] secretHash, Guid claimKey, int launchDeadlineSeconds, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(launchDeadlineSeconds, 1);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var host = await conn.QuerySingleOrDefaultAsync<HostRow>(new CommandDefinition(
            """
            select h.enabled as Enabled, h.drained as Drained,
                   coalesce(h.last_heartbeat_at > clock_timestamp() - make_interval(secs => @lease), false) as Live,
                   h.capacity as Capacity, h.port_base as PortBase, h.build_revision as BuildRevision, h.catalogue_version as CatalogueVersion,
                   exists (select 1 from allowed_builds b where b.enabled and b.build_revision = h.build_revision and b.catalogue_version = h.catalogue_version) as BuildAllowed
            from mission_hosts h where h.host_id = @hostId and h.kind = 'agent' and h.secret_hash = @secretHash and not h.secret_revoked
            for update of h
            """, new { hostId, secretHash, lease = (double)leaseSeconds }, tx, cancellationToken: ct));
        if (host is not { Enabled: true }) return new ClaimResult.Unauthenticated();

        var retry = await conn.QuerySingleOrDefaultAsync<(Guid AttemptId, string State, int Port, int Generation, DateTime LaunchDeadline, bool Revoked)?>(new CommandDefinition(
            """
            select attempt_id, state, mission_port, credential_generation, launch_deadline, credential_revoked_at is not null from mission_attempts
            where host_id = @hostId and claim_key = @claimKey for update
            """, new { hostId, claimKey }, tx, cancellationToken: ct));
        if (retry is { } prior)
        {
            if (prior.State != "assigned" || prior.Revoked) return new ClaimResult.InvalidState(); // R8: a revoked credential is never re-issued
            var rotated = OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix);
            var generation = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                """
                update mission_attempts set process_credential_hash = @hash, credential_generation = credential_generation + 1
                where attempt_id = @AttemptId and state = 'assigned' returning credential_generation
                """, new { hash = rotated.Hash(), prior.AttemptId }, tx, cancellationToken: ct))
                ?? throw new InvalidOperationException("A locked assigned attempt was not updated.");
            await tx.CommitAsync(ct);
            return new ClaimResult.Claimed(prior.AttemptId, hostId, prior.Port, rotated, generation, Utc(prior.LaunchDeadline), Recovered: true);
        }

        if (host.Drained || !host.Live || !host.BuildAllowed || host.PortBase is not { } portBase) return new ClaimResult.HostUnavailable();
        var used = (await conn.QueryAsync<int>(new CommandDefinition(
            "select mission_port from mission_attempts where host_id = @hostId and slot_released_at is null", new { hostId }, tx, cancellationToken: ct))).ToHashSet();
        if (used.Count >= host.Capacity || Blackglass.Accounts.Domain.Hosts.PortAllocator.Lowest(portBase, host.Capacity, used) is not { } port) return new ClaimResult.NoCandidate();

        var candidate = await conn.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            select a.attempt_id from mission_attempts a
            where a.state = 'admitted' and a.assign_deadline > clock_timestamp() and a.catalogue_version = @CatalogueVersion
              and exists (select 1 from allowed_build_templates t
                          where t.build_revision = @BuildRevision and t.catalogue_version = @CatalogueVersion
                            and t.template_id = a.template_id and t.template_version = a.template_version)
            order by a.created_at, a.attempt_id limit 1
            for update of a skip locked
            """, new { host.BuildRevision, host.CatalogueVersion }, tx, cancellationToken: ct));
        if (candidate is not { } attemptId) return new ClaimResult.NoCandidate();

        var credential = OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix);
        var deadline = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            """
            update mission_attempts set state = 'assigned', host_id = @hostId, mission_port = @port, build_revision = @BuildRevision,
                   process_credential_hash = @hash, credential_generation = 1, claim_key = @claimKey,
                   launch_deadline = clock_timestamp() + make_interval(secs => @launchDeadlineSeconds)
            where attempt_id = @attemptId and state = 'admitted' returning launch_deadline
            """, new { attemptId, hostId, port, host.BuildRevision, hash = credential.Hash(), claimKey, launchDeadlineSeconds = (double)launchDeadlineSeconds },
            tx, cancellationToken: ct))
            ?? throw new InvalidOperationException("A locked admitted attempt was not assigned.");
        await tx.CommitAsync(ct);
        return new ClaimResult.Claimed(attemptId, hostId, port, credential, 1, Utc(deadline), Recovered: false);
    }

    /// <summary>
    /// The long poll: <see cref="ClaimAsync"/> until it claims, refuses, or <paramref name="wait"/> has passed, sleeping
    /// <paramref name="poll"/> between tries. Each try is its own short transaction on its own connection: nothing is held while
    /// waiting. Cancelling the request ends the wait (OperationCanceledException).
    /// </summary>
    public async Task<ClaimResult> ClaimWaitingAsync(string hostId, byte[] secretHash, Guid claimKey, int launchDeadlineSeconds, TimeSpan wait, TimeSpan poll, CancellationToken ct)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            var result = await ClaimAsync(hostId, secretHash, claimKey, launchDeadlineSeconds, ct);
            if (result is not ClaimResult.NoCandidate || DateTime.UtcNow + poll > until) return result;
            await Task.Delay(poll, ct);
        }
    }

    /// <summary>
    /// The agent's launching report (R1): the attempt FOR UPDATE, this host's only (another host's or an unknown one is
    /// NotFound). The reported generation must be the attempt's current one (CredentialSuperseded otherwise, nothing written):
    /// the agent launches only with a credential the backend has not replaced. Assigned moves to launching; launching or later
    /// answers its state (a lost acknowledgement); cancelled or settled is InvalidState.
    /// </summary>
    public async Task<LaunchingResult> LaunchingAsync(string hostId, byte[] secretHash, Guid attemptId, int credentialGeneration, CancellationToken ct)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string State, int Generation, bool Revoked)?>(new CommandDefinition(
            "select state, credential_generation, credential_revoked_at is not null from mission_attempts where attempt_id = @attemptId and host_id = @hostId for update",
            new { attemptId, hostId }, tx, cancellationToken: ct));
        if (row is not { } attempt) return new LaunchingResult.NotFound();
        if (!await SecretCurrentAsync(conn, tx, hostId, secretHash, ct)) return new LaunchingResult.Unauthenticated();
        if (Ended.Contains(attempt.State) || attempt.Revoked) return new LaunchingResult.InvalidState(); // R8: never launch a revoked credential
        if (attempt.Generation != credentialGeneration) return new LaunchingResult.CredentialSuperseded();
        if (attempt.State != "assigned") return new LaunchingResult.Accepted(attempt.State);
        await conn.ExecuteAsync(new CommandDefinition(
            "update mission_attempts set state = 'launching' where attempt_id = @attemptId and state = 'assigned'", new { attemptId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new LaunchingResult.Accepted("launching");
    }

    /// <summary>
    /// The agent's exit report for one of its attempts (B11 revised, R6, R7; Stage 9 S9-B1, S9-B5, S9-B7, revision 3). Release lock
    /// order (no host lock): every participant's accounts row FOR KEY SHARE, the attempt FOR UPDATE (this host's only, else
    /// NotFound), its participants, then, before any start, the release of every admitted participant with <c>process_exited</c>
    /// (which cancels the attempt). The presented secret is re-checked under the attempt lock. The report is stored once
    /// (insert-only evidence: exit code, signal, per-participant presence, the outbox scan's completeness, whether the attempt was
    /// already revoked, the state seen, and whether anyone had started) and the slot is released (the unit is gone, B6).
    /// <para>After a start (S9-B7), a participant listed absent is voided (positive evidence) only when the report has an exit
    /// status, declares a complete outbox scan, the attempt is not revoked, the participant started, is neither settled nor
    /// quarantined, and has no stored submission and no recorded hash; one listed present moves joined | left → result_pending.
    /// Anything less voids nothing and raises exit_evidence_inconclusive; a recorded hash or a submission against an absence
    /// raises exit_evidence_contradiction; a revoked attempt's report raises exit_evidence_post_revocation and changes no state.
    /// A report declaring a complete scan must list every started participant (IncompleteResults, nothing written).</para>
    /// <para>An identical repeat answers the stored outcome. After a stored report of an incomplete scan, a report with the same
    /// exit status and a complete scan is stored as the attempt's one outbox observation and evaluated the same way (the first
    /// report is never changed); any other different report is Conflicting and writes nothing.</para>
    /// </summary>
    public async Task<ExitReportResult> ExitedAsync(string hostId, byte[] secretHash, Guid attemptId, ExitedRequest report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var owner = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "select host_id from mission_attempts where attempt_id = @attemptId", new { attemptId }, tx, cancellationToken: ct));
        if (owner != hostId) return new ExitReportResult.NotFound();
        var participants = (await conn.QueryAsync<long>(new CommandDefinition(
            "select account_id from attempt_participants where attempt_id = @attemptId", new { attemptId }, tx, cancellationToken: ct))).ToHashSet();
        if (report.Results.Any(r => !participants.Contains(r.AccountId))) return new ExitReportResult.UnknownParticipant();

        var stored = await ExitReports.StoredAgentAsync(conn, tx, attemptId, ct);
        if (stored is not null && ExitReports.Same(stored, report)) return new ExitReportResult.Applied(stored.Outcome);
        if (stored is not null && !ExitReports.CanObserve(stored, report)) return new ExitReportResult.Conflicting();

        var batch = await AttemptStore.ReleaseParticipantsLockedAsync(conn, tx, attemptId, null, CancelReasons.ProcessExited,
            AttemptStore.PreStartStates, ct) ?? throw new InvalidOperationException("An attempt has no participants.");
        // The presented secret must still be current, read while this transaction holds the attempt row: a disable (which locks
        // the host's attempts after the host) either committed first and is seen here, or waits for this report (R8). Nothing
        // the release wrote survives a stale secret: the transaction rolls back.
        if (!await SecretCurrentAsync(conn, tx, hostId, secretHash, ct)) return new ExitReportResult.Ended();
        var revoked = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select credential_revoked_at is not null from mission_attempts where attempt_id = @attemptId", new { attemptId }, tx, cancellationToken: ct));
        var started = (await conn.QueryAsync<long>(new CommandDefinition(
            "select account_id from attempt_participants where attempt_id = @attemptId and started_at is not null", new { attemptId }, tx, cancellationToken: ct))).ToHashSet();
        if (report.OutboxComplete && !started.IsSubsetOf(report.Results.Select(r => r.AccountId))) return new ExitReportResult.IncompleteResults();

        // A concurrent first report committed while this one waited on the attempt: answer from it.
        stored = await ExitReports.StoredAgentAsync(conn, tx, attemptId, ct);
        if (stored is not null)
        {
            if (ExitReports.Same(stored, report)) return new ExitReportResult.Applied(stored.Outcome);
            if (!ExitReports.CanObserve(stored, report)) return new ExitReportResult.Conflicting();
            var observed = await ExitReports.StoredObservationAsync(conn, tx, attemptId, ct);
            if (observed is not null)
                return observed.Results.SetEquals(report.Results.Select(r => (r.AccountId, r.Present))) ? new ExitReportResult.Applied(observed.Outcome) : new ExitReportResult.Conflicting();
            var firstPresent = stored.Results.Where(r => r.Item2).Select(r => r.Item1).ToHashSet();
            var observedOutcome = await EvaluateExitEvidenceAsync(conn, tx, hostId, attemptId, revoked, report, firstPresent, ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into attempt_outbox_observations (attempt_id, host_id, attempt_revoked, outcome) values (@attemptId, @hostId, @revoked, @observedOutcome)",
                new { attemptId, hostId, revoked, observedOutcome }, tx, cancellationToken: ct));
            foreach (var r in report.Results)
                await conn.ExecuteAsync(new CommandDefinition(
                    "insert into attempt_outbox_observation_results (attempt_id, account_id, present) values (@attemptId, @AccountId, @Present)",
                    new { attemptId, r.AccountId, r.Present }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return new ExitReportResult.Applied(observedOutcome);
        }

        var outcome = batch.ReleasedAny ? "cancelled"
            : batch.AnyStarted ? await EvaluateExitEvidenceAsync(conn, tx, hostId, attemptId, revoked, report, new HashSet<long>(), ct)
            : "none";
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into attempt_exit_reports (attempt_id, source, host_id, attempt_state, any_started, exit_code, signal, outcome, outbox_complete, attempt_revoked)
            values (@attemptId, 'agent', @hostId, @seen, @AnyStarted, @ExitCode, @Signal, @outcome, @OutboxComplete, @revoked)
            """, new { attemptId, hostId, seen = batch.StateBefore, batch.AnyStarted, report.ExitCode, report.Signal, outcome, report.OutboxComplete, revoked },
            tx, cancellationToken: ct));
        foreach (var r in report.Results)
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into attempt_exit_results (attempt_id, account_id, present) values (@attemptId, @AccountId, @Present)",
                new { attemptId, r.AccountId, r.Present }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "update mission_attempts set slot_released_at = clock_timestamp() where attempt_id = @attemptId and slot_released_at is null",
            new { attemptId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new ExitReportResult.Applied(outcome);
    }

    sealed record EvidenceParticipant(long AccountId, string State, bool Started, bool Recorded, bool Submitted);

    /// <summary>
    /// S9-B7 over one report or observation, inside the exit transaction (accounts, attempt and participants locked by the
    /// release): returns "voided" when it voided someone, else "none". Only positive evidence voids; see <see cref="ExitedAsync"/>.
    /// </summary>
    static async Task<string> EvaluateExitEvidenceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string hostId, Guid attemptId, bool revoked, ExitedRequest report,
        IReadOnlySet<long> presentInFirstReport, CancellationToken ct)
    {
        var build = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("select build_revision from mission_attempts where attempt_id = @attemptId",
            new { attemptId }, tx, cancellationToken: ct));
        if (revoked)
        {
            await Settlement.Anomalies.RaiseAsync(conn, tx, Settlement.Anomalies.ExitEvidencePostRevocation, $"{attemptId}:{(report.OutboxComplete ? "complete" : "incomplete")}",
                new { hostId, report.ExitCode, report.Signal, outboxComplete = report.OutboxComplete }, ct, hostId: hostId, buildRevision: build, attemptId: attemptId);
            return "none";
        }
        var rows = (await conn.QueryAsync<EvidenceParticipant>(new CommandDefinition(
            """
            select p.account_id as AccountId, p.state as State, p.started_at is not null as Started, p.recorded_manifest_hash is not null as Recorded,
                   exists (select 1 from result_submissions s where s.attempt_id = p.attempt_id and s.account_id = p.account_id) as Submitted
            from attempt_participants p where p.attempt_id = @attemptId order by p.account_id
            """, new { attemptId }, tx, cancellationToken: ct))).ToList();
        var presence = report.Results.ToDictionary(r => r.AccountId, r => r.Present);
        var proof = report.OutboxComplete && (report.ExitCode is not null || report.Signal is not null);
        var voided = false;
        var inconclusive = new List<long>();
        foreach (var p in rows.Where(p => p.Started))
        {
            var listed = presence.TryGetValue(p.AccountId, out var present);
            if (listed && !present && (p.Recorded || p.Submitted || presentInFirstReport.Contains(p.AccountId)))
            {
                await Settlement.Anomalies.RaiseAsync(conn, tx, Settlement.Anomalies.ExitEvidenceContradiction, $"{attemptId}:{p.AccountId}",
                    new { hostId, recorded = p.Recorded, submitted = p.Submitted, presentInFirstReport = presentInFirstReport.Contains(p.AccountId) }, ct,
                    accountId: p.AccountId, hostId: hostId, buildRevision: build, attemptId: attemptId);
                continue;
            }
            if (p.State is not ("joined" or "left" or "result_pending")) continue; // settled, quarantined (its own recovery) or released
            if (listed && present)
            {
                if (p.State is "joined" or "left")
                    await conn.ExecuteAsync(new CommandDefinition(
                        "update attempt_participants set state = 'result_pending' where attempt_id = @attemptId and account_id = @AccountId and state in ('joined', 'left')",
                        new { attemptId, p.AccountId }, tx, cancellationToken: ct));
                continue;
            }
            if (listed && proof)
            {
                var state = await conn.ExecuteScalarAsync<string>(new CommandDefinition("select state from mission_attempts where attempt_id = @attemptId",
                    new { attemptId }, tx, cancellationToken: ct));
                await Settlement.VoidStore.VoidParticipantLockedAsync(conn, tx, attemptId, p.AccountId, state!, "exit_evidence", ct);
                await Settlement.Anomalies.RaiseAsync(conn, tx, Settlement.Anomalies.InfraVoid, $"{attemptId}:{p.AccountId}",
                    new { hostId, buildRevision = build, report.ExitCode, report.Signal, basis = "exit_evidence" }, ct,
                    accountId: p.AccountId, hostId: hostId, buildRevision: build, attemptId: attemptId);
                voided = true;
                continue;
            }
            inconclusive.Add(p.AccountId);
        }
        if (inconclusive.Count > 0)
            await Settlement.Anomalies.RaiseAsync(conn, tx, Settlement.Anomalies.ExitEvidenceInconclusive, $"{attemptId}:{(report.OutboxComplete ? "complete" : "incomplete")}",
                new { hostId, report.ExitCode, report.Signal, outboxComplete = report.OutboxComplete, unproven = inconclusive.Select(Blackglass.Accounts.Domain.Ids.Base58Id.Format) }, ct,
                hostId: hostId, buildRevision: build, attemptId: attemptId);
        var after = await conn.ExecuteScalarAsync<string>(new CommandDefinition("select state from mission_attempts where attempt_id = @attemptId",
            new { attemptId }, tx, cancellationToken: ct));
        await Settlement.SettlementStore.MarkEndingAsync(conn, tx, new Settlement.SettlementStore.Link(attemptId, 0), after!, ct);
        return voided ? "voided" : "none";
    }

    /// <summary>Whether <paramref name="secretHash"/> is still the enabled, unrevoked host's secret (a plain read: no host lock).</summary>
    static Task<bool> SecretCurrentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string hostId, byte[] secretHash, CancellationToken ct) =>
        conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from mission_hosts where host_id = @hostId and secret_hash = @secretHash and enabled and not secret_revoked)",
            new { hostId, secretHash }, tx, cancellationToken: ct));

    static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");

    /// <summary>The enabled agent host whose current secret hashes to <paramref name="secretHash"/>, or null. A disabled host,
    /// a development fixture and an unknown or rotated secret are all null (the uniform 401).</summary>
    public async Task<AuthenticatedHost?> AuthenticateAsync(byte[] secretHash, CancellationToken ct)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        var hostId = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select host_id from mission_hosts where secret_hash = @secretHash and kind = 'agent' and enabled and not secret_revoked", new { secretHash }, cancellationToken: ct));
        return hostId is null ? null : new AuthenticatedHost(hostId);
    }

    /// <summary>
    /// One heartbeat, one READ COMMITTED transaction: the host row FOR UPDATE (still enabled), the clock, the stamp and the
    /// reported build. Only when the report is a complete inventory (R2), the slot of each of this host's ended attempts that
    /// it no longer lists is released, once (attempts locked FOR UPDATE SKIP LOCKED: one an exit report is working on is
    /// left for the next heartbeat). The answer lists this host's ended attempts that the report still names, for the agent
    /// to stop (R4). Units of other hosts and unknown attempts are ignored. A host disabled since authentication gets null.
    /// </summary>
    public async Task<HeartbeatResult?> HeartbeatAsync(string hostId, byte[] secretHash, HeartbeatRequest report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        var listed = report.Units.Select(u => u.AttemptId).ToArray();
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var drained = await conn.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            "select drained from mission_hosts where host_id = @hostId and kind = 'agent' and enabled and secret_hash = @secretHash and not secret_revoked for update",
            new { hostId, secretHash }, tx, cancellationToken: ct));
        if (drained is null) return null;
        await conn.ExecuteAsync(new CommandDefinition(
            "update mission_hosts set last_heartbeat_at = clock_timestamp(), build_revision = @Revision, catalogue_version = @CatalogueVersion where host_id = @hostId",
            new { hostId, report.Build.Revision, report.Build.CatalogueVersion }, tx, cancellationToken: ct));
        if (report.InventoryComplete)
            await conn.ExecuteAsync(new CommandDefinition(
                """
                update mission_attempts set slot_released_at = clock_timestamp()
                where attempt_id in (select attempt_id from mission_attempts
                                     where host_id = @hostId and slot_released_at is null and state = any(@Ended) and attempt_id <> all(@listed)
                                     for update skip locked)
                """, new { hostId, Ended, listed }, tx, cancellationToken: ct));
        var stop = (await conn.QueryAsync<Guid>(new CommandDefinition(
            "select attempt_id from mission_attempts where host_id = @hostId and state = any(@Ended) and attempt_id = any(@listed) order by attempt_id",
            new { hostId, Ended, listed }, tx, cancellationToken: ct))).ToList();
        await tx.CommitAsync(ct);
        return new HeartbeatResult(drained.Value, stop);
    }
}
