using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Migrations;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Advancement picks under the inventory row lock and version, with exactly the rule of Unity's
/// PersistentOperativeState.TryPickChoice: a choice not on the track is refused first, then a pick with no rank gained
/// to spend. The inventories row is locked (the app has no lock on operatives), the version is checked after the lock,
/// and a refusal writes nothing. Test XP uses the embedded track: thresholds 0, 100, 250, 450, 700; choices ability,
/// combat, mobility, survivability.
/// </summary>
[Trait("Requires", "Postgres")]
public class PickStoreTests : IAsyncLifetime
{
    static readonly GameCatalogue Catalogue = GameCatalogue.Embedded;
    static readonly DbTimeouts Short = new(LockTimeoutMs: 300, StatementTimeoutMs: 10_000);
    TestDatabase db = null!;
    PickStore picks = null!;
    AccountAdminStore admin = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        picks = new PickStore(db.AsApp());
        admin = new AccountAdminStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    /// <summary>A provisioned account and its first operative, which holds <paramref name="xp"/>.</summary>
    async Task<(long Account, long Operative)> AccountWithXpAsync(int xp)
    {
        var account = (await admin.CreateAccountAsync(null, null, Op)).AccountId;
        var operative = await ScalarAsync<long>("select min(operative_id) from operatives where account_id = @account", new { account });
        await ExecuteAsSuperuserAsync(db, "update operatives set experience = @xp where operative_id = @operative", new { xp, operative });
        return (account, operative);
    }

