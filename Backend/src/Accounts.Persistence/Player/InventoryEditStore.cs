using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;
using static Blackglass.Accounts.Persistence.Player.InventoryViewReader;

namespace Blackglass.Accounts.Persistence.Player;

/// <summary>
/// An atomic, version-checked inventory batch (owner rulings S5-D1 to S5-D8), in one READ COMMITTED transaction. Lock order
/// (the documented accounts, inventories, owned rows): first the accounts row FOR KEY SHARE, which conflicts only with an
/// operator lifecycle lock (FOR UPDATE, as the starter backfill takes before its inventories lock) and is needed because the
/// item_stacks insert's foreign-key check would otherwise take it after the inventories lock; then the inventories row FOR
/// UPDATE; then the owned rows. Every writer of owned rows holds the inventories lock, so the rows read under it cannot
/// change before commit.
/// <list type="number">
/// <item>Lock the accounts row (key share) and the inventories row, then refuse in this order: <see cref="PlayerError.NotProvisioned"/>, <see cref="PlayerError.InventoryLocked"/>
/// (the <see cref="IInventoryLock"/> seam, asked under the lock), <see cref="PlayerError.VersionMismatch"/>.</item>
/// <item>Load every operative, instance and stack of the account (only <c>account_id = @a</c> rows, so the queries never
/// depend on whether a guessed id exists in another account) and replay the batch with <see cref="InventoryEditor"/>. A
/// refusal is <see cref="InventoryRefusedException"/> with nothing written.</item>
/// <item>Write the difference: clear every departing equipped slot, then set each changed instance's operative and slot
/// (so item_one_per_slot never sees two items in one slot, even transiently), then delete, update and insert the changed
/// stack rows by (operative, definition). Every statement is scoped to the account and must touch exactly the rows it
/// expects.</item>
/// <item>Re-read the rows: the instance id set and the per-definition totals must equal the loaded ones (conservation), and
/// the rows must equal the replayed final state.</item>
/// <item>Bump the version once, read the view in the same transaction, commit.</item>
/// </list>
/// Any other failure (a row count or conservation mismatch is an <see cref="InventoryInvariantException"/>) rolls the whole
/// batch back. Nothing here ever retries or replays a batch (S5-D6): not on a version mismatch, a busy or unavailable
/// database, or an ambiguous commit.
/// </summary>
public sealed class InventoryEditStore(NpgsqlDataSource db, GameCatalogue? catalogue = null, IInventoryLock? inventoryLock = null)
{
    readonly GameCatalogue known = catalogue ?? GameCatalogue.Embedded;
    readonly IInventoryLock mission = inventoryLock ?? new NoParticipationsYet();

