using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Dapper;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Hosts;
using Blackglass.Accounts.Persistence.Settlement;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>The process's recorded hash (S9-B5; revision 3 B23, T0-1, T0-2) and quarantine (S9-B21): supporting evidence, never a
/// prerequisite; a mismatch quarantines an unsettled participant or alerts after a settlement; a revoked credential never
/// authenticates; a quarantine is sticky.</summary>
[Trait("Requires", "Postgres")]
public class RecordedTests : IAsyncLifetime
{
    TestDatabase db = null!;
    MissionKit kit = null!;
    SettlementStore store = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        kit = await MissionKit.CreateAsync(db);
        store = ManifestKit.Store(db);
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    static string HashOf(ResultManifest m) => ResultManifest.HashOf(m.ToCanonicalBytes());

    Task<RecordedOutcome> Recorded(ManifestKit.Running r, string hash) =>
        store.RecordedAsync(r.Attempt, r.Account, r.Mission.Credential.Hash(), hash, default);

    Task<ResultOutcome> Relay(ResultManifest m) => ManifestKit.SubmitAsync(store, db, ManifestKit.Body(m));

    async Task<(string State, string? Reason, string? Recorded)> ParticipantAsync(ManifestKit.Running r) =>
        await (await db.AsSuperuser().OpenConnectionAsync()).QuerySingleAsync<(string, string?, string?)>(
            "select state, quarantine_reason, recorded_manifest_hash from attempt_participants where attempt_id = @Attempt", new { r.Attempt });