    async Task<List<string>> ChoicesAsync(long operative)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<string>("select choice_id from operative_choices where operative_id = @operative order by ordinal", new { operative })).ToList();
    }

    Task<long> VersionAsync(long account) => ScalarAsync<long>("select version from inventories where account_id = @account", new { account });

    async Task AssertUnchangedAsync(long account, long operative, long version, int choices)
    {
        Assert.Equal(version, await VersionAsync(account));
        Assert.Equal(choices, (await ChoicesAsync(operative)).Count);
    }

    static async Task<PlayerError> RefusalAsync(Task<(long Version, OperativeView Operative)> pick) =>
        (await Assert.ThrowsAsync<PlayerException>(() => pick)).Error;

    // --- the rule ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_pick_needs_a_pending_pick()
    {
        var (account, operative) = await AccountWithXpAsync(99); // rank 1
        Assert.Equal(PlayerError.NoPendingPick, await RefusalAsync(picks.PickAsync(account, operative, "combat", 1)));
        await AssertUnchangedAsync(account, operative, 1, 0);

        await ExecuteAsSuperuserAsync(db, "update operatives set experience = 100 where operative_id = @operative", new { operative }); // rank 2
        await picks.PickAsync(account, operative, "combat", 1);
        Assert.Equal(PlayerError.NoPendingPick, await RefusalAsync(picks.PickAsync(account, operative, "mobility", 2)));
        await AssertUnchangedAsync(account, operative, 2, 1);
    }

    [Theory]
    [InlineData("Combat")]          // wrong case: ordinal comparison
    [InlineData("COMBAT")]
    [InlineData("combat ")]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("Bad Id!")]          // not even the catalogue id shape: refused in code, never by the table's check
    [InlineData("ability.é")]
    public async Task A_pick_needs_a_known_choice(string choice)
    {
        var (account, operative) = await AccountWithXpAsync(700);
        Assert.Equal(PlayerError.UnknownChoice, await RefusalAsync(picks.PickAsync(account, operative, choice, 1)));
        await AssertUnchangedAsync(account, operative, 1, 0);
    }

    [Fact]
    public async Task An_overlong_choice_is_UnknownChoice_not_a_check_violation()
    {
        var (account, operative) = await AccountWithXpAsync(700);
        Assert.Equal(PlayerError.UnknownChoice, await RefusalAsync(picks.PickAsync(account, operative, new string('a', 65), 1)));
        await AssertUnchangedAsync(account, operative, 1, 0);
    }

    [Fact]
    public async Task A_choice_removed_from_the_catalogue_is_UnknownChoice()
    {
        var (account, operative) = await AccountWithXpAsync(700);
        var track = Catalogue.Document.Progression with { ChoiceIds = Catalogue.Document.Progression.ChoiceIds.Where(c => c != "combat").ToList() };
        var narrowed = new PickStore(db.AsApp(), GameCatalogue.Load(CanonicalCatalogueJson.ToBytes(Catalogue.Document with { Progression = track })));

        Assert.Equal(PlayerError.UnknownChoice, await RefusalAsync(narrowed.PickAsync(account, operative, "combat", 1)));
        await AssertUnchangedAsync(account, operative, 1, 0);
        var (version, _) = await narrowed.PickAsync(account, operative, "mobility", 1); // the others still work
        Assert.Equal(2L, version);
    }

    [Fact]
    public async Task Unknown_choice_is_reported_before_no_pending_pick()
    {
        var (account, operative) = await AccountWithXpAsync(0); // nothing pending either
        Assert.Equal(PlayerError.UnknownChoice, await RefusalAsync(picks.PickAsync(account, operative, "nonsense", 1)));
        Assert.Equal(PlayerError.NoPendingPick, await RefusalAsync(picks.PickAsync(account, operative, "combat", 1)));
        await AssertUnchangedAsync(account, operative, 1, 0);
    }

    [Fact]
    public async Task A_pick_takes_the_next_ordinal_and_bumps_the_version()
    {
        var (account, operative) = await AccountWithXpAsync(450); // rank 4: three picks
        var (v1, first) = await picks.PickAsync(account, operative, "survivability", 1);
        Assert.Equal(2L, v1);
        Assert.Equal((operative, 450, 4, 2), (first.OperativeId, first.Experience, first.Rank, first.PendingPicks));
        Assert.Equal(["survivability"], first.Choices);

        var (v2, second) = await picks.PickAsync(account, operative, "ability", v1);
        Assert.Equal(3L, v2);
        Assert.Equal(1, second.PendingPicks);
        Assert.Equal(["survivability", "ability"], second.Choices);

        Assert.Equal(["survivability", "ability"], await ChoicesAsync(operative));
        await using (var conn = await db.AsSuperuser().OpenConnectionAsync())
            Assert.Equal([0, 1], (await conn.QueryAsync<int>("select ordinal from operative_choices where operative_id = @operative order by ordinal", new { operative })).ToList());
        Assert.Equal(3L, await VersionAsync(account));
    }

    [Fact]
    public async Task The_same_choice_may_be_picked_again()
    {
        var (account, operative) = await AccountWithXpAsync(250);
        var (v1, _) = await picks.PickAsync(account, operative, "combat", 1);
        var (v2, view) = await picks.PickAsync(account, operative, "combat", v1);
        Assert.Equal(3L, v2);
        Assert.Equal(["combat", "combat"], view.Choices);
        Assert.Equal(["combat", "combat"], await ChoicesAsync(operative));
    }

    [Fact]
    public async Task Picks_stop_at_the_number_of_ranks_gained()
    {
        var (account, operative) = await AccountWithXpAsync(700); // rank 5: four picks
        var version = 1L;
        foreach (var choice in Catalogue.Document.Progression.ChoiceIds)
            (version, _) = await picks.PickAsync(account, operative, choice, version);
        Assert.Equal(5L, version);

        Assert.Equal(PlayerError.NoPendingPick, await RefusalAsync(picks.PickAsync(account, operative, "combat", version)));
        await AssertUnchangedAsync(account, operative, 5, 4);
    }

    // --- version, scope, provisioning -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task A_stale_version_is_VersionMismatch_and_writes_nothing(long expected)
    {
        var (account, operative) = await AccountWithXpAsync(700);
        Assert.Equal(PlayerError.VersionMismatch, await RefusalAsync(picks.PickAsync(account, operative, "combat", expected)));
        Assert.Equal(PlayerError.VersionMismatch, await RefusalAsync(picks.PickAsync(account, operative, "nonsense", expected))); // the version is checked first
        await AssertUnchangedAsync(account, operative, 1, 0);

        var (version, _) = await picks.PickAsync(account, operative, "combat", 1);
        Assert.Equal(PlayerError.VersionMismatch, await RefusalAsync(picks.PickAsync(account, operative, "combat", 1))); // a replay of the old version
        await AssertUnchangedAsync(account, operative, version, 1);
    }

    [Fact]
    public async Task Another_accounts_operative_is_OperativeNotFound_and_writes_nothing()
    {
        var (account, _) = await AccountWithXpAsync(700);
        var (other, theirs) = await AccountWithXpAsync(700);

        Assert.Equal(PlayerError.OperativeNotFound, await RefusalAsync(picks.PickAsync(account, theirs, "combat", 1)));
        Assert.Equal(PlayerError.OperativeNotFound, await RefusalAsync(picks.PickAsync(account, long.MaxValue, "combat", 1))); // unknown: the same error
        Assert.Equal(PlayerError.OperativeNotFound, await RefusalAsync(picks.PickAsync(account, -1, "combat", 1)));
        await AssertUnchangedAsync(other, theirs, 1, 0);
        Assert.Equal(1L, await VersionAsync(account));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from operative_choices"));
    }

    [Fact]
    public async Task An_unprovisioned_account_is_NotProvisioned()
    {
        await using var old = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(old.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync(2);
        var account = await AccountTestKit.PreStage4AccountAsync(old);

        Assert.Equal(PlayerError.NotProvisioned, await RefusalAsync(new PickStore(old.AsApp()).PickAsync(account, 1, "combat", 1)));
        Assert.Equal(PlayerError.NotProvisioned, await RefusalAsync(new PickStore(old.AsApp()).PickAsync(long.MaxValue, 1, "combat", 1))); // no such account either
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(old, "select count(*) from inventories"));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(old, "select count(*) from operative_choices"));
    }

    // --- concurrency (Review Focus 3) -------------------------------------------------------------------------------------

    static async Task WaitForWaitersAsync(TestDatabase db, int count)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await su.ExecuteScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Fewer than {count} backends waited on the lock.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Two_picks_with_the_same_version_one_wins_one_is_409()
    {
        var (account, operative) = await AccountWithXpAsync(700); // four picks pending: only the version can refuse the loser
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account }, tx);

        async Task<PlayerError?> AttemptAsync(string choice)
        {
            try { await picks.PickAsync(account, operative, choice, 1); return null; }
            catch (PlayerException e) { return e.Error; }
        }
        var first = Task.Run(() => AttemptAsync("combat"));
        var second = Task.Run(() => AttemptAsync("mobility"));
        await WaitForWaitersAsync(db, 2); // both are queued behind the held lock with the same expected version
        await tx.RollbackAsync();

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r == PlayerError.VersionMismatch);
        Assert.Equal(2L, await VersionAsync(account)); // +1 once
        var choices = await ChoicesAsync(operative);
        Assert.Single(choices);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from operative_choices where operative_id = @operative and ordinal = 0", new { operative }));
    }

    [Fact]
    public async Task A_pick_behind_a_held_inventory_lock_times_out_and_writes_nothing()
    {
        var (account, operative) = await AccountWithXpAsync(700);
        var bounded = new PickStore(db.AsAppWith(Short));
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account }, tx);

        var e = await Assert.ThrowsAsync<PostgresException>(() => bounded.PickAsync(account, operative, "combat", 1).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
        await tx.RollbackAsync();
        await AssertUnchangedAsync(account, operative, 1, 0);
    }
}
