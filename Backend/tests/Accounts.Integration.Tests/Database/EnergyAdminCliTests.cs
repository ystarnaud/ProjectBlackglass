using System.Text.RegularExpressions;
using Dapper;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Api.Admin;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The admin CLI's energy verbs: <c>energy open</c>, <c>energy grant</c> and <c>energy policy publish</c> print their
/// outputs and exit codes, show their target and act only when confirmed (the Base58 account id for the account verbs,
/// <c>publish</c> for a publication, or --yes), and never load the game catalogue. <c>energy reconcile</c> prints its counts
/// here; EnergyReconcilerTests covers its findings.
/// </summary>
[Trait("Requires", "Postgres")]
public class EnergyAdminCliTests : IAsyncLifetime
{
    TestDatabase db = null!;
    AccountAdminStore store = null!;
    int loads;

    public async Task InitializeAsync() { db = await MigratedAsync(); store = new AccountAdminStore(db.AsApp()); }

    public async Task DisposeAsync()
    {
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

    static string[] Open(long account, params string[] more) =>
        ["energy", "open", "--account-base58", AccountId.Format(account), "--operator", "ops", "--reason", "energy test", .. more];
    static string[] Grant(long account, int amount, Guid key, params string[] more) =>
        ["energy", "grant", "--account-base58", AccountId.Format(account), "--amount", amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--idempotency-key", key.ToString(), "--operator", "ops", "--reason", "energy test", .. more];
    static string[] Publish(int cap, int opening, int seconds, int amount, int refund, params string[] more) =>
        ["energy", "policy", "publish", "--cap", $"{cap}", "--opening-balance", $"{opening}", "--interval-seconds", $"{seconds}", "--amount", $"{amount}",
            "--void-refund-pct", $"{refund}", "--operator", "ops", "--reason", "energy test", .. more];

    async Task<long> BareAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    Task<long> CountAsync(string table) => ScalarAsSuperuserAsync<long>(db, $"select count(*) from {table}");

    Task<Guid> LastCorrelationAsync() => ScalarAsSuperuserAsync<Guid>(db, "select correlation_id from operator_actions order by id desc limit 1");

    [Theory]
    [InlineData("open-new")]
    [InlineData("open-existing")]
    [InlineData("grant")]
    [InlineData("grant-replay")]
    [InlineData("grant-reused")]
    [InlineData("grant-no-wallet")]
    [InlineData("grant-unknown-account")]
    [InlineData("grant-above-max")]
    [InlineData("grant-overflow")]
    [InlineData("publish")]
    [InlineData("publish-invalid")]
    [InlineData("reconcile")]
    public async Task Energy_verbs_print_their_outputs_and_exit_codes(string scenario)
    {
        var account = (await store.CreateAccountAsync(null, null, Op)).AccountId;
        var id = AccountId.Format(account);
        var key = Guid.CreateVersion7();
        var auditRows = await CountAsync("operator_actions");
        switch (scenario)
        {
            case "open-new":
            {
                var bare = await BareAccountAsync();
                var (code, output, _) = await CliAsync(null, null, Open(bare, "--yes"));
                Assert.Equal(ExitCodes.Ok, code);
                Assert.Equal(["accountId", "energyOpened", "balance", "policyVersion", "correlationId"], Without(Names(output), "target."));
                Assert.Equal((AccountId.Format(bare), "opened", "50", "1"), (Field(output, "accountId"), Field(output, "energyOpened"), Field(output, "balance"), Field(output, "policyVersion")));
                Assert.Equal(await LastCorrelationAsync(), Guid.Parse(Field(output, "correlationId")));
                break;
            }
            case "open-existing":
            {
                var (code, output, _) = await CliAsync(null, null, Open(account, "--yes"));
                Assert.Equal(ExitCodes.Ok, code);
                Assert.Equal(["accountId", "energyOpened", "balance", "policyVersion", "advisory", "correlationId"], Without(Names(output), "target."));
                Assert.Equal((id, "already", "50", "1", "true"),
                    (Field(output, "accountId"), Field(output, "energyOpened"), Field(output, "balance"), Field(output, "policyVersion"), Field(output, "advisory")));
                Assert.Equal(1L, await CountAsync("energy_ledger"));
                break;
            }
            case "grant" or "grant-replay":
            {
                var (code, output, _) = await CliAsync(null, null, Grant(account, 7, key, "--yes"));
                Assert.Equal(ExitCodes.Ok, code);
                Assert.Equal(["accountId", "amount", "balanceAfter", "regenerated", "replayed", "correlationId"], Without(Names(output), "target."));
                Assert.Equal((id, "7", "57", "0", "false"), (Field(output, "accountId"), Field(output, "amount"), Field(output, "balanceAfter"), Field(output, "regenerated"), Field(output, "replayed")));
                Assert.Equal(await LastCorrelationAsync(), Guid.Parse(Field(output, "correlationId")));
                if (scenario == "grant") break;
                var replay = await CliAsync(null, null, Grant(account, 7, key, "--yes"));
                Assert.Equal(ExitCodes.Ok, replay.Code);
                Assert.Equal("57", Field(replay.Out, "target.energyBalance"));
                Assert.Equal(("7", "57", "0", "true"), (Field(replay.Out, "amount"), Field(replay.Out, "balanceAfter"), Field(replay.Out, "regenerated"), Field(replay.Out, "replayed")));
                Assert.Equal(2L, await CountAsync("energy_ledger"));
                break;
            }
            case "grant-reused":
            {
                Assert.Equal(ExitCodes.Ok, (await CliAsync(null, null, Grant(account, 7, key, "--yes"))).Code);
                var other = (await store.CreateAccountAsync(null, null, Op)).AccountId;
                auditRows = await CountAsync("operator_actions");
                foreach (var reuse in new[] { Grant(account, 8, key, "--yes"), Grant(other, 7, key, "--yes") })
                {
                    var (code, output, error) = await CliAsync(null, null, reuse);
                    Assert.Equal(ExitCodes.Conflict, code);
                    Assert.Contains("idempotency key", error);
                    Assert.DoesNotContain("correlationId=", output);
                }
                Assert.Equal(3L, await CountAsync("energy_ledger")); // two opens and the one grant
                break;
            }
            case "grant-no-wallet":
            {
                var bare = await BareAccountAsync();
                var (code, output, error) = await CliAsync(null, null, Grant(bare, 7, key, "--yes"));
                Assert.Equal(ExitCodes.NotFound, code);
                Assert.Equal("(no wallet)", Field(output, "target.energyBalance"));
                Assert.Contains("energy open", error);
                Assert.DoesNotContain("correlationId=", output);
                break;
            }
            case "grant-unknown-account":
            {
                var (code, output, _) = await CliAsync(null, null, Grant(account + 1000, 7, key, "--yes"));
                Assert.Equal((ExitCodes.NotFound, ""), (code, output));
                break;
            }
            case "grant-above-max":
            {
                var env = db.EnvFor("app");
                env["BLACKGLASS_ENERGY_GRANT_MAX"] = "10";
                Assert.Equal(ExitCodes.Ok, (await CliAsync(null, env, Grant(account, 10, Guid.CreateVersion7(), "--yes"))).Code);
                Assert.Equal(ExitCodes.Ok, (await CliAsync(null, null, Grant(account, 1000, Guid.CreateVersion7(), "--yes"))).Code); // the default maximum
                auditRows = await CountAsync("operator_actions");
                foreach (var (amount, max, settings) in new[] { (11, "1 to 10", env), (0, "1 to 10", env), (-1, "1 to 10", env), (1001, "1 to 1000", null) })
                {
                    var (code, output, error) = await CliAsync(null, settings, Grant(account, amount, Guid.CreateVersion7(), "--yes"));
                    Assert.Equal((ExitCodes.Usage, ""), (code, output)); // refused before the target is even described
                    Assert.Contains(max, error);
                }
                break;
            }
            case "grant-overflow":
            {
                long rich;
                await using (var conn = await db.AsApp().OpenConnectionAsync())
                await using (var tx = await conn.BeginTransactionAsync())
                {
                    // A wallet five below the largest balance, opened as the app would (the wallet and its open entry together).
                    rich = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id", transaction: tx);
                    await conn.ExecuteAsync("insert into energy_wallets (account_id, balance, regen_anchor, regen_progress_us, policy_version) values (@rich, @balance, clock_timestamp(), 0, 1)",
                        new { rich, balance = int.MaxValue - 5 }, tx);
                    await conn.ExecuteAsync("insert into energy_ledger (account_id, kind, delta, balance_after, idempotency_key) values (@rich, 'open', @balance, @balance, 'open')",
                        new { rich, balance = int.MaxValue - 5 }, tx);
                    await tx.CommitAsync();
                }
                var (code, output, error) = await CliAsync(null, null, Grant(rich, 6, key, "--yes"));
                Assert.Equal(ExitCodes.Usage, code);
                Assert.Contains("2147483647", error);
                Assert.Equal((int.MaxValue - 5).ToString(System.Globalization.CultureInfo.InvariantCulture), Field(output, "target.energyBalance"));
                Assert.DoesNotContain("correlationId=", output);
                Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from energy_ledger where account_id = @rich", new { rich }));
                break;
            }
            case "publish":
            {
                var (code, output, _) = await CliAsync(null, null, Publish(80, 40, 300, 2, 50, "--yes"));
                Assert.Equal(ExitCodes.Ok, code);
                Assert.Equal(["policyVersion", "effectiveAt", "cap", "openingBalance", "intervalSeconds", "amount", "voidRefundPct", "correlationId"], Without(Names(output), "target."));
                Assert.Equal(("2", "80", "40", "300", "2", "50"), (Field(output, "policyVersion"), Field(output, "cap"), Field(output, "openingBalance"), Field(output, "intervalSeconds"),
                    Field(output, "amount"), Field(output, "voidRefundPct")));
                Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{6}Z$", Field(output, "effectiveAt"));
                Assert.Equal(await ScalarAsSuperuserAsync<DateTime>(db, "select effective_at from energy_policies where version = 2"),
                    DateTime.Parse(Field(output, "effectiveAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal));
                Assert.Equal(await LastCorrelationAsync(), Guid.Parse(Field(output, "correlationId")));
                break;
            }
            case "publish-invalid":
            {
                foreach (var args in new[] { Publish(0, 0, 600, 1, 100, "--yes"), Publish(50, 51, 600, 1, 100, "--yes"), Publish(50, 50, 604_801, 1, 100, "--yes"),
                             Publish(50, 50, 0, 1, 100, "--yes"), Publish(50, 50, 600, 0, 100, "--yes"), Publish(50, 50, 600, 1, 101, "--yes") })
                {
                    var (code, output, error) = await CliAsync(null, null, args);
                    Assert.Equal((ExitCodes.Usage, ""), (code, output)); // refused before the current policy is even shown
                    Assert.NotEqual("", error);
                }
                Assert.Equal(1L, await CountAsync("energy_policies"));
                break;
            }
            case "reconcile":
            {
                // A consistent database: the one wallet is checked, nothing is found, and nothing is audited (it changes no account).
                var (code, output, _) = await CliAsync(null, null, "energy", "reconcile", "--operator", "ops", "--reason", "energy test");
                Assert.Equal(ExitCodes.Ok, code);
                Assert.Equal(["walletsChecked", "mismatches", "anomaliesRecorded", "correlationId"], Names(output));
                Assert.Equal(("1", "0", "0"), (Field(output, "walletsChecked"), Field(output, "mismatches"), Field(output, "anomaliesRecorded")));
                Assert.True(Guid.TryParseExact(Field(output, "correlationId"), "D", out _));
                Assert.Equal(0L, await CountAsync("anomaly_events"));
                break;
            }
        }
        if (scenario is "grant-reused" or "grant-no-wallet" or "grant-unknown-account" or "grant-above-max" or "grant-overflow" or "publish-invalid" or "reconcile")
            Assert.Equal(auditRows, await CountAsync("operator_actions"));
    }

    [Fact]
    public async Task Energy_grant_open_and_publish_need_confirmation()
    {
        var issued = await store.CreateAccountAsync("energy-target", "Energy Target", Op);
        var account = issued.AccountId;
        var id = AccountId.Format(account);
        var bare = await BareAccountAsync();
        var bareId = AccountId.Format(bare);
        var key = Guid.CreateVersion7();
        var (ledger, audits) = (await CountAsync("energy_ledger"), await CountAsync("operator_actions"));

        // No terminal, no --yes: the target is shown, nothing changes (64).
        var grantPreview = await CliAsync(null, null, Grant(account, 5, key));
        Assert.Equal(ExitCodes.Usage, grantPreview.Code);
        Assert.Equal((id, "energy-target", "Energy Target", "50"),
            (Field(grantPreview.Out, "target.accountId"), Field(grantPreview.Out, "target.handle"), Field(grantPreview.Out, "target.displayName"), Field(grantPreview.Out, "target.energyBalance")));
        Assert.Contains("--yes", grantPreview.Err);
        Assert.Contains("nothing was changed", grantPreview.Err);
        var openPreview = await CliAsync(null, null, Open(bare));
        Assert.Equal(ExitCodes.Usage, openPreview.Code);
        Assert.Equal((bareId, "(no wallet)"), (Field(openPreview.Out, "target.accountId"), Field(openPreview.Out, "target.energyBalance")));
        var publishPreview = await CliAsync(null, null, Publish(80, 40, 300, 2, 50));
        Assert.Equal(ExitCodes.Usage, publishPreview.Code);
        Assert.Equal(
            ["target.policyCurrentVersion", "target.policyCurrentEffectiveAt", "target.policyCurrentCap", "target.policyCurrentOpeningBalance", "target.policyCurrentIntervalSeconds",
                "target.policyCurrentAmount", "target.policyCurrentVoidRefundPct", "target.policyProposedCap", "target.policyProposedOpeningBalance",
                "target.policyProposedIntervalSeconds", "target.policyProposedAmount", "target.policyProposedVoidRefundPct"],
            Names(publishPreview.Out));
        Assert.Equal(("1", "50", "50", "600", "1", "100"), (Field(publishPreview.Out, "target.policyCurrentVersion"), Field(publishPreview.Out, "target.policyCurrentCap"),
            Field(publishPreview.Out, "target.policyCurrentOpeningBalance"), Field(publishPreview.Out, "target.policyCurrentIntervalSeconds"),
            Field(publishPreview.Out, "target.policyCurrentAmount"), Field(publishPreview.Out, "target.policyCurrentVoidRefundPct")));
        Assert.Equal(("80", "40", "300", "2", "50"), (Field(publishPreview.Out, "target.policyProposedCap"), Field(publishPreview.Out, "target.policyProposedOpeningBalance"),
            Field(publishPreview.Out, "target.policyProposedIntervalSeconds"), Field(publishPreview.Out, "target.policyProposedAmount"), Field(publishPreview.Out, "target.policyProposedVoidRefundPct")));
        Assert.Contains("publish", publishPreview.Err);
        Assert.Contains("nothing was changed", publishPreview.Err);

        // A wrong answer at the terminal: 67, nothing changes. The account verbs want the Base58 id, a publication the word publish.
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync("publish\n", null, Grant(account, 5, key))).Code);
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync(id + "\n", null, Open(bare))).Code); // another account's id
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync("yes\n", null, Publish(80, 40, 300, 2, 50))).Code);
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync(" publish\n", null, Publish(80, 40, 300, 2, 50))).Code);
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync("", null, Publish(80, 40, 300, 2, 50))).Code); // end of input
        Assert.Equal((ledger, audits, 0L, 1L), (await CountAsync("energy_ledger"), await CountAsync("operator_actions"), await ScalarAsSuperuserAsync<long>(db, "select count(*) from energy_wallets where account_id = @bare", new { bare }),
            await CountAsync("energy_policies")));

        // The right answer confirms.
        var granted = await CliAsync(id + "\n", null, Grant(account, 5, key));
        Assert.Equal((ExitCodes.Ok, "55"), (granted.Code, Field(granted.Out, "balanceAfter")));
        Assert.Contains($"Type the Base58 account id {id}", granted.Err);
        var opened = await CliAsync(bareId + "\n", null, Open(bare));
        Assert.Equal((ExitCodes.Ok, "opened"), (opened.Code, Field(opened.Out, "energyOpened")));
        var published = await CliAsync("publish\n", null, Publish(80, 40, 300, 2, 50));
        Assert.Equal((ExitCodes.Ok, "2"), (published.Code, Field(published.Out, "policyVersion")));
        Assert.Contains("Type publish", published.Err);
        Assert.Equal(audits + 3, await CountAsync("operator_actions"));
    }

    [Fact]
    public async Task Energy_verbs_never_load_the_catalogue()
    {
        var account = (await store.CreateAccountAsync(null, null, Op)).AccountId;
        var bare = await BareAccountAsync();

        Assert.Equal(ExitCodes.Ok, (await CliAsync(null, null, Open(bare, "--yes"))).Code);
        Assert.Equal(ExitCodes.Ok, (await CliAsync(null, null, Grant(account, 5, Guid.CreateVersion7(), "--yes"))).Code);
        Assert.Equal(ExitCodes.Ok, (await CliAsync(null, null, Publish(60, 60, 600, 1, 100, "--yes"))).Code);
        Assert.Equal(ExitCodes.Ok, (await CliAsync(null, null, "energy", "reconcile", "--operator", "ops", "--reason", "x")).Code);
        Assert.Equal(ExitCodes.Usage, (await CliAsync(null, null, Grant(account, 5, Guid.CreateVersion7()))).Code); // a preview too
        Assert.Equal(0, loads);

        // The boundary itself: account creation still needs the catalogue and is refused by the broken one.
        Assert.Equal(ExitCodes.InvalidCatalogue, (await CliAsync(null, null, "account", "create", "--operator", "ops", "--reason", "x")).Code);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Grant_output_and_audit_carry_no_secret()
    {
        var issued = await store.CreateAccountAsync("secret-holder", null, Op);
        await new SessionStore(db.AsApp()).LoginAsync(issued.AccountKey.Hash(), 60);
        var outputs = new List<string>();
        foreach (var args in new[] { Grant(issued.AccountId, 5, Guid.CreateVersion7(), "--yes"), Grant(issued.AccountId, 5, Guid.CreateVersion7()), Open(issued.AccountId, "--yes"),
                     Publish(60, 60, 600, 1, 100, "--yes") })
        {
            var (_, output, error) = await CliAsync(null, null, args);
            outputs.Add(output + error);
        }
        string auditText;
        await using (var su = await db.AsSuperuser().OpenConnectionAsync())
            auditText = string.Join('\n', await su.QueryAsync<string>("select operator || ' ' || reason || ' ' || result::text from operator_actions where action like 'energy_%'"));
        var text = string.Join('\n', outputs) + '\n' + auditText;

        Assert.Contains("balanceAfter", text); // the capture works
        Assert.DoesNotContain(issued.AccountKey.Text[4..], text);
        Assert.DoesNotContain("dk1_", text);
        Assert.DoesNotContain("at1_", text);
        var hash = issued.AccountKey.Hash();
        Assert.DoesNotContain(Convert.ToHexStringLower(hash)[..9], text);
        Assert.DoesNotContain(Convert.ToHexString(hash)[..9], text);
        Assert.DoesNotContain(Convert.ToBase64String(hash)[..12], text);
        Assert.DoesNotContain(db.Password("app"), text);
    }

    [Fact]
    public async Task An_upper_case_key_replays_the_grant_made_with_its_lower_case_form()
    {
        var account = (await store.CreateAccountAsync(null, null, Op)).AccountId;
        var key = Guid.CreateVersion7();
        var first = await CliAsync(null, null, Grant(account, 5, key, "--yes"));
        Assert.Equal((ExitCodes.Ok, "false"), (first.Code, Field(first.Out, "replayed")));

        var upper = Grant(account, 5, key, "--yes");
        upper[Array.IndexOf(upper, "--idempotency-key") + 1] = key.ToString("D").ToUpperInvariant();
        Assert.NotEqual(key.ToString("D"), upper[Array.IndexOf(upper, "--idempotency-key") + 1]);
        var replay = await CliAsync(null, null, upper);

        Assert.Equal((ExitCodes.Ok, "true", "55"), (replay.Code, Field(replay.Out, "replayed"), Field(replay.Out, "balanceAfter")));
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        Assert.Equal(["dev_grant:" + key.ToString("D")], (await su.QueryAsync<string>("select idempotency_key from energy_ledger where kind = 'dev_grant'")).ToList());
    }

    [Fact]
    public async Task An_unknown_grant_outcome_tells_the_operator_to_retry_with_the_same_key()
    {
        var account = (await store.CreateAccountAsync(null, null, Op)).AccountId;
        var key = Guid.CreateVersion7();
        var guidance = $"The grant may or may not have committed: retry with the same --idempotency-key {key:D}, never a new one.";

        // Unexpected (exit 1): a failure the CLI cannot classify.
        await ExecuteAsSuperuserAsync(db,
            """
            create function fake_duplicate() returns trigger language plpgsql as $$ begin
              raise exception 'duplicate on purpose' using errcode = 'unique_violation', constraint = 'energy_ledger_account_id_idempotency_key_key'; end $$;
            create trigger fake_duplicate before insert on energy_ledger for each row when (new.kind = 'dev_grant') execute function fake_duplicate();
            """);
        var unexpected = await CliAsync(null, null, Grant(account, 5, key, "--yes"));
        await ExecuteAsSuperuserAsync(db, "drop trigger fake_duplicate on energy_ledger; drop function fake_duplicate()");
        Assert.Equal(ExitCodes.Failure, unexpected.Code);
        Assert.Contains("Unexpected database error", unexpected.Err);
        Assert.Contains(guidance, unexpected.Err);
        Assert.DoesNotContain("correlationId=", unexpected.Out);
        Assert.Equal(1L, await CountAsync("energy_ledger")); // the open entry only

        // Unavailable (exit 69): the connection fails.
        var unreachable = db.EnvFor("app");
        unreachable["BLACKGLASS_DB_PORT"] = "1";
        var unavailable = await CliAsync(null, unreachable, Grant(account, 5, key, "--yes"));
        Assert.Equal(ExitCodes.Unavailable, unavailable.Code);
        Assert.Contains(guidance, unavailable.Err);
        // Only a grant gets the guidance: an open has no idempotency key to retry with.
        var openUnavailable = await CliAsync(null, unreachable, Open(account, "--yes"));
        Assert.Equal(ExitCodes.Unavailable, openUnavailable.Code);
        Assert.DoesNotContain("may or may not", openUnavailable.Err);

        // And the operator's retry with the same key grants once.
        var retry = await CliAsync(null, null, Grant(account, 5, key, "--yes"));
        Assert.Equal((ExitCodes.Ok, "false", "55"), (retry.Code, Field(retry.Out, "replayed"), Field(retry.Out, "balanceAfter")));
    }

    [Fact]
    public async Task An_invalid_energy_setting_refuses_the_energy_verbs_only()
    {
        var account = (await store.CreateAccountAsync(null, null, Op)).AccountId;
        var env = db.EnvFor("app");
        env["BLACKGLASS_ENERGY_GRANT_MAX"] = "lots-value-xyz";
        var auditRows = await CountAsync("operator_actions");

        foreach (var args in new[] { Grant(account, 5, Guid.CreateVersion7(), "--yes"), Open(account, "--yes"), Publish(60, 60, 600, 1, 100, "--yes") })
        {
            var (code, output, error) = await CliAsync(null, env, args);
            Assert.Equal((ExitCodes.Failure, ""), (code, output));
            Assert.Contains("BLACKGLASS_ENERGY_GRANT_MAX", error);
            Assert.DoesNotContain("lots-value-xyz", error);
        }
        Assert.Equal(auditRows, await CountAsync("operator_actions"));
        // Account-security administration never depends on an energy setting.
        Assert.Equal(ExitCodes.Ok, (await CliAsync(null, env, "account", "suspend", "--account-base58", AccountId.Format(account), "--operator", "ops", "--reason", "x", "--yes")).Code);
    }
}
