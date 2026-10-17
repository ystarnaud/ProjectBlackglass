using System.Globalization;
using Npgsql;
using Blackglass.Accounts.Api.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.Persistence.Catalogue;
using Blackglass.Accounts.Persistence.Settlement;

namespace Blackglass.Accounts.Api.Admin;

/// <summary>
/// Stage 9's operator recovery verbs (S9-B9; revision 3), run by <see cref="AdminCommand"/> after its schema check and the
/// verb's confirmation: attempt show, revalidate, import-result and void-participant; host declare-lost; catalogue import;
/// anomaly list and review. Each prints "key=value" lines; refusals print the rule and exit 64 (a missing attestation), 65
/// (not found) or 66 (not allowed), with nothing changed. Output never holds a seed, salt, credential or receipt.
/// </summary>
internal static class RecoveryCommand
{
    /// <summary>The largest file a recovery verb reads (an imported result is at most 64 KiB; a catalogue a few KiB).</summary>
    const int MaxFileBytes = 4 * 1024 * 1024;

    public static byte[] ReadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxFileBytes) throw new AdminUsageException($"--file is larger than {MaxFileBytes} bytes.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new AdminUsageException("--file can't be read."); }
    }

    public static async Task<int> RunAsync(AdminArguments a, NpgsqlDataSource db, IReadOnlyDictionary<string, string?> env, byte[]? file, TextWriter output, TextWriter error,
        CancellationToken ct)
    {
        AttemptSettings settings;
        try { settings = AttemptSettings.FromEnvironment(env); }
        catch (AttemptSettingsException e) { error.WriteLine($"Configuration error: {e.Message}"); return ExitCodes.Failure; }
        var options = new SettlementOptions(settings.ResultSubmissionsMax, settings.ResultUnidentifiedMax, settings.QuarantineEscalationHours,
            new SettlementLimits(settings.ManifestMaxElapsedMs, settings.ManifestElapsedToleranceMs));
        var catalogues = new CatalogueHistoryStore(db);
        var recovery = new RecoveryStore(db, catalogues, options);
        try
        {
            switch (a.Verb)
            {
                case "attempt-show":
                    foreach (var line in await recovery.ShowAsync(a.Attempt!.Value, a.Submission, a.Has("--raw"), ct)) output.WriteLine(line);
                    break;
                case "attempt-revalidate":
                    var r = await recovery.RevalidateAsync(a.Attempt!.Value, a.Account!.Value, a.Submission!.Value, a.Has("--host-trust-reviewed"), a.Operator, ct);
                    output.WriteLine($"accountId={Base58Id.Format(a.Account.Value)}");
                    output.WriteLine($"result={r.Result}");
                    output.WriteLine($"codes={string.Join(',', r.Codes)}");
                    output.WriteLine($"manifestHash={r.ManifestHash ?? "-"}");
                    break;
                case "attempt-import-result":
                    var imported = await recovery.ImportResultAsync(a.Attempt!.Value, a.Account!.Value, file!, a.Evidence!, a.Has("--host-trust-reviewed"), a.Operator, ct);
                    output.WriteLine($"accountId={Base58Id.Format(a.Account.Value)}");
                    output.WriteLine($"submissionId={imported.SubmissionId}");
                    output.WriteLine($"inserted={(imported.Inserted ? "true" : "false")}");
                    output.WriteLine($"disposition={imported.Disposition}");
                    break;
                case "attempt-void-participant":
                    var voided = await recovery.VoidParticipantAsync(a.Attempt!.Value, a.Account!.Value,
                        new VoidChecks(a.Has("--outbox-checked"), a.Has("--revalidated"), a.Has("--host-trust-reviewed"), a.Has("--unrecoverable")), a.Evidence!, a.Operator, ct);
                    output.WriteLine($"accountId={Base58Id.Format(a.Account.Value)}");
                    output.WriteLine($"voidBasis={voided.Basis}");
                    output.WriteLine($"refunded={voided.Refunded}");
                    break;
                case "host-declare-lost":
                    var lost = await recovery.DeclareLostAsync(a.HostId!, a.Has("--outbox-unrecoverable"), a.Evidence!, a.Operator, ct);
                    output.WriteLine($"hostId={a.HostId}");
                    output.WriteLine($"declaredNow={(lost.DeclaredNow ? "true" : "false")}");
                    output.WriteLine($"voided={lost.Voided.Count}");
                    foreach (var (attempt, account) in lost.Voided) output.WriteLine($"voided.participant={attempt}/{Base58Id.Format(account)}");
                    break;
                case "catalogue-import":
                    GameCatalogue catalogue;
                    try { catalogue = GameCatalogue.Load(file!); }
                    catch (CatalogueException e) { error.WriteLine($"Refusing to import: the file is not a valid catalogue ({e.Message})."); return ExitCodes.Usage; }
                    if (a.CatalogueVersion is { } expected && expected != catalogue.Version)
                    {
                        error.WriteLine($"Refusing to import: the file's version is {catalogue.Version}, not {expected}.");
                        return ExitCodes.Usage;
                    }
                    var registered = await catalogues.RegisterAsync(catalogue, a.Operator, ct);
                    output.WriteLine($"catalogueVersion={catalogue.Version}");
                    output.WriteLine($"registered={(registered ? "true" : "false")}");
                    break;
                case "anomaly-list":
                    foreach (var x in await new AnomalyStore(db).ListAsync(a.Has("--open"), ct))
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"anomaly={x.Id} kind={x.Kind} attempt={x.AttemptId?.ToString("D") ?? "-"} account={(x.AccountId is { } acc ? Base58Id.Format(acc) : "-")} host={x.HostId ?? "-"} created={x.CreatedAt:O} reviewed={(x.ReviewedAt is { } rv ? rv.ToString("O", CultureInfo.InvariantCulture) : "-")} detail={x.Detail}"));
                    break;
                case "anomaly-review":
                    await new AnomalyStore(db).ReviewAsync(a.Anomaly!.Value, a.Note!, a.Operator, ct);
                    output.WriteLine($"anomaly={a.Anomaly}");
                    output.WriteLine("reviewed=true");
                    break;
                default:
                    throw new InvalidOperationException($"{a.Verb} is not a recovery verb.");
            }
            return ExitCodes.Ok;
        }
        catch (RecoveryException e)
        {
            error.WriteLine($"Refusing admin {a.Verb.Replace('-', ' ')}: {e.Message} Nothing was changed.");
            return e.Error switch { RecoveryError.NotFound => ExitCodes.NotFound, RecoveryError.MissingCheck => ExitCodes.Usage, _ => ExitCodes.Conflict };
        }
        catch (CatalogueRegistrationException e)
        {
            error.WriteLine($"Refusing to import: {e.Message}. Nothing was changed.");
            return ExitCodes.Conflict;
        }
    }
}
