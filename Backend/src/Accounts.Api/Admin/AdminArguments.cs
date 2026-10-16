using System.Globalization;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Persistence.Accounts;

namespace Blackglass.Accounts.Api.Admin;

public sealed class AdminUsageException(string message) : Exception(message);

/// <summary>What a verb must be confirmed by: nothing; the Base58 id of the existing account it changes; or, for a policy
/// publication (which changes every wallet's future), the word "publish". --yes confirms either.</summary>
public enum AdminConfirmation { None, Account, Publish }

/// <summary>The proposed values of <c>energy policy publish</c>, as typed (checked against 0006's CHECKs before any connection).</summary>
public sealed record EnergyPolicyValues(int Cap, int OpeningBalance, int IntervalSeconds, int Amount, int VoidRefundPct);

/// <summary>
/// The admin CLI's strict grammar: "&lt;noun&gt; &lt;verb&gt;" (or "energy policy publish"), then known flags, each once, each taking a value that
/// isn't itself a flag, with exactly the flags the verb requires plus its optional ones; --yes is the only switch.
/// Accounts are named by --account-base58: the canonical Base58 id the CLI prints, even when it is all digits (an id
/// is never read as decimal). Credentials and grant idempotency keys are named by UUID. A change to an existing account, and
/// a policy publication, must be confirmed (<see cref="Confirmed"/> or at a terminal; see AdminCommand). Numbers are
/// parsed as written (a sign allowed); their bounds are the stores' to check, before any connection.
/// </summary>
public sealed record AdminArguments(string Verb, string? Handle, string? DisplayName, long? Account, Guid? Credential, OperatorContext Operator, bool Confirmed,
    bool AccountWasAllDigits = false, int? Amount = null, Guid? IdempotencyKey = null, EnergyPolicyValues? Policy = null)
{
    public const string AccountFlag = "--account-base58";
    public const string YesSwitch = "--yes";

    public const string Usage =
        "usage: Accounts.Api admin account create [--handle <h>] [--display-name <name>] | " +
        "credential rotate --account-base58 <Base58 account id> | credential revoke --credential <uuid> | " +
        "sessions revoke --account-base58 <Base58 account id> | account suspend --account-base58 <Base58 account id> | " +
        "account reactivate --account-base58 <Base58 account id> | starter backfill --account-base58 <Base58 account id> | " +
        "energy open --account-base58 <Base58 account id> | " +
        "energy grant --account-base58 <Base58 account id> --amount <n> --idempotency-key <uuid> | " +
        "energy policy publish --cap <n> --opening-balance <n> --interval-seconds <n> --amount <n> --void-refund-pct <0-100> | " +
        "energy reconcile; each with --operator <name> --reason <text>. " +
        "An account id is always Base58 (as printed, e.g. BukQL or 2), never decimal. Every command that changes an existing " +
        "account shows its target and acts only when confirmed: type the Base58 account id at the terminal, or add --yes. " +
        "energy policy publish shows the current and proposed policy and is confirmed by typing publish, or by --yes.";

    // NeedsCatalogue: whether the verb reads game definitions (only the starter-granting ones). Confirm: what confirms it.
    // Every verb declares both here, so a new verb can't be added without deciding whether an invalid catalogue may block it
    // and what an operator must confirm.
    static readonly Dictionary<string, (string[] Required, string[] Optional, bool NeedsCatalogue, AdminConfirmation Confirm)> Verbs = new()
    {
        ["account-create"] = ([], ["--handle", "--display-name"], true, AdminConfirmation.None),
        ["credential-rotate"] = ([AccountFlag], [], false, AdminConfirmation.Account),
        ["credential-revoke"] = (["--credential"], [], false, AdminConfirmation.Account),
        ["sessions-revoke"] = ([AccountFlag], [], false, AdminConfirmation.Account),
        ["account-suspend"] = ([AccountFlag], [], false, AdminConfirmation.Account),
        ["account-reactivate"] = ([AccountFlag], [], false, AdminConfirmation.Account),
        ["starter-backfill"] = ([AccountFlag], [], true, AdminConfirmation.Account),
        ["energy-open"] = ([AccountFlag], [], false, AdminConfirmation.Account),
        ["energy-grant"] = ([AccountFlag, "--amount", "--idempotency-key"], [], false, AdminConfirmation.Account),
        ["energy-policy-publish"] = (["--cap", "--opening-balance", "--interval-seconds", "--amount", "--void-refund-pct"], [], false, AdminConfirmation.Publish),
        ["energy-reconcile"] = ([], [], false, AdminConfirmation.None),
    };

    /// <summary>Every verb ("noun-verb") the CLI knows.</summary>
    public static IReadOnlyCollection<string> KnownVerbs => Verbs.Keys;

    /// <summary>What confirms this verb.</summary>
    public AdminConfirmation Confirmation => Verbs[Verb].Confirm;

    /// <summary>The verbs that change an existing account, which an operator confirms by its Base58 id.</summary>
    public bool ChangesAnExistingAccount => Confirmation == AdminConfirmation.Account;

    /// <summary>The energy verbs, which read <c>EnergySettings</c>.</summary>
    public bool IsEnergyVerb => Verb.StartsWith("energy-", StringComparison.Ordinal);

    /// <summary>Only the verbs that grant a starter read game definitions. Account-security administration (rotate, revoke a
    /// credential, revoke sessions, suspend, reactivate) never loads the catalogue, so it works whatever state that is in.</summary>
    public bool NeedsCatalogue => Verbs[Verb].NeedsCatalogue;

    public static AdminArguments Parse(IReadOnlyList<string> args)
    {
        // "energy policy publish" is the one three-word verb; every other verb is "<noun> <verb>".
        var words = args.Count >= 3 && Verbs.ContainsKey(string.Join('-', args.Take(3))) ? 3 : 2;
        if (args.Count < words || !Verbs.TryGetValue(string.Join('-', args.Take(words)), out var shape)) throw new AdminUsageException(Usage);
        var verb = string.Join('-', args.Take(words));
        if (args.Contains("--account", StringComparer.Ordinal))
            throw new AdminUsageException($"--account is now {AccountFlag}: an account id is always its Base58 form, never decimal. {Usage}");
        var allowed = shape.Required.Concat(shape.Optional).Append("--operator").Append("--reason").ToHashSet(StringComparer.Ordinal);
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var confirmed = false;
        for (var i = words; i < args.Count; i += 2)
        {
            if (args[i] == YesSwitch && shape.Confirm != AdminConfirmation.None && !confirmed)
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
        Guid? idempotencyKey = null;
        if (flags.TryGetValue("--idempotency-key", out var keyText))
            idempotencyKey = Guid.TryParseExact(keyText, "D", out var k) ? k : throw new AdminUsageException("--idempotency-key must be a UUID (8-4-4-4-12 hexadecimal digits).");
        int? amount = verb == "energy-grant" ? Integer(flags, "--amount") : null;
        EnergyPolicyValues? policy = verb == "energy-policy-publish"
            ? new EnergyPolicyValues(Integer(flags, "--cap"), Integer(flags, "--opening-balance"), Integer(flags, "--interval-seconds"), Integer(flags, "--amount"),
                Integer(flags, "--void-refund-pct"))
            : null;
        return new AdminArguments(verb, flags.GetValueOrDefault("--handle"), flags.GetValueOrDefault("--display-name"), account, credential, op, confirmed,
            accountText is not null && accountText.All(char.IsAsciiDigit), amount, idempotencyKey, policy);
    }

    static int Integer(Dictionary<string, string> flags, string flag) =>
        int.TryParse(flags[flag], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new AdminUsageException($"{flag} must be a whole number.");
}
