using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Migrations;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The explicit starter backfill for accounts created before Stage 4 (owner ruling D4): an operator-confirmed, audited,
/// idempotent grant under the account lock. The catalogue version is recorded, and a later catalogue never grants again
/// or replaces an existing starter. A pre-Stage-4 account is built the way Stage 3 built it: a database at version 2,
/// the account inserted as the app role, then migrated to the latest version (it has no inventories row).
/// </summary>
[Trait("Requires", "Postgres")]
public class BackfillTests : IAsyncLifetime
{
    static readonly GameCatalogue Catalogue = GameCatalogue.Embedded;
    TestDatabase db = null!;
    AccountAdminStore store = null!;

    public async Task InitializeAsync()
    {
        db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync(2);
        store = new AccountAdminStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    Task MigrateToLatestAsync() => new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();

    Task<long> PreStage4AccountAsync(string status = "active") => AccountTestKit.PreStage4AccountAsync(db, status);

    sealed record AuditRow(string Operator, string Reason, Guid CorrelationId, long? AccountId, JsonElement Result);

    async Task<List<AuditRow>> AuditAsync(long? account = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var rows = await conn.QueryAsync<(string Operator, string Reason, Guid CorrelationId, long? AccountId, string Result)>(
            "select operator, reason, correlation_id, account_id, result::text from operator_actions where action = 'starter_backfill' and (@account::bigint is null or account_id = @account) order by id",
            new { account });
        return rows.Select(r => new AuditRow(r.Operator, r.Reason, r.CorrelationId, r.AccountId, JsonDocument.Parse(r.Result).RootElement.Clone())).ToList();
    }

    async Task<(long Operatives, long Instances, long Stacks)> RowCountsAsync(long account) =>
        (await ScalarAsync<long>("select count(*) from operatives where account_id = @account", new { account }),
         await ScalarAsync<long>("select count(*) from item_instances where account_id = @account", new { account }),
         await ScalarAsync<long>("select count(*) from item_stacks where account_id = @account", new { account }));

    static (long, long, long) Planned(long account)
    {
        var plan = StarterPlanner.For(account, Catalogue.Document);
        return (plan.Operatives.Count, plan.Instances.Count, plan.Stacks.Count);
    }

    /// <summary>The stored rows are exactly the planner's, with the recorded version (the same check account creation uses).</summary>
    async Task AssertGrantedOnceAsync(long account)
    {
        var plan = StarterPlanner.For(account, Catalogue.Document);
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        Assert.Equal(plan.Operatives.Select(o => (o.DefinitionId, o.OriginRef)).Order(),
            (await conn.QueryAsync<(string, string)>("select definition_id, origin_ref from operatives where account_id = @account", new { account })).Order());
        Assert.Equal(plan.Instances.Select(i => (i.DefinitionId, i.OriginRef)).Order(),
            (await conn.QueryAsync<(string, string)>("select definition_id, origin_ref from item_instances where account_id = @account", new { account })).Order());
        Assert.Equal(plan.Stacks.Select(s => (s.DefinitionId, s.Quantity)).Order(),
            (await conn.QueryAsync<(string, int)>("select definition_id, quantity from item_stacks where account_id = @account", new { account })).Order());
        Assert.Equal((true, Catalogue.Version, 1L),
            await conn.QuerySingleAsync<(bool, string, long)>("select starter_granted, starter_catalogue_version, version from inventories where account_id = @account", new { account }));
    }

    /// <summary>A granting row carries the grant's counts; an already row (counts null) carries exactly granted and catalogueVersion.</summary>
    static void AssertAudit(AuditRow row, bool granted, long account, string catalogueVersion, (long Operatives, long Instances, long Stacks)? counts)
    {
        Assert.Equal(account, row.AccountId);
        Assert.Equal(granted, row.Result.GetProperty("granted").GetBoolean());
        Assert.Equal(catalogueVersion, row.Result.GetProperty("catalogueVersion").GetString());
        if (counts is null)
        {
            Assert.Equal(["catalogueVersion", "granted"], row.Result.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.False(granted);
        }
        else
            Assert.Equal(counts, (row.Result.GetProperty("operatives").GetInt64(), row.Result.GetProperty("instances").GetInt64(), row.Result.GetProperty("stacks").GetInt64()));
    }

    [Fact]
    public async Task Backfill_grants_a_pre_stage_4_account_exactly_once()
    {
        var account = await PreStage4AccountAsync();
        var op = OperatorContext.Create("backfill-op", "grant the starter to a pre-Stage-4 account");

        var first = await store.BackfillStarterAsync(account, op);
        Assert.True(first.Granted);
        Assert.Equal(Planned(account), (first.Grant!.Operatives, first.Grant.Instances, first.Grant.Stacks));
        Assert.Equal(Catalogue.Version, first.CatalogueVersion);
        await AssertGrantedOnceAsync(account);

        var second = await store.BackfillStarterAsync(account, OperatorContext.Create("backfill-op", "again"));
        Assert.False(second.Granted);
        Assert.Equal(Catalogue.Version, second.CatalogueVersion);
        Assert.Null(second.Grant);
        await AssertGrantedOnceAsync(account);
        Assert.Equal(Planned(account), await RowCountsAsync(account));

        var audit = await AuditAsync(account);
        Assert.Equal(2, audit.Count);
        AssertAudit(audit[0], true, account, Catalogue.Version, Planned(account));
        AssertAudit(audit[1], false, account, Catalogue.Version, null);
        Assert.Equal(("backfill-op", "grant the starter to a pre-Stage-4 account", op.CorrelationId), (audit[0].Operator, audit[0].Reason, audit[0].CorrelationId));
        Assert.Equal("again", audit[1].Reason);
    }

    [Fact]
    public async Task A_suspended_account_is_backfilled_too_and_stays_suspended()
    {
        var account = await PreStage4AccountAsync("suspended");
        Assert.True((await store.BackfillStarterAsync(account, Op)).Granted);
        await AssertGrantedOnceAsync(account);
        Assert.Equal("suspended", await ScalarAsync<string>("select status from accounts where account_id = @account", new { account }));
    }

    [Fact]
    public async Task Backfill_of_an_unknown_account_is_not_found_and_audits_nothing()
    {
        await MigrateToLatestAsync();
        var e = await Assert.ThrowsAsync<AccountAdminException>(() => store.BackfillStarterAsync(987654, Op));
        Assert.Equal(AccountAdminError.NotFound, e.Error);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from operator_actions"));
    }

    [Fact]
    public async Task Concurrent_backfills_grant_once()
    {
        const int Callers = 8;
        var account = await PreStage4AccountAsync();
        // The account row is held, so every caller is seen queued on the first statement of its transaction (the pool has 5
        // connections: five wait on the lock, three on the pool); then the lock is released and they run one after another.
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var holdTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(new CommandDefinition("select 1 from accounts where account_id = @account for update", new { account }, holdTx));
        var calls = Enumerable.Range(0, Callers).Select(i =>
            Task.Run(() => store.BackfillStarterAsync(account, OperatorContext.Create("racer", $"race {i}")))).ToArray();
        await WaitForWaitersAsync(5);
        Assert.All(calls, c => Assert.False(c.IsCompleted));
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from operatives"));
        await holdTx.CommitAsync();

        var results = await Task.WhenAll(calls); // none throws
        Assert.Equal(1, results.Count(r => r.Granted));
        Assert.Equal(Callers - 1, results.Count(r => !r.Granted));
        Assert.All(results.Where(r => !r.Granted), r => Assert.Null(r.Grant));
        Assert.Single(results.Select(r => r.CatalogueVersion).Distinct());
        await AssertGrantedOnceAsync(account);
        Assert.Equal(Planned(account), await RowCountsAsync(account));
        var audit = await AuditAsync(account);
        Assert.Equal(Callers, audit.Count);
        Assert.Equal(1, audit.Count(a => a.Result.GetProperty("granted").GetBoolean()));
        Assert.Equal(Callers - 1, audit.Count(a => !a.Result.GetProperty("granted").GetBoolean()));
        Assert.Equal(Callers, audit.Select(a => a.CorrelationId).Distinct().Count());
    }

    /// <summary>Concurrent callers racing for the inventories row itself (the account lock is free): still one grant.</summary>
    [Fact]
    public async Task Backfills_racing_with_no_account_lock_held_still_grant_once()
    {
        var account = await PreStage4AccountAsync();
        var gate = new TaskCompletionSource();
        var calls = Enumerable.Range(0, 5).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await store.BackfillStarterAsync(account, OperatorContext.Create("racer", $"free race {i}"));
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(calls);
        Assert.Equal(1, results.Count(r => r.Granted));
        await AssertGrantedOnceAsync(account);
        Assert.Equal(5, (await AuditAsync(account)).Count);
    }

    async Task WaitForWaitersAsync(int count)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await su.ExecuteScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Fewer than {count} backends waited on a lock.");
            await Task.Yield();
        }
    }

