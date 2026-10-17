using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Insurance;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Insurance;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The operator insurance verbs (owner ruling S7-D3, spec §6.2) against a real PostgreSQL 17: immutable policy versions
/// minted under a per-policy advisory lock, and timed account coverages extended by idempotent activations. An activation is
/// one READ COMMITTED transaction: the accounts row FOR UPDATE, the coverage FOR UPDATE, the key lookup, then (a new key
/// only) the latest policy version and the database clock, and the writes: the activation row, the coverage, the audit. The
/// extension is exactly greatest(old expiry, now) + duration with now = clock_timestamp() after the coverage lock. Fixtures
/// write past coverages as the superuser; the two exact-microsecond boundaries use a frozen clock_timestamp() on one data
/// source (see <see cref="FrozenClockAsync"/>), the only way to make the database's now equal an expiry. The 0000-0007
/// invariants hold after every test.
/// </summary>
[Trait("Requires", "Postgres")]
public class InsuranceAdminStoreTests : IAsyncLifetime
{
    static readonly DbTimeouts Short = new(LockTimeoutMs: 300, StatementTimeoutMs: 10_000);
    static readonly TimeSpan Day = TimeSpan.FromDays(1);
    const int DaySeconds = 86_400;
    TestDatabase db = null!;
    InsuranceAdminStore insurance = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        insurance = new InsuranceAdminStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    sealed record Coverage(string PolicyId, int PolicyVersion, Guid ActivationId, DateTime StartsAt, DateTime ExpiresAt);
    sealed record Activation(Guid ActivationId, string PolicyId, int PolicyVersion, TimeSpan Duration, DateTime? PreviousExpiresAt, DateTime ResultingExpiresAt,
        string Source, Guid IdempotencyKey);
    sealed record Audit(string Action, long? AccountId, Guid CorrelationId, JsonElement Result);

    static string Terms(int refundPercent = 50) => $$$"""
        {"covers":["defeat","abandoned","disconnected","expired"],
         "consumableRefund":{"percent":{{{refundPercent}}},"rounding":"floor","destination":"stash"},
         "lootRetention":{"maxEntries":2,"maxUnitsPerEntry":3,"eligibleCategories":["Consumable","Armor","Utility","Weapon"],
           "rank":["categoryPriority","saltedHash"],"categoryPriority":["Weapon","Armor","Utility","Consumable"]},
         "progressXp":{"percent":40,"basis":"requiredObjectivesCompleted"}}
        """;

