using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Database.PlayerApiTests;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// POST /v1/inventory/operations (Stage 5, Task 7) over HTTP against a real database. Accounts start from the embedded
/// starter: three operatives, each with a rifle equipped and 2 medkits in its bag, and a stash of a combat blade, a service
/// rifle, a light vest, servo boots and 4 medkits. Ids are Base58 strings; the account comes only from the session.
/// </summary>
[Trait("Requires", "Postgres")]
public class InventoryApiTests : IAsyncLifetime
{
    const string Medkit = "item.medkit", Vest = "item.light-vest";
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

    async Task<ApiServer> StartAsync(Dictionary<string, string?>? settings = null, Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configure = null)
    {
        var api = await ApiServer.StartAsync(db, settings ?? DevOn, configure);
        servers.Add(api);
        return api;
    }

    /// <summary>A provisioned account: its token, first operative, that operative's equipped rifle and the stash's vest.</summary>
    sealed record Player(long Account, string Token, string Operative, string Rifle, string Vest);

    static async Task<string> LoginAsync(ApiServer api, string key) =>
        JsonDocument.Parse(await (await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = key })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;

    async Task<Player> NewPlayerAsync(ApiServer api)
    {
        var account = await admin.CreateAccountAsync(null, null, Op);
        var token = await LoginAsync(api, account.AccountKey.Text);
        var inventory = Parse(await (await api.Http.SendAsync(As(token, HttpMethod.Get, "/v1/inventory"))).Content.ReadAsStringAsync());
        var first = inventory.GetProperty("operatives")[0];
        var vest = inventory.GetProperty("stash").GetProperty("instances").EnumerateArray()
            .First(i => i.GetProperty("definitionId").GetString() == Vest).GetProperty("itemId").GetString()!;
        return new Player(account.AccountId, token, first.GetProperty("operativeId").GetString()!, first.GetProperty("equipment").GetProperty("weapon").GetString()!, vest);
    }

    static string Batch(long expectedVersion, params string[] operations) =>
        $"{{\"expectedVersion\":{expectedVersion},\"operations\":[{string.Join(",", operations)}]}}";

    static HttpRequestMessage Edit(string? token, string json, string mediaType = "application/json") =>
        As(token, HttpMethod.Post, "/v1/inventory/operations", json, mediaType);

    static string EquipOp(string operative, string item) => $"{{\"op\":\"equip\",\"operativeId\":\"{operative}\",\"itemId\":\"{item}\"}}";
    static string UnequipOp(string operative, string slot) => $"{{\"op\":\"unequip\",\"operativeId\":\"{operative}\",\"slot\":\"{slot}\"}}";
    static string ToStashOp(string operative, string item) => $"{{\"op\":\"toStash\",\"operativeId\":\"{operative}\",\"itemId\":\"{item}\"}}";
    static string ToBagOp(string operative, string item) => $"{{\"op\":\"toBag\",\"operativeId\":\"{operative}\",\"itemId\":\"{item}\"}}";
    static string FromStashOp(string operative, string item) => $"{{\"op\":\"equipFromStash\",\"operativeId\":\"{operative}\",\"itemId\":\"{item}\"}}";
    static string StackOp(string verb, string operative, string definition, int quantity) =>
        $"{{\"op\":\"{verb}\",\"operativeId\":\"{operative}\",\"definitionId\":\"{definition}\",\"quantity\":{quantity}}}";

    static async Task<(HttpStatusCode Status, string Body)> SendAsync(ApiServer api, HttpRequestMessage request)
    {
        var response = await api.Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();

    static string CodeOf(string text) => Parse(text).GetProperty("code").GetString()!;

    static int? IndexOf(string text) => Parse(text).TryGetProperty("index", out var index) ? index.GetInt32() : null;

    Task<long> VersionAsync(long account) => ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @account", new { account });

    /// <summary>Every inventory row of the account with its xmin and the version: equal snapshots mean nothing was written.</summary>
    Task<string> SnapshotAsync(long account) => ScalarAsSuperuserAsync<string>(db,
        """
        select concat_ws(E'\n',
          (select json_agg(json_build_object('xmin', i.xmin::text, 'row', row_to_json(i)) order by i.item_id)::text from item_instances i where i.account_id = @account),
          (select json_agg(json_build_object('xmin', s.xmin::text, 'row', row_to_json(s)) order by s.operative_id nulls first, s.definition_id collate "C")::text from item_stacks s where s.account_id = @account),
          (select version::text from inventories where account_id = @account))
        """, new { account });

    static async Task AssertUniform401Async(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().ToString());
    }

