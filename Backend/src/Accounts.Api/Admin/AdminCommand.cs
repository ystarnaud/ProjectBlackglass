using System.Globalization;
using Npgsql;
using Blackglass.Accounts.Api.Energy;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Energy;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.Api.Admin;

/// <summary>
/// The operator CLI on bg-dev-services01 (spec §4.2), run with `docker compose run --rm admin …` as accounts_app. A new
/// key is printed once, on stdout, to the operator who asked for it; nothing else ever shows it. Every command prints
/// the Base58 account id it acted on (when it acted on one) and the correlation id of its audit row (energy reconcile,
/// which changes no account, writes no audit row: its correlation id is only printed).
/// A change to an existing account first prints its target ("target.*" lines: the canonical Base58 id, status, handle,
/// display name, creation time, live credential and sessions, and whether its starter is granted; for the energy verbs also
/// the stored energy balance) and then acts only when confirmed: --yes, or, at a terminal, typing that Base58 id. A policy
/// publication first prints the current policy and the proposed one ("target.policy*" lines) and is confirmed by --yes or by
/// typing "publish". Without confirmation nothing changes (exit 64 with no terminal, 67 on a wrong answer).
/// </summary>
public static class AdminCommand
{
    /// <param name="input">The operator's terminal, or null when stdin isn't one (then only --yes confirms).</param>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, TextWriter output, TextWriter error,
        CancellationToken ct, TextReader? input = null, Func<GameCatalogue>? loadCatalogue = null)
    {
        AdminArguments a;
        try { a = AdminArguments.Parse(args); }
        catch (AdminUsageException e) { error.WriteLine(e.Message); return ExitCodes.Usage; }
        // Only the verbs that grant a starter load the catalogue, and before the database, as for serve: an invalid or
        // missing one refuses them (exit 4) with nothing written. Account-security and energy verbs never load it, so an
        // operator can still revoke, suspend, reactivate and rotate when a bad build ships.
        var load = loadCatalogue ?? (() => GameCatalogue.Embedded);
        GameCatalogue? catalogue = null;
        if (a.NeedsCatalogue)
        {
            try { catalogue = load(); }
            catch (Exception e) when (e is CatalogueException or InvalidOperationException) // InvalidOperation: no embedded resource
            {
                error.WriteLine($"Refusing to run: {e.Message}");
                return ExitCodes.InvalidCatalogue;
            }
        }
        DbSettings settings;
        DbTimeouts timeouts;
        try { (settings, timeouts) = (DbSettings.FromEnvironment(env), DbTimeouts.FromEnvironment(env)); }
        catch (DbSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return ExitCodes.Failure; }
        // Only the energy verbs read the energy settings, so a bad one never blocks account-security administration.
        EnergySettings? energySettings = null;
        if (a.IsEnergyVerb)
        {
            try { energySettings = EnergySettings.FromEnvironment(env); }
            catch (EnergySettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return ExitCodes.Failure; }
            // The values are checked before any connection: an out-of-bounds grant or policy never reaches the database.
            try
            {
                if (a.Amount is { } amount) EnergyAdminStore.ValidateGrantAmount(amount, energySettings.GrantMax);
                if (a.Policy is { } p) EnergyAdminStore.ValidatePolicy(p.Cap, p.OpeningBalance, TimeSpan.FromSeconds(p.IntervalSeconds), p.Amount, p.VoidRefundPct);
            }
            catch (EnergyException e) { error.WriteLine(e.Message); return ExitCodes.Usage; }
        }
        await using var dataSource = settings.CreateDataSource("blackglass-admin", maxPoolSize: 2, timeouts: timeouts);
        // Without a loaded catalogue the store gets one that refuses: no security verb may come to depend on it unnoticed.
        var store = catalogue is not null
            ? new AccountAdminStore(dataSource, catalogue)
            : new AccountAdminStore(dataSource, () => throw new InvalidOperationException($"admin {a.Verb} does not load the catalogue."));
        var energy = energySettings is not null ? new EnergyAdminStore(dataSource, energySettings.GrantMax) : null;
        try
        {
            // The same schema policy as serve (Stage 1 ruling 4): behind, drifted or ahead, the CLI writes nothing.
            var status = await SchemaGuard.CheckAsync(dataSource, MigrationCatalog.Embedded, ct);
            if (status.State != SchemaState.Current)
            {
                error.WriteLine($"Refusing to run: schema {status.State}: {status.Detail}");
                return ExitCodes.SchemaNotCurrent;
            }
            var refused = a.Confirmation switch
            {
                AdminConfirmation.Account => await ConfirmAsync(a, store, energy, output, error, input, ct),
                AdminConfirmation.Publish => await ConfirmPublishAsync(a, energy!, output, error, input, ct),
                _ => null,
            };
            if (refused is { } code) return code;
            switch (a.Verb)
            {
                case "account-create" or "credential-rotate":
                    var issued = a.Verb == "account-create"
                        ? await store.CreateAccountAsync(a.Handle, a.DisplayName, a.Operator, ct)
                        : await store.RotateCredentialAsync(a.Account!.Value, a.Operator, ct);
                    output.WriteLine($"accountId={AccountId.Format(issued.AccountId)}");
                    output.WriteLine($"credentialId={issued.CredentialId}");
                    output.WriteLine($"accountKey={issued.AccountKey.Text}");
                    if (issued.Starter is { } starter)
                    {
                        output.WriteLine($"starterGranted={(starter.Granted ? "granted" : "already")}");
                        output.WriteLine($"starterOperatives={starter.Operatives}");
                        output.WriteLine($"starterInstances={starter.Instances}");
                        output.WriteLine($"starterStacks={starter.Stacks}");
                    }
                    if (issued.Energy is { } opened)
                    {
                        output.WriteLine($"energyOpened={(opened.Opened ? "opened" : "already")}");
                        output.WriteLine($"energyBalance={opened.Balance}");
                    }
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
                case "starter-backfill":
                    var backfill = await store.BackfillStarterAsync(a.Account!.Value, a.Operator, ct);
                    output.WriteLine($"accountId={AccountId.Format(a.Account.Value)}");
                    output.WriteLine($"starterGranted={(backfill.Granted ? "granted" : "already")}");
                    // The grant's version and counts when this call granted; only the recorded version when it was already granted:
                    // the account's current holdings are never printed under the grant's labels.
                    output.WriteLine($"catalogueVersion={backfill.CatalogueVersion}");
                    if (backfill.Grant is { } grant)
                    {
                        output.WriteLine($"operatives={grant.Operatives}");
                        output.WriteLine($"instances={grant.Instances}");
                        output.WriteLine($"stacks={grant.Stacks}");
                    }
                    break;
                case "energy-open":
                    var wallet = await energy!.OpenAsync(a.Account!.Value, a.Operator, ct);
                    output.WriteLine($"accountId={AccountId.Format(a.Account.Value)}");
                    output.WriteLine($"energyOpened={(wallet.Opened ? "opened" : "already")}");
                    // For an existing wallet these are read without its row lock: what it held then, not a materialized balance
                    // (so advisory=true follows them).
                    output.WriteLine($"balance={wallet.Balance}");
                    output.WriteLine($"policyVersion={wallet.PolicyVersion}");
                    if (!wallet.Opened) output.WriteLine("advisory=true");
                    break;
                case "energy-grant":
                    var granted = await energy!.GrantAsync(a.Account!.Value, a.Amount!.Value, a.IdempotencyKey!.Value, a.Operator, ct);
                    output.WriteLine($"accountId={AccountId.Format(a.Account.Value)}");
                    output.WriteLine($"amount={granted.Amount}");
                    output.WriteLine($"balanceAfter={granted.BalanceAfter}");
                    output.WriteLine($"regenerated={granted.Regenerated}");
                    output.WriteLine($"replayed={(granted.Replayed ? "true" : "false")}");
                    break;
                case "energy-policy-publish":
                    var p = a.Policy!;
                    var policy = await energy!.PublishPolicyAsync(p.Cap, p.OpeningBalance, TimeSpan.FromSeconds(p.IntervalSeconds), p.Amount, p.VoidRefundPct, a.Operator, ct);
                    output.WriteLine($"policyVersion={policy.Version}");
                    output.WriteLine($"effectiveAt={Timestamp(policy.EffectiveAt)}");
                    output.WriteLine($"cap={policy.Cap}");
                    output.WriteLine($"openingBalance={policy.OpeningBalance}");
                    output.WriteLine($"intervalSeconds={Seconds(policy.RegenInterval)}");
                    output.WriteLine($"amount={policy.RegenAmount}");
                    output.WriteLine($"voidRefundPct={policy.VoidRefundPct}");
                    break;
                case "energy-reconcile":
                    // On demand, the same sweep as serve's (E4). It changes no account, so nothing is audited and the correlation
                    // id below is the operator's alone. Mismatches still exit 0: the recorded anomaly is the signal.
                    var report = await new EnergyReconciler(dataSource).RunAsync(ct);
                    output.WriteLine($"walletsChecked={report.WalletsChecked}");
                    output.WriteLine($"mismatches={report.Mismatches.Count}");
                    output.WriteLine($"anomaliesRecorded={report.AnomaliesRecorded}");
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
        catch (EnergyException e)
        {
            error.WriteLine(e.Message);
            return e.Error switch { EnergyError.NoWallet => ExitCodes.NotFound, EnergyError.IdempotencyKeyReused => ExitCodes.Conflict, _ => ExitCodes.Usage };
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
                    WriteGrantOutcomeUnknown(a, error);
                    return ExitCodes.Unavailable;
                default:
                    error.WriteLine($"Unexpected database error: {DbFailure.SafeDescription(e)}");
                    WriteGrantOutcomeUnknown(a, error);
                    return ExitCodes.Failure;
            }
        }
    }

    /// <summary>Prints the target, then returns null when the change is confirmed, else the exit code to stop with.
    /// An unknown account or credential throws NotFound before anything is printed. <paramref name="energy"/> is set for the
    /// energy verbs, which also print the stored energy balance (read without regeneration or a lock), or "(no wallet)".</summary>
    static async Task<int?> ConfirmAsync(AdminArguments a, AccountAdminStore store, EnergyAdminStore? energy, TextWriter output, TextWriter error, TextReader? input,
        CancellationToken ct)
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
        output.WriteLine($"target.starterGranted={(target.StarterGranted ? "true" : "false")}");
        if (energy is not null)
            output.WriteLine($"target.energyBalance={(await energy.DescribeStoredBalanceAsync(target.AccountId, ct))?.ToString(CultureInfo.InvariantCulture) ?? "(no wallet)"}");

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

    /// <summary>A publication changes every wallet's future: prints the latest policy and the proposed values, then returns
    /// null when confirmed (--yes, or typing "publish"), else the exit code to stop with. Its transaction begins only after.</summary>
    static async Task<int?> ConfirmPublishAsync(AdminArguments a, EnergyAdminStore energy, TextWriter output, TextWriter error, TextReader? input, CancellationToken ct)
    {
        var current = await energy.DescribeLatestPolicyAsync(ct);
        var proposed = a.Policy!;
        output.WriteLine($"target.policyCurrentVersion={current.Version}");
        output.WriteLine($"target.policyCurrentEffectiveAt={Timestamp(current.EffectiveAt)}");
        output.WriteLine($"target.policyCurrentCap={current.Cap}");
        output.WriteLine($"target.policyCurrentOpeningBalance={current.OpeningBalance}");
        output.WriteLine($"target.policyCurrentIntervalSeconds={Seconds(current.RegenInterval)}");
        output.WriteLine($"target.policyCurrentAmount={current.RegenAmount}");
        output.WriteLine($"target.policyCurrentVoidRefundPct={current.VoidRefundPct}");
        output.WriteLine($"target.policyProposedCap={proposed.Cap}");
        output.WriteLine($"target.policyProposedOpeningBalance={proposed.OpeningBalance}");
        output.WriteLine($"target.policyProposedIntervalSeconds={proposed.IntervalSeconds}");
        output.WriteLine($"target.policyProposedAmount={proposed.Amount}");
        output.WriteLine($"target.policyProposedVoidRefundPct={proposed.VoidRefundPct}");

        if (a.Confirmed) return null;
        if (input is null)
        {
            error.WriteLine($"Not confirmed, so nothing was changed. To publish this energy policy for every wallet, run this at a terminal and type publish, or add {AdminArguments.YesSwitch}.");
            return ExitCodes.Usage;
        }
        error.Write("Type publish to confirm the new energy policy (it applies to every wallet from now on): ");
        var typed = await input.ReadLineAsync(ct);
        if (typed == "publish") return null;
        error.WriteLine("Not confirmed, so nothing was changed.");
        return ExitCodes.NotConfirmed;
    }

    /// <summary>A grant whose connection broke (or failed oddly) may have committed before the failure was seen: a retry with a
    /// new key could grant twice, while the same key replays or grants once.</summary>
    static void WriteGrantOutcomeUnknown(AdminArguments a, TextWriter error)
    {
        if (a.Verb == "energy-grant")
            error.WriteLine($"The grant may or may not have committed: retry with the same --idempotency-key {a.IdempotencyKey!.Value:D}, never a new one.");
    }

    /// <summary>A database time, UTC, to the microsecond.</summary>
    static string Timestamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>An interval in seconds: whole for any version this verb published; a version inserted otherwise may carry a fraction.</summary>
    static string Seconds(TimeSpan interval) => interval.TotalSeconds.ToString(CultureInfo.InvariantCulture);
}