    [Fact]
    public async Task Backfill_of_an_account_created_in_stage_4_grants_nothing()
    {
        await MigrateToLatestAsync();
        var created = await store.CreateAccountAsync("stage-4", null, Op);
        var before = await RowCountsAsync(created.AccountId);

        var result = await store.BackfillStarterAsync(created.AccountId, Op);
        Assert.False(result.Granted);
        Assert.Null(result.Grant); // nothing was granted by this call, so there is nothing to count
        Assert.Equal(Catalogue.Version, result.CatalogueVersion);
        Assert.Equal(before, await RowCountsAsync(created.AccountId));
        await AssertGrantedOnceAsync(created.AccountId);
        var audit = Assert.Single(await AuditAsync(created.AccountId));
        AssertAudit(audit, false, created.AccountId, Catalogue.Version, null);
    }

    [Fact]
    public async Task Backfill_failure_rolls_back_and_a_retry_grants_once()
    {
        var account = await PreStage4AccountAsync();
        await ExecuteAsSuperuserAsync(db,
            """
            create function refuse_stacks() returns trigger language plpgsql as $$ begin raise exception 'backfill failed on purpose'; end $$;
            create trigger refuse_stacks before insert on item_stacks for each row execute function refuse_stacks();
            """);
        var failure = await Assert.ThrowsAnyAsync<PostgresException>(() => store.BackfillStarterAsync(account, Op));
        Assert.Contains("backfill failed on purpose", failure.MessageText);
        Assert.Equal(0L, await ScalarAsync<long>(
            "select (select count(*) from inventories) + (select count(*) from operatives) + (select count(*) from item_instances) + (select count(*) from item_stacks) + (select count(*) from operator_actions)"));

        await ExecuteAsSuperuserAsync(db, "drop trigger refuse_stacks on item_stacks");
        var retry = await store.BackfillStarterAsync(account, Op);
        Assert.True(retry.Granted);
        await AssertGrantedOnceAsync(account);
        Assert.Equal(Planned(account), await RowCountsAsync(account));
        AssertAudit(Assert.Single(await AuditAsync(account)), true, account, Catalogue.Version, Planned(account));
    }

