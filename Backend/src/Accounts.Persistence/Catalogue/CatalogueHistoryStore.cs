using System.Collections.Concurrent;
using System.Data;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Persistence.Accounts;

namespace Blackglass.Accounts.Persistence.Catalogue;

/// <summary>A catalogue version looked up in the history: found (verified and parsed), or genuinely unavailable.</summary>
public abstract record CatalogueLookup
{
    public sealed record Found(GameCatalogue Catalogue) : CatalogueLookup;

    /// <param name="Reason">"missing" (no history row), "hash" (the bytes don't hash to the version) or "invalid" (they no longer
    /// load as a catalogue under this backend's rules).</param>
    public sealed record Unavailable(string Reason) : CatalogueLookup;
}

/// <summary>A catalogue that would change the identity kind of an item id some registered version already uses (S9-B20.2).
/// The message names the id; never the bytes.</summary>
public sealed class CatalogueRegistrationException(string message) : Exception(message);

/// <summary>
/// The immutable catalogue history (S9-B20, owner corrections of revision 3): every catalogue an attempt is admitted under,
/// verbatim, keyed by its catalogueVersion. Settlement and ready look an attempt's admission catalogue up here and never judge
/// it under another. Registration (serve at startup, admission's first use, the audited `catalogue import`) inserts a
/// validated catalogue once, under a transaction-scoped advisory lock that serializes registrations, and refuses one that would
/// change the identity kind (stackable or instance) of an item id any registered version uses. Retiring a definition (leaving
/// it out) is allowed: older versions stay in the history. Parsed catalogues are cached by version (rows never change).
/// </summary>
public sealed class CatalogueHistoryStore(NpgsqlDataSource db)
{
    const long RegistrationLock = 4786318449935675245; // pg_advisory_xact_lock key for registrations (0006 uses ...244 for policies)

    readonly ConcurrentDictionary<string, GameCatalogue> parsed = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers <paramref name="catalogue"/> when absent and verifies the stored row; returns true when this call inserted it.
    /// With <paramref name="op"/>, the insert is audited (<c>catalogue_import</c>) in the same transaction. Throws
    /// <see cref="CatalogueRegistrationException"/> for an identity-kind change or, for a new version only, a failed registration rule
    /// (<see cref="CatalogueRegistrationRules"/>). No retry.
    /// </summary>
    public async Task<bool> RegisterAsync(GameCatalogue catalogue, OperatorContext? op, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        var bytes = CanonicalCatalogueJson.ToBytes(catalogue.Document);
        if (CatalogueVersion.Of(bytes) != catalogue.Version) throw new InvalidOperationException("A loaded catalogue's bytes do not hash to its version.");
        await using var conn = await DbFailure.OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await conn.ExecuteAsync(new CommandDefinition("select pg_advisory_xact_lock(@RegistrationLock)", new { RegistrationLock }, tx, cancellationToken: ct));
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from catalogue_versions where version = @Version)", new { catalogue.Version }, tx, cancellationToken: ct));
        if (!exists)
        {
            // Stage 11, owner ruling D7: rules for new registrations only. A stored version is never re-judged by them, so it stays
            // readable under its original rules (LookupLockedAsync loads it with GameCatalogue.Load alone).
            var problems = CatalogueRegistrationRules.Check(catalogue.Document);
            if (problems.Count > 0) throw new CatalogueRegistrationException(string.Join("; ", problems));
            var versions = (await conn.QueryAsync<string>(new CommandDefinition("select version from catalogue_versions order by version", transaction: tx, cancellationToken: ct))).ToList();
            foreach (var version in versions)
            {
                if (await LookupLockedAsync(conn, tx, version, ct) is not CatalogueLookup.Found { Catalogue: var earlier }) continue; // unreadable rows can't be compared
                foreach (var item in catalogue.Document.Items)
                    if (earlier.FindItem(item.Id) is { } before && before.Stackable != item.Stackable)
                        throw new CatalogueRegistrationException(
                            $"item {item.Id} is {(before.Stackable ? "stackable" : "an instance")} in registered catalogue {version}; an id never changes identity kind");
            }
            await conn.ExecuteAsync(new CommandDefinition("insert into catalogue_versions (version, bytes) values (@Version, @bytes)",
                new { catalogue.Version, bytes }, tx, cancellationToken: ct));
            if (op is not null)
                await OperatorAudit.AuditHostAsync(conn, tx, op, "catalogue_import", null, new { result = "registered", version = catalogue.Version }, ct);
        }
        else if (op is not null)
            await OperatorAudit.AuditHostAsync(conn, tx, op, "catalogue_import", null, new { result = "already_registered", version = catalogue.Version }, ct);
        var stored = await conn.ExecuteScalarAsync<byte[]>(new CommandDefinition(
            "select bytes from catalogue_versions where version = @Version", new { catalogue.Version }, tx, cancellationToken: ct));
        if (stored is null || !stored.AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("The stored catalogue differs from the registered bytes.");
        await tx.CommitAsync(ct);
        parsed.TryAdd(catalogue.Version, catalogue);
        return !exists;
    }

    /// <summary>The catalogue <paramref name="version"/>, verified (hash) and parsed, from its own short read.</summary>
    public async Task<CatalogueLookup> LookupAsync(string version, CancellationToken ct)
    {
        if (parsed.TryGetValue(version, out var cached)) return new CatalogueLookup.Found(cached);
        await using var conn = await DbFailure.OpenAsync(db, ct);
        return await LookupLockedAsync(conn, null, version, ct);
    }

    /// <summary>The same lookup inside the caller's transaction (the history is insert-only: no lock is taken or needed).</summary>
    public async Task<CatalogueLookup> LookupLockedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        if (parsed.TryGetValue(version, out var cached)) return new CatalogueLookup.Found(cached);
        var bytes = await conn.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
            "select bytes from catalogue_versions where version = @version", new { version }, tx, cancellationToken: ct));
        if (bytes is null) return new CatalogueLookup.Unavailable("missing");
        if (CatalogueVersion.Of(bytes) != version) return new CatalogueLookup.Unavailable("hash");
        GameCatalogue catalogue;
        try { catalogue = GameCatalogue.Load(bytes); }
        catch (CatalogueException) { return new CatalogueLookup.Unavailable("invalid"); }
        parsed.TryAdd(version, catalogue);
        return new CatalogueLookup.Found(catalogue);
    }
}
