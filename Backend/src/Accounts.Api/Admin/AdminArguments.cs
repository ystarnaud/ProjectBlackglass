using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Persistence.Accounts;

namespace Blackglass.Accounts.Api.Admin;

public sealed class AdminUsageException(string message) : Exception(message);

/// <summary>
/// The admin CLI's strict grammar: "&lt;noun&gt; &lt;verb&gt;", then known flags, each once, each taking a value that
/// isn't itself a flag, with exactly the flags the verb requires plus its optional ones. Accounts are named by their
/// canonical Base58 id (the form the CLI prints), credentials by UUID.
/// </summary>
public sealed record AdminArguments(string Verb, string? Handle, string? DisplayName, long? Account, Guid? Credential, OperatorContext Operator)
{
    public const string Usage =
        "usage: Accounts.Api admin account create [--handle <h>] [--display-name <name>] | credential rotate --account <id> | " +
        "credential revoke --credential <uuid> | sessions revoke --account <id> | account suspend --account <id> | " +
        "account reactivate --account <id>; each with --operator <name> --reason <text>; <id> is the Base58 account id";

    static readonly Dictionary<string, (string[] Required, string[] Optional)> Verbs = new()
    {
        ["account-create"] = ([], ["--handle", "--display-name"]),
        ["credential-rotate"] = (["--account"], []),
        ["credential-revoke"] = (["--credential"], []),
        ["sessions-revoke"] = (["--account"], []),
        ["account-suspend"] = (["--account"], []),
        ["account-reactivate"] = (["--account"], []),
    };

    public static AdminArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !Verbs.TryGetValue(args[0] + "-" + args[1], out var shape)) throw new AdminUsageException(Usage);
        var verb = args[0] + "-" + args[1];
        var allowed = shape.Required.Concat(shape.Optional).Append("--operator").Append("--reason").ToHashSet(StringComparer.Ordinal);
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Count; i += 2)
        {
            if (!allowed.Contains(args[i]) || i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !flags.TryAdd(args[i], args[i + 1]))
                throw new AdminUsageException(Usage);
        }
        if (!shape.Required.Append("--operator").Append("--reason").All(flags.ContainsKey)) throw new AdminUsageException(Usage);

        OperatorContext op;
        try { op = OperatorContext.Create(flags["--operator"], flags["--reason"]); }
        catch (ArgumentException e) { throw new AdminUsageException(e.Message); }

        long? account = null;
        if (flags.TryGetValue("--account", out var accountText))
            account = AccountId.TryParse(accountText, out var id) ? id : throw new AdminUsageException("--account must be a canonical Base58 account id.");
        Guid? credential = null;
        if (flags.TryGetValue("--credential", out var credentialText))
            credential = Guid.TryParseExact(credentialText, "D", out var g) ? g : throw new AdminUsageException("--credential must be a UUID.");
        return new AdminArguments(verb, flags.GetValueOrDefault("--handle"), flags.GetValueOrDefault("--display-name"), account, credential, op);
    }
}
