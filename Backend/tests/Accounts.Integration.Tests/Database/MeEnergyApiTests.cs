using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Energy;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Energy;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// GET /v1/me's projected energy (Stage 6, Task 6; owner rulings E2, E3; plan Checkpoint 1 F1). The projection is
/// <see cref="EnergyMath.Materialize"/> in memory over one READ COMMITTED, read-only statement taken after the shared
/// policy lock: it never writes a wallet, an entry or an anchor, and an account without a wallet reads <c>energy: null</c>,
/// never a zero balance. Time is never mocked: wallets are reopened as the superuser under
/// <c>session_replication_role = replica</c> with a backdated anchor, consistently with their ledger, and every expected
/// time derives from the stored anchor. Stage 7 adds the active attempt and the insurance coverage, read in the same
/// statement. The 0000-0007 invariants hold after every test.
/// </summary>
[Trait("Requires", "Postgres")]
public class MeEnergyApiTests : IAsyncLifetime
{
    static readonly Dictionary<string, string?> DevOn = new() { ["BLACKGLASS_DEV_AUTH"] = "enabled" };
    const string InsertPolicy = "insert into energy_policies (cap, opening_balance, regen_interval, regen_amount, void_refund_pct) values ";
    TestDatabase db = null!;
    AccountAdminStore admin = null!;

    public async Task InitializeAsync() { db = await MigratedAsync(); admin = new AccountAdminStore(db.AsApp()); }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    // ---- fixtures ----

    /// <summary>A provisioned account (starter, full wallet) and a session for it.</summary>
    async Task<(long Account, string Token)> PlayerAsync(ApiServer api)
    {
        var issued = await admin.CreateAccountAsync(null, null, Op);
        var token = JsonDocument.Parse(await (await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = issued.AccountKey.Text })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;
        return (issued.AccountId, token);
    }