    [Fact]
    public async Task A_failed_audit_rolls_the_grant_back()
    {
        var account = await PreStage4AccountAsync();
        await ExecuteAsSuperuserAsync(db,
            """
            create function refuse_audit() returns trigger language plpgsql as $$ begin raise exception 'audit failed on purpose'; end $$;
            create trigger refuse_audit before insert on operator_actions for each row execute function refuse_audit();
            """);
        await Assert.ThrowsAnyAsync<PostgresException>(() => store.BackfillStarterAsync(account, Op));
        Assert.Equal(0L, await ScalarAsync<long>("select (select count(*) from inventories) + (select count(*) from operatives) + (select count(*) from item_instances) + (select count(*) from item_stacks)"));
    }

    /// <summary>The embedded catalogue minus its second starter operative: valid, different version, different starter.</summary>
    static GameCatalogue ChangedCatalogue()
    {
        var document = Catalogue.Document;
        var changed = GameCatalogue.Load(CanonicalCatalogueJson.ToBytes(document with { Starter = document.Starter with { Operatives = document.Starter.Operatives.Take(1).ToList() } }));
        Assert.NotEqual(Catalogue.Version, changed.Version);
        return changed;
    }

    [Fact]
    public async Task Backfill_records_the_catalogue_version_and_never_regrants_after_a_catalogue_change()
    {
        var account = await PreStage4AccountAsync();
        var changed = ChangedCatalogue();
        var changedStore = new AccountAdminStore(db.AsApp(), changed);
        Assert.NotEqual(StarterPlanner.For(account, Catalogue.Document).Operatives.Count, StarterPlanner.For(account, changed.Document).Operatives.Count);

        Assert.True((await store.BackfillStarterAsync(account, Op)).Granted);
        var rows = await RowCountsAsync(account);
        Assert.Equal(Catalogue.Version, await ScalarAsync<string>("select starter_catalogue_version from inventories where account_id = @account", new { account }));

        var later = await changedStore.BackfillStarterAsync(account, Op);
        Assert.False(later.Granted);
        Assert.Equal(Catalogue.Version, later.CatalogueVersion); // as granted, not the catalogue this store holds
        Assert.Equal(rows, await RowCountsAsync(account));
        Assert.Equal(Catalogue.Version, await ScalarAsync<string>("select starter_catalogue_version from inventories where account_id = @account", new { account }));
        await AssertGrantedOnceAsync(account);
        var audit = await AuditAsync(account);
        AssertAudit(audit[1], false, account, Catalogue.Version, null);
    }

