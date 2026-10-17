using System.Text.RegularExpressions;
using Dapper;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Api.Admin;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Insurance;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The admin CLI's insurance verbs: <c>insurance policy publish</c> and <c>insurance activate</c> print their outputs and exit
/// codes, show their target and act only when confirmed (the word <c>publish</c> for a publication, the Base58 account id for
/// an activation, or --yes), refuse invalid terms, policy ids and durations before any connection, and never load the game
/// catalogue. InsuranceAdminStoreTests covers the transactions themselves.
/// </summary>
[Trait("Requires", "Postgres")]
public class InsuranceCliTests : IAsyncLifetime
{
    TestDatabase db = null!;
    int loads;
    readonly List<string> files = [];

    public async Task InitializeAsync() => db = await MigratedAsync();

    public async Task DisposeAsync()
    {
        foreach (var f in files) File.Delete(f);
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    /// <summary>Runs the admin CLI with a catalogue loader that counts its calls and refuses, as a broken build would.</summary>
    async Task<(int Code, string Out, string Err)> CliAsync(string? typed, Dictionary<string, string?>? env, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await AdminCommand.RunAsync(args, env ?? db.EnvFor("app"), output, error, default,
            input: typed is null ? null : new StringReader(typed),
            loadCatalogue: () => { loads++; throw new InvalidOperationException("The embedded catalogue.json resource is missing."); });
        return (code, output.ToString(), error.ToString());
    }

    static string Field(string output, string name) => Regex.Match(output, $"(?m)^{Regex.Escape(name)}=(.*)$").Groups[1].Value.TrimEnd('\r');
    static string[] Names(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').Split('=')[0]).ToArray();
    static string[] Without(string[] names, string prefix) => names.Where(n => !n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    static string Terms(int refundPercent = 50) => $$"""
        {
          "covers": ["defeat", "abandoned", "disconnected", "expired"],
          "consumableRefund": { "percent": {{refundPercent}}, "rounding": "floor", "destination": "stash" },
          "lootRetention": { "maxEntries": 2, "maxUnitsPerEntry": 3, "eligibleCategories": ["Consumable", "Armor", "Utility", "Weapon"],
            "rank": ["categoryPriority", "saltedHash"], "categoryPriority": ["Weapon", "Armor", "Utility", "Consumable"] },
          "progressXp": { "percent": 40, "basis": "requiredObjectivesCompleted" }
        }
        """;

    string TermsFile(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bg-terms-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, text);
        files.Add(path);
        return path;
    }

    static string[] Publish(string policy, string termsFile, params string[] more) =>
        ["insurance", "policy", "publish", "--policy-id", policy, "--terms-file", termsFile, "--operator", "ops", "--reason", "insurance test", .. more];
    static string[] Activate(long account, string policy, int seconds, Guid key, params string[] more) =>
        ["insurance", "activate", "--account-base58", AccountId.Format(account), "--policy-id", policy,
            "--duration-seconds", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--idempotency-key", key.ToString(),
            "--operator", "ops", "--reason", "insurance test", .. more];

    async Task<long> NewAccountAsync(string? displayName = null)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status, display_name) values ('active', @displayName) returning account_id", new { displayName });
    }

    Task<long> CountAsync(string table) => ScalarAsSuperuserAsync<long>(db, $"select count(*) from {table}");
    Task<Guid> LastCorrelationAsync() => ScalarAsSuperuserAsync<Guid>(db, "select correlation_id from operator_actions order by id desc limit 1");

