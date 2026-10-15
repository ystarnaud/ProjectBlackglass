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
        Assert.Equal(("account-create", null, null, false), (bare.Verb, bare.Handle, bare.DisplayName, bare.ChangesAnExistingAccount));
    }

    [Fact]
    public void Account_targets_are_named_by_an_explicitly_base58_flag()
    {
        Assert.Equal(123456789L, Parse("credential rotate --account-base58 BukQL --operator o --reason r").Account);
        Assert.Equal(long.MaxValue, Parse("account suspend --account-base58 NQm6nKp8qFC --operator o --reason r").Account);
        Assert.Contains("--account-base58 <Base58 account id>", AdminArguments.Usage);
    }

    [Fact]
    public void An_all_digit_id_is_base58_like_any_other_never_decimal()
    {
        // "2" is account 1 and "23456789" is account 2286136885086: the flag says Base58, and digits get no special case.
        Assert.Equal(1L, Parse("account suspend --account-base58 2 --operator o --reason r").Account);
        Assert.Equal(2286136885086L, Parse("credential rotate --account-base58 23456789 --operator o --reason r").Account);
        // A decimal id with a 0 or a leading 1 isn't canonical Base58 at all.
        Assert.Throws<AdminUsageException>(() => Parse("credential rotate --account-base58 123456789 --operator o --reason r"));
        Assert.Throws<AdminUsageException>(() => Parse("credential rotate --account-base58 1024 --operator o --reason r"));
    }

    [Fact]
    public void The_old_ambiguous_account_flag_is_refused_with_a_pointer_to_the_base58_one()
    {
        var e = Assert.Throws<AdminUsageException>(() => Parse("account suspend --account BukQL --operator o --reason r"));
        Assert.Contains("--account-base58", e.Message);
    }

    [Theory]
    [InlineData("credential rotate --account-base58 BukQL --operator o --reason r")]
    [InlineData("credential revoke --credential 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")]
    [InlineData("sessions revoke --account-base58 BukQL --operator o --reason r")]
    [InlineData("account suspend --account-base58 BukQL --operator o --reason r")]
    [InlineData("account reactivate --account-base58 BukQL --operator o --reason r")]
    [InlineData("starter backfill --account-base58 BukQL --operator o --reason r")]
    public void Every_change_to_an_existing_account_needs_confirmation_and_takes_yes(string line)
    {
        var a = Parse(line);
        Assert.True(a.ChangesAnExistingAccount);
        Assert.False(a.Confirmed);
        Assert.True(Parse(line + " --yes").Confirmed);
        Assert.True(Parse(line.Replace(" --operator", " --yes --operator", StringComparison.Ordinal)).Confirmed); // anywhere among the flags
    }

    [Fact]
    public void Credential_revocation_takes_a_uuid()
    {
        var id = Guid.CreateVersion7();
        Assert.Equal(id, Parse($"credential revoke --credential {id} --operator o --reason r").Credential);
    }

    [Theory]
    [InlineData("account create --operator o")]                                            // reason missing
    [InlineData("account create --handle x --handle y --operator o --reason r")]           // flag twice
    [InlineData("account create --operator o --reason r --extra 1")]                        // unknown flag
    [InlineData("account create --account-base58 BukQL --operator o --reason r")]           // a flag this verb doesn't take
    [InlineData("account create --operator o --reason r --yes")]                            // nothing to confirm
    [InlineData("account create --handle --operator o --reason r")]                         // a flag as a value
    [InlineData("account delete --account-base58 BukQL --operator o --reason r")]           // unknown verb
    [InlineData("credential rotate --operator o --reason r")]                               // account missing
    [InlineData("credential rotate --account-base58 1BukQL --operator o --reason r")]       // non-canonical
    [InlineData("credential rotate --account-base58 Buk0L --operator o --reason r")]        // not Base58
    [InlineData("credential rotate --account-base58 NQm6nKp8qFD --operator o --reason r")]  // beyond bigint
    [InlineData("credential rotate --account-base58 1 --operator o --reason r")]            // zero
    [InlineData("credential rotate --account-base58 BukQL --operator o --reason r --yes --yes")] // switch twice
    [InlineData("credential rotate --account-base58 BukQL --operator o --reason r --yes yes")]   // a switch takes no value
    [InlineData("credential revoke --credential not-a-uuid --operator o --reason r")]
    [InlineData("sessions revoke --account-base58 BukQL --credential 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")]
    [InlineData("starter backfill --operator o --reason r")]                                // account missing
    [InlineData("starter backfill --credential 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")] // a credential is not an account
    [InlineData("starter backfill --account-base58 BukQL --handle x --operator o --reason r")]  // a flag this verb doesn't take
    [InlineData("starter grant --account-base58 BukQL --operator o --reason r")]            // unknown verb
    [InlineData("account")]
    [InlineData("")]
    public void Bad_arguments_are_usage_errors(string line) =>
        Assert.Throws<AdminUsageException>(() => Parse(line));

    [Fact]
    public void A_blank_operator_or_reason_is_a_usage_error() =>
        Assert.Throws<AdminUsageException>(() => AdminArguments.Parse(["account", "create", "--operator", " ", "--reason", "r"]));
}
