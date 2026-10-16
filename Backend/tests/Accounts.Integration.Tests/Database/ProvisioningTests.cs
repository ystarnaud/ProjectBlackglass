using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Player;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Atomic starter provisioning during account creation (owner ruling D4) and mint-or-find by origin reference
/// (D3), against a real PostgreSQL 17: rows equal the planner's plan, a failure rolls the whole account back, and
/// concurrent or repeated mints of one reference converge on one id.</summary>
[Trait("Requires", "Postgres")]
public class ProvisioningTests : IAsyncLifetime
{
    static readonly string AdminKey = NewAdminKey();
    static readonly GameCatalogue Catalogue = GameCatalogue.Embedded;
    static readonly string OperativeDefinition = Catalogue.Document.Operatives[0].Id;
    const string ItemDefinition = "item.service-rifle";
    TestDatabase db = null!;
    AccountAdminStore store = null!;

    public async Task InitializeAsync() { db = await MigratedAsync(); store = new AccountAdminStore(db.AsApp()); }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    Task<T> ScalarAsync<T>(string sql, object? args = null) => ScalarAsSuperuserAsync<T>(db, sql, args);

    sealed record OperativeRow(string DefinitionId, string Origin, string OriginRef);
    sealed record InstanceRow(string DefinitionId, string? OperativeDefinitionId, string? EquippedSlot, string Origin, string OriginRef);
    sealed record StackRow(string? OperativeDefinitionId, string DefinitionId, int Quantity);

    /// <summary>Asserts the stored rows of one account are exactly the planner's plan, and that every equipped item sits in
    /// the slot the catalogue gives its definition.</summary>
    async Task AssertStarterRowsAsync(long accountId)
    {
        var plan = StarterPlanner.For(accountId, Catalogue.Document);
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var operatives = (await conn.QueryAsync<OperativeRow>(
            "select definition_id as DefinitionId, origin as Origin, origin_ref as OriginRef from operatives where account_id = @accountId", new { accountId })).ToList();
        var instances = (await conn.QueryAsync<InstanceRow>(
            """
            select i.definition_id as DefinitionId, o.definition_id as OperativeDefinitionId, i.equipped_slot as EquippedSlot, i.origin as Origin, i.origin_ref as OriginRef
            from item_instances i left join operatives o on o.operative_id = i.operative_id where i.account_id = @accountId
            """, new { accountId })).ToList();
        var stacks = (await conn.QueryAsync<StackRow>(
            """
            select o.definition_id as OperativeDefinitionId, s.definition_id as DefinitionId, s.quantity as Quantity
            from item_stacks s left join operatives o on o.operative_id = s.operative_id where s.account_id = @accountId
            """, new { accountId })).ToList();

        Assert.Equal(plan.Operatives.Select(o => (o.DefinitionId, "starter", o.OriginRef)).Order(),
            operatives.Select(o => (o.DefinitionId, o.Origin, o.OriginRef)).Order());
        Assert.Equal(plan.Instances.Select(i => (i.DefinitionId, i.OperativeDefinitionId, i.EquippedSlot, "starter", i.OriginRef)).Order(),
            instances.Select(i => (i.DefinitionId, i.OperativeDefinitionId, i.EquippedSlot, i.Origin, i.OriginRef)).Order());
        Assert.Equal(plan.Stacks.Select(s => (s.OperativeDefinitionId, s.DefinitionId, s.Quantity)).Order(),
            stacks.Select(s => (s.OperativeDefinitionId, s.DefinitionId, s.Quantity)).Order());

        Assert.Contains(instances, i => i.EquippedSlot is not null);
        foreach (var equipped in instances.Where(i => i.EquippedSlot is not null))
            Assert.Equal(Catalogue.FindItem(equipped.DefinitionId)!.Slot.ToString().ToLowerInvariant(), equipped.EquippedSlot);

        var inventory = await conn.QuerySingleAsync<(bool Granted, string Version, long InventoryVersion)>(
            "select starter_granted as Granted, starter_catalogue_version as Version, version as InventoryVersion from inventories where account_id = @accountId", new { accountId });
        Assert.Equal((true, Catalogue.Version, 1L), inventory);
        Assert.NotNull(await conn.ExecuteScalarAsync<DateTime?>("select starter_granted_at from inventories where account_id = @accountId", new { accountId }));
    }

