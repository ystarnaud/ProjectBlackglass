using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Insurance;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.Persistence.Energy;
using Blackglass.Accounts.Persistence.Player;

namespace Blackglass.Accounts.Persistence.Attempts;

/// <summary>Why an admission was refused. The route maps each to its status (one exhaustive switch).</summary>
public enum AdmissionError
{
    /// <summary>The session's account row is missing (never happens: accounts are never deleted). Answered as unauthenticated.</summary>
    UnknownAccount,
    NotProvisioned, IdempotencyKeyReused, ServiceUnavailable, AttemptActive,
    UnknownTemplate, UnknownOperative, OperativeCount, RetiredOperative, UnknownItem, InsufficientEnergy,
}

/// <summary>A refused admission, with nothing written. Index is the requested operative's position when the refusal names one
/// (UnknownOperative, RetiredOperative, UnknownItem). The message is the reason's name only: no part of the request is echoed.</summary>
public sealed class AdmissionException(AdmissionError error, int? index = null) : Exception(error.ToString())
{
    public AdmissionError Error { get; } = error;
    public int? Index { get; } = index;
}

/// <summary>A 201 answer: the body text exactly as stored in <c>deploy_requests.response</c> (<c>response::text</c>, PostgreSQL's
/// jsonb rendering), so the first answer and every replay are byte-identical. Replayed when an earlier request with the same
/// key and request hash recorded it.</summary>
public sealed record Admission(string Body, bool Replayed);

/// <summary>
/// The 201 body of POST /v1/attempts: <c>{attemptId, state, energy:{cost, balanceAfter}, insurance:{covered, policyId,
/// policyVersion}}</c>. It never carries the seed or the retention salt.
/// </summary>
public sealed record DeployResponse(Guid AttemptId, string State, DeployEnergy Energy, DeployInsurance Insurance);

/// <param name="BalanceAfter">For a positive cost, the stored wallet balance right after the reserve. For a zero-cost template,
/// always null: such an admission takes no energy path at all (no wallet lock, no materialization, no policy lock), and an
/// exact balance is only known under the policy lock, so the answer never reports one (GET /v1/me does).</param>
public sealed record DeployEnergy(int Cost, int? BalanceAfter);

/// <summary>Whether the participant's insurance snapshot was taken, and of which policy version; both null when not covered.</summary>
public sealed record DeployInsurance(bool Covered, string? PolicyId, int? PolicyVersion);