    static InsuranceTerms ParsedTerms(int refundPercent = 50) => InsuranceTerms.Parse(Terms(refundPercent));

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    async Task<Coverage?> CoverageAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<Coverage>(
            "select policy_id as PolicyId, policy_version as PolicyVersion, activation_id as ActivationId, starts_at as StartsAt, expires_at as ExpiresAt from insurance_coverages where account_id = @account",
            new { account });
    }

    async Task<List<Activation>> ActivationsAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<Activation>(
            """
            select activation_id as ActivationId, policy_id as PolicyId, policy_version as PolicyVersion, duration as Duration, previous_expires_at as PreviousExpiresAt,
              resulting_expires_at as ResultingExpiresAt, source as Source, idempotency_key as IdempotencyKey
            from insurance_activations where account_id = @account order by created_at, activation_id
            """, new { account })).ToList();
    }

    async Task<List<Audit>> AuditsAsync(string action)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var rows = await conn.QueryAsync<(string Action, long? AccountId, Guid CorrelationId, string Result)>(
            "select action, account_id, correlation_id, result::text from operator_actions where action = @action order by id", new { action });
        return rows.Select(r => new Audit(r.Action, r.AccountId, r.CorrelationId, JsonDocument.Parse(r.Result).RootElement.Clone())).ToList();
    }

    /// <summary>Everything an activation could write for this account, and the audit table as a whole.</summary>
    async Task<(Coverage? Coverage, string? Row, List<Activation> Activations, long Audits)> StateAsync(long account) =>
        (await CoverageAsync(account), await ScalarAsync<string?>("select xmin::text from insurance_coverages where account_id = @account", new { account }),
            await ActivationsAsync(account), await ScalarAsync<long>("select count(*) from operator_actions"));

    async Task AssertUnchangedAsync(long account, (Coverage? Coverage, string? Row, List<Activation> Activations, long Audits) before)
    {
        var after = await StateAsync(account);
        Assert.Equal(before.Coverage, after.Coverage);
        Assert.Equal(before.Row, after.Row); // the row was not even rewritten
        Assert.Equal(before.Activations, after.Activations);
        Assert.Equal(before.Audits, after.Audits);
    }

    Task<DateTime> DbNowAsync() => ScalarAsync<DateTime>("select clock_timestamp()");

    /// <summary>A coverage written directly (with the activation that set it): <paramref name="policy"/> from
    /// <paramref name="startsAt"/> to <paramref name="expiresAt"/>, which may be in the past.</summary>
    async Task<Guid> SeedCoverageAsync(long account, DateTime startsAt, DateTime expiresAt, string policy = "standard", int version = 1)
    {
        var activation = Guid.CreateVersion7();
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(
            """
            insert into insurance_activations (activation_id, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source, idempotency_key)
            values (@activation, @account, @policy, @version, @duration, null, @expiresAt, 'dev_grant', gen_random_uuid())
            """, new { activation, account, policy, version, duration = expiresAt - startsAt, expiresAt }, tx);
        await conn.ExecuteAsync(
            "insert into insurance_coverages (account_id, policy_id, policy_version, activation_id, starts_at, expires_at) values (@account, @policy, @version, @activation, @startsAt, @expiresAt)",
            new { account, policy, version, activation, startsAt, expiresAt }, tx);
        await tx.CommitAsync();
        return activation;
    }

    NpgsqlConnectionStringBuilder AppConnection(int maxPoolSize) => new()
    {
        Host = db.Host, Port = int.Parse(db.EnvFor("app")[DbSettings.PortVar]!, System.Globalization.CultureInfo.InvariantCulture), Database = db.Name,
        Username = "accounts_app", Password = db.Password("app"), ApplicationName = "blackglass-tests", MaxPoolSize = maxPoolSize,
    };

    /// <summary>The app role with a pool wide enough for every racer, and no lock_timeout.</summary>
    NpgsqlDataSource Wide(int connections) => NpgsqlDataSource.Create(AppConnection(connections));

    /// <summary>
    /// The app role on connections whose unqualified clock_timestamp() reads <paramref name="at"/>: a test-only function in
    /// schema bg_test_clock, put before pg_catalog in their search_path. The store's SQL is unchanged (it calls
    /// clock_timestamp() unqualified, as every Stage 7 path does); only the database's answer is pinned, so an expiry can equal
    /// the activation's now to the microsecond. Dropped by <see cref="DropFrozenClockAsync"/> before the invariant check.
    /// </summary>
    async Task<NpgsqlDataSource> FrozenClockAsync(DateTime at)
    {
        await ExecuteAsSuperuserAsync(db,
            """
            create schema bg_test_clock;
            create table bg_test_clock.frozen (at timestamptz not null);
            create function bg_test_clock.clock_timestamp() returns timestamptz language sql stable as 'select at from bg_test_clock.frozen';
            grant usage on schema bg_test_clock to accounts_app;
            grant select on bg_test_clock.frozen to accounts_app;
            grant execute on function bg_test_clock.clock_timestamp() to accounts_app;
            """);
        await ExecuteAsSuperuserAsync(db, "insert into bg_test_clock.frozen (at) values (@at)", new { at });
        var builder = AppConnection(2);
        builder.SearchPath = "bg_test_clock, pg_catalog, public";
        return NpgsqlDataSource.Create(builder);
    }

    Task DropFrozenClockAsync() => ExecuteAsSuperuserAsync(db, "drop schema bg_test_clock cascade");

    /// <summary>Returns once at least <paramref name="count"/> backends of this database wait on a lock (polls every 20 ms).</summary>
    async Task WaitForLockWaitersAsync(int count = 1)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await su.ExecuteScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Fewer than {count} backends waited on a lock.");
            await Task.Delay(20);
        }
    }

    static string[] Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
    static string[] Keys(params string[] names) => names.Order(StringComparer.Ordinal).ToArray();

    // ---- activation ----

    [Fact]
    public async Task Activating_the_same_policy_extends_from_the_later_of_expiry_and_now()
    {
        var account = await NewAccountAsync();
        var key1 = Guid.CreateVersion7();
        var op = OperatorContext.Create("insurer", "extend test");

        // No coverage: a fresh one starts at the database's now.
        var before = await DbNowAsync();
        var first = await insurance.ActivateAsync(account, "standard", DaySeconds, key1, op);
        var after = await DbNowAsync();
        var fresh = await CoverageAsync(account);
        Assert.NotNull(fresh);
        Assert.InRange(fresh.StartsAt, before, after);
        Assert.Equal(fresh.StartsAt + Day, fresh.ExpiresAt);
        Assert.Equal(new ActivationResult(first.ActivationId, "standard", 1, fresh.StartsAt, fresh.ExpiresAt, false), first);
        Assert.Equal(new Coverage("standard", 1, first.ActivationId, fresh.StartsAt, fresh.ExpiresAt), fresh);

        // Running: the remaining time is kept and the new duration added to the old expiry; the start is kept.
        var key2 = Guid.CreateVersion7();
        var second = await insurance.ActivateAsync(account, "standard", 2 * DaySeconds, key2, op);
        var extended = await CoverageAsync(account);
        Assert.Equal(new Coverage("standard", 1, second.ActivationId, fresh.StartsAt, fresh.ExpiresAt + 2 * Day), extended);
        Assert.Equal(new ActivationResult(second.ActivationId, "standard", 1, fresh.ExpiresAt, fresh.ExpiresAt + 2 * Day, false), second); // its own period

        Assert.Equal(
            [
                new Activation(first.ActivationId, "standard", 1, Day, null, fresh.ExpiresAt, "dev_grant", key1),
                new Activation(second.ActivationId, "standard", 1, 2 * Day, fresh.ExpiresAt, fresh.ExpiresAt + 2 * Day, "dev_grant", key2),
            ],
            await ActivationsAsync(account));
        var audits = await AuditsAsync("insurance_activate");
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a => Assert.Equal((account, op.CorrelationId), (a.AccountId!.Value, a.CorrelationId)));
        Assert.Equal(Keys("activationId", "policyId", "policyVersion", "durationSeconds", "previousExpiresAt", "coverageStartsAt", "expiresAt", "idempotencyKey"),
            Names(audits[1].Result));
        Assert.Equal((second.ActivationId.ToString("D"), "standard", 1, 2 * DaySeconds, key2.ToString("D")),
            (audits[1].Result.GetProperty("activationId").GetString(), audits[1].Result.GetProperty("policyId").GetString(), audits[1].Result.GetProperty("policyVersion").GetInt32(),
                audits[1].Result.GetProperty("durationSeconds").GetInt32(), audits[1].Result.GetProperty("idempotencyKey").GetString()));
        Assert.Equal(JsonValueKind.Null, audits[0].Result.GetProperty("previousExpiresAt").ValueKind);

        // A newer policy version: the next extension moves the coverage to it (a running attempt's snapshot never moves).
        var v2 = await insurance.PublishPolicyAsync("standard", ParsedTerms(60), op);
        Assert.Equal(2, v2.Version);
        var third = await insurance.ActivateAsync(account, "standard", DaySeconds, Guid.CreateVersion7(), op);
        Assert.Equal(new Coverage("standard", 2, third.ActivationId, fresh.StartsAt, fresh.ExpiresAt + 3 * Day), await CoverageAsync(account));
        Assert.Equal(2, third.PolicyVersion);
    }

    [Fact]
    public async Task An_expired_coverage_restarts_from_now()
    {
        var now = await DbNowAsync();
        var oldExpiry = now - TimeSpan.FromHours(1);

        // The same policy, expired: a fresh coverage from now (not from the old expiry).
        var same = await NewAccountAsync();
        await SeedCoverageAsync(same, oldExpiry - Day, oldExpiry);
        var before = await DbNowAsync();
        var renewed = await insurance.ActivateAsync(same, "standard", DaySeconds, Guid.CreateVersion7(), Op);
        var coverage = await CoverageAsync(same);
        Assert.NotNull(coverage);
        Assert.True(coverage.StartsAt >= before, $"{coverage.StartsAt:O} < {before:O}");
        Assert.Equal((coverage.StartsAt + Day, renewed.ActivationId), (coverage.ExpiresAt, coverage.ActivationId));
        Assert.Equal((coverage.StartsAt, coverage.ExpiresAt, false), (renewed.StartsAt, renewed.ExpiresAt, renewed.Replayed));
        Assert.Equal(oldExpiry, (await ActivationsAsync(same))[^1].PreviousExpiresAt);

        // Another policy once the old coverage expired: a fresh coverage of the new policy (S7-D3).
        await insurance.PublishPolicyAsync("premium", ParsedTerms(80), Op);
        var other = await NewAccountAsync();
        await SeedCoverageAsync(other, oldExpiry - Day, oldExpiry);
        var switched = await insurance.ActivateAsync(other, "premium", DaySeconds, Guid.CreateVersion7(), Op);
        var premium = await CoverageAsync(other);
        Assert.NotNull(premium);
        Assert.Equal(("premium", 1, switched.ActivationId, premium.StartsAt + Day), (premium.PolicyId, premium.PolicyVersion, premium.ActivationId, premium.ExpiresAt));
        Assert.True(premium.StartsAt >= before);
        Assert.Equal(oldExpiry, (await ActivationsAsync(other))[^1].PreviousExpiresAt);
    }

    [Fact]
    public async Task A_different_policy_while_active_is_CrossTierDeferred_and_writes_nothing()
    {
        await insurance.PublishPolicyAsync("premium", ParsedTerms(80), Op);
        var account = await NewAccountAsync();
        await insurance.ActivateAsync(account, "standard", DaySeconds, Guid.CreateVersion7(), Op);
        var state = await StateAsync(account);
        var key = Guid.CreateVersion7();

        var e = await Assert.ThrowsAsync<InsuranceException>(() => insurance.ActivateAsync(account, "premium", DaySeconds, key, Op));

        Assert.Equal(InsuranceError.CrossTierDeferred, e.Error);
        Assert.Contains("nothing was changed", e.Message, StringComparison.OrdinalIgnoreCase);
        await AssertUnchangedAsync(account, state);
        // The refused key was never used: the same policy may still be activated with it.
        var extended = await insurance.ActivateAsync(account, "standard", DaySeconds, key, Op);
        Assert.False(extended.Replayed);
    }

    [Fact]
    public async Task A_duplicate_key_does_not_extend_twice()
    {
        var account = await NewAccountAsync();
        var key = Guid.CreateVersion7();
        var first = await insurance.ActivateAsync(account, "standard", DaySeconds, key, Op);
        var state = await StateAsync(account);

        var replay = await insurance.ActivateAsync(account, "standard", DaySeconds, key, OperatorContext.Create("someone-else", "a retry"));

        Assert.Equal(first with { Replayed = true }, replay);
        await AssertUnchangedAsync(account, state); // no activation, no coverage rewrite, no audit row

        // A replay still answers after a newer version was published and the coverage expired: it is the stored result.
        await insurance.PublishPolicyAsync("standard", ParsedTerms(60), Op);
        await ExecuteAsSuperuserAsync(db,
            "begin; set local session_replication_role = replica; update insurance_coverages set starts_at = starts_at - interval '3 days', expires_at = expires_at - interval '3 days' where account_id = @account; commit;",
            new { account });
        state = await StateAsync(account);
        Assert.Equal(first with { Replayed = true }, await insurance.ActivateAsync(account, "standard", DaySeconds, key, Op));
        await AssertUnchangedAsync(account, state);
    }

    [Fact]
    public async Task Twenty_concurrent_activations_with_one_key_extend_once()
    {
        const int Callers = 20;
        var account = await NewAccountAsync();
        await insurance.ActivateAsync(account, "standard", DaySeconds, Guid.CreateVersion7(), Op);
        var running = await CoverageAsync(account);
        await using var wide = Wide(Callers + 1);
        var racing = new InsuranceAdminStore(wide);
        var key = Guid.CreateVersion7();

        // The start barrier: the account row is held, so all twenty reach its lock on distinct connections before any proceeds.
        await using var holder = await db.AsApp().OpenConnectionAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from accounts where account_id = @account for update", new { account }, holderTx);
        var callers = Enumerable.Range(0, Callers)
            .Select(i => Task.Run(() => racing.ActivateAsync(account, "standard", DaySeconds, key, OperatorContext.Create("racer", $"race {i}")))).ToArray();
        await WaitForLockWaitersAsync(Callers);
        await holderTx.CommitAsync();

        var results = await Task.WhenAll(callers);
        var made = Assert.Single(results, r => !r.Replayed);
        Assert.All(results, r => Assert.Equal(made with { Replayed = r.Replayed }, r));
        Assert.Equal(running!.ExpiresAt + Day, (await CoverageAsync(account))!.ExpiresAt); // extended once
        Assert.Equal(2, (await ActivationsAsync(account)).Count);
        Assert.Equal(2, (await AuditsAsync("insurance_activate")).Count); // the nineteen replays wrote nothing
    }

    [Fact]
    public async Task A_reused_key_with_other_parameters_is_refused()
    {
        await insurance.PublishPolicyAsync("premium", ParsedTerms(80), Op);
        var account = await NewAccountAsync();
        var key = Guid.CreateVersion7();
        await insurance.ActivateAsync(account, "standard", DaySeconds, key, Op);
        var state = await StateAsync(account);

        foreach (var (policy, seconds) in new[] { ("standard", DaySeconds + 1), ("premium", DaySeconds), ("unknown-policy", DaySeconds) })
        {
            var e = await Assert.ThrowsAsync<InsuranceException>(() => insurance.ActivateAsync(account, policy, seconds, key, Op));
            Assert.Equal(InsuranceError.IdempotencyKeyReused, e.Error); // the key is looked up first, before the policy or tier
            await AssertUnchangedAsync(account, state);
        }

        // The key is unique per account: another account may use the same one.
        var other = await NewAccountAsync();
        Assert.False((await insurance.ActivateAsync(other, "premium", DaySeconds, key, Op)).Replayed);
    }

    [Fact]
    public async Task Activating_an_unknown_policy_or_for_an_unknown_account_writes_nothing()
    {
        var account = await NewAccountAsync();
        var state = await StateAsync(account);

        var unknown = await Assert.ThrowsAsync<InsuranceException>(() => insurance.ActivateAsync(account, "never-published", DaySeconds, Guid.CreateVersion7(), Op));
        Assert.Equal(InsuranceError.UnknownPolicy, unknown.Error);
        var missing = await Assert.ThrowsAsync<AccountAdminException>(() => insurance.ActivateAsync(account + 1000, "standard", DaySeconds, Guid.CreateVersion7(), Op));
        Assert.Equal(AccountAdminError.NotFound, missing.Error);

        await AssertUnchangedAsync(account, state);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from insurance_activations"));
    }

    [Fact]
    public async Task Activation_uses_the_database_clock_after_the_coverage_lock()
    {
        var account = await NewAccountAsync();
        var now = await DbNowAsync();
        await SeedCoverageAsync(account, now - 2 * Day, now - Day); // expired: the new coverage starts at the activation's now

        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from insurance_coverages where account_id = @account for update", new { account }, holderTx);
        var activation = insurance.ActivateAsync(account, "standard", DaySeconds, Guid.CreateVersion7(), Op);
        await WaitForLockWaitersAsync();
        var released = await holder.ExecuteScalarAsync<DateTime>("select clock_timestamp()", transaction: holderTx);
        await holderTx.CommitAsync();

        var result = await activation;
        Assert.True(result.StartsAt > released, $"now {result.StartsAt:O} was read before the coverage lock was released at {released:O}");
        Assert.Equal(result.StartsAt, (await CoverageAsync(account))!.StartsAt);
    }

    [Fact]
    public async Task Activation_exactly_at_expiry_restarts_from_now()
    {
        var account = await NewAccountAsync();
        var expiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(2), DateTimeKind.Utc);
        var oldStart = expiry - Day;
        await SeedCoverageAsync(account, oldStart, expiry);
        await using var frozen = await FrozenClockAsync(expiry); // now == expires_at: the coverage is over
        try
        {
            var result = await new InsuranceAdminStore(frozen).ActivateAsync(account, "standard", DaySeconds, Guid.CreateVersion7(), Op);

            var coverage = await CoverageAsync(account);
            Assert.Equal(new Coverage("standard", 1, result.ActivationId, expiry, expiry + Day), coverage); // a fresh start at now
            Assert.Equal((expiry, expiry + Day), (result.StartsAt, result.ExpiresAt));
            Assert.Equal((expiry, expiry + Day), ((await ActivationsAsync(account))[^1].PreviousExpiresAt, (await ActivationsAsync(account))[^1].ResultingExpiresAt));
        }
        finally { await DropFrozenClockAsync(); }
    }

    [Fact]
    public async Task Activation_one_microsecond_before_expiry_extends_from_the_old_expiry()
    {
        var account = await NewAccountAsync();
        var expiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(2), DateTimeKind.Utc);
        var oldStart = expiry - Day;
        await SeedCoverageAsync(account, oldStart, expiry);
        await using var frozen = await FrozenClockAsync(expiry - TimeSpan.FromMicroseconds(1)); // still active
        try
        {
            var result = await new InsuranceAdminStore(frozen).ActivateAsync(account, "standard", DaySeconds, Guid.CreateVersion7(), Op);

            var coverage = await CoverageAsync(account);
            Assert.Equal(new Coverage("standard", 1, result.ActivationId, oldStart, expiry + Day), coverage); // the start is kept
            Assert.Equal((expiry, expiry + Day), (result.StartsAt, result.ExpiresAt));
            Assert.Equal(expiry, (await ActivationsAsync(account))[^1].PreviousExpiresAt);
        }
        finally { await DropFrozenClockAsync(); }
    }

    [Fact]
    public async Task An_invalid_duration_or_policy_id_is_refused_before_any_connection()
    {
        // A store whose database can't be reached: a refusal proves the check came before any connection.
        await using var nowhere = new DbSettings("127.0.0.1", 1, db.Name, "accounts_app", "unused").CreateDataSource("blackglass-tests", maxPoolSize: 1);
        var unreachable = new InsuranceAdminStore(nowhere);

        foreach (var seconds in new[] { 0, -1, InsuranceAdminStore.MaxDurationSeconds + 1 })
            Assert.Equal(InsuranceError.InvalidDuration,
                (await Assert.ThrowsAsync<InsuranceException>(() => unreachable.ActivateAsync(1, "standard", seconds, Guid.CreateVersion7(), Op))).Error);
        foreach (var policy in new[] { "", "Standard", "a_b", "a..b", new string('a', 65) })
        {
            Assert.Equal(InsuranceError.InvalidPolicyId,
                (await Assert.ThrowsAsync<InsuranceException>(() => unreachable.ActivateAsync(1, policy, DaySeconds, Guid.CreateVersion7(), Op))).Error);
            Assert.Equal(InsuranceError.InvalidPolicyId,
                (await Assert.ThrowsAsync<InsuranceException>(() => unreachable.PublishPolicyAsync(policy, ParsedTerms(), Op))).Error);
        }
        // The bounds themselves are accepted: 1 s and 3650 days.
        InsuranceAdminStore.ValidateDuration(1);
        InsuranceAdminStore.ValidateDuration(InsuranceAdminStore.MaxDurationSeconds);
        InsuranceAdminStore.ValidatePolicyId(new string('a', 64));
    }

    // ---- publication ----

    [Fact]
    public async Task Policies_are_immutable_and_versions_increase()
    {
        var seeded = await ScalarAsync<string>("select terms::text from insurance_policies where policy_id = 'standard' and version = 1");
        var op = OperatorContext.Create("publisher", "publish test");

        var premium1 = await insurance.PublishPolicyAsync("premium", ParsedTerms(80), op);
        var premium2 = await insurance.PublishPolicyAsync("premium", ParsedTerms(90), op);
        var standard2 = await insurance.PublishPolicyAsync("standard", ParsedTerms(60), op);

        Assert.Equal(("premium", 1), (premium1.PolicyId, premium1.Version));
        Assert.Equal(("premium", 2), (premium2.PolicyId, premium2.Version));
        Assert.Equal(("standard", 2), (standard2.PolicyId, standard2.Version)); // after the seeded version 1
        Assert.Equal(seeded, await ScalarAsync<string>("select terms::text from insurance_policies where policy_id = 'standard' and version = 1")); // untouched
        // Stored as the canonical terms, whose SHA-256 the audit records (never the terms themselves).
        var canonical = ParsedTerms(90).Canonical();
        Assert.True(await ScalarAsync<bool>("select terms = @canonical::jsonb from insurance_policies where policy_id = 'premium' and version = 2", new { canonical }));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))), premium2.TermsSha256);
        var audits = await AuditsAsync("insurance_policy_publish");
        Assert.Equal(3, audits.Count);
        Assert.All(audits, a => { Assert.Null(a.AccountId); Assert.Equal(op.CorrelationId, a.CorrelationId); });
        Assert.Equal(Keys("policyId", "version", "termsSha256"), Names(audits[1].Result));
        Assert.Equal(("premium", 2, premium2.TermsSha256),
            (audits[1].Result.GetProperty("policyId").GetString(), audits[1].Result.GetProperty("version").GetInt32(), audits[1].Result.GetProperty("termsSha256").GetString()));

        // Ten concurrent publications of one policy: the advisory lock gives ten consecutive versions, no conflict.
        await using var wide = Wide(11);
        var racing = new InsuranceAdminStore(wide);
        var published = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => racing.PublishPolicyAsync("race", ParsedTerms(i), op))));
        Assert.Equal(Enumerable.Range(1, 10), published.Select(p => p.Version).Order());

        // A published version never changes: the app role may not update it, and the trigger refuses even the owner.
        await using var app = await db.AsApp().OpenConnectionAsync();
        var denied = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync("update insurance_policies set terms = '{}' where policy_id = 'premium' and version = 1"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var refused = await Assert.ThrowsAsync<PostgresException>(() => su.ExecuteAsync("update insurance_policies set terms = '{}' where policy_id = 'premium' and version = 1"));
        Assert.Equal("insurance_policy_is_immutable", refused.ConstraintName);
    }

    [Fact]
    public async Task A_version_conflict_on_the_primary_key_is_reported_and_not_retried()
    {
        // A unique violation on the primary key, as an insert bypassing the publication lock would cause. Each attempt is
        // counted outside the transaction (a sequence is not rolled back), so a retry would show.
        await ExecuteAsSuperuserAsync(db,
            """
            create sequence publish_attempts;
            grant usage on sequence publish_attempts to accounts_app;
            create function fake_policy_duplicate() returns trigger language plpgsql security definer as $$ begin
              perform nextval('publish_attempts');
              raise exception 'duplicate on purpose' using errcode = 'unique_violation', constraint = 'insurance_policies_pkey'; end $$;
            create trigger fake_policy_duplicate before insert on insurance_policies for each row execute function fake_policy_duplicate();
            """);
        try
        {
            var e = await Assert.ThrowsAsync<InsuranceException>(() => insurance.PublishPolicyAsync("premium", ParsedTerms(), Op));
            Assert.Equal(InsuranceError.PolicyVersionConflict, e.Error);
            Assert.Contains("retry", e.Message);
            Assert.Equal(1L, await ScalarAsync<long>("select last_value from publish_attempts"));
            Assert.Equal(0L, await ScalarAsync<long>("select count(*) from insurance_policies where policy_id = 'premium'"));
            Assert.Empty(await AuditsAsync("insurance_policy_publish"));
        }
        finally
        {
            await ExecuteAsSuperuserAsync(db, "drop trigger fake_policy_duplicate on insurance_policies; drop function fake_policy_duplicate(); drop sequence publish_attempts");
        }
    }

    // ---- failure injection and locks ----

    [Fact]
    public async Task A_failure_writing_the_audit_rolls_back_the_publication_and_the_activation()
    {
        var account = await NewAccountAsync();
        await insurance.ActivateAsync(account, "standard", DaySeconds, Guid.CreateVersion7(), Op);
        var state = await StateAsync(account);
        await ExecuteAsSuperuserAsync(db,
            """
            create function refuse_insurance_audit() returns trigger language plpgsql as $$ begin raise exception 'audit failed on purpose'; end $$;
            create trigger refuse_insurance_audit before insert on operator_actions for each row
              when (new.action in ('insurance_activate', 'insurance_policy_publish')) execute function refuse_insurance_audit();
            """);
        var key = Guid.CreateVersion7();

        var activation = await Assert.ThrowsAsync<PostgresException>(() => insurance.ActivateAsync(account, "standard", DaySeconds, key, Op));
        var publication = await Assert.ThrowsAsync<PostgresException>(() => insurance.PublishPolicyAsync("standard", ParsedTerms(60), Op));

        Assert.Contains("audit failed on purpose", activation.MessageText);
        Assert.Contains("audit failed on purpose", publication.MessageText);
        await AssertUnchangedAsync(account, state);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from insurance_policies"));
        await ExecuteAsSuperuserAsync(db, "drop trigger refuse_insurance_audit on operator_actions; drop function refuse_insurance_audit()");
        Assert.False((await insurance.ActivateAsync(account, "standard", DaySeconds, key, Op)).Replayed); // the key was never used
        Assert.Equal(2, (await insurance.PublishPolicyAsync("standard", ParsedTerms(60), Op)).Version);
    }

    [Fact]
    public async Task An_activation_waiting_on_a_held_coverage_lock_times_out_busy_and_writes_nothing()
    {
        var account = await NewAccountAsync();
        await insurance.ActivateAsync(account, "standard", DaySeconds, Guid.CreateVersion7(), Op);
        var state = await StateAsync(account);
        var bounded = new InsuranceAdminStore(db.AsAppWith(Short));
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from insurance_coverages where account_id = @account for update", new { account }, holderTx);

        var clock = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<PostgresException>(() => bounded.ActivateAsync(account, "standard", DaySeconds, Guid.CreateVersion7(), Op).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the activation waited {clock.Elapsed}");
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
        Assert.Equal(DbFailureKind.Busy, DbFailure.Classify(e));

        // The CLI reports it as busy (exit 75), after its unlocked preview, and changes nothing.
        var env = db.EnvFor("app");
        env[DbTimeouts.LockTimeoutVar] = "300";
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await Cli.RunAsync(["admin", "insurance", "activate", "--account-base58", AccountId.Format(account), "--policy-id", "standard", "--duration-seconds", "60",
            "--idempotency-key", Guid.CreateVersion7().ToString(), "--operator", "ops", "--reason", "busy", "--yes"], env, output, error).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ExitCodes.Busy, code);
        Assert.Contains("nothing was changed", error.ToString());
        Assert.StartsWith("standard v1 until ", Regex.Match(output.ToString(), "(?m)^target.coverage=(.*)$").Groups[1].Value);
        Assert.DoesNotContain("correlationId=", output.ToString());
        await holderTx.RollbackAsync();

        await AssertUnchangedAsync(account, state);
    }
}
