using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Advancement;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.Persistence.Player;

/// <summary>
/// An advancement pick, with exactly the rule of Unity's PersistentOperativeState.TryPickChoice
/// (<see cref="ProgressionRules.CheckPick"/>): a choice not on the catalogue's track is refused first, then a pick with
/// no rank gained to spend. One transaction at READ COMMITTED under the account's inventories row lock (the app role has
/// no lock on operatives), so two picks never read the same pick count and the version is checked after the lock: the
/// loser of a race with the same expected version is <see cref="PlayerError.VersionMismatch"/>. The choice is validated in
/// code before the insert, so an unknown or malformed id is never a check violation. A refusal writes nothing.
/// </summary>
public sealed class PickStore(NpgsqlDataSource db, GameCatalogue? catalogue = null)
{
    readonly GameCatalogue known = catalogue ?? GameCatalogue.Embedded;

    // InventoryLocked (an operative deployed in a live attempt cannot advance) is deferred until participations exist (spec §5.3, Stage 6+).
    public async Task<(long Version, OperativeView Operative)> PickAsync(long accountId, long operativeId, string choiceId, long expectedVersion, CancellationToken ct = default)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var inventory = await conn.QuerySingleOrDefaultAsync<(long Version, bool StarterGranted)?>(new CommandDefinition(
            "select version, starter_granted from inventories where account_id = @accountId for update", new { accountId }, tx, cancellationToken: ct));
        if (inventory is not { StarterGranted: true })
            throw new PlayerException(PlayerError.NotProvisioned, "The account has no starter roster or inventory yet.");
        if (inventory.Value.Version != expectedVersion)
            throw new PlayerException(PlayerError.VersionMismatch, "The inventory changed since it was read.");

        var operative = await conn.QuerySingleOrDefaultAsync<(int Experience, long Picks)?>(new CommandDefinition(
            """
            select o.experience, (select count(*) from operative_choices c where c.operative_id = o.operative_id)
            from operatives o where o.operative_id = @operativeId and o.account_id = @accountId
            """, new { operativeId, accountId }, tx, cancellationToken: ct)); // another account's operative is just as absent
        if (operative is null)
            throw new PlayerException(PlayerError.OperativeNotFound, "The account has no such operative.");

        switch (ProgressionRules.CheckPick(known.Document.Progression, operative.Value.Experience, (int)operative.Value.Picks, choiceId))
        {
            case PickRefusal.UnknownChoice:
                throw new PlayerException(PlayerError.UnknownChoice, "The advancement choice is not on the track.");
            case PickRefusal.NoPendingPick:
                throw new PlayerException(PlayerError.NoPendingPick, "The operative has no rank gained to spend on a pick.");
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "insert into operative_choices (operative_id, ordinal, choice_id) values (@operativeId, @ordinal, @choiceId)",
            new { operativeId, ordinal = (short)operative.Value.Picks, choiceId }, tx, cancellationToken: ct));
        var version = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "update inventories set version = version + 1 where account_id = @accountId returning version", new { accountId }, tx, cancellationToken: ct));
        var view = (await PlayerReadStore.ReadOperativesAsync(known, conn, tx, accountId, operativeId, ct)).Single();
        await tx.CommitAsync(ct);
        return (version, view);
    }
}
