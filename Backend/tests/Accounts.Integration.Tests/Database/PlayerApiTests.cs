using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Migrations;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// GET /v1/roster, GET /v1/inventory, POST /v1/operatives/{operativeId}/choices and /v1/me's inventoryVersion (Stage 4,
/// Task 10). Owned ids (operatives, items) are Base58 strings on the wire; the account comes only from the session. Test XP
/// uses the embedded track: thresholds 0, 100, 250, 450, 700; choices ability, combat, mobility, survivability.
/// </summary>
[Trait("Requires", "Postgres")]
public class PlayerApiTests : IAsyncLifetime
{
    static readonly Dictionary<string, string?> DevOn = new() { ["BLACKGLASS_DEV_AUTH"] = "enabled" };
    TestDatabase db = null!;
    AccountAdminStore admin = null!;

    public async Task InitializeAsync() { db = await MigratedAsync(); admin = new AccountAdminStore(db.AsApp()); }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    static async Task<string> TokenAsync(HttpClient http, string key) =>
        JsonDocument.Parse(await (await http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = key })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;

    internal static HttpRequestMessage As(string? token, HttpMethod method, string path, string? json = null, string mediaType = "application/json")
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, mediaType);
        return request;
    }

    internal static HttpRequestMessage Pick(string? token, string operativeId, string json) =>
        As(token, HttpMethod.Post, $"/v1/operatives/{operativeId}/choices", json);

    static async Task<(HttpResponseMessage Response, string Text)> SendAsync(HttpClient http, HttpRequestMessage request)
    {
        var response = await http.SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    static string CodeOf(string text) => Json(text).GetProperty("code").GetString()!;

    async Task<(IssuedCredential Account, string Token, long Operative)> PlayerAsync(ApiServer api, string? handle = null, int xp = 0)
    {
        var account = await admin.CreateAccountAsync(handle, null, Op);
        var operative = await ScalarAsSuperuserAsync<long>(db, "select min(operative_id) from operatives where account_id = @a", new { a = account.AccountId });
        if (xp != 0) await ExecuteAsSuperuserAsync(db, "update operatives set experience = @xp where operative_id = @operative", new { xp, operative });
        return (account, await TokenAsync(api.Http, account.AccountKey.Text), operative);
    }

    static async Task AssertUniform401Async(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().ToString());
    }

    // ---- reads ----

    [Fact]
    public async Task Roster_and_inventory_show_the_starter_after_login()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token, operative) = await PlayerAsync(api, "starter");

        var (rosterResponse, rosterText) = await SendAsync(api.Http, As(token, HttpMethod.Get, "/v1/roster"));
        Assert.Equal(HttpStatusCode.OK, rosterResponse.StatusCode);
        Assert.True(rosterResponse.Headers.CacheControl!.NoStore);
        var roster = Json(rosterText);
        Assert.Equal(["version", "operatives"], roster.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, roster.GetProperty("version").GetInt64());
        var operatives = roster.GetProperty("operatives").EnumerateArray().ToList();
        Assert.Equal(await ScalarAsSuperuserAsync<long>(db, "select count(*) from operatives where account_id = @a", new { a = account.AccountId }), operatives.Count);
        var first = operatives[0];
        Assert.Equal(["operativeId", "definitionId", "definitionKnown", "experience", "rank", "pendingPicks", "choices"], first.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.String, first.GetProperty("operativeId").ValueKind);
        Assert.Equal(Base58Id.Format(operative), first.GetProperty("operativeId").GetString());
        Assert.Equal(await ScalarAsSuperuserAsync<string>(db, "select definition_id from operatives where operative_id = @operative", new { operative }), first.GetProperty("definitionId").GetString());
        Assert.True(first.GetProperty("definitionKnown").GetBoolean());
        Assert.Equal((0, 1, 0), (first.GetProperty("experience").GetInt32(), first.GetProperty("rank").GetInt32(), first.GetProperty("pendingPicks").GetInt32()));
        Assert.Empty(first.GetProperty("choices").EnumerateArray());

        var (inventoryResponse, inventoryText) = await SendAsync(api.Http, As(token, HttpMethod.Get, "/v1/inventory"));
        Assert.Equal(HttpStatusCode.OK, inventoryResponse.StatusCode);
        Assert.True(inventoryResponse.Headers.CacheControl!.NoStore);
        var inventory = Json(inventoryText);
        Assert.Equal(["version", "locked", "stash", "operatives"], inventory.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, inventory.GetProperty("version").GetInt64());
        Assert.False(inventory.GetProperty("locked").GetBoolean());
        Assert.Equal(["instances", "stacks"], inventory.GetProperty("stash").EnumerateObject().Select(p => p.Name).ToArray());

        // Every stored item instance appears once, as a Base58 string, in the bag of the operative that carries it.
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var rows = (await conn.QueryAsync<(long ItemId, long? OperativeId, string? Slot)>(
            "select item_id, operative_id, equipped_slot from item_instances where account_id = @a", new { a = account.AccountId })).ToList();
        Assert.NotEmpty(rows);
        var bag = inventory.GetProperty("operatives").EnumerateArray().Single(o => o.GetProperty("operativeId").GetString() == Base58Id.Format(operative));
        Assert.Equal(["operativeId", "bag", "equipment"], bag.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["instances", "stacks"], bag.GetProperty("bag").EnumerateObject().Select(p => p.Name).ToArray());
        var inBag = bag.GetProperty("bag").GetProperty("instances").EnumerateArray().Select(i => i.GetProperty("itemId").GetString()!).Order().ToArray();
        Assert.Equal(rows.Where(r => r.OperativeId == operative).Select(r => Base58Id.Format(r.ItemId)).Order().ToArray(), inBag);
        var inStash = inventory.GetProperty("stash").GetProperty("instances").EnumerateArray().Select(i => i.GetProperty("itemId").GetString()!).Order().ToArray();
        Assert.Equal(rows.Where(r => r.OperativeId is null).Select(r => Base58Id.Format(r.ItemId)).Order().ToArray(), inStash);
        var equipment = bag.GetProperty("equipment");
        Assert.Equal(["weapon", "armor", "utility"], equipment.EnumerateObject().Select(p => p.Name).ToArray());
        foreach (var slot in new[] { "weapon", "armor", "utility" })
        {
            var stored = rows.SingleOrDefault(r => r.OperativeId == operative && r.Slot == slot);
            var shown = equipment.GetProperty(slot);
            if (stored == default) Assert.Equal(JsonValueKind.Null, shown.ValueKind);
            else Assert.Equal(Base58Id.Format(stored.ItemId), shown.GetString());
        }
        var stack = (await conn.QueryAsync<(string Definition, int Quantity)>("select definition_id, quantity from item_stacks where account_id = @a", new { a = account.AccountId })).ToList();
        var shownStacks = inventory.GetProperty("stash").GetProperty("stacks").EnumerateArray()
            .Concat(inventory.GetProperty("operatives").EnumerateArray().SelectMany(o => o.GetProperty("bag").GetProperty("stacks").EnumerateArray()))
            .Select(s => (s.GetProperty("definitionId").GetString()!, s.GetProperty("quantity").GetInt32())).Order().ToList();
        Assert.Equal(stack.Select(s => (s.Definition, s.Quantity)).Order().ToList(), shownStacks);
    }

    [Fact]
    public async Task A_locked_pick_is_409_InventoryLocked()
    {
        // The existing configure hook replaces the IInventoryLock singleton (the last registration wins); production has no switch that locks an account.
        await using var api = await ApiServer.StartAsync(db, DevOn, configure: b => b.Services.AddSingleton<IInventoryLock>(new AlwaysLocked()));
        var (account, token, operative) = await PlayerAsync(api, xp: 100);

        var (response, text) = await SendAsync(api.Http, Pick(token, Base58Id.Format(operative), "{\"choiceId\":\"combat\",\"expectedVersion\":1}"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("InventoryLocked", CodeOf(text));
        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @a", new { a = account.AccountId }));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operative_choices"));

        var (inventory, inventoryText) = await SendAsync(api.Http, As(token, HttpMethod.Get, "/v1/inventory"));
        Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);
        Assert.True(Json(inventoryText).GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task A_pick_round_trip_persists_and_returns_the_new_version()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token, operative) = await PlayerAsync(api, xp: 100);
        var id = Base58Id.Format(operative);

        var (response, text) = await SendAsync(api.Http, Pick(token, id, "{\"choiceId\":\"combat\",\"expectedVersion\":1}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = Json(text);
        Assert.Equal(["version", "operative"], body.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        var picked = body.GetProperty("operative");
        Assert.Equal(id, picked.GetProperty("operativeId").GetString());
        Assert.Equal((100, 2, 0), (picked.GetProperty("experience").GetInt32(), picked.GetProperty("rank").GetInt32(), picked.GetProperty("pendingPicks").GetInt32()));
        Assert.Equal(["combat"], picked.GetProperty("choices").EnumerateArray().Select(c => c.GetString()!).ToArray());

        Assert.Equal(2L, await ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @a", new { a = account.AccountId }));
        Assert.Equal("combat", await ScalarAsSuperuserAsync<string>(db, "select choice_id from operative_choices where operative_id = @operative", new { operative }));

        // The reads agree, and so does /v1/me.
        var roster = Json((await SendAsync(api.Http, As(token, HttpMethod.Get, "/v1/roster"))).Text);
        Assert.Equal(2, roster.GetProperty("version").GetInt64());
        Assert.Equal(["combat"], roster.GetProperty("operatives")[0].GetProperty("choices").EnumerateArray().Select(c => c.GetString()!).ToArray());
        Assert.Equal(2, Json((await SendAsync(api.Http, As(token, HttpMethod.Get, "/v1/inventory"))).Text).GetProperty("version").GetInt64());
        Assert.Equal(2, Json((await SendAsync(api.Http, As(token, HttpMethod.Get, "/v1/me"))).Text).GetProperty("inventoryVersion").GetInt64());
    }

    // ---- refusals ----

    [Fact]
    public async Task Stale_version_is_409()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token, operative) = await PlayerAsync(api, xp: 250); // two picks pending
        var id = Base58Id.Format(operative);

        var (wrong, wrongText) = await SendAsync(api.Http, Pick(token, id, "{\"choiceId\":\"combat\",\"expectedVersion\":7}"));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("VersionMismatch", CodeOf(wrongText));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.Http, Pick(token, id, "{\"choiceId\":\"combat\",\"expectedVersion\":1}"))).Response.StatusCode);

        // Replaying the version just spent is stale, and the second pick it would have made does not happen.
        var (replay, replayText) = await SendAsync(api.Http, Pick(token, id, "{\"choiceId\":\"mobility\",\"expectedVersion\":1}"));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("VersionMismatch", CodeOf(replayText));
        Assert.Equal(2L, await ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @a", new { a = account.AccountId }));
        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operative_choices where operative_id = @operative", new { operative }));
    }

    [Fact]
    public async Task No_pending_pick_is_422()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token, operative) = await PlayerAsync(api, xp: 99);
        var (response, text) = await SendAsync(api.Http, Pick(token, Base58Id.Format(operative), "{\"choiceId\":\"combat\",\"expectedVersion\":1}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("NoPendingPick", CodeOf(text));
        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @a", new { a = account.AccountId }));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operative_choices where operative_id = @operative", new { operative }));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData("Combat")]
    public async Task Unknown_choice_is_422(string choice)
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token, operative) = await PlayerAsync(api, xp: 100);
        var (response, text) = await SendAsync(api.Http, Pick(token, Base58Id.Format(operative), JsonSerializer.Serialize(new { choiceId = choice, expectedVersion = 1 })));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("UnknownChoice", CodeOf(text));
        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @a", new { a = account.AccountId }));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operative_choices where operative_id = @operative", new { operative }));
    }

    [Fact]
    public async Task An_operative_that_does_not_exist_is_404()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (_, token, operative) = await PlayerAsync(api, xp: 100);
        var (response, text) = await SendAsync(api.Http, Pick(token, Base58Id.Format(operative + 1000), "{\"choiceId\":\"combat\",\"expectedVersion\":1}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("OperativeNotFound", CodeOf(text));
    }

    [Fact]
    public async Task Malformed_bodies_and_ids_are_400()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token, operative) = await PlayerAsync(api, xp: 100); // a valid pick would change the version: nothing may
        var id = Base58Id.Format(operative);

        var badBodies = new[]
        {
            "{\"expectedVersion\":1}", "{\"choiceId\":null,\"expectedVersion\":1}", "{\"choiceId\":\"combat\"}", "{\"choiceId\":\"combat\",\"expectedVersion\":null}",
            "{\"choiceId\":\"combat\",\"expectedVersion\":1.5}", "{\"choiceId\":\"combat\",\"expectedVersion\":\"1\"}", "{\"choiceId\":\"combat\",\"expectedVersion\":1e0}",
            "{\"choiceId\":7,\"expectedVersion\":1}", "{\"choiceId\":\"combat\",\"expectedVersion\":1,\"accountId\":\"2\"}", "{\"choiceId\":\"combat\",\"expectedVersion\":1,\"extra\":true}",
            "{not json", "[]", "null", "5", "\"combat\"", "",
        };
        foreach (var body in badBodies)
        {
            var (response, text) = await SendAsync(api.Http, Pick(token, id, body));
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"{body} -> {(int)response.StatusCode}");
            Assert.Equal("InvalidBody", CodeOf(text));
        }
        var (notJson, notJsonText) = await SendAsync(api.Http, As(token, HttpMethod.Post, $"/v1/operatives/{id}/choices", "{\"choiceId\":\"combat\",\"expectedVersion\":1}", "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Equal("InvalidBody", CodeOf(notJsonText));

        // A path id must be the canonical Base58 spelling: no leading '1' (the zero digit), no 0, O, I or l, 1 to 11 characters, within long.
        foreach (var badId in new[] { "1", "1" + id, "0", "O", "I", "l", "2l", "zzzzzzzzzzz", "222222222222", "-2", "%20", "2%20", "٣" })
        {
            var (response, text) = await SendAsync(api.Http, Pick(token, badId, "{\"choiceId\":\"combat\",\"expectedVersion\":1}"));
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"{badId} -> {(int)response.StatusCode}");
            Assert.Equal("InvalidOperativeId", CodeOf(text));
        }

        var oversized = $"{{\"choiceId\":\"{new string('a', 5000)}\",\"expectedVersion\":1}}";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await SendAsync(api.Http, Pick(token, id, oversized))).Response.StatusCode);

        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @a", new { a = account.AccountId }));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operative_choices where operative_id = @operative", new { operative }));
        // The same body, well formed, does pick: the refusals above were about shape alone.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.Http, Pick(token, id, "{\"choiceId\":\"combat\",\"expectedVersion\":1}"))).Response.StatusCode);
    }

    // ---- authentication, /v1/me, an unprovisioned account, a busy lock ----

    [Fact]
    public async Task Every_player_route_without_a_token_is_the_uniform_401()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (_, token, operative) = await PlayerAsync(api, xp: 100);
        var id = Base58Id.Format(operative);
        var good = "{\"choiceId\":\"combat\",\"expectedVersion\":1}";
        foreach (var bearer in new string?[] { null, "garbage", "at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", token + "x" })
        {
            await AssertUniform401Async(await api.Http.SendAsync(As(bearer, HttpMethod.Get, "/v1/roster")));
            await AssertUniform401Async(await api.Http.SendAsync(As(bearer, HttpMethod.Get, "/v1/inventory")));
            await AssertUniform401Async(await api.Http.SendAsync(Pick(bearer, id, good)));
            await AssertUniform401Async(await api.Http.SendAsync(Pick(bearer, "0", "{not json"))); // authentication comes before any id or body check
            await AssertUniform401Async(await api.Http.SendAsync(As(bearer, HttpMethod.Get, "/v1/me")));
        }
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operative_choices"));
    }

    [Fact]
    public async Task Player_routes_accept_only_their_own_methods()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (_, token, operative) = await PlayerAsync(api);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.Http.SendAsync(As(token, HttpMethod.Post, "/v1/roster", "{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.Http.SendAsync(As(token, HttpMethod.Put, "/v1/inventory", "{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.Http.SendAsync(As(token, HttpMethod.Get, $"/v1/operatives/{Base58Id.Format(operative)}/choices"))).StatusCode);
    }

    [Fact]
    public async Task Me_reports_the_inventory_version()
    {
        await using var api = await ApiServer.StartAsync(db, DevOn);
        var (account, token, _) = await PlayerAsync(api, "me-inv");
        var (response, text) = await SendAsync(api.Http, As(token, HttpMethod.Get, "/v1/me"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal($"{{\"accountId\":\"{AccountId.Format(account.AccountId)}\",\"inventoryVersion\":1}}", text);

        await ExecuteAsSuperuserAsync(db, "update inventories set version = 41 where account_id = @a", new { a = account.AccountId });
        Assert.Equal($"{{\"accountId\":\"{AccountId.Format(account.AccountId)}\",\"inventoryVersion\":41}}", (await SendAsync(api.Http, As(token, HttpMethod.Get, "/v1/me"))).Text);
    }

    [Fact]
    public async Task An_unprovisioned_account_reads_409_NotProvisioned_and_nothing_is_written()
    {
        await using var old = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(old.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync(2);
        var account = await PreStage4AccountAsync(old);
        var key = OpaqueSecret.New("dk1");
        await ExecuteAsSuperuserAsync(old, "insert into dev_credentials (credential_id, account_id, secret_hash) values (@id, @account, @hash)", new { id = Guid.CreateVersion7(), account, hash = key.Hash() });
        try
        {
            await using var api = await ApiServer.StartAsync(old, DevOn);
            var token = await TokenAsync(api.Http, key.Text);

            foreach (var path in new[] { "/v1/roster", "/v1/inventory" })
            {
                var (response, text) = await SendAsync(api.Http, As(token, HttpMethod.Get, path));
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("NotProvisioned", CodeOf(text));
            }
            var (pick, pickText) = await SendAsync(api.Http, Pick(token, "2", "{\"choiceId\":\"combat\",\"expectedVersion\":1}"));
            Assert.Equal(HttpStatusCode.Conflict, pick.StatusCode);
            Assert.Equal("NotProvisioned", CodeOf(pickText));
            Assert.Equal($"{{\"accountId\":\"{AccountId.Format(account)}\",\"inventoryVersion\":null}}", (await SendAsync(api.Http, As(token, HttpMethod.Get, "/v1/me"))).Text);

            foreach (var table in new[] { "inventories", "operatives", "item_instances", "item_stacks", "operative_choices" })
                Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(old, $"select count(*) from {table}"));
        }
        finally { Assert.Empty(await InvariantFailuresAsync(old)); }
    }

    [Fact]
    public async Task A_held_inventory_lock_is_503_busy()
    {
        await using var api = await ApiServer.StartAsync(db, new(DevOn) { [DbTimeouts.LockTimeoutVar] = "300" });
        var (account, token, operative) = await PlayerAsync(api, xp: 100);

        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("select 1 from inventories where account_id = @a for update", new { a = account.AccountId }, tx);
        HttpResponseMessage busy, read;
        try
        {
            busy = await api.Http.SendAsync(Pick(token, Base58Id.Format(operative), "{\"choiceId\":\"combat\",\"expectedVersion\":1}"));
            read = await api.Http.SendAsync(As(token, HttpMethod.Get, "/v1/roster")); // a plain read waits for nobody
        }
        finally { await tx.RollbackAsync(); }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.NotNull(busy.Headers.RetryAfter);
        Assert.Equal("", await busy.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operative_choices where operative_id = @operative", new { operative }));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(api.Http, Pick(token, Base58Id.Format(operative), "{\"choiceId\":\"combat\",\"expectedVersion\":1}"))).Response.StatusCode);
    }
}