    [Fact]
    public async Task A_recorded_hash_moves_the_participant_to_result_pending_and_a_repeat_is_accepted()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var hash = HashOf(ManifestKit.Manifest(r));
        Assert.Equal(new RecordedOutcome.Accepted("result_pending"), await Recorded(r, hash));
        Assert.Equal(("result_pending", null, hash), await ParticipantAsync(r));
        Assert.Equal("ending", await ScalarAsSuperuserAsync<string>(db, "select state from mission_attempts where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(new RecordedOutcome.Accepted("result_pending"), await Recorded(r, hash));
        // The relay matches: settled with recorded_hash 'matched'.
        Assert.IsType<ResultOutcome.Settled>(await Relay(ManifestKit.Manifest(r)));
        Assert.Equal("matched", await ScalarAsSuperuserAsync<string>(db, "select recorded_hash from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
    }

    [Fact]
    public async Task A_different_recorded_hash_is_conflicting_raises_the_anomaly_and_quarantines()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<RecordedOutcome.Accepted>(await Recorded(r, HashOf(ManifestKit.Manifest(r))));
        Assert.IsType<RecordedOutcome.Conflicting>(await Recorded(r, "sha256:" + new string('d', 64)));
        Assert.Equal("quarantined", (await ParticipantAsync(r)).State);
        Assert.Equal("recorded_hash_mismatch", (await ParticipantAsync(r)).Reason);
        Assert.True(await ScalarAsSuperuserAsync<bool>(db, "select exists (select 1 from anomaly_events where kind = 'recorded_hash_mismatch' and attempt_id = @Attempt)", new { r.Attempt }));
    }

    [Fact]
    public async Task A_relay_that_differs_from_the_recorded_hash_quarantines()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<RecordedOutcome.Accepted>(await Recorded(r, HashOf(ManifestKit.Manifest(r, "defeat"))));
        var q = Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(r)));
        Assert.Equal(["V15.recorded_hash"], q.Codes);
        Assert.Equal(("quarantined", "recorded_hash_mismatch"), ((await ParticipantAsync(r)).State, (await ParticipantAsync(r)).Reason));
    }

    [Fact]
    public async Task A_relay_before_any_recorded_call_settles_and_a_late_contradicting_hash_only_alerts()
    {
        // B23: the backend was unreachable for the process's notification, so the agent relayed the published manifest first.
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Settled>(await Relay(ManifestKit.Manifest(r)));
        var receipt = await ScalarAsSuperuserAsync<string>(db, "select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt });
        // The attempt is settled, yet the same credential still authenticates for this comparison (T0-1).
        Assert.Equal(new RecordedOutcome.Accepted("settled"), await Recorded(r, HashOf(ManifestKit.Manifest(r))));
        Assert.IsType<RecordedOutcome.Conflicting>(await Recorded(r, HashOf(ManifestKit.Manifest(r, "defeat"))));
        Assert.True(await ScalarAsSuperuserAsync<bool>(db, "select exists (select 1 from anomaly_events where kind = 'recorded_hash_mismatch' and attempt_id = @Attempt)", new { r.Attempt }));
        Assert.Equal(receipt, await ScalarAsSuperuserAsync<string>(db, "select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal("settled", (await ParticipantAsync(r)).State);
    }

    [Fact]
    public async Task A_quarantined_participants_later_contradicting_hash_stays_quarantined_and_alerts()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(r) with { Integrity = "ledgerMismatch" }));
        Assert.IsType<RecordedOutcome.Conflicting>(await Recorded(r, HashOf(ManifestKit.Manifest(r))));
        Assert.Equal("quarantined", (await ParticipantAsync(r)).State);
    }

    [Fact]
    public async Task A_revoked_or_superseded_credential_is_unauthenticated()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<RecordedOutcome.Unauthenticated>(await store.RecordedAsync(r.Attempt, r.Account, new byte[32], HashOf(ManifestKit.Manifest(r)), default));
        await new HostAdminStore(db.AsApp()).DisableAsync("h1", Op);
        Assert.IsType<RecordedOutcome.Unauthenticated>(await Recorded(r, HashOf(ManifestKit.Manifest(r))));
        Assert.Null((await ParticipantAsync(r)).Recorded);
    }

    [Fact]
    public async Task An_unstarted_participant_is_invalid_state()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        await ExecuteAsSuperuserAsync(db, "begin; set local session_replication_role = replica; update attempt_participants set state = 'admitted', started_at = null where attempt_id = @Attempt; commit", new { r.Attempt });
        Assert.IsType<RecordedOutcome.InvalidState>(await Recorded(r, HashOf(ManifestKit.Manifest(r))));
        await ExecuteAsSuperuserAsync(db, "begin; set local session_replication_role = replica; update attempt_participants set state = 'joined', started_at = now() where attempt_id = @Attempt; commit", new { r.Attempt });
    }

    [Fact]
    public async Task Over_http_the_route_accepts_a_settled_attempts_credential_but_never_a_revoked_one()
    {
        await using var api = await ApiServer.StartAsync(db);
        var r = await ManifestKit.StartAsync(kit, db);
        async Task<HttpStatusCode> Post(string hash)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/server/v1/attempts/{r.Attempt}/participants/{Base58Id.Format(r.Account)}/recorded")
            { Content = new StringContent($"{{\"manifestHash\":\"{hash}\"}}", Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", r.Mission.Credential.Text);
            return (await api.Http.SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.OK, await Post(HashOf(ManifestKit.Manifest(r))));
        Assert.IsType<ResultOutcome.Settled>(await Relay(ManifestKit.Manifest(r)));
        Assert.Equal(HttpStatusCode.OK, await Post(HashOf(ManifestKit.Manifest(r))));
        Assert.Equal(HttpStatusCode.Conflict, await Post(HashOf(ManifestKit.Manifest(r, "defeat"))));
        // Other process routes still refuse a settled attempt's credential (R4).
        using (var hb = new HttpRequestMessage(HttpMethod.Post, $"/server/v1/attempts/{r.Attempt}/heartbeat")
               { Content = new StringContent($"{{\"processSessionId\":\"{r.Mission.Session}\",\"phase\":\"lobby\",\"participants\":[]}}", Encoding.UTF8, "application/json") })
        {
            hb.Headers.Authorization = new AuthenticationHeaderValue("Bearer", r.Mission.Credential.Text);
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(hb)).StatusCode);
        }
        var revoked = await ManifestKit.StartAsync(kit, db);
        await new HostAdminStore(db.AsApp()).DisableAsync("h1", Op);
        using var late = new HttpRequestMessage(HttpMethod.Post, $"/server/v1/attempts/{revoked.Attempt}/participants/{Base58Id.Format(revoked.Account)}/recorded")
        { Content = new StringContent($"{{\"manifestHash\":\"{HashOf(ManifestKit.Manifest(revoked))}\"}}", Encoding.UTF8, "application/json") };
        late.Headers.Authorization = new AuthenticationHeaderValue("Bearer", revoked.Mission.Credential.Text);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Http.SendAsync(late)).StatusCode);
    }

    // ---- quarantine (S9-B21) ----

    [Fact]
    public async Task A_quarantine_keeps_everything_and_is_sticky_even_for_a_valid_later_manifest()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var inventory = await ManifestKit.InventoryAsync(db, r.Account);
        var balance = await ScalarAsSuperuserAsync<int>(db, "select balance from energy_wallets where account_id = @Account", new { r.Account });
        var bad = ManifestKit.Manifest(r) with { BuildRevision = "r9" };
        var q = Assert.IsType<ResultOutcome.Quarantined>(await Relay(bad));
        Assert.Equal(["V4.build"], q.Codes);
        var (escalation, raw, canonical) = await (await db.AsSuperuser().OpenConnectionAsync()).QuerySingleAsync<(bool, byte[], byte[])>("""
            select p.escalate_at between p.quarantined_at + interval '23 hours 59 minutes' and p.quarantined_at + interval '24 hours 1 minute', x.raw_bytes, x.canonical_bytes
            from attempt_participants p join result_submissions s using (attempt_id, account_id) join result_submission_payloads x using (submission_id)
            where p.attempt_id = @Attempt
            """, new { r.Attempt });
        Assert.True(escalation);
        Assert.Equal(bad.ToCanonicalBytes(), raw);
        Assert.Equal(bad.ToCanonicalBytes(), canonical);
        Assert.True(await ScalarAsSuperuserAsync<bool>(db, "select exists (select 1 from anomaly_events where kind = 'result_quarantined' and attempt_id = @Attempt)", new { r.Attempt }));

        // A later, fully valid manifest is stored but settles nothing; a second invalid one adds a row; earlier rows are unchanged.
        var sticky = Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(r)));
        Assert.Equal(["quarantine_active"], sticky.Codes);
        Assert.IsType<ResultOutcome.Quarantined>(await Relay(ManifestKit.Manifest(r) with { Integrity = "ledgerMismatch" }));
        Assert.Equal(3L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from result_submissions where attempt_id = @Attempt and disposition = 'quarantined'", new { r.Attempt }));
        Assert.Equal(bad.ToCanonicalBytes(), await ScalarAsSuperuserAsync<byte[]>(db,
            "select x.raw_bytes from result_submission_payloads x join result_submissions s using (submission_id) where s.attempt_id = @Attempt order by s.submission_id limit 1", new { r.Attempt }));
        Assert.Equal(("quarantined", "invalid_manifest"), ((await ParticipantAsync(r)).State, (await ParticipantAsync(r)).Reason));
        Assert.Equal(inventory, await ManifestKit.InventoryAsync(db, r.Account));
        Assert.Equal(balance, await ScalarAsSuperuserAsync<int>(db, "select balance from energy_wallets where account_id = @Account", new { r.Account }));
        Assert.Equal("charged", await ScalarAsSuperuserAsync<string>(db, "select state from energy_holds where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from participant_settlements"));
    }
}
