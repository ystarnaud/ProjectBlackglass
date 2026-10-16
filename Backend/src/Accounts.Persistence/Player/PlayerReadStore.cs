using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Advancement;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Persistence.Player;

/// <param name="Choices">The advancement choice ids picked, in order. An id the catalogue no longer lists still appears: only definitions carry a Known flag.</param>
public sealed record OperativeView(long OperativeId, string DefinitionId, bool DefinitionKnown, int Experience, int Rank, int PendingPicks, IReadOnlyList<string> Choices);
public sealed record RosterView(long Version, IReadOnlyList<OperativeView> Operatives);

public sealed record InstanceView(long ItemId, string DefinitionId, bool DefinitionKnown);
public sealed record StackView(string DefinitionId, bool DefinitionKnown, int Quantity);
public sealed record LocationView(IReadOnlyList<InstanceView> Instances, IReadOnlyList<StackView> Stacks);
public sealed record EquipmentView(long? Weapon, long? Armor, long? Utility);

/// <summary>An operative's carried items (equipped ones included) and which of them sit in each slot.</summary>
public sealed record BagView(long OperativeId, LocationView Bag, EquipmentView Equipment);

/// <param name="Locked">Whether the account is in a mission (the <see cref="IInventoryLock"/> seam; never true until Stage 7 connects participations).</param>
public sealed record InventoryView(long Version, bool Locked, LocationView Stash, IReadOnlyList<BagView> Operatives);

/// <summary>
/// The player's roster and inventory, scoped to the account (owner ruling D4: a GET never mutates persistent state). Every
/// read is one <c>repeatable read, read only</c> transaction, so the version and every row come from one snapshot and no
/// statement can write. An account with no inventories row, or one whose starter is not granted, is
/// <see cref="PlayerError.NotProvisioned"/>: provisioning happens at account creation or by the operator's backfill, never
/// here. A stored definition id the catalogue does not know is returned with its Known flag false, never an error: a
/// later catalogue can retire an id the database still holds.
/// </summary>
public sealed class PlayerReadStore(NpgsqlDataSource db, GameCatalogue? catalogue = null, IInventoryLock? inventoryLock = null)
{
    readonly GameCatalogue known = catalogue ?? GameCatalogue.Embedded;
    readonly IInventoryLock mission = inventoryLock ?? new NoParticipationsYet();

    /// <summary>The inventory version, or null when the account has no inventories row with the starter granted.</summary>
    public async Task<long?> GetInventoryVersionAsync(long accountId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await BeginSnapshotAsync(conn, ct);
        return await ReadVersionAsync(conn, tx, accountId, ct);
    }

    public async Task<RosterView> GetRosterAsync(long accountId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await BeginSnapshotAsync(conn, ct);
        var version = await ReadVersionAsync(conn, tx, accountId, ct) ?? throw NotProvisioned();
        return new RosterView(version, await ReadOperativesAsync(known, conn, tx, accountId, null, ct));
    }

    public async Task<InventoryView> GetInventoryAsync(long accountId, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await BeginSnapshotAsync(conn, ct);
        var version = await ReadVersionAsync(conn, tx, accountId, ct) ?? throw NotProvisioned();
        var locked = await mission.IsLockedAsync(conn, tx, accountId, ct);
        return await InventoryViewReader.ReadAsync(known, conn, tx, accountId, version, locked, ct);
    }

    static PlayerException NotProvisioned() =>
        new(PlayerError.NotProvisioned, "The account has no starter roster or inventory yet.");

    /// <summary>The snapshot (the first statement of the transaction takes it) and the read-only mode, set before any query.</summary>
    static async Task<NpgsqlTransaction> BeginSnapshotAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await conn.ExecuteAsync(new CommandDefinition("set transaction read only", transaction: tx, cancellationToken: ct));
        return tx;
    }

    static Task<long?> ReadVersionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select version from inventories where account_id = @accountId and starter_granted", new { accountId }, tx, cancellationToken: ct));

    /// <summary>The account's operatives (or only <paramref name="operativeId"/>) as views; shared with <see cref="PickStore"/>.</summary>
    internal static async Task<List<OperativeView>> ReadOperativesAsync(GameCatalogue known, NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, long? operativeId, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<OperativeRow>(new CommandDefinition(
            """
            select operative_id as OperativeId, definition_id as DefinitionId, experience as Experience from operatives
            where account_id = @accountId and (@operativeId::bigint is null or operative_id = @operativeId) order by operative_id
            """, new { accountId, operativeId }, tx, cancellationToken: ct))).ToList();
        var choices = (await conn.QueryAsync<ChoiceRow>(new CommandDefinition(
            """
            select c.operative_id as OperativeId, c.choice_id as ChoiceId
            from operative_choices c join operatives o on o.operative_id = c.operative_id
            where o.account_id = @accountId and (@operativeId::bigint is null or o.operative_id = @operativeId) order by c.operative_id, c.ordinal
            """, new { accountId, operativeId }, tx, cancellationToken: ct))).ToLookup(c => c.OperativeId, c => c.ChoiceId);
        var track = known.Document.Progression;
        return rows.Select(o =>
        {
            var picked = choices[o.OperativeId].ToList();
            return new OperativeView(o.OperativeId, o.DefinitionId, known.FindOperative(o.DefinitionId) is not null, o.Experience,
                ProgressionRules.RankFor(track, o.Experience), ProgressionRules.PendingPicks(track, o.Experience, picked.Count), picked);
        }).ToList();
    }

    sealed record OperativeRow(long OperativeId, string DefinitionId, int Experience);
    sealed record ChoiceRow(long OperativeId, string ChoiceId);
}
