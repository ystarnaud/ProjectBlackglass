using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Hosts;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Hosts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Join tickets (Stage 8 Task 8; spec §7.5; owner ruling B7): minted only by an authenticated POST for a participant of a
/// ready or running attempt (GET never mints), 256-bit, stored hashed, expiring, revoked by a newer mint, redeemed once by the
/// attempt's own process session (a repeat by that session answers the same), bound to account and attempt.
/// </summary>
[Trait("Requires", "Postgres")]
public class TicketTests : IAsyncLifetime
{
    TestDatabase db = null!;
    HostStore hosts = null!;
    ProcessStore processes = null!;
    TicketStore tickets = null!;
    AdmissionStore admissions = null!;
    AccountAdminStore accounts = null!;
    static readonly GameCatalogue Catalogue = GameCatalogue.Embedded;
    static readonly BuildIdentity Build = new("r1", GameCatalogue.Embedded.Version);

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        hosts = new HostStore(db.AsApp(), leaseSeconds: 30);
        processes = new ProcessStore(db.AsApp());
        tickets = new TicketStore(db.AsApp());
        accounts = new AccountAdminStore(db.AsApp());
        admissions = new AdmissionStore(db.AsApp(), new ServiceHealth(devHostsEnabled: false, leaseSeconds: 30), new CryptoAttemptSecrets(), Catalogue, 60);
        var admin = new HostAdminStore(db.AsApp());
        await admin.AllowBuildAsync(Build, [new TemplateRef("procedural-standard", 1)], Op);
        await admin.RegisterAsync("h1", 8, 7000, Op);
        await hosts.HeartbeatAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), new HeartbeatRequest(Build, true, [], 0), default);
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    sealed record Ready(long Account, Guid Attempt, Guid Session, OpaqueSecret Credential, string[] Operatives);

    /// <summary>A real admission (two operatives), claimed by h1, registered and ready.</summary>
    async Task<Ready> ReadyAsync()
    {
        var issued = await accounts.CreateAccountAsync(null, null, Op);
        var operatives = (await (await db.AsSuperuser().OpenConnectionAsync()).QueryAsync<long>(
            "select operative_id from operatives where account_id = @a order by operative_id desc limit 2", new { a = issued.AccountId })).ToArray();
        var admission = await admissions.AdmitAsync(issued.AccountId, Guid.CreateVersion7(), new DeployRequest("procedural-standard", operatives));
        var attempt = Guid.Parse(JsonDocument.Parse(admission.Body).RootElement.GetProperty("attemptId").GetString()!);
        await hosts.HeartbeatAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), new HeartbeatRequest(Build, true, [], 0), default);
        var claim = Assert.IsType<ClaimResult.Claimed>(await hosts.ClaimAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), Guid.NewGuid(), 120, default));
        Assert.Equal(attempt, claim.AttemptId);
        var session = Assert.IsType<RegisterResult.Registered>(await processes.RegisterAsync(attempt, claim.Credential.Hash(), new RegisterRequest(Build), default)).ProcessSessionId;
        PoolLine[] pool = [new(0, 0, "item.medkit", 1)];
        var table = Catalogue.Document.LootTable;
        Assert.IsType<ReadyResult.Ready>(await processes.ReadyAsync(attempt,
            new ReadyReport(session, "cf2e14dece9cc2a6", table.Version, LootHash.Compute(table.Version, pool), [new Objective("extract", true)], pool), table, Catalogue.Version, 120, default));
        return new Ready(issued.AccountId, attempt, session, claim.Credential, operatives.Select(Base58Id.Format).ToArray());
    }

    Task<MintResult> MintAsync(Ready r) => tickets.MintAsync(r.Account, r.Attempt, 120, default);

    async Task<OpaqueSecret> TicketAsync(Ready r) => Assert.IsType<MintResult.Minted>(await MintAsync(r)).Ticket;

    // ---- mint and redeem ----

    [Fact]
    public async Task A_ticket_redeems_once_and_returns_the_snapshot_operatives_in_order()
    {
        var r = await ReadyAsync();
        var minted = Assert.IsType<MintResult.Minted>(await MintAsync(r));
        Assert.Equal(OpaqueSecret.JoinTicketPrefix, minted.Ticket.Prefix);
        var now = await ScalarAsSuperuserAsync<DateTime>(db, "select clock_timestamp()");
        Assert.InRange(minted.ExpiresAt, now.AddSeconds(110), now.AddSeconds(121));
        Assert.Equal(minted.Ticket.Hash(), await ScalarAsSuperuserAsync<byte[]>(db, "select ticket_hash from join_tickets"));
        var redeemed = Assert.IsType<RedeemResult.Redeemed>(await tickets.RedeemAsync(r.Attempt, r.Session, minted.Ticket.Text, default));
        Assert.Equal(r.Account, redeemed.AccountId);
        Assert.Equal(r.Operatives, redeemed.OperativeIds); // the snapshot's (requested) order
        Assert.Equal(r.Session, await ScalarAsSuperuserAsync<Guid>(db, "select redeemed_by_session from join_tickets"));
    }

    [Fact]
    public async Task A_redeem_replay_by_the_same_session_returns_the_same_answer()
    {
        var r = await ReadyAsync();
        var ticket = await TicketAsync(r);
        var first = await tickets.RedeemAsync(r.Attempt, r.Session, ticket.Text, default);
        var stamp = await ScalarAsSuperuserAsync<DateTime>(db, "select redeemed_at from join_tickets");
        Assert.Equal(first, await tickets.RedeemAsync(r.Attempt, r.Session, ticket.Text, default));
        Assert.Equal(stamp, await ScalarAsSuperuserAsync<DateTime>(db, "select redeemed_at from join_tickets"));
        Assert.IsType<RedeemResult.WrongSession>(await tickets.RedeemAsync(r.Attempt, Guid.CreateVersion7(), ticket.Text, default));
    }

    [Fact]
    public async Task Unknown_malformed_expired_and_revoked_tickets_are_unknown()
    {
        var r = await ReadyAsync();
        foreach (var text in new[] { "jt1_nope", OpaqueSecret.New(OpaqueSecret.JoinTicketPrefix).Text, OpaqueSecret.New(OpaqueSecret.AccessTokenPrefix).Text, "" })
            Assert.IsType<RedeemResult.Unknown>(await tickets.RedeemAsync(r.Attempt, r.Session, text, default));
        var expired = await TicketAsync(r);
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
        await using (var tx = await su.BeginTransactionAsync())
        {
            await su.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
            await su.ExecuteAsync("update join_tickets set issued_at = issued_at - interval '1 hour', expires_at = clock_timestamp() - interval '1 second'", transaction: tx);
            await tx.CommitAsync();
        }
        Assert.IsType<RedeemResult.Unknown>(await tickets.RedeemAsync(r.Attempt, r.Session, expired.Text, default));
        var older = await TicketAsync(r);
        var newer = await TicketAsync(r); // minting revokes the earlier live ticket: at most one live per participant
        Assert.IsType<RedeemResult.Unknown>(await tickets.RedeemAsync(r.Attempt, r.Session, older.Text, default));
        Assert.IsType<RedeemResult.Redeemed>(await tickets.RedeemAsync(r.Attempt, r.Session, newer.Text, default));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from join_tickets where redeemed_at is null and revoked_at is null and expires_at > clock_timestamp()"));
    }

    [Fact]
    public async Task A_ticket_for_another_attempt_is_WrongAttempt()
    {
        var mine = await ReadyAsync();
        var theirs = await ReadyAsync();
        var ticket = await TicketAsync(mine);
        Assert.IsType<RedeemResult.WrongAttempt>(await tickets.RedeemAsync(theirs.Attempt, theirs.Session, ticket.Text, default));
        Assert.Null(await ScalarAsSuperuserAsync<DateTime?>(db, "select redeemed_at from join_tickets"));
    }

    [Fact]
    public async Task Mint_and_redeem_refuse_a_released_participant_and_an_attempt_not_ready_or_running()
    {
        var r = await ReadyAsync();
        var ticket = await TicketAsync(r);
        await new AttemptStore(db.AsApp()).CancelAsync(r.Account, r.Attempt);
        Assert.IsType<MintResult.InvalidState>(await MintAsync(r));
        Assert.IsType<RedeemResult.InvalidState>(await tickets.RedeemAsync(r.Attempt, r.Session, ticket.Text, default));
        // Before ready: nothing to join yet.
        var issued = await accounts.CreateAccountAsync(null, null, Op);
        var operative = await ScalarAsSuperuserAsync<long>(db, "select min(operative_id) from operatives where account_id = @a", new { a = issued.AccountId });
        var admission = await admissions.AdmitAsync(issued.AccountId, Guid.CreateVersion7(), new DeployRequest("procedural-standard", [operative]));
        var waiting = Guid.Parse(JsonDocument.Parse(admission.Body).RootElement.GetProperty("attemptId").GetString()!);
        Assert.IsType<MintResult.InvalidState>(await tickets.MintAsync(issued.AccountId, waiting, 120, default));
        Assert.IsType<MintResult.NotFound>(await tickets.MintAsync(issued.AccountId, r.Attempt, 120, default)); // another account's attempt
        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from join_tickets"));
    }

    [Fact]
    public async Task Twenty_concurrent_redeems_of_one_ticket_redeem_it_once()
    {
        var r = await ReadyAsync();
        var ticket = await TicketAsync(r);
        var start = new TaskCompletionSource();
        var redeems = Enumerable.Range(0, 20).Select(_ => Task.Run(async () => { await start.Task; return await tickets.RedeemAsync(r.Attempt, r.Session, ticket.Text, default); })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(redeems);
        Assert.All(results, x => Assert.Equal(results[0], x));
        Assert.IsType<RedeemResult.Redeemed>(results[0]);
        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from join_tickets where redeemed_at is not null"));
    }

    // ---- HTTP: mint is a POST; GET shows where to join, never a ticket or the seed ----

    [Fact]
    public async Task Tickets_over_http_and_GET_shows_join_host_and_port_but_never_a_ticket_or_seed()
    {
        var r = await ReadyAsync();
        await using var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_DEV_AUTH"] = "enabled" });
        var key = (await accounts.RotateCredentialAsync(r.Account, Op)).AccountKey;
        var login = await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = key.Text });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
        async Task<(HttpStatusCode Status, string Body, string? Cache)> SendAsync(HttpMethod method, string path, string? body, string bearer)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await api.Http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.CacheControl?.ToString());
        }

        var view = await SendAsync(HttpMethod.Get, $"/v1/attempts/{r.Attempt}", null, token);
        Assert.Equal(HttpStatusCode.OK, view.Status);
        var json = JsonDocument.Parse(view.Body).RootElement;
        Assert.Equal(("h1", 7000), (json.GetProperty("join").GetProperty("hostId").GetString(), json.GetProperty("join").GetProperty("port").GetInt32()));
        Assert.True(json.GetProperty("deadlines").TryGetProperty("join", out _));
        var seed = await ScalarAsSuperuserAsync<int>(db, "select seed from attempt_secrets where attempt_id = @a", new { a = r.Attempt });
        Assert.DoesNotContain("jt1_", view.Body);
        Assert.DoesNotContain($":{seed}", view.Body);
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from join_tickets")); // GET minted nothing

        var mint = await SendAsync(HttpMethod.Post, $"/v1/attempts/{r.Attempt}/tickets", "{}", token);
        Assert.Equal((HttpStatusCode.Created, "no-store"), (mint.Status, mint.Cache));
        var minted = JsonDocument.Parse(mint.Body).RootElement;
        Assert.Equal(["ticket", "ticketExpiresAt"], minted.EnumerateObject().Select(p => p.Name));
        var ticket = minted.GetProperty("ticket").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, $"/v1/attempts/{r.Attempt}/tickets", """{"accountId":"2"}""", token)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/v1/attempts/{Guid.CreateVersion7()}/tickets", "{}", token)).Status);

        var redeem = await SendAsync(HttpMethod.Post, $"/server/v1/attempts/{r.Attempt}/tickets/redeem", $$"""{"processSessionId":"{{r.Session}}","ticket":"{{ticket}}"}""", r.Credential.Text);
        Assert.Equal(HttpStatusCode.OK, redeem.Status);
        var redeemed = JsonDocument.Parse(redeem.Body).RootElement;
        Assert.Equal(Base58Id.Format(r.Account), redeemed.GetProperty("accountId").GetString());
        Assert.Equal(r.Operatives, redeemed.GetProperty("operativeIds").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, $"/server/v1/attempts/{r.Attempt}/tickets/redeem",
            $$"""{"processSessionId":"{{r.Session}}","ticket":"{{OpaqueSecret.New(OpaqueSecret.JoinTicketPrefix).Text}}"}""", r.Credential.Text)).Status);
    }
}
