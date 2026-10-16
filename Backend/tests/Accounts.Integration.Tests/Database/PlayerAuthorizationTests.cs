using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Database.PlayerApiTests;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Review Focus 1 (IDOR) for the player routes. Operative and item ids are sequential and public by design, so account A,
/// guessing the operative that sits next to its own (B's), must learn nothing about it: the same 404 as for an id that
/// belongs to nobody. And no request carries an account: the session is the only source.
/// </summary>
[Trait("Requires", "Postgres")]
public class PlayerAuthorizationTests : IAsyncLifetime
{
    const string Good = "{\"choiceId\":\"combat\",\"expectedVersion\":1}";
    TestDatabase db = null!;
    ApiServer api = null!;
    IssuedCredential a = null!, b = null!;
    string tokenA = null!, tokenB = null!;
    long operativeA, operativeB;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_DEV_AUTH"] = "enabled" });
        var admin = new AccountAdminStore(db.AsApp());
        a = await admin.CreateAccountAsync("alice", null, Op);
        b = await admin.CreateAccountAsync("bob", null, Op);
        tokenA = await TokenAsync(a);
        tokenB = await TokenAsync(b);
        operativeA = await ScalarAsSuperuserAsync<long>(db, "select max(operative_id) from operatives where account_id = @a", new { a = a.AccountId });
        operativeB = await ScalarAsSuperuserAsync<long>(db, "select min(operative_id) from operatives where account_id = @a", new { a = b.AccountId });
        // Both could pick, so a pick is refused for ownership alone.
        await ExecuteAsSuperuserAsync(db, "update operatives set experience = 100 where operative_id = any(@ids)", new { ids = new[] { operativeA, operativeB } });
        Assert.Equal(operativeA + 1, operativeB); // A.s last operative and B.s first are neighbours: the obvious guess
    }

    public async Task DisposeAsync()
    {
        await api.DisposeAsync();
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    async Task<string> TokenAsync(IssuedCredential account) =>
        JsonDocument.Parse(await (await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = account.AccountKey.Text })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;

    async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request)
    {
        var response = await api.Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    Task<long> VersionOfAsync(IssuedCredential account) =>
        ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @a", new { a = account.AccountId });

    async Task<long[]> OperativesOfAsync(IssuedCredential account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await Dapper.SqlMapper.QueryAsync<long>(conn, "select operative_id from operatives where account_id = @a order by operative_id", new { a = account.AccountId })).ToArray();
    }

    Task<long> PicksOfAsync(long operative) =>
        ScalarAsSuperuserAsync<long>(db, "select count(*) from operative_choices where operative_id = @operative", new { operative });

    [Fact]
    public async Task A_pick_for_a_guessed_neighbouring_operative_is_the_same_404_as_an_unknown_one()
    {
        var neighbour = await SendAsync(Pick(tokenA, Base58Id.Format(operativeB), Good));
        var nobodys = await SendAsync(Pick(tokenA, Base58Id.Format(operativeB + 1000), Good));
        var otherOfB = await SendAsync(Pick(tokenA, Base58Id.Format(operativeB + 1), Good));
        Assert.Equal(HttpStatusCode.NotFound, neighbour.Status);
        Assert.Equal("OperativeNotFound", JsonDocument.Parse(neighbour.Body).RootElement.GetProperty("code").GetString());
        Assert.Equal(nobodys, neighbour);  // status and the whole body, byte for byte
        Assert.Equal(nobodys, otherOfB);

        // B's operative, version and picks are untouched, and so are A's.
        Assert.Equal((1L, 1L, 0L, 0L), (await VersionOfAsync(a), await VersionOfAsync(b), await PicksOfAsync(operativeA), await PicksOfAsync(operativeB)));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Pick(tokenB, Base58Id.Format(operativeB), Good))).Status); // B's own pick still works
    }

    [Fact]
    public async Task Reads_show_only_the_callers_operatives_and_items()
    {
        var rosterA = JsonDocument.Parse((await SendAsync(As(tokenA, HttpMethod.Get, "/v1/roster"))).Body).RootElement;
        var ownedByA = await OperativesOfAsync(a);
        Assert.Equal(ownedByA.Select(Base58Id.Format).ToArray(), rosterA.GetProperty("operatives").EnumerateArray().Select(o => o.GetProperty("operativeId").GetString()!).ToArray());

        var inventoryA = JsonDocument.Parse((await SendAsync(As(tokenA, HttpMethod.Get, "/v1/inventory"))).Body).RootElement;
        var itemsOf = async (IssuedCredential account) => (await ScalarAsSuperuserAsync<long[]>(db, "select array_agg(item_id) from item_instances where account_id = @a", new { a = account.AccountId })).Select(Base58Id.Format).Order().ToArray();
        var shownItems = inventoryA.GetProperty("stash").GetProperty("instances").EnumerateArray()
            .Concat(inventoryA.GetProperty("operatives").EnumerateArray().SelectMany(o => o.GetProperty("bag").GetProperty("instances").EnumerateArray()))
            .Select(i => i.GetProperty("itemId").GetString()!).Order().ToArray();
        Assert.NotEmpty(await itemsOf(b));
        Assert.Equal(await itemsOf(a), shownItems);
        Assert.Equal(ownedByA.Select(Base58Id.Format).ToArray(), inventoryA.GetProperty("operatives").EnumerateArray().Select(o => o.GetProperty("operativeId").GetString()!).ToArray());
    }

    [Fact]
    public async Task An_account_id_in_query_header_or_body_changes_nothing()
    {
        var idB = AccountId.Format(b.AccountId);
        string[] queries = [$"?accountId={idB}", $"?account_id={b.AccountId}&sub={b.AccountId}&operativeId={Base58Id.Format(operativeB)}"];

        foreach (var path in new[] { "/v1/roster", "/v1/inventory", "/v1/me" })
        {
            var plain = await SendAsync(As(tokenA, HttpMethod.Get, path));
            Assert.Equal(HttpStatusCode.OK, plain.Status);
            foreach (var query in queries) Assert.Equal(plain, await SendAsync(As(tokenA, HttpMethod.Get, path + query)));
            var header = As(tokenA, HttpMethod.Get, path);
            header.Headers.Add("X-Account-Id", idB);
            Assert.Equal(plain, await SendAsync(header));
            Assert.Equal(plain, await SendAsync(As(tokenA, HttpMethod.Get, path, $"{{\"accountId\":\"{idB}\"}}")));
        }

        // A pick takes the account from the session whatever else the request says...
        var viaQuery = Pick(tokenA, Base58Id.Format(operativeA), Good);
        viaQuery.RequestUri = new Uri($"/v1/operatives/{Base58Id.Format(operativeA)}/choices?accountId={idB}&operativeId={Base58Id.Format(operativeB)}", UriKind.Relative);
        viaQuery.Headers.Add("X-Account-Id", idB);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(viaQuery)).Status);
        Assert.Equal((2L, 1L, 1L, 0L), (await VersionOfAsync(a), await VersionOfAsync(b), await PicksOfAsync(operativeA), await PicksOfAsync(operativeB)));

        // ...and a body that names an account is refused outright: no request type has such a member.
        foreach (var body in new[] { $"{{\"choiceId\":\"combat\",\"expectedVersion\":2,\"accountId\":\"{idB}\"}}", $"{{\"choiceId\":\"combat\",\"expectedVersion\":2,\"account_id\":{b.AccountId}}}" })
        {
            var (status, text) = await SendAsync(Pick(tokenA, Base58Id.Format(operativeA), body));
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("InvalidBody", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
        }
        Assert.Equal((2L, 1L, 1L, 0L), (await VersionOfAsync(a), await VersionOfAsync(b), await PicksOfAsync(operativeA), await PicksOfAsync(operativeB)));
    }

    // ---- inventory edits (Stage 5): the same guarantees for POST /v1/inventory/operations ----

    static string Edits(long expectedVersion, params string[] operations) =>
        $"{{\"expectedVersion\":{expectedVersion},\"operations\":[{string.Join(",", operations)}]}}";

    static HttpRequestMessage Edit(string token, string json, string path = "/v1/inventory/operations") => As(token, HttpMethod.Post, path, json);

    async Task<long[]> ItemsOfAsync(IssuedCredential account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await Dapper.SqlMapper.QueryAsync<long>(conn, "select item_id from item_instances where account_id = @a order by item_id", new { a = account.AccountId })).ToArray();
    }

    Task<long> RifleOfAsync(long operative) =>
        ScalarAsSuperuserAsync<long>(db, "select item_id from item_instances where operative_id = @operative and equipped_slot = 'weapon'", new { operative });

    /// <summary>Every inventory row of both accounts and their versions, with xmin: equal snapshots mean nothing was written.</summary>
    Task<string> InventoriesSnapshotAsync() => ScalarAsSuperuserAsync<string>(db,
        """
        select concat_ws(E'\n',
          (select json_agg(json_build_object('xmin', i.xmin::text, 'row', row_to_json(i)) order by i.item_id)::text from item_instances i),
          (select json_agg(json_build_object('xmin', s.xmin::text, 'row', row_to_json(s)) order by s.operative_id nulls first, s.account_id, s.definition_id collate "C")::text from item_stacks s),
          (select json_agg(json_build_object('xmin', v.xmin::text, 'row', row_to_json(v)) order by v.account_id)::text from inventories v))
        """);

    static string CodeAndIndex(string body)
    {
        var json = JsonDocument.Parse(body).RootElement;
        return $"{json.GetProperty("code").GetString()}@{json.GetProperty("index").GetInt32()}";
    }

    [Fact]
    public async Task A_guessed_neighbouring_item_or_operative_id_is_the_same_422_as_an_unknown_one()
    {
        var itemsA = await ItemsOfAsync(a);
        var itemsB = await ItemsOfAsync(b);
        var opsA = await OperativesOfAsync(a);
        var opsB = await OperativesOfAsync(b);
        var ghostItem = Base58Id.Format(itemsA.Concat(itemsB).Max() + 1000);
        var ghostOperative = Base58Id.Format(opsA.Concat(opsB).Max() + 1000);
        var snapshot = await InventoriesSnapshotAsync();

        // Each way round: the attacker names the victim's ids (every one, and the ones either side of them) and a nonexistent id.
        foreach (var (token, own, ownItems, ownOps, foreignItems, foreignOps) in new[]
                 {
                     (tokenB, opsB[0], itemsB, opsB, itemsA, opsA),
                     (tokenA, opsA[0], itemsA, opsA, itemsB, opsB),
                 })
        {
            var guessedItems = foreignItems.SelectMany(i => new[] { i - 1, i, i + 1 }).Where(i => i > 0 && !ownItems.Contains(i)).Distinct().Select(Base58Id.Format).ToList();
            var guessedOps = foreignOps.SelectMany(o => new[] { o - 1, o, o + 1 }).Where(o => o > 0 && !ownOps.Contains(o)).Distinct().Select(Base58Id.Format).ToList();
            Assert.Contains(Base58Id.Format(foreignItems[0]), guessedItems);
            Assert.Contains(Base58Id.Format(foreignOps[0]), guessedOps);
            var ownId = Base58Id.Format(own);
            var ownRifle = Base58Id.Format(await RifleOfAsync(own));
            var version = await VersionOfAsync(token == tokenB ? b : a);

            // Item ids, in each place an item id is accepted.
            foreach (var shape in new Func<string, string>[]
                     {
                         item => $"{{\"op\":\"equip\",\"operativeId\":\"{ownId}\",\"itemId\":\"{item}\"}}",
                         item => $"{{\"op\":\"toBag\",\"operativeId\":\"{ownId}\",\"itemId\":\"{item}\"}}",
                         item => $"{{\"op\":\"toStash\",\"operativeId\":\"{ownId}\",\"itemId\":\"{item}\"}}",
                         item => $"{{\"op\":\"equipFromStash\",\"operativeId\":\"{ownId}\",\"itemId\":\"{item}\"}}",
                     })
            {
                var unknown = await SendAsync(Edit(token, Edits(version, shape(ghostItem))));
                Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.Status);
                Assert.Equal("NotFound@0", CodeAndIndex(unknown.Body));
                foreach (var guess in guessedItems)
                    Assert.Equal(unknown, await SendAsync(Edit(token, Edits(version, shape(guess))))); // status and the whole body, byte for byte
            }

            // Operative ids.
            foreach (var shape in new Func<string, string>[]
                     {
                         op => $"{{\"op\":\"equip\",\"operativeId\":\"{op}\",\"itemId\":\"{ownRifle}\"}}",
                         op => $"{{\"op\":\"unequip\",\"operativeId\":\"{op}\",\"slot\":\"weapon\"}}",
                         op => $"{{\"op\":\"toBag\",\"operativeId\":\"{op}\",\"definitionId\":\"item.medkit\",\"quantity\":1}}",
                         op => $"{{\"op\":\"toStash\",\"operativeId\":\"{op}\",\"definitionId\":\"item.medkit\",\"quantity\":1}}",
                     })
            {
                var unknown = await SendAsync(Edit(token, Edits(version, shape(ghostOperative))));
                Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.Status);
                Assert.Equal("UnknownOperative@0", CodeAndIndex(unknown.Body));
                foreach (var guess in guessedOps)
                    Assert.Equal(unknown, await SendAsync(Edit(token, Edits(version, shape(guess)))));
            }
        }

        // Nothing was written for either account, and each owner's own edit still works.
        Assert.Equal(snapshot, await InventoriesSnapshotAsync());
        var own1 = $"{{\"op\":\"equip\",\"operativeId\":\"{Base58Id.Format(operativeB)}\",\"itemId\":\"{Base58Id.Format(await RifleOfAsync(operativeB))}\"}}";
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Edit(tokenB, Edits(1, own1)))).Status);
        Assert.Equal((1L, 2L), (await VersionOfAsync(a), await VersionOfAsync(b)));
    }

    [Fact]
    public async Task An_account_id_anywhere_in_the_request_changes_nothing()
    {
        var idB = AccountId.Format(b.AccountId);
        var opA = Base58Id.Format(operativeA);
        var rifleA = Base58Id.Format(await RifleOfAsync(operativeA));
        var operation = $"{{\"op\":\"equip\",\"operativeId\":\"{opA}\",\"itemId\":\"{rifleA}\"}}";
        var valid = Edits(1, operation);
        const string rowsOfB = "select concat_ws(E'\\n', (select json_agg(row_to_json(i) order by item_id)::text from item_instances i where account_id = @b), (select version::text from inventories where account_id = @b))";
        var snapshotB = await ScalarAsSuperuserAsync<string>(db, rowsOfB, new { b = b.AccountId });

        // A body that names an account is refused outright: no request type has such a member (top level or in an operation).
        foreach (var member in new[] { $"\"accountId\":\"{idB}\"", $"\"account_id\":{b.AccountId}", $"\"AccountId\":\"{idB}\"", "\"accountId\":null" })
        {
            var top = $"{{\"expectedVersion\":1,{member},\"operations\":[{operation}]}}";
            var inner = $"{{\"expectedVersion\":1,\"operations\":[{operation.TrimEnd('}')},{member}}}]}}";
            foreach (var body in new[] { top, inner })
            {
                var (status, text) = await SendAsync(Edit(tokenA, body));
                Assert.Equal(HttpStatusCode.BadRequest, status);
                Assert.Equal("InvalidBody", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
            }
        }
        Assert.Equal((1L, 1L), (await VersionOfAsync(a), await VersionOfAsync(b)));

        // A query or a header naming B's account is ignored: A's own session decides, so A's edit applies to A alone.
        var request = Edit(tokenA, valid, $"/v1/inventory/operations?accountId={idB}&account_id={b.AccountId}&operativeId={Base58Id.Format(operativeB)}");
        request.Headers.Add("X-Account-Id", idB);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(request)).Status);
        Assert.Equal((2L, 1L), (await VersionOfAsync(a), await VersionOfAsync(b)));
        Assert.Equal(snapshotB, await ScalarAsSuperuserAsync<string>(db, rowsOfB, new { b = b.AccountId }));
    }
}
