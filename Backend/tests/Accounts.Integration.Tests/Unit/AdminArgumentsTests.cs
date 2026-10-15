using Blackglass.Accounts.Api.Admin;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class AdminArgumentsTests
{
    static AdminArguments Parse(string line) => AdminArguments.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Account_create_parses_with_optional_handle_and_display_name()
    {
        var a = AdminArguments.Parse(["account", "create", "--handle", "tester-1", "--display-name", "Tester One", "--operator", "ops", "--reason", "new tester"]);
        Assert.Equal(("account-create", "tester-1", "Tester One", "ops", "new tester"), (a.Verb, a.Handle, a.DisplayName, a.Operator.Operator, a.Operator.Reason));
        var bare = Parse("account create --operator ops --reason r");
        Assert.Equal(("account-create", null, null), (bare.Verb, bare.Handle, bare.DisplayName));
    }

    [Fact]
    public void Account_targets_take_a_canonical_base58_account_id()
    {
        Assert.Equal(123456789L, Parse("credential rotate --account BukQL --operator o --reason r").Account);
        Assert.Equal(long.MaxValue, Parse("account suspend --account NQm6nKp8qFC --operator o --reason r").Account);
    }

    [Fact]
    public void Credential_revocation_takes_a_uuid()
    {
        var id = Guid.CreateVersion7();
        Assert.Equal(id, Parse($"credential revoke --credential {id} --operator o --reason r").Credential);
    }

    [Theory]
    [InlineData("account create --operator o")]                                       // reason missing
    [InlineData("account create --handle x --handle y --operator o --reason r")]      // flag twice
    [InlineData("account create --operator o --reason r --extra 1")]                   // unknown flag
    [InlineData("account create --account BukQL --operator o --reason r")]             // a flag this verb doesn't take
    [InlineData("account create --handle --operator o --reason r")]                    // a flag as a value
    [InlineData("account delete --account BukQL --operator o --reason r")]             // unknown verb
    [InlineData("credential rotate --operator o --reason r")]                          // account missing
    [InlineData("credential rotate --account 1BukQL --operator o --reason r")]         // non-canonical
    [InlineData("credential rotate --account Buk0L --operator o --reason r")]          // not Base58
    [InlineData("credential rotate --account NQm6nKp8qFD --operator o --reason r")]    // beyond bigint
    [InlineData("credential rotate --account 1 --operator o --reason r")]              // zero
    [InlineData("credential revoke --credential not-a-uuid --operator o --reason r")]
    [InlineData("sessions revoke --account BukQL --credential 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")]
    [InlineData("account")]
    [InlineData("")]
    public void Bad_arguments_are_usage_errors(string line) =>
        Assert.Throws<AdminUsageException>(() => Parse(line));

    [Fact]
    public void A_decimal_id_without_zeros_is_read_as_base58_not_as_the_decimal_number()
    {
        // The documented operator hazard: "23456789" (a decimal id without 0 or a leading 1) is also the Base58 id of
        // another account. --account always takes the Base58 form the CLI prints; every command echoes the account it
        // acted on. (A decimal id with a 0, or starting with 1, is refused as non-canonical Base58.)
        Assert.Equal(2286136885086L, Parse("credential rotate --account 23456789 --operator o --reason r").Account);
        Assert.Throws<AdminUsageException>(() => Parse("credential rotate --account 123456789 --operator o --reason r"));
        Assert.Throws<AdminUsageException>(() => Parse("credential rotate --account 1024 --operator o --reason r"));
    }

    [Fact]
    public void A_blank_operator_or_reason_is_a_usage_error() =>
        Assert.Throws<AdminUsageException>(() => AdminArguments.Parse(["account", "create", "--operator", " ", "--reason", "r"]));
}
