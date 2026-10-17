using System.Text.RegularExpressions;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Api.Admin;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Settlement;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>The Stage 9 recovery verbs through the admin CLI (S9-B9): confirmation (exit 64 without --yes when stdin is not a
/// terminal), the attestation switches, the exit codes of refusals, the audit, and output free of secrets.</summary>
[Trait("Requires", "Postgres")]
public class RecoveryCliTests : IAsyncLifetime
{
    TestDatabase db = null!;
    MissionKit kit = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        kit = await MissionKit.CreateAsync(db);
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    async Task<(int Code, string Out, string Err)> CliAsync(string? typed, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await AdminCommand.RunAsync([.. args, "--operator", "ops", "--reason", "stage 9 test"], db.EnvFor("app"), output, error, default,
            input: typed is null ? null : new StringReader(typed), loadCatalogue: () => throw new InvalidOperationException("Recovery verbs never load the catalogue."));
        return (code, output.ToString(), error.ToString());
    }

    static string Field(string output, string name) => Regex.Match(output, $"(?m)^{Regex.Escape(name)}=(.*)$").Groups[1].Value.TrimEnd('\r');

    [Fact]
    public async Task Revalidate_and_void_participant_need_confirmation_and_their_attestations()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        Assert.IsType<ResultOutcome.Quarantined>(await ManifestKit.SubmitAsync(ManifestKit.Store(db), db, ManifestKit.Body(ManifestKit.Manifest(r) with { BuildRevision = "r9" })));
        var submission = await ScalarAsSuperuserAsync<long>(db, "select max(submission_id) from result_submissions");
        var target = new[] { "--attempt", r.Attempt.ToString(), "--account-base58", Base58Id.Format(r.Account) };
        Assert.Equal(ExitCodes.Usage, (await CliAsync(null, ["attempt", "revalidate", .. target, "--submission", submission.ToString()])).Code); // no terminal, no --yes
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync("nope", ["attempt", "revalidate", .. target, "--submission", submission.ToString()])).Code);
        var revalidated = await CliAsync(r.Attempt.ToString(), ["attempt", "revalidate", .. target, "--submission", submission.ToString()]);
        Assert.Equal(ExitCodes.Ok, revalidated.Code);
        Assert.Equal(("failed", "V4.build"), (Field(revalidated.Out, "result"), Field(revalidated.Out, "codes")));

        Assert.Equal(ExitCodes.Usage, (await CliAsync(null, ["attempt", "void-participant", .. target, "--evidence", "outbox empty", "--revalidated", "--yes"])).Code); // no --outbox-checked
        Assert.Equal(ExitCodes.Usage, (await CliAsync(null, ["attempt", "void-participant", .. target, "--evidence", "outbox empty", "--outbox-checked", "--yes"])).Code); // no --revalidated
        var voided = await CliAsync(null, ["attempt", "void-participant", .. target, "--evidence", "outbox empty", "--outbox-checked", "--revalidated", "--yes"]);
        Assert.Equal((ExitCodes.Ok, "operator_quarantine", "10"), (voided.Code, Field(voided.Out, "voidBasis"), Field(voided.Out, "refunded")));
        Assert.Equal(ExitCodes.Conflict, (await CliAsync(null, ["attempt", "void-participant", .. target, "--evidence", "again", "--outbox-checked", "--revalidated", "--yes"])).Code);

        var shown = await CliAsync(null, ["attempt", "show", "--attempt", r.Attempt.ToString()]);
        Assert.Equal(ExitCodes.Ok, shown.Code);
        Assert.Contains($"participant.{Base58Id.Format(r.Account)}.state=settled", shown.Out);
        Assert.DoesNotContain("rawBase64", shown.Out);
        Assert.DoesNotContain(r.Mission.Credential.Text, shown.Out);
        Assert.Equal(ExitCodes.Usage, (await CliAsync(null, ["attempt", "show", "--attempt", r.Attempt.ToString(), "--raw"])).Code); // --raw needs --submission
    }

    [Fact]
    public async Task Catalogue_import_is_confirmed_by_typing_import_verifies_and_audits()
    {
        var file = Path.Combine(Path.GetTempPath(), $"bg-cat-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllBytesAsync(file, Blackglass.Accounts.Domain.Catalogue.CanonicalCatalogueJson.ToBytes(CatalogueHistoryTests.Retiring().Document));
            Assert.Equal(ExitCodes.Usage, (await CliAsync(null, "catalogue", "import", "--file", file)).Code);
            Assert.Equal(ExitCodes.Usage, (await CliAsync(null, "catalogue", "import", "--file", file, "--catalogue-version", "sha256:" + new string('0', 64), "--yes")).Code);
            var imported = await CliAsync("import", "catalogue", "import", "--file", file);
            Assert.Equal((ExitCodes.Ok, "true"), (imported.Code, Field(imported.Out, "registered")));
            Assert.Equal(CatalogueHistoryTests.Retiring().Version, Field(imported.Out, "catalogueVersion"));
            Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from operator_actions where action = 'catalogue_import'"));
            await File.WriteAllTextAsync(file, "{\"not\":\"a catalogue\"}\n");
            Assert.Equal(ExitCodes.Usage, (await CliAsync(null, "catalogue", "import", "--file", file, "--yes")).Code);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Declare_lost_and_the_anomaly_verbs_work_through_the_cli()
    {
        var r = await ManifestKit.StartAsync(kit, db);
        await new Blackglass.Accounts.Persistence.Hosts.HostAdminStore(db.AsApp()).DisableAsync("h1", Op);
        await ExecuteAsSuperuserAsync(db, "update mission_hosts set last_heartbeat_at = clock_timestamp() - interval '1 hour' where host_id = 'h1'");
        Assert.Equal(ExitCodes.Usage, (await CliAsync(null, "host", "declare-lost", "--host-id", "h1", "--evidence", "disk failed", "--yes")).Code); // no --outbox-unrecoverable
        var lost = await CliAsync("h1", "host", "declare-lost", "--host-id", "h1", "--evidence", "disk failed", "--outbox-unrecoverable");
        Assert.Equal((ExitCodes.Ok, "true", "1"), (lost.Code, Field(lost.Out, "declaredNow"), Field(lost.Out, "voided")));
        var list = await CliAsync(null, "anomaly", "list", "--open");
        Assert.Equal(ExitCodes.Ok, list.Code);
        var id = Regex.Match(list.Out, @"(?m)^anomaly=(\d+) kind=infra_void").Groups[1].Value;
        Assert.NotEmpty(id);
        Assert.Equal(ExitCodes.Ok, (await CliAsync(id, "anomaly", "review", "--anomaly", id, "--note", "host replaced")).Code);
        Assert.Equal(ExitCodes.Conflict, (await CliAsync(null, "anomaly", "review", "--anomaly", id, "--note", "again", "--yes")).Code);
        Assert.Equal(ExitCodes.NotFound, (await CliAsync(null, "anomaly", "review", "--anomaly", "999999", "--note", "x", "--yes")).Code);
        _ = r;
    }
}
