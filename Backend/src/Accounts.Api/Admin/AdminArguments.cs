using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Persistence.Accounts;

namespace Blackglass.Accounts.Api.Admin;

public sealed class AdminUsageException(string message) : Exception(message);

/// <summary>
/// The admin CLI's strict grammar: "&lt;noun&gt; &lt;verb&gt;", then known flags, each once, each taking a value that
/// isn't itself a flag, with exactly the flags the verb requires plus its optional ones; --yes is the only switch.
/// Accounts are named by --account-base58: the canonical Base58 id the CLI prints, even when it is all digits (an id
/// is never read as decimal). Credentials are named by UUID. A change to an existing account must be confirmed
/// (<see cref="Confirmed"/> or at a terminal; see AdminCommand).
/// </summary>
public sealed record AdminArguments(string Verb, string? Handle, string? DisplayName, long? Account, Guid? Credential, OperatorContext Operator, bool Confirmed)
{
    public const string AccountFlag = "--account-base58";
    public const string YesSwitch = "--yes";

    public const string Usage =
        "usage: Accounts.Api admin account create [--handle <h>] [--display-name <name>] | " +
        "credential rotate --account-base58 <Base58 account id> | credential revoke --credential <uuid> | " +
        "sessions revoke --account-base58 <Base58 account id> | account suspend --account-base58 <Base58 account id> | " +
        "account reactivate --account-base58 <Base58 account id>; each with --operator <name> --reason <text>. " +
        "An account id is always Base58 (as printed, e.g. BukQL or 2), never decimal. Every command but create shows its " +
        "target and acts only when confirmed: type the Base58 account id at the terminal, or add --yes.";

    static readonly Dictionary<string, (string[] Required, string[] Optional)> Verbs = new()
    {
        ["account-create"] = ([], ["--handle", "--display-name"]),
        ["credential-rotate"] = ([AccountFlag], []),
        ["credential-revoke"] = (["--credential"], []),
        ["sessions-revoke"] = ([AccountFlag], []),
        ["account-suspend"] = ([AccountFlag], []),
        ["account-reactivate"] = ([AccountFlag], []),
    };

    /// <summary>Every verb but account create changes an existing account and needs confirmation.</summary>
    public bool ChangesAnExistingAccount => Verb != "account-create";

    public static AdminArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !Verbs.TryGetValue(args[0] + "-" + args[1], out var shape)) throw new AdminUsageException(Usage);
        var verb = args[0] + "-" + args[1];
        if (args.Contains("--account", StringComparer.Ordinal))
            throw new AdminUsageException($"--account is now {AccountFlag}: an account id is always its Base58 form, never decimal. {Usage}");
        var allowed = shape.Required.Concat(shape.Optional).Append("--operator").Append("--reason").ToHashSet(StringComparer.Ordinal);
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var confirmed = false;
        for (var i = 2; i < args.Count; i += 2)
        {
            if (args[i] == YesSwitch && verb != "account-create" && !confirmed)
            {
                confirmed = true;
                i--; // a switch takes no value
                continue;
            }
            if (!allowed.Contains(args[i]) || i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !flags.TryAdd(args[i], args[i + 1]))
                throw new AdminUsageException(Usage);
        }
        if (!shape.Required.Append("--operator").Append("--reason").All(flags.ContainsKey)) throw new AdminUsageException(Usage);

        OperatorContext op;
        try { op = OperatorContext.Create(flags["--operator"], flags["--reason"]); }
        catch (ArgumentException e) { throw new AdminUsageException(e.Message); }

        long? account = null;
        if (flags.TryGetValue(AccountFlag, out var accountText))
            account = AccountId.TryParse(accountText, out var id) ? id : throw new AdminUsageException($"{AccountFlag} must be a canonical Base58 account id.");
        Guid? credential = null;
        if (flags.TryGetValue("--credential", out var credentialText))
            credential = Guid.TryParseExact(credentialText, "D", out var g) ? g : throw new AdminUsageException("--credential must be a UUID.");
        return new AdminArguments(verb, flags.GetValueOrDefault("--handle"), flags.GetValueOrDefault("--display-name"), account, credential, op, confirmed);
    }
}