    static DateTime Utc(string text) => DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    [Theory]
    [InlineData("publish")]
    [InlineData("publish-missing-file")]
    [InlineData("publish-bad-policy-id")]
    [InlineData("publish-conflict")]
    [InlineData("activate")]
    [InlineData("activate-replay")]
    [InlineData("activate-reused")]
    [InlineData("activate-cross-tier")]
    [InlineData("activate-unknown-policy")]
    [InlineData("activate-unknown-account")]
    [InlineData("activate-bad-duration")]
    public async Task Insurance_verbs_print_their_outputs_and_exit_codes(string scenario)
    {
        var account = await NewAccountAsync();
        var id = AccountId.Format(account);
        var key = Guid.CreateVersion7();
        var auditRows = await CountAsync("operator_actions");
        switch (scenario)
        {
            case "publish":
            {
                var (code, output, _) = await CliAsync(null, null, Publish("premium", TermsFile(Terms(80)), "--yes"));
                Assert.Equal(ExitCodes.Ok, code);
                Assert.Equal(["policyId", "policyVersion", "correlationId"], Without(Names(output), "target."));
                Assert.Equal(("premium", "1"), (Field(output, "policyId"), Field(output, "policyVersion")));
                Assert.Equal(await LastCorrelationAsync(), Guid.Parse(Field(output, "correlationId")));
                Assert.Equal("2", Field((await CliAsync(null, null, Publish("premium", TermsFile(Terms(90)), "--yes"))).Out, "policyVersion"));
                Assert.Equal(auditRows + 2, await CountAsync("operator_actions"));
                break;
            }
            case "publish-missing-file":
            {
                var (code, output, error) = await CliAsync(null, null, Publish("premium", Path.Combine(Path.GetTempPath(), $"bg-missing-{Guid.NewGuid():N}.json"), "--yes"));
                Assert.Equal((ExitCodes.Usage, ""), (code, output));
                Assert.Contains("terms file", error);
                break;
            }
            case "publish-bad-policy-id":
            {
                foreach (var policy in new[] { "Premium", "a_b", new string('a', 65) })
                {
                    var (code, output, error) = await CliAsync(null, null, Publish(policy, TermsFile(Terms()), "--yes"));
                    Assert.Equal((ExitCodes.Usage, ""), (code, output));
                    Assert.Contains("policy id", error);
                }
                break;
            }
            case "publish-conflict":
            {
                await ExecuteAsSuperuserAsync(db,
                    """
                    create function fake_policy_duplicate() returns trigger language plpgsql as $$ begin
                      raise exception 'duplicate on purpose' using errcode = 'unique_violation', constraint = 'insurance_policies_pkey'; end $$;
                    create trigger fake_policy_duplicate before insert on insurance_policies for each row execute function fake_policy_duplicate();
                    """);
                var (code, output, error) = await CliAsync(null, null, Publish("premium", TermsFile(Terms()), "--yes"));
                await ExecuteAsSuperuserAsync(db, "drop trigger fake_policy_duplicate on insurance_policies; drop function fake_policy_duplicate()");
                Assert.Equal(ExitCodes.Conflict, code);
                Assert.Contains("retry", error);
                Assert.DoesNotContain("correlationId=", output);
                break;
            }
            case "activate" or "activate-replay":
            {
                var (code, output, _) = await CliAsync(null, null, Activate(account, "standard", 86_400, key, "--yes"));
                Assert.Equal(ExitCodes.Ok, code);
                Assert.Equal(["accountId", "policyId", "policyVersion", "startsAt", "expiresAt", "replayed", "correlationId"], Without(Names(output), "target."));
                Assert.Equal((id, "standard", "1", "false"), (Field(output, "accountId"), Field(output, "policyId"), Field(output, "policyVersion"), Field(output, "replayed")));
                Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{6}Z$", Field(output, "startsAt"));
                Assert.Equal(Utc(Field(output, "startsAt")).AddDays(1), Utc(Field(output, "expiresAt")));
                Assert.Equal(await ScalarAsSuperuserAsync<DateTime>(db, "select expires_at from insurance_coverages where account_id = @account", new { account }), Utc(Field(output, "expiresAt")));
                Assert.Equal(await LastCorrelationAsync(), Guid.Parse(Field(output, "correlationId")));
                if (scenario == "activate") break;
                auditRows = await CountAsync("operator_actions");
                var replay = await CliAsync(null, null, Activate(account, "standard", 86_400, key, "--yes"));
                Assert.Equal(ExitCodes.Ok, replay.Code);
                Assert.Equal($"standard v1 until {Field(output, "expiresAt")}", Field(replay.Out, "target.coverage"));
                Assert.Equal((Field(output, "startsAt"), Field(output, "expiresAt"), "true"), (Field(replay.Out, "startsAt"), Field(replay.Out, "expiresAt"), Field(replay.Out, "replayed")));
                Assert.Equal(1L, await CountAsync("insurance_activations"));
                Assert.Equal(auditRows, await CountAsync("operator_actions")); // a replay writes no audit row
                break;
            }
            case "activate-reused":
            {
                Assert.Equal(ExitCodes.Ok, (await CliAsync(null, null, Activate(account, "standard", 86_400, key, "--yes"))).Code);
                auditRows = await CountAsync("operator_actions");
                var (code, output, error) = await CliAsync(null, null, Activate(account, "standard", 3_600, key, "--yes"));
                Assert.Equal(ExitCodes.Conflict, code);
                Assert.Contains("idempotency key", error);
                Assert.DoesNotContain("correlationId=", output);
                Assert.Equal(1L, await CountAsync("insurance_activations"));
                break;
            }
            case "activate-cross-tier":
            {
                Assert.Equal(ExitCodes.Ok, (await CliAsync(null, null, Publish("premium", TermsFile(Terms(80)), "--yes"))).Code);
                Assert.Equal(ExitCodes.Ok, (await CliAsync(null, null, Activate(account, "standard", 86_400, key, "--yes"))).Code);
                auditRows = await CountAsync("operator_actions");
                var (code, output, error) = await CliAsync(null, null, Activate(account, "premium", 86_400, Guid.CreateVersion7(), "--yes"));
                Assert.Equal(ExitCodes.Conflict, code);
                Assert.StartsWith("standard v1 until ", Field(output, "target.coverage"));
                Assert.Contains("standard", error);
                Assert.DoesNotContain("correlationId=", output);
                Assert.Equal(1L, await CountAsync("insurance_activations"));
                break;
            }
            case "activate-unknown-policy":
            {
                var (code, output, error) = await CliAsync(null, null, Activate(account, "never-published", 86_400, key, "--yes"));
                Assert.Equal(ExitCodes.NotFound, code);
                Assert.Equal("(none)", Field(output, "target.coverage"));
                Assert.Contains("never-published", error);
                Assert.DoesNotContain("correlationId=", output);
                break;
            }
            case "activate-unknown-account":
            {
                var (code, output, _) = await CliAsync(null, null, Activate(account + 1000, "standard", 86_400, key, "--yes"));
                Assert.Equal((ExitCodes.NotFound, ""), (code, output));
                break;
            }
            case "activate-bad-duration":
            {
                foreach (var seconds in new[] { 0, -5, 315_360_001 })
                {
                    var (code, output, error) = await CliAsync(null, null, Activate(account, "standard", seconds, key, "--yes"));
                    Assert.Equal((ExitCodes.Usage, ""), (code, output)); // before the target is even described
                    Assert.Contains("315360000", error);
                }
                break;
            }
        }
        if (scenario is not ("publish" or "activate" or "activate-replay"))
            Assert.Equal(auditRows, await CountAsync("operator_actions"));
        if (scenario is not ("publish" or "activate-cross-tier"))
            Assert.Equal(1L, await CountAsync("insurance_policies"));
    }

