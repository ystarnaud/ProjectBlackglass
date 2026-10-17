using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Energy;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Insurance;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Energy;
using Blackglass.Accounts.Persistence.Insurance;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Admission (Task 6; spec §7.3 as amended by owner rulings S7-D1 to S7-D9 and Checkpoint 1's F1 to F3) against a real
/// PostgreSQL 17, through <see cref="AdmissionStore"/>. The lock order is accounts FOR KEY SHARE, inventories FOR UPDATE,
/// the wallet FOR UPDATE and the shared energy policy lock, then the clock and inserts of new rows only. Races are built
/// with the held-lock pattern: a holder takes the contested lock, the racers are seen waiting in pg_stat_activity, then the
/// holder lets go. Every admitting test has a fresh development host fixture (the registered and heartbeated
/// <c>dev_fixture</c> kind, counted because the store's health check has fixtures enabled). The 0000-0007 invariants hold
/// after every test.
/// </summary>
[Trait("Requires", "Postgres")]
public class AdmissionStoreTests : IAsyncLifetime
{
    const string Template = "procedural-standard";
    const long GateKey = 7_007;
    TestDatabase db = null!;
    AccountAdminStore accounts = null!;
    EnergyAdminStore energy = null!;
    AdmissionStore store = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        accounts = new AccountAdminStore(db.AsApp());
        energy = new EnergyAdminStore(db.AsApp(), 1000);
        var hosts = new DevHosts(db.AsApp());
        await hosts.RegisterAsync("dev-fixture-1", 10, Op);
        await hosts.HeartbeatAsync("dev-fixture-1", Op);
        store = Store(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    /// <summary>Fixtures count (the test serve's BLACKGLASS_DEV_HOSTS=enabled) and a long lease, so a slow run never goes stale.</summary>
    static AdmissionStore Store(NpgsqlDataSource ds, IAttemptSecrets? secrets = null) =>
        new(ds, new ServiceHealth(devHostsEnabled: true, leaseSeconds: 600), secrets ?? new CryptoAttemptSecrets(), GameCatalogue.Embedded, 60);

    static DeployRequest Deploy(params long[] operatives) => new(Template, operatives);

    static DeployRequest DeployOf(string template, params long[] operatives) => new(template, operatives);

    /// <summary>A provisioned account (wallet 50 under policy v1, three starter operatives), with its operatives ascending.</summary>
    async Task<(long Account, long[] Operatives)> PlayerAsync()
    {
        var account = (await accounts.CreateAccountAsync(null, null, Op)).AccountId;
        return (account, await OperativesAsync(account));
    }

    async Task<long[]> OperativesAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<long>("select operative_id from operatives where account_id = @account order by operative_id", new { account })).ToArray();
    }

    /// <summary>A provisioned account without an energy wallet (as one created before Stage 6 is): the starter only.</summary>
    async Task<(long Account, long[] Operatives)> WalletlessPlayerAsync()
    {
        long account;
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id", transaction: tx);
            await StarterProvisioner.ProvisionAsync(conn, tx, account, GameCatalogue.Embedded, default);
            await tx.CommitAsync();
        }
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from energy_wallets where account_id = @account", new { account }));
        return (account, await OperativesAsync(account));
    }

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    /// <summary>One row, as a tuple or record (a scalar read would return only its first column).</summary>
    async Task<T> RowAsync<T>(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<T>(sql, args);
    }

    static Guid AttemptOf(Admission admission) => Guid.Parse(JsonDocument.Parse(admission.Body).RootElement.GetProperty("attemptId").GetString()!);

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    static async Task<AdmissionException> RefusedAsync(Task task) => await Assert.ThrowsAsync<AdmissionException>(() => task);

    /// <summary>What a call ended as: admitted, replayed, or its refusal.</summary>
    static async Task<string> OutcomeAsync(Task<Admission> call)
    {
        try { return (await call).Replayed ? "replayed" : "admitted"; }
        catch (AdmissionException e) { return e.Error.ToString(); }
    }

    /// <summary>
    /// Everything an admission could write, database-wide: the attempt-table counts, the ledger count, and every wallet,
    /// inventories and coverage row with its xmin. Equal snapshots mean nothing was written (a row lock leaves xmin alone).
    /// </summary>
    Task<string> StateAsync() => ScalarAsync<string>(
        """
        select concat_ws(' | ',
          (select count(*) from mission_attempts), (select count(*) from attempt_participants), (select count(*) from energy_holds),
          (select count(*) from attempt_insurance), (select count(*) from deploy_requests), (select count(*) from energy_ledger),
          (select string_agg(account_id || ':' || xmin::text || ':' || balance || ':' || policy_version, ',' order by account_id) from energy_wallets),
          (select string_agg(account_id || ':' || xmin::text || ':' || version, ',' order by account_id) from inventories),
          (select string_agg(account_id || ':' || xmin::text, ',' order by account_id) from insurance_coverages))
        """);

    sealed record Entry(string Kind, int Delta, int BalanceAfter);

    async Task<List<Entry>> EntriesAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<Entry>(
            "select kind as Kind, delta as Delta, balance_after as BalanceAfter from energy_ledger where account_id = @account order by entry_id", new { account })).ToList();
    }

    Task<int> BalanceAsync(long account) => ScalarAsync<int>("select balance from energy_wallets where account_id = @account", new { account });

    Task<long> ActiveParticipationsAsync(long account) => ScalarAsync<long>(
        "select count(*) from attempt_participants where account_id = @account and state = 'admitted'", new { account });

    /// <summary>Returns once at least <paramref name="count"/> backends of this database wait on a lock (polls every 20 ms).</summary>
    async Task WaitForLockWaitersAsync(int count)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await su.ExecuteScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Fewer than {count} backends waited on a lock.");
            await Task.Delay(20);
        }
    }

    NpgsqlConnectionStringBuilder AppConnection(int maxPoolSize) => new()
    {
        Host = "127.0.0.1", Port = int.Parse(db.EnvFor("app")[DbSettings.PortVar]!, System.Globalization.CultureInfo.InvariantCulture), Database = db.Name,
        Username = "accounts_app", Password = db.Password("app"), ApplicationName = "blackglass-tests", MaxPoolSize = maxPoolSize,
    };

    /// <summary>The app role with a pool wide enough for every racer, and no lock_timeout (a real cycle surfaces as 40P01).</summary>
    NpgsqlDataSource Wide(int connections) => NpgsqlDataSource.Create(AppConnection(connections));

    /// <summary>
    /// The app role on connections whose unqualified clock_timestamp() reads <paramref name="at"/> (a test-only function in
    /// schema bg_test_clock, before pg_catalog in their search_path; the technique of InsuranceAdminStoreTests). The store's
    /// SQL is unchanged, so admission's now can equal a coverage boundary to the microsecond. Dropped before the invariants.
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

    /// <summary>A test-only BEFORE INSERT trigger on <paramref name="table"/> that waits on advisory lock <see cref="GateKey"/>:
    /// a holder of that lock stops the admission exactly at that insert, with every earlier lock and write in place.</summary>
    Task InstallGateAsync(string table) => ExecuteAsSuperuserAsync(db,
        "create function bg_test_gate() returns trigger language plpgsql as $$ begin perform pg_advisory_xact_lock(7007); return new; end $$; " +
        "create trigger bg_test_gate before insert on " + table + " for each row execute function bg_test_gate();");

    Task RemoveGateAsync(string table) => ExecuteAsSuperuserAsync(db, "drop trigger bg_test_gate on " + table + "; drop function bg_test_gate();");

    /// <summary>A coverage written directly with the activation that set it (it may lie in the past or end soon).</summary>
    async Task<Guid> SeedCoverageAsync(long account, DateTime startsAt, DateTime expiresAt, int version = 1)
    {
        var activation = Guid.CreateVersion7();
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(
            """
            insert into insurance_activations (activation_id, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source, idempotency_key)
            values (@activation, @account, 'standard', @version, @duration, null, @expiresAt, 'dev_grant', gen_random_uuid())
            """, new { activation, account, version, duration = expiresAt - startsAt, expiresAt }, tx);
        await conn.ExecuteAsync(
            "insert into insurance_coverages (account_id, policy_id, policy_version, activation_id, starts_at, expires_at) values (@account, 'standard', @version, @activation, @startsAt, @expiresAt)",
            new { account, version, activation, startsAt, expiresAt }, tx);
        await tx.CommitAsync();
        return activation;
    }

    Task<DateTime> DbNowAsync() => ScalarAsync<DateTime>("select clock_timestamp()");

    async Task WaitUntilDatabasePassesAsync(DateTime moment)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await ScalarAsync<bool>("select clock_timestamp() >= @moment", new { moment }))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The database clock never passed the moment.");
            await Task.Delay(100);
        }
    }

    /// <summary>The attempt's snapshot row as text with its xmin: equal values mean the row was never rewritten.</summary>
    Task<string?> InsuranceRowAsync(Guid attempt) => ScalarAsync<string?>(
        "select xmin::text || ' ' || row_to_json(i)::text from attempt_insurance i where attempt_id = @attempt", new { attempt });

    static string Terms(int refundPercent) => $$$"""
        {"covers":["defeat","abandoned","disconnected","expired"],
         "consumableRefund":{"percent":{{{refundPercent}}},"rounding":"floor","destination":"stash"},
         "lootRetention":{"maxEntries":2,"maxUnitsPerEntry":3,"eligibleCategories":["Consumable","Armor","Utility","Weapon"],
           "rank":["categoryPriority","saltedHash"],"categoryPriority":["Weapon","Armor","Utility","Consumable"]},
         "progressXp":{"percent":40,"basis":"requiredObjectivesCompleted"}}
        """;

    Task PublishTemplateAsync(string id, int cost, int min, int max) => new MissionTemplates(db.AsApp()).PublishAsync(id, cost, min, max, Op);

    // ---- concurrency and idempotency ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Fifty_parallel_deploys_for_one_account_admit_once()
    {
        const int Callers = 50;
        var (account, operatives) = await PlayerAsync();
        await using var wide = Wide(Callers + 2);
        var racing = Store(wide);

        List<Task<string>> calls;
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var held = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account }, held);
            calls = Enumerable.Range(0, Callers)
                .Select(_ => Task.Run(() => OutcomeAsync(racing.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0]))))).ToList();
            await WaitForLockWaitersAsync(Callers); // every caller queued on the inventories row, past its key lookup and health check
            await held.CommitAsync();
        }
        var outcomes = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(120));

        Assert.Equal(1, outcomes.Count(o => o == "admitted"));
        Assert.Equal(Callers - 1, outcomes.Count(o => o == nameof(AdmissionError.AttemptActive)));
        Assert.Equal((1L, 1L, 1L, 1L), (await ScalarAsync<long>("select count(*) from mission_attempts"), await ScalarAsync<long>("select count(*) from attempt_participants"),
            await ScalarAsync<long>("select count(*) from energy_holds"), await ScalarAsync<long>("select count(*) from deploy_requests")));
        Assert.Equal([new Entry("open", 50, 50), new Entry("reserve", -10, 40)], await EntriesAsync(account)); // deducted once
        Assert.Equal(40, await BalanceAsync(account));
    }

    [Fact]
    public async Task A_reused_key_with_a_different_body_writes_nothing()
    {
        // Sequential, both orders of the two bodies: the first admits, the second is IdempotencyKeyReused with nothing written.
        foreach (var (first, second) in new[] { (0, 1), (1, 0) })
        {
            var (account, operatives) = await PlayerAsync();
            var key = Guid.CreateVersion7();
            var admitted = await store.AdmitAsync(account, key, Deploy(operatives[first]));
            Assert.False(admitted.Replayed);
            var before = await StateAsync();

            Assert.Equal(AdmissionError.IdempotencyKeyReused, (await RefusedAsync(store.AdmitAsync(account, key, Deploy(operatives[second])))).Error);
            // The same ids in another order are another request too (S7-D9).
            Assert.Equal(AdmissionError.IdempotencyKeyReused, (await RefusedAsync(store.AdmitAsync(account, key, Deploy(operatives[second], operatives[first])))).Error);
            Assert.Equal(before, await StateAsync());
            Assert.Equal(admitted.Body, (await store.AdmitAsync(account, key, Deploy(operatives[first]))).Body); // the recorded body still replays
        }

        // Concurrent first use: both bodies queue on the inventories row; the first admits, and the second, re-reading the key
        // under that lock (Checkpoint 1 F2), is IdempotencyKeyReused, never AttemptActive.
        var (racer, ops) = await PlayerAsync();
        var shared = Guid.CreateVersion7();
        Task<string> winner, loser;
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var held = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select 1 from inventories where account_id = @racer for update", new { racer }, held);
            winner = Task.Run(() => OutcomeAsync(store.AdmitAsync(racer, shared, Deploy(ops[0]))));
            await WaitForLockWaitersAsync(1);
            loser = Task.Run(() => OutcomeAsync(store.AdmitAsync(racer, shared, Deploy(ops[1]))));
            await WaitForLockWaitersAsync(2);
            await held.CommitAsync();
        }
        Assert.Equal(("admitted", nameof(AdmissionError.IdempotencyKeyReused)), (await winner, await loser));
        Assert.Equal(Deploy(ops[0]).RequestHash(), await ScalarAsync<byte[]>(
            "select request_hash from deploy_requests where account_id = @racer and idempotency_key = @shared", new { racer, shared }));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from energy_ledger where account_id = @racer and kind = 'reserve'", new { racer }));
    }

    [Fact]
    public async Task Admission_drains_with_a_grant_and_a_queued_publication_without_deadlock()
    {
        // The foreign-key key-share hazard (Review Focus 3), built deterministically. Admission A holds the account's key
        // share, its inventories and wallet rows and the shared policy lock, and is stopped at its first insert (the attempt
        // row, whose created_by foreign-key check needs the account row's key share). An operator grant G on the same account
        // queues for the account row FOR UPDATE; a publication P queues for the exclusive policy lock behind A. Then A goes on.
        // With the key share taken first, A's foreign-key check is already covered and everything drains. Without it, G would
        // hold the account row and wait for A's wallet while A's check waited for G: a cycle (40P01). The pool has no
        // lock_timeout, so a cycle surfaces as 40P01, and a hang as the timeout below.
        var (account, operatives) = await PlayerAsync();
        await using var wide = Wide(6);
        var racingEnergy = new EnergyAdminStore(wide, 1000);
        var racing = Store(wide);
        const string AdvisoryWaiters = "select count(*) from pg_stat_activity where datname = current_database() and wait_event = 'advisory'";

        await InstallGateAsync("mission_attempts");
        Task<Admission> a;
        Task<GrantResult> g;
        Task<EnergyPolicy> p;
        await using (var gate = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var gateTx = await gate.BeginTransactionAsync();
            await gate.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = GateKey }, gateTx);

            a = Task.Run(() => racing.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0])));
            await WaitForLockWaitersAsync(1);
            Assert.Equal(1L, await ScalarAsync<long>(AdvisoryWaiters)); // A at the gate, past every lock

            g = Task.Run(() => racingEnergy.GrantAsync(account, 5, Guid.CreateVersion7(), Op));
            await WaitForLockWaitersAsync(2);
            p = Task.Run(() => racingEnergy.PublishPolicyAsync(60, 60, TimeSpan.FromMinutes(10), 1, 100, Op));
            await WaitForLockWaitersAsync(3);
            Assert.Equal(2L, await ScalarAsync<long>(AdvisoryWaiters)); // A at the gate, P on the policy lock (G waits on a row)
            Assert.False(a.IsCompleted || g.IsCompleted || p.IsCompleted);

            await gateTx.CommitAsync();
            await Task.WhenAll(a, g, p).WaitAsync(TimeSpan.FromSeconds(60)); // no 40P01, no other failure, no hang
        }
        await RemoveGateAsync("mission_attempts");

        Assert.False((await a).Replayed);
        Assert.Equal(45, (await g).BalanceAfter); // the grant waited for the admission: 50 - 10 + 5
        Assert.Equal(2, (await p).Version);
        Assert.Equal(["open", "reserve", "dev_grant"], (await EntriesAsync(account)).Select(e => e.Kind).ToArray());
        Assert.Equal(45, await BalanceAsync(account));
    }

    [Fact]
    public async Task Admission_racing_an_inventory_edit_serializes_and_the_loser_is_refused_cleanly()
    {
        var edits = new InventoryEditStore(db.AsApp(), new ParticipationLock());
        foreach (var admissionFirst in new[] { true, false })
        {
            var (account, operatives) = await PlayerAsync();
            var vest = await ScalarAsync<long>("select item_id from item_instances where account_id = @account and definition_id = 'item.light-vest' and operative_id is null", new { account });
            InventoryOperation[] equip = [new EquipFromStash(operatives[0], vest)];

            Task<string> admission;
            Task<(long Version, InventoryView Inventory)> edit;
            await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
            {
                await using var held = await holder.BeginTransactionAsync();
                await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account }, held);
                if (admissionFirst)
                {
                    admission = Task.Run(() => OutcomeAsync(store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0]))));
                    await WaitForLockWaitersAsync(1);
                    edit = Task.Run(() => edits.ApplyAsync(account, 1, equip));
                }
                else
                {
                    edit = Task.Run(() => edits.ApplyAsync(account, 1, equip));
                    await WaitForLockWaitersAsync(1);
                    admission = Task.Run(() => OutcomeAsync(store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0]))));
                }
                await WaitForLockWaitersAsync(2);
                await held.CommitAsync();
            }

            Assert.Equal("admitted", await admission);
            var snapshot = Json(await ScalarAsync<string>("select loadout_snapshot::text from attempt_participants where account_id = @account", new { account }));
            var armor = snapshot.GetProperty("operatives")[0].GetProperty("equipment").GetProperty("armor");
            if (admissionFirst)
            {
                // The edit queued behind the admission and saw its participation: refused, with nothing written.
                var refused = await Assert.ThrowsAsync<PlayerException>(() => edit);
                Assert.Equal(PlayerError.InventoryLocked, refused.Error);
                Assert.Equal(1L, await ScalarAsync<long>("select version from inventories where account_id = @account", new { account }));
                Assert.True(await ScalarAsync<bool>("select operative_id is null from item_instances where item_id = @vest", new { vest }));
                Assert.Equal(1L, snapshot.GetProperty("inventoryVersion").GetInt64());
                Assert.Equal(JsonValueKind.Null, armor.ValueKind);
            }
            else
            {
                // The admission queued behind the edit and snapshotted the edited inventory at its new version.
                Assert.Equal(2L, (await edit).Version);
                Assert.Equal(2L, snapshot.GetProperty("inventoryVersion").GetInt64());
                Assert.Equal(2L, await ScalarAsync<long>("select inventory_version from attempt_participants where account_id = @account", new { account }));
                Assert.Equal(Base58Id.Format(vest), armor.GetString());
            }
        }
    }

    [Fact]
    public async Task Admission_racing_a_pick_serializes()
    {
        var picks = new PickStore(db.AsApp(), new ParticipationLock());
        foreach (var admissionFirst in new[] { true, false })
        {
            var (account, operatives) = await PlayerAsync();
            await ExecuteAsSuperuserAsync(db, "update operatives set experience = 100 where operative_id = @o", new { o = operatives[0] }); // one pick pending

            Task<string> admission;
            Task<(long Version, OperativeView Operative)> pick;
            await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
            {
                await using var held = await holder.BeginTransactionAsync();
                await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account }, held);
                if (admissionFirst)
                {
                    admission = Task.Run(() => OutcomeAsync(store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0]))));
                    await WaitForLockWaitersAsync(1);
                    pick = Task.Run(() => picks.PickAsync(account, operatives[0], "combat", 1));
                }
                else
                {
                    pick = Task.Run(() => picks.PickAsync(account, operatives[0], "combat", 1));
                    await WaitForLockWaitersAsync(1);
                    admission = Task.Run(() => OutcomeAsync(store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0]))));
                }
                await WaitForLockWaitersAsync(2);
                await held.CommitAsync();
            }

            Assert.Equal("admitted", await admission);
            var slot = Json(await ScalarAsync<string>("select loadout_snapshot::text from attempt_participants where account_id = @account", new { account }))
                .GetProperty("operatives")[0];
            if (admissionFirst)
            {
                Assert.Equal(PlayerError.InventoryLocked, (await Assert.ThrowsAsync<PlayerException>(() => pick)).Error);
                Assert.Equal(0L, await ScalarAsync<long>("select count(*) from operative_choices where operative_id = @o", new { o = operatives[0] }));
                Assert.Empty(slot.GetProperty("choices").EnumerateArray());
            }
            else
            {
                Assert.Equal(2L, (await pick).Version);
                Assert.Equal(["combat"], slot.GetProperty("choices").EnumerateArray().Select(c => c.GetString()!).ToArray());
                Assert.Equal(2L, await ScalarAsync<long>("select inventory_version from attempt_participants where account_id = @account", new { account }));
            }
        }
    }

    [Fact]
    public async Task Admission_racing_a_grant_serializes_without_deadlock()
    {
        // Both take the account row first (admission FOR KEY SHARE, the grant FOR UPDATE), so they serialize there in queue order.
        foreach (var admissionFirst in new[] { true, false })
        {
            var (account, operatives) = await PlayerAsync();
            Task<Admission> admission;
            Task<GrantResult> grant;
            await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
            {
                await using var held = await holder.BeginTransactionAsync();
                await holder.ExecuteAsync("select 1 from accounts where account_id = @account for update", new { account }, held);
                if (admissionFirst)
                {
                    admission = Task.Run(() => store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0])));
                    await WaitForLockWaitersAsync(1);
                    grant = Task.Run(() => energy.GrantAsync(account, 5, Guid.CreateVersion7(), Op));
                }
                else
                {
                    grant = Task.Run(() => energy.GrantAsync(account, 5, Guid.CreateVersion7(), Op));
                    await WaitForLockWaitersAsync(1);
                    admission = Task.Run(() => store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0])));
                }
                await WaitForLockWaitersAsync(2);
                await held.CommitAsync();
            }
            await Task.WhenAll(admission, grant).WaitAsync(TimeSpan.FromSeconds(60)); // no 40P01

            var energyBody = Json((await admission).Body).GetProperty("energy");
            if (admissionFirst)
            {
                Assert.Equal([new Entry("open", 50, 50), new Entry("reserve", -10, 40), new Entry("dev_grant", 5, 45)], await EntriesAsync(account));
                Assert.Equal(40, energyBody.GetProperty("balanceAfter").GetInt32());
            }
            else
            {
                Assert.Equal([new Entry("open", 50, 50), new Entry("dev_grant", 5, 55), new Entry("reserve", -10, 45)], await EntriesAsync(account));
                Assert.Equal(45, energyBody.GetProperty("balanceAfter").GetInt32());
            }
            Assert.Equal(45, await BalanceAsync(account));
        }
    }

    [Fact]
    public Task Admission_racing_a_player_cancel_of_the_previous_attempt() => RaceTheReleaseOfThePreviousAttemptAsync("player_cancel");

    [Fact]
    public Task Admission_racing_assignment_expiry_of_the_previous_attempt() => RaceTheReleaseOfThePreviousAttemptAsync("no_capacity");

    /// <summary>
    /// The previous attempt is released (a pre-start player cancel or the assignment deadline; Task 7 replaces the direct SQL
    /// of <see cref="AttemptFixtures.BeginReleaseAsync"/> with the real paths) while a new admission arrives:
    /// <list type="number">
    /// <item>while the release has changed its rows but not committed or taken the inventories lock, the admission sees the
    /// committed active participation and answers AttemptActive at once, writing nothing;</item>
    /// <item>once the release holds the inventories lock, the admission queues on it and admits after the release commits.</item>
    /// </list>
    /// </summary>
    async Task RaceTheReleaseOfThePreviousAttemptAsync(string reason)
    {
        var (account, operatives) = await PlayerAsync();
        var previous = await AttemptFixtures.AdmitAsync(db, account);
        var key = Guid.CreateVersion7();

        Admission admitted;
        await using (var release = await AttemptFixtures.BeginReleaseAsync(db, previous, reason))
        {
            var before = await StateAsync();
            var early = await RefusedAsync(store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0])).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(AdmissionError.AttemptActive, early.Error);
            Assert.Equal(before, await StateAsync());

            await release.LockInventoryAsync();
            var admitting = Task.Run(() => store.AdmitAsync(account, key, Deploy(operatives[0])));
            await WaitForLockWaitersAsync(1);
            Assert.False(admitting.IsCompleted);
            await release.CommitAsync();
            admitted = await admitting.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.False(admitted.Replayed);
        Assert.Equal(("cancelled", reason), await RowAsync<(string, string)>(
            "select state, cancel_reason from mission_attempts where attempt_id = @previous", new { previous }));
        Assert.Equal("released", await ScalarAsync<string>("select state from attempt_participants where attempt_id = @previous", new { previous }));
        Assert.Equal("admitted", await ScalarAsync<string>("select state from attempt_participants where attempt_id = @attempt", new { attempt = AttemptOf(admitted) }));
        Assert.Equal(1L, await ActiveParticipationsAsync(account));
    }

    // ---- refusals and atomicity ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Insufficient_energy_writes_nothing()
    {
        var (account, operatives) = await PlayerAsync();
        await PublishTemplateAsync("expensive", 51, 1, 3);
        await PublishTemplateAsync("exact", 50, 1, 3);
        // A later policy version makes any materialization rewrite the wallet (its version moves), so an unchanged wallet row
        // proves the refused admission's materialization was rolled back with everything else.
        await energy.PublishPolicyAsync(100, 50, TimeSpan.FromMinutes(10), 1, 100, Op);
        var before = await StateAsync();

        Assert.Equal(AdmissionError.InsufficientEnergy, (await RefusedAsync(store.AdmitAsync(account, Guid.CreateVersion7(), DeployOf("expensive", operatives[0])))).Error);
        Assert.Equal(before, await StateAsync());
        Assert.Equal(1, await ScalarAsync<int>("select policy_version from energy_wallets where account_id = @account", new { account }));

        // The boundary: a cost equal to the balance admits and leaves zero.
        var exact = await store.AdmitAsync(account, Guid.CreateVersion7(), DeployOf("exact", operatives[0]));
        Assert.Equal(0, Json(exact.Body).GetProperty("energy").GetProperty("balanceAfter").GetInt32());
        Assert.Equal(0, await BalanceAsync(account));
        Assert.Equal(new Entry("reserve", -50, 0), (await EntriesAsync(account))[^1]);
    }

    [Fact]
    public async Task No_wallet_is_InsufficientEnergy_not_zero()
    {
        var (account, operatives) = await WalletlessPlayerAsync();
        var before = await StateAsync();

        Assert.Equal(AdmissionError.InsufficientEnergy, (await RefusedAsync(store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0])))).Error);

        Assert.Equal(before, await StateAsync());
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from energy_wallets where account_id = @account", new { account })); // no wallet was made
        Assert.Empty(await EntriesAsync(account));
    }

    [Fact]
    public async Task An_unprovisioned_or_unknown_account_is_refused_with_nothing_written()
    {
        long bare;
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            bare = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
        var (_, operatives) = await PlayerAsync();
        var before = await StateAsync();

        Assert.Equal(AdmissionError.NotProvisioned, (await RefusedAsync(store.AdmitAsync(bare, Guid.CreateVersion7(), Deploy(operatives[0])))).Error);
        Assert.Equal(AdmissionError.UnknownAccount, (await RefusedAsync(store.AdmitAsync(bare + 1000, Guid.CreateVersion7(), Deploy(operatives[0])))).Error);
        Assert.Equal(before, await StateAsync());
    }

    [Theory]
    [InlineData("mission_attempts", null)]
    [InlineData("attempt_participants", null)]
    [InlineData("energy_holds", null)]
    [InlineData("energy_ledger", "new.kind = 'reserve'")]
    [InlineData("attempt_insurance", null)]
    [InlineData("deploy_requests", null)]
    public async Task Failure_injection_at_each_insert_rolls_back_everything(string table, string? when)
    {
        var (account, operatives) = await PlayerAsync();
        var now = await DbNowAsync();
        await SeedCoverageAsync(account, now.AddHours(-1), now.AddDays(1)); // so the insurance snapshot is written too
        var key = Guid.CreateVersion7();
        await ExecuteAsSuperuserAsync(db,
            "create function bg_test_fail() returns trigger language plpgsql as $$ begin raise exception 'injected failure' using errcode = 'P0001'; end $$; " +
            "create trigger bg_test_fail before insert on " + table + " for each row " + (when is null ? "" : "when (" + when + ") ") + "execute function bg_test_fail();");
        var before = await StateAsync();

        var failure = await Assert.ThrowsAsync<PostgresException>(() => store.AdmitAsync(account, key, Deploy(operatives[0])));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Equal(before, await StateAsync());

        // The same request, retried after the fault is gone, admits once; another retry replays it.
        await ExecuteAsSuperuserAsync(db, "drop trigger bg_test_fail on " + table + "; drop function bg_test_fail();");
        var admitted = await store.AdmitAsync(account, key, Deploy(operatives[0]));
        Assert.False(admitted.Replayed);
        Assert.True((await store.AdmitAsync(account, key, Deploy(operatives[0]))).Replayed);
        Assert.Equal((1L, 1L, 1L), (await ScalarAsync<long>("select count(*) from mission_attempts"), await ScalarAsync<long>("select count(*) from attempt_insurance"),
            await ScalarAsync<long>("select count(*) from energy_ledger where kind = 'reserve'")));
    }

    [Fact]
    public async Task A_terminated_connection_writes_nothing()
    {
        var (account, operatives) = await PlayerAsync();
        var key = Guid.CreateVersion7();
        var before = await StateAsync();
        await InstallGateAsync("deploy_requests"); // the last insert: the attempt, participant, hold and reserve are written
        await using (var gate = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var gateTx = await gate.BeginTransactionAsync();
            await gate.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = GateKey }, gateTx);
            var admission = Task.Run(() => store.AdmitAsync(account, key, Deploy(operatives[0])));
            await WaitForLockWaitersAsync(1);
            Assert.True(await gate.ExecuteScalarAsync<bool>(
                "select pg_terminate_backend(pid) from pg_stat_activity where datname = current_database() and wait_event = 'advisory'", transaction: gateTx));
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => admission);
            await gateTx.RollbackAsync();
        }
        await RemoveGateAsync("deploy_requests");

        Assert.Equal(before, await StateAsync());
        Assert.False((await store.AdmitAsync(account, key, Deploy(operatives[0]))).Replayed); // the client's retry with the key admits once
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from energy_ledger where kind = 'reserve'"));
    }

    // ---- snapshots ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_insurance_snapshot_is_copied_when_active_and_unchanged_by_later_extension_expiry_or_new_versions()
    {
        var insurance = new InsuranceAdminStore(db.AsApp());
        var (account, operatives) = await PlayerAsync();
        var now = await DbNowAsync();
        var expiry = now.AddSeconds(3);
        var activation = await SeedCoverageAsync(account, now.AddMinutes(-5), expiry);

        var admitted = await store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0]));
        var attempt = AttemptOf(admitted);
        Assert.Equal("""{"covered":true,"policyId":"standard","policyVersion":1}""", Json(admitted.Body).GetProperty("insurance").GetRawText().Replace(" ", ""));
        Assert.Equal((activation, "standard", 1), await RowAsync<(Guid, string, int)>(
            "select activation_id, policy_id, policy_version from attempt_insurance where attempt_id = @attempt", new { attempt }));
        Assert.True(await ScalarAsync<bool>(
            "select i.terms = p.terms from attempt_insurance i join insurance_policies p on (p.policy_id, p.version) = (i.policy_id, i.policy_version) where i.attempt_id = @attempt",
            new { attempt }));
        var row = await InsuranceRowAsync(attempt);

        // The account-level coverage expires, restarts, extends and moves to a new policy version: the snapshot never changes.
        await WaitUntilDatabasePassesAsync(expiry);
        Assert.Equal(row, await InsuranceRowAsync(attempt));
        await insurance.ActivateAsync(account, "standard", 86_400, Guid.CreateVersion7(), Op); // restarts from now
        await insurance.ActivateAsync(account, "standard", 86_400, Guid.CreateVersion7(), Op); // extends while active
        await insurance.PublishPolicyAsync("standard", InsuranceTerms.Parse(Terms(25)), Op);
        await insurance.ActivateAsync(account, "standard", 86_400, Guid.CreateVersion7(), Op); // moves the coverage to version 2
        Assert.Equal(2, await ScalarAsync<int>("select policy_version from insurance_coverages where account_id = @account", new { account }));
        Assert.Equal(row, await InsuranceRowAsync(attempt));
    }

    [Fact]
    public async Task An_expired_or_absent_coverage_is_not_covered()
    {
        var (absent, absentOps) = await PlayerAsync();
        var (expired, expiredOps) = await PlayerAsync();
        var now = await DbNowAsync();
        await SeedCoverageAsync(expired, now.AddDays(-2), now.AddDays(-1));

        foreach (var (account, operative) in new[] { (absent, absentOps[0]), (expired, expiredOps[0]) })
        {
            var admitted = await store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operative));
            var body = Json(admitted.Body).GetProperty("insurance");
            Assert.False(body.GetProperty("covered").GetBoolean());
            Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (body.GetProperty("policyId").ValueKind, body.GetProperty("policyVersion").ValueKind));
            Assert.Equal(0L, await ScalarAsync<long>("select count(*) from attempt_insurance where account_id = @account", new { account }));
        }
    }

    [Fact]
    public async Task Coverage_expiring_exactly_at_admission_now_is_not_covered()
    {
        var (account, operatives) = await PlayerAsync();
        var at = await DbNowAsync();
        await SeedCoverageAsync(account, at.AddHours(-1), at); // expires_at == admission's now: over
        await using var frozen = await FrozenClockAsync(at);
        try
        {
            var admitted = await Store(frozen).AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0]));
            Assert.False(Json(admitted.Body).GetProperty("insurance").GetProperty("covered").GetBoolean());
            Assert.Equal(0L, await ScalarAsync<long>("select count(*) from attempt_insurance"));
            Assert.Equal(at.AddSeconds(60), await ScalarAsync<DateTime>("select assign_deadline from mission_attempts")); // the frozen now drove it all
        }
        finally { await DropFrozenClockAsync(); }
    }

    [Fact]
    public async Task Coverage_active_at_admission_now_is_snapshotted_with_complete_terms()
    {
        var (account, operatives) = await PlayerAsync();
        var at = await DbNowAsync();
        var activation = await SeedCoverageAsync(account, at, at.AddDays(1)); // starts_at == admission's now: in force
        await using var frozen = await FrozenClockAsync(at);
        try
        {
            var admitted = await Store(frozen).AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0]));
            Assert.True(Json(admitted.Body).GetProperty("insurance").GetProperty("covered").GetBoolean());
            var attempt = AttemptOf(admitted);
            Assert.Equal(activation, await ScalarAsync<Guid>("select activation_id from attempt_insurance where attempt_id = @attempt", new { attempt }));
            var terms = await ScalarAsync<string>("select terms::text from attempt_insurance where attempt_id = @attempt", new { attempt });
            // Complete: the stored snapshot parses as the full §6.2 terms and equals the policy version's, member for member.
            Assert.Equal(InsuranceTerms.Parse(Terms(50)).Canonical(), InsuranceTerms.Parse(terms).Canonical());
            Assert.True(await ScalarAsync<bool>("select terms = (select terms from insurance_policies where policy_id = 'standard' and version = 1) from attempt_insurance where attempt_id = @attempt",
                new { attempt }));
        }
        finally { await DropFrozenClockAsync(); }
    }

    [Fact]
    public async Task The_snapshot_survives_coverage_expiry_extension_and_new_policy_versions()
    {
        // Successive attempts of one account: each snapshots the coverage in force at its own admission, and the earlier
        // snapshots stay as they were. A zero-cost template keeps the fixture release ledger-free.
        var insurance = new InsuranceAdminStore(db.AsApp());
        await PublishTemplateAsync("free", 0, 1, 3);
        var (account, operatives) = await PlayerAsync();
        var now = await DbNowAsync();
        var expiry = now.AddSeconds(3);
        await SeedCoverageAsync(account, now.AddMinutes(-5), expiry);

        var first = AttemptOf(await store.AdmitAsync(account, Guid.CreateVersion7(), DeployOf("free", operatives[0])));
        var firstRow = await InsuranceRowAsync(first);
        Assert.NotNull(firstRow);
        await AttemptFixtures.ReleaseAsync(db, first);

        await WaitUntilDatabasePassesAsync(expiry);
        var second = await store.AdmitAsync(account, Guid.CreateVersion7(), DeployOf("free", operatives[0]));
        Assert.False(Json(second.Body).GetProperty("insurance").GetProperty("covered").GetBoolean()); // expired at its now
        await AttemptFixtures.ReleaseAsync(db, AttemptOf(second));

        await insurance.PublishPolicyAsync("standard", InsuranceTerms.Parse(Terms(25)), Op);
        await insurance.ActivateAsync(account, "standard", 86_400, Guid.CreateVersion7(), Op); // restarts on version 2
        var extension = await insurance.ActivateAsync(account, "standard", 86_400, Guid.CreateVersion7(), Op); // extends
        var third = await store.AdmitAsync(account, Guid.CreateVersion7(), DeployOf("free", operatives[0]));
        Assert.Equal("""{"covered":true,"policyId":"standard","policyVersion":2}""", Json(third.Body).GetProperty("insurance").GetRawText().Replace(" ", ""));
        var thirdAttempt = AttemptOf(third);
        Assert.Equal(extension.ActivationId, await ScalarAsync<Guid>("select activation_id from attempt_insurance where attempt_id = @thirdAttempt", new { thirdAttempt }));
        Assert.Equal(25, Json(await ScalarAsync<string>("select terms::text from attempt_insurance where attempt_id = @thirdAttempt", new { thirdAttempt }))
            .GetProperty("consumableRefund").GetProperty("percent").GetInt32());

        Assert.Equal(firstRow, await InsuranceRowAsync(first)); // version 1, its activation, untouched
        Assert.Equal(50, Json(firstRow![(firstRow.IndexOf(' ') + 1)..]).GetProperty("terms").GetProperty("consumableRefund").GetProperty("percent").GetInt32());
        Assert.Null(await InsuranceRowAsync(AttemptOf(second)));
    }

    [Fact]
    public async Task The_loadout_snapshot_matches_the_inventory_at_admission()
    {
        var picks = new PickStore(db.AsApp(), new ParticipationLock());
        var edits = new InventoryEditStore(db.AsApp(), new ParticipationLock());
        var (account, operatives) = await PlayerAsync();
        var (o1, o2, o3) = (operatives[0], operatives[1], operatives[2]);
        // o3 picks twice, in an order that is not alphabetical; o1 equips the stash vest; o2 stays home.
        await ExecuteAsSuperuserAsync(db, "update operatives set experience = 250 where operative_id = @o3", new { o3 });
        await picks.PickAsync(account, o3, "survivability", 1);
        await picks.PickAsync(account, o3, "ability", 2);
        var vest = await ScalarAsync<long>("select item_id from item_instances where account_id = @account and definition_id = 'item.light-vest' and operative_id is null", new { account });
        Assert.Equal(4L, (await edits.ApplyAsync(account, 3, [new EquipFromStash(o1, vest)])).Version);

        var admitted = await store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(o3, o1)); // the requested order is kept
        var attempt = AttemptOf(admitted);

        // The expected snapshot, built by the domain from the rows as they stand (choices in pick order).
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var sources = new List<SnapshotOperativeSource>();
        foreach (var o in new[] { o3, o1 })
        {
            var (definition, experience) = await su.QuerySingleAsync<(string, int)>("select definition_id, experience from operatives where operative_id = @o", new { o });
            var choices = (await su.QueryAsync<string>("select choice_id from operative_choices where operative_id = @o order by ordinal", new { o })).ToList();
            sources.Add(new SnapshotOperativeSource(o, definition, experience, choices));
        }
        var instances = (await su.QueryAsync<(long ItemId, string DefinitionId, long? OperativeId, string? Slot)>(
                "select item_id, definition_id, operative_id, equipped_slot from item_instances where account_id = @account", new { account }))
            .Select(i => new HeldInstance(i.ItemId, i.DefinitionId, i.OperativeId, i.Slot switch
            {
                null => null, "weapon" => ItemSlot.Weapon, "armor" => ItemSlot.Armor, "utility" => ItemSlot.Utility, _ => throw new InvalidOperationException(),
            })).ToList();
        var stacks = (await su.QueryAsync<(long? OperativeId, string DefinitionId, int Quantity)>(
                "select operative_id, definition_id, quantity from item_stacks where account_id = @account", new { account }))
            .Select(s => new HeldStack(s.OperativeId, s.DefinitionId, s.Quantity)).ToList();
        var expected = LoadoutSnapshot.Build(account, 4, GameCatalogue.Embedded.Version, [o3, o1], sources, new InventoryModel(operatives, instances, stacks),
            GameCatalogue.Embedded.Document);
        Assert.Empty(expected.UnknownItems);

        Assert.True(await ScalarAsync<bool>("select loadout_snapshot = @expected::jsonb from attempt_participants where attempt_id = @attempt",
            new { expected = expected.Snapshot.ToJson(), attempt }));
        Assert.Equal(4L, await ScalarAsync<long>("select inventory_version from attempt_participants where attempt_id = @attempt", new { attempt }));

        var snapshot = Json(await ScalarAsync<string>("select loadout_snapshot::text from attempt_participants where attempt_id = @attempt", new { attempt }));
        Assert.Equal(Base58Id.Format(account), snapshot.GetProperty("accountId").GetString());
        Assert.Equal(GameCatalogue.Embedded.Version, snapshot.GetProperty("catalogueVersion").GetString());
        var slots = snapshot.GetProperty("operatives").EnumerateArray().ToList();
        Assert.Equal([Base58Id.Format(o3), Base58Id.Format(o1)], slots.Select(s => s.GetProperty("operativeId").GetString()!).ToArray());
        Assert.Equal(["survivability", "ability"], slots[0].GetProperty("choices").EnumerateArray().Select(c => c.GetString()!).ToArray());
        Assert.Equal(Base58Id.Format(vest), slots[1].GetProperty("equipment").GetProperty("armor").GetString());
        // Only the two bags: no stash item and nothing of o2 (whose id is in no slot either, above).
        var carried = slots.SelectMany(s => s.GetProperty("bag").GetProperty("instances").EnumerateArray()).Select(i => i.GetProperty("itemId").GetString()!).ToHashSet();
        Assert.Equal(instances.Where(i => i.OperativeId == o3 || i.OperativeId == o1).Select(i => Base58Id.Format(i.ItemId)).ToHashSet(), carried);
    }

    // ---- zero cost and identity --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_zero_cost_template_admits_with_no_wallet_change_or_ledger_entry()
    {
        await PublishTemplateAsync("free", 0, 1, 3);
        var (account, operatives) = await PlayerAsync();
        var wallet = await ScalarAsync<string>("select xmin::text || ':' || balance from energy_wallets where account_id = @account", new { account });
        var entries = await EntriesAsync(account);
        // With the wallet row and the exclusive publication lock both held elsewhere, a zero-cost admission still goes through:
        // it takes no wallet lock and no policy lock (a 300 ms lock_timeout would have failed it otherwise).
        var bounded = Store(db.AsAppWith(new DbTimeouts(LockTimeoutMs: 300, StatementTimeoutMs: 10_000)));
        Admission admitted;
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var held = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("select 1 from energy_wallets where account_id = @account for update", new { account }, held);
            await holder.ExecuteAsync("select pg_advisory_xact_lock(@key)", new { key = EnergyPolicies.PublicationLockKey }, held);
            admitted = await bounded.AdmitAsync(account, Guid.CreateVersion7(), DeployOf("free", operatives[0])).WaitAsync(TimeSpan.FromSeconds(10));
            await held.RollbackAsync();
        }

        Assert.Equal("""{"cost":0,"balanceAfter":null}""", Json(admitted.Body).GetProperty("energy").GetRawText().Replace(" ", ""));
        var attempt = AttemptOf(admitted);
        Assert.Equal((0, "reserved"), await RowAsync<(int, string)>("select amount, state from energy_holds where attempt_id = @attempt", new { attempt }));
        Assert.Equal(0, await ScalarAsync<int>("select energy_cost from attempt_participants where attempt_id = @attempt", new { attempt }));
        Assert.Equal(wallet, await ScalarAsync<string>("select xmin::text || ':' || balance from energy_wallets where account_id = @account", new { account }));
        Assert.Equal(entries, await EntriesAsync(account));
    }

    [Fact]
    public async Task A_zero_cost_template_admits_an_account_without_a_wallet()
    {
        await PublishTemplateAsync("free", 0, 1, 3);
        var (account, operatives) = await WalletlessPlayerAsync();

        var admitted = await store.AdmitAsync(account, Guid.CreateVersion7(), DeployOf("free", operatives[0]));

        Assert.Equal("""{"cost":0,"balanceAfter":null}""", Json(admitted.Body).GetProperty("energy").GetRawText().Replace(" ", ""));
        Assert.Equal(1L, await ActiveParticipationsAsync(account));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from energy_wallets where account_id = @account", new { account }));
        Assert.Empty(await EntriesAsync(account));
    }

    [Fact]
    public async Task A_retry_of_the_same_attempt_never_reserves_again()
    {
        var (account, operatives) = await PlayerAsync();
        var key = Guid.CreateVersion7();
        var admitted = await store.AdmitAsync(account, key, Deploy(operatives[0]));
        await energy.GrantAsync(account, 5, Guid.CreateVersion7(), Op); // the balance moves on after admission

        for (var i = 0; i < 3; i++)
        {
            var retry = await store.AdmitAsync(account, key, Deploy(operatives[0]));
            Assert.True(retry.Replayed);
            Assert.Equal(admitted.Body, retry.Body); // the recorded attempt and balance, byte for byte
        }
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from energy_ledger where account_id = @account and kind = 'reserve'", new { account }));
        Assert.Equal(45, await BalanceAsync(account));

        // A recorded attempt replays even while the service is unhealthy (the key lookup comes before the health check);
        // a new key is refused there.
        await ExecuteAsSuperuserAsync(db, "update mission_hosts set drained = true");
        Assert.Equal(admitted.Body, (await store.AdmitAsync(account, key, Deploy(operatives[0]))).Body);
        Assert.Equal(AdmissionError.ServiceUnavailable, (await RefusedAsync(store.AdmitAsync(account, Guid.CreateVersion7(), Deploy(operatives[0])))).Error);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from mission_attempts"));
    }

    [Fact]
    public async Task Admission_refuses_more_than_one_participant()
    {
        // Phase 13 admits one participant per attempt (spec §7.1, §17). No request can name another one: a deploy body has
        // exactly missionTemplate and operativeIds, and any other member is refused as InvalidBody.
        Assert.Equal(["MissionTemplate", "OperativeIds"], typeof(DeployRequest).GetProperties().Select(p => p.Name).Order().ToArray());
        foreach (var extra in new[] { "\"participants\":[\"2\"]", "\"accountIds\":[\"2\"]", "\"accountId\":\"2\"", "\"attemptId\":\"0190d1e2-0000-7000-8000-000000000000\"" })
            Assert.Equal(DeployRequestError.InvalidBody, Assert.Throws<DeployRequestException>(() =>
                DeployRequest.Parse(System.Text.Encoding.UTF8.GetBytes($$"""{"missionTemplate":"procedural-standard","operativeIds":["2"],{{extra}}}"""))).Error);

        // The store takes only the session's account and mints a fresh attempt for each admission: two accounts admitted at
        // the same time get two attempts of one participant each; neither joins the other's.
        var (first, firstOps) = await PlayerAsync();
        var (second, secondOps) = await PlayerAsync();
        var admitted = await Task.WhenAll(
            Task.Run(() => store.AdmitAsync(first, Guid.CreateVersion7(), Deploy(firstOps[0]))),
            Task.Run(() => store.AdmitAsync(second, Guid.CreateVersion7(), Deploy(secondOps[0]))));
        Assert.NotEqual(AttemptOf(admitted[0]), AttemptOf(admitted[1]));
        Assert.Equal(new long[] { 1, 1 }, (await ParticipantsPerAttemptAsync()).ToArray());
        Assert.Equal(first, await ScalarAsync<long>("select account_id from attempt_participants where attempt_id = @a", new { a = AttemptOf(admitted[0]) }));
        Assert.Equal(first, await ScalarAsync<long>("select created_by from mission_attempts where attempt_id = @a", new { a = AttemptOf(admitted[0]) }));

        // An account's second admission never joins its own first one either: it is AttemptActive.
        Assert.Equal(AdmissionError.AttemptActive, (await RefusedAsync(store.AdmitAsync(first, Guid.CreateVersion7(), Deploy(firstOps[1])))).Error);

        async Task<IEnumerable<long>> ParticipantsPerAttemptAsync()
        {
            await using var conn = await db.AsSuperuser().OpenConnectionAsync();
            return await conn.QueryAsync<long>("select count(*) from attempt_participants group by attempt_id order by attempt_id");
        }
    }
}
