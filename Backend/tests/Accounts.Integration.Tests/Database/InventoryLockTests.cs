using Dapper;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Migrations;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The IInventoryLock seam (owner ruling S5-D5): under the inventories row lock the refusal order is NotProvisioned,
/// InventoryLocked, VersionMismatch, then the operation; a locked pick writes nothing; the inventory read reports Locked
/// from the same seam, inside its snapshot. Stage 5 ships NoParticipationsYet, which never locks.
/// </summary>
[Trait("Requires", "Postgres")]
public class InventoryLockTests : IAsyncLifetime
{
    TestDatabase db = null!;
    AccountAdminStore admin = null!;

    public async Task InitializeAsync() { db = await MigratedAsync(); admin = new AccountAdminStore(db.AsApp()); }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    /// <summary>A provisioned account and its first operative, who has a pick to spend (100 xp).</summary>
    async Task<(long Account, long Operative)> AccountWithPickAsync()
    {
        var account = (await admin.CreateAccountAsync(null, null, Op)).AccountId;
        var operative = await ScalarAsync<long>("select min(operative_id) from operatives where account_id = @account", new { account });
        await ExecuteAsSuperuserAsync(db, "update operatives set experience = 100 where operative_id = @operative", new { operative });
        return (account, operative);
    }

    static async Task<PlayerError> RefusalAsync(Task<(long Version, OperativeView Operative)> pick) =>
        (await Assert.ThrowsAsync<PlayerException>(() => pick)).Error;

    async Task AssertNothingWrittenAsync(long account)
    {
        Assert.Equal(1L, await ScalarAsync<long>("select version from inventories where account_id = @account", new { account }));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from operative_choices"));
    }

    [Fact]
    public async Task A_pick_on_a_locked_inventory_is_InventoryLocked_and_writes_nothing()
    {
        var (account, operative) = await AccountWithPickAsync();
        var locked = new AlwaysLocked();
        var picks = new PickStore(db.AsApp(), inventoryLock: locked);

        Assert.Equal(PlayerError.InventoryLocked, await RefusalAsync(picks.PickAsync(account, operative, "combat", 1)));
        Assert.Equal(1, locked.Calls);
        await AssertNothingWrittenAsync(account);
    }

    [Fact]
    public async Task InventoryLocked_comes_before_VersionMismatch()
    {
        var (account, operative) = await AccountWithPickAsync();
        var picks = new PickStore(db.AsApp(), inventoryLock: new AlwaysLocked());

        Assert.Equal(PlayerError.InventoryLocked, await RefusalAsync(picks.PickAsync(account, operative, "combat", expectedVersion: 99))); // stale version, locked account
        await AssertNothingWrittenAsync(account);
    }

    [Fact]
    public async Task NotProvisioned_comes_before_InventoryLocked()
    {
        await using var old = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(old.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync(2);
        var account = await PreStage4AccountAsync(old);
        var locked = new AlwaysLocked();

        Assert.Equal(PlayerError.NotProvisioned, await RefusalAsync(new PickStore(old.AsApp(), inventoryLock: locked).PickAsync(account, 1, "combat", 1)));
        Assert.Equal(0, locked.Calls); // an unprovisioned account is never asked about
    }

    [Fact]
    public async Task The_lock_is_asked_while_the_inventories_row_is_held()
    {
        var (account, operative) = await AccountWithPickAsync();
        var probe = new RowProbe(db.AsSuperuser());
        var picks = new PickStore(db.AsApp(), inventoryLock: probe);

        var (version, _) = await picks.PickAsync(account, operative, "combat", 1); // unlocked answer: the pick goes through
        Assert.Equal(2L, version);
        Assert.Equal(["55P03"], probe.Outcomes); // NOWAIT on the row failed: the pick's transaction held it
    }

    [Fact]
    public async Task The_inventory_read_reports_locked_from_the_seam()
    {
        var (account, _) = await AccountWithPickAsync();
        var locked = new AlwaysLocked();

        var view = await new PlayerReadStore(db.AsApp(), inventoryLock: locked).GetInventoryAsync(account);
        Assert.True(view.Locked);
        Assert.Equal(1L, view.Version);
        Assert.Equal(1, locked.Calls);

        // Reads never write and take no row lock: the probe, called inside the read, finds the row free.
        var probe = new RowProbe(db.AsSuperuser(), answer: true);
        Assert.True((await new PlayerReadStore(db.AsApp(), inventoryLock: probe).GetInventoryAsync(account)).Locked);
        Assert.Equal(["free"], probe.Outcomes);
        Assert.Equal(1L, await ScalarAsync<long>("select version from inventories where account_id = @account", new { account }));
    }

    [Fact]
    public async Task Unlocked_is_the_Stage_5_default()
    {
        var (account, operative) = await AccountWithPickAsync();

        Assert.False((await new PlayerReadStore(db.AsApp()).GetInventoryAsync(account)).Locked);
        var (version, view) = await new PickStore(db.AsApp()).PickAsync(account, operative, "combat", 1);
        Assert.Equal(2L, version);
        Assert.Equal(["combat"], view.Choices);

        Assert.False(await new NoParticipationsYet().IsLockedAsync(null!, null!, account, default));
    }
}
