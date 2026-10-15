using System.Text.RegularExpressions;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The admin CLI names accounts by an explicitly Base58 flag, and every change to an existing account (rotate, revoke a
/// credential, revoke sessions, suspend, reactivate) first prints its target: the canonical Base58 id, status, handle,
/// display name, creation time, live credential and live sessions. It then acts only when confirmed, by typing that
/// Base58 id at a terminal or by --yes; otherwise nothing changes.
/// </summary>
[Trait("Requires", "Postgres")]
public class AdminConfirmationTests : IAsyncLifetime
{
    TestDatabase db = null!;
    AccountAdminStore store = null!;

    public async Task InitializeAsync() { db = await MigratedAsync(); store = new AccountAdminStore(db.AsApp()); }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    /// <param name="typed">What the operator types at the terminal; null when there is no terminal.</param>
    async Task<(int Code, string Out, string Err)> CliAsync(string? typed, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await Cli.RunAsync(["admin", .. args], db.EnvFor("app"), output, error, input: typed is null ? null : new StringReader(typed));
        return (code, output.ToString(), error.ToString());
    }

    static string Field(string output, string name) => Regex.Match(output, $"(?m)^{Regex.Escape(name)}=(.*)$").Groups[1].Value.TrimEnd('\r');

    Task<long> AuditRowsAsync() => ScalarAsSuperuserAsync<long>(db, "select count(*) from operator_actions");

    async Task AssertUnchangedAsync(IssuedCredential account, long auditRows)
    {
        Assert.Equal(auditRows, await AuditRowsAsync());
        Assert.Equal("active", await ScalarAsSuperuserAsync<string>(db, "select status from accounts where account_id = @a", new { a = account.AccountId }));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from dev_credentials where revoked_at is not null"));
    }

    string[] Command(string verb, IssuedCredential account) => verb switch
    {
        "rotate" => ["credential", "rotate", "--account-base58", AccountId.Format(account.AccountId)],
        "revoke-credential" => ["credential", "revoke", "--credential", account.CredentialId.ToString()],
        "revoke-sessions" => ["sessions", "revoke", "--account-base58", AccountId.Format(account.AccountId)],
        "suspend" => ["account", "suspend", "--account-base58", AccountId.Format(account.AccountId)],
        "reactivate" => ["account", "reactivate", "--account-base58", AccountId.Format(account.AccountId)],
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    static string[] With(string[] command, params string[] more) => [.. command, "--operator", "ops", "--reason", "test", .. more];

    [Theory]
    [InlineData("rotate")]
    [InlineData("revoke-credential")]
    [InlineData("revoke-sessions")]
    [InlineData("suspend")]
    [InlineData("reactivate")]
    public async Task Without_a_terminal_or_yes_it_shows_the_target_and_changes_nothing(string verb)
    {
        var account = await store.CreateAccountAsync("first-op", "First Operative", Op);
        await new SessionStore(db.AsApp()).LoginAsync(account.AccountKey.Hash(), 60);
        var (code, output, error) = await CliAsync(null, With(Command(verb, account)));

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Equal(AccountId.Format(account.AccountId), Field(output, "target.accountId"));
        Assert.Equal("active", Field(output, "target.status"));
        Assert.Equal("first-op", Field(output, "target.handle"));
        Assert.Equal("First Operative", Field(output, "target.displayName"));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", Field(output, "target.createdAt"));
        Assert.Equal(account.CredentialId.ToString(), Field(output, "target.liveCredentialId"));
        Assert.Equal("1", Field(output, "target.liveSessions"));
        Assert.DoesNotContain("correlationId=", output);
        Assert.Contains("--yes", error);
        Assert.Contains("nothing was changed", error);
        await AssertUnchangedAsync(account, auditRows: 1);
        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from auth_sessions where revoked_at is null"));
    }

    [Fact]
    public async Task Typing_the_base58_id_at_the_terminal_confirms()
    {
        var account = await store.CreateAccountAsync(null, null, Op);
        var id = AccountId.Format(account.AccountId);
        var (code, output, error) = await CliAsync(id + "\n", With(Command("suspend", account)));
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains($"Type the Base58 account id {id}", error);
        Assert.Equal("suspended", await ScalarAsSuperuserAsync<string>(db, "select status from accounts"));
        Assert.Equal("status=suspended", output.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith("status=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no")]
    [InlineData("y")]
    [InlineData("1")]        // the decimal id: account 1 is "2" in Base58
    [InlineData(" 2")]       // padded
    [InlineData(null)]       // end of input at the prompt
    public async Task Typing_anything_else_aborts_and_changes_nothing(string? typed)
    {
        var account = await store.CreateAccountAsync(null, null, Op);
        Assert.Equal("2", AccountId.Format(account.AccountId));
        var (code, output, error) = await CliAsync(typed is null ? "" : typed + "\n", With(Command("suspend", account)));
        Assert.Equal(ExitCodes.NotConfirmed, code);
        Assert.Contains("nothing was changed", error);
        Assert.DoesNotContain("correlationId=", output);
        await AssertUnchangedAsync(account, auditRows: 1);
    }

    [Fact]
    public async Task Yes_confirms_without_reading_the_terminal()
    {
        var account = await store.CreateAccountAsync(null, null, Op);
        var (code, output, error) = await CliAsync("this would not confirm\n", With(Command("suspend", account), "--yes"));
        Assert.Equal(ExitCodes.Ok, code);
        Assert.DoesNotContain("Type the Base58", error);
        Assert.Equal(AccountId.Format(account.AccountId), Field(output, "target.accountId"));
        Assert.Equal("suspended", await ScalarAsSuperuserAsync<string>(db, "select status from accounts"));
    }

    [Fact]
    public async Task An_all_digit_base58_id_names_the_account_it_encodes_and_the_target_shows_which()
    {
        var first = await store.CreateAccountAsync("first", null, Op);   // id 1, Base58 "2"
        var second = await store.CreateAccountAsync("second", null, Op); // id 2, Base58 "3"
        var (code, output, _) = await CliAsync(null, "account", "suspend", "--account-base58", "2", "--operator", "ops", "--reason", "test", "--yes");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(("2", "first"), (Field(output, "target.accountId"), Field(output, "target.handle")));
        Assert.Contains("Base58", Field(output, "target.note"));
        Assert.Contains("never decimal", Field(output, "target.note"));
        Assert.Equal("suspended", await ScalarAsSuperuserAsync<string>(db, "select status from accounts where account_id = @a", new { a = first.AccountId }));
        Assert.Equal("active", await ScalarAsSuperuserAsync<string>(db, "select status from accounts where account_id = @a", new { a = second.AccountId }));
    }

    [Fact]
    public async Task Revoking_a_credential_shows_the_credential_and_its_account_and_is_confirmed_by_the_account_id()
    {
        var account = await store.CreateAccountAsync("owner", null, Op);
        var id = AccountId.Format(account.AccountId);
        var preview = await CliAsync(null, With(Command("revoke-credential", account)));
        Assert.Equal((account.CredentialId.ToString(), "live", id, "owner"),
            (Field(preview.Out, "target.credentialId"), Field(preview.Out, "target.credentialStatus"), Field(preview.Out, "target.accountId"), Field(preview.Out, "target.handle")));
        var (code, _, _) = await CliAsync(id + "\n", With(Command("revoke-credential", account)));
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from dev_credentials where revoked_at is not null"));
    }

    [Fact]
    public async Task An_unknown_account_or_credential_is_65_before_any_prompt_and_prints_no_target()
    {
        var account = await store.CreateAccountAsync(null, null, Op);
        var unknownAccount = await CliAsync("anything\n", "account", "suspend", "--account-base58", AccountId.Format(account.AccountId + 1), "--operator", "ops", "--reason", "x");
        Assert.Equal((ExitCodes.NotFound, ""), (unknownAccount.Code, unknownAccount.Out));
        Assert.DoesNotContain("Type the Base58", unknownAccount.Err);
        var unknownCredential = await CliAsync(null, "credential", "revoke", "--credential", Guid.CreateVersion7().ToString(), "--operator", "ops", "--reason", "x");
        Assert.Equal((ExitCodes.NotFound, ""), (unknownCredential.Code, unknownCredential.Out));
    }

    [Fact]
    public async Task Account_create_needs_no_confirmation()
    {
        var (code, output, _) = await CliAsync(null, "account", "create", "--operator", "ops", "--reason", "test");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("2", Field(output, "accountId"));
    }
}
