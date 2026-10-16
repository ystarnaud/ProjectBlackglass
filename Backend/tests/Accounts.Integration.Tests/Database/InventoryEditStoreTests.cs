using System.Text.Json;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Atomic, version-checked inventory batches (owner rulings S5-D1 to S5-D8) against a real database: one transaction under
/// the account's inventories row lock, refusals in the order NotProvisioned, InventoryLocked, VersionMismatch, operations;
/// a refused or failed batch writes nothing; a successful one bumps the version exactly once; nothing is ever retried.
/// Accounts start from the embedded starter: three operatives, each with a rifle equipped and 2 medkits in its bag, and a
/// stash of a combat blade, a service rifle, a light vest, servo boots and 4 medkits (maxStack 3, bag capacity 8).
/// </summary>
[Trait("Requires", "Postgres")]
public class InventoryEditStoreTests : IAsyncLifetime
{
    const string Medkit = "item.medkit", Vest = "item.light-vest", Boots = "item.servo-boots", Blade = "item.combat-blade", ServiceRifle = "item.service-rifle";
    const long Ghost = long.MaxValue;
    static readonly DbTimeouts Short = new(LockTimeoutMs: 300, StatementTimeoutMs: 10_000);
    TestDatabase db = null!;
    InventoryEditStore edits = null!;
    PlayerReadStore reads = null!;
    AccountAdminStore admin = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        edits = new InventoryEditStore(db.AsApp());
        reads = new PlayerReadStore(db.AsApp());
        admin = new AccountAdminStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    // --- helpers ----------------------------------------------------------------------------------------------------------

    sealed record Account(long Id, long[] Ops);
    sealed record ItemRow(long ItemId, string DefinitionId, long? OperativeId, string? EquippedSlot, string Origin, string OriginRef);
    sealed record StackRow(long? OperativeId, string DefinitionId, int Quantity);

    /// <summary>A provisioned account and its operatives in id order (the starter plan's order).</summary>
    async Task<Account> NewAccountAsync()
    {
        var id = (await admin.CreateAccountAsync(null, null, Op)).AccountId;
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var ops = (await conn.QueryAsync<long>("select operative_id from operatives where account_id = @id order by operative_id", new { id })).ToArray();
        Assert.Equal(3, ops.Length);
        return new Account(id, ops);
    }

    /// <summary>An account row with no inventories row: not provisioned.</summary>
    Task<long> BareAccountAsync() => ScalarAsSuperuserAsync<long>(db, "insert into accounts (status) values ('active') returning account_id");

    /// <summary>The account's instances and stacks, in one round trip (the test cluster is reached through a tunnel).</summary>
    async Task<(List<ItemRow> Items, List<StackRow> Stacks)> RowsAsync(long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var grid = await conn.QueryMultipleAsync(
            """
            select item_id as ItemId, definition_id as DefinitionId, operative_id as OperativeId, equipped_slot as EquippedSlot,
                   origin as Origin, origin_ref as OriginRef
            from item_instances where account_id = @account order by item_id;
            select operative_id as OperativeId, definition_id as DefinitionId, quantity as Quantity
            from item_stacks where account_id = @account order by operative_id nulls first, definition_id collate "C";
            """, new { account });
        return ((await grid.ReadAsync<ItemRow>()).ToList(), (await grid.ReadAsync<StackRow>()).ToList());
    }

    async Task<List<ItemRow>> ItemsAsync(long account) => (await RowsAsync(account)).Items;

    async Task<List<StackRow>> StacksAsync(long account) => (await RowsAsync(account)).Stacks;

    async Task<int?> StackAsync(long account, long? operative, string definition) =>
        (await StacksAsync(account)).SingleOrDefault(s => s.OperativeId == operative && s.DefinitionId == definition)?.Quantity;

    async Task<long> StashItemAsync(long account, string definition) =>
        (await ItemsAsync(account)).First(i => i.OperativeId is null && i.DefinitionId == definition).ItemId;

    async Task<long> EquippedAsync(long account, long operative, string slot) =>
        (await ItemsAsync(account)).Single(i => i.OperativeId == operative && i.EquippedSlot == slot).ItemId;

    /// <summary>Where an item is, as "operative/slot", "operative/-" or "stash/-".</summary>
    async Task<string> WhereAsync(long account, long item)
    {
        var row = (await ItemsAsync(account)).Single(i => i.ItemId == item);
        return $"{row.OperativeId?.ToString() ?? "stash"}/{row.EquippedSlot ?? "-"}";
    }

    Task<long> VersionAsync(long account) => ScalarAsSuperuserAsync<long>(db, "select version from inventories where account_id = @account", new { account });

    /// <summary>
    /// Every row the account owns, byte for byte, with each row's xmin: an UPDATE that rewrote a row to the same values
    /// would still change its xmin, so an equal snapshot means nothing was written (a row lock alone leaves xmin alone).
    /// </summary>
    Task<string> SnapshotAsync(long account) => ScalarAsSuperuserAsync<string>(db,
        """
        select concat_ws(E'\n',
          (select json_build_object('xmin', i.xmin::text, 'row', row_to_json(i))::text from inventories i where i.account_id = @account),
          (select json_agg(json_build_object('xmin', o.xmin::text, 'row', row_to_json(o)) order by o.operative_id)::text
             from operatives o where o.account_id = @account),
          (select json_agg(json_build_object('xmin', c.xmin::text, 'row', row_to_json(c)) order by c.operative_id, c.ordinal)::text
             from operative_choices c join operatives o on o.operative_id = c.operative_id where o.account_id = @account),
          (select json_agg(json_build_object('xmin', t.xmin::text, 'row', row_to_json(t)) order by t.item_id)::text
             from item_instances t where t.account_id = @account),
          (select json_agg(json_build_object('xmin', s.xmin::text, 'row', row_to_json(s)) order by s.operative_id nulls first, s.definition_id collate "C")::text
             from item_stacks s where s.account_id = @account))
        """, new { account });

    /// <summary>The per-definition instance counts and stack totals, and the instance id set: what an edit conserves.</summary>
    async Task<string> TotalsAsync(long account) => Totals(await RowsAsync(account));

