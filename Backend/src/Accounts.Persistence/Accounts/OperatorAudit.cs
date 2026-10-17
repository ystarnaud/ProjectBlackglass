using System.Text.Json;
using Dapper;
using Npgsql;

namespace Blackglass.Accounts.Persistence.Accounts;

/// <summary>
/// The operator_actions row every operator change appends inside its own transaction, so a failed audit rolls the change
/// back. Shared by the account lifecycle and the energy verbs. The account is null for an action on no account (a policy
/// publication).
/// </summary>
internal static class OperatorAudit
{
    internal static Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, OperatorContext op, string action, long? accountId, Guid? credentialId, object result, CancellationToken ct) =>
        InsertAsync(conn, tx, op, action, accountId, credentialId, null, result, ct);

    /// <summary>A host or build verb's row: <paramref name="hostId"/> fills operator_actions.host_id (null for a build verb).</summary>
    internal static Task AuditHostAsync(NpgsqlConnection conn, NpgsqlTransaction tx, OperatorContext op, string action, string? hostId, object result, CancellationToken ct) =>
        InsertAsync(conn, tx, op, action, null, null, hostId, result, ct);

    static async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, OperatorContext op, string action, long? accountId, Guid? credentialId, string? hostId,
        object result, CancellationToken ct)
    {
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into operator_actions (correlation_id, operator, action, account_id, credential_id, host_id, reason, checks, result)
                values (@correlationId, @operator, @action, @accountId, @credentialId, @hostId, @reason, '{}'::jsonb, @result::jsonb)
                """,
                new { correlationId = op.CorrelationId, @operator = op.Operator, action, accountId, credentialId, hostId, reason = op.Reason, result = JsonSerializer.Serialize(result) },
                tx, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.CheckViolation && e.ConstraintName is "operator_actions_operator_check" or "operator_actions_reason_check")
        {
            // OperatorContext.Create and the column checks should agree; where they don't, it's bad input and the change rolls back.
            throw new AccountAdminException(AccountAdminError.InvalidOperator, "The database refused that operator name or reason.");
        }
    }
}