/// <summary>
/// Admission (spec §7.3 as amended by owner rulings S7-D1 to S7-D9 and Checkpoint 1's F1 to F3): reserve energy, snapshot the
/// loadout and the insurance, and enforce one active attempt per account, in one READ COMMITTED transaction.
/// <list type="number">
/// <item>The request hash, then the <c>deploy_requests (account, key)</c> row read with no lock: the same hash replays the
/// stored body (even while the service is unhealthy); another hash is IdempotencyKeyReused.</item>
/// <item>The service-health check (<see cref="ServiceHealth"/>), before any transaction or lock: false is ServiceUnavailable,
/// with nothing locked, materialized or written (S7-D2).</item>
/// <item>The documented lock order: the accounts row FOR KEY SHARE first (the attempt, participant and deploy-request inserts
/// check their account foreign keys; taken there, after the energy policy lock, that check could wait on an operator's FOR
/// UPDATE while a publisher is queued: the Stage 5 precedent and the Stage 6 leaf rule), then the inventories row FOR UPDATE
/// (missing or unprovisioned: NotProvisioned).</item>
/// <item>The key re-read under the inventories lock (F2): a concurrent identical request that queued here replays, and a
/// conflicting one is IdempotencyKeyReused, never AttemptActive. Then the mandatory participation query: AttemptActive.</item>
/// <item>The latest template version (UnknownTemplate) and the operatives: each the account's own (another account's id is
/// the same UnknownOperative as an unknown one, with the request index), the count within the template's range
/// (OperativeCount), each definition known to the catalogue (RetiredOperative), each carried item's definition known
/// (UnknownItem). The snapshot (<see cref="LoadoutSnapshot"/>) keeps the requested order, reads choices in pick order and
/// carries the locked inventories version.</item>
/// <item>Energy and the clock. A positive cost: the wallet FOR UPDATE and its materialization
/// (<see cref="EnergyLedgerWriter.MaterializeLockedAsync"/>, which takes the shared policy lock and reads the clock after it);
/// no wallet, or a materialized balance below the cost, is InsufficientEnergy and everything rolls back. A zero cost takes no
/// wallet, materialization or policy lock: the clock is read directly.</item>
/// <item>Inserts of new rows only (F1, in foreign-key order): the attempt (UUIDv7, the API catalogue's version,
/// <c>assign_deadline</c> = now + the configured seconds), its <c>attempt_secrets</c> row (seed and salt from
/// <see cref="IAttemptSecrets"/>, owner ruling B1), the participant, the hold (the
/// cost, 0 for a zero-cost template), for a positive cost one wallet UPDATE and the <c>reserve:&lt;attempt&gt;</c> entry,
/// the insurance snapshot when the coverage (read without a lock) is active at now (its policy version's complete terms,
/// copied in SQL, and the coverage's activation), and the <c>deploy_requests</c> row, whose <c>response::text</c> is the body
/// answered (F3).</item>
/// </list>
/// A 23505 on <c>one_active_participation</c> (a race the inventories lock already prevents) is AttemptActive; one on
/// <c>deploy_requests_pkey</c> re-reads the key and answers as the first step. Any other failure rolls everything back and
/// propagates (a 23514 is a bug, a timeout is busy). Nothing here retries, and nothing here logs: the seed and salt never
/// leave the attempt_secrets row.
/// </summary>
public sealed class AdmissionStore
{
    public const int MinAssignDeadlineSeconds = 10;
    public const int MaxAssignDeadlineSeconds = 3600;
    public const string AdmittedState = "admitted";

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    static readonly ParticipationLock Participation = new(); // the same mandatory query the inventory lock uses (S7-D4)

    readonly NpgsqlDataSource db;
    readonly ServiceHealth health;
    readonly IAttemptSecrets secrets;
    readonly GameCatalogue catalogue;
    readonly int assignDeadlineSeconds;

    public AdmissionStore(NpgsqlDataSource db, ServiceHealth health, IAttemptSecrets secrets, GameCatalogue catalogue, int assignDeadlineSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(assignDeadlineSeconds, MinAssignDeadlineSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(assignDeadlineSeconds, MaxAssignDeadlineSeconds);
        (this.db, this.health, this.secrets, this.catalogue, this.assignDeadlineSeconds) = (
            db ?? throw new ArgumentNullException(nameof(db)), health ?? throw new ArgumentNullException(nameof(health)),
            secrets ?? throw new ArgumentNullException(nameof(secrets)), catalogue ?? throw new ArgumentNullException(nameof(catalogue)), assignDeadlineSeconds);
    }

    public async Task<Admission> AdmitAsync(long accountId, Guid idempotencyKey, DeployRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = request.RequestHash(); // the normalized, validated request (S7-D5): an unparseable one throws here, before any connection

        await using var conn = await DbFailure.OpenAsync(db, ct);
        if (await RecordedAsync(conn, null, accountId, idempotencyKey, hash, ct) is { } recorded) return recorded;
        if (!await health.CheckAsync(conn, null, ct)) throw new AdmissionException(AdmissionError.ServiceUnavailable);

        try { return await AdmitLockedAsync(conn, accountId, idempotencyKey, request, hash, ct); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "one_active_participation")
        {
            throw new AdmissionException(AdmissionError.AttemptActive);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "deploy_requests_pkey")
        {
            // The transaction is already rolled back. A concurrent first use of the key committed first: answer from its row.
            return await RecordedAsync(conn, null, accountId, idempotencyKey, hash, ct)
                ?? throw new InvalidOperationException("A deploy request key conflicted but no recorded request was found.");
        }
    }

    /// <summary>The recorded answer for the key: a replay with the same hash, IdempotencyKeyReused with another, null when none.</summary>
    static async Task<Admission?> RecordedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long accountId, Guid idempotencyKey, byte[] hash, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(byte[] Hash, string Body)?>(new CommandDefinition(
            "select request_hash, response::text from deploy_requests where account_id = @accountId and idempotency_key = @idempotencyKey",
            new { accountId, idempotencyKey }, tx, cancellationToken: ct));
        if (row is not { } stored) return null;
        if (!stored.Hash.AsSpan().SequenceEqual(hash)) throw new AdmissionException(AdmissionError.IdempotencyKeyReused);
        return new Admission(stored.Body, Replayed: true);
    }

