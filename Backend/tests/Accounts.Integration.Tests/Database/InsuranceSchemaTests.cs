using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Migration 0007's insurance tables (Stage 7; owner ruling S7-D3, spec §6.2, §15.1-§15.2) against a real
/// PostgreSQL 17: immutable policy versions; append-only activations whose key is honoured once per account; one coverage
/// per account, naming its own account's activation, whose expiry never moves back under the same policy and which only
/// an expired coverage lets change policy; and immutable insurance snapshots that name their participant's activation.</summary>
[Trait("Requires", "Postgres")]
public class InsuranceSchemaTests : IAsyncLifetime
{
    TestDatabase db = null!;

    public async Task InitializeAsync()
    {
        db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync(); await AttemptFixtures.RegisterTestCatalogueAsync(db);
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    static async Task<PostgresException> FailureOf(NpgsqlDataSource ds, string sql, object? args = null)
    {
        await using var conn = await ds.OpenConnectionAsync();
        return await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, args));
    }

    async Task<T> SuperScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    async Task AppExecuteAsync(string sql, object? args = null)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync(sql, args);
    }

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    const string InsertPolicy = "insert into insurance_policies (policy_id, version, terms) values ";

    const string InsertActivation = """
        insert into insurance_activations (activation_id, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source, idempotency_key)
        values (@activation, @account, @policy, @version, @duration::interval, @previous, @resulting, @source, @key)
        """;

    /// <summary>An activation of <paramref name="policy"/> for a day from now, and the coverage it sets, as the app writes them.</summary>
    async Task<Guid> ActivateAsync(long account, string policy = "standard", int version = 1)
    {
        var activation = Guid.CreateVersion7();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("""
            insert into insurance_activations (activation_id, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source, idempotency_key)
            values (@activation, @account, @policy, @version, interval '1 day', null, clock_timestamp() + interval '1 day', 'dev_grant', gen_random_uuid())
            """, new { activation, account, policy, version }, tx);
        await conn.ExecuteAsync("""
            insert into insurance_coverages (account_id, policy_id, policy_version, activation_id, starts_at, expires_at)
            select account_id, policy_id, policy_version, activation_id, created_at, resulting_expires_at from insurance_activations where activation_id = @activation
            """, new { activation }, tx);
        await tx.CommitAsync();
        return activation;
    }

    // ---- policies ----

    [Theory]
    [InlineData("('', 1, '{}')", "insurance_policies_policy_id_check")]
    [InlineData("('Standard', 1, '{}')", "insurance_policies_policy_id_check")]
    [InlineData("('a_b', 1, '{}')", "insurance_policies_policy_id_check")]
    [InlineData("('a b', 1, '{}')", "insurance_policies_policy_id_check")]
    [InlineData("(repeat('a', 65), 1, '{}')", "insurance_policies_policy_id_check")]
    [InlineData("('premium', 0, '{}')", "insurance_policies_version_check")]
    [InlineData("('premium', 1, '[]')", "insurance_policies_terms_check")]
    [InlineData("('premium', 1, 'null')", "insurance_policies_terms_check")]
    public async Task A_policy_check_is_enforced(string values, string constraint)
    {
        var e = await FailureOf(db.AsApp(), InsertPolicy + values);
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
        Assert.Equal(1L, await SuperScalarAsync<long>("select count(*) from insurance_policies"));
    }

    [Fact]
    public async Task A_policy_version_is_published_once()
    {
        await AppExecuteAsync(InsertPolicy + "('standard', 2, '{}'), (repeat('a', 64), 1, '{}'), ('premium.v-2', 1, '{}')");
        var e = await FailureOf(db.AsApp(), InsertPolicy + "('standard', 1, '{}')");
        Assert.Equal(("23505", "insurance_policies_pkey"), (e.SqlState, e.ConstraintName));
        Assert.Equal(4L, await SuperScalarAsync<long>("select count(*) from insurance_policies"));
    }

    [Fact]
    public async Task Policies_cannot_be_updated_deleted_or_truncated_even_by_the_owner()
    {
        foreach (var ds in new[] { db.AsMigrator(), db.AsSuperuser() })
        {
            foreach (var sql in new[]
            {
                "update insurance_policies set terms = '{}'",
                "update insurance_policies set created_at = created_at",
                "delete from insurance_policies",
                "truncate insurance_policies cascade",
            })
            {
                var e = await FailureOf(ds, sql);
                Assert.Equal(("23514", "insurance_policy_is_immutable"), (e.SqlState, e.ConstraintName));
            }
        }
        Assert.True(await SuperScalarAsync<bool>("select terms = @StandardTerms::jsonb from insurance_policies", new { AttemptSchemaTests.StandardTerms }));
    }

    // ---- activations ----

    [Theory]
    [InlineData("0 seconds", null, "dev_grant", "insurance_activations_duration_check")]
    [InlineData("-1 second", null, "dev_grant", "insurance_activations_duration_check")]
    [InlineData("1 month", null, "dev_grant", "insurance_activations_duration_check")]
    [InlineData("1 year -364 days", null, "dev_grant", "insurance_activations_duration_check")]
    [InlineData("1 day", null, "purchase", "insurance_activations_source_check")]
    [InlineData("1 day", "now", "dev_grant", "insurance_activation_extends")]      // resulting = previous
    [InlineData("1 day", "later", "dev_grant", "insurance_activation_extends")]    // resulting before previous
    public async Task An_activation_check_is_enforced(string duration, string? previous, string source, string constraint)
    {
        var account = await NewAccountAsync();
        var resulting = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var e = await FailureOf(db.AsApp(), InsertActivation, new
        {
            activation = Guid.CreateVersion7(), account, policy = "standard", version = 1, duration,
            previous = previous switch { "now" => resulting, "later" => resulting.AddTicks(10), _ => (DateTime?)null },
            resulting, source, key = Guid.NewGuid(),
        });
        Assert.Equal(("23514", constraint), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task An_activation_names_a_published_policy_version_and_a_known_account()
    {
        var account = await NewAccountAsync();
        var resulting = DateTime.UtcNow.AddDays(1);
        var e = await FailureOf(db.AsApp(), InsertActivation, new { activation = Guid.CreateVersion7(), account, policy = "standard", version = 2, duration = "1 day", previous = (DateTime?)null, resulting, source = "dev_grant", key = Guid.NewGuid() });
        Assert.Equal(("23503", "insurance_activations_policy_fkey"), (e.SqlState, e.ConstraintName));
        e = await FailureOf(db.AsApp(), InsertActivation, new { activation = Guid.CreateVersion7(), account = account + 1000, policy = "standard", version = 1, duration = "1 day", previous = (DateTime?)null, resulting, source = "dev_grant", key = Guid.NewGuid() });
        Assert.Equal(("23503", "insurance_activations_account_id_fkey"), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task An_activation_key_is_honoured_once_per_account()
    {
        var account = await NewAccountAsync();
        var other = await NewAccountAsync();
        var key = Guid.NewGuid();
        var resulting = DateTime.UtcNow.AddDays(1);
        object Args(long a) => new { activation = Guid.CreateVersion7(), account = a, policy = "standard", version = 1, duration = "1 day", previous = (DateTime?)null, resulting, source = "dev_grant", key };
        await AppExecuteAsync(InsertActivation, Args(account));
        var e = await FailureOf(db.AsApp(), InsertActivation, Args(account));
        Assert.Equal(("23505", "insurance_activations_account_id_idempotency_key_key"), (e.SqlState, e.ConstraintName));
        await AppExecuteAsync(InsertActivation, Args(other));   // keys are per account
        Assert.Equal(2L, await SuperScalarAsync<long>("select count(*) from insurance_activations"));
    }

    [Fact]
    public async Task Activations_are_append_only_even_for_the_owner()
    {
        await ActivateAsync(await NewAccountAsync());
        foreach (var ds in new[] { db.AsMigrator(), db.AsSuperuser() })
        {
            foreach (var sql in new[]
            {
                "update insurance_activations set resulting_expires_at = resulting_expires_at + interval '1 day'",
                "update insurance_activations set created_at = created_at",
                "delete from insurance_activations",
                "truncate insurance_activations cascade",
            })
            {
                var e = await FailureOf(ds, sql);
                Assert.Equal(("23514", "insurance_activation_is_append_only"), (e.SqlState, e.ConstraintName));
            }
        }
        Assert.Equal(1L, await SuperScalarAsync<long>("select count(*) from insurance_activations"));
    }

    // ---- coverages ----

    const string InsertCoverage = "insert into insurance_coverages (account_id, policy_id, policy_version, activation_id, starts_at, expires_at) values (@account, @policy, 1, @activation, @starts, @expires)";

    [Fact]
    public async Task A_coverage_names_its_own_accounts_activation_of_its_policy_version()
    {
        var account = await NewAccountAsync();
        var other = await NewAccountAsync();
        var activation = await ActivateAsync(other);
        await AppExecuteAsync(InsertPolicy + "('premium', 1, '{}')");
        var starts = DateTime.UtcNow;
        // Another account's activation.
        var e = await FailureOf(db.AsApp(), InsertCoverage, new { account, policy = "standard", activation, starts, expires = starts.AddDays(1) });
        Assert.Equal(("23503", "insurance_coverages_activation_fkey"), (e.SqlState, e.ConstraintName));
        // The account's own activation, but of another policy than the coverage claims.
        var own = Guid.CreateVersion7();
        await AppExecuteAsync(InsertActivation, new { activation = own, account, policy = "standard", version = 1, duration = "1 day", previous = (DateTime?)null, resulting = starts.AddDays(1), source = "dev_grant", key = Guid.NewGuid() });
        e = await FailureOf(db.AsApp(), InsertCoverage, new { account, policy = "premium", activation = own, starts, expires = starts.AddDays(1) });
        Assert.Equal(("23503", "insurance_coverages_activation_fkey"), (e.SqlState, e.ConstraintName));
        // An expiry not after the start.
        e = await FailureOf(db.AsApp(), InsertCoverage, new { account, policy = "standard", activation = own, starts, expires = starts });
        Assert.Equal(("23514", "insurance_coverage_expires_after_start"), (e.SqlState, e.ConstraintName));
        // One coverage per account.
        await AppExecuteAsync(InsertCoverage, new { account, policy = "standard", activation = own, starts, expires = starts.AddDays(1) });
        e = await FailureOf(db.AsApp(), InsertCoverage, new { account, policy = "standard", activation = own, starts, expires = starts.AddDays(2) });
        Assert.Equal(("23505", "insurance_coverages_pkey"), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task The_coverage_expiry_never_moves_back_while_the_policy_is_unchanged()
    {
        var account = await NewAccountAsync();
        await ActivateAsync(account);
        await AppExecuteAsync(InsertPolicy + "('standard', 2, '{}')");
        const string Where = " where account_id = @account";
        // Unchanged or later is fine, also when the coverage moves to the activated version.
        await AppExecuteAsync("update insurance_coverages set expires_at = expires_at" + Where, new { account });
        await AppExecuteAsync("update insurance_coverages set expires_at = expires_at + interval '1 microsecond'" + Where, new { account });
        var second = Guid.CreateVersion7();
        await AppExecuteAsync(InsertActivation, new { activation = second, account, policy = "standard", version = 2, duration = "1 day", previous = (DateTime?)null, resulting = DateTime.UtcNow.AddDays(3), source = "dev_grant", key = Guid.NewGuid() });
        await AppExecuteAsync("update insurance_coverages set policy_version = 2, activation_id = @second, expires_at = expires_at + interval '1 day'" + Where, new { account, second });

        foreach (var set in new[]
        {
            "expires_at = expires_at - interval '1 microsecond'",
            "starts_at = starts_at - interval '1 day', expires_at = expires_at - interval '1 day'",
        })
        {
            var e = await FailureOf(db.AsApp(), $"update insurance_coverages set {set}" + Where, new { account });
            Assert.Equal(("23514", "insurance_coverage_forward_only"), (e.SqlState, e.ConstraintName));
        }
        // The owner is held to it too.
        var owner = await FailureOf(db.AsMigrator(), "update insurance_coverages set expires_at = expires_at - interval '1 hour'" + Where, new { account });
        Assert.Equal("insurance_coverage_forward_only", owner.ConstraintName);
    }

    [Fact]
    public async Task A_coverage_changes_policy_only_once_it_has_expired()
    {
        var account = await NewAccountAsync();
        await ActivateAsync(account);
        await AppExecuteAsync(InsertPolicy + "('premium', 1, '{}')");
        var premium = Guid.CreateVersion7();
        await AppExecuteAsync(InsertActivation, new { activation = premium, account, policy = "premium", version = 1, duration = "1 day", previous = (DateTime?)null, resulting = DateTime.UtcNow.AddDays(1), source = "dev_grant", key = Guid.NewGuid() });
        const string Switch = "update insurance_coverages set policy_id = 'premium', policy_version = 1, activation_id = @premium, starts_at = clock_timestamp(), expires_at = clock_timestamp() + interval '1 day' where account_id = @account";

        // While the standard coverage is active (cross-tier is deferred, S7-D3).
        var e = await FailureOf(db.AsApp(), Switch, new { account, premium });
        Assert.Equal(("23514", "insurance_coverage_forward_only"), (e.SqlState, e.ConstraintName));

        // Once it has expired (backdated as the superuser in replica mode).
        await using (var conn = await db.AsSuperuser().OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("set local session_replication_role = replica", transaction: tx);
            await conn.ExecuteAsync("update insurance_coverages set starts_at = clock_timestamp() - interval '2 days', expires_at = clock_timestamp() - interval '1 microsecond' where account_id = @account", new { account }, tx);
            await tx.CommitAsync();
        }
        await AppExecuteAsync(Switch, new { account, premium });
        Assert.Equal("premium", await SuperScalarAsync<string>("select policy_id from insurance_coverages where account_id = @account", new { account }));
    }

    // ---- snapshots ----

    async Task<(long Account, Guid Attempt, Guid Activation)> InsuredParticipantAsync()
    {
        var account = await NewAccountAsync();
        var activation = await ActivateAsync(account);
        var attempt = Guid.CreateVersion7();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("""
            insert into mission_attempts (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline)
            values (@attempt, @account, 'procedural-standard', 1, (select min(version) from catalogue_versions), clock_timestamp() + interval '60 seconds');
            insert into attempt_secrets (attempt_id, seed, retention_salt) values (@attempt, 7, decode(repeat('ab', 32), 'hex'))
            """, new { attempt, account }, tx);
        await conn.ExecuteAsync("""
            insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
            values (@attempt, @account, 'procedural-standard', 1, '{}', 1, 0)
            """, new { attempt, account }, tx);
        await conn.ExecuteAsync("insert into energy_holds (attempt_id, account_id, amount) values (@attempt, @account, 0)", new { attempt, account }, tx);
        await conn.ExecuteAsync("""
            insert into attempt_insurance (attempt_id, account_id, activation_id, policy_id, policy_version, terms)
            select @attempt, c.account_id, c.activation_id, c.policy_id, c.policy_version, p.terms
            from insurance_coverages c join insurance_policies p on p.policy_id = c.policy_id and p.version = c.policy_version
            where c.account_id = @account
            """, new { attempt, account }, tx);
        await tx.CommitAsync();
        return (account, attempt, activation);
    }

    [Fact]
    public async Task An_insurance_snapshot_copies_the_terms_and_names_its_participants_activation()
    {
        var (account, attempt, _) = await InsuredParticipantAsync();
        Assert.True(await SuperScalarAsync<bool>("select terms = @StandardTerms::jsonb from attempt_insurance where attempt_id = @attempt", new { attempt, AttemptSchemaTests.StandardTerms }));
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));

        // Another account's activation, or a policy the activation isn't of, is refused.
        var other = await NewAccountAsync();
        var foreign = await ActivateAsync(other);
        const string Insert = "insert into attempt_insurance (attempt_id, account_id, activation_id, policy_id, policy_version, terms) values (@attempt, @account, @activation, @policy, @version, '{}')";
        var e = await FailureOf(db.AsApp(), Insert, new { attempt, account, activation = foreign, policy = "standard", version = 1 });
        Assert.Equal(("23505", "attempt_insurance_pkey"), (e.SqlState, e.ConstraintName));   // one snapshot per participant
        var fresh = await InsuredParticipantWithoutSnapshotAsync();
        e = await FailureOf(db.AsApp(), Insert, new { attempt = fresh.Attempt, account = fresh.Account, activation = foreign, policy = "standard", version = 1 });
        Assert.Equal(("23503", "attempt_insurance_activation_fkey"), (e.SqlState, e.ConstraintName));
        await AppExecuteAsync(InsertPolicy + "('standard', 2, '{}')");
        e = await FailureOf(db.AsApp(), Insert, new { attempt = fresh.Attempt, account = fresh.Account, activation = fresh.Activation, policy = "standard", version = 2 });
        Assert.Equal(("23503", "attempt_insurance_activation_fkey"), (e.SqlState, e.ConstraintName));
        // A non-participant.
        e = await FailureOf(db.AsApp(), Insert, new { attempt, account = other, activation = foreign, policy = "standard", version = 1 });
        Assert.Equal(("23503", "attempt_insurance_participant_fkey"), (e.SqlState, e.ConstraintName));
        // Terms that are not an object.
        e = await FailureOf(db.AsApp(), Insert.Replace("'{}'", "'[]'"), new { attempt = fresh.Attempt, account = fresh.Account, activation = fresh.Activation, policy = "standard", version = 1 });
        Assert.Equal(("23514", "attempt_insurance_terms_check"), (e.SqlState, e.ConstraintName));
    }

    async Task<(long Account, Guid Attempt, Guid Activation)> InsuredParticipantWithoutSnapshotAsync()
    {
        var account = await NewAccountAsync();
        var activation = await ActivateAsync(account);
        var attempt = Guid.CreateVersion7();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("""
            insert into mission_attempts (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline)
            values (@attempt, @account, 'procedural-standard', 1, (select min(version) from catalogue_versions), clock_timestamp() + interval '60 seconds');
            insert into attempt_secrets (attempt_id, seed, retention_salt) values (@attempt, 7, decode(repeat('ab', 32), 'hex'))
            """, new { attempt, account }, tx);
        await conn.ExecuteAsync("""
            insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
            values (@attempt, @account, 'procedural-standard', 1, '{}', 1, 0)
            """, new { attempt, account }, tx);
        await conn.ExecuteAsync("insert into energy_holds (attempt_id, account_id, amount) values (@attempt, @account, 0)", new { attempt, account }, tx);
        await tx.CommitAsync();
        return (account, attempt, activation);
    }

    [Fact]
    public async Task Insurance_snapshots_cannot_be_updated_deleted_or_truncated_even_by_the_owner()
    {
        var (_, attempt, _) = await InsuredParticipantAsync();
        foreach (var ds in new[] { db.AsMigrator(), db.AsSuperuser() })
        {
            foreach (var sql in new[]
            {
                "update attempt_insurance set terms = '{}'",
                "update attempt_insurance set snapshotted_at = snapshotted_at",
                "delete from attempt_insurance",
                "truncate attempt_insurance",
            })
            {
                var e = await FailureOf(ds, sql);
                Assert.Equal(("23514", "attempt_insurance_is_immutable"), (e.SqlState, e.ConstraintName));
            }
        }
        Assert.True(await SuperScalarAsync<bool>("select terms = @StandardTerms::jsonb from attempt_insurance where attempt_id = @attempt", new { attempt, AttemptSchemaTests.StandardTerms }));
    }

    [Fact]
    public async Task The_snapshot_survives_the_coverage_expiring_extending_and_a_new_policy_version()
    {
        var (account, attempt, _) = await InsuredParticipantAsync();
        await AppExecuteAsync(InsertPolicy + "('standard', 2, '{\"covers\":[]}')");
        var second = Guid.CreateVersion7();
        await AppExecuteAsync(InsertActivation, new { activation = second, account, policy = "standard", version = 2, duration = "2 days", previous = (DateTime?)null, resulting = DateTime.UtcNow.AddDays(3), source = "dev_grant", key = Guid.NewGuid() });
        await AppExecuteAsync("update insurance_coverages set policy_version = 2, activation_id = @second, expires_at = expires_at + interval '2 days' where account_id = @account", new { account, second });
        Assert.Equal((1, true), await SingleAsync<(int, bool)>(
            "select policy_version, terms = @StandardTerms::jsonb from attempt_insurance where attempt_id = @attempt", new { attempt, AttemptSchemaTests.StandardTerms }));
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
    }

    async Task<T> SingleAsync<T>(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return await conn.QuerySingleAsync<T>(sql, args);
    }
}
