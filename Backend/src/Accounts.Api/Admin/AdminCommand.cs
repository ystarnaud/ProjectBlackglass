using System.Globalization;
using Npgsql;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.Api.Admin;

/// <summary>
/// The operator CLI on bg-dev-services01 (spec §4.2), run with `docker compose run --rm admin …` as accounts_app. A new
/// key is printed once, on stdout, to the operator who asked for it; nothing else ever shows it. Every command prints
/// the Base58 account id it acted on and the correlation id of its audit row.
/// A change to an existing account first prints its target ("target.*" lines: the canonical Base58 id, status, handle,
/// display name, creation time, live credential and sessions) and then acts only when confirmed: --yes, or, at a
/// terminal, typing that Base58 id. Without either it changes nothing (exit 64 with no terminal, 67 on a wrong answer).
/// </summary>
public static class AdminCommand
{
    /// <param name="input">The operator's terminal, or null when stdin isn't one (then only --yes confirms).</param>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, TextWriter output, TextWriter error,
        CancellationToken ct, TextReader? input = null)
    {
        AdminArguments a;
        try { a = AdminArguments.Parse(args); }
        catch (AdminUsageException e) { error.WriteLine(e.Message); return ExitCodes.Usage; }
        DbSettings settings;
        DbTimeouts timeouts;
        try { (settings, timeouts) = (DbSettings.FromEnvironment(env), DbTimeouts.FromEnvironment(env)); }
        catch (DbSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return ExitCodes.Failure; }
        await using var dataSource = settings.CreateDataSource("blackglass-admin", maxPoolSize: 2, timeouts: timeouts);
        var store = new AccountAdminStore(dataSource);
        try
        {
            // The same schema policy as serve (Stage 1 ruling 4): behind, drifted or ahead, the CLI writes nothing.
            var status = await SchemaGuard.CheckAsync(dataSource, MigrationCatalog.Embedded, ct);
            if (status.State != SchemaState.Current)
            {
                error.WriteLine($"Refusing to run: schema {status.State}: {status.Detail}");
                return ExitCodes.SchemaNotCurrent;
            }
            if (a.ChangesAnExistingAccount && await ConfirmAsync(a, store, output, error, input, ct) is { } refused)
                return refused;
            switch (a.Verb)
            {
                case "account-create" or "credential-rotate":
                    var issued = a.Verb == "account-create"
                        ? await store.CreateAccountAsync(a.Handle, a.DisplayName, a.Operator, ct)
                        : await store.RotateCredentialAsync(a.Account!.Value, a.Operator, ct);
                    output.WriteLine($"accountId={AccountId.Format(issued.AccountId)}");
                    output.WriteLine($"credentialId={issued.CredentialId}");
                    output.WriteLine($"accountKey={issued.AccountKey.Text}");
                    break;
                case "credential-revoke":
                    var revoked = await store.RevokeCredentialAsync(a.Credential!.Value, a.Operator, ct);
                    output.WriteLine($"credentialId={a.Credential}");
                    output.WriteLine($"revokedSessions={revoked}");
                    break;
                case "sessions-revoke":
                    output.WriteLine($"revokedSessions={await store.RevokeSessionsAsync(a.Account!.Value, a.Operator, ct)}");
                    output.WriteLine($"accountId={AccountId.Format(a.Account.Value)}");
                    break;
                case "account-suspend":
                    output.WriteLine($"revokedSessions={await store.SuspendAsync(a.Account!.Value, a.Operator, ct)}");
                    output.WriteLine($"accountId={AccountId.Format(a.Account.Value)}");
                    output.WriteLine("status=suspended");
                    break;
                case "account-reactivate":
                    await store.ReactivateAsync(a.Account!.Value, a.Operator, ct);
                    output.WriteLine($"accountId={AccountId.Format(a.Account.Value)}");
                    output.WriteLine("status=active");
                    break;
            }
            output.WriteLine($"correlationId={a.Operator.CorrelationId}");
            return ExitCodes.Ok;
        }
        catch (AccountAdminException e)
        {
            error.WriteLine(e.Message);
            return e.Error switch { AccountAdminError.NotFound => ExitCodes.NotFound, AccountAdminError.HandleTaken => ExitCodes.Conflict, _ => ExitCodes.Usage };
        }
        catch (NpgsqlException e)
        {
            // The SQLSTATE and constraint only: a server message can quote values.
            switch (DbFailure.Classify(e))
            {
                case DbFailureKind.Busy:
                    error.WriteLine($"The database is busy (a lock or statement timeout): nothing was changed; retry. {DbFailure.SafeDescription(e)}");
                    return ExitCodes.Busy;
                case DbFailureKind.Unavailable:
                    error.WriteLine($"The database is unavailable ({settings}): {DbFailure.SafeDescription(e)}");
                    return ExitCodes.Unavailable;
                default:
                    error.WriteLine($"Unexpected database error: {DbFailure.SafeDescription(e)}");
                    return ExitCodes.Failure;
            }
        }
    }

    /// <summary>Prints the target, then returns null when the change is confirmed, else the exit code to stop with.
    /// An unknown account or credential throws NotFound before anything is printed.</summary>
    static async Task<int?> ConfirmAsync(AdminArguments a, AccountAdminStore store, TextWriter output, TextWriter error, TextReader? input, CancellationToken ct)
    {
        CredentialSummary? credential = null;
        if (a.Credential is { } credentialId)
            credential = await store.DescribeCredentialAsync(credentialId, ct) ?? throw new AccountAdminException(AccountAdminError.NotFound, "No such credential.");
        var accountId = credential?.AccountId ?? a.Account!.Value;
        var target = await store.DescribeAccountAsync(accountId, ct) ?? throw new AccountAdminException(AccountAdminError.NotFound, "No such account.");
        var id = AccountId.Format(target.AccountId);

        if (credential is not null)
        {
            output.WriteLine($"target.credentialId={credential.CredentialId}");
            output.WriteLine($"target.credentialStatus={(credential.Live ? "live" : "revoked")}");
        }
        output.WriteLine($"target.accountId={id}");
        if (a.AccountWasAllDigits) // the decimal-id hazard: say how the argument was read, next to the account it named
            output.WriteLine($"target.note={id} was read as a Base58 account id (account ids are never decimal); check the handle and display name below");
        output.WriteLine($"target.status={target.Status}");
        output.WriteLine($"target.handle={target.Handle ?? "(none)"}");
        output.WriteLine($"target.displayName={target.DisplayName ?? "(none)"}"); // no control characters: one line (0001's check)
        output.WriteLine($"target.createdAt={target.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}");
        output.WriteLine($"target.liveCredentialId={target.LiveCredentialId?.ToString() ?? "(none)"}");
        output.WriteLine($"target.liveSessions={target.LiveSessions}");

        if (a.Confirmed) return null;
        var action = a.Verb.Replace('-', ' ');
        if (input is null)
        {
            error.WriteLine($"Not confirmed, so nothing was changed. To {action} account {id}, run this at a terminal and type its Base58 id, or add {AdminArguments.YesSwitch}.");
            return ExitCodes.Usage;
        }
        error.Write($"Type the Base58 account id {id} to confirm {action}: ");
        var typed = await input.ReadLineAsync(ct);
        if (typed == id) return null;
        error.WriteLine("Not confirmed, so nothing was changed.");
        return ExitCodes.NotConfirmed;
    }
}
