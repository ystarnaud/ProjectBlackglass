using System.Globalization;
using System.Text;
using Npgsql;
using Blackglass.Accounts.Api.Attempts;
using Blackglass.Accounts.Api.Energy;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Insurance;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Energy;
using Blackglass.Accounts.Persistence.Insurance;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.Api.Admin;

/// <summary>
/// The operator CLI on bg-dev-services01 (spec §4.2), run with `docker compose run --rm admin …` as accounts_app. A new
/// key is printed once, on stdout, to the operator who asked for it; nothing else ever shows it. Every command prints
/// the Base58 account id it acted on (when it acted on one) and the correlation id of its audit row (energy reconcile,
/// which changes no account, and a replayed energy grant or insurance activation, which writes nothing, write no audit row:
/// their correlation id is only printed; energy reconcile prints one
/// "mismatch=&lt;Base58 account id&gt;:&lt;rule&gt;" line per finding, after its counts).
/// A change to an existing account first prints its target ("target.*" lines: the canonical Base58 id, status, handle,
/// display name, creation time, live credential and sessions, and whether its starter is granted; for the energy verbs also
/// the stored energy balance; for an insurance activation the stored coverage) and then acts only when confirmed: --yes, or,
/// at a terminal, typing that Base58 id. A policy publication (energy or insurance) first prints the current policy and the
/// proposed one ("target.policy*" lines) and is confirmed by --yes or by typing "publish"; a mission template publication
/// likewise ("target.template*" lines). A development host fixture's registration shows its target ("target.host*", kind,
/// capacity) and is confirmed by --yes or by typing "register"; the two fixture verbs (not every host verb) refuse (64)
/// unless this process has BLACKGLASS_DEV_HOSTS=enabled, before any connection. Without confirmation nothing changes (exit
/// 64 with no terminal, 67 on a wrong answer).
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
            // The values are checked before any connection: an out-of-bounds policy or a grant below 1 never reaches the
            // database. A grant's configurable maximum is not: it applies to a new key only, inside the grant's transaction
            // after its key lookup, so rerunning a grant after BLACKGLASS_ENERGY_GRANT_MAX was lowered still replays it.
            try
            {
                if (a.Amount is { } amount) EnergyAdminStore.ValidateGrantAmount(amount);
                if (a.Policy is { } p) EnergyAdminStore.ValidatePolicy(p.Cap, p.OpeningBalance, TimeSpan.FromSeconds(p.IntervalSeconds), p.Amount, p.VoidRefundPct);
            }
            catch (EnergyException e) { error.WriteLine(e.Message); return ExitCodes.Usage; }
        }
        // The insurance inputs are checked before any connection too: the policy id's form, the duration's bounds, and the terms
        // file, read and parsed here (an unreadable file or invalid terms never reaches the database).
        InsuranceTerms? terms = null;
        if (a.IsInsuranceVerb)
        {
            try
            {
                InsuranceAdminStore.ValidatePolicyId(a.PolicyId);
                if (a.DurationSeconds is { } seconds) InsuranceAdminStore.ValidateDuration(seconds);
                if (a.TermsFile is { } path) terms = InsuranceAdminStore.ParseTerms(ReadTermsFile(path));
            }
            catch (InsuranceException e) { error.WriteLine(e.Message); return ExitCodes.Usage; }
        }
        // The development host fixture verbs read the attempt settings and refuse, before any connection, unless this admin
        // process has development host fixtures enabled (S7-D2: test and smoke use only, never in production).
        if (a.IsDevHostVerb)
        {
            AttemptSettings attempts;
            try { attempts = AttemptSettings.FromEnvironment(env); }
            catch (AttemptSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return ExitCodes.Failure; }
            if (!attempts.DevHostsEnabled)
            {
                error.WriteLine($"Refusing admin {a.Verb.Replace('-', ' ')}: development host fixtures need {AttemptSettings.DevHostsVar}=enabled in this admin process " +
                    "(tests and the throwaway smoke stack only, never in production). Nothing was changed.");
                return ExitCodes.Usage;
            }
            try
            {
                DevHosts.ValidateHostId(a.HostId);
                if (a.Capacity is { } capacity) DevHosts.ValidateCapacity(capacity);
            }
            catch (MissionAdminException e) { error.WriteLine(e.Message); return ExitCodes.Usage; }
        }
        // A template's values are checked before any connection too (MissionTemplate's rules; a zero cost is valid).
        if (a.Template is { } t)
        {
            try { MissionTemplates.ValidateProposed(t.TemplateId, t.EnergyCost, t.MinOperatives, t.MaxOperatives); }
            catch (MissionAdminException e) { error.WriteLine(e.Message); return ExitCodes.Usage; }
        }
        await using var dataSource = settings.CreateDataSource("blackglass-admin", maxPoolSize: 2, timeouts: timeouts);
        // Without a loaded catalogue the store gets one that refuses: no security verb may come to depend on it unnoticed.
        var store = catalogue is not null
            ? new AccountAdminStore(dataSource, catalogue)
            : new AccountAdminStore(dataSource, () => throw new InvalidOperationException($"admin {a.Verb} does not load the catalogue."));
        var energy = energySettings is not null ? new EnergyAdminStore(dataSource, energySettings.GrantMax) : null;
        var insurance = a.IsInsuranceVerb ? new InsuranceAdminStore(dataSource) : null;
        var insuranceReader = a.IsInsuranceVerb ? new InsuranceReader(dataSource) : null;
        var templates = a.IsMissionTemplateVerb ? new MissionTemplates(dataSource) : null;
        var devHosts = a.IsDevHostVerb ? new DevHosts(dataSource) : null;
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
                AdminConfirmation.Account => await ConfirmAsync(a, store, energy, insuranceReader, output, error, input, ct),
                AdminConfirmation.Publish when a.IsInsuranceVerb => await ConfirmInsurancePublishAsync(a, terms!, insuranceReader!, output, error, input, ct),
                AdminConfirmation.Publish when a.IsMissionTemplateVerb => await ConfirmTemplatePublishAsync(a, templates!, output, error, input, ct),
                AdminConfirmation.Register => await ConfirmRegisterDevAsync(a, devHosts!, output, error, input, ct),
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
                    // id below is the operator's alone. Mismatches still exit 0: the recorded anomaly is the signal. Each finding
                    // is named on its own line, in the reconciler's order, whether this run recorded it or found it outstanding.
                    var report = await new EnergyReconciler(dataSource).RunAsync(ct);
                    output.WriteLine($"walletsChecked={report.WalletsChecked}");
                    output.WriteLine($"mismatches={report.Mismatches.Count}");
                    output.WriteLine($"anomaliesRecorded={report.AnomaliesRecorded}");
                    foreach (var m in report.Mismatches)
                        output.WriteLine($"mismatch={AccountId.Format(m.AccountId)}:{m.Rule}");
                    break;
                case "insurance-policy-publish":
                    var published = await insurance!.PublishPolicyAsync(a.PolicyId!, terms!, a.Operator, ct);
                    output.WriteLine($"policyId={published.PolicyId}");
                    output.WriteLine($"policyVersion={published.Version}");
                    break;
                case "insurance-activate":
                    var activated = await insurance!.ActivateAsync(a.Account!.Value, a.PolicyId!, a.DurationSeconds!.Value, a.IdempotencyKey!.Value, a.Operator, ct);
                    output.WriteLine($"accountId={AccountId.Format(a.Account.Value)}");
                    output.WriteLine($"policyId={activated.PolicyId}");
                    output.WriteLine($"policyVersion={activated.PolicyVersion}");
                    // The period this activation bought, from its stored row: a replay prints exactly what the first call did.
                    output.WriteLine($"startsAt={Timestamp(activated.StartsAt)}");
                    output.WriteLine($"expiresAt={Timestamp(activated.ExpiresAt)}");
                    output.WriteLine($"replayed={(activated.Replayed ? "true" : "false")}");
                    break;
                case "mission-template-publish":
                    var proposed = a.Template!;
                    var template = await templates!.PublishAsync(proposed.TemplateId, proposed.EnergyCost, proposed.MinOperatives, proposed.MaxOperatives, a.Operator, ct);
                    output.WriteLine($"templateId={template.TemplateId}");
                    output.WriteLine($"templateVersion={template.Version}");
                    output.WriteLine($"energyCost={template.EnergyCost}");
                    output.WriteLine($"minOperatives={template.MinOperatives}");
                    output.WriteLine($"maxOperatives={template.MaxOperatives}");
                    break;
                case "host-register-dev":
                    // The host's secret is never printed: only its hash is stored, and nothing in Stage 7 uses it.
                    var registered = await devHosts!.RegisterAsync(a.HostId!, a.Capacity!.Value, a.Operator, ct);
                    output.WriteLine($"hostId={registered.HostId}");
                    output.WriteLine("kind=dev_fixture");
                    output.WriteLine($"capacity={registered.Capacity}");
                    break;
                case "host-heartbeat-dev":
                    var beat = await devHosts!.HeartbeatAsync(a.HostId!, a.Operator, ct);
                    output.WriteLine($"hostId={beat.HostId}");
                    output.WriteLine($"lastHeartbeatAt={Timestamp(beat.LastHeartbeatAt)}");
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
        catch (InsuranceException e)
        {
            error.WriteLine(e.Message);
            return e.Error switch
            {
                InsuranceError.UnknownPolicy => ExitCodes.NotFound,
                InsuranceError.IdempotencyKeyReused or InsuranceError.CrossTierDeferred or InsuranceError.PolicyVersionConflict => ExitCodes.Conflict,
                _ => ExitCodes.Usage,
            };
        }
        catch (MissionAdminException e)
        {
            error.WriteLine(e.Message);
            return e.Error switch
            {
                MissionAdminError.TemplateVersionConflict or MissionAdminError.HostExists => ExitCodes.Conflict,
                MissionAdminError.UnknownDevHost => ExitCodes.NotFound,
                _ => ExitCodes.Usage,
            };
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
    /// energy verbs, which also print the stored energy balance (read without regeneration or a lock), or "(no wallet)";
    /// <paramref name="insurance"/> for an activation, which also prints the stored coverage (read without a lock, as stored:
    /// an expired one too), or "(none)".</summary>
    static async Task<int?> ConfirmAsync(AdminArguments a, AccountAdminStore store, EnergyAdminStore? energy, InsuranceReader? insurance, TextWriter output, TextWriter error,
        TextReader? input, CancellationToken ct)
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
        if (insurance is not null)
        {
            var coverage = await insurance.DescribeCoverageAsync(target.AccountId, ct);
            output.WriteLine($"target.coverage={(coverage is null ? "(none)" : $"{coverage.PolicyId} v{coverage.PolicyVersion} until {Timestamp(coverage.ExpiresAt)}")}");
        }

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

    /// <summary>An insurance policy publication: prints the policy's latest version and terms and the proposed ones (the
    /// proposed version is advisory: the transaction mints it under the policy's lock), then returns null when confirmed (--yes,
    /// or typing "publish"), else the exit code to stop with. Its transaction begins only after.</summary>
    static async Task<int?> ConfirmInsurancePublishAsync(AdminArguments a, InsuranceTerms proposed, InsuranceReader insurance, TextWriter output, TextWriter error,
        TextReader? input, CancellationToken ct)
    {
        var current = await insurance.DescribeLatestPolicyAsync(a.PolicyId!, ct);
        output.WriteLine($"target.policyId={a.PolicyId}");
        output.WriteLine($"target.policyCurrentVersion={current?.Version.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
        output.WriteLine($"target.policyCurrentTerms={(current is null ? "(none)" : CanonicalOrStored(current.Terms))}");
        output.WriteLine($"target.policyProposedVersion={(current?.Version ?? 0) + 1}");
        output.WriteLine($"target.policyProposedTerms={proposed.Canonical()}");

        if (a.Confirmed) return null;
        if (input is null)
        {
            error.WriteLine($"Not confirmed, so nothing was changed. To publish this insurance policy version, run this at a terminal and type publish, or add {AdminArguments.YesSwitch}.");
            return ExitCodes.Usage;
        }
        error.Write($"Type publish to confirm the new version of insurance policy {a.PolicyId} (later activations of it use it): ");
        var typed = await input.ReadLineAsync(ct);
        if (typed == "publish") return null;
        error.WriteLine("Not confirmed, so nothing was changed.");
        return ExitCodes.NotConfirmed;
    }

    /// <summary>A mission template publication: prints the template's latest version and values and the proposed ones (the
    /// proposed version is advisory: the transaction mints it under the template's lock), then returns null when confirmed
    /// (--yes, or typing "publish"), else the exit code to stop with. Its transaction begins only after.</summary>
    static async Task<int?> ConfirmTemplatePublishAsync(AdminArguments a, MissionTemplates templates, TextWriter output, TextWriter error, TextReader? input,
        CancellationToken ct)
    {
        var proposed = a.Template!;
        var current = await templates.DescribeLatestAsync(proposed.TemplateId, ct);
        output.WriteLine($"target.templateId={proposed.TemplateId}");
        output.WriteLine($"target.templateCurrentVersion={current?.Version.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
        output.WriteLine($"target.templateCurrentEnergyCost={current?.EnergyCost.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
        output.WriteLine($"target.templateCurrentMinOperatives={current?.MinOperatives.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
        output.WriteLine($"target.templateCurrentMaxOperatives={current?.MaxOperatives.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
        output.WriteLine($"target.templateProposedVersion={(current?.Version ?? 0) + 1}");
        output.WriteLine($"target.templateProposedEnergyCost={proposed.EnergyCost}");
        output.WriteLine($"target.templateProposedMinOperatives={proposed.MinOperatives}");
        output.WriteLine($"target.templateProposedMaxOperatives={proposed.MaxOperatives}");

        if (a.Confirmed) return null;
        if (input is null)
        {
            error.WriteLine($"Not confirmed, so nothing was changed. To publish this mission template version, run this at a terminal and type publish, or add {AdminArguments.YesSwitch}.");
            return ExitCodes.Usage;
        }
        error.Write($"Type publish to confirm the new version of mission template {proposed.TemplateId} (later admissions to it use it): ");
        var typed = await input.ReadLineAsync(ct);
        if (typed == "publish") return null;
        error.WriteLine("Not confirmed, so nothing was changed.");
        return ExitCodes.NotConfirmed;
    }

    /// <summary>A development host fixture's registration: prints the host id, whether a host of that id exists, its kind and
    /// capacity, then returns null when confirmed (--yes, or typing "register"), else the exit code to stop with.</summary>
    static async Task<int?> ConfirmRegisterDevAsync(AdminArguments a, DevHosts devHosts, TextWriter output, TextWriter error, TextReader? input, CancellationToken ct)
    {
        output.WriteLine($"target.hostId={a.HostId}");
        output.WriteLine($"target.hostExists={(await devHosts.ExistsAsync(a.HostId!, ct) ? "true" : "false")}");
        output.WriteLine("target.kind=dev_fixture");
        output.WriteLine($"target.capacity={a.Capacity}");

        if (a.Confirmed) return null;
        if (input is null)
        {
            error.WriteLine($"Not confirmed, so nothing was changed. To register this development host fixture, run this at a terminal and type register, or add {AdminArguments.YesSwitch}.");
            return ExitCodes.Usage;
        }
        error.Write($"Type register to confirm development host fixture {a.HostId} (capacity only where {AttemptSettings.DevHostsVar}=enabled; never in production): ");
        var typed = await input.ReadLineAsync(ct);
        if (typed == "register") return null;
        error.WriteLine("Not confirmed, so nothing was changed.");
        return ExitCodes.NotConfirmed;
    }

    /// <summary>Stored terms in the same canonical form as the proposed ones, so the two compare line for line; terms the parser
    /// refuses (none should exist) are shown as stored.</summary>
    static string CanonicalOrStored(string stored)
    {
        try { return InsuranceTerms.Parse(stored).Canonical(); }
        catch (InsuranceTermsException) { return stored; }
    }

    /// <summary>The largest terms file read (64 KiB): terms are a few hundred bytes.</summary>
    const int MaxTermsFileBytes = 64 * 1024;

    /// <summary>The terms file's text, strict UTF-8; an unreadable, oversized or undecodable file is InvalidTerms.</summary>
    static string ReadTermsFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxTermsFileBytes)
                throw new InsuranceException(InsuranceError.InvalidTerms, $"The terms file is larger than {MaxTermsFileBytes} bytes. Nothing was published.");
            return File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InsuranceException(InsuranceError.InvalidTerms, $"The terms file could not be read ({e.GetType().Name}). Nothing was published.");
        }
    }

    /// <summary>A grant or activation whose connection broke (or failed oddly) may have committed before the failure was seen: a
    /// retry with a new key could grant or extend twice, while the same key replays or acts once.</summary>
    static void WriteGrantOutcomeUnknown(AdminArguments a, TextWriter error)
    {
        if (a.Verb == "energy-grant")
            error.WriteLine($"The grant may or may not have committed: retry with the same --idempotency-key {a.IdempotencyKey!.Value:D}, never a new one.");
        if (a.Verb == "insurance-activate")
            error.WriteLine($"The activation may or may not have committed: retry with the same --idempotency-key {a.IdempotencyKey!.Value:D}, never a new one.");
    }

    /// <summary>A database time, UTC, to the microsecond.</summary>
    static string Timestamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>An interval in seconds: whole for any version this verb published; a version inserted otherwise may carry a fraction.</summary>
    static string Seconds(TimeSpan interval) => interval.TotalSeconds.ToString(CultureInfo.InvariantCulture);
}