    [Fact]
    public async Task An_invalid_terms_file_is_refused_before_any_connection()
    {
        // The database is unreachable: the refusal (64, not 69) proves the terms were checked before any connection.
        var unreachable = db.EnvFor("app");
        unreachable["BLACKGLASS_DB_PORT"] = "1";
        var invalid = new[]
        {
            "not json", Terms().Replace("\"floor\"", "\"ceil\""), Terms().Replace("\"percent\": 50", "\"percent\": 101"),
            Terms().Replace("\"progressXp\"", "\"extra\": 1, \"progressXp\""), "",
        };
        foreach (var text in invalid)
        {
            var (code, output, error) = await CliAsync(null, unreachable, Publish("premium", TermsFile(text), "--yes"));
            Assert.Equal((ExitCodes.Usage, ""), (code, output));
            Assert.Contains("Nothing was published", error);
            Assert.DoesNotContain("unavailable", error);
        }
        // The same valid file gets as far as the connection (69): the check is the terms, not the file.
        Assert.Equal(ExitCodes.Unavailable, (await CliAsync(null, unreachable, Publish("premium", TermsFile(Terms()), "--yes"))).Code);
        Assert.Equal(1L, await CountAsync("insurance_policies"));
        Assert.Equal(0L, await CountAsync("operator_actions"));
    }