    async Task<Admission> AdmitLockedAsync(NpgsqlConnection conn, long accountId, Guid idempotencyKey, DeployRequest request, byte[] hash, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // The lock order (Global Constraints): the account's key share, then its inventories row.
        var account = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "select 1 from accounts where account_id = @accountId for key share", new { accountId }, tx, cancellationToken: ct));
        if (account is null) throw new AdmissionException(AdmissionError.UnknownAccount);
        var inventory = await conn.QuerySingleOrDefaultAsync<(long Version, bool StarterGranted)?>(new CommandDefinition(
            "select version, starter_granted from inventories where account_id = @accountId for update", new { accountId }, tx, cancellationToken: ct));
        if (inventory is not { StarterGranted: true } locked) throw new AdmissionException(AdmissionError.NotProvisioned);

        if (await RecordedAsync(conn, tx, accountId, idempotencyKey, hash, ct) is { } recorded) return recorded; // F2; disposing rolls back
        if (await Participation.IsLockedAsync(conn, tx, accountId, ct)) throw new AdmissionException(AdmissionError.AttemptActive);

        var template = await MissionTemplates.LatestAsync(conn, tx, request.MissionTemplate, ct)
            ?? throw new AdmissionException(AdmissionError.UnknownTemplate);
        var snapshot = await SnapshotAsync(conn, tx, accountId, locked.Version, request.OperativeIds, template, ct);

        var cost = template.EnergyCost;
        DateTime now;
        int? balanceAfter = null;
        if (cost > 0)
        {
            var materialized = await EnergyLedgerWriter.MaterializeLockedAsync(conn, tx, accountId, ct)
                ?? throw new AdmissionException(AdmissionError.InsufficientEnergy); // no wallet is never a zero balance (E3)
            if (materialized.Wallet.Balance < cost) throw new AdmissionException(AdmissionError.InsufficientEnergy);
            (now, balanceAfter) = (materialized.Now, materialized.Wallet.Balance - cost);
        }
        else
        {
            now = Utc(await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition("select clock_timestamp()", transaction: tx, cancellationToken: ct)));
        }

        var attemptId = Guid.CreateVersion7();
        var salt = secrets.NextRetentionSalt();
        try
        {
            await ExactlyOneAsync(conn, tx, ct,
                """
                insert into mission_attempts (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline)
                values (@attemptId, @accountId, @TemplateId, @Version, @catalogueVersion, @deadline)
                """, new { attemptId, accountId, template.TemplateId, template.Version, catalogueVersion = catalogue.Version, deadline = now.AddSeconds(assignDeadlineSeconds) });
            await ExactlyOneAsync(conn, tx, ct,
                "insert into attempt_secrets (attempt_id, seed, retention_salt) values (@attemptId, @seed, @salt)",
                new { attemptId, seed = secrets.NextSeed(), salt });
        }
        finally { CryptographicOperations.ZeroMemory(salt); }
        await ExactlyOneAsync(conn, tx, ct,
            """
            insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
            values (@attemptId, @accountId, @TemplateId, @Version, @snapshot::jsonb, @inventoryVersion, @cost)
            """, new { attemptId, accountId, template.TemplateId, template.Version, snapshot = snapshot.ToJson(), inventoryVersion = locked.Version, cost });
        await ExactlyOneAsync(conn, tx, ct,
            "insert into energy_holds (attempt_id, account_id, amount) values (@attemptId, @accountId, @cost)", new { attemptId, accountId, cost });
        if (balanceAfter is { } after)
        {
            await ExactlyOneAsync(conn, tx, ct, "update energy_wallets set balance = @after where account_id = @accountId", new { accountId, after });
            await ExactlyOneAsync(conn, tx, ct,
                """
                insert into energy_ledger (account_id, attempt_id, kind, delta, balance_after, idempotency_key)
                values (@accountId, @attemptId, 'reserve', @delta, @after, @key)
                """, new { accountId, attemptId, delta = -cost, after, key = "reserve:" + attemptId.ToString("D") });
        }
        var insurance = await SnapshotInsuranceAsync(conn, tx, accountId, attemptId, now, ct);

        var body = JsonSerializer.Serialize(new DeployResponse(attemptId, AdmittedState, new DeployEnergy(cost, balanceAfter), insurance), Web);
        var stored = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            """
            insert into deploy_requests (account_id, idempotency_key, request_hash, attempt_id, response)
            values (@accountId, @idempotencyKey, @hash, @attemptId, @body::jsonb)
            returning response::text
            """, new { accountId, idempotencyKey, hash, attemptId, body }, tx, cancellationToken: ct))
            ?? throw new InvalidOperationException("The deploy request was not recorded.");
        await tx.CommitAsync(ct);
        return new Admission(stored, Replayed: false);
    }

    /// <summary>
    /// Validates the requested operatives in refusal order (UnknownOperative, OperativeCount, RetiredOperative, UnknownItem) and
    /// builds the snapshot from their rows, read under the inventories lock: every writer of operatives, choices and items holds
    /// that lock, so nothing read here can change before commit. Only the account's own rows are read, and only those of the
    /// requested operatives (S7-D8: no stash, no other operative).
    /// </summary>
    async Task<LoadoutSnapshot> SnapshotAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, long inventoryVersion, IReadOnlyList<long> requested,
        MissionTemplate template, CancellationToken ct)
    {
        var ids = requested.ToArray();
        var rows = (await conn.QueryAsync<(long OperativeId, string DefinitionId, int Experience)>(new CommandDefinition(
            "select operative_id, definition_id, experience from operatives where account_id = @accountId and operative_id = any(@ids)",
            new { accountId, ids }, tx, cancellationToken: ct))).ToDictionary(r => r.OperativeId);
        for (var i = 0; i < ids.Length; i++)
            if (!rows.ContainsKey(ids[i])) throw new AdmissionException(AdmissionError.UnknownOperative, i); // another account's is just as absent
        if (ids.Length < template.MinOperatives || ids.Length > template.MaxOperatives) throw new AdmissionException(AdmissionError.OperativeCount);
        for (var i = 0; i < ids.Length; i++)
            if (catalogue.FindOperative(rows[ids[i]].DefinitionId) is null) throw new AdmissionException(AdmissionError.RetiredOperative, i);

        var choices = (await conn.QueryAsync<(long OperativeId, string ChoiceId)>(new CommandDefinition(
            """
            select c.operative_id, c.choice_id from operative_choices c join operatives o on o.operative_id = c.operative_id
            where o.account_id = @accountId and c.operative_id = any(@ids) order by c.operative_id, c.ordinal
            """, new { accountId, ids }, tx, cancellationToken: ct))).ToLookup(c => c.OperativeId, c => c.ChoiceId);
        var instances = (await conn.QueryAsync<(long ItemId, string DefinitionId, long OperativeId, string? Slot)>(new CommandDefinition(
            "select item_id, definition_id, operative_id, equipped_slot from item_instances where account_id = @accountId and operative_id = any(@ids) order by item_id",
            new { accountId, ids }, tx, cancellationToken: ct)))
            .Select(i => new HeldInstance(i.ItemId, i.DefinitionId, i.OperativeId, ToSlot(i.Slot))).ToList();
        var stacks = (await conn.QueryAsync<(long OperativeId, string DefinitionId, int Quantity)>(new CommandDefinition(
            "select operative_id, definition_id, quantity from item_stacks where account_id = @accountId and operative_id = any(@ids)",
            new { accountId, ids }, tx, cancellationToken: ct)))
            .Select(s => new HeldStack(s.OperativeId, s.DefinitionId, s.Quantity)).ToList();

        var sources = ids.Select(id => new SnapshotOperativeSource(id, rows[id].DefinitionId, rows[id].Experience, choices[id].ToList())).ToList();
        var build = LoadoutSnapshot.Build(accountId, inventoryVersion, catalogue.Version, ids, sources, new InventoryModel(ids, instances, stacks), catalogue.Document);
        if (build.UnknownItems.Count > 0) throw new AdmissionException(AdmissionError.UnknownItem, build.UnknownItems.Min(u => u.OperativeIndex));
        return build.Snapshot;
    }

    /// <summary>
    /// The account's coverage, read without a lock (activations lock only their own rows, spec §15.3): when it is active at
    /// admission's now (<see cref="CoverageMath.IsActive"/>, starts_at &lt;= now &lt; expires_at), its policy version's
    /// complete terms are copied in SQL with the coverage's activation (S7-D3). An activation committing after this read does
    /// not apply to this attempt.
    /// </summary>
    static async Task<DeployInsurance> SnapshotInsuranceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, Guid attemptId, DateTime now, CancellationToken ct)
    {
        var coverage = await conn.QuerySingleOrDefaultAsync<(string PolicyId, int PolicyVersion, Guid ActivationId, DateTime StartsAt, DateTime ExpiresAt)?>(new CommandDefinition(
            "select policy_id, policy_version, activation_id, starts_at, expires_at from insurance_coverages where account_id = @accountId",
            new { accountId }, tx, cancellationToken: ct));
        if (coverage is not { } c || !CoverageMath.IsActive(Utc(c.StartsAt), Utc(c.ExpiresAt), now)) return new DeployInsurance(false, null, null);
        await ExactlyOneAsync(conn, tx, ct,
            """
            insert into attempt_insurance (attempt_id, account_id, activation_id, policy_id, policy_version, terms)
            select @attemptId, @accountId, @ActivationId, p.policy_id, p.version, p.terms
            from insurance_policies p where p.policy_id = @PolicyId and p.version = @PolicyVersion
            """, new { attemptId, accountId, c.ActivationId, c.PolicyId, c.PolicyVersion });
        return new DeployInsurance(true, c.PolicyId, c.PolicyVersion);
    }

    static async Task ExactlyOneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, string sql, object args)
    {
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));
        if (affected != 1) throw new InvalidOperationException($"An admission write touched {affected} rows instead of 1.");
    }

    static ItemSlot? ToSlot(string? stored) => stored switch
    {
        null => null,
        "weapon" => ItemSlot.Weapon,
        "armor" => ItemSlot.Armor,
        "utility" => ItemSlot.Utility,
        _ => throw new InvalidOperationException("An instance is stored in an unknown slot."),
    };

    /// <summary>Npgsql reads timestamptz as UTC; anything else is a broken mapping, never silently converted.</summary>
    static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : throw new InvalidOperationException($"A timestamptz was read as {value.Kind}, not UTC.");
}
