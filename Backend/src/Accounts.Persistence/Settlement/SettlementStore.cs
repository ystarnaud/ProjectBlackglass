using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Insurance;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.Persistence.Catalogue;
using Blackglass.Accounts.Persistence.Energy;

namespace Blackglass.Accounts.Persistence.Settlement;

/// <summary>The results route's answer (plan §C). Every answer but <see cref="Unauthenticated"/> follows a committed record.</summary>
public abstract record ResultOutcome
{
    public sealed record Settled(string Outcome) : ResultOutcome;
    public sealed record Duplicate(string Outcome) : ResultOutcome;
    public sealed record Quarantined(IReadOnlyList<string> Codes) : ResultOutcome;
    public sealed record Held : ResultOutcome;

    /// <param name="Code">Conflict, AlreadySettledAsVoid, AttemptRevoked, WrongAttempt, InvalidState or SubmissionLimit.</param>
    public sealed record Refused(string Code) : ResultOutcome;

    public sealed record Malformed(string Code) : ResultOutcome;

    /// <summary>The host secret is no longer current (the uniform 401): nothing committed.</summary>
    public sealed record Unauthenticated : ResultOutcome;
}

/// <summary>A relayed body as the route read it: the bytes (up to the cap), their full length, and the parse.</summary>
/// <param name="Raw">The request bytes; null when the body was over <see cref="ResultManifest.MaxBytes"/> (only its length is kept).</param>
public sealed record ResultBody(byte[]? Raw, int Length, ParseResult Parsed)
{
    public bool TooLarge => Raw is null;

    public static ResultBody Read(byte[] raw) => new(raw, raw.Length, ResultManifest.Parse(raw));

    public static ResultBody Oversized(int length) => new(null, length, new ParseResult.Malformed(ManifestCodes.TooLarge, null, null));
}

/// <summary>Stage 9's provisional settlement values (spec §19), each with its environment variable in the API's settings.</summary>
public sealed record SettlementOptions(int SubmissionsPerParticipant, int UnidentifiedPerHostPerDay, int EscalationHours, SettlementLimits Limits)
{
    public static readonly SettlementOptions Default = new(16, 64, 24, SettlementLimits.Default);
}

/// <summary>
/// The results route (plan §C, §E; S9-B1-B5, B10-B16, B20-B22): one READ COMMITTED transaction decides and commits exactly one
/// record (the settlement, the quarantine records, or the refusal record) before any definitive answer, and nothing is retried.
/// Lock order (§E): the participant's accounts row FOR KEY SHARE, the attempt FOR UPDATE, the host secret by plain read, the
/// participant FOR UPDATE; then, to settle, the hold FOR UPDATE, the inventory FOR UPDATE (V16), item and operative rows (under
/// the inventory lock), pool collections (under the attempt lock), the wallet FOR UPDATE, the shared energy policy lock, the
/// clock, the ledger, the submission and settlement rows, the states, and the post-apply check. A body naming no participant
/// of an attempt of this host takes no lock and writes only its own submission (and an anomaly without an account).
/// </summary>
public sealed class SettlementStore
{
    readonly NpgsqlDataSource db;
    readonly CatalogueHistoryStore catalogues;
    readonly SettlementOptions options;

    /// <summary>Test seam (S9-B14.3's corrupted-plan test): applied to every plan before it is written. Never set in production.</summary>
    internal Func<SettlementPlan, SettlementPlan>? PlanFilterForTests { get; set; }