    [Fact]
    public async Task The_catalogue_a_backfill_grants_from_is_the_one_recorded()
    {
        var account = await PreStage4AccountAsync();
        var changed = ChangedCatalogue();
        var granted = await new AccountAdminStore(db.AsApp(), changed).BackfillStarterAsync(account, Op);
        Assert.True(granted.Granted);
        Assert.Equal(changed.Version, granted.CatalogueVersion);
        Assert.Equal(changed.Version, await ScalarAsync<string>("select starter_catalogue_version from inventories where account_id = @account", new { account }));
        Assert.Equal(StarterPlanner.For(account, changed.Document).Operatives.Count, (int)await ScalarAsync<long>("select count(*) from operatives where account_id = @account", new { account }));

        Assert.False((await store.BackfillStarterAsync(account, Op)).Granted); // the embedded catalogue replaces nothing
        Assert.Equal(changed.Version, await ScalarAsync<string>("select starter_catalogue_version from inventories where account_id = @account", new { account }));
        Assert.Equal(StarterPlanner.For(account, changed.Document).Operatives.Count, (int)await ScalarAsync<long>("select count(*) from operatives where account_id = @account", new { account }));
    }

    [Fact]
    public async Task Describe_reports_whether_the_starter_is_granted()
    {
        var account = await PreStage4AccountAsync();
        Assert.False((await store.DescribeAccountAsync(account))!.StarterGranted);
        await store.BackfillStarterAsync(account, Op);
        Assert.True((await store.DescribeAccountAsync(account))!.StarterGranted);
        var created = await store.CreateAccountAsync(null, null, Op);
        Assert.True((await store.DescribeAccountAsync(created.AccountId))!.StarterGranted);
    }

    /// <summary>
    /// Moves one operative's medkit stack to the stash (a batch through the edit store), so the account's current stack rows
    /// no longer equal what the starter granted; returns the grant's stack count and the current one.
    /// </summary>
    async Task<(long Granted, long Now)> DivergeFromTheGrantAsync(long account, GameCatalogue forEdits)
    {
        var granted = Planned(account).Item3;
        var operative = await ScalarAsync<long>("select min(operative_id) from operatives where account_id = @account", new { account });
        var quantity = await ScalarAsync<int>("select quantity from item_stacks where account_id = @account and operative_id = @operative and definition_id = 'item.medkit'", new { account, operative });
        var edits = new Blackglass.Accounts.Persistence.Player.InventoryEditStore(db.AsApp(), forEdits);
        await edits.ApplyAsync(account, 1, [new MoveStack(operative, "item.medkit", quantity, Destination.Stash)]);
        var now = (await RowCountsAsync(account)).Stacks;
        Assert.NotEqual(granted, now);
        return (granted, now);
    }

