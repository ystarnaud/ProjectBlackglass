using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;

namespace Blackglass.Accounts.Persistence.Player;

public sealed record ProvisionResult(long InventoryVersion, int Operatives, int Instances, int Stacks, string CatalogueVersion);

/// <summary>
/// Creates an account's starter operatives, items and stacks from the catalogue (owner ruling D4), inside the caller's
/// transaction: it never opens, commits or rolls one back, so a failure anywhere leaves nothing behind once the caller
/// rolls back. The caller holds the account row (just inserted, or locked FOR UPDATE by the backfill).
/// Every permanent operative and item is minted by one INSERT … ON CONFLICT (origin, origin_ref) DO NOTHING RETURNING id;
/// when nothing is returned the existing row is read and must describe the same thing (D3): a retry after a commit finds
/// the committed row, a retry after a rollback mints fresh, and concurrent mints of one reference converge on one id
/// (the second INSERT waits for the first transaction, then either conflicts or, if it rolled back, inserts). That relies
/// on READ COMMITTED, PostgreSQL's default, where each statement sees rows committed before it began: never raise the
/// isolation level here, or a conflict with a row committed after the snapshot would raise 40001 instead of being found.
/// Provisioning mints only after taking the inventories row FOR UPDATE, so two provisioners of one account serialize.
/// </summary>
public static class StarterProvisioner
{
    public static async Task<ProvisionResult> ProvisionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, GameCatalogue catalogue, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "insert into inventories (account_id) values (@accountId) on conflict do nothing", new { accountId }, tx, cancellationToken: ct));
        var state = await conn.QuerySingleAsync<InventoryState>(new CommandDefinition(
            "select starter_granted as StarterGranted, version as Version, starter_catalogue_version as CatalogueVersion from inventories where account_id = @accountId for update",
            new { accountId }, tx, cancellationToken: ct));
        if (state.StarterGranted)
        {
            // Already granted: the current state, as granted (the caller decides whether that is success).
            var counts = await conn.QuerySingleAsync<Counts>(new CommandDefinition(
                """
                select (select count(*)::int from operatives where account_id = @accountId) as Operatives,
                       (select count(*)::int from item_instances where account_id = @accountId) as Instances,
                       (select count(*)::int from item_stacks where account_id = @accountId) as Stacks
                """, new { accountId }, tx, cancellationToken: ct));
            return new ProvisionResult(state.Version, counts.Operatives, counts.Instances, counts.Stacks, state.CatalogueVersion!);
        }

        var plan = StarterPlanner.For(accountId, catalogue.Document);
        var operativeIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var planned in plan.Operatives)
            operativeIds[planned.DefinitionId] = await MintOperativeAsync(conn, tx, accountId, planned.DefinitionId, "starter", planned.OriginRef, ct);
        foreach (var planned in plan.Instances)
        {
            long? operativeId = planned.OperativeDefinitionId is { } definition ? operativeIds[definition] : null;
            await MintItemAsync(conn, tx, accountId, planned.DefinitionId, operativeId, planned.EquippedSlot, "starter", planned.OriginRef, ct);
        }
        foreach (var planned in plan.Stacks)
        {
            long? operativeId = planned.OperativeDefinitionId is { } definition ? operativeIds[definition] : null;
            await conn.ExecuteAsync(new CommandDefinition(
                "insert into item_stacks (account_id, operative_id, definition_id, quantity) values (@accountId, @operativeId, @definitionId, @quantity)",
                new { accountId, operativeId, definitionId = planned.DefinitionId, quantity = planned.Quantity }, tx, cancellationToken: ct));
        }
        await conn.ExecuteAsync(new CommandDefinition(
            """
            update inventories set starter_granted = true, starter_catalogue_version = @version, starter_granted_at = statement_timestamp()
            where account_id = @accountId
            """, new { accountId, version = catalogue.Version }, tx, cancellationToken: ct));
        return new ProvisionResult(state.Version, plan.Operatives.Count, plan.Instances.Count, plan.Stacks.Count, catalogue.Version);
    }

    /// <summary>The id of the operative with this origin reference, minting it if there is none. A found row of another
    /// account or definition is a collision, never a find.</summary>
    public static async Task<long> MintOperativeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, string definitionId, string origin, string originRef, CancellationToken ct)
    {
        var minted = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            insert into operatives (account_id, definition_id, origin, origin_ref) values (@accountId, @definitionId, @origin, @originRef)
            on conflict (origin, origin_ref) do nothing returning operative_id
            """, new { accountId, definitionId, origin, originRef }, tx, cancellationToken: ct));
        if (minted is { } id) return id;
        var found = await conn.QuerySingleOrDefaultAsync<FoundOperative>(new CommandDefinition(
            "select operative_id as OperativeId, account_id as AccountId, definition_id as DefinitionId from operatives where origin = @origin and origin_ref = @originRef",
            new { origin, originRef }, tx, cancellationToken: ct));
        if (found is null || found.AccountId != accountId || found.DefinitionId != definitionId)
            throw new InvalidOperationException("origin reference collision");
        return found.OperativeId;
    }

    /// <summary>The id of the item with this origin reference, minting it if there is none. A found row that differs in
    /// account, definition, operative or equipped slot is a collision, never a find.</summary>
    public static async Task<long> MintItemAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, string definitionId, long? operativeId, string? equippedSlot,
        string origin, string originRef, CancellationToken ct)
    {
        var minted = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            insert into item_instances (account_id, definition_id, operative_id, equipped_slot, origin, origin_ref)
            values (@accountId, @definitionId, @operativeId, @equippedSlot, @origin, @originRef)
            on conflict (origin, origin_ref) do nothing returning item_id
            """, new { accountId, definitionId, operativeId, equippedSlot, origin, originRef }, tx, cancellationToken: ct));
        if (minted is { } id) return id;
        var found = await conn.QuerySingleOrDefaultAsync<FoundItem>(new CommandDefinition(
            """
            select item_id as ItemId, account_id as AccountId, definition_id as DefinitionId, operative_id as OperativeId, equipped_slot as EquippedSlot
            from item_instances where origin = @origin and origin_ref = @originRef
            """, new { origin, originRef }, tx, cancellationToken: ct));
        if (found is null || found.AccountId != accountId || found.DefinitionId != definitionId || found.OperativeId != operativeId || found.EquippedSlot != equippedSlot)
            throw new InvalidOperationException("origin reference collision");
        return found.ItemId;
    }

    sealed record Counts(int Operatives, int Instances, int Stacks);
    sealed record InventoryState(bool StarterGranted, long Version, string? CatalogueVersion);
    sealed record FoundOperative(long OperativeId, long AccountId, string DefinitionId);
    sealed record FoundItem(long ItemId, long AccountId, string DefinitionId, long? OperativeId, string? EquippedSlot);
}
