using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Api.Admin;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Attempts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Versioned, immutable mission templates (owner ruling S7-D1): <c>mission template publish</c> validates its values before
/// any connection (a zero cost is valid), shows the current and proposed template and acts only when confirmed, then mints
/// the next version under a per-template advisory lock and audits it in the same transaction. A published version never
/// changes. <see cref="MissionTemplates.LatestAsync"/> reads the latest version for admission.
/// </summary>
[Trait("Requires", "Postgres")]
public class MissionTemplateTests : IAsyncLifetime
{
    TestDatabase db = null!;

    public async Task InitializeAsync() => db = await MigratedAsync();

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    async Task<(int Code, string Out, string Err)> CliAsync(string? typed, Dictionary<string, string?>? env, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await AdminCommand.RunAsync(args, env ?? db.EnvFor("app"), output, error, default, input: typed is null ? null : new StringReader(typed),
            loadCatalogue: () => throw new InvalidOperationException("mission template publish never loads the catalogue."));
        return (code, output.ToString(), error.ToString());
    }

    static string[] Publish(string templateId, int cost, int min, int max, params string[] more) =>
    [
        "mission", "template", "publish", "--template-id", templateId, "--energy-cost", cost.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--min-operatives", min.ToString(System.Globalization.CultureInfo.InvariantCulture), "--max-operatives", max.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--operator", "ops", "--reason", "template test", .. more,
    ];

    static string Field(string output, string name) => Regex.Match(output, $"(?m)^{Regex.Escape(name)}=(.*)$").Groups[1].Value.TrimEnd('\r');
    static string[] Names(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').Split('=')[0]).ToArray();
    Task<long> CountAsync(string table) => ScalarAsSuperuserAsync<long>(db, $"select count(*) from {table}");

    async Task<MissionTemplate?> LatestAsync(string templateId, bool inTransaction = false)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        if (!inTransaction) return await MissionTemplates.LatestAsync(conn, null, templateId);
        await using var tx = await conn.BeginTransactionAsync();
        return await MissionTemplates.LatestAsync(conn, tx, templateId);
    }

