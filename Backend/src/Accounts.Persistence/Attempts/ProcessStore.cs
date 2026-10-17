using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Attempts;

public sealed record AuthenticatedProcess(Guid AttemptId);

/// <summary>
/// The mission process side of an attempt (spec §16.3; plan revision 2 R1, R4). A process authenticates with its attempt's
/// <c>pc1_</c> credential: one indexed lookup of the SHA-256 per request (no cache), valid only while the attempt is neither
/// cancelled nor settled. The route's attempt must be the credential's (the authentication handler compares them). There is
/// no exception after cancellation: stop instructions reach the process through its host agent only.
/// </summary>
public sealed class ProcessStore(NpgsqlDataSource db)
{
    public async Task<AuthenticatedProcess?> AuthenticateAsync(byte[] credentialHash, CancellationToken ct)
    {
        await using var conn = await DbFailure.OpenAsync(db, ct);
        var attempt = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select attempt_id from mission_attempts where process_credential_hash = @credentialHash and state not in ('cancelled', 'settled')",
            new { credentialHash }, cancellationToken: ct));
        return attempt is { } id ? new AuthenticatedProcess(id) : null;
    }
}