    public async Task<(long Version, InventoryView Inventory)> ApplyAsync(long accountId, long expectedVersion, IReadOnlyList<InventoryOperation> operations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0) throw new ArgumentException("A batch needs at least one operation.", nameof(operations)); // the route refuses it first (S5-D6)

        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // The documented lock order starts at accounts. The stack insert's foreign-key check on item_stacks.account_id takes
        // FOR KEY SHARE on the accounts row; taken there it would follow the inventories lock, the reverse of the starter
        // backfill (accounts FOR UPDATE, then inventories). So it is taken first, on every call, refused ones included.
        var account = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "select 1 from accounts where account_id = @accountId for key share", new { accountId }, tx, cancellationToken: ct));
        if (account is null) throw NotProvisioned(); // defensive: accounts are never deleted, and no account has no inventory either
        var inventory = await conn.QuerySingleOrDefaultAsync<(long Version, bool StarterGranted)?>(new CommandDefinition(
            "select version, starter_granted from inventories where account_id = @accountId for update", new { accountId }, tx, cancellationToken: ct));
        if (inventory is not { StarterGranted: true }) throw NotProvisioned();
        if (await mission.IsLockedAsync(conn, tx, accountId, ct)) // while the row lock is held, so the answer cannot change under the batch
            throw new PlayerException(PlayerError.InventoryLocked, "The account is in a mission; its inventory cannot change.");
        if (inventory.Value.Version != expectedVersion)
            throw new PlayerException(PlayerError.VersionMismatch, "The inventory changed since it was read.");

        var before = await LoadAsync(conn, tx, accountId, ct);
        var outcome = InventoryEditor.Apply(before, known.Document, operations);
        if (!outcome.Applied) throw new InventoryRefusedException(outcome.RefusedIndex!.Value, outcome.Refusal!.Value);
        var after = outcome.After!;

        await WriteInstancesAsync(conn, tx, accountId, before, after, ct);
        await WriteStacksAsync(conn, tx, accountId, before, after, ct);
        CheckStored(before, after, await LoadAsync(conn, tx, accountId, ct));

        var version = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "update inventories set version = version + 1 where account_id = @accountId returning version", new { accountId }, tx, cancellationToken: ct));
        if (version != expectedVersion + 1) throw new InventoryInvariantException("The inventory version did not advance by exactly one.");
        var view = await ReadAsync(known, conn, tx, accountId, version, locked: false, ct); // the seam said unlocked under this lock
        await tx.CommitAsync(ct);
        return (version, view);
    }

    static PlayerException NotProvisioned() =>
        new(PlayerError.NotProvisioned, "The account has no starter roster or inventory yet.");

    /// <summary>Every operative, instance and stack of the account: an omitted operative would make its rows look foreign.</summary>
    static async Task<InventoryModel> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct)
    {
        var operatives = await ReadOperativeIdsAsync(conn, tx, accountId, ct);
        var instances = (await ReadInstancesAsync(conn, tx, accountId, ct))
            .Select(i => new HeldInstance(i.ItemId, i.DefinitionId, i.OperativeId, ToSlot(i.EquippedSlot))).ToList();
        var stacks = (await ReadStacksAsync(conn, tx, accountId, ct))
            .Select(s => new HeldStack(s.OperativeId, s.DefinitionId, s.Quantity)).ToList();
        return new InventoryModel(operatives, instances, stacks);
    }

    /// <summary>
    /// First clears the slot of every instance that leaves its (operative, slot), then writes each changed instance's final
    /// operative and slot. After the first statement no departing pair is held, and each later statement only adds a pair of
    /// the final state, which is unique: the one-item-per-slot index is never violated, whatever the order.
    /// </summary>
    static async Task WriteInstancesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, InventoryModel before, InventoryModel after, CancellationToken ct)
    {
        var prior = before.Instances.ToDictionary(i => i.ItemId);
        var changed = after.Instances
            .Where(i => prior[i.ItemId] is var p && (p.OperativeId != i.OperativeId || p.Equipped != i.Equipped))
            .OrderBy(i => i.ItemId).ToList();

        var departing = changed.Where(i => prior[i.ItemId].Equipped is not null).Select(i => i.ItemId).ToArray();
        if (departing.Length > 0)
            await ExecuteExactlyAsync(conn, tx, departing.Length,
                "update item_instances set equipped_slot = null where account_id = @accountId and item_id = any(@departing) and equipped_slot is not null",
                new { accountId, departing }, ct);

        foreach (var instance in changed)
        {
            var p = prior[instance.ItemId];
            var cleared = p.Equipped is null ? p : p with { Equipped = null };
            if (cleared.OperativeId == instance.OperativeId && cleared.Equipped == instance.Equipped) continue; // clearing was the whole change
            await ExecuteExactlyAsync(conn, tx, 1,
                "update item_instances set operative_id = @operativeId, equipped_slot = @slot where account_id = @accountId and item_id = @itemId",
                new { accountId, itemId = instance.ItemId, operativeId = instance.OperativeId, slot = ToWord(instance.Equipped) }, ct);
        }
    }

    /// <summary>
    /// Stack rows have no id: they are matched by (operative, definition), never by position. A row the batch emptied is
    /// deleted (never written at zero), a changed one updated, a new location inserted. A stash row (operative_id null) and a
    /// bag row use separate statements, since <c>operative_id = null</c> matches nothing. The prior quantity is part of
    /// each predicate, so a row that is not what was loaded fails the row count instead of being overwritten.
    /// </summary>
    static async Task WriteStacksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, InventoryModel before, InventoryModel after, CancellationToken ct)
    {
        var was = before.Stacks.ToDictionary(s => (s.OperativeId, s.DefinitionId));
        var now = after.Stacks.ToDictionary(s => (s.OperativeId, s.DefinitionId));
        static IOrderedEnumerable<HeldStack> Ordered(IEnumerable<HeldStack> stacks) =>
            stacks.OrderBy(s => s.OperativeId ?? 0).ThenBy(s => s.DefinitionId, StringComparer.Ordinal);

        foreach (var gone in Ordered(was.Values.Where(s => !now.ContainsKey((s.OperativeId, s.DefinitionId)))))
            await ExecuteExactlyAsync(conn, tx, 1, gone.OperativeId is null
                    ? "delete from item_stacks where account_id = @accountId and operative_id is null and definition_id = @definitionId and quantity = @prior"
                    : "delete from item_stacks where account_id = @accountId and operative_id = @operativeId and definition_id = @definitionId and quantity = @prior",
                new { accountId, operativeId = gone.OperativeId, definitionId = gone.DefinitionId, prior = gone.Quantity }, ct);

        foreach (var row in Ordered(now.Values.Where(s => was.TryGetValue((s.OperativeId, s.DefinitionId), out var p) && p.Quantity != s.Quantity)))
            await ExecuteExactlyAsync(conn, tx, 1, row.OperativeId is null
                    ? "update item_stacks set quantity = @quantity where account_id = @accountId and operative_id is null and definition_id = @definitionId and quantity = @prior"
                    : "update item_stacks set quantity = @quantity where account_id = @accountId and operative_id = @operativeId and definition_id = @definitionId and quantity = @prior",
                new { accountId, operativeId = row.OperativeId, definitionId = row.DefinitionId, quantity = Positive(row), prior = was[(row.OperativeId, row.DefinitionId)].Quantity }, ct);

        foreach (var row in Ordered(now.Values.Where(s => !was.ContainsKey((s.OperativeId, s.DefinitionId)))))
            await ExecuteExactlyAsync(conn, tx, 1,
                "insert into item_stacks (account_id, operative_id, definition_id, quantity) values (@accountId, @operativeId, @definitionId, @quantity)",
                new { accountId, operativeId = row.OperativeId, definitionId = row.DefinitionId, quantity = Positive(row) }, ct);
    }

    static int Positive(HeldStack row) =>
        row.Quantity >= 1 ? row.Quantity : throw new InventoryInvariantException("A stack row would be written with no units.");

    static async Task ExecuteExactlyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int expected, string sql, object args, CancellationToken ct)
    {
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct));
        if (affected != expected) throw new InventoryInvariantException($"An inventory write touched {affected} rows instead of {expected}.");
    }

    /// <summary>
    /// The rows as stored after the writes: the same instance ids, the same instance count and stack total of every
    /// definition as were loaded (nothing created or destroyed), and exactly the replayed final state.
    /// </summary>
    static void CheckStored(InventoryModel before, InventoryModel after, InventoryModel stored)
    {
        if (!before.Instances.Select(i => i.ItemId).Order().SequenceEqual(stored.Instances.Select(i => i.ItemId).Order()))
            throw new InventoryInvariantException("The stored instance ids differ from the loaded ones.");
        if (Totals(before) != Totals(stored))
            throw new InventoryInvariantException("The stored per-definition totals differ from the loaded ones.");

        var expected = after.Instances.ToDictionary(i => i.ItemId);
        if (stored.Instances.Any(i => expected[i.ItemId] != i))
            throw new InventoryInvariantException("A stored instance differs from the replayed final state.");
        var rows = after.Stacks.ToDictionary(s => (s.OperativeId, s.DefinitionId), s => s.Quantity);
        if (stored.Stacks.Count != rows.Count || stored.Stacks.Any(s => !rows.TryGetValue((s.OperativeId, s.DefinitionId), out var q) || q != s.Quantity))
            throw new InventoryInvariantException("The stored stacks differ from the replayed final state.");
    }

    static string Totals(InventoryModel model) => string.Join("\n",
        model.Instances.GroupBy(i => i.DefinitionId, StringComparer.Ordinal).Select(g => $"i {g.Key} {g.LongCount()}")
            .Concat(model.Stacks.GroupBy(s => s.DefinitionId, StringComparer.Ordinal).Select(g => $"s {g.Key} {g.Sum(s => (long)s.Quantity)}"))
            .Order(StringComparer.Ordinal));

    static ItemSlot? ToSlot(string? stored) => stored switch
    {
        null => null,
        "weapon" => ItemSlot.Weapon,
        "armor" => ItemSlot.Armor,
        "utility" => ItemSlot.Utility,
        _ => throw new InventoryInvariantException("An instance is stored in an unknown slot."),
    };

    static string? ToWord(ItemSlot? slot) => slot switch
    {
        null => null,
        ItemSlot.Weapon => "weapon",
        ItemSlot.Armor => "armor",
        ItemSlot.Utility => "utility",
        _ => throw new InventoryInvariantException("An instance cannot be equipped in no slot."),
    };
}