    [Fact]
    public async Task Creating_an_account_provisions_its_starter_atomically()
    {
        var issued = await store.CreateAccountAsync("starter-1", "Starter One", Op);
        await AssertStarterRowsAsync(issued.AccountId);

        var plan = StarterPlanner.For(issued.AccountId, Catalogue.Document);
        var result = JsonDocument.Parse(await ScalarAsync<string>("select result::text from operator_actions where action = 'account_create'")).RootElement;
        var starter = result.GetProperty("starter");
        Assert.Equal(Catalogue.Version, starter.GetProperty("catalogueVersion").GetString());
        Assert.Equal((plan.Operatives.Count, plan.Instances.Count, plan.Stacks.Count),
            (starter.GetProperty("operatives").GetInt32(), starter.GetProperty("instances").GetInt32(), starter.GetProperty("stacks").GetInt32()));
        Assert.Equal("starter-1", result.GetProperty("handle").GetString());
        // The wallet opens in the same transaction (owner ruling E1), and the audit records it beside the starter.
        var energy = result.GetProperty("energy");
        Assert.Equal((true, 50, 1), (energy.GetProperty("opened").GetBoolean(), energy.GetProperty("balance").GetInt32(), energy.GetProperty("policyVersion").GetInt32()));
        Assert.Equal(new Blackglass.Accounts.Persistence.Energy.WalletOpen(true, 50, 1), issued.Energy);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from energy_ledger where account_id = @a and kind = 'open'", new { a = issued.AccountId }));
    }

    [Fact]
    public async Task Each_account_gets_its_own_references_and_ids()
    {
        var a = await store.CreateAccountAsync(null, null, Op);
        var b = await store.CreateAccountAsync(null, null, Op);
        await AssertStarterRowsAsync(a.AccountId);
        await AssertStarterRowsAsync(b.AccountId);
        Assert.Equal(2 * StarterPlanner.For(a.AccountId, Catalogue.Document).Operatives.Count, await ScalarAsync<long>("select count(distinct operative_id) from operatives"));
    }

    [Fact]
    public async Task Both_the_cli_and_the_admin_route_provision()
    {
        var output = new StringWriter();
        var code = await Cli.RunAsync(["admin", "account", "create", "--handle", "via-cli", "--operator", "ops", "--reason", "test"], db.EnvFor("app"), output, new StringWriter());
        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(AccountId.TryParse(Regex.Match(output.ToString(), "(?m)^accountId=(.*)$").Groups[1].Value.TrimEnd('\r'), out var cliAccount));

        await using var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_ADMIN_ROUTES"] = "enabled", ["BLACKGLASS_DEV_AUTH"] = "enabled" }, adminKey: AdminKey);
        var request = new HttpRequestMessage(HttpMethod.Post, "/dev/v1/accounts") { Content = JsonContent.Create(new { handle = "via-route", @operator = "ops", reason = "test" }) };
        request.Headers.Add("X-Admin-Key", AdminKey);
        var response = await api.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(AccountId.TryParse(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("accountId").GetString(), out var routeAccount));

        Assert.NotEqual(cliAccount, routeAccount);
        await AssertStarterRowsAsync(cliAccount);
        await AssertStarterRowsAsync(routeAccount);
        Assert.Equal(2L, await ScalarAsync<long>("select count(*) from operator_actions where result ? 'starter'"));
    }

