using System.Text;
using Dapper;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Catalogue;
using Blackglass.Accounts.Persistence.Hosts;
using Blackglass.Accounts.Persistence.Player;
using Blackglass.Accounts.Persistence.Settlement;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>The results route's store (plan Task 3; S9-B1-B4, B11-B16, B20-B22): exactly-once settlement of every outcome, its
/// effects, repeats, conflicts, refusals, malformed bodies, the catalogue upgrade during a mission, V16, the post-apply check and
/// the submission cap. The invariants hold after every test.</summary>
[Trait("Requires", "Postgres")]
public class SettlementTests : IAsyncLifetime
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

    Task<ResultOutcome> Submit(ResultBody body, string host = "h1") => ManifestKit.SubmitAsync(store, db, body, host);

    Task<T> Scalar<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    async Task<(string Participant, string Attempt, string Hold, long Version)> StateAsync(ManifestKit.Running r) =>
        await (await db.AsSuperuser().OpenConnectionAsync()).QuerySingleAsync<(string, string, string, long)>("""
            select p.state, a.state, h.state, i.version from attempt_participants p join mission_attempts a using (attempt_id)
            join energy_holds h using (attempt_id, account_id) join inventories i on i.account_id = p.account_id
            where p.attempt_id = @Attempt and p.account_id = @Account
            """, new { r.Attempt, r.Account });

    // ---- each outcome settles once ----

    [Theory]
    [InlineData("success")]
    [InlineData("defeat")]
    [InlineData("abandoned")]
    [InlineData("disconnected")]
    [InlineData("expired")]
    public async Task Each_outcome_settles_once_and_ends_the_attempt(string outcome)
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var before = (await StateAsync(r)).Version;
        Assert.Equal(new ResultOutcome.Settled(outcome), await Submit(ManifestKit.Body(ManifestKit.Manifest(r, outcome))));
        var (participant, attempt, hold, version) = await StateAsync(r);
        Assert.Equal(("settled", "settled", "settled", before + 1), (participant, attempt, hold, version));
        Assert.Equal(1L, await Scalar<long>("select count(*) from energy_ledger where kind = 'settle' and attempt_id = @Attempt and delta = 0", new { r.Attempt }));
        Assert.Equal(outcome, await Scalar<string>("select outcome from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal("absent", await Scalar<string>("select recorded_hash from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
        Assert.True(await Scalar<bool>("select exists (select 1 from anomaly_events where kind = 'result_without_recorded_hash' and attempt_id = @Attempt)", new { r.Attempt }));
        // The participation lock is released by the settlement: an inventory read is no longer InventoryLocked.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
            Assert.False(await new ParticipationLock().IsLockedAsync(conn, tx, r.Account, default));
    }

    [Fact]
    public async Task Success_grants_loot_mints_instances_applies_xp_and_equipment()
    {
        var r = await ManifestKit.StartAsync(kit, db, pool: [new(0, 0, "item.medkit", 2), new(0, 1, "item.light-vest", 1)]);
        var m = ManifestKit.Manifest(r) is var base0 ? base0 with
        {
            Operatives = [base0.Operatives[0] with { Equipment = [.. base0.Operatives[0].Equipment, EquipmentRef.Loot("armor", 0, 1)] }],
        } : null!;
        var xpBefore = await Scalar<int>("select experience from operatives where operative_id = @o", new { o = r.Snapshot.Operatives[0].OperativeId });
        Assert.IsType<ResultOutcome.Settled>(await Submit(ManifestKit.Body(m)));
        var op = r.Snapshot.Operatives[0].OperativeId;
        var vest = await Scalar<long>("select item_id from item_instances where origin = 'settlement' and origin_ref = @o",
            new { o = $"settlement:{r.Attempt}:{Base58Id.Format(r.Account)}:c0:l1:u0" });
        Assert.Equal(("armor", op), await (await db.AsSuperuser().OpenConnectionAsync()).QuerySingleAsync<(string, long)>(
            "select equipped_slot, operative_id from item_instances where item_id = @vest", new { vest }));
        Assert.Equal(4, await Scalar<int>("select quantity from item_stacks where operative_id = @op and definition_id = 'item.medkit'", new { op }));
        Assert.Equal(xpBefore + GameCatalogue.Embedded.Document.Progression.MissionCompletionXp,
            await Scalar<int>("select experience from operatives where operative_id = @op", new { op }));
        var receipt = await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt });
        Assert.Contains($"\"itemIds\":[\"{Base58Id.Format(vest)}\"]", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_stackable_line_of_two_mints_two_items_and_a_replay_mints_none()
    {
        var r = await ManifestKit.StartAsync(kit, db, pool: [new(0, 0, "item.combat-blade", 1), new(1, 0, "item.light-vest", 1)]);
        // A blade line of quantity 2 can't come from the table's ranges, so this case widens the stored pool line directly
        // (written once at ready; the superuser bypasses nothing but the app's grant).
        await ExecuteAsSuperuserAsync(db, "begin; set local session_replication_role = replica; update attempt_loot_pool set quantity = 2 where attempt_id = @Attempt and container_index = 0; update mission_attempts set loot_hash = @hash where attempt_id = @Attempt; commit",
            new { r.Attempt, hash = LootHash.Compute(MissionKit.Table.Version, [new(0, 0, "item.combat-blade", 2), new(1, 0, "item.light-vest", 1)]) });
        var wide = r with { Pool = [new(0, 0, "item.combat-blade", 2), new(1, 0, "item.light-vest", 1)] };
        var body = ManifestKit.Body(ManifestKit.Manifest(wide));
        Assert.IsType<ResultOutcome.Settled>(await Submit(body));
        var refs = (await (await db.AsSuperuser().OpenConnectionAsync()).QueryAsync<string>("select origin_ref from item_instances where origin = 'settlement' and definition_id = 'item.combat-blade' order by 1")).ToList();
        var acc = Base58Id.Format(r.Account);
        Assert.Equal([$"settlement:{r.Attempt}:{acc}:c0:l0:u0", $"settlement:{r.Attempt}:{acc}:c0:l0:u1"], refs);
        Assert.Equal(new ResultOutcome.Duplicate("success"), await Submit(body));
        Assert.Equal(3L, await Scalar<long>("select count(*) from item_instances where origin = 'settlement'"));
    }

    [Fact]
    public async Task Consumption_deletes_an_exactly_consumed_stack_and_decrements_a_partial_one()
    {
        var full = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Settled>(await Submit(ManifestKit.Body(ManifestKit.Manifest(full, "defeat", consume: 2, collectAll: false))));
        Assert.Equal(0L, await Scalar<long>("select count(*) from item_stacks where operative_id = @o", new { o = full.Snapshot.Operatives[0].OperativeId }));
        var partial = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Settled>(await Submit(ManifestKit.Body(ManifestKit.Manifest(partial, "defeat", consume: 1, collectAll: false))));
        Assert.Equal(1, await Scalar<int>("select quantity from item_stacks where operative_id = @o", new { o = partial.Snapshot.Operatives[0].OperativeId }));
    }

    [Fact]
    public async Task An_insured_defeat_refunds_and_retains_to_the_stash_and_stores_the_selection()
    {
        var r = await ManifestKit.StartAsync(kit, db, insured: true);
        var stashBefore = await ManifestKit.InventoryAsync(db, r.Account);
        Assert.IsType<ResultOutcome.Settled>(await Submit(ManifestKit.Body(ManifestKit.Manifest(r, "defeat", consume: 2))));
        // floor(2 × 50 / 100) = 1 medkit refunded; retention (max 2 entries, Armor before Consumable): the vest (0,1), then medkits (0,0) ×2.
        Assert.Equal("[{\"line\": 1, \"units\": 1, \"definitionId\": \"item.light-vest\", \"containerIndex\": 0}, {\"line\": 0, \"units\": 2, \"definitionId\": \"item.medkit\", \"containerIndex\": 0}]",
            await Scalar<string>("select retention_selection::text from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
        var stash = (await ManifestKit.InventoryAsync(db, r.Account)).Where(l => l.StartsWith("stash:", StringComparison.Ordinal)).ToList();
        Assert.Contains("stash:item.light-vest:item:-:insurance", stash);
        Assert.Contains("stash:item.medkit:x7", stash); // 4 starter + 1 refund + 2 retained
        Assert.DoesNotContain(stashBefore, l => l == "stash:item.medkit:x7");
    }

    [Fact]
    public async Task Pool_collections_sum_across_settled_participants()
    {
        // Co-op shaped: another participant of the attempt already collected one medkit from (0,0) (direct SQL stands in for it).
        var r = await ManifestKit.StartAsync(kit, db);
        await ExecuteAsSuperuserAsync(db, "insert into pool_collections (attempt_id, container_index, line, collected) values (@Attempt, 0, 0, 1)", new { r.Attempt });
        var refused = Assert.IsType<ResultOutcome.Quarantined>(await Submit(ManifestKit.Body(ManifestKit.Manifest(r))));
        Assert.Contains("V9.pool_exceeded", refused.Codes);
        await ExecuteAsSuperuserAsync(db, "begin; set local session_replication_role = replica; delete from pool_collections where attempt_id = @Attempt; commit", new { r.Attempt });
    }

    // ---- repeats, conflicts and refusals ----

    [Fact]
    public async Task Repeated_settlement_after_a_committed_result_answers_duplicate_and_changes_nothing()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var m = ManifestKit.Manifest(r);
        var canonical = Encoding.UTF8.GetString(m.ToCanonicalBytes());
        Assert.IsType<ResultOutcome.Settled>(await Submit(ManifestKit.Body(canonical)));
        var receipt = await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt });
        var inventory = await ManifestKit.InventoryAsync(db, r.Account);

        Assert.Equal(new ResultOutcome.Duplicate("success"), await Submit(ManifestKit.Body(canonical)));
        Assert.Equal(2, await Scalar<int>("select receive_count from result_submissions where attempt_id = @Attempt and disposition = 'settled'", new { r.Attempt }));
        var pretty = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(canonical).RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(new ResultOutcome.Duplicate("success"), await Submit(ManifestKit.Body(pretty)));
        Assert.Equal(1L, await Scalar<long>("select count(*) from result_submissions where attempt_id = @Attempt and disposition = 'same_as'", new { r.Attempt }));
        // After an API restart (new stores) and after the process credential ended with the attempt.
        var restarted = ManifestKit.Store(db);
        Assert.Equal(new ResultOutcome.Duplicate("success"), await ManifestKit.SubmitAsync(restarted, db, ManifestKit.Body(canonical)));
        Assert.Equal(new ResultOutcome.Duplicate("success"), await ManifestKit.SubmitAsync(restarted, db, ManifestKit.Body(pretty)));
        Assert.Null(await kit.Processes.AuthenticateAsync(r.Mission.Credential.Hash(), default));
        Assert.Equal(receipt, await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(inventory, await ManifestKit.InventoryAsync(db, r.Account));
        Assert.Equal(1L, await Scalar<long>("select count(*) from energy_ledger where kind = 'settle' and attempt_id = @Attempt", new { r.Attempt }));
    }

    [Fact]
    public async Task A_different_manifest_after_a_settlement_is_a_conflict_and_changes_nothing()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Settled>(await Submit(ManifestKit.Body(ManifestKit.Manifest(r))));
        var receipt = await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt });
        Assert.Equal(new ResultOutcome.Refused("Conflict"), await Submit(ManifestKit.Body(ManifestKit.Manifest(r, "defeat"))));
        Assert.Equal(1L, await Scalar<long>("select count(*) from result_submissions where attempt_id = @Attempt and disposition = 'conflict'", new { r.Attempt }));
        Assert.True(await Scalar<bool>("select exists (select 1 from anomaly_events where kind = 'settlement_conflict' and attempt_id = @Attempt)", new { r.Attempt }));
        Assert.Equal(receipt, await Scalar<string>("select receipt from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
    }

    [Fact]
    public async Task Another_hosts_or_sessions_manifest_is_wrong_attempt()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        await new HostAdminStore(db.AsApp()).RegisterAsync("h2", 2, 7100, Op);
        Assert.Equal(new ResultOutcome.Refused("WrongAttempt"), await Submit(ManifestKit.Body(ManifestKit.Manifest(r)), "h2"));
        Assert.True(await Scalar<bool>("select exists (select 1 from result_submissions where host_id = 'h2' and attempt_id is null and claimed_attempt_id = @Attempt and disposition = 'refused')", new { r.Attempt }));
        Assert.Equal(new ResultOutcome.Refused("WrongAttempt"), await Submit(ManifestKit.Body(ManifestKit.Manifest(r) with { ProcessSessionId = Guid.CreateVersion7() })));
        Assert.Equal("joined", (await StateAsync(r)).Participant);
    }

    [Fact]
    public async Task A_disabled_host_or_a_rotated_secret_is_unauthenticated_with_nothing_written()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var oldSecret = await AttemptFixtures.HostSecretHashAsync(db, "h1");
        await new HostAdminStore(db.AsApp()).RotateSecretAsync("h1", Op);
        Assert.IsType<ResultOutcome.Unauthenticated>(await store.SubmitAsync("h1", oldSecret, ManifestKit.Body(ManifestKit.Manifest(r)), default));
        Assert.Equal(0L, await Scalar<long>("select count(*) from result_submissions"));
        Assert.Equal(0L, await Scalar<long>("select count(*) from anomaly_events where host_id = 'h1'"));
    }

    [Fact]
    public async Task A_revoked_attempt_refuses_the_result_and_stores_no_payload_even_after_rotation()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var admin = new HostAdminStore(db.AsApp());
        await admin.DisableAsync("h1", Op);
        await admin.RotateSecretAsync("h1", Op);
        await admin.SwitchAsync("h1", HostSwitch.Enable, Op);
        var body = ManifestKit.Body(ManifestKit.Manifest(r));
        Assert.Equal(new ResultOutcome.Refused("AttemptRevoked"), await Submit(body));
        Assert.Equal(0L, await Scalar<long>("select count(*) from result_submissions"));
        Assert.Equal(0L, await Scalar<long>("select count(*) from result_submission_payloads"));
        var detail = await Scalar<string>("select detail::text from anomaly_events where kind = 'result_refused_revoked'");
        Assert.DoesNotContain("operatives", detail, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body.Raw!)), detail, StringComparison.Ordinal);
        Assert.Equal("joined", (await StateAsync(r)).Participant);
    }

    [Fact]
    public async Task A_released_or_unstarted_participant_is_invalid_state()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        await ExecuteAsSuperuserAsync(db, "begin; set local session_replication_role = replica; update attempt_participants set state = 'admitted', started_at = null where attempt_id = @Attempt; commit", new { r.Attempt });
        Assert.Equal(new ResultOutcome.Refused("InvalidState"), await Submit(ManifestKit.Body(ManifestKit.Manifest(r))));
        Assert.Equal("refused", await Scalar<string>("select disposition from result_submissions where attempt_id = @Attempt", new { r.Attempt }));
        await ExecuteAsSuperuserAsync(db, "begin; set local session_replication_role = replica; update attempt_participants set state = 'joined', started_at = now() where attempt_id = @Attempt; commit", new { r.Attempt });
    }

    // ---- malformed bodies ----

    [Fact]
    public async Task A_malformed_body_naming_a_started_participant_quarantines_it_and_an_unidentified_one_is_stored_once()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var canonical = Encoding.UTF8.GetString(ManifestKit.Manifest(r).ToCanonicalBytes());
        var bad = canonical.Replace("\"outcome\":\"success\"", "\"outcome\":\"victory\"", StringComparison.Ordinal);
        Assert.Equal(new ResultOutcome.Malformed("BadEnum"), await Submit(ManifestKit.Body(bad)));
        Assert.Equal(("quarantined", "malformed_manifest"), await (await db.AsSuperuser().OpenConnectionAsync()).QuerySingleAsync<(string, string)>(
            "select state, quarantine_reason from attempt_participants where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(new ResultOutcome.Malformed("NotJson"), await Submit(ManifestKit.Body("not json")));
        Assert.Equal(new ResultOutcome.Malformed("NotJson"), await Submit(ManifestKit.Body("not json")));
        Assert.Equal(2, await Scalar<int>("select receive_count from result_submissions where attempt_id is null and disposition = 'malformed'"));
        Assert.Equal(new ResultOutcome.Malformed("TooLarge"), await Submit(ResultBody.Oversized(70_000)));
        Assert.True(await Scalar<bool>("select raw_too_large and raw_hash is null and raw_length = 70000 from result_submissions where raw_too_large"));
        Assert.Null(await Scalar<byte[]?>("select raw_bytes from result_submission_payloads x join result_submissions s using (submission_id) where s.raw_too_large"));
    }

    // ---- catalogue upgrade during a mission ----

    [Fact]
    public async Task An_admission_under_catalogue_x_settles_under_x_after_the_api_moves_to_a_tighter_y()
    {
        var r = await ManifestKit.StartAsync(kit, db, pool: [new(0, 0, "item.medkit", 2), new(0, 1, "item.servo-boots", 1)]);
        // The API restarts on Y: a 6-entry bag, medkits stacking to 2, servo boots retired, 90 completion XP.
        var y = CatalogueHistoryTests.Retiring();
        await new CatalogueHistoryStore(db.AsApp()).RegisterAsync(y, null, default);
        var restarted = ManifestKit.Store(db);
        var op = r.Snapshot.Operatives[0].OperativeId;
        var xp = await Scalar<int>("select experience from operatives where operative_id = @op", new { op });
        Assert.IsType<ResultOutcome.Settled>(await ManifestKit.SubmitAsync(restarted, db, ManifestKit.Body(ManifestKit.Manifest(r))));
        Assert.Equal(GameCatalogue.Embedded.Version, await Scalar<string>("select catalogue_version from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(xp + GameCatalogue.Embedded.Document.Progression.MissionCompletionXp, await Scalar<int>("select experience from operatives where operative_id = @op", new { op }));
        Assert.Equal(1L, await Scalar<long>("select count(*) from item_instances where definition_id = 'item.servo-boots' and operative_id = @op", new { op }));
        // The bag (rifle, boots, 4 medkits) is over Y's limits for medkit stacks; Stage 5's move of the retired boots to the stash still works under Y.
        var edits = new InventoryEditStore(db.AsApp(), new ParticipationLock(), y);
        var version = await Scalar<long>("select version from inventories where account_id = @a", new { a = r.Account });
        var boots = await Scalar<long>("select item_id from item_instances where definition_id = 'item.servo-boots' and operative_id = @op", new { op });
        var (after, _) = await edits.ApplyAsync(r.Account, version, [new MoveInstance(op, boots, Destination.Stash)], default);
        Assert.Equal(version + 1, after);
    }

    [Fact]
    public async Task An_attempt_whose_admission_catalogue_is_missing_is_held_and_its_result_preserved()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var missing = "sha256:" + new string('c', 64);
        await ExecuteAsSuperuserAsync(db, """
            insert into allowed_builds (build_revision, catalogue_version) values ('r1', @missing);
            insert into allowed_build_templates (build_revision, catalogue_version, template_id, template_version) values ('r1', @missing, 'procedural-standard', 1);
            begin; set local session_replication_role = replica; update mission_attempts set catalogue_version = @missing where attempt_id = @Attempt; commit;
            """, new { missing, r.Attempt });
        var m = ManifestKit.Manifest(r) with { CatalogueVersion = missing };
        Assert.IsType<ResultOutcome.Held>(await Submit(ManifestKit.Body(m)));
        Assert.Equal(("quarantined", "catalogue_unavailable"), await (await db.AsSuperuser().OpenConnectionAsync()).QuerySingleAsync<(string, string)>(
            "select state, quarantine_reason from attempt_participants where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal("held", await Scalar<string>("select disposition from result_submissions where attempt_id = @Attempt", new { r.Attempt }));
        Assert.True(await Scalar<bool>("select exists (select 1 from anomaly_events where kind = 'catalogue_unavailable' and attempt_id = @Attempt)", new { r.Attempt }));
        await ExecuteAsSuperuserAsync(db, "begin; set local session_replication_role = replica; update mission_attempts set catalogue_version = @v where attempt_id = @Attempt; commit",
            new { v = GameCatalogue.Embedded.Version, r.Attempt }); // keep the invariants about this attempt's build meaningful
    }

    // ---- V16, the post-apply check and the cap ----

    [Fact]
    public async Task A_changed_inventory_base_quarantines()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        await ExecuteAsSuperuserAsync(db, "update inventories set version = version + 1 where account_id = @Account", new { r.Account });
        var q = Assert.IsType<ResultOutcome.Quarantined>(await Submit(ManifestKit.Body(ManifestKit.Manifest(r))));
        Assert.Equal(["V16.inventory_base"], q.Codes);
        Assert.Equal("inventory_base_changed", await Scalar<string>("select quarantine_reason from attempt_participants where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal("charged", (await StateAsync(r)).Hold);
    }

    [Fact]
    public async Task A_post_apply_bag_mismatch_rolls_back_and_quarantines()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var inventory = await ManifestKit.InventoryAsync(db, r.Account);
        store.PlanFilterForTests = p => p with { Credits = [.. p.Credits, new StackCredit(r.Snapshot.Operatives[0].OperativeId, "item.medkit", 1)] };
        var q = Assert.IsType<ResultOutcome.Quarantined>(await Submit(ManifestKit.Body(ManifestKit.Manifest(r))));
        Assert.Equal(["settlement_check_failed"], q.Codes);
        Assert.Equal(inventory, await ManifestKit.InventoryAsync(db, r.Account));
        Assert.Equal(("quarantined", "charged"), ((await StateAsync(r)).Participant, (await StateAsync(r)).Hold));
        Assert.Equal(0L, await Scalar<long>("select count(*) from participant_settlements"));
    }

    [Fact]
    public async Task The_seventeenth_distinct_body_hits_the_cap_but_an_identical_retry_still_answers()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var capped = ManifestKit.Store(db, SettlementOptions.Default with { SubmissionsPerParticipant = 3 });
        var first = ManifestKit.Manifest(r) with { OutcomeSequence = 1, Integrity = "ledgerMismatch" };
        Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(capped, db, ManifestKit.Body(first)));
        for (var i = 2; i <= 3; i++)
            Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(capped, db, ManifestKit.Body(first with { OutcomeSequence = i })));
        Assert.Equal(new ResultOutcome.Refused("SubmissionLimit"), await ManifestKit.SubmitAsync(capped, db, ManifestKit.Body(first with { OutcomeSequence = 4 })));
        Assert.Equal(3L, await Scalar<long>("select count(*) from result_submissions where attempt_id = @Attempt", new { r.Attempt }));
        Assert.True(await Scalar<bool>("select exists (select 1 from anomaly_events where kind = 'submission_limit' and attempt_id = @Attempt)", new { r.Attempt }));
        Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(capped, db, ManifestKit.Body(first)));
        Assert.Equal(16, SettlementOptions.Default.SubmissionsPerParticipant);
    }
}