    [Fact]
    public async Task The_already_audit_row_has_no_holdings_counts()
    {
        var account = await PreStage4AccountAsync();
        await store.BackfillStarterAsync(account, Op);
        await DivergeFromTheGrantAsync(account, Catalogue);

        await store.BackfillStarterAsync(account, OperatorContext.Create("backfill-op", "again after an edit"));

        var audit = await AuditAsync(account);
        Assert.Equal(2, audit.Count);
        AssertAudit(audit[0], true, account, Catalogue.Version, Planned(account)); // what was granted, still the audited record
        AssertAudit(audit[1], false, account, Catalogue.Version, null);            // exactly granted and catalogueVersion
    }

    [Fact]
    public async Task A_granting_backfill_still_reports_the_grant_counts()
    {
        var account = await PreStage4AccountAsync();
        var changed = ChangedCatalogue();
        var result = await new AccountAdminStore(db.AsApp(), changed).BackfillStarterAsync(account, Op);

        Assert.True(result.Granted);
        Assert.Equal(changed.Version, result.CatalogueVersion);
        var plan = StarterPlanner.For(account, changed.Document);
        Assert.Equal((plan.Operatives.Count, plan.Instances.Count, plan.Stacks.Count), (result.Grant!.Operatives, result.Grant.Instances, result.Grant.Stacks));
        AssertAudit(Assert.Single(await AuditAsync(account)), true, account, changed.Version, (plan.Operatives.Count, plan.Instances.Count, plan.Stacks.Count));
    }

    [Fact]
    public async Task An_already_granted_backfill_reports_the_recorded_version_and_no_counts()
    {
        var account = await PreStage4AccountAsync();
        var changed = ChangedCatalogue();
        Assert.True((await new AccountAdminStore(db.AsApp(), changed).BackfillStarterAsync(account, Op)).Granted); // the recorded version is changed's
        var (granted, now) = await DivergeFromTheGrantAsync(account, changed);
        Assert.NotEqual(Catalogue.Version, changed.Version);

        var later = await store.BackfillStarterAsync(account, Op); // this store holds the embedded catalogue and the account has since changed
        Assert.False(later.Granted);
        Assert.Equal(changed.Version, later.CatalogueVersion); // the recorded version, not this store's catalogue
        Assert.Equal(changed.Version, await ScalarAsync<string>("select starter_catalogue_version from inventories where account_id = @account", new { account }));
        Assert.Null(later.Grant); // and no counts: neither the grant's nor the current holdings (which now differ from it)
        Assert.NotEqual(granted, now);
    }

    // --- the CLI ---------------------------------------------------------------------------------------------------------

    async Task<(int Code, string Out, string Err)> CliAsync(string? typed, Dictionary<string, string?>? env, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await Cli.RunAsync(["admin", .. args], env ?? db.EnvFor("app"), output, error, input: typed is null ? null : new StringReader(typed));
        return (code, output.ToString(), error.ToString());
    }

    static string Field(string output, string name) => Regex.Match(output, $"(?m)^{Regex.Escape(name)}=(.*)$").Groups[1].Value.TrimEnd('\r');

    /// <summary>The already output names no counts at all: current holdings are never printed under the grant's labels.</summary>
    static void AssertNoHoldingsLines(string output)
    {
        foreach (var name in new[] { "operatives", "instances", "stacks", "starterOperatives", "starterInstances", "starterStacks" })
            Assert.DoesNotMatch($"(?m)^{name}=", output);
    }

    static string[] Backfill(long account, params string[] more) =>
        ["starter", "backfill", "--account-base58", AccountId.Format(account), "--operator", "ops", "--reason", "backfill test", .. more];

