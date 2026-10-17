using System.Globalization;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Persistence.Accounts;

namespace Blackglass.Accounts.Api.Admin;

public sealed class AdminUsageException(string message) : Exception(message);

/// <summary>What a verb must be confirmed by: nothing; the Base58 id of the existing account it changes; for a policy or
/// template publication (an energy policy changes every wallet's future; an insurance policy version every later activation
/// of it; a mission template version every later admission to it), the word "publish"; or, for a development host fixture's
/// registration or an agent host's, the word "register"; for a change to an existing host, its host id; for a build verb, its
/// revision. --yes confirms any of them.
/// Stage 9's recovery verbs are confirmed by typing the attempt id (Attempt), the word "import" (catalogue import) or the anomaly
/// id (Anomaly).</summary>
public enum AdminConfirmation { None, Account, Publish, Register, Host, Build, Attempt, Import, Anomaly }

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
    string? PolicyId = null, string? TermsFile = null, int? DurationSeconds = null, MissionTemplateValues? Template = null, string? HostId = null, int? Capacity = null,
    int? PortBase = null, string? Revision = null, string? CatalogueVersion = null, string? Templates = null, Guid? Attempt = null, long? Submission = null,
    string? File = null, string? Evidence = null, long? Anomaly = null, string? Note = null, IReadOnlySet<string>? Switches = null)
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
        "host register-dev --host-id <id> --capacity <1-1000> | host heartbeat-dev --host-id <id> | " +
        "host register --host-id <id> --capacity <1-1000> --port-base <port> | host rotate-secret --host-id <id> | " +
        "host drain|enable|disable --host-id <id> | host capacity --host-id <id> --capacity <1-1000> | " +
        "build allow --revision <r> --catalogue-version sha256:<hex> --templates <id>:<version>[,...] | build disable --revision <r> --catalogue-version sha256:<hex> | " +
        "attempt show --attempt <uuid> [--submission <id> --raw] | attempt revalidate --attempt <uuid> --account-base58 <id> --submission <id> [--host-trust-reviewed] | " +
        "attempt import-result --attempt <uuid> --account-base58 <id> --file <path> --evidence <text> [--host-trust-reviewed] | " +
        "attempt void-participant --attempt <uuid> --account-base58 <id> --evidence <text> --outbox-checked [--revalidated] [--host-trust-reviewed --unrecoverable] | " +
        "host declare-lost --host-id <id> --evidence <text> --outbox-unrecoverable | catalogue import --file <path> [--catalogue-version sha256:<hex>] | " +
        "anomaly list [--open] | anomaly review --anomaly <id> --note <text>; " +
        "each with --operator <name> --reason <text>. " +
        "An account id is always Base58 (as printed, e.g. BukQL or 2), never decimal. Every command that changes an existing " +
        "account shows its target and acts only when confirmed: type the Base58 account id at the terminal, or add --yes. " +
        "energy policy publish and insurance policy publish show the current and proposed policy and are confirmed by typing publish, or by --yes. " +
        "insurance activate prints startsAt and expiresAt: the period that activation bought (from the later of the old expiry and now). " +
        "mission template publish shows the current and proposed template and is confirmed by typing publish, or by --yes; an energy cost of 0 is allowed. " +
        "host register-dev and host heartbeat-dev are for development host fixtures in tests and the throwaway smoke stack only: they refuse " +
        "unless this process has BLACKGLASS_DEV_HOSTS=enabled (never in production). host register-dev is confirmed by typing register, or by --yes. " +
        "host register registers an agent host and prints its secret once (hostSecret=hs1_...): install it for the agent, it is never shown again; " +
        "it is confirmed by typing register. host rotate-secret prints a new secret once and refuses the old one from then on. " +
        "The other host verbs are confirmed by typing the host id, the build verbs by typing the revision; --yes confirms any of them.";

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
        ["host-register"] = (["--host-id", "--capacity", "--port-base"], [], false, AdminConfirmation.Register),
        ["host-rotate-secret"] = (["--host-id"], [], false, AdminConfirmation.Host),
        ["host-drain"] = (["--host-id"], [], false, AdminConfirmation.Host),
        ["host-enable"] = (["--host-id"], [], false, AdminConfirmation.Host),
        ["host-disable"] = (["--host-id"], [], false, AdminConfirmation.Host),
        ["host-capacity"] = (["--host-id", "--capacity"], [], false, AdminConfirmation.Host),
        ["build-allow"] = (["--revision", "--catalogue-version", "--templates"], [], false, AdminConfirmation.Build),
        ["build-disable"] = (["--revision", "--catalogue-version"], [], false, AdminConfirmation.Build),
        // Stage 9 recovery (S9-B9; revision 3): confirmed, audited, CLI-only.
        ["attempt-show"] = (["--attempt"], ["--submission"], false, AdminConfirmation.None),
        ["attempt-revalidate"] = (["--attempt", AccountFlag, "--submission"], [], false, AdminConfirmation.Attempt),
        ["attempt-import-result"] = (["--attempt", AccountFlag, "--file", "--evidence"], [], false, AdminConfirmation.Attempt),
        ["attempt-void-participant"] = (["--attempt", AccountFlag, "--evidence"], [], false, AdminConfirmation.Attempt),
        ["host-declare-lost"] = (["--host-id", "--evidence"], [], false, AdminConfirmation.Host),
        ["catalogue-import"] = (["--file"], ["--catalogue-version"], false, AdminConfirmation.Import),
        ["anomaly-list"] = ([], [], false, AdminConfirmation.None),
        ["anomaly-review"] = (["--anomaly", "--note"], [], false, AdminConfirmation.Anomaly),
    };

    /// <summary>The attestation switches each Stage 9 verb accepts (each a flag without a value; --yes stays the confirmation).</summary>
    static readonly Dictionary<string, string[]> VerbSwitches = new()
    {
        ["attempt-show"] = ["--raw"],
        ["attempt-revalidate"] = ["--host-trust-reviewed"],
        ["attempt-import-result"] = ["--host-trust-reviewed"],
        ["attempt-void-participant"] = ["--outbox-checked", "--revalidated", "--host-trust-reviewed", "--unrecoverable"],
        ["host-declare-lost"] = ["--outbox-unrecoverable"],
        ["anomaly-list"] = ["--open"],
    };

    /// <summary>Whether this run gave <paramref name="name"/> (a Stage 9 attestation switch).</summary>
    public bool Has(string name) => Switches?.Contains(name) == true;

    /// <summary>The Stage 9 recovery verbs (RecoveryCommand runs them).</summary>
    public bool IsRecoveryVerb => Verb.StartsWith("attempt-", StringComparison.Ordinal) || Verb.StartsWith("anomaly-", StringComparison.Ordinal)
        || Verb is "host-declare-lost" or "catalogue-import";

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
    public bool IsDevHostVerb => IsDevHostFixtureVerb(Verb);

    /// <summary>
    /// Exactly the two development host fixture verbs. Not every <c>host-*</c> verb: Stage 8's real host switches (0007
    /// reserves <c>host_drain</c> and <c>host_enable</c>) must work in production, where fixtures are never enabled.
    /// </summary>
    public static bool IsDevHostFixtureVerb(string verb) => verb is "host-register-dev" or "host-heartbeat-dev";

    /// <summary>The agent host verbs (Stage 8, owner ruling B3): registration, secret rotation, the switches and capacity.</summary>
    public bool IsHostVerb => Verb is "host-register" or "host-rotate-secret" or "host-drain" or "host-enable" or "host-disable" or "host-capacity" or "host-declare-lost";

    /// <summary>The build allowlist verbs (owner ruling B4).</summary>
    public bool IsBuildVerb => Verb.StartsWith("build-", StringComparison.Ordinal);

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
        var switchesAllowed = VerbSwitches.GetValueOrDefault(verb, []);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        for (var i = words; i < args.Count; i += 2)
        {
            if (args[i] == YesSwitch && shape.Confirm != AdminConfirmation.None && !confirmed)
            {
                confirmed = true;
                i--; // a switch takes no value
                continue;
            }
            if (Array.IndexOf(switchesAllowed, args[i]) >= 0)
            {
                if (!switches.Add(args[i])) throw new AdminUsageException(Usage);
                i--;
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
        int? capacity = verb is "host-register-dev" or "host-register" or "host-capacity" ? Integer(flags, "--capacity") : null;
        int? portBase = verb == "host-register" ? Integer(flags, "--port-base") : null;
        Guid? attempt = null;
        if (flags.TryGetValue("--attempt", out var attemptText))
            attempt = Blackglass.Accounts.Domain.Attempts.CanonicalUuid.TryParse(attemptText, out var at) ? at : throw new AdminUsageException("--attempt must be a canonical lower-case UUID.");
        long? submission = flags.TryGetValue("--submission", out var submissionText) ? Long(submissionText, "--submission") : null;
        long? anomaly = flags.TryGetValue("--anomaly", out var anomalyText) ? Long(anomalyText, "--anomaly") : null;
        if (switches.Contains("--raw") && submission is null) throw new AdminUsageException("--raw needs --submission <id>.");
        return new AdminArguments(verb, flags.GetValueOrDefault("--handle"), flags.GetValueOrDefault("--display-name"), account, credential, op, confirmed,
            accountText is not null && accountText.All(char.IsAsciiDigit), amount, idempotencyKey, policy,
            flags.GetValueOrDefault("--policy-id"), flags.GetValueOrDefault("--terms-file"), durationSeconds, template, flags.GetValueOrDefault("--host-id"), capacity,
            portBase, flags.GetValueOrDefault("--revision"), flags.GetValueOrDefault("--catalogue-version"), flags.GetValueOrDefault("--templates"),
            attempt, submission, flags.GetValueOrDefault("--file"), flags.GetValueOrDefault("--evidence"), anomaly, flags.GetValueOrDefault("--note"), switches);
    }

    static long Long(string text, string flag) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new AdminUsageException($"{flag} must be a positive whole number.");

    static int Integer(Dictionary<string, string> flags, string flag) =>
        int.TryParse(flags[flag], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new AdminUsageException($"{flag} must be a whole number.");
}
