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
    [InlineData("energy open --account-base58 BukQL --operator o --reason r")]
    [InlineData("energy grant --account-base58 BukQL --amount 5 --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")]
    [InlineData("insurance activate --account-base58 BukQL --policy-id standard --duration-seconds 60 --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")]
    public void Every_change_to_an_existing_account_needs_confirmation_and_takes_yes(string line)
    {
        var a = Parse(line);
        Assert.True(a.ChangesAnExistingAccount);
        Assert.False(a.Confirmed);
        Assert.True(Parse(line + " --yes").Confirmed);
        Assert.True(Parse(line.Replace(" --operator", " --yes --operator", StringComparison.Ordinal)).Confirmed); // anywhere among the flags
    }

    [Theory]
    [InlineData("account create --operator o --reason r", true)]
    [InlineData("starter backfill --account-base58 BukQL --operator o --reason r", true)]
    [InlineData("credential rotate --account-base58 BukQL --operator o --reason r", false)]
    [InlineData("credential revoke --credential 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r", false)]
    [InlineData("sessions revoke --account-base58 BukQL --operator o --reason r", false)]
    [InlineData("account suspend --account-base58 BukQL --operator o --reason r", false)]
    [InlineData("account reactivate --account-base58 BukQL --operator o --reason r", false)]
    [InlineData("energy open --account-base58 BukQL --operator o --reason r", false)]
    [InlineData("energy grant --account-base58 BukQL --amount 5 --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r", false)]
    [InlineData("energy policy publish --cap 50 --opening-balance 50 --interval-seconds 600 --amount 1 --void-refund-pct 100 --operator o --reason r", false)]
    [InlineData("energy reconcile --operator o --reason r", false)]
    [InlineData("insurance policy publish --policy-id standard --terms-file t.json --operator o --reason r", false)]
    [InlineData("insurance activate --account-base58 BukQL --policy-id standard --duration-seconds 60 --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r", false)]
    [InlineData("mission template publish --template-id procedural-standard --energy-cost 0 --min-operatives 1 --max-operatives 3 --operator o --reason r", false)]
    [InlineData("host register-dev --host-id dev-1 --capacity 2 --operator o --reason r", false)]
    [InlineData("host heartbeat-dev --host-id dev-1 --operator o --reason r", false)]
    public void Only_the_starter_granting_verbs_need_the_catalogue(string line, bool needs) =>
        Assert.Equal(needs, Parse(line).NeedsCatalogue);

    [Fact]
    public void Every_verb_is_classified_and_only_the_starter_granting_ones_need_the_catalogue() =>
        // A new verb fails this until it is listed here and classified in the theory above (security administration, or needing game definitions).
        Assert.Equal(new SortedSet<string>(["account-create", "account-reactivate", "account-suspend", "credential-revoke", "credential-rotate", "energy-grant", "energy-open",
                "energy-policy-publish", "energy-reconcile", "host-heartbeat-dev", "host-register-dev", "insurance-activate", "insurance-policy-publish",
                "mission-template-publish", "sessions-revoke", "starter-backfill"]),
            new SortedSet<string>(AdminArguments.KnownVerbs));

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
    [InlineData("energy open --operator o --reason r")]                                     // account missing
    [InlineData("energy open --account-base58 BukQL --amount 5 --operator o --reason r")]   // a flag this verb doesn't take
    [InlineData("energy grant --account-base58 BukQL --amount 5 --operator o --reason r")]  // key missing
    [InlineData("energy grant --account-base58 BukQL --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")] // amount missing
    [InlineData("energy grant --amount 5 --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")]              // account missing
    [InlineData("energy grant --account-base58 BukQL --amount five --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")]
    [InlineData("energy grant --account-base58 BukQL --amount 5.0 --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")]
    [InlineData("energy grant --account-base58 BukQL --amount 99999999999 --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")]
    [InlineData("energy grant --account-base58 BukQL --amount 5 --idempotency-key not-a-uuid --operator o --reason r")]
    [InlineData("energy grant --account-base58 BukQL --amount 5 --idempotency-key {0192f1a4-0000-7000-8000-000000000000} --operator o --reason r")]
    [InlineData("energy grant --account-base58 BukQL --amount 5 --idempotency-key 0192f1a400007000800000000000000 --operator o --reason r")]
    [InlineData("energy policy publish --cap 50 --opening-balance 50 --interval-seconds 600 --amount 1 --operator o --reason r")] // refund missing
    [InlineData("energy policy publish --cap 50 --opening-balance 50 --interval-seconds 10m --amount 1 --void-refund-pct 100 --operator o --reason r")]
    [InlineData("energy policy publish --cap 50 --opening-balance 50 --interval-seconds 600 --amount 1 --void-refund-pct 100 --account-base58 BukQL --operator o --reason r")]
    [InlineData("energy policy --cap 50 --operator o --reason r")]                         // unknown verb
    [InlineData("energy reconcile --operator o --reason r --yes")]                         // nothing to confirm
    [InlineData("energy reconcile --account-base58 BukQL --operator o --reason r")]        // takes no account
    [InlineData("energy close --account-base58 BukQL --operator o --reason r")]            // unknown verb
    [InlineData("insurance policy publish --policy-id standard --operator o --reason r")]                  // terms file missing
    [InlineData("insurance policy publish --terms-file t.json --operator o --reason r")]                   // policy id missing
    [InlineData("insurance policy publish --policy-id standard --terms-file t.json --account-base58 BukQL --operator o --reason r")] // takes no account
    [InlineData("insurance activate --account-base58 BukQL --policy-id standard --duration-seconds 60 --operator o --reason r")]   // key missing
    [InlineData("insurance activate --account-base58 BukQL --policy-id standard --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")] // duration missing
    [InlineData("insurance activate --account-base58 BukQL --duration-seconds 60 --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")] // policy missing
    [InlineData("insurance activate --policy-id standard --duration-seconds 60 --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")]   // account missing
    [InlineData("insurance activate --account-base58 BukQL --policy-id standard --duration-seconds 1h --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --operator o --reason r")]
    [InlineData("insurance activate --account-base58 BukQL --policy-id standard --duration-seconds 60 --idempotency-key not-a-uuid --operator o --reason r")]
    [InlineData("insurance activate --account-base58 BukQL --policy-id standard --duration-seconds 60 --idempotency-key 0192f1a4-0000-7000-8000-000000000000 --terms-file t.json --operator o --reason r")]
    [InlineData("insurance policy --policy-id standard --operator o --reason r")]                         // unknown verb
    [InlineData("mission template publish --template-id t --energy-cost 1 --min-operatives 1 --operator o --reason r")]                    // max missing
    [InlineData("mission template publish --energy-cost 1 --min-operatives 1 --max-operatives 3 --operator o --reason r")]                 // template missing
    [InlineData("mission template publish --template-id t --energy-cost ten --min-operatives 1 --max-operatives 3 --operator o --reason r")]
    [InlineData("mission template publish --template-id t --energy-cost 1 --min-operatives 1 --max-operatives 3.0 --operator o --reason r")]
    [InlineData("mission template publish --template-id t --energy-cost 1 --min-operatives 1 --max-operatives 3 --account-base58 BukQL --operator o --reason r")]
    [InlineData("mission template --template-id t --operator o --reason r")]                             // unknown verb
    [InlineData("host register-dev --host-id dev-1 --operator o --reason r")]                             // capacity missing
    [InlineData("host register-dev --capacity 2 --operator o --reason r")]                                // host missing
    [InlineData("host register-dev --host-id dev-1 --capacity two --operator o --reason r")]
    [InlineData("host register-dev --host-id dev-1 --capacity 2 --account-base58 BukQL --operator o --reason r")]
    [InlineData("host heartbeat-dev --operator o --reason r")]                                            // host missing
    [InlineData("host heartbeat-dev --host-id dev-1 --capacity 2 --operator o --reason r")]               // a flag this verb doesn't take
    [InlineData("host heartbeat-dev --host-id dev-1 --operator o --reason r --yes")]                      // nothing to confirm
    [InlineData("host register --host-id dev-1 --capacity 2 --operator o --reason r")]                    // unknown verb
    [InlineData("account")]
    [InlineData("")]
    public void Bad_arguments_are_usage_errors(string line) =>
        Assert.Throws<AdminUsageException>(() => Parse(line));

    [Fact]
    public void A_blank_operator_or_reason_is_a_usage_error() =>
        Assert.Throws<AdminUsageException>(() => AdminArguments.Parse(["account", "create", "--operator", " ", "--reason", "r"]));

    [Fact]
    public void Energy_verbs_parse_their_values()
    {
        var key = Guid.CreateVersion7();
        var grant = Parse($"energy grant --account-base58 BukQL --amount 25 --idempotency-key {key.ToString().ToUpperInvariant()} --operator o --reason r");
        Assert.Equal(("energy-grant", 123456789L, 25, key), (grant.Verb, grant.Account, grant.Amount, grant.IdempotencyKey)); // one UUID, either case
        Assert.Equal(-3, Parse($"energy grant --account-base58 BukQL --amount -3 --idempotency-key {key} --operator o --reason r").Amount); // the store refuses it (InvalidAmount)

        var open = Parse("energy open --account-base58 BukQL --operator o --reason r");
        Assert.Equal(("energy-open", 123456789L, null, null), (open.Verb, open.Account, open.Amount, open.IdempotencyKey));

        var publish = Parse("energy policy publish --cap 80 --opening-balance 40 --interval-seconds 300 --amount 2 --void-refund-pct 50 --operator o --reason r");
        Assert.Equal("energy-policy-publish", publish.Verb);
        Assert.Equal(new EnergyPolicyValues(80, 40, 300, 2, 50), publish.Policy);
        Assert.Equal((null, null, false, AdminConfirmation.Publish), (publish.Account, publish.Amount, publish.ChangesAnExistingAccount, publish.Confirmation));
        Assert.True(Parse("energy policy publish --cap 80 --opening-balance 40 --interval-seconds 300 --amount 2 --void-refund-pct 50 --operator o --reason r --yes").Confirmed);
        Assert.Equal(-1, Parse("energy policy publish --cap -1 --opening-balance 0 --interval-seconds 0 --amount 0 --void-refund-pct 101 --operator o --reason r").Policy!.Cap); // InvalidPolicy, not usage

        var reconcile = Parse("energy reconcile --operator o --reason r");
        Assert.Equal(("energy-reconcile", null, false, AdminConfirmation.None), (reconcile.Verb, reconcile.Account, reconcile.ChangesAnExistingAccount, reconcile.Confirmation));
        Assert.Equal(AdminConfirmation.None, Parse("account create --operator o --reason r").Confirmation);
        Assert.Equal(AdminConfirmation.Account, open.Confirmation);
        Assert.Contains("energy grant --account-base58", AdminArguments.Usage);
        Assert.Contains("energy policy publish", AdminArguments.Usage);
        Assert.Contains("energy reconcile", AdminArguments.Usage);
    }

    [Fact]
    public void Insurance_verbs_parse_their_values()
    {
        var key = Guid.CreateVersion7();
        var activate = Parse($"insurance activate --account-base58 BukQL --policy-id standard --duration-seconds 3600 --idempotency-key {key.ToString().ToUpperInvariant()} --operator o --reason r");
        Assert.Equal(("insurance-activate", 123456789L, "standard", 3600, key, AdminConfirmation.Account),
            (activate.Verb, activate.Account, activate.PolicyId, activate.DurationSeconds, activate.IdempotencyKey, activate.Confirmation));
        Assert.Equal(-3, Parse($"insurance activate --account-base58 BukQL --policy-id standard --duration-seconds -3 --idempotency-key {key} --operator o --reason r").DurationSeconds); // the store refuses it
        Assert.Equal("Not_A_Policy", Parse($"insurance activate --account-base58 BukQL --policy-id Not_A_Policy --duration-seconds 1 --idempotency-key {key} --operator o --reason r").PolicyId); // likewise

        var publish = Parse("insurance policy publish --policy-id premium --terms-file /tmp/terms.json --operator o --reason r");
        Assert.Equal(("insurance-policy-publish", "premium", "/tmp/terms.json", null, AdminConfirmation.Publish, false),
            (publish.Verb, publish.PolicyId, publish.TermsFile, publish.Account, publish.Confirmation, publish.ChangesAnExistingAccount));
        Assert.True(Parse("insurance policy publish --policy-id premium --terms-file t.json --operator o --reason r --yes").Confirmed);
        Assert.False(publish.IsEnergyVerb);
        Assert.Contains("insurance policy publish --policy-id", AdminArguments.Usage);
        Assert.Contains("insurance activate --account-base58", AdminArguments.Usage);
    }

    [Fact]
    public void Mission_template_and_host_verbs_parse_their_values()
    {
        var publish = Parse("mission template publish --template-id procedural-standard --energy-cost 0 --min-operatives 1 --max-operatives 3 --operator o --reason r");
        Assert.Equal(("mission-template-publish", new MissionTemplateValues("procedural-standard", 0, 1, 3), AdminConfirmation.Publish, false, null),
            (publish.Verb, publish.Template, publish.Confirmation, publish.ChangesAnExistingAccount, publish.Account));
        Assert.True(Parse("mission template publish --template-id t --energy-cost 1 --min-operatives 1 --max-operatives 3 --operator o --reason r --yes").Confirmed);
        Assert.Equal(new MissionTemplateValues("Bad_Id", -1, 0, 99), // MissionTemplate refuses these before any connection, not the parser
            Parse("mission template publish --template-id Bad_Id --energy-cost -1 --min-operatives 0 --max-operatives 99 --operator o --reason r").Template);
        Assert.True(publish.IsMissionTemplateVerb);
        Assert.False(publish.IsEnergyVerb || publish.IsInsuranceVerb || publish.IsHostVerb);

        var register = Parse("host register-dev --host-id dev-1 --capacity 2 --operator o --reason r");
        Assert.Equal(("host-register-dev", "dev-1", 2, AdminConfirmation.Register, false), (register.Verb, register.HostId, register.Capacity, register.Confirmation, register.ChangesAnExistingAccount));
        Assert.True(Parse("host register-dev --host-id dev-1 --capacity 2 --yes --operator o --reason r").Confirmed);
        Assert.True(register.IsHostVerb);
        var heartbeat = Parse("host heartbeat-dev --host-id dev-1 --operator o --reason r");
        Assert.Equal(("host-heartbeat-dev", "dev-1", null, AdminConfirmation.None), (heartbeat.Verb, heartbeat.HostId, heartbeat.Capacity, heartbeat.Confirmation));
        Assert.True(heartbeat.IsHostVerb);

        Assert.Contains("mission template publish --template-id", AdminArguments.Usage);
        Assert.Contains("host register-dev --host-id", AdminArguments.Usage);
        Assert.Contains("host heartbeat-dev --host-id", AdminArguments.Usage);
        Assert.Contains("BLACKGLASS_DEV_HOSTS=enabled", AdminArguments.Usage);
    }
}
