using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Persistence.Player;

/// <summary>
/// Reads an account's inventory rows and builds the <see cref="InventoryView"/> that GET /v1/inventory returns. Shared by
/// <see cref="PlayerReadStore"/> (inside its read-only snapshot) and <see cref="InventoryEditStore"/> (in its own transaction,
/// after the version bump), so an edit answers with exactly what a read would show. Every query is scoped to the account.
/// </summary>
internal static class InventoryViewReader
{
    internal sealed record InstanceRow(long ItemId, string DefinitionId, long? OperativeId, string? EquippedSlot);
    internal sealed record StackRow(long? OperativeId, string DefinitionId, int Quantity);

    internal static async Task<InventoryView> ReadAsync(GameCatalogue known, NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, long version, bool locked, CancellationToken ct)
    {
        var operatives = await ReadOperativeIdsAsync(conn, tx, accountId, ct);
        var instances = await ReadInstancesAsync(conn, tx, accountId, ct);
        var stacks = await ReadStacksAsync(conn, tx, accountId, ct);

        LocationView Location(long? operativeId) => new(
            instances.Where(i => i.OperativeId == operativeId).Select(i => new InstanceView(i.ItemId, i.DefinitionId, known.FindItem(i.DefinitionId) is not null)).ToList(),
            stacks.Where(s => s.OperativeId == operativeId).Select(s => new StackView(s.DefinitionId, known.FindItem(s.DefinitionId) is not null, s.Quantity)).ToList());

        long? InSlot(long operativeId, string slot) =>
            instances.FirstOrDefault(i => i.OperativeId == operativeId && i.EquippedSlot == slot)?.ItemId;

        var bags = operatives
            .Select(o => new BagView(o, Location(o),
                new EquipmentView(InSlot(o, "weapon"), InSlot(o, "armor"), InSlot(o, "utility"))))
            .ToList();
        return new InventoryView(version, locked, Location(null), bags);
    }

    internal static async Task<List<long>> ReadOperativeIdsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct) =>
        (await conn.QueryAsync<long>(new CommandDefinition(
            "select operative_id from operatives where account_id = @accountId order by operative_id",
            new { accountId }, tx, cancellationToken: ct))).ToList();

    internal static async Task<List<InstanceRow>> ReadInstancesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct) =>
        (await conn.QueryAsync<InstanceRow>(new CommandDefinition(
            """
            select item_id as ItemId, definition_id as DefinitionId, operative_id as OperativeId, equipped_slot as EquippedSlot
            from item_instances where account_id = @accountId order by item_id
            """, new { accountId }, tx, cancellationToken: ct))).ToList();

    internal static async Task<List<StackRow>> ReadStacksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct) =>
        (await conn.QueryAsync<StackRow>(new CommandDefinition(
            """
            select operative_id as OperativeId, definition_id as DefinitionId, quantity as Quantity
            from item_stacks where account_id = @accountId order by definition_id collate "C"
            """, new { accountId }, tx, cancellationToken: ct))).ToList();
}
