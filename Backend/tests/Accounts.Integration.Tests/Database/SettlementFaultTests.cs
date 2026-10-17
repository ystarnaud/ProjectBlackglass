using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Hosts;
using Blackglass.Accounts.Persistence.Settlement;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Fault injection (plan Task 8): a failure at every written table leaves the database exactly as before and a retry
/// settles once; a backend terminated mid-transaction commits nothing; deadlock-class errors answer 503 and are never retried; a
/// crash before the hash notification still settles without a void.</summary>
[Trait("Requires", "Postgres")]
public class SettlementFaultTests : IAsyncLifetime
{
    TestDatabase db = null!;
    MissionKit kit = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        kit = await MissionKit.CreateAsync(db);
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    public static TheoryData<string, string> Steps => new()
    {
        { "item_stacks", "update" }, { "item_stacks", "delete" }, { "item_stacks", "insert" }, { "item_instances", "insert" }, { "item_instances", "update" },
        { "operatives", "update" }, { "pool_collections", "insert" }, { "energy_holds", "update" }, { "energy_ledger", "insert" },
        { "inventories", "update" }, { "result_submissions", "insert" }, { "result_submission_payloads", "insert" }, { "participant_settlements", "insert" },
        { "attempt_participants", "update" }, { "mission_attempts", "update" }, { "anomaly_events", "insert" },
    };

    [Theory]
    [MemberData(nameof(Steps))]
    public async Task A_fault_at_any_written_table_leaves_the_database_unchanged_and_a_retry_settles_once(string table, string operation)
    {
        // A defeat with consumption (stack update or delete) and a success with loot (instance insert, equipment update) between them
        // touch every table; each case runs the shape that reaches its table.
        var r = await ManifestKit.StartAsync(kit, db, pool: [new(0, 0, "item.medkit", 2), new(0, 1, "item.light-vest", 1)]);
        var consume = table == "item_stacks" && operation is "delete" or "insert" ? 2 : 1; // all owned medkits used: the loot credit inserts a new stack row
        var m = ManifestKit.Manifest(r, "success", consume);
        if (table == "item_instances" && operation == "update")
            m = m with { Operatives = [m.Operatives[0] with { Equipment = [.. m.Operatives[0].Equipment, EquipmentRef.Loot("armor", 0, 1)] }] };
        if (table == "item_stacks" && operation == "delete")
            m = ManifestKit.Manifest(r, "defeat", consume: 2, collectAll: false);
        var store = ManifestKit.Store(db);
        var before = await FaultTriggers.DigestAsync(db);
        await FaultTriggers.InstallRaiseAsync(db, table, operation);
        try
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => ManifestKit.SubmitAsync(store, db, ManifestKit.Body(m)));
            Assert.Equal("P0001", (e as PostgresException ?? e.InnerException as PostgresException)?.SqlState);
            Assert.Equal(before, await FaultTriggers.DigestAsync(db));
        }
        finally { await FaultTriggers.RemoveAsync(db, table); }
        Assert.IsType<ResultOutcome.Settled>(await ManifestKit.SubmitAsync(store, db, ManifestKit.Body(m)));
        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
    }

    [Fact]
    public async Task A_settling_backend_terminated_while_paused_commits_nothing_and_the_retry_settles_once()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var m = ManifestKit.Manifest(r);
        var before = await FaultTriggers.DigestAsync(db);
        const long Key = 900_001;
        await FaultTriggers.InstallPauseAsync(db, "participant_settlements", "insert", Key);
        Task<ResultOutcome> relay;
        await using (await FaultTriggers.HoldAsync(db, Key))
        {
            relay = ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(m));
            var pid = await FaultTriggers.WaitForPausedAsync(db, Key);
            await ExecuteAsSuperuserAsync(db, "select pg_terminate_backend(@pid)", new { pid });
            await Assert.ThrowsAnyAsync<Exception>(() => relay);
        }
        await FaultTriggers.RemoveAsync(db, "participant_settlements");
        Assert.Equal(before, await FaultTriggers.DigestAsync(db));
        Assert.IsType<ResultOutcome.Settled>(await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(m)));
    }

    [Fact]
    public async Task A_crash_inside_the_hash_notification_commits_nothing_and_the_relay_still_settles_without_a_void()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        const long Key = 900_002;
        await FaultTriggers.InstallPauseAsync(db, "attempt_participants", "update", Key);
        await using (await FaultTriggers.HoldAsync(db, Key))
        {
            var recorded = ManifestKit.Store(db).RecordedAsync(r.Attempt, r.Account, r.Mission.Credential.Hash(),
                ResultManifest.HashOf(ManifestKit.Manifest(r).ToCanonicalBytes()), default);
            var pid = await FaultTriggers.WaitForPausedAsync(db, Key);
            await ExecuteAsSuperuserAsync(db, "select pg_terminate_backend(@pid)", new { pid });
            await Assert.ThrowsAnyAsync<Exception>(() => recorded);
        }
        await FaultTriggers.RemoveAsync(db, "attempt_participants");
        Assert.Null(await ScalarAsSuperuserAsync<string?>(db, "select recorded_manifest_hash from attempt_participants where attempt_id = @Attempt", new { r.Attempt }));
        Assert.IsType<ResultOutcome.Settled>(await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r))));
        Assert.Equal(("success", "absent"), await RowAsSuperuserAsync<(string, string)>(db, 
            "select outcome, recorded_hash from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
    }

    [Fact]
    public async Task Deadlock_and_serialization_classes_answer_503_once_and_commit_nothing()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        await using var api = await ApiServer.StartAsync(db);
        var secret = (await new HostAdminStore(db.AsApp()).RotateSecretAsync("h1", Op)).Secret.Text;
        var body = Encoding.UTF8.GetString(ManifestKit.Manifest(r).ToCanonicalBytes());
        foreach (var state in new[] { "40P01", "40001" })
        {
            await FaultTriggers.InstallRaiseAsync(db, "participant_settlements", "insert", state);
            var before = await FaultTriggers.DigestAsync(db);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/agent/v1/results") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            var response = await api.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(response.Headers.Contains("Retry-After"));
            Assert.Equal(before, await FaultTriggers.DigestAsync(db));
            await FaultTriggers.RemoveAsync(db, "participant_settlements");
        }
        // One attempt per request (no automatic retry): the stored submissions count is still zero and the next relay settles.
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from result_submissions"));
        using var ok = new HttpRequestMessage(HttpMethod.Post, "/agent/v1/results") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        ok.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await api.Http.SendAsync(ok)).StatusCode);
    }
}
