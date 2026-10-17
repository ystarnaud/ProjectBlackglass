using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Database.PlayerApiTests;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The mandatory, fail-closed inventory lock (owner ruling S7-D4, the Stage 5 obligation): while the account has a
/// participation in an active state, an inventory edit and an advancement pick are 409 InventoryLocked with nothing written,
/// whatever their expected version, and GET /v1/inventory reports locked: true. Only a terminal transition (released,
/// settled) releases it. A participation query that fails is a 500 and one that waits past its timeouts is a 503: never
/// "unlocked". Driven through serve, so the production wiring is what is tested.
/// </summary>
[Trait("Requires", "Postgres")]
public class ParticipationLockTests : IAsyncLifetime
{
    const string Vest = "item.light-vest";
    static readonly Dictionary<string, string?> DevOn = new() { ["BLACKGLASS_DEV_AUTH"] = "enabled" };
    TestDatabase db = null!;
    AccountAdminStore admin = null!;
    readonly List<ApiServer> servers = [];

    public async Task InitializeAsync() { db = await MigratedAsync(); admin = new AccountAdminStore(db.AsApp()); }

    public async Task DisposeAsync()
    {
        foreach (var server in servers) await server.DisposeAsync();
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    // --- helpers ----------------------------------------------------------------------------------------------------------

    async Task<ApiServer> StartAsync(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>(DevOn);
        foreach (var (k, v) in extra ?? new()) settings[k] = v;
        var api = await ApiServer.StartAsync(db, settings);
        servers.Add(api);
        return api;
    }

    /// <summary>A provisioned, logged-in account whose first operative has one pick pending (100 xp), and its stash vest.</summary>
    sealed record Player(long Account, string Token, long Operative, long Vest);

    async Task<Player> NewPlayerAsync(ApiServer api)
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        var a = account.AccountId;
        var operative = await ScalarAsSuperuserAsync<long>(db, "select min(operative_id) from operatives where account_id = @a", new { a });
        await ExecuteAsSuperuserAsync(db, "update operatives set experience = 100 where operative_id = @operative", new { operative });
        var vest = await ScalarAsSuperuserAsync<long>(db,
            "select item_id from item_instances where account_id = @a and definition_id = @Vest and operative_id is null", new { a, Vest });
        var login = await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = account.AccountKey.Text });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
        return new Player(a, token, operative, vest);
    }

    static async Task<(HttpStatusCode Status, string Body)> SendAsync(ApiServer api, HttpRequestMessage request)
    {
        var response = await api.Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Equips the stash vest on the first operative.</summary>
    static Task<(HttpStatusCode Status, string Body)> EditAsync(ApiServer api, Player p, long expectedVersion) => SendAsync(api, As(p.Token, HttpMethod.Post, "/v1/inventory/operations",
        $$"""{"expectedVersion":{{expectedVersion}},"operations":[{"op":"equipFromStash","operativeId":"{{Base58Id.Format(p.Operative)}}","itemId":"{{Base58Id.Format(p.Vest)}}"}]}"""));

    static Task<(HttpStatusCode Status, string Body)> PickAsync(ApiServer api, Player p, long expectedVersion) =>
        SendAsync(api, Pick(p.Token, Base58Id.Format(p.Operative), $$"""{"choiceId":"combat","expectedVersion":{{expectedVersion}}}"""));

    static Task<(HttpStatusCode Status, string Body)> InventoryAsync(ApiServer api, Player p) => SendAsync(api, As(p.Token, HttpMethod.Get, "/v1/inventory"));

    static Task<(HttpStatusCode Status, string Body)> RosterAsync(ApiServer api, Player p) => SendAsync(api, As(p.Token, HttpMethod.Get, "/v1/roster"));

    static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();

    static void AssertLocked((HttpStatusCode Status, string Body) response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("InventoryLocked", Parse(response.Body).GetProperty("code").GetString());
    }

    async Task<bool> ReadLockedAsync(ApiServer api, Player p)
    {
        var (status, body) = await InventoryAsync(api, p);
        Assert.Equal(HttpStatusCode.OK, status);
        return Parse(body).GetProperty("locked").GetBoolean();
    }

    Task<long> VersionAsync(long account) => ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @account", new { account });

    /// <summary>Every row an edit or a pick could write, with its xmin, and the version: equal snapshots mean nothing was written.</summary>
    Task<string> SnapshotAsync(long account) => ScalarAsSuperuserAsync<string>(db,
        """
        select concat_ws(E'\n',
          (select json_agg(json_build_object('xmin', i.xmin::text, 'row', row_to_json(i)) order by i.item_id)::text from item_instances i where i.account_id = @account),
          (select json_agg(json_build_object('xmin', s.xmin::text, 'row', row_to_json(s)) order by s.operative_id nulls first, s.definition_id collate "C")::text from item_stacks s where s.account_id = @account),
          (select json_agg(json_build_object('xmin', c.xmin::text, 'row', row_to_json(c)) order by c.operative_id, c.ordinal)::text
             from operative_choices c join operatives o on o.operative_id = c.operative_id where o.account_id = @account),
          (select json_agg(json_build_object('xmin', o.xmin::text, 'row', row_to_json(o)) order by o.operative_id)::text from operatives o where o.account_id = @account),
          (select json_build_object('xmin', v.xmin::text, 'row', row_to_json(v))::text from inventories v where v.account_id = @account))
        """, new { account });

    // --- tests ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_and_a_pick_during_a_participation_are_InventoryLocked()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        await AttemptFixtures.AdmitAsync(db, p.Account);
        var before = await SnapshotAsync(p.Account);

        AssertLocked(await EditAsync(api, p, 1));
        AssertLocked(await PickAsync(api, p, 1));

        Assert.Equal(before, await SnapshotAsync(p.Account)); // nothing written, xmin included
        Assert.Equal(1L, await VersionAsync(p.Account));
    }

    [Fact]
    public async Task A_stale_expected_version_never_hides_InventoryLocked()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        Assert.Equal(HttpStatusCode.OK, (await PickAsync(api, p, 1)).Status); // the version is now 2, so 1 is stale
        await ExecuteAsSuperuserAsync(db, "update operatives set experience = 300 where operative_id = @operative", new { operative = p.Operative }); // a pick is pending again
        await AttemptFixtures.AdmitAsync(db, p.Account);
        var before = await SnapshotAsync(p.Account);

        foreach (var stale in new long[] { 1, 99 })
        {
            AssertLocked(await EditAsync(api, p, stale)); // never VersionMismatch while locked
            AssertLocked(await PickAsync(api, p, stale));
        }
        AssertLocked(await EditAsync(api, p, 2)); // and the current version is just as locked
        AssertLocked(await PickAsync(api, p, 2));

        Assert.Equal(before, await SnapshotAsync(p.Account));
        Assert.Equal(2L, await VersionAsync(p.Account));
    }

    [Fact]
    public async Task Reads_during_a_participation_report_locked_true()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        Assert.False(await ReadLockedAsync(api, p));
        var attempt = await AttemptFixtures.AdmitAsync(db, p.Account);
        var before = await SnapshotAsync(p.Account);

        var (status, body) = await InventoryAsync(api, p);
        Assert.Equal(HttpStatusCode.OK, status); // reads stay allowed during a mission (S7-D4)
        var inventory = Parse(body);
        Assert.True(inventory.GetProperty("locked").GetBoolean());
        Assert.Equal(1L, inventory.GetProperty("version").GetInt64());
        Assert.NotEmpty(inventory.GetProperty("stash").GetProperty("instances").EnumerateArray());

        var (rosterStatus, rosterBody) = await RosterAsync(api, p);
        Assert.Equal(HttpStatusCode.OK, rosterStatus);
        Assert.False(Parse(rosterBody).TryGetProperty("locked", out _)); // the roster has no locked field

        Assert.Equal(before, await SnapshotAsync(p.Account)); // a read writes nothing
        await AttemptFixtures.ReleaseAsync(db, attempt);
        Assert.False(await ReadLockedAsync(api, p));
    }

    [Fact]
    public async Task After_release_the_account_can_edit_and_pick_again()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        var attempt = await AttemptFixtures.AdmitAsync(db, p.Account);
        AssertLocked(await EditAsync(api, p, 1));
        AssertLocked(await PickAsync(api, p, 1));

        await AttemptFixtures.ReleaseAsync(db, attempt);
        var (editStatus, editBody) = await EditAsync(api, p, 1);
        Assert.Equal(HttpStatusCode.OK, editStatus);
        Assert.Equal(2L, Parse(editBody).GetProperty("version").GetInt64());
        Assert.False(Parse(editBody).GetProperty("inventory").GetProperty("locked").GetBoolean());
        var (pickStatus, pickBody) = await PickAsync(api, p, 2);
        Assert.Equal(HttpStatusCode.OK, pickStatus);
        Assert.Equal(3L, Parse(pickBody).GetProperty("version").GetInt64());

        // A new participation (the index allows one once the old one is released) locks the account again.
        await AttemptFixtures.AdmitAsync(db, p.Account);
        Assert.True(await ReadLockedAsync(api, p));
        AssertLocked(await EditAsync(api, p, 3));
        Assert.Equal(3L, await VersionAsync(p.Account));
    }

    [Fact]
    public async Task The_lock_releases_only_on_a_terminal_transition()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        var attempt = await AttemptFixtures.AdmitAsync(db, p.Account);
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        Task MoveAsync(string table, string to, string? extra = null) => su.ExecuteAsync(
            $"update {table} set state = @to{extra} where attempt_id = @attempt", new { to, attempt });
        async Task ForcedAsync(string sql) // a state 0007 gives no edge yet: written with the transition triggers skipped (superuser only)
        {
            await using var tx = await su.BeginTransactionAsync();
            await su.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
            await su.ExecuteAsync(sql, new { attempt }, tx);
            await tx.CommitAsync();
        }

        Assert.True(await ReadLockedAsync(api, p)); // admitted

        foreach (var state in new[] { "assigned", "launching", "ready", "running" }) await AttemptFixtures.MoveAsync(db, attempt, state);
        await MoveAsync("energy_holds", "charged"); // the start commit's edge
        await MoveAsync("attempt_participants", "joined", ", started_at = clock_timestamp()");
        Assert.True(await ReadLockedAsync(api, p));
        await MoveAsync("attempt_participants", "left");
        Assert.True(await ReadLockedAsync(api, p));
        await MoveAsync("attempt_participants", "result_pending");
        Assert.True(await ReadLockedAsync(api, p));
        await MoveAsync("attempt_participants", "quarantined");
        Assert.True(await ReadLockedAsync(api, p));
        await ForcedAsync("update attempt_participants set state = 'unresponsive' where attempt_id = @attempt");
        Assert.True(await ReadLockedAsync(api, p));
        AssertLocked(await EditAsync(api, p, 1));
        await ForcedAsync("update attempt_participants set state = 'quarantined' where attempt_id = @attempt");

        // Settlement is terminal: quarantined -> settled (Stage 9 adds the hold's settle edge, so it is forced here).
        await MoveAsync("attempt_participants", "settled");
        await ForcedAsync("update energy_holds set state = 'settled' where attempt_id = @attempt");
        await AttemptFixtures.MoveAsync(db, attempt, "ending");
        await AttemptFixtures.MoveAsync(db, attempt, "settled");
        Assert.False(await ReadLockedAsync(api, p));
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(api, p, 1)).Status);

        // Release is terminal too: a second account, admitted then released.
        var q = await NewPlayerAsync(api);
        var other = await AttemptFixtures.AdmitAsync(db, q.Account);
        Assert.True(await ReadLockedAsync(api, q));
        await AttemptFixtures.ReleaseAsync(db, other);
        Assert.False(await ReadLockedAsync(api, q));
    }

    [Fact]
    public async Task A_failing_participation_query_refuses_the_mutation()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        var before = await SnapshotAsync(p.Account);
        await ExecuteAsSuperuserAsync(db, "revoke select on attempt_participants from accounts_app"); // this test database only

        foreach (var response in new[] { await EditAsync(api, p, 1), await PickAsync(api, p, 1), await InventoryAsync(api, p) })
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.Status); // never "unlocked", never locked: false
            Assert.Equal("", response.Body);
        }
        Assert.Equal(before, await SnapshotAsync(p.Account));

        // The participation query was the only failure: with SELECT back, the same edit applies.
        await ExecuteAsSuperuserAsync(db, "grant select on attempt_participants to accounts_app");
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(api, p, 1)).Status);
    }

    [Fact]
    public async Task A_participation_query_stuck_behind_a_lock_is_busy()
    {
        var api = await StartAsync(new() { [DbTimeouts.LockTimeoutVar] = "300" });
        var p = await NewPlayerAsync(api);
        var before = await SnapshotAsync(p.Account);

        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var tx = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("lock table attempt_participants in access exclusive mode", transaction: tx); // the query's only wait

            foreach (var send in new Func<Task<HttpResponseMessage>>[]
                     {
                         () => api.Http.SendAsync(As(p.Token, HttpMethod.Post, "/v1/inventory/operations",
                             $$"""{"expectedVersion":1,"operations":[{"op":"equipFromStash","operativeId":"{{Base58Id.Format(p.Operative)}}","itemId":"{{Base58Id.Format(p.Vest)}}"}]}""")),
                         () => api.Http.SendAsync(Pick(p.Token, Base58Id.Format(p.Operative), """{"choiceId":"combat","expectedVersion":1}""")),
                         () => api.Http.SendAsync(As(p.Token, HttpMethod.Get, "/v1/inventory")),
                     })
            {
                var response = await send().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); // busy, never "unlocked"
                Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
                Assert.Equal("", await response.Content.ReadAsStringAsync());
            }
            await tx.RollbackAsync();
        }
        Assert.Equal(before, await SnapshotAsync(p.Account));

        // A statement timeout shorter than the lock wait is just as busy, for the edit, the pick and the read.
        var bounded = db.AsAppWith(new DbTimeouts(LockTimeoutMs: 10_000, StatementTimeoutMs: 300));
        var participations = new ParticipationLock();
        await using (var holder = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var tx = await holder.BeginTransactionAsync();
            await holder.ExecuteAsync("lock table attempt_participants in access exclusive mode", transaction: tx);
            foreach (var call in new Func<Task>[]
                     {
                         () => new InventoryEditStore(bounded, participations).ApplyAsync(p.Account, 1, [new EquipFromStash(p.Operative, p.Vest)]),
                         () => new PickStore(bounded, participations).PickAsync(p.Account, p.Operative, "combat", 1),
                         () => new PlayerReadStore(bounded, participations).GetInventoryAsync(p.Account),
                     })
            {
                var e = await Assert.ThrowsAsync<PostgresException>(() => call().WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(PostgresErrorCodes.QueryCanceled, e.SqlState);
                Assert.Equal(DbFailureKind.Busy, DbFailure.Classify(e));
            }
            await tx.RollbackAsync();
        }
        Assert.Equal(before, await SnapshotAsync(p.Account));
    }

    [Fact]
    public async Task The_active_states_match_the_index_predicate_and_the_invariants()
    {
        static string[] Quoted(string sql) =>
            Regex.Matches(sql, "'([a-z_]+)'").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        var active = AttemptStates.ActiveParticipant.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["admitted", "joined", "left", "quarantined", "result_pending", "unresponsive"], active);

        // 0007's one_active_participation predicate, as PostgreSQL stores it.
        var index = await ScalarAsSuperuserAsync<string>(db, "select indexdef from pg_indexes where schemaname = 'public' and indexname = 'one_active_participation'");
        Assert.StartsWith("CREATE UNIQUE INDEX one_active_participation ON public.attempt_participants USING btree (account_id) WHERE ", index);
        Assert.Equal(active, Quoted(index[index.IndexOf(" WHERE ", StringComparison.Ordinal)..]));

        // Both active-state lists of the 0007 invariant file (two_active_participations, ended_attempt_active_participant).
        var invariant = await File.ReadAllTextAsync(Path.Combine(InvariantRunner.RealDirectory, "0007_attempts.sql"));
        var lists = Regex.Matches(invariant, @"p\.state in \(([^)]*)\)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, lists.Count);
        Assert.All(lists, list => Assert.Equal(active, Quoted(list)));

        // Every participant state is either active or terminal (released, settled): a state added later must be classified.
        var check = await ScalarAsSuperuserAsync<string>(db,
            "select pg_get_constraintdef(oid) from pg_constraint where conrelid = 'attempt_participants'::regclass and contype = 'c' and pg_get_constraintdef(oid) like 'CHECK ((state = %'");
        Assert.Equal(active.Concat(["released", "settled"]).Order(StringComparer.Ordinal), Quoted(check));

        // The lock's query, with the states as its array parameter, can use the partial unique index (no table scan as the
        // participation history grows).
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("set local enable_seqscan = off", transaction: tx);
        var plan = string.Join('\n', await conn.QueryAsync<string>("explain " + ParticipationLock.Query,
            new { accountId = 1L, activeStates = AttemptStates.ActiveParticipant.ToArray() }, tx));
        Assert.Contains("one_active_participation", plan);
    }

    [Fact]
    public async Task Serve_wires_the_database_backed_lock_only()
    {
        var api = await StartAsync();
        var services = api.App.Services;
        var registered = Assert.Single(services.GetServices<IInventoryLock>());
        Assert.IsType<ParticipationLock>(registered);

        foreach (var store in new object[]
                 {
                     services.GetRequiredService<PlayerReadStore>(), services.GetRequiredService<PickStore>(), services.GetRequiredService<InventoryEditStore>(),
                 })
        {
            var field = Assert.Single(store.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic), f => f.FieldType == typeof(IInventoryLock));
            Assert.Same(registered, field.GetValue(store));
        }
    }
}