    static string[] ItemIds(JsonElement inventory) =>
        inventory.GetProperty("stash").GetProperty("instances").EnumerateArray()
            .Concat(inventory.GetProperty("operatives").EnumerateArray().SelectMany(o => o.GetProperty("bag").GetProperty("instances").EnumerateArray()))
            .Select(i => i.GetProperty("itemId").GetString()!)
            .Order(StringComparer.Ordinal).ToArray();

    // --- tests ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_batch_round_trip_persists_and_returns_the_new_inventory()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        var beforeText = await (await api.Http.SendAsync(As(p.Token, HttpMethod.Get, "/v1/inventory"))).Content.ReadAsStringAsync();
        var itemIds = ItemIds(Parse(beforeText));

        var response = await api.Http.SendAsync(Edit(p.Token, Batch(1, FromStashOp(p.Operative, p.Vest), StackOp("toBag", p.Operative, Medkit, 2))));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = Parse(text);
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        var inventory = body.GetProperty("inventory");
        Assert.Equal(2, inventory.GetProperty("version").GetInt64());
        Assert.False(inventory.GetProperty("locked").GetBoolean());

        var op0 = inventory.GetProperty("operatives")[0];
        Assert.Equal(p.Operative, op0.GetProperty("operativeId").GetString());
        Assert.Equal(p.Vest, op0.GetProperty("equipment").GetProperty("armor").GetString());
        Assert.DoesNotContain(inventory.GetProperty("stash").GetProperty("instances").EnumerateArray(), i => i.GetProperty("itemId").GetString() == p.Vest);
        Assert.Equal(2, inventory.GetProperty("stash").GetProperty("stacks").EnumerateArray().Single(s => s.GetProperty("definitionId").GetString() == Medkit).GetProperty("quantity").GetInt32());
        Assert.Equal(4, op0.GetProperty("bag").GetProperty("stacks").EnumerateArray().Single(s => s.GetProperty("definitionId").GetString() == Medkit).GetProperty("quantity").GetInt32());
        Assert.Equal(itemIds, ItemIds(inventory)); // the same Base58 ids as before: nothing created, nothing renamed

