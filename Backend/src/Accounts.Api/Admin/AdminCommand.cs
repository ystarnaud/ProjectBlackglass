using Npgsql;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;

namespace Blackglass.Accounts.Api.Admin;

/// <summary>
/// The operator CLI on bg-dev-services01 (spec §4.2), run with `docker compose run --rm admin …` as accounts_app. A new
/// key is printed once, on stdout, to the operator who asked for it; nothing else ever shows it. Every command prints
/// the Base58 account id it acted on and the correlation id of its audit row.
/// </summary>
public static class AdminCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, TextWriter output, TextWriter error, CancellationToken ct)
    {
        AdminArguments a;
        try { a = AdminArguments.Parse(args); }
        catch (AdminUsageException e) { error.WriteLine(e.Message); return ExitCodes.Usage; }
        DbSettings settings;
        try { settings = DbSettings.FromEnvironment(env); }
        catch (DbSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return ExitCodes.Failure; }
        await using var dataSource = settings.CreateDataSource("blackglass-admin", maxPoolSize: 2);
        var store = new AccountAdminStore(dataSource);
        try
        {
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
                    output.WriteLine($"credentialId={a.Credential}");
                    output.WriteLine($"revokedSessions={await store.RevokeCredentialAsync(a.Credential!.Value, a.Operator, ct)}");
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
        catch (NpgsqlException e) { error.WriteLine($"Database error ({settings}): {ServeCommand.Describe(e)}"); return ExitCodes.Failure; }
    }
}
