using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Hosts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>POST /agent/v1/results over HTTP (plan §C, S9-B19 option (a)) and GET /v1/attempts/{id}/receipt: each row's status
/// and minimal body, the record committed before it (or nothing, for 401 and 503), and no echo of the body.</summary>
[Trait("Requires", "Postgres")]
public class ResultResponseTests : IAsyncLifetime
{
    TestDatabase db = null!;
    MissionKit kit = null!;
    ApiServer api = null!;
    string hostSecret = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        kit = await MissionKit.CreateAsync(db);
        api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_RESULT_SUBMISSIONS_MAX"] = "3", ["BLACKGLASS_DB_LOCK_TIMEOUT_MS"] = "500", ["BLACKGLASS_DEV_AUTH"] = "enabled" });
        hostSecret = (await new HostAdminStore(db.AsApp()).RotateSecretAsync("h1", Op)).Secret.Text;
    }

    public async Task DisposeAsync()
    {
        await api.DisposeAsync();
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> PostAsync(string body, string? secret = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent/v1/results") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret ?? hostSecret);
        var response = await api.Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    static string Canonical(Blackglass.Accounts.Domain.Settlement.ResultManifest m) => Encoding.UTF8.GetString(m.ToCanonicalBytes());

    Task<long> SubmissionsAsync() => ScalarAsSuperuserAsync<long>(db, "select count(*) from result_submissions");

    static void NoEcho(string response, ManifestKit.Running r)
    {
        Assert.DoesNotContain(Base58Id.Format(r.Snapshot.Operatives[0].OperativeId), response, StringComparison.Ordinal);
        Assert.DoesNotContain("layoutHash", response, StringComparison.Ordinal);
        Assert.DoesNotContain(r.LayoutHash, response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settled_then_duplicate_then_conflict()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var body = Canonical(ManifestKit.Manifest(r));
        var settled = await PostAsync(body);
        Assert.Equal((HttpStatusCode.OK, "{\"disposition\":\"settled\",\"outcome\":\"success\"}"), (settled.Status, settled.Body));
        Assert.Equal("no-store", settled.Response.Headers.CacheControl?.ToString());
        var duplicate = await PostAsync(body);
        Assert.Equal((HttpStatusCode.OK, "{\"disposition\":\"duplicate\",\"outcome\":\"success\"}"), (duplicate.Status, duplicate.Body));
        var conflict = await PostAsync(Canonical(ManifestKit.Manifest(r, "defeat")));
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Contains("\"code\":\"Conflict\"", conflict.Body);
        foreach (var answer in new[] { settled.Body, duplicate.Body, conflict.Body }) NoEcho(answer, r);
    }

    [Fact]
    public async Task Quarantined_and_held_are_202_with_codes_and_never_resent()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var q = await PostAsync(Canonical(ManifestKit.Manifest(r) with { Integrity = "ledgerMismatch" }));
        Assert.Equal((HttpStatusCode.Accepted, "{\"disposition\":\"quarantined\",\"codes\":[\"V14.integrity\"]}"), (q.Status, q.Body));
        Assert.Equal(1L, await SubmissionsAsync());

        var h = await ManifestKit.StartAsync(kit, db);
        var missing = "sha256:" + new string('c', 64);
        await ExecuteAsSuperuserAsync(db, """
            insert into allowed_builds (build_revision, catalogue_version) values ('r1', @missing);
            insert into allowed_build_templates (build_revision, catalogue_version, template_id, template_version) values ('r1', @missing, 'procedural-standard', 1);
            begin; set local session_replication_role = replica; update mission_attempts set catalogue_version = @missing where attempt_id = @Attempt; commit;
            """, new { missing, h.Attempt });
        var held = await PostAsync(Canonical(ManifestKit.Manifest(h) with { CatalogueVersion = missing }));
        Assert.Equal((HttpStatusCode.Accepted, "{\"disposition\":\"held\"}"), (held.Status, held.Body));
        await ExecuteAsSuperuserAsync(db, "begin; set local session_replication_role = replica; update mission_attempts set catalogue_version = @v where attempt_id = @Attempt; commit",
            new { v = MissionKit.Catalogue.Version, h.Attempt });
    }

    [Fact]
    public async Task Refusals_are_409_with_their_code_after_their_record()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var wrong = await PostAsync(Canonical(ManifestKit.Manifest(r) with { ProcessSessionId = Guid.CreateVersion7() }));
        Assert.Equal(HttpStatusCode.Conflict, wrong.Status);
        Assert.Contains("\"code\":\"WrongAttempt\"", wrong.Body);
        Assert.Equal(1L, await SubmissionsAsync());
        // The cap (3 here): the third distinct body is stored, the fourth is refused with only the anomaly.
        var m = ManifestKit.Manifest(r) with { Integrity = "ledgerMismatch" };
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(Canonical(m with { OutcomeSequence = 2 }))).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(Canonical(m with { OutcomeSequence = 3 }))).Status);
        var capped = await PostAsync(Canonical(m with { OutcomeSequence = 4 }));
        Assert.Equal(HttpStatusCode.Conflict, capped.Status);
        Assert.Contains("\"code\":\"SubmissionLimit\"", capped.Body);
        Assert.Equal(3L, await SubmissionsAsync());
    }

    [Fact]
    public async Task A_revoked_attempt_is_409_attempt_revoked_with_no_submission_stored()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var admin = new HostAdminStore(db.AsApp());
        await admin.DisableAsync("h1", Op);
        var rotated = (await admin.RotateSecretAsync("h1", Op)).Secret.Text;
        await admin.SwitchAsync("h1", HostSwitch.Enable, Op);
        var refused = await PostAsync(Canonical(ManifestKit.Manifest(r)), rotated);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("\"code\":\"AttemptRevoked\"", refused.Body);
        Assert.Equal(0L, await SubmissionsAsync());
        hostSecret = rotated;
    }

    [Fact]
    public async Task A_body_that_is_not_a_manifest_is_422_with_its_code_and_stored()
    {
        var (status, body, _) = await PostAsync("{\"formatVersion\":1}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("\"code\":\"MissingMember\"", body);
        var empty = await PostAsync("");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.Status);
        Assert.Contains("\"code\":\"NotJson\"", empty.Body);
        var large = await PostAsync(new string(' ', 70_000));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, large.Status);
        Assert.Contains("\"code\":\"TooLarge\"", large.Body);
        Assert.Equal(3L, await SubmissionsAsync());
    }

    [Fact]
    public async Task An_unknown_secret_is_the_uniform_401_with_nothing_written()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var (status, _, response) = await PostAsync(Canonical(ManifestKit.Manifest(r)), "hs1_" + new string('A', 43));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(0L, await SubmissionsAsync());
        _ = response;
    }

    [Fact]
    public async Task A_database_outage_or_lock_timeout_is_503_with_retry_after_and_nothing_committed()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var body = Canonical(ManifestKit.Manifest(r));
        // Busy: another transaction holds the attempt row past the lock timeout (500 ms here).
        await using (var conn = await db.AsSuperuser().OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync("select 1 from mission_attempts where attempt_id = @Attempt for update", new { r.Attempt }, tx);
            var busy = await PostAsync(body);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.Status);
            Assert.True(busy.Response.Headers.Contains("Retry-After"));
        }
        Assert.Equal(0L, await SubmissionsAsync());
        // Unavailable: the database refuses connections.
        await ApiServer.SetDatabaseReachableAsync(db, false);
        try
        {
            var down = await PostAsync(body);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.Status);
        }
        finally { await ApiServer.SetDatabaseReachableAsync(db, true); }
        Assert.Equal(0L, await SubmissionsAsync());
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(body)).Status); // and the retry settles once
    }

    // ---- receipts ----

    async Task<(HttpStatusCode Status, string Body)> ReceiptAsync(string token, string attempt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/attempts/{attempt}/receipt");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await api.Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    async Task<string> LoginAsync(string key) =>
        JsonDocument.Parse(await (await api.Http.PostAsJsonAsync("/v1/auth/dev/login", new { accountKey = key })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;

    [Fact]
    public async Task A_receipt_is_pending_until_settled_then_byte_identical_and_private()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var token = await LoginAsync(r.AccountKey);
        Assert.Equal((HttpStatusCode.Accepted, "{\"state\":\"running\"}"), await ReceiptAsync(token, r.Attempt.ToString()));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Canonical(ManifestKit.Manifest(r)))).Status);
        var first = await ReceiptAsync(token, r.Attempt.ToString());
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(await ScalarAsSuperuserAsync<string>(db, "select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt }), first.Body);
        Assert.Equal(first, await ReceiptAsync(token, r.Attempt.ToString()));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Canonical(ManifestKit.Manifest(r)))).Status); // a duplicate relay
        Assert.Equal(first, await ReceiptAsync(token, r.Attempt.ToString()));
        await using (var restarted = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_DEV_AUTH"] = "enabled" }))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/attempts/{r.Attempt}/receipt");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(first.Body, await (await restarted.Http.SendAsync(request)).Content.ReadAsStringAsync());
        }
        foreach (var secret in new[] { "seed", "salt", "retention", "hs1_", "pc1_", "layoutHash" })
            Assert.DoesNotContain(secret, first.Body, StringComparison.OrdinalIgnoreCase);

        var other = await ManifestKit.StartAsync(kit, db);
        var otherToken = await LoginAsync(other.AccountKey);
        Assert.Equal(HttpStatusCode.NotFound, (await ReceiptAsync(otherToken, r.Attempt.ToString())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ReceiptAsync(token, r.Attempt.ToString().ToUpperInvariant())).Status);
    }

    [Fact]
    public async Task A_quarantined_or_revoked_participant_reads_under_review_and_a_released_one_has_no_receipt()
    {
        var q = await ManifestKit.StartAsync(kit, db);
        await PostAsync(Canonical(ManifestKit.Manifest(q) with { Integrity = "ledgerMismatch" }));
        Assert.Equal((HttpStatusCode.Accepted, "{\"state\":\"under_review\"}"), await ReceiptAsync(await LoginAsync(q.AccountKey), q.Attempt.ToString()));

        var released = await kit.Accounts.CreateAccountAsync(null, null, Op);
        var operative = await ScalarAsSuperuserAsync<long>(db, "select min(operative_id) from operatives where account_id = @a", new { a = released.AccountId });
        var admission = await kit.Admissions.AdmitAsync(released.AccountId, Guid.CreateVersion7(), new Domain.Attempts.DeployRequest("procedural-standard", [operative]));
        var attempt = Guid.Parse(JsonDocument.Parse(admission.Body).RootElement.GetProperty("attemptId").GetString()!);
        await new Persistence.Attempts.AttemptStore(db.AsApp(), 30).CancelAsync(released.AccountId, attempt);
        var none = await ReceiptAsync(await LoginAsync(released.AccountKey.Text), attempt.ToString());
        Assert.Equal(HttpStatusCode.Conflict, none.Status);
        Assert.Contains("\"code\":\"NoReceipt\"", none.Body);

        var revoked = await ManifestKit.StartAsync(kit, db);
        await new HostAdminStore(db.AsApp()).DisableAsync("h1", Op);
        Assert.Equal((HttpStatusCode.Accepted, "{\"state\":\"under_review\"}"), await ReceiptAsync(await LoginAsync(revoked.AccountKey), revoked.Attempt.ToString()));
    }
}