    /// <summary>Replaces the account's wallet and ledger with a wallet of <paramref name="balance"/> anchored at
    /// <paramref name="anchorSql"/> (by default <paramref name="ago"/> before the database clock) and its one open entry,
    /// as the superuser under replica (the append-only trigger would refuse the delete). The ledger stays consistent.</summary>
    async Task SetWalletAsync(long account, int balance, TimeSpan ago, long progress = 0, string anchorSql = "clock_timestamp() - @ago")
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
        await conn.ExecuteAsync(
            $"""
            delete from energy_ledger where account_id = @account;
            delete from energy_wallets where account_id = @account;
            insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@account, @balance, {anchorSql}, @progress, 1);
            insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@account, 'open', @balance, @balance, 'open');
            """, new { account, balance, ago, progress }, tx);
        await tx.CommitAsync();
    }

    async Task RemoveWalletAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
        await conn.ExecuteAsync("delete from energy_ledger where account_id = @account; delete from energy_wallets where account_id = @account", new { account }, tx);
        await tx.CommitAsync();
    }

    async Task AsReplicaAsync(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
        await conn.ExecuteAsync(sql, args, tx);
        await tx.CommitAsync();
    }

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    async Task<WalletState> WalletAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<WalletState>(
            "select balance as Balance, regen_anchor as RegenAnchor, regen_progress_us as ProgressMicros, policy_version as PolicyVersion from energy_wallets where account_id = @account",
            new { account });
    }

    /// <summary>The wallet row with its xmin (any rewrite, even to equal values, changes it), and the account's entry count.</summary>
    async Task<(string Row, long Entries)> FootprintAsync(long account) =>
        (await ScalarAsync<string>("select w.xmin::text || ' ' || w::text from energy_wallets w where account_id = @account", new { account }),
         await ScalarAsync<long>("select count(*) from energy_ledger where account_id = @account", new { account }));

    async Task<List<EnergyPolicy>> PoliciesAsync()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<EnergyPolicy>(
            """
            select version as Version, cap as Cap, opening_balance as OpeningBalance, regen_interval as RegenInterval, regen_amount as RegenAmount,
              void_refund_pct as VoidRefundPct, effective_at as EffectiveAt
            from energy_policies order by version
            """)).ToList();
    }

    static HttpRequestMessage Me(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    static async Task<string> MeTextAsync(ApiServer api, string token)
    {
        var response = await api.Http.SendAsync(Me(token));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");
        return text;
    }

    static JsonElement EnergyOf(string text) => JsonDocument.Parse(text).RootElement.GetProperty("energy").Clone();

    static DateTime? NextRegenAtOf(JsonElement energy)
    {
        var value = energy.GetProperty("nextRegenAt");
        if (value.ValueKind == JsonValueKind.Null) return null;
        var parsed = DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        return parsed;
    }

    static string Wire(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

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

    // ---- shape and projection ----

    [Fact]
    public async Task Me_returns_exactly_accountId_inventoryVersion_energy_activeAttemptId_and_insurance()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token) = await PlayerAsync(api);
        var text = await MeTextAsync(api, token);

        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(["accountId", "inventoryVersion", "energy", "activeAttemptId", "insurance"], root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["active", "policyId", "expiresAt"], root.GetProperty("insurance").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["balance", "cap", "nextRegenAt"], root.GetProperty("energy").EnumerateObject().Select(p => p.Name).ToArray());
        // A new account's wallet opens full (E1): at the cap, regeneration is paused, so there is no next unit.
        Assert.Equal($"{{\"accountId\":\"{AccountId.Format(account)}\",\"inventoryVersion\":1,\"energy\":{{\"balance\":50,\"cap\":50,\"nextRegenAt\":null}},\"activeAttemptId\":null,\"insurance\":{{\"active\":false,\"policyId\":null,\"expiresAt\":null}}}}", text);
    }

    [Fact]
    public async Task Me_projects_regeneration_without_writing()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token) = await PlayerAsync(api);
        await SetWalletAsync(account, 10, TimeSpan.FromMinutes(35));
        var before = await WalletAsync(account);
        var footprint = await FootprintAsync(account);

        for (var i = 0; i < 2; i++)
        {
            var energy = EnergyOf(await MeTextAsync(api, token));
            Assert.Equal((13, 50), (energy.GetProperty("balance").GetInt32(), energy.GetProperty("cap").GetInt32()));
            Assert.Equal(before.RegenAnchor.AddMinutes(40), NextRegenAtOf(energy)); // the fourth unit lands 40 minutes after the anchor
        }

        Assert.Equal(footprint, await FootprintAsync(account)); // the wallet row was not even rewritten; no entry was added
        Assert.Equal(before, await WalletAsync(account)); // the anchor did not move
    }

    [Fact]
    public async Task Me_projects_across_a_policy_transition_as_a_materialization_would()
    {
        // As EnergyLedgerWriterTests' non-retroactive transition: version 2 (5-minute interval, cap 60) takes effect 60
        // minutes after an anchor 2 hours old: 6 units under version 1, then 12 under version 2. Retroactive would be 24.
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token) = await PlayerAsync(api);
        await SetWalletAsync(account, 10, TimeSpan.FromHours(2));
        var before = await WalletAsync(account);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync(InsertPolicy + "(60, 50, interval '5 minutes', 1, 100)");
        await AsReplicaAsync(
            """
            update energy_policies set effective_at = '2000-01-01T00:00:00Z' where version = 1;
            update energy_policies set effective_at = @at where version = 2;
            """, new { at = before.RegenAnchor.AddMinutes(60) });
        var footprint = await FootprintAsync(account);

        var energy = EnergyOf(await MeTextAsync(api, token));
        Assert.Equal((28, 60), (energy.GetProperty("balance").GetInt32(), energy.GetProperty("cap").GetInt32()));
        var projected = NextRegenAtOf(energy);
        Assert.Equal(before.RegenAnchor.AddMinutes(125), projected);
        Assert.Equal(footprint, await FootprintAsync(account));

        // A materialization just after writes what the projection showed.
        await using var writer = await db.AsApp().OpenConnectionAsync();
        await using var tx = await writer.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var materialized = (await EnergyLedgerWriter.MaterializeLockedAsync(writer, tx, account, default))!;
        await tx.CommitAsync();
        Assert.Equal((28, 2, 60), (materialized.Wallet.Balance, materialized.Wallet.PolicyVersion, materialized.Current.Cap));
        Assert.Equal(EnergyMath.NextRegenAt(materialized.Wallet, materialized.Current, materialized.Now), projected);

        // The stored wallet is now on version 2, so the read takes the policies from version 2 (Materialize refuses a list
        // that does not start at the wallet's version): the same projection, and still nothing written.
        Assert.Equal(2, (await WalletAsync(account)).PolicyVersion);
        var onVersion2 = await FootprintAsync(account);
        var again = EnergyOf(await MeTextAsync(api, token));
        Assert.Equal((28, 60), (again.GetProperty("balance").GetInt32(), again.GetProperty("cap").GetInt32()));
        Assert.Equal(projected, NextRegenAtOf(again));
        Assert.Equal(onVersion2, await FootprintAsync(account));
    }

    [Fact]
    public async Task Me_with_the_clock_behind_the_anchor_shows_the_wallet_unchanged()
    {
        // An anchor an hour ahead of the database clock (a step back, or an anchor written ahead): no time is accounted, and
        // the next unit lands one interval minus the carried progress after the anchor, not after now.
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token) = await PlayerAsync(api);
        await SetWalletAsync(account, 10, TimeSpan.Zero, progress: 61_000_000, anchorSql: "clock_timestamp() + interval '1 hour'");
        var before = await WalletAsync(account);
        var footprint = await FootprintAsync(account);

        var energy = EnergyOf(await MeTextAsync(api, token));
        Assert.Equal((10, 50), (energy.GetProperty("balance").GetInt32(), energy.GetProperty("cap").GetInt32()));
        Assert.Equal(before.RegenAnchor.AddMinutes(10).AddSeconds(-61), NextRegenAtOf(energy));
        Assert.Equal(footprint, await FootprintAsync(account));
        Assert.Equal(before, await WalletAsync(account));
    }

    [Fact]
    public async Task Me_shows_a_balance_above_the_cap_with_no_next_regeneration()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token) = await PlayerAsync(api);
        await SetWalletAsync(account, 70, TimeSpan.FromMinutes(35), progress: 1000);
        var footprint = await FootprintAsync(account);

        var text = await MeTextAsync(api, token);
        Assert.Equal($"{{\"accountId\":\"{AccountId.Format(account)}\",\"inventoryVersion\":1,\"energy\":{{\"balance\":70,\"cap\":50,\"nextRegenAt\":null}},\"activeAttemptId\":null,\"insurance\":{{\"active\":false,\"policyId\":null,\"expiresAt\":null}}}}", text);
        Assert.Equal(footprint, await FootprintAsync(account)); // a materialization would reset the progress; the projection writes nothing
    }

    [Fact]
    public async Task Me_shows_null_energy_for_an_account_without_a_wallet_and_creates_none()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token) = await PlayerAsync(api);
        await RemoveWalletAsync(account); // as an account created before Stage 6 is, until `energy open`

        var text = await MeTextAsync(api, token);
        Assert.Equal($"{{\"accountId\":\"{AccountId.Format(account)}\",\"inventoryVersion\":1,\"energy\":null,\"activeAttemptId\":null,\"insurance\":{{\"active\":false,\"policyId\":null,\"expiresAt\":null}}}}", text);
        Assert.Equal(JsonValueKind.Null, EnergyOf(text).ValueKind);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from energy_wallets where account_id = @account", new { account }));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from energy_ledger where account_id = @account", new { account }));
    }

    [Fact]
    public async Task Me_nextRegenAt_is_now_plus_the_remaining_progress_in_UTC()
    {
        // An anchor with a known sub-second part (.123456) 35 minutes back and 61 s of carried progress: 3 units, then the
        // remaining progress is (10 min − (5 min + 61 s + the time since)), so the next unit lands at anchor + 40 min − 61 s.
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token) = await PlayerAsync(api);
        await SetWalletAsync(account, 10, TimeSpan.Zero, progress: 61_000_000,
            anchorSql: "date_trunc('second', clock_timestamp()) - interval '35 minutes' + interval '0.123456 seconds'");
        var before = await WalletAsync(account);
        Assert.Equal(1_234_560, before.RegenAnchor.Ticks % TimeSpan.TicksPerSecond);
        var expected = before.RegenAnchor.AddMinutes(40).AddSeconds(-61);

        var t0 = await ScalarAsync<DateTime>("select clock_timestamp()");
        var text = await MeTextAsync(api, token);
        var t1 = await ScalarAsync<DateTime>("select clock_timestamp()");

        // System.Text.Json writes a UTC-kind DateTime as ISO-8601 with 'Z', trimming trailing zero fraction digits.
        Assert.Contains($"\"energy\":{{\"balance\":13,\"cap\":50,\"nextRegenAt\":\"{Wire(expected)}\"}}", text);
        Assert.EndsWith(".123456Z", Wire(expected));
        var next = NextRegenAtOf(EnergyOf(text))!.Value;
        Assert.Equal(expected, next);
        // now + (interval − progress at now), with now read during the request: strictly after it began, at most one interval after it ended.
        Assert.True(next > t0, $"{next:O} should be after {t0:O}");
        Assert.True(next <= t1.AddMinutes(10), $"{next:O} should be at most one interval after {t1:O}");
    }

    // ---- consistency ----

    [Fact]
    public async Task Me_reads_inventory_and_energy_from_one_snapshot()
    {
        // A grant-like write bumps the inventory version (1 → 2) and grants 5 energy (50 → 55, with its dev_grant entry) in
        // one transaction. /v1/me must show (1, 50) or (2, 55). The write does not take the policy lock, so it can commit
        // while /v1/me waits on that lock: the held-lock pattern. This proves the read's snapshot is taken after the lock (a
        // snapshot taken at, or before, the lock statement would miss the write after the wait). That the inventory version
        // and the wallet come from one statement is guarded by EnergyReader's doc comment and code review, not by this test.
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token) = await PlayerAsync(api);

        static (long, int) Seen(string text)
        {
            var root = JsonDocument.Parse(text).RootElement;
            return (root.GetProperty("inventoryVersion").GetInt64(), root.GetProperty("energy").GetProperty("balance").GetInt32());
        }

        await using var granter = await db.AsSuperuser().OpenConnectionAsync();
        await using var grant = await granter.BeginTransactionAsync();
        await granter.ExecuteAsync(
            """
            update inventories set version = version + 1 where account_id = @account;
            update energy_wallets set balance = balance + 5 where account_id = @account;
            insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key)
              values (@account, 'dev_grant', 5, 55, 'dev_grant:' || gen_random_uuid());
            """, new { account }, grant);

        // In flight: not seen at all.
        Assert.Equal((1L, 50), Seen(await MeTextAsync(api, token)));

        // Committed while the read waits on the shared policy lock: seen whole.
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = EnergyPolicies.PublicationLockKey }, holderTx);
        var waiting = MeTextAsync(api, token);
        await WaitForLockWaitersAsync();
        Assert.False(waiting.IsCompleted);
        await grant.CommitAsync();
        await holderTx.CommitAsync();
        Assert.Equal((2L, 55), Seen(await waiting));
        Assert.Equal((2L, 55), Seen(await MeTextAsync(api, token)));
    }

    [Fact]
    public async Task Me_waits_for_an_in_flight_publication_and_projects_with_it()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token) = await PlayerAsync(api);
        await SetWalletAsync(account, 10, TimeSpan.FromMinutes(35));
        var before = await WalletAsync(account);
        var footprint = await FootprintAsync(account);

        await using var publisher = await db.AsSuperuser().OpenConnectionAsync();
        await using var publisherTx = await publisher.BeginTransactionAsync();
        await publisher.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = EnergyPolicies.PublicationLockKey }, publisherTx);
        var (version, effectiveAt) = await publisher.QuerySingleAsync<(int, DateTime)>(
            InsertPolicy + "(80, 50, interval '5 minutes', 1, 100) returning version, effective_at", transaction: publisherTx);
        var waiting = MeTextAsync(api, token);
        await WaitForLockWaitersAsync();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event = 'advisory'"));
        await publisherTx.CommitAsync();

        var energy = EnergyOf(await waiting);
        Assert.Equal(2, version);
        // 35-odd minutes under version 1 (3 units), the progress halved at effective_at, then the cap and interval of version 2.
        Assert.Equal((13, 80), (energy.GetProperty("balance").GetInt32(), energy.GetProperty("cap").GetInt32()));
        var policies = await PoliciesAsync();
        var atTransition = EnergyMath.Materialize(before, policies, effectiveAt).After;
        Assert.Equal(2, atTransition.PolicyVersion);
        Assert.Equal(EnergyMath.NextRegenAt(atTransition, policies[1], effectiveAt), NextRegenAtOf(energy));
        Assert.Equal(footprint, await FootprintAsync(account));
    }

    [Fact]
    public async Task GetMe_runs_on_read_only_connections_and_writes_nothing()
    {
        var issued = await admin.CreateAccountAsync(null, null, Op);
        await SetWalletAsync(issued.AccountId, 10, TimeSpan.FromMinutes(35));
        var footprint = await FootprintAsync(issued.AccountId);

        // The read-only source's transactions default to read only (25006 on any write); the store must succeed on it.
        var me = await new PlayerReadStore(db.AsAppReadOnly(), new ParticipationLock()).GetMeAsync(issued.AccountId, default);
        Assert.Equal(1L, me.InventoryVersion);
        Assert.Equal((13, 50), (me.Energy!.Balance, me.Energy.Cap));
        Assert.Equal(DateTimeKind.Utc, me.Energy.NextRegenAt!.Value.Kind);
        Assert.Equal(footprint, await FootprintAsync(issued.AccountId));

        var nobody = await new PlayerReadStore(db.AsApp(), new ParticipationLock()).GetMeAsync(424242, default);
        Assert.Equal(new MeView(null, null, null, InsuranceView.None), nobody);
    }

    [Fact]
    public async Task GetMe_sets_its_transaction_read_only_on_a_read_write_source()
    {
        // A row-level security policy lets the app role see energy policies only inside a read-only transaction. On the
        // ordinary (read-write) app source, a plain transaction then sees none, while GetMeAsync, whose first statement is
        // `set transaction read only`, sees them and projects. Without that statement it would find its wallet's version
        // missing and throw.
        var issued = await admin.CreateAccountAsync(null, null, Op);
        await SetWalletAsync(issued.AccountId, 10, TimeSpan.FromMinutes(35));
        await ExecuteAsSuperuserAsync(db,
            """
            alter table energy_policies enable row level security;
            alter table energy_policies force row level security;
            create policy bg_test_read_only_only on energy_policies for select to accounts_app using (current_setting('transaction_read_only') = 'on');
            """);

        await using (var conn = await db.AsApp().OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted))
        {
            Assert.Equal("off", await conn.ExecuteScalarAsync<string>("show transaction_read_only", transaction: tx));
            Assert.Equal(0L, await conn.ExecuteScalarAsync<long>("select count(*) from energy_policies", transaction: tx));
        }

        var me = await new PlayerReadStore(db.AsApp(), new ParticipationLock()).GetMeAsync(issued.AccountId, default);
        Assert.Equal((13, 50), (me.Energy!.Balance, me.Energy.Cap));

        await ExecuteAsSuperuserAsync(db,
            "drop policy bg_test_read_only_only on energy_policies; alter table energy_policies no force row level security; alter table energy_policies disable row level security");
    }

    // ---- the active attempt and insurance (Stage 7) ----

    /// <summary>An admitted solo participation and an insurance coverage written together, in one superuser transaction that is
    /// left open for the caller to commit (the attempt fixture's rows and a coverage with its activation).</summary>
    async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx, Guid Attempt, DateTime ExpiresAt)> BeginAdmissionWithCoverageAsync(long account)
    {
        var conn = await db.AsSuperuser().OpenConnectionAsync();
        var tx = await conn.BeginTransactionAsync();
        var attempt = Guid.CreateVersion7();
        var activation = Guid.CreateVersion7();
        var expiresAt = await conn.ExecuteScalarAsync<DateTime>("select date_trunc('second', clock_timestamp()) + interval '1 day'", transaction: tx);
        await conn.ExecuteAsync(
            """
            insert into mission_attempts (attempt_id, created_by, template_id, template_version, seed, retention_salt, assign_deadline)
            values (@attempt, @account, 'procedural-standard', 1, 12345, @salt, clock_timestamp() + interval '60 seconds');
            insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
            values (@attempt, @account, 'procedural-standard', 1, '{"operatives":[]}'::jsonb, 1, 0);
            insert into energy_holds (attempt_id, account_id, amount) values (@attempt, @account, 0);
            insert into insurance_activations (activation_id, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source, idempotency_key)
            values (@activation, @account, 'standard', 1, interval '2 days', null, @expiresAt, 'dev_grant', gen_random_uuid());
            insert into insurance_coverages (account_id, policy_id, policy_version, activation_id, starts_at, expires_at)
            values (@account, 'standard', 1, @activation, @expiresAt - interval '2 days', @expiresAt);
            """, new { attempt, account, activation, expiresAt, salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32) }, tx);
        return (conn, tx, attempt, expiresAt);
    }

    [Fact]
    public async Task Me_shows_the_active_attempt_and_insurance()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token) = await PlayerAsync(api);
        var id = AccountId.Format(account);
        const string Energy = "\"energy\":{\"balance\":50,\"cap\":50,\"nextRegenAt\":null}";

        // None yet: no attempt, no coverage (both fields present, both empty).
        Assert.Equal($"{{\"accountId\":\"{id}\",\"inventoryVersion\":1,{Energy},\"activeAttemptId\":null,\"insurance\":{{\"active\":false,\"policyId\":null,\"expiresAt\":null}}}}",
            await MeTextAsync(api, token));

        // An admission and a coverage that commit while /v1/me waits on the shared policy lock are seen whole: the attempt, the
        // coverage, the inventory version and the energy all come from the one statement taken after the lock.
        var (conn, tx, attempt, expiresAt) = await BeginAdmissionWithCoverageAsync(account);
        await using (conn)
        await using (tx)
        {
            Assert.EndsWith("\"activeAttemptId\":null,\"insurance\":{\"active\":false,\"policyId\":null,\"expiresAt\":null}}", await MeTextAsync(api, token)); // in flight: unseen
            await using var holder = await db.AsSuperuser().OpenConnectionAsync();
            await using var holderTx = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = EnergyPolicies.PublicationLockKey }, holderTx);
            var waiting = MeTextAsync(api, token);
            await WaitForLockWaitersAsync();
            Assert.False(waiting.IsCompleted);
            await tx.CommitAsync();
            await holderTx.CommitAsync();
            Assert.Equal($"{{\"accountId\":\"{id}\",\"inventoryVersion\":1,{Energy},\"activeAttemptId\":\"{attempt:D}\",\"insurance\":{{\"active\":true,\"policyId\":\"standard\",\"expiresAt\":\"{expiresAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}\"}}}}", await waiting);
        }

        // Released: no active attempt any more; the coverage is still in force.
        await AttemptFixtures.ReleaseAsync(db, attempt);
        var released = JsonDocument.Parse(await MeTextAsync(api, token)).RootElement;
        Assert.Equal(JsonValueKind.Null, released.GetProperty("activeAttemptId").ValueKind);
        Assert.True(released.GetProperty("insurance").GetProperty("active").GetBoolean());

        // Expired (moved into the past as the superuser, under replica: the coverage trigger forbids it otherwise): not active,
        // but its policy and expiry are still shown.
        await AsReplicaAsync(
            "update insurance_coverages set starts_at = clock_timestamp() - interval '2 days', expires_at = clock_timestamp() - interval '1 day' where account_id = @account",
            new { account });
        var expiredAt = await ScalarAsync<DateTime>("select expires_at from insurance_coverages where account_id = @account", new { account });
        var insurance = JsonDocument.Parse(await MeTextAsync(api, token)).RootElement.GetProperty("insurance");
        Assert.False(insurance.GetProperty("active").GetBoolean());
        Assert.Equal("standard", insurance.GetProperty("policyId").GetString());
        Assert.Equal(expiredAt, DateTime.Parse(insurance.GetProperty("expiresAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

        // Another account's attempt and coverage are never shown.
        var (_, otherToken) = await PlayerAsync(api);
        Assert.EndsWith("\"activeAttemptId\":null,\"insurance\":{\"active\":false,\"policyId\":null,\"expiresAt\":null}}", await MeTextAsync(api, otherToken));
    }

    // ---- headers and failures, as before ----

    [Fact]
    public async Task Me_is_no_store_and_rate_limited_as_before()
    {
        await using var api = await ApiServer.StartAsync(db, new()
        {
            ["BLACKGLASS_DEV_AUTH"] = "enabled", ["BLACKGLASS_RATE_REQUESTS_PER_MINUTE"] = "2",
        });
        var (_, chatty) = await PlayerAsync(api);
        var (_, quiet) = await PlayerAsync(api);
        for (var i = 0; i < 2; i++)
        {
            var ok = await api.Http.SendAsync(Me(chatty));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.True(ok.Headers.CacheControl!.NoStore);
        }
        var limited = await api.Http.SendAsync(Me(chatty));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.GetValues("Retry-After").Single());
        Assert.Equal(HttpStatusCode.OK, (await api.Http.SendAsync(Me(quiet))).StatusCode);
    }

    [Fact]
    public async Task Me_maps_database_failures_as_before()
    {
        await using var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_DEV_AUTH"] = "enabled", [DbTimeouts.LockTimeoutVar] = "300" });
        var (account, token) = await PlayerAsync(api);
        var footprint = await FootprintAsync(account);

        // Busy: a publication lock held past the lock timeout is 503 with Retry-After, empty, nothing written.
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        await using (var holderTx = await holder.BeginTransactionAsync())
        {
            await holder.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = EnergyPolicies.PublicationLockKey }, holderTx);
            var busy = await api.Http.SendAsync(Me(token));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.Equal("1", busy.Headers.GetValues("Retry-After").Single());
            Assert.Equal("", await busy.Content.ReadAsStringAsync());
            await holderTx.RollbackAsync();
        }
        Assert.Equal(footprint, await FootprintAsync(account));
        Assert.Equal(HttpStatusCode.OK, (await api.Http.SendAsync(Me(token))).StatusCode);

        // Unexpected: any other SQL error is 500, never 503, with no body.
        await ExecuteAsSuperuserAsync(db, "revoke select on energy_policies from accounts_app");
        var broken = await api.Http.SendAsync(Me(token));
        Assert.Equal(HttpStatusCode.InternalServerError, broken.StatusCode);
        Assert.Null(broken.Headers.RetryAfter);
        Assert.Equal("", await broken.Content.ReadAsStringAsync());
    }
}