    [Fact]
    public async Task Templates_are_immutable_and_publish_audited()
    {
        Assert.Equal(new MissionTemplate("procedural-standard", 1, 10, 1, 3), await LatestAsync("procedural-standard")); // the seed
        Assert.Null(await LatestAsync("never-published"));

        // The store: the next version, audited in the same transaction with every value.
        var published = await new MissionTemplates(db.AsApp()).PublishAsync("procedural-standard", 12, 2, 4, Op);
        Assert.Equal(new MissionTemplate("procedural-standard", 2, 12, 2, 4), published);
        Assert.Equal(published, await LatestAsync("procedural-standard", inTransaction: true));
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
        {
            var audit = await su.QuerySingleAsync<(string Action, long? AccountId, string Operator, string Result)>(
                "select action, account_id, operator, result::text from operator_actions");
            Assert.Equal(("mission_template_publish", (long?)null, "test-operator"), (audit.Action, audit.AccountId, audit.Operator));
            var result = JsonDocument.Parse(audit.Result).RootElement;
            Assert.Equal(("procedural-standard", 2, 12, 2, 4),
                (result.GetProperty("templateId").GetString(), result.GetProperty("version").GetInt32(), result.GetProperty("energyCost").GetInt32(),
                    result.GetProperty("minOperatives").GetInt32(), result.GetProperty("maxOperatives").GetInt32()));
        }

        // Ten concurrent publications of one template take ten consecutive versions; another template starts at 1.
        var store = new MissionTemplates(db.AsApp());
        var versions = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => store.PublishAsync("procedural-standard", i, 1, 3, Op)));
        Assert.Equal(Enumerable.Range(3, 10), versions.Select(v => v.Version).Order());
        Assert.Equal(1, (await store.PublishAsync("procedural-raid", 20, 2, 6, Op)).Version);
        Assert.Equal(12, (await LatestAsync("procedural-standard"))!.Version);
        Assert.Equal(12L, await CountAsync("operator_actions"));

        // A published version never changes: the app has no UPDATE or DELETE, and even the owner's are refused.
        await using (var app = await db.AsApp().OpenConnectionAsync())
        {
            var update = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync("update mission_templates set energy_cost = 0 where template_id = 'procedural-standard'"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);
            var delete = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync("delete from mission_templates where version = 1"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
        }
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => su.ExecuteAsync("update mission_templates set energy_cost = 0 where version = 1"));
            Assert.Equal("mission_template_is_immutable", refused.ConstraintName);
        }
        Assert.Equal(10, await ScalarAsSuperuserAsync<int>(db, "select energy_cost from mission_templates where template_id = 'procedural-standard' and version = 1"));

        // A failed audit rolls the publication back.
        await ExecuteAsSuperuserAsync(db,
            """
            create function fail_audit() returns trigger language plpgsql as $$ begin raise exception 'audit fails on purpose' using errcode = 'P0001'; end $$;
            create trigger fail_audit before insert on operator_actions for each row execute function fail_audit();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => store.PublishAsync("procedural-standard", 1, 1, 1, Op));
        await ExecuteAsSuperuserAsync(db, "drop trigger fail_audit on operator_actions; drop function fail_audit()");
        Assert.Equal(12, (await LatestAsync("procedural-standard"))!.Version);
    }

    [Fact]
    public async Task A_zero_cost_template_can_be_published()
    {
        var (code, output, _) = await CliAsync(null, null, Publish("training-free", 0, 1, 1, "--yes"));
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(["templateId", "templateVersion", "energyCost", "minOperatives", "maxOperatives", "correlationId"],
            Names(output).Where(n => !n.StartsWith("target.", StringComparison.Ordinal)));
        Assert.Equal(("training-free", "1", "0", "1", "1"),
            (Field(output, "templateId"), Field(output, "templateVersion"), Field(output, "energyCost"), Field(output, "minOperatives"), Field(output, "maxOperatives")));
        Assert.Equal(new MissionTemplate("training-free", 1, 0, 1, 1), await LatestAsync("training-free"));
        Assert.Equal(await ScalarAsSuperuserAsync<Guid>(db, "select correlation_id from operator_actions"), Guid.Parse(Field(output, "correlationId")));
    }

    [Fact]
    public async Task Mission_template_publish_validates_before_connecting_previews_and_needs_confirmation()
    {
        // Invalid values never reach the database (it is unreachable: 64, not 69).
        var unreachable = db.EnvFor("app");
        unreachable["BLACKGLASS_DB_PORT"] = "1";
        foreach (var args in new[]
                 {
                     Publish("Procedural_Standard", 10, 1, 3, "--yes"), Publish(new string('a', 65), 10, 1, 3, "--yes"), Publish("procedural-standard", -1, 1, 3, "--yes"),
                     Publish("procedural-standard", 10, 0, 3, "--yes"), Publish("procedural-standard", 10, 3, 2, "--yes"), Publish("procedural-standard", 10, 1, 17, "--yes"),
                 })
        {
            var (code, output, error) = await CliAsync(null, unreachable, args);
            Assert.Equal((ExitCodes.Usage, ""), (code, output));
            Assert.Contains("Nothing was published", error);
        }
        Assert.Equal(ExitCodes.Unavailable, (await CliAsync(null, unreachable, Publish("procedural-standard", 0, 1, 16, "--yes"))).Code); // valid: gets as far as the connection

        // Not confirmed: current and proposed values are shown, nothing changes.
        var preview = await CliAsync(null, null, Publish("procedural-standard", 15, 2, 4));
        Assert.Equal(ExitCodes.Usage, preview.Code);
        Assert.Equal(["target.templateId", "target.templateCurrentVersion", "target.templateCurrentEnergyCost", "target.templateCurrentMinOperatives",
            "target.templateCurrentMaxOperatives", "target.templateProposedVersion", "target.templateProposedEnergyCost", "target.templateProposedMinOperatives",
            "target.templateProposedMaxOperatives"], Names(preview.Out));
        Assert.Equal(("procedural-standard", "1", "10", "1", "3", "2", "15", "2", "4"),
            (Field(preview.Out, "target.templateId"), Field(preview.Out, "target.templateCurrentVersion"), Field(preview.Out, "target.templateCurrentEnergyCost"),
                Field(preview.Out, "target.templateCurrentMinOperatives"), Field(preview.Out, "target.templateCurrentMaxOperatives"),
                Field(preview.Out, "target.templateProposedVersion"), Field(preview.Out, "target.templateProposedEnergyCost"),
                Field(preview.Out, "target.templateProposedMinOperatives"), Field(preview.Out, "target.templateProposedMaxOperatives")));
        Assert.Contains("--yes", preview.Err);
        var fresh = await CliAsync(null, null, Publish("procedural-raid", 15, 2, 4));
        Assert.Equal(("(none)", "(none)", "1"), (Field(fresh.Out, "target.templateCurrentVersion"), Field(fresh.Out, "target.templateCurrentEnergyCost"),
            Field(fresh.Out, "target.templateProposedVersion")));
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync("yes\n", null, Publish("procedural-standard", 15, 2, 4))).Code);
        Assert.Equal((1L, 0L), (await CountAsync("mission_templates"), await CountAsync("operator_actions")));

        // Typing publish confirms.
        var published = await CliAsync("publish\n", null, Publish("procedural-standard", 15, 2, 4));
        Assert.Equal((ExitCodes.Ok, "2"), (published.Code, Field(published.Out, "templateVersion")));
        Assert.Contains("Type publish", published.Err);
        Assert.Equal(1L, await CountAsync("operator_actions"));

        // A 23505 on the primary key (an insert that bypassed the lock) is a conflict (66), reported and not retried.
        await ExecuteAsSuperuserAsync(db,
            """
            create sequence fake_template_attempts;
            grant usage on sequence fake_template_attempts to accounts_app;
            create function fake_template_duplicate() returns trigger language plpgsql as $$ begin
              perform nextval('fake_template_attempts');
              raise exception 'duplicate on purpose' using errcode = 'unique_violation', constraint = 'mission_templates_pkey'; end $$;
            create trigger fake_template_duplicate before insert on mission_templates for each row execute function fake_template_duplicate();
            """);
        var conflict = await CliAsync(null, null, Publish("procedural-standard", 15, 2, 4, "--yes"));
        // A sequence ignores rollback: it counts every insert the CLI tried.
        var attempts = await ScalarAsSuperuserAsync<long>(db, "select case when is_called then last_value else 0 end from fake_template_attempts");
        await ExecuteAsSuperuserAsync(db, "drop trigger fake_template_duplicate on mission_templates; drop function fake_template_duplicate(); drop sequence fake_template_attempts");
        Assert.Equal(1L, attempts);
        Assert.Equal(ExitCodes.Conflict, conflict.Code);
        Assert.Contains("retry", conflict.Err);
        Assert.DoesNotContain("correlationId=", conflict.Out);
        Assert.Equal((2L, 1L), (await CountAsync("mission_templates"), await CountAsync("operator_actions")));
    }
}