    [Fact]
    public async Task The_cli_previews_and_needs_confirmation()
    {
        var account = await PreStage4AccountAsync();
        var id = AccountId.Format(account);

        var preview = await CliAsync(null, null, Backfill(account));
        Assert.Equal(ExitCodes.Usage, preview.Code);
        Assert.Equal((id, "active", "false"), (Field(preview.Out, "target.accountId"), Field(preview.Out, "target.status"), Field(preview.Out, "target.starterGranted")));
        Assert.DoesNotContain("correlationId=", preview.Out);
        Assert.Contains("--yes", preview.Err);
        Assert.Contains("nothing was changed", preview.Err);
        Assert.Equal((0L, 0L, 0L), await RowCountsAsync(account));
        Assert.Empty(await AuditAsync());

        var wrong = await CliAsync("no\n", null, Backfill(account));
        Assert.Equal(ExitCodes.NotConfirmed, wrong.Code);
        Assert.Equal((0L, 0L, 0L), await RowCountsAsync(account));

        var typed = await CliAsync(id + "\n", null, Backfill(account));
        Assert.Equal(ExitCodes.Ok, typed.Code);
        Assert.Equal("granted", Field(typed.Out, "starterGranted"));
        await AssertGrantedOnceAsync(account);
        Assert.Single(await AuditAsync());
    }

    [Fact]
    public async Task The_cli_with_yes_grants_then_reports_already()
    {
        var account = await PreStage4AccountAsync("suspended");
        var plan = Planned(account);

        var first = await CliAsync(null, null, Backfill(account, "--yes"));
        Assert.Equal(ExitCodes.Ok, first.Code);
        Assert.Equal("suspended", Field(first.Out, "target.status"));
        Assert.Equal("false", Field(first.Out, "target.starterGranted"));
        Assert.Equal(AccountId.Format(account), Field(first.Out, "accountId"));
        Assert.Equal("granted", Field(first.Out, "starterGranted"));
        Assert.Equal((plan.Item1.ToString(), plan.Item2.ToString(), plan.Item3.ToString()),
            (Field(first.Out, "operatives"), Field(first.Out, "instances"), Field(first.Out, "stacks")));
        Assert.Equal(Catalogue.Version, Field(first.Out, "catalogueVersion"));
        var audit = await AuditAsync(account);
        Assert.Equal(Guid.Parse(Field(first.Out, "correlationId")), Assert.Single(audit).CorrelationId);

        var second = await CliAsync(null, null, Backfill(account, "--yes"));
        Assert.Equal(ExitCodes.Ok, second.Code);
        Assert.Equal("true", Field(second.Out, "target.starterGranted"));
        Assert.Equal("already", Field(second.Out, "starterGranted"));
        Assert.Equal(Catalogue.Version, Field(second.Out, "catalogueVersion")); // the recorded one
        AssertNoHoldingsLines(second.Out);
        await AssertGrantedOnceAsync(account);
        Assert.Equal(2, (await AuditAsync(account)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unknown_account_is_65(bool yes)
    {
        await MigrateToLatestAsync();
        var missing = Backfill(987654, yes ? ["--yes"] : []);
        var result = await CliAsync(null, null, missing);
        Assert.Equal(ExitCodes.NotFound, result.Code);
        Assert.DoesNotContain("correlationId=", result.Out);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from operator_actions"));
    }

    [Fact]
    public async Task Backfill_waits_are_bounded()
    {
        var account = await PreStage4AccountAsync();
        var env = db.EnvFor("app");
        env[DbTimeouts.LockTimeoutVar] = "300";
        await using var holder = await db.AsSuperuser().OpenConnectionAsync();
        await using var holdTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(new CommandDefinition("select 1 from accounts where account_id = @account for update", new { account }, holdTx));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await CliAsync(null, env, Backfill(account, "--yes")).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"the command waited {clock.Elapsed}");
        Assert.Equal(ExitCodes.Busy, result.Code);
        Assert.Contains("nothing was changed", result.Err);
        Assert.DoesNotContain("correlationId=", result.Out);
        await holdTx.RollbackAsync();

        Assert.Equal((0L, 0L, 0L), await RowCountsAsync(account));
        Assert.Empty(await AuditAsync());
        Assert.True((await store.BackfillStarterAsync(account, Op)).Granted); // and a retry works
    }
}