    public SettlementStore(NpgsqlDataSource db, CatalogueHistoryStore catalogues, SettlementOptions options)
    {
        (this.db, this.catalogues, this.options) = (db ?? throw new ArgumentNullException(nameof(db)),
            catalogues ?? throw new ArgumentNullException(nameof(catalogues)), options ?? throw new ArgumentNullException(nameof(options)));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SubmissionsPerParticipant, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.UnidentifiedPerHostPerDay, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.EscalationHours, 1);
    }

    /// <summary>
    /// Decides a relayed body for the host whose secret hashes to <paramref name="secretHash"/>. A post-apply check failure
    /// rolls the settlement back and quarantines the participant in a fresh transaction (<c>settlement_check_failed</c>), which
    /// is still one committed record. A transient database failure propagates (the route answers 503); nothing is retried.
    /// </summary>
    public async Task<ResultOutcome> SubmitAsync(string hostId, byte[] secretHash, ResultBody body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        try { return await DecideAsync(hostId, secretHash, body, forceQuarantine: false, ct); }
        catch (SettlementCheckFailedException) { return await DecideAsync(hostId, secretHash, body, forceQuarantine: true, ct); }
    }

    sealed class SettlementCheckFailedException(string message) : Exception(message);

    internal sealed record AttemptRow(string State, string HostId, Guid? ProcessSessionId, bool Revoked, DateTime? StartedAt, string CatalogueVersion,
        string? BuildRevision, string? LayoutHash, int? LootTableVersion, string? LootHash);

    internal sealed record ParticipantRow(string State, DateTime? StartedAt, string Snapshot, long InventoryVersion, int EnergyCost, string? RecordedHash,
        DateTime? QuarantinedAt);

    sealed class StoredSubmission
    {
        public long SubmissionId { get; init; }
        public string Disposition { get; init; } = "";
        public string[] Codes { get; init; } = [];
        public long? SameAs { get; init; }
    }

    async Task<ResultOutcome> DecideAsync(string hostId, byte[] secretHash, ResultBody body, bool forceQuarantine, CancellationToken ct)
    {
        var parsed = body.Parsed as ParseResult.Parsed;
        var (claimedAttempt, claimedAccount) = body.Parsed switch
        {
            ParseResult.Parsed p => ((Guid?)p.Manifest.AttemptId, (long?)p.Manifest.AccountId),
            ParseResult.Malformed bad => (bad.ClaimedAttemptId, bad.ClaimedAccountId),
            _ => (null, null),
        };
        var rawHash = body.Raw is null ? null : SHA256.HashData(body.Raw);

        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var linked = false;
        AttemptRow? attempt = null;
        if (claimedAttempt is { } a0 && claimedAccount is { } c0)
        {
            var owner = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "select a.host_id from mission_attempts a join attempt_participants p using (attempt_id) where a.attempt_id = @a0 and p.account_id = @c0",
                new { a0, c0 }, tx, cancellationToken: ct));
            linked = owner == hostId;
        }
        if (linked)
        {
            await LockAccountAsync(conn, tx, claimedAccount!.Value, ct);
            attempt = await LockAttemptAsync(conn, tx, claimedAttempt!.Value, ct);
        }
        if (!await SecretCurrentAsync(conn, tx, hostId, secretHash, ct)) return new ResultOutcome.Unauthenticated();

        // A byte-identical repeat returns its stored answer (and only counts the repeat), whatever happened since.
        if (rawHash is not null)
        {
            var existing = linked
                ? await conn.QuerySingleOrDefaultAsync<StoredSubmission>(new CommandDefinition(
                    """
                    select submission_id as SubmissionId, disposition as Disposition, codes as Codes, same_as_submission_id as SameAs from result_submissions
                    where attempt_id = @a and account_id = @c and raw_hash = @rawHash
                    """, new { a = claimedAttempt, c = claimedAccount, rawHash }, tx, cancellationToken: ct))
                : await conn.QuerySingleOrDefaultAsync<StoredSubmission>(new CommandDefinition(
                    """
                    select submission_id as SubmissionId, disposition as Disposition, codes as Codes, same_as_submission_id as SameAs from result_submissions
                    where host_id = @hostId and raw_hash = @rawHash and attempt_id is null
                    """, new { hostId, rawHash }, tx, cancellationToken: ct));
            if (existing is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition("update result_submissions set receive_count = receive_count + 1 where submission_id = @SubmissionId",
                    existing, tx, cancellationToken: ct));
                var answer = await AnswerOfAsync(conn, tx, existing, ct);
                await tx.CommitAsync(ct);
                return answer;
            }
        }

        var detail = new { hostId, rawHash = rawHash is null ? null : Convert.ToHexStringLower(rawHash), length = body.Length };
        if (linked && attempt!.Revoked)
        {
            // S9-B1: no payload is stored for a revoked attempt; only the refusal, for the operator.
            await Anomalies.RaiseAsync(conn, tx, Anomalies.ResultRefusedRevoked, $"{hostId}:{detail.rawHash ?? "oversized:" + body.Length}",
                new { detail.hostId, detail.rawHash, detail.length, attemptId = claimedAttempt, accountId = Base58Id.Format(claimedAccount!.Value) }, ct,
                accountId: claimedAccount, hostId: hostId, attemptId: claimedAttempt);
            await tx.CommitAsync(ct);
            return new ResultOutcome.Refused("AttemptRevoked");
        }

        ParticipantRow? participant = linked ? await LockParticipantAsync(conn, tx, claimedAttempt!.Value, claimedAccount!.Value, ct) : null;

        // S9-B22: the caps, after the identical-retry check.
        if (linked)
        {
            var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "select count(*)::int from result_submissions where attempt_id = @a and account_id = @c and source = 'agent'",
                new { a = claimedAttempt, c = claimedAccount }, tx, cancellationToken: ct));
            if (count >= options.SubmissionsPerParticipant)
            {
                await Anomalies.RaiseAsync(conn, tx, Anomalies.SubmissionLimit, $"{claimedAttempt}:{claimedAccount}", new { detail.hostId, attemptId = claimedAttempt, count }, ct,
                    accountId: claimedAccount, hostId: hostId, attemptId: claimedAttempt);
                await tx.CommitAsync(ct);
                return new ResultOutcome.Refused("SubmissionLimit");
            }
        }
        else
        {
            var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "select count(*)::int from result_submissions where host_id = @hostId and attempt_id is null and first_received_at > clock_timestamp() - interval '24 hours'",
                new { hostId }, tx, cancellationToken: ct));
            if (count >= options.UnidentifiedPerHostPerDay)
            {
                await Anomalies.RaiseAsync(conn, tx, Anomalies.SubmissionLimit, $"{hostId}:unidentified", new { detail.hostId, count }, ct, hostId: hostId);
                await tx.CommitAsync(ct);
                return new ResultOutcome.Refused("SubmissionLimit");
            }
        }

        var link = linked ? new Link(claimedAttempt!.Value, claimedAccount!.Value) : null;
        if (parsed is null)
        {
            var code = ((ParseResult.Malformed)body.Parsed).Code;
            var id = await InsertSubmissionAsync(conn, tx, "agent", hostId, link, claimedAttempt, claimedAccount, false, body, rawHash, null, "malformed", null, [code], ct);
            // Quarantine only a participant the body identifies canonically, of this host's attempt, started and unsettled (§C 422).
            if (link is not null && participant!.State is "joined" or "left" or "result_pending" && attempt!.StartedAt is not null)
                await QuarantineAsync(conn, tx, link, "malformed_manifest", ct);
            await Anomalies.RaiseAsync(conn, tx, Anomalies.ResultMalformed, $"submission:{id}", new { submissionId = id, detail.hostId, detail.rawHash, detail.length, code }, ct,
                accountId: link?.Account, hostId: hostId, attemptId: link?.Attempt);
            await tx.CommitAsync(ct);
            return new ResultOutcome.Malformed(code);
        }

        var m = parsed.Manifest;
        if (link is null)
        {
            var id = await InsertSubmissionAsync(conn, tx, "agent", hostId, null, claimedAttempt, claimedAccount, false, body, rawHash, parsed, "refused", null, ["WrongAttempt"], ct);
            await Anomalies.RaiseAsync(conn, tx, Anomalies.ResultRefused, $"submission:{id}", new { submissionId = id, detail.hostId, code = "WrongAttempt" }, ct, hostId: hostId);
            await tx.CommitAsync(ct);
            return new ResultOutcome.Refused("WrongAttempt");
        }

        var outcome = await DecideLinkedAsync(conn, tx, "agent", hostId, link, attempt!, participant!, body, rawHash, parsed, forceQuarantine, ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    internal sealed record Link(Guid Attempt, long Account);

    /// <summary>The decision for a parsed manifest naming a participant (its attempt and participant locked), shared by the route
    /// and `attempt import-result` (source operator_import, which always quarantines an unsettled participant instead of settling).</summary>
    internal async Task<ResultOutcome> DecideLinkedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string source, string? hostId, Link link, AttemptRow attempt,
        ParticipantRow participant, ResultBody body, byte[]? rawHash, ParseResult.Parsed parsed, bool forceQuarantine, CancellationToken ct, string? importReason = null)
    {
        var m = parsed.Manifest;
        var revoked = attempt.Revoked;
        async Task<ResultOutcome> Refuse(string code, string kind)
        {
            var id = await InsertSubmissionAsync(conn, tx, source, hostId, link, link.Attempt, link.Account, revoked, body, rawHash, parsed, "refused", null, [code], ct);
            await Anomalies.RaiseAsync(conn, tx, kind, $"submission:{id}", new { submissionId = id, hostId, code }, ct, accountId: link.Account, hostId: hostId, attemptId: link.Attempt);
            return new ResultOutcome.Refused(code);
        }
        if (m.ProcessSessionId != attempt.ProcessSessionId) return await Refuse("WrongAttempt", Anomalies.ResultRefused);
        if (attempt.StartedAt is null || participant.State is "admitted" or "released") return await Refuse("InvalidState", Anomalies.ResultRefused);

        var settlement = await conn.QuerySingleOrDefaultAsync<(string Outcome, long? SubmissionId, string? ManifestHash)?>(new CommandDefinition(
            "select outcome, submission_id, manifest_hash from participant_settlements where attempt_id = @Attempt and account_id = @Account", link, tx, cancellationToken: ct));
        if (settlement is { } s)
        {
            if (s.Outcome == ResultManifest.InfraVoid)
            {
                var id = await InsertSubmissionAsync(conn, tx, source, hostId, link, link.Attempt, link.Account, revoked, body, rawHash, parsed, "late_after_void", null, [], ct);
                await Anomalies.RaiseAsync(conn, tx, Anomalies.LateResultAfterVoid, $"submission:{id}", new { submissionId = id, hostId, manifestHash = parsed.ManifestHash }, ct,
                    accountId: link.Account, hostId: hostId, attemptId: link.Attempt);
                return new ResultOutcome.Refused("AlreadySettledAsVoid");
            }
            if (s.ManifestHash == parsed.ManifestHash)
            {
                await InsertSubmissionAsync(conn, tx, source, hostId, link, link.Attempt, link.Account, revoked, body, rawHash, parsed, "same_as", s.SubmissionId, [], ct);
                return new ResultOutcome.Duplicate(s.Outcome);
            }
            var conflict = await InsertSubmissionAsync(conn, tx, source, hostId, link, link.Attempt, link.Account, revoked, body, rawHash, parsed, "conflict", null, [], ct);
            await Anomalies.RaiseAsync(conn, tx, Anomalies.SettlementConflict, $"submission:{conflict}", new { submissionId = conflict, hostId, manifestHash = parsed.ManifestHash,
                settledHash = s.ManifestHash }, ct, accountId: link.Account, hostId: hostId, attemptId: link.Attempt);
            return source == "operator_import" ? new ResultOutcome.Quarantined(["settled_conflict"]) : new ResultOutcome.Refused("Conflict");
        }

        // A different raw encoding of a manifest already stored for this participant gets that row's answer (S9-B3).
        var root = await conn.QuerySingleOrDefaultAsync<StoredSubmission>(new CommandDefinition(
            """
            select submission_id as SubmissionId, disposition as Disposition, codes as Codes, same_as_submission_id as SameAs from result_submissions
            where attempt_id = @Attempt and account_id = @Account and manifest_hash = @hash and disposition <> 'same_as'
            order by submission_id limit 1
            """, new { link.Attempt, link.Account, hash = parsed.ManifestHash }, tx, cancellationToken: ct));
        if (root is not null && source == "agent")
        {
            await InsertSubmissionAsync(conn, tx, source, hostId, link, link.Attempt, link.Account, revoked, body, rawHash, parsed, "same_as", root.SubmissionId, [], ct);
            return await AnswerOfAsync(conn, tx, root, ct);
        }

        var lookup = await catalogues.LookupLockedAsync(conn, tx, attempt.CatalogueVersion, ct);
        async Task<ResultOutcome> Quarantine(IReadOnlyList<string> codes, string reason, string disposition = "quarantined")
        {
            var id = await InsertSubmissionAsync(conn, tx, source, hostId, link, link.Attempt, link.Account, revoked, body, rawHash, parsed, disposition, null, codes, ct);
            if (participant.State is "joined" or "left" or "result_pending") await QuarantineAsync(conn, tx, link, reason, ct);
            var kind = disposition == "held" ? Anomalies.CatalogueUnavailable : Anomalies.ResultQuarantined;
            await Anomalies.RaiseAsync(conn, tx, kind, $"submission:{id}", new { submissionId = id, hostId, source, codes, reason = importReason }, ct,
                accountId: link.Account, hostId: hostId, attemptId: link.Attempt);
            return disposition == "held" ? new ResultOutcome.Held() : new ResultOutcome.Quarantined(codes);
        }
        if (lookup is not CatalogueLookup.Found { Catalogue: var admission })
            return await Quarantine(["catalogue_unavailable"], "catalogue_unavailable", "held");

        var context = await ContextAsync(conn, tx, link, attempt, participant, admission, ct);
        var codes = ManifestValidator.Validate(m, parsed.ManifestHash, context);
        if (source == "operator_import")
            return await Quarantine(codes.Count > 0 ? codes : ["operator_import"], revoked ? "host_revoked" : "operator_import");
        if (participant.State == "quarantined") // S9-B21: sticky, even for a manifest that would pass every check
            return await Quarantine(codes.Count > 0 ? codes : ["quarantine_active"], "invalid_manifest");
        if (codes.Count > 0)
            return await Quarantine(codes, codes.Contains("V15.recorded_hash") ? "recorded_hash_mismatch" : "invalid_manifest");

        var hold = await conn.QuerySingleAsync<(int Amount, string State)>(new CommandDefinition(
            "select amount, state from energy_holds where attempt_id = @Attempt and account_id = @Account for update", link, tx, cancellationToken: ct));
        var inventoryVersion = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "select version from inventories where account_id = @Account for update", link, tx, cancellationToken: ct));
        if (inventoryVersion != participant.InventoryVersion) return await Quarantine(["V16.inventory_base"], "inventory_base_changed");
        if (forceQuarantine) return await Quarantine(["settlement_check_failed"], "settlement_check_failed");
        if (hold.State != "charged") throw new InvalidOperationException("A started participant's hold is not charged.");

        var submission = await InsertSubmissionAsync(conn, tx, source, hostId, link, link.Attempt, link.Account, revoked, body, rawHash, parsed, "settled", null, [], ct);
        await ApplySettlementAsync(conn, tx, link, attempt, participant, context, m, parsed.ManifestHash, submission, hold.Amount, ct);
        if (participant.RecordedHash is null)
            await Anomalies.RaiseAsync(conn, tx, Anomalies.ResultWithoutRecordedHash, $"{link.Attempt}:{link.Account}", new { submissionId = submission, hostId }, ct,
                accountId: link.Account, hostId: hostId, attemptId: link.Attempt);
        return new ResultOutcome.Settled(m.Outcome);
    }

    /// <summary>
    /// Writes every effect of a validated manifest's settlement inside the caller's transaction (its participant, hold and
    /// inventory already locked; the submission already inserted): the plan's stock, items, equipment and XP, the energy settle,
    /// the inventory version, pool collections, the settlement row with its receipt, the participant's and attempt's states, then
    /// the post-apply check (a mismatch throws, rolling everything back). Shared with `attempt revalidate`.
    /// </summary>
    internal async Task ApplySettlementAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Link link, AttemptRow attempt, ParticipantRow participant,
        SettlementContext context, ResultManifest m, string manifestHash, long submissionId, int holdAmount, CancellationToken ct)
    {
        var salt = await conn.ExecuteScalarAsync<byte[]>(new CommandDefinition("select retention_salt from attempt_secrets where attempt_id = @Attempt", link, tx, cancellationToken: ct))
            ?? throw new InvalidOperationException("An attempt has no secrets row.");
        var insurance = await InsuranceOfAsync(conn, tx, link, ct);
        var plan = SettlementPlanner.Plan(m, context, insurance, salt);
        Array.Clear(salt);
        if (PlanFilterForTests is { } filter) plan = filter(plan);

        foreach (var d in plan.Debits)
        {
            var removed = await conn.ExecuteAsync(new CommandDefinition(
                "delete from item_stacks where account_id = @Account and operative_id = @OperativeId and definition_id = @DefinitionId and quantity = @Quantity",
                new { link.Account, d.OperativeId, d.DefinitionId, d.Quantity }, tx, cancellationToken: ct));
            if (removed == 0 && await conn.ExecuteAsync(new CommandDefinition(
                    "update item_stacks set quantity = quantity - @Quantity where account_id = @Account and operative_id = @OperativeId and definition_id = @DefinitionId and quantity > @Quantity",
                    new { link.Account, d.OperativeId, d.DefinitionId, d.Quantity }, tx, cancellationToken: ct)) != 1)
                throw new InvalidOperationException("A validated consumption found insufficient stock.");
        }
        var minted = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var u in plan.Mint) // plain inserts: a repeated origin_ref aborts the whole settlement (S9-B11.3)
            minted[u.OriginRef] = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                insert into item_instances (account_id, definition_id, operative_id, origin, origin_ref)
                values (@Account, @DefinitionId, @OperativeId, @Origin, @OriginRef) returning item_id
                """, new { link.Account, u.DefinitionId, u.OperativeId, u.Origin, u.OriginRef }, tx, cancellationToken: ct));
        foreach (var credit in plan.Credits)
        {
            var added = await conn.ExecuteAsync(new CommandDefinition(
                "update item_stacks set quantity = quantity + @Quantity where account_id = @Account and operative_id is not distinct from @OperativeId and definition_id = @DefinitionId",
                new { link.Account, credit.OperativeId, credit.DefinitionId, credit.Quantity }, tx, cancellationToken: ct));
            if (added == 0)
                await conn.ExecuteAsync(new CommandDefinition(
                    "insert into item_stacks (account_id, operative_id, definition_id, quantity) values (@Account, @OperativeId, @DefinitionId, @Quantity)",
                    new { link.Account, credit.OperativeId, credit.DefinitionId, credit.Quantity }, tx, cancellationToken: ct));
        }
        foreach (var gear in plan.Equipment)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "update item_instances set equipped_slot = null where account_id = @Account and operative_id = @OperativeId and equipped_slot is not null",
                new { link.Account, gear.OperativeId }, tx, cancellationToken: ct));
            foreach (var (slot, target) in new[] { ("weapon", gear.Weapon), ("armor", gear.Armor), ("utility", gear.Utility) })
            {
                var itemId = target.ItemId ?? (target.MintRef is { } r ? minted[r] : (long?)null);
                if (itemId is null) continue;
                if (await conn.ExecuteAsync(new CommandDefinition(
                        "update item_instances set equipped_slot = @slot where item_id = @itemId and account_id = @Account and operative_id = @OperativeId",
                        new { slot, itemId, link.Account, gear.OperativeId }, tx, cancellationToken: ct)) != 1)
                    throw new InvalidOperationException("A validated equipment reference was not in its operative's bag.");
            }
        }
        foreach (var grant in plan.Xp.Where(x => x.Amount > 0))
            if (await conn.ExecuteAsync(new CommandDefinition(
                    "update operatives set experience = least(2147483647, experience::bigint + @Amount)::int where operative_id = @OperativeId and account_id = @Account",
                    new { grant.Amount, grant.OperativeId, link.Account }, tx, cancellationToken: ct)) != 1)
                throw new InvalidOperationException("A snapshot operative is not the account's.");

        // Energy (S9-B15): a positive hold's wallet, the shared policy lock and the clock, then one settle entry of delta 0.
        DateTime now;
        if (holdAmount > 0)
        {
            var materialized = await EnergyLedgerWriter.MaterializeLockedAsync(conn, tx, link.Account, ct)
                ?? throw new InvalidOperationException("A positive energy hold has no wallet to settle.");
            now = materialized.Now;
            await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key)
                values (@Account, @Attempt, 'settle', 0, @balance, @key)
                """, new { link.Account, link.Attempt, balance = materialized.Wallet.Balance, key = SettleKey(link.Attempt) }, tx, cancellationToken: ct));
        }
        else now = Utc(await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition("select clock_timestamp()", transaction: tx, cancellationToken: ct)));
        await ExactlyOneAsync(conn, tx, ct, "update energy_holds set state = 'settled' where attempt_id = @Attempt and account_id = @Account and state = 'charged'", link);
        var version = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "update inventories set version = version + 1 where account_id = @Account returning version", link, tx, cancellationToken: ct));
        foreach (var ((ci, line), quantity) in plan.Collections)
            await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into pool_collections (attempt_id, container_index, line, collected) values (@Attempt, @ci, @line, @quantity)
                on conflict (attempt_id, container_index, line) do update set collected = pool_collections.collected + excluded.collected
                """, new { link.Attempt, ci, line, quantity }, tx, cancellationToken: ct));

        var receipt = ReceiptWriter.ForSettlement(plan, m, context, insurance, minted, holdAmount, version, now);
        var retained = JsonSerializer.Serialize((plan.Insurance?.Retained ?? []).Select(r => new { containerIndex = r.ContainerIndex, line = r.Line, definitionId = r.DefinitionId, units = r.Units }));
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into participant_settlements (attempt_id, account_id, outcome, submission_id, recorded_hash, manifest_hash, catalogue_version, retention_selection, receipt, settled_at)
            values (@Attempt, @Account, @outcome, @submissionId, @recorded, @manifestHash, @catalogue, @retained::jsonb, @receipt, @now)
            """, new { link.Attempt, link.Account, outcome = m.Outcome, submissionId, recorded = participant.RecordedHash is null ? "absent" : "matched", manifestHash,
                catalogue = context.AdmissionCatalogue.Version, retained, receipt = System.Text.Encoding.UTF8.GetString(receipt), now }, tx, cancellationToken: ct));
        await SettleStatesAsync(conn, tx, link, attempt.State, ct);
        await CheckBagsAsync(conn, tx, link, plan, minted, ct);
    }

    /// <summary>The participant → settled (escalation cleared), then the attempt: settled when no participant remains unsettled
    /// (running passes through ending in the same transaction), ending when every remaining one is result_pending.</summary>
    internal static async Task SettleStatesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Link link, string attemptState, CancellationToken ct)
    {
        await ExactlyOneAsync(conn, tx, ct,
            "update attempt_participants set state = 'settled', escalate_at = null where attempt_id = @Attempt and account_id = @Account and state in ('joined', 'left', 'result_pending', 'quarantined')", link);
        var remaining = (await conn.QueryAsync<string>(new CommandDefinition(
            "select state from attempt_participants where attempt_id = @Attempt and state not in ('settled', 'released')", link, tx, cancellationToken: ct))).ToList();
        if (remaining.Count == 0)
        {
            if (attemptState == "running")
                await ExactlyOneAsync(conn, tx, ct, "update mission_attempts set state = 'ending' where attempt_id = @Attempt and state = 'running'", link);
            await ExactlyOneAsync(conn, tx, ct,
                "update mission_attempts set state = 'settled', ended_at = clock_timestamp() where attempt_id = @Attempt and state in ('running', 'ending', 'unresponsive')", link);
        }
        else if (remaining.All(s => s == "result_pending") && attemptState is "running" or "unresponsive")
            await ExactlyOneAsync(conn, tx, ct, "update mission_attempts set state = 'ending' where attempt_id = @Attempt and state in ('running', 'unresponsive')", link);
    }

    /// <summary>S9-B14.3: each snapshot operative's bag and equipment equal the plan's expected bag, read back inside the transaction.</summary>
    async Task CheckBagsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Link link, SettlementPlan plan, IReadOnlyDictionary<string, long> minted, CancellationToken ct)
    {
        foreach (var bag in plan.ExpectedBags)
        {
            var items = (await conn.QueryAsync<(long ItemId, string? Slot)>(new CommandDefinition(
                "select item_id, equipped_slot from item_instances where account_id = @Account and operative_id = @OperativeId",
                new { link.Account, bag.OperativeId }, tx, cancellationToken: ct))).ToList();
            var stacks = (await conn.QueryAsync<(string Definition, int Quantity)>(new CommandDefinition(
                "select definition_id, quantity from item_stacks where account_id = @Account and operative_id = @OperativeId",
                new { link.Account, bag.OperativeId }, tx, cancellationToken: ct))).ToDictionary(s => s.Definition, s => s.Quantity, StringComparer.Ordinal);
            var expectedItems = bag.ItemIds.Concat(bag.MintRefs.Select(r => minted[r])).ToHashSet();
            long? Expected(EquipTarget t) => t.ItemId ?? (t.MintRef is { } r ? minted[r] : null);
            long? Actual(string slot) => items.Where(i => i.Slot == slot).Select(i => (long?)i.ItemId).SingleOrDefault();
            var same = expectedItems.SetEquals(items.Select(i => i.ItemId))
                && stacks.Count == bag.Stacks.Count && bag.Stacks.All(s => stacks.TryGetValue(s.Key, out var q) && q == s.Value)
                && Expected(bag.Equipment.Weapon) == Actual("weapon") && Expected(bag.Equipment.Armor) == Actual("armor") && Expected(bag.Equipment.Utility) == Actual("utility");
            if (!same) throw new SettlementCheckFailedException("A settled bag differs from its plan.");
        }
    }

    // ---- shared pieces ----

    internal static async Task LockAccountAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long account, CancellationToken ct) =>
        await conn.ExecuteAsync(new CommandDefinition("select 1 from accounts where account_id = @account for key share", new { account }, tx, cancellationToken: ct));

    internal static Task<AttemptRow> LockAttemptAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid attempt, CancellationToken ct) =>
        conn.QuerySingleAsync<AttemptRow>(new CommandDefinition(
            """
            select state as State, host_id as HostId, process_session_id as ProcessSessionId, credential_revoked_at is not null as Revoked, started_at as StartedAt,
                   catalogue_version as CatalogueVersion, build_revision as BuildRevision, layout_hash as LayoutHash, loot_table_version as LootTableVersion, loot_hash as LootHash
            from mission_attempts where attempt_id = @attempt for update
            """, new { attempt }, tx, cancellationToken: ct));

    internal static Task<ParticipantRow> LockParticipantAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid attempt, long account, CancellationToken ct) =>
        conn.QuerySingleAsync<ParticipantRow>(new CommandDefinition(
            """
            select state as State, started_at as StartedAt, loadout_snapshot::text as Snapshot, inventory_version as InventoryVersion, energy_cost as EnergyCost,
                   recorded_manifest_hash as RecordedHash, quarantined_at as QuarantinedAt
            from attempt_participants where attempt_id = @attempt and account_id = @account for update
            """, new { attempt, account }, tx, cancellationToken: ct));

    /// <summary>Whether <paramref name="secretHash"/> is still the enabled, unrevoked host's secret (a plain read: no host lock).</summary>
    internal static Task<bool> SecretCurrentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string hostId, byte[] secretHash, CancellationToken ct) =>
        conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from mission_hosts where host_id = @hostId and secret_hash = @secretHash and enabled and not secret_revoked)",
            new { hostId, secretHash }, tx, cancellationToken: ct));

    /// <summary>joined | left | result_pending → quarantined with its reason (kept once set) and the first escalation time.</summary>
    internal async Task QuarantineAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Link link, string reason, CancellationToken ct) =>
        await conn.ExecuteAsync(new CommandDefinition(
            """
            update attempt_participants set state = 'quarantined', quarantined_at = coalesce(quarantined_at, clock_timestamp()),
                   quarantine_reason = coalesce(quarantine_reason, @reason), escalate_at = clock_timestamp() + make_interval(hours => @hours)
            where attempt_id = @Attempt and account_id = @Account and state in ('joined', 'left', 'result_pending')
            """, new { link.Attempt, link.Account, reason, hours = options.EscalationHours }, tx, cancellationToken: ct));

    internal async Task<SettlementContext> ContextAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Link link, AttemptRow attempt, ParticipantRow participant,
        Blackglass.Accounts.Domain.Catalogue.GameCatalogue admission, CancellationToken ct)
    {
        var pool = (await conn.QueryAsync<PoolLine>(new CommandDefinition(
            "select container_index as ContainerIndex, line as Line, definition_id as DefinitionId, quantity as Quantity from attempt_loot_pool where attempt_id = @Attempt order by 1, 2",
            link, tx, cancellationToken: ct))).ToList();
        var objectives = (await conn.QueryAsync<(string Id, bool Required)>(new CommandDefinition(
            "select objective_id, required from attempt_objectives where attempt_id = @Attempt order by ordinal", link, tx, cancellationToken: ct)))
            .Select(o => new Objective(o.Id, o.Required)).ToList();
        var settled = (await conn.QueryAsync<(int Ci, int Line, int Collected)>(new CommandDefinition(
            "select container_index, line, collected from pool_collections where attempt_id = @Attempt", link, tx, cancellationToken: ct)))
            .ToDictionary(x => (x.Ci, x.Line), x => x.Collected);
        var now = Utc(await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition("select clock_timestamp()", transaction: tx, cancellationToken: ct)));
        var facts = new AttemptFacts(link.Attempt, link.Account, attempt.ProcessSessionId ?? Guid.Empty, attempt.BuildRevision ?? "", attempt.CatalogueVersion,
            attempt.LayoutHash ?? "", attempt.LootTableVersion ?? 0, attempt.LootHash ?? "", Utc(attempt.StartedAt!.Value), Utc(participant.StartedAt!.Value), now);
        return new SettlementContext(LoadoutSnapshot.Parse(participant.Snapshot), facts, pool, objectives, settled, admission, participant.RecordedHash, options.Limits);
    }

    static async Task<InsuranceSnapshot?> InsuranceOfAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Link link, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(string PolicyId, int PolicyVersion, string Terms)?>(new CommandDefinition(
            "select policy_id, policy_version, terms::text from attempt_insurance where attempt_id = @Attempt and account_id = @Account", link, tx, cancellationToken: ct));
        return row is { } r ? new InsuranceSnapshot(r.PolicyId, r.PolicyVersion, InsuranceTerms.Parse(r.Terms)) : null;
    }

    internal static async Task<long> InsertSubmissionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string source, string? hostId, Link? link, Guid? claimedAttempt,
        long? claimedAccount, bool attemptRevoked, ResultBody body, byte[]? rawHash, ParseResult.Parsed? parsed, string disposition, long? sameAs, IReadOnlyList<string> codes,
        CancellationToken ct)
    {
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            insert into result_submissions (source, host_id, process_session_id, attempt_id, account_id, claimed_attempt_id, claimed_account_id, attempt_revoked,
                                            raw_too_large, raw_length, raw_hash, manifest_hash, disposition, same_as_submission_id, codes)
            values (@source, @hostId, @session, @attempt, @account, @claimedAttempt, @claimedAccount, @attemptRevoked,
                    @tooLarge, @length, @rawHash, @manifestHash, @disposition, @sameAs, @codes)
            returning submission_id
            """, new { source, hostId, session = parsed?.Manifest.ProcessSessionId, attempt = link?.Attempt, account = link?.Account, claimedAttempt, claimedAccount,
                attemptRevoked, tooLarge = body.TooLarge, length = body.Length, rawHash, manifestHash = parsed?.ManifestHash, disposition, sameAs, codes = codes.ToArray() },
            tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "insert into result_submission_payloads (submission_id, raw_bytes, canonical_bytes) values (@id, @raw, @canonical)",
            new { id, raw = body.Raw, canonical = parsed?.CanonicalBytes }, tx, cancellationToken: ct));
        return id;
    }

    /// <summary>The answer a stored submission received (a repeat of it, or a same_as row of it, gets the same).</summary>
    static async Task<ResultOutcome> AnswerOfAsync(NpgsqlConnection conn, NpgsqlTransaction tx, StoredSubmission row, CancellationToken ct)
    {
        if (row.Disposition == "same_as")
        {
            var root = await conn.QuerySingleAsync<StoredSubmission>(new CommandDefinition(
                "select submission_id as SubmissionId, disposition as Disposition, codes as Codes, same_as_submission_id as SameAs from result_submissions where submission_id = @SameAs",
                row, tx, cancellationToken: ct));
            return await AnswerOfAsync(conn, tx, root, ct);
        }
        return row.Disposition switch
        {
            "settled" => new ResultOutcome.Duplicate(await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "select outcome from participant_settlements where submission_id = @SubmissionId", row, tx, cancellationToken: ct)) ?? "unknown"),
            "quarantined" => new ResultOutcome.Quarantined(row.Codes),
            "held" => new ResultOutcome.Held(),
            "conflict" => new ResultOutcome.Refused("Conflict"),
            "late_after_void" => new ResultOutcome.Refused("AlreadySettledAsVoid"),
            "refused" => new ResultOutcome.Refused(row.Codes.FirstOrDefault() ?? "WrongAttempt"),
            "malformed" => new ResultOutcome.Malformed(row.Codes.FirstOrDefault() ?? ManifestCodes.NotJson),
            _ => throw new InvalidOperationException("Unknown stored disposition."),
        };
    }

    /// <summary>The ledger key of a participant's settle entry (0009: exactly 'settle:' and the attempt).</summary>
    public static string SettleKey(Guid attemptId) => "settle:" + attemptId.ToString("D");

    internal static async Task ExactlyOneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, string sql, object args)
    {
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));
        if (affected != 1) throw new InvalidOperationException($"A settlement write touched {affected} rows instead of 1.");
    }

    internal static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");
}
