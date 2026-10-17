using System.Globalization;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Persistence.Accounts;

namespace Blackglass.Accounts.Api.Admin;

public sealed class AdminUsageException(string message) : Exception(message);

/// <summary>What a verb must be confirmed by: nothing; the Base58 id of the existing account it changes; for a policy or
/// template publication (an energy policy changes every wallet's future; an insurance policy version every later activation
/// of it; a mission template version every later admission to it), the word "publish"; or, for a development host fixture's
/// registration, the word "register". --yes confirms any of them.</summary>
public enum AdminConfirmation { None, Account, Publish, Register }

/// <summary>The proposed values of <c>energy policy publish</c>, as typed (checked against 0006's CHECKs before any connection).</summary>
public sealed record EnergyPolicyValues(int Cap, int OpeningBalance, int IntervalSeconds, int Amount, int VoidRefundPct);

/// <summary>The proposed values of <c>mission template publish</c>, as typed (MissionTemplate checks them before any connection).</summary>
public sealed record MissionTemplateValues(string TemplateId, int EnergyCost, int MinOperatives, int MaxOperatives);

/// <summary>
/// The admin CLI's strict grammar: "&lt;noun&gt; &lt;verb&gt;" (or "energy policy publish"), then known flags, each once, each taking a value that
/// isn't itself a flag, with exactly the flags the verb requires plus its optional ones; --yes is the only switch.
/// Accounts are named by --account-base58: the canonical Base58 id the CLI prints, even when it is all digits (an id
/// is never read as decimal). Credentials and grant and activation idempotency keys are named by UUID. A change to an existing
/// account, and a policy publication, must be confirmed (<see cref="Confirmed"/> or at a terminal; see AdminCommand). Numbers
/// are parsed as written (a sign allowed); their bounds, an insurance policy id's form and a terms file's content are the
/// stores' to check, before any connection.
/// </summary>
public sealed record AdminArguments(string Verb, string? Handle, string? DisplayName, long? Account, Guid? Credential, OperatorContext Operator, bool Confirmed,
    bool AccountWasAllDigits = false, int? Amount = null, Guid? IdempotencyKey = null, EnergyPolicyValues? Policy = null,
    string? PolicyId = null, string? TermsFile = null, int? DurationSeconds = null, MissionTemplateValues? Template = null, string? HostId = null, int? Capacity = null)
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
        "energy reconcile | " +
        "insurance policy publish --policy-id <id> --terms-file <path> | " +
        "insurance activate --account-base58 <Base58 account id> --policy-id <id> --duration-seconds <1-315360000> --idempotency-key <uuid> | " +
        "mission template publish --template-id <id> --energy-cost <n> --min-operatives <n> --max-operatives <n> | " +
        "host register-dev --host-id <id> --capacity <1-1000> | host heartbeat-dev --host-id <id>; " +
        "each with --operator <name> --reason <text>. " +
        "An account id is always Base58 (as printed, e.g. BukQL or 2), never decimal. Every command that changes an existing " +
        "account shows its target and acts only when confirmed: type the Base58 account id at the terminal, or add --yes. " +
        "energy policy publish and insurance policy publish show the current and proposed policy and are confirmed by typing publish, or by --yes. " +
        "insurance activate prints startsAt and expiresAt: the period that activation bought (from the later of the old expiry and now). " +
        "mission template publish shows the current and proposed template and is confirmed by typing publish, or by --yes; an energy cost of 0 is allowed. " +
        "host register-dev and host heartbeat-dev are for development host fixtures in tests and the throwaway smoke stack only: they refuse " +
        "unless this process has BLACKGLASS_DEV_HOSTS=enabled (never in production). host register-dev is confirmed by typing register, or by --yes.";

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
        ["insurance-policy-publish"] = (["--policy-id", "--terms-file"], [], false, AdminConfirmation.Publish),
        ["insurance-activate"] = ([AccountFlag, "--policy-id", "--duration-seconds", "--idempotency-key"], [], false, AdminConfirmation.Account),
        ["mission-template-publish"] = (["--template-id", "--energy-cost", "--min-operatives", "--max-operatives"], [], false, AdminConfirmation.Publish),
        ["host-register-dev"] = (["--host-id", "--capacity"], [], false, AdminConfirmation.Register),
        ["host-heartbeat-dev"] = (["--host-id"], [], false, AdminConfirmation.None),
    };

    /// <summary>Every verb ("noun-verb") the CLI knows.</summary>
    public static IReadOnlyCollection<string> KnownVerbs => Verbs.Keys;

    /// <summary>What confirms this verb.</summary>
    public AdminConfirmation Confirmation => Verbs[Verb].Confirm;

    /// <summary>The verbs that change an existing account, which an operator confirms by its Base58 id.</summary>
    public bool ChangesAnExistingAccount => Confirmation == AdminConfirmation.Account;

    /// <summary>The energy verbs, which read <c>EnergySettings</c>.</summary>
    public bool IsEnergyVerb => Verb.StartsWith("energy-", StringComparison.Ordinal);

    /// <summary>The insurance verbs, which touch no energy table, lock or setting.</summary>
    public bool IsInsuranceVerb => Verb.StartsWith("insurance-", StringComparison.Ordinal);

    /// <summary>The mission template verbs, which touch no energy or inventory table, lock or setting.</summary>
    public bool IsMissionTemplateVerb => Verb.StartsWith("mission-template-", StringComparison.Ordinal);

    /// <summary>The development host fixture verbs, which read <c>AttemptSettings</c> and refuse unless BLACKGLASS_DEV_HOSTS=enabled.</summary>
    public bool IsHostVerb => Verb.StartsWith("host-", StringComparison.Ordinal);

    /// <summary>Only the verbs that grant a starter read game definitions. Account-security administration (rotate, revoke a
    /// credential, revoke sessions, suspend, reactivate) never loads the catalogue, so it works whatever state that is in.</summary>
    public bool NeedsCatalogue => Verbs[Verb].NeedsCatalogue;

    public static AdminArguments Parse(IReadOnlyList<string> args)
    {
        // "energy policy publish", "insurance policy publish" and "mission template publish" are the three-word verbs; every
        // other verb is "<noun> <verb>".
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
        int? durationSeconds = verb == "insurance-activate" ? Integer(flags, "--duration-seconds") : null;
        MissionTemplateValues? template = verb == "mission-template-publish"
            ? new MissionTemplateValues(flags["--template-id"], Integer(flags, "--energy-cost"), Integer(flags, "--min-operatives"), Integer(flags, "--max-operatives"))
            : null;
        int? capacity = verb == "host-register-dev" ? Integer(flags, "--capacity") : null;
        return new AdminArguments(verb, flags.GetValueOrDefault("--handle"), flags.GetValueOrDefault("--display-name"), account, credential, op, confirmed,
            accountText is not null && accountText.All(char.IsAsciiDigit), amount, idempotencyKey, policy,
            flags.GetValueOrDefault("--policy-id"), flags.GetValueOrDefault("--terms-file"), durationSeconds, template, flags.GetValueOrDefault("--host-id"), capacity);
    }

    static int Integer(Dictionary<string, string> flags, string flag) =>
        int.TryParse(flags[flag], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new AdminUsageException($"{flag} must be a whole number.");
}