    static string Totals((List<ItemRow> Items, List<StackRow> Stacks) rows)
    {
        var (items, stacks) = rows;
        return string.Join("\n",
            items.GroupBy(i => i.DefinitionId).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"instances {g.Key} {g.Count()}")
                .Concat(stacks.GroupBy(s => s.DefinitionId).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"units {g.Key} {g.Sum(s => s.Quantity)}"))
                .Append("ids " + string.Join(",", items.Select(i => i.ItemId))));
    }

    static string Json(InventoryView view) => JsonSerializer.Serialize(view);

    static async Task<InventoryRefusedException> RefusedAsync(Task task) => await Assert.ThrowsAsync<InventoryRefusedException>(() => task);

    static async Task<PlayerError> PlayerRefusalAsync(Task task) => (await Assert.ThrowsAsync<PlayerException>(() => task)).Error;

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

    /// <summary>Holds the caller inside its transaction, under the row lock, at the seam's call until released.</summary>
    sealed class Gate : IInventoryLock
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> IsLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return false;
        }
    }

    // --- behaviour --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_batch_applies_every_operation_and_bumps_the_version_once()
    {
        var a = await NewAccountAsync();
        var (op0, op1) = (a.Ops[0], a.Ops[1]);
        var vest = await StashItemAsync(a.Id, Vest);
        var boots = await StashItemAsync(a.Id, Boots);
        var blade = await StashItemAsync(a.Id, Blade);
        var rifle0 = await EquippedAsync(a.Id, op0, "weapon");
        var totals = await TotalsAsync(a.Id);

        var (version, view) = await edits.ApplyAsync(a.Id, 1, [
            new EquipFromStash(op0, vest),
            new MoveStack(op0, Medkit, 2, Destination.Bag),      // bag 4, stash 2
            new EquipFromStash(op0, boots),
            new Unequip(op0, ItemSlot.Weapon),
            new MoveInstance(op0, rifle0, Destination.Stash),
            new MoveInstance(op1, blade, Destination.Bag),
            new MoveStack(op1, Medkit, 1, Destination.Stash),    // bag 1, stash 3
        ]);

        Assert.Equal(2L, version);
        Assert.Equal(2L, await VersionAsync(a.Id));
        Assert.Equal($"{op0}/armor", await WhereAsync(a.Id, vest));
        Assert.Equal($"{op0}/utility", await WhereAsync(a.Id, boots));
        Assert.Equal("stash/-", await WhereAsync(a.Id, rifle0));
        Assert.Equal($"{op1}/-", await WhereAsync(a.Id, blade));
        Assert.Equal(4, await StackAsync(a.Id, op0, Medkit));
        Assert.Equal(1, await StackAsync(a.Id, op1, Medkit));
        Assert.Equal(3, await StackAsync(a.Id, null, Medkit));
        Assert.Equal(totals, await TotalsAsync(a.Id));

        Assert.Equal(2L, view.Version);
        Assert.False(view.Locked);
        Assert.Equal(Json(await reads.GetInventoryAsync(a.Id)), Json(view)); // the same view GET /v1/inventory returns
    }

    [Fact]
    public async Task A_refused_batch_writes_nothing_and_reports_the_index_and_code()
    {
        var a = await NewAccountAsync();
        var vest = await StashItemAsync(a.Id, Vest);
        var before = await SnapshotAsync(a.Id);

        var e = await RefusedAsync(edits.ApplyAsync(a.Id, 1, [new EquipFromStash(a.Ops[0], vest), new MoveStack(a.Ops[0], Medkit, 99, Destination.Bag)]));
        Assert.Equal((1, InventoryRefusal.InsufficientQuantity), (e.Index, e.Code));
        Assert.Equal("Inventory operation 1 was refused: InsufficientQuantity.", e.Message); // no id in the message
        Assert.Equal(before, await SnapshotAsync(a.Id));

        var (version, _) = await edits.ApplyAsync(a.Id, 1, [new EquipFromStash(a.Ops[0], vest)]); // the version did not move
        Assert.Equal(2L, version);
    }

    [Fact]
    public async Task A_refused_equipFromStash_late_in_a_batch_leaves_everything_as_it_was()
    {
        var a = await NewAccountAsync();
        var (op0, op1) = (a.Ops[0], a.Ops[1]);
        var blade = await StashItemAsync(a.Id, Blade);
        var spare = await StashItemAsync(a.Id, ServiceRifle);
        var vest = await StashItemAsync(a.Id, Vest);
        var boots = await StashItemAsync(a.Id, Boots);
        var rifle1 = await EquippedAsync(a.Id, op1, "weapon");
        var before = await SnapshotAsync(a.Id);

        // op0's bag starts at 2 entries (its rifle and 2 medkits) and is filled to its capacity of 8 before the last operation.
        var e = await RefusedAsync(edits.ApplyAsync(a.Id, 1, [
            new MoveInstance(op0, blade, Destination.Bag),        // 3
            new MoveInstance(op0, spare, Destination.Bag),        // 4
            new MoveInstance(op0, vest, Destination.Bag),         // 5
            new MoveStack(op0, Medkit, 4, Destination.Bag),       // 6 medkits: 6 entries; the stash row empties
            new Unequip(op1, ItemSlot.Weapon),
            new MoveInstance(op1, rifle1, Destination.Stash),
            new MoveInstance(op0, rifle1, Destination.Bag),       // 7
            new MoveStack(op1, Medkit, 2, Destination.Stash),     // a new stash row; op1's row empties
            new MoveStack(op0, Medkit, 1, Destination.Bag),       // 7 medkits: 8 entries
            new Equip(op0, vest),
            new EquipFromStash(op0, boots),                       // no free entry
        ]));
        Assert.Equal((10, InventoryRefusal.DestinationFull), (e.Index, e.Code));
        Assert.Equal(before, await SnapshotAsync(a.Id));
        Assert.Equal(1L, await VersionAsync(a.Id));
    }

    [Fact]
    public async Task Several_operations_on_one_item_in_a_batch_persist_the_final_position()
    {
        var a = await NewAccountAsync();
        var (op0, op1) = (a.Ops[0], a.Ops[1]);
        var vest = await StashItemAsync(a.Id, Vest);

        var (version, view) = await edits.ApplyAsync(a.Id, 1, [
            new EquipFromStash(op0, vest),
            new Unequip(op0, ItemSlot.Armor),
            new MoveInstance(op0, vest, Destination.Stash),
            new EquipFromStash(op1, vest),
            new Unequip(op1, ItemSlot.Armor),
            new Equip(op1, vest),
        ]);

        Assert.Equal(2L, version); // once for the whole batch
        Assert.Equal($"{op1}/armor", await WhereAsync(a.Id, vest));
        Assert.Equal(vest, view.Operatives.Single(b => b.OperativeId == op1).Equipment.Armor);
        Assert.Null(view.Operatives.Single(b => b.OperativeId == op0).Equipment.Armor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task A_stale_version_is_VersionMismatch(long expected)
    {
        var a = await NewAccountAsync();
        var vest = await StashItemAsync(a.Id, Vest);
        var before = await SnapshotAsync(a.Id);

        Assert.Equal(PlayerError.VersionMismatch, await PlayerRefusalAsync(edits.ApplyAsync(a.Id, expected, [new EquipFromStash(a.Ops[0], vest)])));
        Assert.Equal(PlayerError.VersionMismatch, await PlayerRefusalAsync(edits.ApplyAsync(a.Id, expected, [new Equip(a.Ops[0], Ghost)]))); // before the operations
        Assert.Equal(before, await SnapshotAsync(a.Id));

        var (version, _) = await edits.ApplyAsync(a.Id, 1, [new EquipFromStash(a.Ops[0], vest)]);
        Assert.Equal(2L, version);
        var after = await SnapshotAsync(a.Id);
        Assert.Equal(PlayerError.VersionMismatch, await PlayerRefusalAsync(edits.ApplyAsync(a.Id, 1, [new Unequip(a.Ops[0], ItemSlot.Armor)]))); // a replay of the old version
        Assert.Equal(after, await SnapshotAsync(a.Id));
    }

    [Fact]
    public async Task An_unprovisioned_account_is_NotProvisioned()
    {
        var bare = await BareAccountAsync();
        IReadOnlyList<InventoryOperation> batch = [new Unequip(1, ItemSlot.Weapon)];

        Assert.Equal(PlayerError.NotProvisioned, await PlayerRefusalAsync(edits.ApplyAsync(bare, 1, batch)));
        Assert.Equal(PlayerError.NotProvisioned, await PlayerRefusalAsync(edits.ApplyAsync(long.MaxValue, 1, batch))); // no such account either
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from inventories where account_id = @bare", new { bare }));

        // An inventories row whose starter is not granted is just as unprovisioned, whatever the version.
        await ExecuteAsSuperuserAsync(db, "insert into inventories (account_id) values (@bare)", new { bare });
        var before = await SnapshotAsync(bare);
        Assert.Equal(PlayerError.NotProvisioned, await PlayerRefusalAsync(edits.ApplyAsync(bare, 1, batch)));
        Assert.Equal(PlayerError.NotProvisioned, await PlayerRefusalAsync(edits.ApplyAsync(bare, 7, batch)));
        Assert.Equal(before, await SnapshotAsync(bare));
    }

    [Fact]
    public async Task A_locked_inventory_is_InventoryLocked()
    {
        var a = await NewAccountAsync();
        var vest = await StashItemAsync(a.Id, Vest);
        var locked = new AlwaysLocked();
        var store = new InventoryEditStore(db.AsApp(), inventoryLock: locked);
        var before = await SnapshotAsync(a.Id);

        Assert.Equal(PlayerError.InventoryLocked, await PlayerRefusalAsync(store.ApplyAsync(a.Id, 1, [new EquipFromStash(a.Ops[0], vest)])));
        Assert.Equal(PlayerError.InventoryLocked, await PlayerRefusalAsync(store.ApplyAsync(a.Id, 99, [new EquipFromStash(a.Ops[0], vest)]))); // before the version (S5-D5)
        Assert.Equal(PlayerError.InventoryLocked, await PlayerRefusalAsync(store.ApplyAsync(a.Id, 1, [new Equip(a.Ops[0], Ghost)])));         // before the operations
        Assert.Equal(3, locked.Calls);
        Assert.Equal(before, await SnapshotAsync(a.Id));

        // NotProvisioned comes first: the seam is not even asked.
        Assert.Equal(PlayerError.NotProvisioned, await PlayerRefusalAsync(store.ApplyAsync(await BareAccountAsync(), 1, [new Equip(a.Ops[0], vest)])));
        Assert.Equal(3, locked.Calls);

        // The seam is asked while this batch's transaction holds the account's row lock.
        var probe = new RowProbe(db.AsSuperuser());
        await new InventoryEditStore(db.AsApp(), inventoryLock: probe).ApplyAsync(a.Id, 1, [new EquipFromStash(a.Ops[0], vest)]);
        Assert.Equal([PostgresErrorCodes.LockNotAvailable], probe.Outcomes);
    }

    [Fact]
    public async Task Equipping_over_an_occupied_slot_writes_in_index_safe_order()
    {
        var a = await NewAccountAsync();
        var (op0, op1) = (a.Ops[0], a.Ops[1]);
        var r0 = await EquippedAsync(a.Id, op0, "weapon");
        var r1 = await EquippedAsync(a.Id, op1, "weapon");
        var spare = await StashItemAsync(a.Id, ServiceRifle);

        // equipFromStash replaces the weapon: the occupant stays in the bag, unequipped.
        var (version, _) = await edits.ApplyAsync(a.Id, 1, [new EquipFromStash(op0, spare)]);
        Assert.Equal(($"{op0}/weapon", $"{op0}/-"), (await WhereAsync(a.Id, spare), await WhereAsync(a.Id, r0)));

        // equip displaces the occupant.
        (version, _) = await edits.ApplyAsync(a.Id, version, [new Equip(op0, r0)]);
        Assert.Equal(($"{op0}/weapon", $"{op0}/-"), (await WhereAsync(a.Id, r0), await WhereAsync(a.Id, spare)));

        // A swap across operatives: whichever row is written first takes a (operative, weapon) pair the other still holds
        // before the batch, so item_one_per_slot would fail unless departing slots are cleared first.
        (version, _) = await edits.ApplyAsync(a.Id, version, [
            new Unequip(op0, ItemSlot.Weapon), new MoveInstance(op0, r0, Destination.Stash),
            new Unequip(op1, ItemSlot.Weapon), new MoveInstance(op1, r1, Destination.Stash),
            new EquipFromStash(op0, r1), new EquipFromStash(op1, r0),
        ]);
        Assert.Equal(4L, version);
        Assert.Equal(($"{op0}/weapon", $"{op1}/weapon"), (await WhereAsync(a.Id, r1), await WhereAsync(a.Id, r0)));
    }

    [Fact]
    public async Task An_emptied_stack_row_is_deleted_and_a_new_location_gets_a_row()
    {
        var a = await NewAccountAsync();
        var (op0, op1) = (a.Ops[0], a.Ops[1]);
        var totals = await TotalsAsync(a.Id);

        var (version, _) = await edits.ApplyAsync(a.Id, 1, [new MoveStack(op0, Medkit, 2, Destination.Stash)]);
        Assert.Null(await StackAsync(a.Id, op0, Medkit)); // deleted, never left at zero
        Assert.Equal(6, await StackAsync(a.Id, null, Medkit));

        (version, _) = await edits.ApplyAsync(a.Id, version, [new MoveStack(op0, Medkit, 6, Destination.Bag)]);
        Assert.Null(await StackAsync(a.Id, null, Medkit));
        Assert.Equal(6, await StackAsync(a.Id, op0, Medkit)); // a new row where there was none

        // Emptied and refilled in one batch: still one row; and the stash gets a row again.
        (version, var view) = await edits.ApplyAsync(a.Id, version, [new MoveStack(op1, Medkit, 2, Destination.Stash), new MoveStack(op1, Medkit, 1, Destination.Bag)]);
        Assert.Equal(4L, version);
        Assert.Equal(1, await StackAsync(a.Id, op1, Medkit));
        Assert.Equal(1, await StackAsync(a.Id, null, Medkit));
        var stacks = await StacksAsync(a.Id);
        Assert.Equal(4, stacks.Count);
        Assert.All(stacks, s => Assert.True(s.Quantity >= 1));
        Assert.Equal(totals, await TotalsAsync(a.Id));
        Assert.Equal([new StackView(Medkit, true, 1)], view.Stash.Stacks);
    }

    [Fact]
    public async Task Instances_keep_their_ids_and_origins_through_every_move()
    {
        var a = await NewAccountAsync();
        var (op0, op1) = (a.Ops[0], a.Ops[1]);
        Task<string> ImmutableAsync() => ScalarAsSuperuserAsync<string>(db,
            """
            select json_agg(json_build_object('id', item_id, 'account', account_id, 'definition', definition_id, 'origin', origin,
                                              'ref', origin_ref, 'created', created_at) order by item_id)::text
            from item_instances where account_id = @account
            """, new { account = a.Id });
        var immutable = await ImmutableAsync();
        var ids = (await ItemsAsync(a.Id)).Select(i => i.ItemId).ToList();
        Assert.All(await ItemsAsync(a.Id), i => Assert.Equal("starter", i.Origin));
        var vest = await StashItemAsync(a.Id, Vest);
        var r0 = await EquippedAsync(a.Id, op0, "weapon");

        IReadOnlyList<InventoryOperation>[] batches =
        [
            [new EquipFromStash(op0, vest)],
            [new Unequip(op0, ItemSlot.Armor), new MoveInstance(op0, vest, Destination.Stash)],
            [new MoveInstance(op1, vest, Destination.Bag), new Equip(op1, vest)],
            [new Unequip(op0, ItemSlot.Weapon), new MoveInstance(op0, r0, Destination.Stash), new EquipFromStash(op1, r0)],
        ];
        var version = 1L;
        foreach (var batch in batches)
        {
            InventoryView view;
            (version, view) = await edits.ApplyAsync(a.Id, version, batch);
            Assert.Equal(immutable, await ImmutableAsync());
            var seen = view.Stash.Instances.Concat(view.Operatives.SelectMany(b => b.Bag.Instances)).Select(i => i.ItemId).Order().ToList();
            Assert.Equal(ids, seen);
        }
        Assert.Equal($"{op1}/armor", await WhereAsync(a.Id, vest));
        Assert.Equal($"{op1}/weapon", await WhereAsync(a.Id, r0));
    }

    [Fact]
    public async Task A_retired_item_can_be_recovered_to_the_stash()
    {
        const string RetiredStim = "item.retired-stim"; // in no catalogue
        var a = await NewAccountAsync();
        var op0 = a.Ops[0];
        var boots = await StashItemAsync(a.Id, Boots);
        var (version, _) = await edits.ApplyAsync(a.Id, 1, [new EquipFromStash(op0, boots)]); // equipped while the catalogue knew it
        await ExecuteAsSuperuserAsync(db, "insert into item_stacks (account_id, operative_id, definition_id, quantity) values (@account, @op0, @RetiredStim, 3)",
            new { account = a.Id, op0, RetiredStim });

        var doc = GameCatalogue.Embedded.Document;
        var retired = GameCatalogue.Load(CanonicalCatalogueJson.ToBytes(doc with
        {
            Items = doc.Items.Where(i => i.Id != Boots).ToList(),
            LootTable = doc.LootTable with { Entries = doc.LootTable.Entries.Where(e => e.ItemId != Boots).ToList() },
            Starter = doc.Starter with { Stash = doc.Starter.Stash.Where(s => s.ItemId != Boots).ToList() },
        }));
        var store = new InventoryEditStore(db.AsApp(), retired);
        var origin = (await ItemsAsync(a.Id)).Single(i => i.ItemId == boots);

        Assert.Equal((0, InventoryRefusal.UnknownItem), await CodeAsync([new Equip(op0, boots)]));

        InventoryView view;
        (version, view) = await store.ApplyAsync(a.Id, version, [
            new Unequip(op0, ItemSlot.Utility), new MoveInstance(op0, boots, Destination.Stash), new MoveStack(op0, RetiredStim, 2, Destination.Stash)]);
        Assert.Equal(3L, version);
        Assert.Equal("stash/-", await WhereAsync(a.Id, boots));
        Assert.Equal(origin with { OperativeId = null, EquippedSlot = null }, (await ItemsAsync(a.Id)).Single(i => i.ItemId == boots));
        Assert.Equal((1, 2), (await StackAsync(a.Id, op0, RetiredStim), await StackAsync(a.Id, null, RetiredStim)));
        Assert.Contains(new InstanceView(boots, Boots, false), view.Stash.Instances);
        Assert.Contains(new StackView(RetiredStim, false, 2), view.Stash.Stacks);

        var before = await SnapshotAsync(a.Id);
        Assert.Equal((0, InventoryRefusal.UnknownItem), await CodeAsync([new MoveInstance(op0, boots, Destination.Bag)]));
        Assert.Equal((0, InventoryRefusal.UnknownItem), await CodeAsync([new EquipFromStash(op0, boots)]));
        Assert.Equal((0, InventoryRefusal.UnknownItem), await CodeAsync([new MoveStack(op0, RetiredStim, 1, Destination.Bag)]));
        Assert.Equal(before, await SnapshotAsync(a.Id));
        Assert.Equal(3L, await VersionAsync(a.Id));

        async Task<(int, InventoryRefusal)> CodeAsync(IReadOnlyList<InventoryOperation> batch)
        {
            var e = await RefusedAsync(store.ApplyAsync(a.Id, version, batch));
            return (e.Index, e.Code);
        }
    }

    // --- isolation between accounts ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_accounts_item_is_NotFound_and_its_operative_UnknownOperative()
    {
        var a = await NewAccountAsync();
        var b = await NewAccountAsync();
        var aVest = await StashItemAsync(a.Id, Vest);
        var aRifle = await EquippedAsync(a.Id, a.Ops[0], "weapon");
        var bVest = await StashItemAsync(b.Id, Vest);
        var bRifle = await EquippedAsync(b.Id, b.Ops[0], "weapon");
        var (beforeA, beforeB) = (await SnapshotAsync(a.Id), await SnapshotAsync(b.Id));
        var op = a.Ops[0];
        InventoryOperation valid = new MoveStack(op, Medkit, 1, Destination.Bag);

        foreach (var item in new[] { bVest, bRifle, Ghost }) // foreign and nonexistent alike
            foreach (var foreign in new InventoryOperation[]
                     {
                         new Equip(op, item), new MoveInstance(op, item, Destination.Stash), new MoveInstance(op, item, Destination.Bag), new EquipFromStash(op, item),
                     })
            {
                var e = await RefusedAsync(edits.ApplyAsync(a.Id, 1, [valid, foreign]));
                Assert.Equal((1, InventoryRefusal.NotFound), (e.Index, e.Code));
            }

        foreach (var operative in new[] { b.Ops[0], long.MaxValue })
            foreach (var foreign in new InventoryOperation[]
                     {
                         new Equip(operative, aRifle), new Unequip(operative, ItemSlot.Weapon), new MoveInstance(operative, aRifle, Destination.Stash),
                         new MoveStack(operative, Medkit, 1, Destination.Bag), new EquipFromStash(operative, aVest), new Equip(operative, bRifle),
                     })
            {
                var e = await RefusedAsync(edits.ApplyAsync(a.Id, 1, [valid, foreign]));
                Assert.Equal((1, InventoryRefusal.UnknownOperative), (e.Index, e.Code));
            }

        Assert.Equal(beforeA, await SnapshotAsync(a.Id));
        Assert.Equal(beforeB, await SnapshotAsync(b.Id));
    }

    [Fact]
    public async Task Nothing_of_another_account_is_read_or_written()
    {
        var a = await NewAccountAsync();
        var b = await NewAccountAsync();
        var bItems = (await ItemsAsync(b.Id)).Select(i => i.ItemId).ToHashSet();
        var beforeB = await SnapshotAsync(b.Id);
        var (op0, op1) = (a.Ops[0], a.Ops[1]);
        var vest = await StashItemAsync(a.Id, Vest);
        var r0 = await EquippedAsync(a.Id, op0, "weapon");

        // Every row of B is locked by another transaction for the whole batch. A's batch, with a 300 ms lock timeout, would
        // fail if it locked, updated or deleted any of them.
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account = b.Id }, tx);
        await holder.ExecuteAsync("select 1 from operatives where account_id = @account for update", new { account = b.Id }, tx);
        await holder.ExecuteAsync("select 1 from item_instances where account_id = @account for update", new { account = b.Id }, tx);
        await holder.ExecuteAsync("select 1 from item_stacks where account_id = @account for update", new { account = b.Id }, tx);

        var bounded = new InventoryEditStore(db.AsAppWith(Short));
        var (version, view) = await bounded.ApplyAsync(a.Id, 1, [
            new EquipFromStash(op0, vest), new Unequip(op0, ItemSlot.Weapon), new MoveInstance(op0, r0, Destination.Stash),
            new EquipFromStash(op1, r0), new MoveStack(op0, Medkit, 2, Destination.Stash), new MoveStack(op1, Medkit, 3, Destination.Bag),
        ]).WaitAsync(TimeSpan.FromSeconds(10));
        await tx.RollbackAsync();

        Assert.Equal(2L, version);
        Assert.Equal(beforeB, await SnapshotAsync(b.Id)); // byte for byte, xmin included
        Assert.Equal(1L, await VersionAsync(b.Id));
        var seen = view.Stash.Instances.Concat(view.Operatives.SelectMany(o => o.Bag.Instances)).Select(i => i.ItemId).ToList();
        Assert.DoesNotContain(seen, bItems.Contains);
        Assert.Equal(a.Ops, view.Operatives.Select(o => o.OperativeId));
    }

    // --- concurrency (Review Focus 2) -------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_batches_with_the_same_version_one_wins_one_is_409()
    {
        var a = await NewAccountAsync();
        var vest = await StashItemAsync(a.Id, Vest);
        var boots = await StashItemAsync(a.Id, Boots);
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account = a.Id }, tx);

        async Task<PlayerError?> AttemptAsync(IReadOnlyList<InventoryOperation> batch)
        {
            try { await edits.ApplyAsync(a.Id, 1, batch); return null; }
            catch (PlayerException e) { return e.Error; }
        }
        var first = Task.Run(() => AttemptAsync([new EquipFromStash(a.Ops[0], vest)]));
        var second = Task.Run(() => AttemptAsync([new EquipFromStash(a.Ops[1], boots)]));
        await WaitForWaitersAsync(db, 2); // both are queued behind the held lock with the same expected version
        await tx.RollbackAsync();

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r == PlayerError.VersionMismatch);
        Assert.Equal(2L, await VersionAsync(a.Id)); // +1 once
        var winnerFirst = results[0] is null;
        Assert.Equal(winnerFirst ? $"{a.Ops[0]}/armor" : "stash/-", await WhereAsync(a.Id, vest));
        Assert.Equal(winnerFirst ? "stash/-" : $"{a.Ops[1]}/utility", await WhereAsync(a.Id, boots));
    }

    [Fact]
    public async Task A_batch_and_a_pick_share_the_version()
    {
        var a = await NewAccountAsync();
        var vest = await StashItemAsync(a.Id, Vest);
        await ExecuteAsSuperuserAsync(db, "update operatives set experience = 700 where operative_id = @operative", new { operative = a.Ops[0] }); // picks pending
        var picks = new PickStore(db.AsApp());
        Task<long> ChoicesAsync() => ScalarAsSuperuserAsync<long>(db, "select count(*) from operative_choices where operative_id = @operative", new { operative = a.Ops[0] });
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account = a.Id }, tx);

        async Task<PlayerError?> AttemptAsync(Func<Task> call)
        {
            try { await call(); return null; }
            catch (PlayerException e) { return e.Error; }
        }
        var pick = Task.Run(() => AttemptAsync(() => picks.PickAsync(a.Id, a.Ops[0], "combat", 1)));
        var batch = Task.Run(() => AttemptAsync(() => edits.ApplyAsync(a.Id, 1, [new EquipFromStash(a.Ops[0], vest)])));
        await WaitForWaitersAsync(db, 2);
        await tx.RollbackAsync();

        var results = await Task.WhenAll(pick, batch).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r == PlayerError.VersionMismatch);
        Assert.Equal(2L, await VersionAsync(a.Id));
        var pickWon = results[0] is null;
        Assert.Equal(pickWon ? 1L : 0L, await ChoicesAsync());
        Assert.Equal(pickWon ? "stash/-" : $"{a.Ops[0]}/armor", await WhereAsync(a.Id, vest));

        // The loser, sent again by its caller with the version the winner produced, applies on top of it.
        if (pickWon) await edits.ApplyAsync(a.Id, 2, [new EquipFromStash(a.Ops[0], vest)]);
        else await picks.PickAsync(a.Id, a.Ops[0], "combat", 2);
        Assert.Equal(3L, await VersionAsync(a.Id));
        Assert.Equal(1L, await ChoicesAsync());
        Assert.Equal($"{a.Ops[0]}/armor", await WhereAsync(a.Id, vest));
    }

    [Fact]
    public async Task Batches_on_one_account_serialize_behind_the_row_lock()
    {
        var a = await NewAccountAsync();
        var vest = await StashItemAsync(a.Id, Vest);
        var boots = await StashItemAsync(a.Id, Boots);
        var gate = new Gate();
        var gated = new InventoryEditStore(db.AsApp(), inventoryLock: gate);

        var first = Task.Run(() => gated.ApplyAsync(a.Id, 1, [new EquipFromStash(a.Ops[0], vest)]));
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15)); // the first batch is inside its transaction, holding the row lock
        var second = Task.Run(() => edits.ApplyAsync(a.Id, 2, [new EquipFromStash(a.Ops[1], boots)])); // expects the first's version
        await WaitForWaitersAsync(db, 1);
        Assert.False(second.IsCompleted);
        Assert.Equal(1L, await VersionAsync(a.Id));

        gate.Release.SetResult();
        var (v1, _) = await first.WaitAsync(TimeSpan.FromSeconds(15));
        var (v2, view) = await second.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal((2L, 3L), (v1, v2));
        Assert.Equal(vest, view.Operatives.Single(o => o.OperativeId == a.Ops[0]).Equipment.Armor); // the second saw the first's commit
        Assert.Equal(boots, view.Operatives.Single(o => o.OperativeId == a.Ops[1]).Equipment.Utility);
    }

    [Fact]
    public async Task A_batch_behind_a_held_lock_times_out_and_writes_nothing()
    {
        var a = await NewAccountAsync();
        var vest = await StashItemAsync(a.Id, Vest);
        var before = await SnapshotAsync(a.Id);
        var bounded = new InventoryEditStore(db.AsAppWith(Short));
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account = a.Id }, tx);

        var e = await Assert.ThrowsAsync<PostgresException>(() => bounded.ApplyAsync(a.Id, 1, [new EquipFromStash(a.Ops[0], vest)]).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
        Assert.Equal(DbFailureKind.Busy, DbFailure.Classify(e));
        await tx.RollbackAsync();
        Assert.Equal(before, await SnapshotAsync(a.Id)); // nothing written, nothing replayed after the lock was released
    }

    [Fact]
    public async Task An_edit_waits_behind_an_operator_lock_on_the_account_and_writes_nothing()
    {
        var a = await NewAccountAsync();
        var op0 = a.Ops[0];
        var (version, _) = await edits.ApplyAsync(a.Id, 1, [new MoveStack(op0, Medkit, 2, Destination.Stash)]); // op0 has no medkit row now
        var before = await SnapshotAsync(a.Id);
        var bounded = new InventoryEditStore(db.AsAppWith(Short));
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("select 1 from accounts where account_id = @account for update", new { account = a.Id }, tx); // an operator lifecycle lock

        // The batch inserts a stack row into op0's bag.
        var e = await Assert.ThrowsAsync<PostgresException>(() =>
            bounded.ApplyAsync(a.Id, version, [new MoveStack(op0, Medkit, 1, Destination.Bag)]).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, e.SqlState);
        await tx.RollbackAsync();
        Assert.Equal(before, await SnapshotAsync(a.Id));
    }

    [Fact]
    public async Task An_edit_and_a_backfill_on_one_account_do_not_deadlock()
    {
        var a = await NewAccountAsync();
        var op0 = a.Ops[0];
        var (version, _) = await edits.ApplyAsync(a.Id, 1, [new MoveStack(op0, Medkit, 2, Destination.Stash)]); // the next batch inserts a row
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("set local lock_timeout = '5s'", transaction: tx); // a deadlock or a wait fails fast instead of hanging
        await holder.ExecuteAsync("select 1 from accounts where account_id = @account for update", new { account = a.Id }, tx); // backfill's first lock

        var batch = Task.Run(() => edits.ApplyAsync(a.Id, version, [new MoveStack(op0, Medkit, 1, Destination.Bag)]));
        await WaitForWaitersAsync(db, 1); // the batch queues

        // ...on the accounts row: it holds no inventories lock yet, so another connection can still take that row NOWAIT.
        await using (var probe = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var probeTx = await probe.BeginTransactionAsync();
            await probe.ExecuteAsync("select 1 from inventories where account_id = @account for update nowait", new { account = a.Id }, probeTx);
            await probeTx.RollbackAsync();
        }

        // Backfill's second lock, in the documented order: granted at once, no deadlock.
        await holder.ExecuteAsync("select 1 from inventories where account_id = @account for update", new { account = a.Id }, tx);
        Assert.False(batch.IsCompleted);
        await tx.RollbackAsync();

        var (after, view) = await batch.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(version + 1, after);
        Assert.Equal(1, await StackAsync(a.Id, op0, Medkit));
        Assert.Equal([new StackView(Medkit, true, 1)], view.Operatives.Single(o => o.OperativeId == op0).Bag.Stacks);
    }

    [Fact]
    public async Task Concurrent_batches_conserve_units()
    {
        const int Rounds = 4;
        var a = await NewAccountAsync();
        var rifles = new long[a.Ops.Length];
        for (var i = 0; i < a.Ops.Length; i++) rifles[i] = await EquippedAsync(a.Id, a.Ops[i], "weapon");
        var totals = await TotalsAsync(a.Id);
        var mismatches = 0;

        // One worker per operative, all at once. Each batch is valid whatever the interleaving (a worker alone touches its
        // operative's bag, and the stash never drops below its starting 4 medkits); only the version can refuse one, and then
        // the test, never the store, reads the new version and sends the batch again.
        async Task WorkerAsync(int i)
        {
            var op = a.Ops[i];
            for (int round = 0, attempts = 0; round < Rounds;)
            {
                if (++attempts > 500) throw new TimeoutException("A worker never got a batch through.");
                var version = (await reads.GetInventoryVersionAsync(a.Id))!.Value;
                IReadOnlyList<InventoryOperation> batch = round % 2 == 0
                    ? [new Unequip(op, ItemSlot.Weapon), new MoveStack(op, Medkit, 1, Destination.Stash)]
                    : [new Equip(op, rifles[i]), new MoveStack(op, Medkit, 1, Destination.Bag)];
                try
                {
                    await edits.ApplyAsync(a.Id, version, batch);
                    round++;
                }
                catch (PlayerException e) when (e.Error == PlayerError.VersionMismatch) { Interlocked.Increment(ref mismatches); }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, a.Ops.Length).Select(i => Task.Run(() => WorkerAsync(i)))).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1L + Rounds * a.Ops.Length, await VersionAsync(a.Id)); // one bump per successful batch, none per refused one
        Assert.Equal(totals, await TotalsAsync(a.Id));
        for (var i = 0; i < a.Ops.Length; i++)
        {
            Assert.Equal($"{a.Ops[i]}/weapon", await WhereAsync(a.Id, rifles[i]));
            Assert.Equal(2, await StackAsync(a.Id, a.Ops[i], Medkit));
        }
        Assert.Equal(4, await StackAsync(a.Id, null, Medkit));
    }

    // --- failure and conservation -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failure_while_writing_rolls_back_the_whole_batch()
    {
        var a = await NewAccountAsync();
        var (op0, op1) = (a.Ops[0], a.Ops[1]);
        var vest = await StashItemAsync(a.Id, Vest);
        var r0 = await EquippedAsync(a.Id, op0, "weapon");
        var (version, _) = await edits.ApplyAsync(a.Id, 1, [new MoveStack(op0, Medkit, 2, Destination.Stash)]); // op0 now has no medkit row
        var before = await SnapshotAsync(a.Id);
        IReadOnlyList<InventoryOperation> batch =
        [
            new EquipFromStash(op0, vest), new Unequip(op0, ItemSlot.Weapon), new MoveInstance(op0, r0, Destination.Stash), // instance writes
            new MoveStack(op1, Medkit, 1, Destination.Stash),                                                              // two stack updates
            new MoveStack(op0, Medkit, 3, Destination.Bag),                                                                // the insert: the last write
        ];

        // A test-only trigger in this test database, created and dropped as the superuser: it fails the batch's last stack write.
        await ExecuteAsSuperuserAsync(db,
            "create function bg_test_fail_stack_insert() returns trigger language plpgsql as $$ begin raise exception 'injected failure'; end $$");
        await ExecuteAsSuperuserAsync(db,
            "create trigger bg_test_fail_stack_insert before insert on item_stacks for each row execute function bg_test_fail_stack_insert()");
        try
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => edits.ApplyAsync(a.Id, version, batch));
            Assert.Equal(PostgresErrorCodes.RaiseException, e.SqlState);
            Assert.Equal(DbFailureKind.Unexpected, DbFailure.Classify(e)); // a 500
            Assert.Equal(before, await SnapshotAsync(a.Id)); // no instance moved, no stack changed, the version unchanged
        }
        finally
        {
            await ExecuteAsSuperuserAsync(db, "drop trigger if exists bg_test_fail_stack_insert on item_stacks");
            await ExecuteAsSuperuserAsync(db, "drop function if exists bg_test_fail_stack_insert()");
        }

        var (after, _) = await edits.ApplyAsync(a.Id, version, batch); // the same batch, sent again by the test, now applies
        Assert.Equal(version + 1, after);
        Assert.Equal($"{op0}/armor", await WhereAsync(a.Id, vest));
        Assert.Equal(3, await StackAsync(a.Id, op0, Medkit));
    }

    [Fact]
    public async Task Conservation_holds_for_every_vector_case()
    {
        var cases = InventoryVectors.Load();
        Assert.True(cases.Count >= 40, "the vector file lost cases");
        Assert.Contains(cases, c => c.Scope == "backend");
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Each case has its own account, so the cases are independent: four at a time keeps the run short over the tunnel.
        using var slots = new SemaphoreSlim(4);
        await Task.WhenAll(cases.Select(async c =>
        {
            await slots.WaitAsync();
            try { await ReplayAsync(c); }
            catch (Exception e) { failures.Add($"{c.Name}: {e.GetType().Name}: {e.Message}"); }
            finally { slots.Release(); }
        }));
        Assert.True(failures.IsEmpty, string.Join("\n\n", failures.Order(StringComparer.Ordinal)));

        async Task ReplayAsync(VectorCase c)
        {
            var (account, operatives, items) = await SeedAsync(c);
            long Operative(string label) => label == "opX" ? long.MaxValue - 1 : operatives[label];
            long Item(string label) => label == "ghost" ? Ghost : items[label];
            var store = new InventoryEditStore(db.AsApp(), c.Catalogue());
            var operations = c.BuildOperations(Operative, Item);
            var before = await SnapshotAsync(account);
            var initial = await RowsAsync(account);
            if (InventoryVectors.Render(c.Initial) != StoredState(initial, operatives, items)) throw new InvalidOperationException("the seeded state is not the case's");

            if (c.RefusedCode is { } code)
            {
                var e = await RefusedAsync(store.ApplyAsync(account, 1, operations));
                if (e.Index != c.RefusedIndex || e.Code.ToString() != code)
                    throw new InvalidOperationException($"refused at {e.Index} with {e.Code}, expected {c.RefusedIndex} with {code}");
                if (before != await SnapshotAsync(account)) throw new InvalidOperationException("a refused batch wrote");
                return;
            }

            var (version, view) = await store.ApplyAsync(account, 1, operations);
            if (version != 2) throw new InvalidOperationException($"the version went to {version}");
            var expected = InventoryVectors.Render(c.Expected!);
            var rows = await RowsAsync(account);
            var stored = StoredState(rows, operatives, items);
            if (expected != stored) throw new InvalidOperationException($"stored state differs\nexpected:\n{expected}\nstored:\n{stored}");
            if (Totals(initial) != Totals(rows)) throw new InvalidOperationException("the totals changed");
            if (view.Version != 2 || view.Operatives.Count != operatives.Count) throw new InvalidOperationException("the view is not the stored inventory");
        }
    }

    /// <summary>Mints a provisioned account holding exactly the case's initial state, as the superuser.</summary>
    async Task<(long Account, Dictionary<string, long> Operatives, Dictionary<string, long> Items)> SeedAsync(VectorCase c)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id", transaction: tx);
        await conn.ExecuteAsync(
            "insert into inventories (account_id, starter_granted, starter_catalogue_version, starter_granted_at) values (@account, true, @version, now())",
            new { account, version = GameCatalogue.Embedded.Version }, tx);
        var definition = GameCatalogue.Embedded.Document.Operatives[0].Id;
        var operatives = new Dictionary<string, long>();
        foreach (var label in c.Operatives)
            operatives[label] = await conn.ExecuteScalarAsync<long>(
                "insert into operatives (account_id, definition_id, origin, origin_ref) values (@account, @definition, 'starter', @originRef) returning operative_id",
                new { account, definition, originRef = $"vector:{account}:{label}" }, tx);
        var items = new Dictionary<string, long>();
        foreach (var i in c.Initial.Instances)
            items[i.Label] = await conn.ExecuteScalarAsync<long>(
                """
                insert into item_instances (account_id, definition_id, operative_id, equipped_slot, origin, origin_ref)
                values (@account, @definition, @operative, @slot, 'dev_grant', @originRef) returning item_id
                """,
                new
                {
                    account, definition = i.DefinitionId, operative = i.Holder is null ? (long?)null : operatives[i.Holder],
                    slot = i.Equipped is { } s ? CanonicalCatalogueJson.Word(s) : null, originRef = $"vector:{account}:{i.Label}",
                }, tx);
        foreach (var s in c.Initial.Stacks)
            await conn.ExecuteAsync(
                "insert into item_stacks (account_id, operative_id, definition_id, quantity) values (@account, @operative, @definition, @quantity)",
                new { account, operative = s.Holder is null ? (long?)null : operatives[s.Holder], definition = s.DefinitionId, quantity = s.Quantity }, tx);
        await tx.CommitAsync();
        return (account, operatives, items);
    }

    /// <summary>The account's stored rows as the vector rendering, with ids mapped back to the case's labels.</summary>
    static string StoredState((List<ItemRow> Items, List<StackRow> Stacks) rows, Dictionary<string, long> operatives, Dictionary<string, long> items)
    {
        var operativeLabel = operatives.ToDictionary(p => p.Value, p => p.Key);
        var itemLabel = items.ToDictionary(p => p.Value, p => p.Key);
        string? Holder(long? id) => id is { } h ? operativeLabel[h] : null;
        ItemSlot? Slot(string? slot) => slot is null ? null : Enum.Parse<ItemSlot>(slot, true);
        var state = new VectorState(
            rows.Items.Select(i => new VectorInstance(itemLabel[i.ItemId], i.DefinitionId, Holder(i.OperativeId), Slot(i.EquippedSlot))).ToList(),
            rows.Stacks.Select(s => new VectorStack(Holder(s.OperativeId), s.DefinitionId, s.Quantity)).ToList());
        return InventoryVectors.Render(state);
    }
}
