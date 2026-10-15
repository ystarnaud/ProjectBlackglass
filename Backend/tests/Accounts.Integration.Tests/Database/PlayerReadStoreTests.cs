using Dapper;
using Blackglass.Accounts.Domain.Advancement;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Migrations;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The read-only roster and inventory (owner ruling D4): scoped to the account, never mutating, from one snapshot, and
/// tolerant of stored ids the catalogue no longer knows. A GET on an unprovisioned account is NotProvisioned, never a grant.
/// </summary>
[Trait("Requires", "Postgres")]
public class PlayerReadStoreTests : IAsyncLifetime
{
    static readonly GameCatalogue Catalogue = GameCatalogue.Embedded;
    TestDatabase db = null!;
    PlayerReadStore reads = null!;
    AccountAdminStore admin = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        reads = new PlayerReadStore(db.AsApp());
        admin = new AccountAdminStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    async Task<long> NewAccountAsync() => (await admin.CreateAccountAsync(null, null, Op)).AccountId;

    async Task<List<long>> OperativeIdsAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<long>("select operative_id from operatives where account_id = @account order by operative_id", new { account })).ToList();
    }

    [Fact]
    public async Task A_new_account_reads_its_starter()
    {
        var account = await NewAccountAsync();
        var plan = StarterPlanner.For(account, Catalogue.Document);

        var roster = await reads.GetRosterAsync(account, default);
        Assert.Equal(1L, roster.Version);
        Assert.Equal(plan.Operatives.Select(o => o.DefinitionId), roster.Operatives.Select(o => o.DefinitionId)); // minted in plan order, listed by id
        Assert.Equal(await OperativeIdsAsync(account), roster.Operatives.Select(o => o.OperativeId));
        Assert.All(roster.Operatives, o =>
        {
            Assert.True(o.DefinitionKnown);
            Assert.Equal((0, 1, 0), (o.Experience, o.Rank, o.PendingPicks));
            Assert.Empty(o.Choices);
        });

        var inventory = await reads.GetInventoryAsync(account, default);
        Assert.Equal(1L, inventory.Version);
        Assert.False(inventory.Locked);
        Assert.Equal(await reads.GetInventoryVersionAsync(account, default), inventory.Version);

        Assert.Equal(StashInstances(plan), inventory.Stash.Instances.Select(i => i.DefinitionId));
        Assert.All(inventory.Stash.Instances, i => Assert.True(i.DefinitionKnown));
        Assert.Equal(Stacks(plan, null), inventory.Stash.Stacks.Select(s => (s.DefinitionId, s.Quantity)));
        Assert.All(inventory.Stash.Stacks, s => Assert.True(s.DefinitionKnown));

        Assert.Equal(roster.Operatives.Select(o => o.OperativeId), inventory.Operatives.Select(b => b.OperativeId));
        foreach (var (bag, operative) in inventory.Operatives.Zip(roster.Operatives))
        {
            Assert.Equal(plan.Instances.Where(i => i.OperativeDefinitionId == operative.DefinitionId).Select(i => i.DefinitionId), bag.Bag.Instances.Select(i => i.DefinitionId));
            Assert.Equal(Stacks(plan, operative.DefinitionId), bag.Bag.Stacks.Select(s => (s.DefinitionId, s.Quantity)));
        }
        Assert.Equal(3, inventory.Operatives.Count);
        Assert.All(inventory.Operatives, b => Assert.NotNull(b.Equipment.Weapon));
    }

    static IEnumerable<string> StashInstances(StarterPlan plan) => plan.Instances.Where(i => i.OperativeDefinitionId is null).Select(i => i.DefinitionId);

    static IEnumerable<(string, int)> Stacks(StarterPlan plan, string? operativeDefinition) =>
        plan.Stacks.Where(s => s.OperativeDefinitionId == operativeDefinition).Select(s => (s.DefinitionId, s.Quantity)).OrderBy(s => s.Item1, StringComparer.Ordinal);

    // --- never provisions ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unprovisioned_account_is_NotProvisioned()
    {
        await using var old = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(old.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync(2);
        var account = await PreStage4AccountAsync(old);
        var store = new PlayerReadStore(old.AsApp());

        Assert.Equal(PlayerError.NotProvisioned, (await Assert.ThrowsAsync<PlayerException>(() => store.GetRosterAsync(account, default))).Error);
        Assert.Equal(PlayerError.NotProvisioned, (await Assert.ThrowsAsync<PlayerException>(() => store.GetInventoryAsync(account, default))).Error);
        Assert.Null(await store.GetInventoryVersionAsync(account, default));
        await AssertNothingWasCreatedAsync(old);

        // An inventories row that has not been granted is just as unprovisioned.
        await ExecuteAsSuperuserAsync(old, "insert into inventories (account_id) values (@account)", new { account });
        Assert.Equal(PlayerError.NotProvisioned, (await Assert.ThrowsAsync<PlayerException>(() => store.GetRosterAsync(account, default))).Error);
        Assert.Equal(PlayerError.NotProvisioned, (await Assert.ThrowsAsync<PlayerException>(() => store.GetInventoryAsync(account, default))).Error);
        Assert.Null(await store.GetInventoryVersionAsync(account, default));
        Assert.Equal((1L, 0L, 0L, 0L, 0L), await CountsAsync(old));
    }

    [Fact]
    public async Task An_account_that_does_not_exist_is_NotProvisioned_too()
    {
        Assert.Equal(PlayerError.NotProvisioned, (await Assert.ThrowsAsync<PlayerException>(() => reads.GetRosterAsync(424242, default))).Error);
        Assert.Equal(PlayerError.NotProvisioned, (await Assert.ThrowsAsync<PlayerException>(() => reads.GetInventoryAsync(424242, default))).Error);
        Assert.Null(await reads.GetInventoryVersionAsync(424242, default));
    }

    static async Task AssertNothingWasCreatedAsync(TestDatabase database) =>
        Assert.Equal((0L, 0L, 0L, 0L, 0L), await CountsAsync(database));

    static async Task<(long Inventories, long Operatives, long Choices, long Instances, long Stacks)> CountsAsync(TestDatabase database) =>
        await ScalarFromAsync(database,
            "select (select count(*) from inventories), (select count(*) from operatives), (select count(*) from operative_choices), (select count(*) from item_instances), (select count(*) from item_stacks)");

    static async Task<(long, long, long, long, long)> ScalarFromAsync(TestDatabase database, string sql)
    {
        await using var conn = await database.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<(long, long, long, long, long)>(sql);
    }

    // --- never writes ----------------------------------------------------------------------------------------------------

    /// <summary>Every row of every player table with its xmin: any insert, update or delete changes it.</summary>
    Task<string> FingerprintAsync() => ScalarAsync<string>(
        """
        select string_agg(x, '|' order by x) from (
          select 'a' || t.xmin::text || t::text as x from accounts t
          union all select 'i' || t.xmin::text || t::text from inventories t
          union all select 'o' || t.xmin::text || t::text from operatives t
          union all select 'c' || t.xmin::text || t::text from operative_choices t
          union all select 'n' || t.xmin::text || t::text from item_instances t
          union all select 's' || t.xmin::text || t::text from item_stacks t) q
        """);

    [Fact]
    public async Task Reads_write_nothing()
    {
        var account = await NewAccountAsync();
        await ExecuteAsSuperuserAsync(db,
            "insert into operative_choices (operative_id, ordinal, choice_id) select min(operative_id), 0, 'combat' from operatives where account_id = @account", new { account });
        var before = await FingerprintAsync();
        var ordinary = (await reads.GetRosterAsync(account, default), await reads.GetInventoryAsync(account, default), await reads.GetInventoryVersionAsync(account, default));

        // The connections of this data source default to read-only transactions (25006 on any write the store attempted outside
        // a read-only one); the store must succeed on them, and read the same thing.
        var readOnly = new PlayerReadStore(db.AsAppReadOnly());
        var viaReadOnly = (await readOnly.GetRosterAsync(account, default), await readOnly.GetInventoryAsync(account, default), await readOnly.GetInventoryVersionAsync(account, default));
        Assert.Equal(1L, viaReadOnly.Item3);
        Assert.Equal(ordinary.Item3, viaReadOnly.Item3);
        Assert.Equal(ordinary.Item1.Version, viaReadOnly.Item1.Version);
        Assert.Equal(ordinary.Item1.Operatives.Select(o => (o.OperativeId, o.Choices.Count)), viaReadOnly.Item1.Operatives.Select(o => (o.OperativeId, o.Choices.Count)));
        Assert.Equal(ordinary.Item2.Stash.Instances, viaReadOnly.Item2.Stash.Instances);

        Assert.Equal(before, await FingerprintAsync()); // no row was inserted, changed or deleted by any read
    }

    // --- scope -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reads_never_show_another_accounts_rows()
    {
        var mine = await NewAccountAsync();
        var other = await NewAccountAsync();
        var otherOperatives = await OperativeIdsAsync(other);
        await ExecuteAsSuperuserAsync(db,
            "insert into operative_choices (operative_id, ordinal, choice_id) values (@operative, 0, 'mobility')", new { operative = otherOperatives[0] });
        await ExecuteAsSuperuserAsync(db, "update operatives set experience = 700 where operative_id = @operative", new { operative = otherOperatives[0] });
        await ExecuteAsSuperuserAsync(db, "update inventories set version = 9 where account_id = @other", new { other });

        var roster = await reads.GetRosterAsync(mine, default);
        var inventory = await reads.GetInventoryAsync(mine, default);
        Assert.Equal(await OperativeIdsAsync(mine), roster.Operatives.Select(o => o.OperativeId));
        Assert.Empty(roster.Operatives.Select(o => o.OperativeId).Intersect(otherOperatives));
        Assert.All(roster.Operatives, o => Assert.Equal((0, 0), (o.Experience, o.Choices.Count)));
        Assert.Equal(1L, roster.Version);
        Assert.Equal(1L, inventory.Version);
        Assert.Empty(inventory.Operatives.Select(b => b.OperativeId).Intersect(otherOperatives));

        var mineItems = (await ItemIdsAsync(mine)).ToHashSet();
        var shown = inventory.Stash.Instances.Concat(inventory.Operatives.SelectMany(b => b.Bag.Instances)).Select(i => i.ItemId).ToList();
        Assert.Equal(mineItems.Order(), shown.Order());

        Assert.Equal(9L, (await reads.GetRosterAsync(other, default)).Version);
        Assert.Equal(9L, await reads.GetInventoryVersionAsync(other, default));
    }

    async Task<List<long>> ItemIdsAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<long>("select item_id from item_instances where account_id = @account", new { account })).ToList();
    }

    // --- advancement -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, 0, 1, 0)]   // a negative XP cannot be stored, so this one is checked against the rules below
    [InlineData(0, 0, 1, 0)]
    [InlineData(99, 0, 1, 0)]
    [InlineData(100, 0, 2, 1)]
    [InlineData(249, 0, 2, 1)]
    [InlineData(250, 0, 3, 2)]
    [InlineData(449, 0, 3, 2)]
    [InlineData(450, 0, 4, 3)]
    [InlineData(700, 0, 5, 4)]
    [InlineData(701, 0, 5, 4)]
    [InlineData(int.MaxValue, 0, 5, 4)]
    [InlineData(700, 1, 5, 3)]
    [InlineData(700, 3, 5, 1)]
    [InlineData(700, 4, 5, 0)]
    [InlineData(700, 6, 5, 0)]
    [InlineData(100, 1, 2, 0)]
    [InlineData(250, 1, 3, 1)]
    [InlineData(250, 2, 3, 0)]
    [InlineData(0, 4, 1, 0)]
    public async Task Rank_and_pending_picks_come_from_xp_and_the_catalogue_track(int xp, int picksMade, int rank, int pending)
    {
        if (xp < 0)
        {
            Assert.Equal((rank, pending), (ProgressionRules.RankFor(Catalogue.Document.Progression, xp), ProgressionRules.PendingPicks(Catalogue.Document.Progression, xp, picksMade)));
            return;
        }
        var account = await NewAccountAsync();
        var operative = (await OperativeIdsAsync(account))[1];
        await ExecuteAsSuperuserAsync(db, "update operatives set experience = @xp where operative_id = @operative", new { xp, operative });
        var picks = Catalogue.Document.Progression.ChoiceIds;
        for (var ordinal = 0; ordinal < picksMade; ordinal++)
            await ExecuteAsSuperuserAsync(db, "insert into operative_choices (operative_id, ordinal, choice_id) values (@operative, @ordinal, @choice)",
                new { operative, ordinal, choice = picks[ordinal % picks.Count] });

        var roster = await reads.GetRosterAsync(account, default);
        var view = Assert.Single(roster.Operatives, o => o.OperativeId == operative);
        Assert.Equal((xp, rank, pending), (view.Experience, view.Rank, view.PendingPicks));
        Assert.Equal(Enumerable.Range(0, picksMade).Select(i => picks[i % picks.Count]), view.Choices); // by ordinal
        Assert.All(roster.Operatives.Where(o => o.OperativeId != operative), o => Assert.Equal((0, 1, 0), (o.Experience, o.Rank, o.PendingPicks)));
    }

    [Fact]
    public async Task The_rules_are_applied_to_the_catalogue_a_store_holds()
    {
        var account = await NewAccountAsync();
        var operative = (await OperativeIdsAsync(account))[0];
        await ExecuteAsSuperuserAsync(db, "update operatives set experience = 100 where operative_id = @operative", new { operative });
        var track = Catalogue.Document.Progression with { XpThresholds = [0, 50, 100, 150, 200] };
        var other = GameCatalogue.Load(CanonicalCatalogueJson.ToBytes(Catalogue.Document with { Progression = track }));

        var view = Assert.Single((await new PlayerReadStore(db.AsApp(), other).GetRosterAsync(account, default)).Operatives, o => o.OperativeId == operative);
        Assert.Equal((3, 2), (view.Rank, view.PendingPicks));
    }

    // --- Review Focus 4 --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stored_id_the_catalogue_no_longer_knows_reads_as_unknown_not_500()
    {
        var account = await NewAccountAsync();
        var operatives = await OperativeIdsAsync(account);
        var gone = operatives[0];
        await ExecuteAsSuperuserAsync(db, "update operatives set definition_id = 'retired.operative' where operative_id = @gone", new { gone });
        await ExecuteAsSuperuserAsync(db, "insert into operative_choices (operative_id, ordinal, choice_id) values (@gone, 0, 'retired.choice')", new { gone });
        await ExecuteAsSuperuserAsync(db, "update item_instances set definition_id = 'item.retired-gun' where item_id = (select min(item_id) from item_instances where operative_id = @gone)", new { gone });
        await ExecuteAsSuperuserAsync(db, "update item_instances set definition_id = 'item.retired-stash-piece' where item_id = (select min(item_id) from item_instances where account_id = @account and operative_id is null)", new { account });
        await ExecuteAsSuperuserAsync(db, "update item_stacks set definition_id = 'item.retired-ammo' where definition_id = 'item.medkit' and operative_id = @gone", new { gone });
        await ExecuteAsSuperuserAsync(db, "insert into item_stacks (account_id, operative_id, definition_id, quantity) values (@account, null, 'item.retired-stash-stack', 3)", new { account });

        var roster = await reads.GetRosterAsync(account, default);
        var view = roster.Operatives[0];
        Assert.Equal(("retired.operative", false), (view.DefinitionId, view.DefinitionKnown));
        Assert.Equal(["retired.choice"], view.Choices); // a choice id is only a string; the track decides nothing about it here
        Assert.All(roster.Operatives.Skip(1), o => Assert.True(o.DefinitionKnown));

        var inventory = await reads.GetInventoryAsync(account, default);
        Assert.Contains(inventory.Operatives[0].Bag.Instances, i => i is { DefinitionId: "item.retired-gun", DefinitionKnown: false });
        Assert.Contains(inventory.Operatives[0].Bag.Stacks, s => s is { DefinitionId: "item.retired-ammo", DefinitionKnown: false, Quantity: 2 });
        Assert.Contains(inventory.Stash.Instances, i => i is { DefinitionId: "item.retired-stash-piece", DefinitionKnown: false });
        Assert.Contains(inventory.Stash.Stacks, s => s is { DefinitionId: "item.retired-stash-stack", DefinitionKnown: false, Quantity: 3 });
        Assert.Contains(inventory.Stash.Instances, i => i.DefinitionKnown);
        Assert.All(inventory.Operatives[1].Bag.Instances, i => Assert.True(i.DefinitionKnown));
    }

    // --- equipment -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Equipment_lists_the_equipped_item_ids_per_slot()
    {
        var account = await NewAccountAsync();
        var operatives = await OperativeIdsAsync(account);
        // Equip the stash vest on the first operative (as a Stage 5 edit would): armor joins the weapon already there.
        var vest = await ScalarAsync<long>(
            "select item_id from item_instances where account_id = @account and operative_id is null and definition_id = 'item.light-vest'", new { account });
        await ExecuteAsSuperuserAsync(db, "update item_instances set operative_id = @operative, equipped_slot = 'armor' where item_id = @vest", new { operative = operatives[0], vest });

        var inventory = await reads.GetInventoryAsync(account, default);
        Assert.Equal(operatives, inventory.Operatives.Select(b => b.OperativeId));
        foreach (var bag in inventory.Operatives)
        {
            var weapon = await ScalarAsync<long?>("select item_id from item_instances where operative_id = @id and equipped_slot = 'weapon'", new { id = bag.OperativeId });
            var armor = await ScalarAsync<long?>("select item_id from item_instances where operative_id = @id and equipped_slot = 'armor'", new { id = bag.OperativeId });
            var utility = await ScalarAsync<long?>("select item_id from item_instances where operative_id = @id and equipped_slot = 'utility'", new { id = bag.OperativeId });
            Assert.Equal(new EquipmentView(weapon, armor, utility), bag.Equipment);
            // an equipped item is also in the bag (it is carried)
            foreach (var equipped in new[] { weapon, armor, utility }.OfType<long>())
                Assert.Contains(bag.Bag.Instances, i => i.ItemId == equipped);
        }
        Assert.Equal(vest, inventory.Operatives[0].Equipment.Armor);
        Assert.NotNull(inventory.Operatives[0].Equipment.Weapon);
        Assert.Null(inventory.Operatives[1].Equipment.Armor);
        Assert.DoesNotContain(inventory.Stash.Instances, i => i.ItemId == vest);
        Assert.Equal(inventory.Operatives[0].Bag.Instances.Select(i => i.ItemId).Order(), inventory.Operatives[0].Bag.Instances.Select(i => i.ItemId)); // by item id
    }

    // --- one snapshot ----------------------------------------------------------------------------------------------------

    /// <summary>A writer commits between the read's first statement (the version) and its last: the read is a repeatable-read
    /// snapshot, so it shows the old version with the old rows, never the old version with new rows. The read is held on an
    /// ACCESS EXCLUSIVE lock of item_instances, which the writer (the lock holder) releases by committing.</summary>
    [Fact]
    public async Task Version_and_rows_come_from_one_snapshot()
    {
        var account = await NewAccountAsync();
        var before = await reads.GetInventoryAsync(account, default);
        var itemsBefore = before.Stash.Instances.Count + before.Operatives.Sum(b => b.Bag.Instances.Count);

        await using var writer = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await writer.ExecuteAsync(new CommandDefinition("lock table item_instances in access exclusive mode", transaction: tx));
        var read = Task.Run(() => reads.GetInventoryAsync(account, default));
        await WaitUntilABackendWaitsOnALockAsync(db);
        Assert.False(read.IsCompleted);
        await writer.ExecuteAsync(new CommandDefinition(
            "insert into item_instances (account_id, definition_id, origin, origin_ref) values (@account, 'item.medkit', 'dev_grant', 'snapshot-test')", new { account }, tx));
        await writer.ExecuteAsync(new CommandDefinition("update inventories set version = version + 1 where account_id = @account", new { account }, tx));
        await tx.CommitAsync();

        var snapshot = await read;
        Assert.Equal(1L, snapshot.Version);
        Assert.Equal(itemsBefore, snapshot.Stash.Instances.Count + snapshot.Operatives.Sum(b => b.Bag.Instances.Count));
        Assert.DoesNotContain(snapshot.Stash.Instances, i => i.DefinitionId == "item.medkit");

        var after = await reads.GetInventoryAsync(account, default);
        Assert.Equal(2L, after.Version);
        Assert.Equal(itemsBefore + 1, after.Stash.Instances.Count + after.Operatives.Sum(b => b.Bag.Instances.Count));
    }
}