        // What the batch returned is exactly what a fresh read shows, and the database agrees.
        var read = await (await api.Http.SendAsync(As(p.Token, HttpMethod.Get, "/v1/inventory"))).Content.ReadAsStringAsync();
        Assert.Equal(read, inventory.GetRawText());
        Assert.Equal(2L, await VersionAsync(p.Account));
        Assert.True(Base58Id.TryParse(p.Vest, out var vest));
        Assert.True(Base58Id.TryParse(p.Operative, out var operative));
        Assert.Equal(operative, await ScalarAsSuperuserAsync<long>(db, "select operative_id from item_instances where item_id = @vest", new { vest }));
        Assert.Equal("armor", await ScalarAsSuperuserAsync<string>(db, "select equipped_slot from item_instances where item_id = @vest", new { vest }));
    }

    [Fact]
    public async Task Each_operation_shape_is_accepted_and_wrong_shapes_are_400_with_the_index()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        var (o, v, r) = (p.Operative, p.Vest, p.Rifle);

        // Every operation takes exactly its members: one request per shape, each applied (the version counts them).
        string[] shapes =
        [
            FromStashOp(o, v), UnequipOp(o, "armor"), EquipOp(o, v), UnequipOp(o, "armor"),
            ToStashOp(o, v), ToBagOp(o, v), StackOp("toStash", o, Medkit, 1), StackOp("toBag", o, Medkit, 1),
        ];
        var version = 1L;
        foreach (var shape in shapes)
        {
            var (status, text) = await SendAsync(api, Edit(p.Token, Batch(version, shape)));
            Assert.True(HttpStatusCode.OK == status, $"{shape} -> {(int)status} {text}");
            Assert.Equal(++version, Parse(text).GetProperty("version").GetInt64());
        }

        // A wrong shape at index 1, after a valid operation at index 0: 400 InvalidOperation naming index 1, nothing applied.
        var snapshot = await SnapshotAsync(p.Account);
        List<string> wrong =
        [
            $"{{\"op\":\"equip\",\"operativeId\":\"{o}\"}}",                                              // missing itemId
            $"{{\"op\":\"equip\",\"operativeId\":\"{o}\",\"itemId\":\"{r}\",\"slot\":\"weapon\"}}",       // a member it does not take
            $"{{\"op\":\"equip\",\"operativeId\":\"{o}\",\"itemId\":\"{r}\",\"quantity\":1}}",
            $"{{\"op\":\"equip\",\"operativeId\":\"{o}\",\"itemId\":\"{r}\",\"definitionId\":\"{Medkit}\"}}",
            $"{{\"op\":\"equip\",\"itemId\":\"{r}\"}}",                                                   // missing operativeId
            $"{{\"op\":\"equip\",\"operativeId\":\"{o}\",\"itemId\":null}}",
            $"{{\"operativeId\":\"{o}\",\"itemId\":\"{r}\"}}",                                            // missing op
            $"{{\"op\":null,\"operativeId\":\"{o}\",\"itemId\":\"{r}\"}}",
            $"{{\"op\":\"teleport\",\"operativeId\":\"{o}\",\"itemId\":\"{r}\"}}",
            $"{{\"op\":\"Equip\",\"operativeId\":\"{o}\",\"itemId\":\"{r}\"}}",                           // verbs are exact
            $"{{\"op\":\"unequip\",\"operativeId\":\"{o}\"}}",                                            // missing slot
            UnequipOp(o, "none"), UnequipOp(o, "Weapon"), UnequipOp(o, "head"), UnequipOp(o, ""),
            $"{{\"op\":\"unequip\",\"operativeId\":\"{o}\",\"slot\":\"weapon\",\"itemId\":\"{r}\"}}",
            $"{{\"op\":\"toStash\",\"operativeId\":\"{o}\"}}",                                            // neither an item nor a stack
            $"{{\"op\":\"toStash\",\"operativeId\":\"{o}\",\"itemId\":\"{r}\",\"definitionId\":\"{Medkit}\",\"quantity\":1}}", // both
            $"{{\"op\":\"toStash\",\"operativeId\":\"{o}\",\"itemId\":\"{r}\",\"quantity\":1}}",
            $"{{\"op\":\"toBag\",\"operativeId\":\"{o}\",\"definitionId\":\"{Medkit}\"}}",                // a stack needs its quantity
            $"{{\"op\":\"toBag\",\"operativeId\":\"{o}\",\"quantity\":1}}",
            $"{{\"op\":\"toBag\",\"operativeId\":\"{o}\",\"definitionId\":\"\",\"quantity\":1}}",
            $"{{\"op\":\"toBag\",\"operativeId\":\"{o}\",\"itemId\":\"{r}\",\"slot\":\"weapon\"}}",
            $"{{\"op\":\"equipFromStash\",\"operativeId\":\"{o}\"}}",
            $"{{\"op\":\"equipFromStash\",\"operativeId\":\"{o}\",\"definitionId\":\"{Medkit}\",\"quantity\":1}}",
            "null",
        ];
        // ids must be the canonical Base58 spelling
        foreach (var bad in new[] { "0", "1", "O", "I", "l", "2l", "", "-2", "zzzzzzzzzzz", "222222222222", "1" + o })
        {
            wrong.Add($"{{\"op\":\"equip\",\"operativeId\":\"{bad}\",\"itemId\":\"{r}\"}}");
            wrong.Add($"{{\"op\":\"equip\",\"operativeId\":\"{o}\",\"itemId\":\"{bad}\"}}");
            wrong.Add($"{{\"op\":\"toBag\",\"operativeId\":\"{o}\",\"itemId\":\"{bad}\"}}");
        }
        foreach (var op in wrong)
        {
            var (status, text) = await SendAsync(api, Edit(p.Token, Batch(version, EquipOp(o, r), op)));
            Assert.True(HttpStatusCode.BadRequest == status, $"{op} -> {(int)status} {text}");
            Assert.Equal("InvalidOperation", CodeOf(text));
            Assert.Equal(1, IndexOf(text));
        }
        Assert.Equal(snapshot, await SnapshotAsync(p.Account));
    }

    [Fact]
    public async Task Malformed_bodies_are_InvalidBody_and_change_nothing()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        var good = EquipOp(p.Operative, p.Rifle);
        var snapshot = await SnapshotAsync(p.Account);
        string[] bad =
        [
            "{not json", "[]", "null", "5", "\"x\"", "",
            $"{{\"operations\":[{good}]}}",                                           // missing expectedVersion
            "{\"expectedVersion\":1}",                                                // missing operations
            "{\"expectedVersion\":1,\"operations\":null}",
            "{\"expectedVersion\":null,\"operations\":[]}",
            $"{{\"expectedVersion\":\"1\",\"operations\":[{good}]}}",                 // strict numbers
            $"{{\"expectedVersion\":1.5,\"operations\":[{good}]}}",
            $"{{\"expectedVersion\":1e0,\"operations\":[{good}]}}",
            $"{{\"expectedVersion\":1,\"operations\":{good}}}",                       // not a list
            "{\"expectedVersion\":1,\"operations\":[]}",                              // an empty batch is never a state change
            $"{{\"expectedVersion\":1,\"operations\":[{good}],\"extra\":true}}",      // unknown members are refused
            $"{{\"expectedVersion\":1,\"operations\":[{good}],\"accountId\":\"2\"}}",
            $"{{\"expectedVersion\":1,\"operations\":[{{\"op\":\"equip\",\"operativeId\":\"{p.Operative}\",\"itemId\":\"{p.Rifle}\",\"accountId\":\"2\"}}]}}",
            Batch(1, StackOp("toBag", p.Operative, Medkit, 1).Replace("\"quantity\":1", "\"quantity\":\"1\"")),
            Batch(1, StackOp("toBag", p.Operative, Medkit, 1).Replace("\"quantity\":1", "\"quantity\":1.5")),
            "{\"expectedVersion\":1,\"operations\":[5]}",
            $"{{\"expectedVersion\":1,\"operations\":[{{\"op\":5,\"operativeId\":\"{p.Operative}\"}}]}}",
            $"{{\"expectedVersion\":1,\"operations\":[{{\"op\":\"equip\",\"operativeId\":{p.Operative},\"itemId\":\"{p.Rifle}\"}}]}}", // an id as a JSON number
        ];
        foreach (var body in bad)
        {
            var (status, text) = await SendAsync(api, Edit(p.Token, body));
            Assert.True(HttpStatusCode.BadRequest == status, $"{body} -> {(int)status} {text}");
            Assert.Equal("InvalidBody", CodeOf(text));
        }
        Assert.Equal(snapshot, await SnapshotAsync(p.Account));
    }

    [Fact]
    public async Task A_duplicated_body_member_is_InvalidBody_and_changes_nothing()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        var good = EquipOp(p.Operative, p.Rifle);
        var snapshot = await SnapshotAsync(p.Account);
        string[] bad =
        [
            $"{{\"expectedVersion\":1,\"expectedVersion\":1,\"operations\":[{good}]}}",   // even when both copies agree
            $"{{\"expectedVersion\":1,\"operations\":[{good}],\"operations\":[{good}]}}",
        ];
        foreach (var body in bad)
        {
            var (status, text) = await SendAsync(api, Edit(p.Token, body));
            Assert.True(HttpStatusCode.BadRequest == status, $"{body} -> {(int)status} {text}");
            Assert.Equal("InvalidBody", CodeOf(text));
        }
        Assert.Equal(snapshot, await SnapshotAsync(p.Account));
    }

    [Fact]
    public async Task Refusals_are_422_with_code_and_index()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        var (o, v, r) = (p.Operative, p.Vest, p.Rifle);
        Assert.True(Base58Id.TryParse(o, out var operative));
        Assert.True(Base58Id.TryParse(r, out var rifle));
        var ghostOperative = Base58Id.Format(operative + 1_000_000);
        var ghostItem = Base58Id.Format(rifle + 1_000_000);
        var snapshot = await SnapshotAsync(p.Account);

        (string Op, string Code)[] cases =
        [
            (EquipOp(ghostOperative, r), "UnknownOperative"),
            (EquipOp(o, ghostItem), "NotFound"),
            (EquipOp(o, v), "NotFound"),                              // the vest is in the stash, not this bag
            (FromStashOp(o, r), "NotFound"),                          // the rifle is equipped, not in the stash
            (UnequipOp(o, "armor"), "NothingEquipped"),
            (ToStashOp(o, r), "Equipped"),                            // unequip it first
            (StackOp("toBag", o, Medkit, 0), "InvalidQuantity"),
            (StackOp("toBag", o, Medkit, -1), "InvalidQuantity"),
            (StackOp("toBag", o, Medkit, 5), "InsufficientQuantity"), // the stash holds 4
            (StackOp("toStash", o, Medkit, 3), "InsufficientQuantity"), // the bag holds 2
        ];
        foreach (var (op, code) in cases)
        {
            // Index 1: the valid first operation (re-equipping the rifle) is rolled back with the rest.
            var (status, text) = await SendAsync(api, Edit(p.Token, Batch(1, EquipOp(o, r), op)));
            Assert.True(HttpStatusCode.UnprocessableEntity == status, $"{op} -> {(int)status} {text}");
            Assert.Equal(code, CodeOf(text));
            Assert.Equal(1, IndexOf(text));
        }
        Assert.Equal(snapshot, await SnapshotAsync(p.Account));

        // A full destination: the stash holds more medkits than the bag could ever take.
        await ExecuteAsSuperuserAsync(db, "update item_stacks set quantity = 100 where account_id = @account and operative_id is null and definition_id = @medkit",
            new { account = p.Account, medkit = Medkit });
        snapshot = await SnapshotAsync(p.Account);
        var full = await SendAsync(api, Edit(p.Token, Batch(1, StackOp("toBag", o, Medkit, 100))));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, full.Status);
        Assert.Equal(("DestinationFull", (int?)0), (CodeOf(full.Body), IndexOf(full.Body)));

        // An item whose definition the catalogue no longer knows cannot be equipped from the stash.
        Assert.True(Base58Id.TryParse(v, out var vestId));
        await ExecuteAsSuperuserAsync(db, "update item_instances set definition_id = 'item.retired' where item_id = @vestId", new { vestId });
        snapshot = await SnapshotAsync(p.Account);
        var retired = await SendAsync(api, Edit(p.Token, Batch(1, FromStashOp(o, v))));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retired.Status);
        Assert.Equal(("UnknownItem", (int?)0), (CodeOf(retired.Body), IndexOf(retired.Body)));
        Assert.Equal(snapshot, await SnapshotAsync(p.Account));
    }

    [Fact]
    public async Task Stale_unprovisioned_and_locked_are_409()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        var snapshot = await SnapshotAsync(p.Account);
        var valid = FromStashOp(p.Operative, p.Vest);

        // Stale (the account is at version 1).
        foreach (var stale in new long[] { 0, 2, 99 })
        {
            var (status, text) = await SendAsync(api, Edit(p.Token, Batch(stale, valid)));
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("VersionMismatch", CodeOf(text));
        }
        Assert.Equal(snapshot, await SnapshotAsync(p.Account));

        // Unprovisioned: an account row with no inventory.
        var key = OpaqueSecret.New("dk1");
        var bare = await ScalarAsSuperuserAsync<long>(db, "insert into accounts (status) values ('active') returning account_id");
        await ExecuteAsSuperuserAsync(db, "insert into dev_credentials (credential_id, account_id, secret_hash) values (@id, @account, @hash)",
            new { id = Guid.CreateVersion7(), account = bare, hash = key.Hash() });
        var bareToken = await LoginAsync(api, key.Text);
        var unprovisioned = await SendAsync(api, Edit(bareToken, Batch(1, EquipOp("2", "3"))));
        Assert.Equal(HttpStatusCode.Conflict, unprovisioned.Status);
        Assert.Equal("NotProvisioned", CodeOf(unprovisioned.Body));
        foreach (var table in new[] { "inventories", "operatives", "item_instances", "item_stacks" })
            Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, $"select count(*) from {table} where account_id = @bare", new { bare }));

        // Locked, through a test-configured seam. The lock is checked before the version and before the operations, so a
        // locked account is 409 InventoryLocked even with a stale version, and even when an operation would be refused.
        var locked = new AlwaysLocked();
        var lockedApi = await StartAsync(DevOn, b => b.Services.AddSingleton<IInventoryLock>(locked));
        foreach (var body in new[] { Batch(1, valid), Batch(99, valid), Batch(0, valid), Batch(1, EquipOp("2", "3")) })
        {
            var (status, text) = await SendAsync(lockedApi, Edit(p.Token, body));
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("InventoryLocked", CodeOf(text));
            Assert.Null(IndexOf(text));
        }
        var pick = await SendAsync(lockedApi, Pick(p.Token, p.Operative, "{\"choiceId\":\"combat\",\"expectedVersion\":1}"));
        Assert.Equal(HttpStatusCode.Conflict, pick.Status); // InventoryLocked is 409 on every route
        Assert.Equal("InventoryLocked", CodeOf(pick.Body));
        Assert.Equal(5, locked.Calls);
        Assert.Equal(snapshot, await SnapshotAsync(p.Account));
    }

    [Fact]
    public async Task Limits_are_enforced()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        var one = EquipOp(p.Operative, p.Rifle);
        var version = 1L;

        // Exactly 32 operations (the default) are accepted, in one version step.
        var ok = await SendAsync(api, Edit(p.Token, Batch(version, Enumerable.Repeat(one, 32).ToArray())));
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal(++version, Parse(ok.Body).GetProperty("version").GetInt64());

        // 33 are not.
        var many = await SendAsync(api, Edit(p.Token, Batch(version, Enumerable.Repeat(one, 33).ToArray())));
        Assert.Equal(HttpStatusCode.BadRequest, many.Status);
        Assert.Equal("TooManyOperations", CodeOf(many.Body));

        // An empty list is not a state change.
        var empty = await SendAsync(api, Edit(p.Token, Batch(version)));
        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
        Assert.Equal("InvalidBody", CodeOf(empty.Body));

        // 16 KiB exactly is read; one byte more is 413. (Padding is whitespace after the JSON: all ASCII.)
        var exact = Batch(version, one).PadRight(16 * 1024);
        Assert.Equal(16 * 1024, exact.Length);
        var accepted = await SendAsync(api, Edit(p.Token, exact));
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Equal(++version, Parse(accepted.Body).GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await SendAsync(api, Edit(p.Token, Batch(version, one).PadRight(16 * 1024 + 1)))).Status);

        // Not JSON is 415, whatever the body says.
        foreach (var type in new[] { "text/plain", "application/x-www-form-urlencoded" })
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await SendAsync(api, Edit(p.Token, Batch(version, one), type))).Status);
        Assert.Equal(version, await VersionAsync(p.Account));

        // The operation limit is configuration (1..256).
        var small = await StartAsync(new(DevOn) { ["BLACKGLASS_INVENTORY_MAX_OPERATIONS"] = "2" });
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(small, Edit(p.Token, Batch(version, one, one)))).Status);
        var three = await SendAsync(small, Edit(p.Token, Batch(version + 1, one, one, one)));
        Assert.Equal(HttpStatusCode.BadRequest, three.Status);
        Assert.Equal("TooManyOperations", CodeOf(three.Body));
    }

    [Fact]
    public async Task Every_inventory_route_without_a_token_is_the_uniform_401()
    {
        var api = await StartAsync();
        var p = await NewPlayerAsync(api);
        var snapshot = await SnapshotAsync(p.Account);
        var good = Batch(1, FromStashOp(p.Operative, p.Vest));
        foreach (var bearer in new string?[] { null, "garbage", "at1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", p.Token + "x" })
        {
            await AssertUniform401Async(await api.Http.SendAsync(As(bearer, HttpMethod.Get, "/v1/inventory")));
            await AssertUniform401Async(await api.Http.SendAsync(Edit(bearer, good)));
            await AssertUniform401Async(await api.Http.SendAsync(Edit(bearer, "{not json"))); // authentication comes before any body check
            await AssertUniform401Async(await api.Http.SendAsync(Edit(bearer, good, "text/plain")));
        }
        Assert.Equal(snapshot, await SnapshotAsync(p.Account));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.Http.SendAsync(As(p.Token, HttpMethod.Get, "/v1/inventory/operations"))).StatusCode);
    }

    [Fact]
    public async Task A_held_inventory_lock_is_503_busy_and_nothing_is_replayed()
    {
        var api = await StartAsync(new(DevOn) { [DbTimeouts.LockTimeoutVar] = "300" });
        var p = await NewPlayerAsync(api);
        var snapshot = await SnapshotAsync(p.Account);
        var batch = Batch(1, FromStashOp(p.Operative, p.Vest));

        HttpResponseMessage busy;
        await using (var conn = await db.AsSuperuser().OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(conn, "select 1 from inventories where account_id = @account for update", new { account = p.Account }, tx);
            try { busy = await api.Http.SendAsync(Edit(p.Token, batch)); }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.NotNull(busy.Headers.RetryAfter);
        Assert.Equal("", await busy.Content.ReadAsStringAsync());

        // The lock is released: the server has not re-applied the batch behind the client's back.
        Assert.Equal(snapshot, await SnapshotAsync(p.Account));
        var again = await SendAsync(api, Edit(p.Token, batch)); // the client's own retry, at the version it still holds
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(2, Parse(again.Body).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task A_broken_invariant_is_a_500_with_a_safe_log_line_and_nothing_is_written()
    {
        var logs = new CapturingLoggerProvider();
        var api = await StartAsync(DevOn, b => { b.Logging.ClearProviders(); b.Logging.SetMinimumLevel(LogLevel.Trace); b.Logging.AddProvider(logs); });
        var p = await NewPlayerAsync(api);
        var snapshot = await SnapshotAsync(p.Account);

        // A test-only trigger silently swallows every instance update, so the write touches no row: the store's row count
        // check throws InventoryInvariantException and the batch rolls back.
        await ExecuteAsSuperuserAsync(db, "create function bg_test_skip_update() returns trigger language plpgsql as $$ begin return null; end $$");
        await ExecuteAsSuperuserAsync(db, "create trigger bg_test_skip_update before update on item_instances for each row execute function bg_test_skip_update()");
        HttpResponseMessage response;
        try { response = await api.Http.SendAsync(Edit(p.Token, Batch(1, FromStashOp(p.Operative, p.Vest)))); }
        finally
        {
            await ExecuteAsSuperuserAsync(db, "drop trigger if exists bg_test_skip_update on item_instances");
            await ExecuteAsSuperuserAsync(db, "drop function if exists bg_test_skip_update()");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync()); // nothing about the failure reaches the client
        Assert.Equal(snapshot, await SnapshotAsync(p.Account));
        var text = logs.Text;
        Assert.Contains("Inventory invariant", text);
        Assert.DoesNotContain(p.Token[4..], text);
    }
}