    [Fact]
    public async Task Insurance_publish_and_activate_need_confirmation()
    {
        var account = await NewAccountAsync("Insured One");
        var id = AccountId.Format(account);
        var key = Guid.CreateVersion7();
        var terms = TermsFile(Terms(70));

        // No terminal, no --yes: the target is shown, nothing changes (64).
        var publishPreview = await CliAsync(null, null, Publish("standard", terms));
        Assert.Equal(ExitCodes.Usage, publishPreview.Code);
        Assert.Equal(["target.policyId", "target.policyCurrentVersion", "target.policyCurrentTerms", "target.policyProposedVersion", "target.policyProposedTerms"],
            Names(publishPreview.Out));
        Assert.Equal(("standard", "1", "2"), (Field(publishPreview.Out, "target.policyId"), Field(publishPreview.Out, "target.policyCurrentVersion"),
            Field(publishPreview.Out, "target.policyProposedVersion")));
        Assert.Equal(InsuranceTerms.Parse(Terms(50)).Canonical(), Field(publishPreview.Out, "target.policyCurrentTerms"));
        Assert.Equal(InsuranceTerms.Parse(Terms(70)).Canonical(), Field(publishPreview.Out, "target.policyProposedTerms"));
        Assert.Contains("publish", publishPreview.Err);
        Assert.Contains("nothing was changed", publishPreview.Err);
        var newPolicyPreview = await CliAsync(null, null, Publish("premium", terms));
        Assert.Equal(("(none)", "(none)", "1"), (Field(newPolicyPreview.Out, "target.policyCurrentVersion"), Field(newPolicyPreview.Out, "target.policyCurrentTerms"),
            Field(newPolicyPreview.Out, "target.policyProposedVersion")));

        var activatePreview = await CliAsync(null, null, Activate(account, "standard", 3_600, key));
        Assert.Equal(ExitCodes.Usage, activatePreview.Code);
        Assert.Equal((id, "Insured One", "(none)"), (Field(activatePreview.Out, "target.accountId"), Field(activatePreview.Out, "target.displayName"), Field(activatePreview.Out, "target.coverage")));
        Assert.DoesNotContain("target.energyBalance", activatePreview.Out); // insurance touches no energy table
        Assert.Contains("--yes", activatePreview.Err);

        // A wrong answer at the terminal: 67, nothing changes.
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync("yes\n", null, Publish("standard", terms))).Code);
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync("publish\n", null, Activate(account, "standard", 3_600, key))).Code);
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync("", null, Activate(account, "standard", 3_600, key))).Code);
        Assert.Equal((1L, 0L, 0L), (await CountAsync("insurance_policies"), await CountAsync("insurance_activations"), await CountAsync("operator_actions")));

        // The right answer confirms.
        var published = await CliAsync("publish\n", null, Publish("standard", terms));
        Assert.Equal((ExitCodes.Ok, "2"), (published.Code, Field(published.Out, "policyVersion")));
        Assert.Contains("Type publish", published.Err);
        var activated = await CliAsync(id + "\n", null, Activate(account, "standard", 3_600, key));
        Assert.Equal((ExitCodes.Ok, "2", "false"), (activated.Code, Field(activated.Out, "policyVersion"), Field(activated.Out, "replayed")));
        Assert.Contains($"Type the Base58 account id {id}", activated.Err);
        Assert.Equal(2L, await CountAsync("operator_actions"));
    }

    [Fact]
    public async Task Insurance_verbs_never_load_the_catalogue()
    {
        var account = await NewAccountAsync();

        Assert.Equal(ExitCodes.Ok, (await CliAsync(null, null, Publish("premium", TermsFile(Terms()), "--yes"))).Code);
        Assert.Equal(ExitCodes.Ok, (await CliAsync(null, null, Activate(account, "premium", 60, Guid.CreateVersion7(), "--yes"))).Code);
        Assert.Equal(ExitCodes.Usage, (await CliAsync(null, null, Activate(account, "premium", 60, Guid.CreateVersion7()))).Code); // a preview too
        Assert.Equal(ExitCodes.Usage, (await CliAsync(null, null, Publish("premium", TermsFile(Terms())))).Code);
        Assert.Equal(0, loads);

        // An invalid energy setting doesn't block them either: they read no energy configuration.
        var env = db.EnvFor("app");
        env["BLACKGLASS_ENERGY_GRANT_MAX"] = "not-a-number";
        Assert.Equal(ExitCodes.Ok, (await CliAsync(null, env, Activate(account, "premium", 60, Guid.CreateVersion7(), "--yes"))).Code);
        Assert.Equal(0L, await CountAsync("energy_ledger"));
    }

    [Fact]
    public async Task An_unknown_activation_outcome_tells_the_operator_to_retry_with_the_same_key()
    {
        var account = await NewAccountAsync();
        var key = Guid.CreateVersion7();
        var guidance = $"The activation may or may not have committed: retry with the same --idempotency-key {key:D}, never a new one.";

        // Unexpected (exit 1): a failure the CLI cannot classify, here a CHECK violation (an internal error, never retried).
        await ExecuteAsSuperuserAsync(db,
            """
            create function fake_check() returns trigger language plpgsql as $$ begin
              raise exception 'check on purpose' using errcode = 'check_violation', constraint = 'insurance_activation_extends'; end $$;
            create trigger fake_check before insert on insurance_activations for each row execute function fake_check();
            """);
        var unexpected = await CliAsync(null, null, Activate(account, "standard", 60, key, "--yes"));
        await ExecuteAsSuperuserAsync(db, "drop trigger fake_check on insurance_activations; drop function fake_check()");
        Assert.Equal(ExitCodes.Failure, unexpected.Code);
        Assert.Contains("Unexpected database error", unexpected.Err);
        Assert.Contains(guidance, unexpected.Err);
        Assert.DoesNotContain("correlationId=", unexpected.Out);
        Assert.Equal(0L, await CountAsync("insurance_activations"));

        // Unavailable (exit 69): the connection fails.
        var unreachable = db.EnvFor("app");
        unreachable["BLACKGLASS_DB_PORT"] = "1";
        var unavailable = await CliAsync(null, unreachable, Activate(account, "standard", 60, key, "--yes"));
        Assert.Equal(ExitCodes.Unavailable, unavailable.Code);
        Assert.Contains(guidance, unavailable.Err);
        // Only an activation gets the guidance: a publication has no idempotency key.
        var publishUnavailable = await CliAsync(null, unreachable, Publish("premium", TermsFile(Terms()), "--yes"));
        Assert.Equal(ExitCodes.Unavailable, publishUnavailable.Code);
        Assert.DoesNotContain("may or may not", publishUnavailable.Err);

        // And the operator's retry with the same key activates once.
        var retry = await CliAsync(null, null, Activate(account, "standard", 60, key, "--yes"));
        Assert.Equal((ExitCodes.Ok, "false"), (retry.Code, Field(retry.Out, "replayed")));
        Assert.Equal(1L, await CountAsync("insurance_activations"));
    }
}
