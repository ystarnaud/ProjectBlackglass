using System.Text.RegularExpressions;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Api.Admin;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Migrations;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// Account-security administration never depends on the game catalogue (owner correction, 2026-10-15): with the embedded
/// catalogue invalid or missing, an operator can still revoke a credential, revoke sessions, suspend, reactivate and rotate,
/// under the same schema check, confirmation, locks and audit as ever, and the catalogue is never even loaded. What needs
/// game definitions (account creation with its starter, the starter backfill) still refuses with exit 4 and writes nothing.
/// The broken catalogue is injected as the CLI's loader: the embedded one in a test build is valid, so a code path that
/// reached for GameCatalogue.Embedded directly would not be caught here.
/// </summary>
[Trait("Requires", "Postgres")]
public class AdminCatalogueBoundaryTests : IAsyncLifetime
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

    /// <summary>A catalogue loader that counts its calls and fails as a broken build would: an embedded file that doesn't
    /// validate, or no embedded file at all (what <see cref="GameCatalogue.EmbeddedBytes"/> throws).</summary>
    Func<GameCatalogue> Broken(string how) => () =>
    {
        loads++;
        return how switch
        {
            "invalid" => GameCatalogue.Load("{}\n"u8.ToArray()),
            "missing" => throw new InvalidOperationException("The embedded catalogue.json resource is missing."),
            _ => throw new ArgumentOutOfRangeException(nameof(how)),
        };
    };

    Task<(int Code, string Out, string Err)> CliAsync(string how, string? typed, params string[] args) => CliOnAsync(db, how, typed, args);

    async Task<(int Code, string Out, string Err)> CliOnAsync(TestDatabase target, string how, string? typed, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await AdminCommand.RunAsync(args, target.EnvFor("app"), output, error, default,
            input: typed is null ? null : new StringReader(typed), loadCatalogue: Broken(how));
        return (code, output.ToString(), error.ToString());
    }

    static string Field(string output, string name) => Regex.Match(output, $"(?m)^{Regex.Escape(name)}=(.*)$").Groups[1].Value.TrimEnd('\r');

    static string[] With(string[] command, params string[] more) => [.. command, "--operator", "ops", "--reason", "test", .. more];

    static string[] Command(string verb, IssuedCredential account) => verb switch
    {
        "rotate" => ["credential", "rotate", "--account-base58", AccountId.Format(account.AccountId)],
        "revoke-credential" => ["credential", "revoke", "--credential", account.CredentialId.ToString()],
        "revoke-sessions" => ["sessions", "revoke", "--account-base58", AccountId.Format(account.AccountId)],
        "suspend" => ["account", "suspend", "--account-base58", AccountId.Format(account.AccountId)],
        "reactivate" => ["account", "reactivate", "--account-base58", AccountId.Format(account.AccountId)],
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    Task<long> AuditRowsAsync() => ScalarAsSuperuserAsync<long>(db, "select count(*) from operator_actions");
    Task<long> LiveSessionsAsync(long account) => ScalarAsSuperuserAsync<long>(db, "select count(*) from auth_sessions where account_id = @account and revoked_at is null", new { account });
    Task<string> StatusAsync(long account) => ScalarAsSuperuserAsync<string>(db, "select status from accounts where account_id = @account", new { account });
    Task<bool> CredentialLiveAsync(Guid credential) => ScalarAsSuperuserAsync<bool>(db, "select revoked_at is null from dev_credentials where credential_id = @credential", new { credential });

    async Task<IssuedCredential> AccountWithASessionAsync()
    {
        var account = await store.CreateAccountAsync("security-op", null, Op);
        await new SessionStore(db.AsApp()).LoginAsync(account.AccountKey.Hash(), 60);
        return account;
    }

    [Theory]
    [InlineData("rotate", "invalid")]
    [InlineData("rotate", "missing")]
    [InlineData("revoke-credential", "invalid")]
    [InlineData("revoke-credential", "missing")]
    [InlineData("revoke-sessions", "invalid")]
    [InlineData("revoke-sessions", "missing")]
    [InlineData("suspend", "invalid")]
    [InlineData("suspend", "missing")]
    [InlineData("reactivate", "invalid")]
    [InlineData("reactivate", "missing")]
    public async Task Security_administration_works_with_a_broken_catalogue_and_never_loads_it(string verb, string how)
    {
        var account = await AccountWithASessionAsync();
        if (verb == "reactivate") await store.SuspendAsync(account.AccountId, Op);
        var auditRows = await AuditRowsAsync();

        var (code, output, error) = await CliAsync(how, null, With(Command(verb, account), "--yes"));

        Assert.True(code == ExitCodes.Ok, error);
        Assert.Equal(0, loads);
        Assert.Equal(auditRows + 1, await AuditRowsAsync()); // the change and its audit row, together
        Assert.Equal(AccountId.Format(account.AccountId), Field(output, "target.accountId"));
        Assert.Equal(await ScalarAsSuperuserAsync<Guid>(db, "select correlation_id from operator_actions order by id desc limit 1"), Guid.Parse(Field(output, "correlationId")));
        Assert.DoesNotContain(account.AccountKey.Text, output + error);
        switch (verb)
        {
            case "rotate":
                Assert.False(await CredentialLiveAsync(account.CredentialId));
                Assert.Equal(0L, await LiveSessionsAsync(account.AccountId));
                Assert.Single(Regex.Matches(output, "(?m)^accountKey=dk1_[A-Za-z0-9_-]{43}\r?$")); // the new key, once, to the operator
                Assert.DoesNotContain("dk1_", error);
                Assert.True(await CredentialLiveAsync(Guid.Parse(Field(output, "credentialId"))));
                break;
            case "revoke-credential":
                Assert.False(await CredentialLiveAsync(account.CredentialId));
                Assert.Equal(0L, await LiveSessionsAsync(account.AccountId));
                break;
            case "revoke-sessions":
                Assert.Equal(0L, await LiveSessionsAsync(account.AccountId));
                Assert.True(await CredentialLiveAsync(account.CredentialId));
                break;
            case "suspend":
                Assert.Equal("suspended", await StatusAsync(account.AccountId));
                Assert.Equal(0L, await LiveSessionsAsync(account.AccountId));
                break;
            case "reactivate":
                Assert.Equal("active", await StatusAsync(account.AccountId));
                break;
        }
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("missing")]
    public async Task With_a_broken_catalogue_security_administration_still_needs_confirmation(string how)
    {
        var account = await AccountWithASessionAsync();
        var auditRows = await AuditRowsAsync();

        Assert.Equal(ExitCodes.Usage, (await CliAsync(how, null, With(Command("suspend", account)))).Code);
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync(how, "not-the-id", With(Command("suspend", account)))).Code);

        Assert.Equal(0, loads);
        Assert.Equal(auditRows, await AuditRowsAsync());
        Assert.Equal("active", await StatusAsync(account.AccountId));
        Assert.Equal(1L, await LiveSessionsAsync(account.AccountId));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("missing")]
    public async Task With_a_broken_catalogue_security_administration_still_refuses_a_schema_that_is_not_current(string how)
    {
        var account = await AccountWithASessionAsync();
        await ExecuteAsSuperuserAsync(db, "insert into schema_migrations (version, name, checksum) values (@v, 'from_the_future', decode(repeat('00', 32), 'hex'))",
            new { v = MigrationCatalog.Embedded.LatestVersion + 1 });
        var auditRows = await AuditRowsAsync();

        try
        {
            var (code, output, _) = await CliAsync(how, null, With(Command("revoke-sessions", account), "--yes"));

            Assert.Equal(ExitCodes.SchemaNotCurrent, code);
            Assert.Equal("", output);
            Assert.Equal(auditRows, await AuditRowsAsync());
            Assert.Equal(1L, await LiveSessionsAsync(account.AccountId));
        }
        finally { await ExecuteAsSuperuserAsync(db, "delete from schema_migrations where name = 'from_the_future'"); } // for the invariant check
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("missing")]
    public async Task Account_creation_with_a_broken_catalogue_is_refused_and_creates_nothing(string how)
    {
        var (code, output, error) = await CliAsync(how, null, With(["account", "create", "--handle", "never"]));

        Assert.Equal(ExitCodes.InvalidCatalogue, code);
        Assert.Equal(1, loads);
        Assert.Equal("", output);
        Assert.StartsWith("Refusing to run: ", error);
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from accounts"));
        Assert.Equal(0L, await AuditRowsAsync());
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("missing")]
    public async Task The_starter_backfill_with_a_broken_catalogue_is_refused_and_grants_nothing(string how)
    {
        await using var old = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(old.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync(2);
        var account = await PreStage4AccountAsync(old, "active"); // unprovisioned, at the latest schema

        var (code, output, error) = await CliOnAsync(old, how, null, With(["starter", "backfill", "--account-base58", AccountId.Format(account)], "--yes"));

        Assert.Equal(ExitCodes.InvalidCatalogue, code);
        Assert.Equal(1, loads);
        Assert.Equal("", output); // refused before the target is even described
        Assert.StartsWith("Refusing to run: ", error);
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(old, "select count(*) from operator_actions"));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(old, "select count(*) from inventories where account_id = @account", new { account }));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(old, "select count(*) from operatives where account_id = @account", new { account }));
    }
    [Fact]
    public async Task The_store_reads_its_catalogue_only_to_grant_a_starter_and_before_touching_the_database()
    {
        var account = await AccountWithASessionAsync();
        var refusing = new AccountAdminStore(db.AsApp(), () => { loads++; throw new InvalidOperationException("The embedded catalogue.json resource is missing."); });

        await refusing.RotateCredentialAsync(account.AccountId, Op);
        await refusing.RevokeSessionsAsync(account.AccountId, Op);
        await refusing.SuspendAsync(account.AccountId, Op);
        await refusing.ReactivateAsync(account.AccountId, Op);
        var live = await ScalarAsSuperuserAsync<Guid>(db, "select credential_id from dev_credentials where revoked_at is null");
        await refusing.RevokeCredentialAsync(live, Op);
        Assert.Equal(0, loads);

        var (accounts, auditRows) = (await ScalarAsSuperuserAsync<long>(db, "select count(*) from accounts"), await AuditRowsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => refusing.CreateAccountAsync("never", null, Op));
        await Assert.ThrowsAsync<InvalidOperationException>(() => refusing.BackfillStarterAsync(account.AccountId, Op));
        Assert.Equal(2, loads);
        Assert.Equal(accounts, await ScalarAsSuperuserAsync<long>(db, "select count(*) from accounts"));
        Assert.Equal(auditRows, await AuditRowsAsync());
    }
}
