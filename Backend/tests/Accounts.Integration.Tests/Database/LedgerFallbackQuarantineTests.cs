using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Settlement;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Stage 11, owner ruling D2: the mission process's fallback manifest (the admission snapshot restated, nothing collected or
/// consumed, integrity "ledgerMismatch") is quarantined by the settlement store with exactly V14 and changes nothing: no
/// settlement, the inventory and its version untouched, the energy hold still charged (no refund). An operator decides.
/// </summary>
[Trait("Requires", "Postgres")]
public class LedgerFallbackQuarantineTests : IAsyncLifetime
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

    [Fact]
    public async Task A_snapshot_restating_ledger_mismatch_manifest_is_quarantined_with_no_effect()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        var inventoryBefore = await ManifestKit.InventoryAsync(db, r.Account);
        var versionBefore = await ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @Account", new { r.Account });
        var fallback = ManifestKit.Manifest(r, outcome: "defeat", collectAll: false) with { Integrity = "ledgerMismatch" };

        var answer = Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(fallback)));

        Assert.Equal(["V14.integrity"], answer.Codes);
        var participant = await RowAsSuperuserAsync<(string State, string Reason)>(db,
            "select state, quarantine_reason from attempt_participants where attempt_id = @Attempt and account_id = @Account", new { r.Attempt, r.Account });
        Assert.Equal(("quarantined", "invalid_manifest"), participant);
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from participant_settlements where attempt_id = @Attempt", new { r.Attempt }));
        Assert.Equal(inventoryBefore, await ManifestKit.InventoryAsync(db, r.Account));
        Assert.Equal(versionBefore, await ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @Account", new { r.Account }));
        Assert.Equal("charged", await ScalarAsSuperuserAsync<string>(db,
            "select state from energy_holds where attempt_id = @Attempt and account_id = @Account", new { r.Attempt, r.Account }));
    }
}