    [Fact]
    public async Task A_provisioning_failure_rolls_back_the_whole_account()
    {
        await ExecuteAsSuperuserAsync(db,
            """
            create function refuse_stacks() returns trigger language plpgsql as $$ begin raise exception 'provisioning failed on purpose'; end $$;
            create trigger refuse_stacks before insert on item_stacks for each row execute function refuse_stacks();
            """);
        var failure = await Assert.ThrowsAnyAsync<PostgresException>(() => store.CreateAccountAsync("rolled-back", "Rolled Back", Op));
        Assert.Contains("provisioning failed on purpose", failure.MessageText);
        Assert.Equal(0L, await ScalarAsync<long>(
            """
            select (select count(*) from accounts) + (select count(*) from dev_credentials) + (select count(*) from account_identities)
              + (select count(*) from operator_actions) + (select count(*) from operatives) + (select count(*) from item_instances)
              + (select count(*) from item_stacks) + (select count(*) from inventories) + (select count(*) from energy_wallets)
              + (select count(*) from energy_ledger)
            """));

        await ExecuteAsSuperuserAsync(db, "drop trigger refuse_stacks on item_stacks");
        var issued = await store.CreateAccountAsync("rolled-back", "Rolled Back", Op); // the handle was never taken
        await AssertStarterRowsAsync(issued.AccountId);
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from accounts"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from operator_actions"));
    }

    [Fact]
    public async Task Provisioning_an_account_that_already_has_its_starter_changes_nothing_and_reports_no_holdings_as_a_grant()
    {
        var issued = await store.CreateAccountAsync(null, null, Op);
        var again = await InTransactionAsync((c, t) => StarterProvisioner.ProvisionAsync(c, t, issued.AccountId, Catalogue, default));
        Assert.Equal(new ProvisionResult(1, null, null, null, Catalogue.Version, false), again); // the recorded versions only: current holdings are never a grant
        await AssertStarterRowsAsync(issued.AccountId);
    }

    [Fact]
    public async Task Provisioning_a_bare_account_in_the_callers_transaction_grants_once_and_rolls_back_with_it()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        var account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await StarterProvisioner.ProvisionAsync(conn, tx, account, Catalogue, default);
            await tx.RollbackAsync();
        }
        Assert.Equal(0L, await ScalarAsync<long>("select (select count(*) from operatives) + (select count(*) from inventories)"));
        await using (var tx = await conn.BeginTransactionAsync())
        {
            var first = await StarterProvisioner.ProvisionAsync(conn, tx, account, Catalogue, default);
            Assert.Equal(StarterPlanner.For(account, Catalogue.Document).Operatives.Count, first.Operatives);
            await tx.CommitAsync();
        }
        await AssertStarterRowsAsync(account);
    }

    // --- mint-or-find ---------------------------------------------------------------------------------------------------

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        var account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
        // The mint tests write operatives and items by hand, which the inventory invariant allows only for an account whose starter is granted.
        await ExecuteAsSuperuserAsync(db,
            "insert into inventories (account_id, starter_granted, starter_catalogue_version, starter_granted_at) values (@account, true, @version, now())",
            new { account, version = Catalogue.Version });
        return account;
    }

    async Task<T> InTransactionAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, bool commit = true)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var value = await work(conn, tx);
        if (commit) await tx.CommitAsync(); else await tx.RollbackAsync();
        return value;
    }

    Task<long> MintOperative(NpgsqlConnection c, NpgsqlTransaction t, long account, string reference, string? definition = null) =>
        StarterProvisioner.MintOperativeAsync(c, t, account, definition ?? OperativeDefinition, "starter", reference, default);

    Task<long> MintItem(NpgsqlConnection c, NpgsqlTransaction t, long account, string reference, long? operative = null, string? slot = null, string definition = ItemDefinition) =>
        StarterProvisioner.MintItemAsync(c, t, account, definition, operative, slot, "starter", reference, default);

    [Fact]
    public async Task Minting_the_same_reference_twice_returns_the_same_id()
    {
        var account = await NewAccountAsync();
        var (op1, op2, item1, item2) = await InTransactionAsync(async (c, t) =>
        {
            var o1 = await MintOperative(c, t, account, "twice:op");
            var i1 = await MintItem(c, t, account, "twice:item", o1, "weapon");
            return (o1, await MintOperative(c, t, account, "twice:op"), i1, await MintItem(c, t, account, "twice:item", o1, "weapon"));
        });
        Assert.Equal((op1, item1), (op2, item2));

        // Two sequential transactions: the second finds what the first committed.
        var (op3, item3) = await InTransactionAsync(async (c, t) =>
            (await MintOperative(c, t, account, "twice:op"), await MintItem(c, t, account, "twice:item", op1, "weapon")));
        Assert.Equal((op1, item1), (op3, item3));
        Assert.Equal((1L, 1L), (await ScalarAsync<long>("select count(*) from operatives"), await ScalarAsync<long>("select count(*) from item_instances")));
    }

    [Fact]
    public async Task Concurrent_mints_of_one_reference_converge_on_one_id()
    {
        var account = await NewAccountAsync();
        var operative = await InTransactionAsync((c, t) => MintOperative(c, t, account, "race:setup"));
        var gate = new TaskCompletionSource();
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            return await InTransactionAsync(async (c, t) =>
            {
                var op = await MintOperative(c, t, account, "race:op");
                var item = await MintItem(c, t, account, "race:item", operative, null);
                await Task.Delay(150); // hold the uncommitted rows so the others reach the same reference while they are in flight
                return (op, item);
            });
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(workers);
        Assert.Single(results.Distinct());
        Assert.Equal(2L, await ScalarAsync<long>("select count(*) from operatives")); // the setup operative and the raced one
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from item_instances"));
    }

    /// <summary>Returns once at least <paramref name="count"/> backends of this database wait on a lock (polls pg_stat_activity).</summary>
    async Task WaitUntilBackendsWaitOnLocksAsync(int count)
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await su.ExecuteScalarAsync<long>("select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'") < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Fewer than {count} backends waited on a lock.");
            await Task.Yield();
        }
    }

    /// <summary>A mints and stays open; B mints the same reference and is seen waiting on A's uncommitted row; then A ends.</summary>
    async Task<(long A, long B)> RaceAsync(Func<NpgsqlConnection, NpgsqlTransaction, Task<long>> mint, bool commitFirst)
    {
        await using var connA = await db.AsApp().OpenConnectionAsync();
        await using var txA = await connA.BeginTransactionAsync();
        var a = await mint(connA, txA);
        var b = InTransactionAsync(mint);
        await WaitUntilBackendsWaitOnLocksAsync(1);
        Assert.False(b.IsCompleted); // B cannot finish while A holds its uncommitted row
        if (commitFirst) await txA.CommitAsync(); else await txA.RollbackAsync();
        return (a, await b);
    }

    [Theory]
    [InlineData("operative")]
    [InlineData("item")]
    public async Task A_mint_waiting_on_an_uncommitted_mint_finds_the_row_when_it_commits(string kind)
    {
        var account = await NewAccountAsync();
        var bag = await InTransactionAsync((c, t) => MintOperative(c, t, account, "wait:setup"));
        Func<NpgsqlConnection, NpgsqlTransaction, Task<long>> mint = kind == "operative"
            ? (c, t) => MintOperative(c, t, account, "wait:ref")
            : (c, t) => MintItem(c, t, account, "wait:ref", bag, "weapon");
        var (a, b) = await RaceAsync(mint, commitFirst: true);
        Assert.Equal(a, b);
        Assert.Equal(1L, await ScalarAsync<long>(kind == "operative"
            ? "select count(*) from operatives where origin_ref = 'wait:ref'" : "select count(*) from item_instances where origin_ref = 'wait:ref'"));
    }

    [Theory]
    [InlineData("operative")]
    [InlineData("item")]
    public async Task A_mint_waiting_on_a_mint_that_rolls_back_inserts_its_own_row(string kind)
    {
        var account = await NewAccountAsync();
        var bag = await InTransactionAsync((c, t) => MintOperative(c, t, account, "wait:setup"));
        Func<NpgsqlConnection, NpgsqlTransaction, Task<long>> mint = kind == "operative"
            ? (c, t) => MintOperative(c, t, account, "wait:ref")
            : (c, t) => MintItem(c, t, account, "wait:ref", bag, "weapon");
        var (a, b) = await RaceAsync(mint, commitFirst: false);
        Assert.NotEqual(a, b);
        Assert.Equal(b, await ScalarAsync<long>(kind == "operative"
            ? "select operative_id from operatives where origin_ref = 'wait:ref'" : "select item_id from item_instances where origin_ref = 'wait:ref'"));
        Assert.Equal(1L, await ScalarAsync<long>(kind == "operative"
            ? "select count(*) from operatives where origin_ref = 'wait:ref'" : "select count(*) from item_instances where origin_ref = 'wait:ref'"));
    }

    // --- concurrent provisioning of one account ---------------------------------------------------------------------------

    /// <summary>Transaction 0 provisions and stays open; N-1 more provisioners are seen waiting (on the inventories row's
    /// unique key when there is none yet, on its row lock when an ungranted one exists); then transaction 0 commits.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_provisioning_of_one_account_grants_exactly_once(bool inventoryRowExists)
    {
        const int Callers = 5;
        long account;
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
            if (inventoryRowExists) await conn.ExecuteAsync("insert into inventories (account_id) values (@account)", new { account });
        }
        await using var first = await db.AsApp().OpenConnectionAsync();
        await using var firstTx = await first.BeginTransactionAsync();
        var granted = await StarterProvisioner.ProvisionAsync(first, firstTx, account, Catalogue, default);

        var others = Enumerable.Range(1, Callers - 1)
            .Select(_ => InTransactionAsync((c, t) => StarterProvisioner.ProvisionAsync(c, t, account, Catalogue, default))).ToArray();
        await WaitUntilBackendsWaitOnLocksAsync(Callers - 1);
        Assert.All(others, o => Assert.False(o.IsCompleted));
        await firstTx.CommitAsync();

        var results = await Task.WhenAll(others); // none throws: each sees the committed grant
        var plan = StarterPlanner.For(account, Catalogue.Document);
        Assert.Equal(new ProvisionResult(1, plan.Operatives.Count, plan.Instances.Count, plan.Stacks.Count, Catalogue.Version, true), granted);
        Assert.All(results, r => Assert.Equal(granted with { Operatives = null, Instances = null, Stacks = null, Granted = false }, r));
        await AssertStarterRowsAsync(account);
        Assert.Equal(plan.Operatives.Count, (int)await ScalarAsync<long>("select count(*) from operatives"));
        Assert.Equal(plan.Instances.Count, (int)await ScalarAsync<long>("select count(*) from item_instances"));
        Assert.Equal(plan.Stacks.Count, (int)await ScalarAsync<long>("select count(*) from item_stacks"));
    }

    /// <summary>The inventories row lock is what serializes provisioners of one account (so nothing is minted before it is held).
    /// Transaction 0 takes only that lock (no new row version, so the others' INSERT ... ON CONFLICT DO NOTHING passes) and
    /// the others are seen waiting on it before it provisions: without FOR UPDATE they would read "not granted" and mint
    /// alongside it instead of waiting.</summary>
    [Fact]
    public async Task Provisioners_of_one_account_wait_on_the_inventories_row_lock_before_minting()
    {
        const int Callers = 5;
        long account;
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
            await conn.ExecuteAsync("insert into inventories (account_id) values (@account)", new { account });
        }
        await using var first = await db.AsApp().OpenConnectionAsync();
        await using var firstTx = await first.BeginTransactionAsync();
        await first.ExecuteAsync(new CommandDefinition("select 1 from inventories where account_id = @account for update", new { account }, firstTx));

        var others = Enumerable.Range(1, Callers - 1)
            .Select(_ => InTransactionAsync((c, t) => StarterProvisioner.ProvisionAsync(c, t, account, Catalogue, default))).ToArray();
        await WaitUntilBackendsWaitOnLocksAsync(Callers - 1);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from operatives")); // nobody minted while waiting
        var granted = await StarterProvisioner.ProvisionAsync(first, firstTx, account, Catalogue, default);
        await firstTx.CommitAsync();

        var results = await Task.WhenAll(others); // none throws: each finds the committed grant
        Assert.True(granted.Granted);
        Assert.All(results, r => Assert.Equal(granted with { Operatives = null, Instances = null, Stacks = null, Granted = false }, r));
        await AssertStarterRowsAsync(account);
    }

    [Fact]
    public async Task A_mint_after_a_rolled_back_mint_creates_one_row()
    {
        var account = await NewAccountAsync();
        await InTransactionAsync((c, t) => MintOperative(c, t, account, "undone:op"), commit: false);
        Assert.Equal(0L, await ScalarAsync<long>("select count(*) from operatives"));
        var id = await InTransactionAsync((c, t) => MintOperative(c, t, account, "undone:op"));
        Assert.Equal(1L, await ScalarAsync<long>("select count(*) from operatives"));
        Assert.Equal(id, await ScalarAsync<long>("select operative_id from operatives where origin_ref = 'undone:op'"));
    }

    [Fact]
    public async Task A_reference_held_by_another_account_is_a_collision_not_a_find()
    {
        var first = await NewAccountAsync();
        var second = await NewAccountAsync();
        var operative = await InTransactionAsync((c, t) => MintOperative(c, t, first, "shared:op"));
        await InTransactionAsync((c, t) => MintItem(c, t, first, "shared:item", operative, "weapon"));

        async Task Collides(Func<NpgsqlConnection, NpgsqlTransaction, Task<long>> mint)
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => InTransactionAsync(mint));
            Assert.Equal("origin reference collision", e.Message);
        }
        await Collides((c, t) => MintOperative(c, t, second, "shared:op"));
        await Collides((c, t) => MintOperative(c, t, first, "shared:op", "00000000-0000-4000-8000-000000000009")); // another definition
        await Collides((c, t) => MintItem(c, t, second, "shared:item", null, null));
        await Collides((c, t) => MintItem(c, t, first, "shared:item", operative, "weapon", "item.combat-blade")); // another definition
        await Collides((c, t) => MintItem(c, t, first, "shared:item", operative, "armor")); // another slot
        await Collides((c, t) => MintItem(c, t, first, "shared:item", operative, null)); // unequipped instead of equipped
        await Collides((c, t) => MintItem(c, t, first, "shared:item", null, null)); // the stash instead of the bag

        Assert.Equal((1L, 1L), (await ScalarAsync<long>("select count(*) from operatives"), await ScalarAsync<long>("select count(*) from item_instances")));
    }

    /// <summary>Each case differs from the committed row in exactly one field, so a check that ignores that field passes it.</summary>
    [Fact]
    public async Task Each_found_row_field_is_checked_on_its_own()
    {
        var first = await NewAccountAsync();
        var second = await NewAccountAsync();
        var bag = await InTransactionAsync((c, t) => MintOperative(c, t, first, "iso:op"));
        var otherBag = await InTransactionAsync((c, t) => MintOperative(c, t, first, "iso:op2")); // the same account's other operative
        await InTransactionAsync((c, t) => MintItem(c, t, first, "iso:equipped", bag, "weapon"));
        await InTransactionAsync((c, t) => MintItem(c, t, first, "iso:bag", bag));
        await InTransactionAsync((c, t) => MintItem(c, t, first, "iso:stash", null));

        async Task Collides(Func<NpgsqlConnection, NpgsqlTransaction, Task<long>> mint)
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => InTransactionAsync(mint));
            Assert.Equal("origin reference collision", e.Message);
        }
        // operative_id alone: same account, definition and slot, another operative of the account
        await Collides((c, t) => MintItem(c, t, first, "iso:equipped", otherBag, "weapon"));
        // operative_id alone, unequipped: a bag item re-minted in another bag and as a stash item; a stash item re-minted in a bag
        await Collides((c, t) => MintItem(c, t, first, "iso:bag", otherBag));
        await Collides((c, t) => MintItem(c, t, first, "iso:bag", null));
        await Collides((c, t) => MintItem(c, t, first, "iso:stash", bag));
        // equipped_slot alone: the same operative, definition and account
        await Collides((c, t) => MintItem(c, t, first, "iso:equipped", bag, "armor"));
        await Collides((c, t) => MintItem(c, t, first, "iso:equipped", bag, null));
        await Collides((c, t) => MintItem(c, t, first, "iso:bag", bag, "weapon"));
        // definition alone
        await Collides((c, t) => MintItem(c, t, first, "iso:equipped", bag, "weapon", "item.combat-blade"));
        await Collides((c, t) => MintItem(c, t, first, "iso:stash", null, null, "item.combat-blade"));
        // account alone: a stash item has no operative, so another account can mint the same shape (the composite foreign key
        // makes the same test impossible for a bag item: another account cannot name this account's operative)
        await Collides((c, t) => MintItem(c, t, second, "iso:stash", null));
        // the shapes that match exactly are a find, not a collision
        Assert.Equal(await ScalarAsync<long>("select item_id from item_instances where origin_ref = 'iso:equipped'"),
            await InTransactionAsync((c, t) => MintItem(c, t, first, "iso:equipped", bag, "weapon")));
        Assert.Equal(await ScalarAsync<long>("select item_id from item_instances where origin_ref = 'iso:stash'"),
            await InTransactionAsync((c, t) => MintItem(c, t, first, "iso:stash", null)));
        Assert.Equal(3L, await ScalarAsync<long>("select count(*) from item_instances"));
    }

    [Fact]
    public async Task Identical_starter_items_get_distinct_ids()
    {
        var issued = await store.CreateAccountAsync(null, null, Op);
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var rifles = (await su.QueryAsync<(long Id, string Reference)>(
            "select item_id as Id, origin_ref as Reference from item_instances where account_id = @a and definition_id = @d", new { a = issued.AccountId, d = ItemDefinition })).ToList();
        Assert.True(rifles.Count >= 2);
        Assert.Equal(rifles.Count, rifles.Select(r => r.Id).Distinct().Count());
        Assert.Equal(rifles.Count, rifles.Select(r => r.Reference).Distinct().Count());

        // The same definition in the same place differs only by its counter.
        var account = await NewAccountAsync();
        var operative = await InTransactionAsync((c, t) => MintOperative(c, t, account, "pair:op"));
        var (n0, n1) = await InTransactionAsync(async (c, t) => (await MintItem(c, t, account, "pair:item:n0", operative), await MintItem(c, t, account, "pair:item:n1", operative)));
        Assert.NotEqual(n0, n1);
    }
}
