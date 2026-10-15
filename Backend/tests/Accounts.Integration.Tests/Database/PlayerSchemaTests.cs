using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Migrations 0003 and 0004 (Stage 4) against a real PostgreSQL 17: bigint identities minted only by PostgreSQL,
/// unique origin references, composite ownership, one item per slot, stacks, the starter-granted state, the app role's
/// least-privilege grants, the extended audit action list and the inventory invariant.</summary>
[Trait("Requires", "Postgres")]
public class PlayerSchemaTests : IAsyncLifetime
{
    TestDatabase db = null!;
    const string Operative = "00000000-0000-4000-8000-000000000001";
    const string Item = "00000000-0000-4000-8000-0000000000a1";
    const string Catalogue = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public async Task InitializeAsync()
    {
        db = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(db.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync();
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    static async Task<string> SqlStateOf(NpgsqlDataSource ds, string sql, object? args = null)
    {
        await using var conn = await ds.OpenConnectionAsync();
        var e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, args));
        return e.SqlState;
    }

    static string NewRef() => Guid.NewGuid().ToString();

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    async Task<long> NewOperativeAsync(long account, string? origin = null)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(
            "insert into operatives (account_id, definition_id, origin, origin_ref) values (@account, @Operative, 'starter', @r) returning operative_id",
            new { account, Operative, r = origin ?? NewRef() });
    }

    async Task<long> NewItemAsync(long account, long? operative = null, string? slot = null, string origin = "starter", string? originRef = null)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(
            "insert into item_instances (account_id, definition_id, operative_id, equipped_slot, origin, origin_ref) values (@account, @Item, @operative, @slot, @origin, @r) returning item_id",
            new { account, Item, operative, slot, origin, r = originRef ?? NewRef() });
    }

    async Task GrantStarterAsync(long account)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into inventories (account_id) values (@account)", new { account });
        await conn.ExecuteAsync(
            "update inventories set starter_granted = true, starter_catalogue_version = @Catalogue, starter_granted_at = now(), version = version + 1 where account_id = @account",
            new { account, Catalogue });
    }

    async Task<T> SuperScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    // ---- ids ----

    [Fact]
    public async Task PostgreSQL_mints_sequential_bigint_operative_and_item_ids()
    {
        var a = await NewAccountAsync();
        var operatives = new List<long> { await NewOperativeAsync(a), await NewOperativeAsync(a), await NewOperativeAsync(a) };
        var items = new List<long> { await NewItemAsync(a), await NewItemAsync(a), await NewItemAsync(a) };
        Assert.Equal(new long[] { 1, 2, 3 }, operatives);
        Assert.Equal(new long[] { 1, 2, 3 }, items);
        foreach (var (table, column) in new[] { ("operatives", "operative_id"), ("item_instances", "item_id") })
        {
            Assert.Equal("bigint", await SuperScalarAsync<string>("select data_type from information_schema.columns where table_name = @table and column_name = @column", new { table, column }));
            Assert.Equal("ALWAYS", await SuperScalarAsync<string>("select identity_generation from information_schema.columns where table_name = @table and column_name = @column", new { table, column }));
        }
    }

    [Theory]
    [InlineData("insert into operatives (operative_id, account_id, definition_id, origin, origin_ref) overriding system value values (999, @a, '00000000-0000-4000-8000-000000000001', 'starter', 'x')", "42501")]
    [InlineData("insert into operatives (operative_id, account_id, definition_id, origin, origin_ref) values (999, @a, '00000000-0000-4000-8000-000000000001', 'starter', 'x')", "428C9")]
    [InlineData("insert into item_instances (item_id, account_id, definition_id, origin, origin_ref) overriding system value values (999, @a, '00000000-0000-4000-8000-0000000000a1', 'starter', 'x')", "42501")]
    [InlineData("insert into item_instances (item_id, account_id, definition_id, origin, origin_ref) values (999, @a, '00000000-0000-4000-8000-0000000000a1', 'starter', 'x')", "428C9")]
    [InlineData("select nextval('operatives_operative_id_seq')", "42501")]
    [InlineData("select nextval('item_instances_item_id_seq')", "42501")]
    [InlineData("select setval('operatives_operative_id_seq', 1)", "42501")]
    [InlineData("select setval('item_instances_item_id_seq', 1)", "42501")]
    public async Task The_app_cannot_choose_an_operative_or_item_id(string sql, string sqlState)
    {
        var a = await NewAccountAsync();
        Assert.Equal(sqlState, await SqlStateOf(db.AsApp(), sql, new { a }));
    }

    [Fact]
    public async Task The_app_cannot_set_experience_or_creation_times_on_insert()
    {
        var a = await NewAccountAsync();
        Assert.Equal("42501", await SqlStateOf(db.AsApp(),
            "insert into operatives (account_id, definition_id, origin, origin_ref, experience) values (@a, @Operative, 'starter', 'x', 500)", new { a, Operative }));
        Assert.Equal("42501", await SqlStateOf(db.AsApp(),
            "insert into item_instances (account_id, definition_id, origin, origin_ref, created_at) values (@a, @Item, 'starter', 'x', '2000-01-01')", new { a, Item }));
    }

    // ---- composite ownership ----

    [Fact]
    public async Task An_item_cannot_be_placed_with_another_accounts_operative()
    {
        var a = await NewAccountAsync();
        var b = await NewAccountAsync();
        var operativeOfA = await NewOperativeAsync(a);
        Assert.Equal("23503", await SqlStateOf(db.AsApp(),
            "insert into item_instances (account_id, definition_id, operative_id, origin, origin_ref) values (@b, @Item, @operativeOfA, 'starter', 'x')",
            new { b, Item, operativeOfA }));
        await NewItemAsync(a, operativeOfA); // its own owner may
    }

    [Fact]
    public async Task A_stack_cannot_be_placed_with_another_accounts_operative()
    {
        var a = await NewAccountAsync();
        var b = await NewAccountAsync();
        var operativeOfA = await NewOperativeAsync(a);
        Assert.Equal("23503", await SqlStateOf(db.AsApp(),
            "insert into item_stacks (account_id, operative_id, definition_id, quantity) values (@b, @operativeOfA, @Item, 5)",
            new { b, Item, operativeOfA }));
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into item_stacks (account_id, operative_id, definition_id, quantity) values (@a, @operativeOfA, @Item, 5)", new { a, operativeOfA, Item });
    }

    [Fact]
    public async Task Rows_need_an_existing_account_and_choices_an_existing_operative()
    {
        Assert.Equal("23503", await SqlStateOf(db.AsApp(), "insert into operatives (account_id, definition_id, origin, origin_ref) values (999, @Operative, 'starter', 'x')", new { Operative }));
        Assert.Equal("23503", await SqlStateOf(db.AsApp(), "insert into inventories (account_id) values (999)"));
        Assert.Equal("23503", await SqlStateOf(db.AsApp(), "insert into item_instances (account_id, definition_id, origin, origin_ref) values (999, @Item, 'starter', 'x')", new { Item }));
        Assert.Equal("23503", await SqlStateOf(db.AsApp(), "insert into item_stacks (account_id, definition_id, quantity) values (999, @Item, 1)", new { Item }));
        Assert.Equal("23503", await SqlStateOf(db.AsApp(), "insert into operative_choices (operative_id, ordinal, choice_id) values (999, 0, 'choice-one')"));
    }

    // ---- slots, stacks and minting ----

    [Fact]
    public async Task One_item_per_slot_per_operative()
    {
        var a = await NewAccountAsync();
        var first = await NewOperativeAsync(a);
        var second = await NewOperativeAsync(a);
        await NewItemAsync(a, first, "weapon");
        await NewItemAsync(a, first, "armor");                // another slot is fine
        await NewItemAsync(a, second, "weapon");              // another operative's slot is fine
        await NewItemAsync(a, first);                         // any number of unequipped items in a bag
        await NewItemAsync(a, first);
        await NewItemAsync(a);                                // and in the stash
        await NewItemAsync(a);
        Assert.Equal("23505", await SqlStateOf(db.AsApp(),
            "insert into item_instances (account_id, definition_id, operative_id, equipped_slot, origin, origin_ref) values (@a, @Item, @first, 'weapon', 'starter', 'another')",
            new { a, Item, first }));
    }

    [Fact]
    public async Task An_equipped_item_must_be_in_a_bag()
    {
        var a = await NewAccountAsync();
        Assert.Equal("23514", await SqlStateOf(db.AsApp(),
            "insert into item_instances (account_id, definition_id, equipped_slot, origin, origin_ref) values (@a, @Item, 'weapon', 'starter', 'x')", new { a, Item }));
        Assert.Equal("23514", await SqlStateOf(db.AsApp(),
            "insert into item_instances (account_id, definition_id, operative_id, equipped_slot, origin, origin_ref) values (@a, @Item, @o, 'helmet', 'starter', 'x')",
            new { a, Item, o = await NewOperativeAsync(a) }));
    }

    [Fact]
    public async Task Stacks_are_unique_per_location_and_definition_including_the_stash()
    {
        var a = await NewAccountAsync();
        var o = await NewOperativeAsync(a);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("insert into item_stacks (account_id, definition_id, quantity) values (@a, @Item, 3)", new { a, Item });                     // stash
            await conn.ExecuteAsync("insert into item_stacks (account_id, operative_id, definition_id, quantity) values (@a, @o, @Item, 3)", new { a, o, Item });  // bag
            await conn.ExecuteAsync("insert into item_stacks (account_id, definition_id, quantity) values (@a, '00000000-0000-4000-8000-0000000000a2', 1)", new { a }); // other definition
        }
        Assert.Equal("23505", await SqlStateOf(db.AsApp(), "insert into item_stacks (account_id, definition_id, quantity) values (@a, @Item, 1)", new { a, Item }));        // the stash twice (NULLS NOT DISTINCT)
        Assert.Equal("23505", await SqlStateOf(db.AsApp(), "insert into item_stacks (account_id, operative_id, definition_id, quantity) values (@a, @o, @Item, 1)", new { a, o, Item }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Stack_quantity_must_be_positive(int quantity)
    {
        var a = await NewAccountAsync();
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "insert into item_stacks (account_id, definition_id, quantity) values (@a, @Item, @quantity)", new { a, Item, quantity }));
    }

    [Fact]
    public async Task An_origin_ref_mints_at_most_one_instance_or_operative()
    {
        var a = await NewAccountAsync();
        var b = await NewAccountAsync();
        await NewOperativeAsync(a, "starter:one");
        await NewItemAsync(a, originRef: "starter:one");     // operatives and items are separate namespaces
        Assert.Equal("23505", await SqlStateOf(db.AsApp(), "insert into operatives (account_id, definition_id, origin, origin_ref) values (@b, @Operative, 'starter', 'starter:one')", new { b, Operative }));
        Assert.Equal("23505", await SqlStateOf(db.AsApp(), "insert into item_instances (account_id, definition_id, origin, origin_ref) values (@b, @Item, 'starter', 'starter:one')", new { b, Item }));
        await NewItemAsync(b, origin: "settlement", originRef: "starter:one"); // the origin is part of the key
    }

    [Theory]
    [InlineData("starter")]
    [InlineData("settlement")]
    [InlineData("insurance")]
    [InlineData("dev_grant")]
    public async Task Every_item_origin_is_accepted_and_an_unknown_one_is_refused(string origin)
    {
        var a = await NewAccountAsync();
        await NewItemAsync(a, origin: origin);
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "insert into item_instances (account_id, definition_id, origin, origin_ref) values (@a, @Item, 'loot', 'x')", new { a, Item }));
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "insert into operatives (account_id, definition_id, origin, origin_ref) values (@a, @Operative, 'settlement', 'x')", new { a, Operative }));
    }

    // ---- inventory row ----

    [Fact]
    public async Task The_starter_granted_state_is_all_or_nothing()
    {
        var a = await NewAccountAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into inventories (account_id) values (@a)", new { a });
        Assert.Equal((1L, false), await conn.QuerySingleAsync<(long, bool)>("select version, starter_granted from inventories where account_id = @a", new { a }));
        // granted without a catalogue version or time
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "update inventories set starter_granted = true where account_id = @a", new { a }));
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "update inventories set starter_granted = true, starter_catalogue_version = @Catalogue where account_id = @a", new { a, Catalogue }));
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "update inventories set starter_granted = true, starter_granted_at = now() where account_id = @a", new { a }));
        // the reverse: not granted, but carrying a version or a time
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "update inventories set starter_catalogue_version = @Catalogue where account_id = @a", new { a, Catalogue }));
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "update inventories set starter_granted_at = now() where account_id = @a", new { a }));
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "update inventories set version = 0 where account_id = @a", new { a }));
        await conn.ExecuteAsync("update inventories set starter_granted = true, starter_catalogue_version = @Catalogue, starter_granted_at = now() where account_id = @a", new { a, Catalogue });
    }

    [Fact]
    public async Task The_starter_grant_is_final_and_the_version_never_decreases()
    {
        var a = await NewAccountAsync();
        await GrantStarterAsync(a); // false -> true with all three columns together, and version + 1: both pass
        Assert.Equal(2L, await SuperScalarAsync<long>("select version from inventories where account_id = @a", new { a }));

        async Task<PostgresException> Refused(string sql)
        {
            await using var conn = await db.AsApp().OpenConnectionAsync();
            var e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, new { a, Other = "sha256:" + new string('b', 64) }));
            Assert.Equal("23514", e.SqlState);
            return e;
        }

        Assert.Equal("starter_grant_is_final", (await Refused("update inventories set starter_granted = false, starter_catalogue_version = null, starter_granted_at = null where account_id = @a")).ConstraintName);
        Assert.Equal("starter_grant_is_final", (await Refused("update inventories set starter_catalogue_version = @Other where account_id = @a")).ConstraintName);
        Assert.Equal("starter_grant_is_final", (await Refused("update inventories set starter_granted_at = now() + interval '1 day' where account_id = @a")).ConstraintName);
        Assert.Equal("inventory_version_never_decreases", (await Refused("update inventories set version = version - 1 where account_id = @a")).ConstraintName);
        Assert.Equal("inventory_version_never_decreases", (await Refused("update inventories set version = 1 where account_id = @a")).ConstraintName);

        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("update inventories set version = version where account_id = @a", new { a });      // unchanged is fine
        await conn.ExecuteAsync("update inventories set version = version + 5 where account_id = @a", new { a });
        Assert.Equal(7L, await conn.ExecuteScalarAsync<long>("select version from inventories where account_id = @a", new { a }));
        Assert.Equal(Catalogue, await conn.ExecuteScalarAsync<string>("select starter_catalogue_version from inventories where account_id = @a", new { a }));
    }

    [Fact]
    public async Task The_version_of_an_ungranted_inventory_also_never_decreases()
    {
        var a = await NewAccountAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into inventories (account_id) values (@a)", new { a });
        await conn.ExecuteAsync("update inventories set version = 4 where account_id = @a", new { a });
        var e = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("update inventories set version = 3 where account_id = @a", new { a }));
        Assert.Equal(("23514", "inventory_version_never_decreases"), (e.SqlState, e.ConstraintName));
    }

    [Fact]
    public async Task An_account_has_one_inventory_row()
    {
        var a = await NewAccountAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into inventories (account_id) values (@a)", new { a });
        Assert.Equal("23505", await SqlStateOf(db.AsApp(), "insert into inventories (account_id) values (@a)", new { a }));
    }

    [Fact]
    public async Task Experience_cannot_be_negative()
    {
        var a = await NewAccountAsync();
        var o = await NewOperativeAsync(a);
        Assert.Equal("23514", await SqlStateOf(db.AsSuperuser(), "update operatives set experience = -1 where operative_id = @o", new { o }));
        Assert.Equal(0, await SuperScalarAsync<int>("select experience from operatives where operative_id = @o", new { o }));
    }

    [Theory]
    [InlineData("insert into operatives (account_id, definition_id, origin, origin_ref) values (@a, 'Upper', 'starter', 'x')")]
    [InlineData("insert into operatives (account_id, definition_id, origin, origin_ref) values (@a, 'trailing-', 'starter', 'x')")]
    [InlineData("insert into operatives (account_id, definition_id, origin, origin_ref) values (@a, repeat('a', 65), 'starter', 'x')")]
    [InlineData("insert into operatives (account_id, definition_id, origin, origin_ref) values (@a, '', 'starter', 'x')")]
    [InlineData("insert into operatives (account_id, definition_id, origin, origin_ref) values (@a, 'op one', 'starter', 'x')")]
    [InlineData("insert into operatives (account_id, definition_id, origin, origin_ref) values (@a, 'ok', 'starter', 'has space')")]
    [InlineData("insert into operatives (account_id, definition_id, origin, origin_ref) values (@a, 'ok', 'starter', E'tab\\there')")]
    [InlineData("insert into operatives (account_id, definition_id, origin, origin_ref) values (@a, 'ok', 'starter', repeat('r', 301))")]
    [InlineData("insert into operatives (account_id, definition_id, origin, origin_ref) values (@a, 'ok', 'starter', '')")]
    [InlineData("insert into item_instances (account_id, definition_id, origin, origin_ref) values (@a, 'ok', 'starter', '')")]
    [InlineData("insert into item_instances (account_id, definition_id, origin, origin_ref) values (@a, 'Upper', 'starter', 'x')")]
    [InlineData("insert into item_instances (account_id, definition_id, origin, origin_ref) values (@a, E'new\\nline', 'starter', 'x')")]
    [InlineData("insert into item_instances (account_id, definition_id, origin, origin_ref) values (@a, repeat('a', 65), 'starter', 'x')")]
    [InlineData("insert into item_instances (account_id, definition_id, origin, origin_ref) values (@a, 'ok', 'starter', 'has space')")]
    [InlineData("insert into item_stacks (account_id, definition_id, quantity) values (@a, 'Upper', 1)")]
    [InlineData("insert into item_stacks (account_id, definition_id, quantity) values (@a, '-leading', 1)")]
    [InlineData("insert into item_stacks (account_id, definition_id, quantity) values (@a, 'a..b', 1)")]
    public async Task Stored_catalogue_ids_follow_the_catalogue_id_rule(string sql)
    {
        var a = await NewAccountAsync();
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), sql, new { a }));
    }

    [Theory]
    [InlineData("Upper")]
    [InlineData("trailing-")]
    [InlineData("has space")]
    [InlineData("")]
    public async Task Stored_choice_ids_follow_the_catalogue_id_rule(string choice)
    {
        var o = await NewOperativeAsync(await NewAccountAsync());
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "insert into operative_choices (operative_id, ordinal, choice_id) values (@o, 0, @choice)", new { o, choice }));
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "insert into operative_choices (operative_id, ordinal, choice_id) values (@o, -1, 'ok')", new { o }));
    }

    [Theory]
    [InlineData("sha256:ABCDEF")]
    [InlineData("sha1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task The_stored_starter_catalogue_version_is_a_sha256_id(string version)
    {
        var a = await NewAccountAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into inventories (account_id) values (@a)", new { a });
        Assert.Equal("23514", await SqlStateOf(db.AsApp(),
            "update inventories set starter_granted = true, starter_catalogue_version = @version, starter_granted_at = now() where account_id = @a", new { a, version }));
    }

    [Fact]
    public async Task Choices_are_numbered_per_operative_and_cannot_repeat_an_ordinal()
    {
        var a = await NewAccountAsync();
        var first = await NewOperativeAsync(a);
        var second = await NewOperativeAsync(a);
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into operative_choices (operative_id, ordinal, choice_id) values (@first, 0, 'one'), (@first, 1, 'two'), (@second, 0, 'one')", new { first, second });
        Assert.Equal("23505", await SqlStateOf(db.AsApp(), "insert into operative_choices (operative_id, ordinal, choice_id) values (@first, 1, 'three')", new { first }));
    }

    // ---- privileges ----

    static readonly string[] Stage4Tables = ["inventories", "item_instances", "item_stacks", "operative_choices", "operatives"];

    [Fact]
    public async Task The_app_holds_exactly_the_stage_4_privileges()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var tables = (await conn.QueryAsync<string>("""
            select table_name || ':' || string_agg(privilege_type, ',' order by privilege_type)
            from information_schema.role_table_grants where grantee = 'accounts_app' and table_name = any(@Stage4Tables)
            group by table_name order by table_name
            """, new { Stage4Tables })).ToList();
        Assert.Equal(new[]
        {
            "inventories:SELECT",
            "item_instances:SELECT",
            "item_stacks:SELECT",
            "operative_choices:SELECT",
            "operatives:SELECT",
        }, tables.OrderBy(t => t, StringComparer.Ordinal));
        var columns = (await conn.QueryAsync<string>("""
            select table_name || '.' || column_name || ':' || string_agg(privilege_type, ',' order by privilege_type)
            from information_schema.column_privileges
            where grantee = 'accounts_app' and privilege_type in ('INSERT', 'UPDATE') and table_name = any(@Stage4Tables)
              and (table_name, column_name, privilege_type) not in (
                select table_name, column_name, privilege_type from information_schema.column_privileges c
                join information_schema.role_table_grants t using (table_name, privilege_type)
                where c.grantee = 'accounts_app' and t.grantee = 'accounts_app')
            group by table_name, column_name
            """, new { Stage4Tables })).ToList();
        Assert.Equal(new[]
        {
            "inventories.account_id:INSERT",
            "inventories.starter_catalogue_version:UPDATE",
            "inventories.starter_granted:UPDATE",
            "inventories.starter_granted_at:UPDATE",
            "inventories.version:UPDATE",
            "item_instances.account_id:INSERT",
            "item_instances.definition_id:INSERT",
            "item_instances.equipped_slot:INSERT",
            "item_instances.operative_id:INSERT",
            "item_instances.origin:INSERT",
            "item_instances.origin_ref:INSERT",
            "item_stacks.account_id:INSERT",
            "item_stacks.definition_id:INSERT",
            "item_stacks.operative_id:INSERT",
            "item_stacks.quantity:INSERT",
            "operative_choices.choice_id:INSERT",
            "operative_choices.operative_id:INSERT",
            "operative_choices.ordinal:INSERT",
            "operatives.account_id:INSERT",
            "operatives.definition_id:INSERT",
            "operatives.origin:INSERT",
            "operatives.origin_ref:INSERT",
        }, columns.OrderBy(c => c, StringComparer.Ordinal));

        // Nowhere in the schema: no DELETE or TRUNCATE, and no table-level UPDATE on the four stage 4 tables that are
        // settled only by later, reviewed paths.
        Assert.Empty(await conn.QueryAsync<string>(
            "select table_name || ':' || privilege_type from information_schema.role_table_grants where grantee = 'accounts_app' and privilege_type in ('DELETE', 'TRUNCATE')"));
        Assert.Empty(await conn.QueryAsync<string>(
            "select table_name || '.' || column_name from information_schema.column_privileges where grantee = 'accounts_app' and privilege_type = 'UPDATE' and table_name in ('operatives', 'operative_choices', 'item_instances', 'item_stacks')"));
        // No sequence privilege at all: identities are minted by inserts, which need none.
        Assert.Empty(await conn.QueryAsync<string>(
            "select relname::text from pg_class where case when relkind = 'S' then has_sequence_privilege('accounts_app', oid, 'USAGE, SELECT, UPDATE') else false end"));
        Assert.Equal(4L, await conn.ExecuteScalarAsync<long>("select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'public' and c.relkind = 'S'"));
    }

    [Theory]
    [InlineData("update operatives set experience = 100", "42501")]
    [InlineData("update operatives set account_id = 1", "42501")]
    [InlineData("delete from operatives", "42501")]
    [InlineData("truncate operatives cascade", "42501")]
    [InlineData("update item_instances set operative_id = null", "42501")]
    [InlineData("update item_instances set equipped_slot = null", "42501")]
    [InlineData("delete from item_instances", "42501")]
    [InlineData("update item_stacks set quantity = 9", "42501")]
    [InlineData("delete from item_stacks", "42501")]
    [InlineData("update inventories set account_id = 2", "42501")]
    [InlineData("delete from inventories", "42501")]
    [InlineData("insert into inventories (account_id, version) values (1, 5)", "42501")]
    [InlineData("insert into inventories (account_id, starter_granted) values (1, true)", "42501")]
    public async Task The_app_cannot_change_or_remove_what_stage_4_never_lets_it(string sql, string sqlState)
    {
        var a = await NewAccountAsync();
        var o = await NewOperativeAsync(a);
        await NewItemAsync(a, o);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("insert into item_stacks (account_id, definition_id, quantity) values (@a, @Item, 2)", new { a, Item });
            await conn.ExecuteAsync("insert into inventories (account_id) values (@a)", new { a });
        }
        Assert.Equal(sqlState, await SqlStateOf(db.AsApp(), sql));
    }

    [Fact]
    public async Task Choices_are_append_only()
    {
        var o = await NewOperativeAsync(await NewAccountAsync());
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("insert into operative_choices (operative_id, ordinal, choice_id) values (@o, 0, 'first')", new { o });
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "update operative_choices set choice_id = 'second'"));
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "update operative_choices set ordinal = 3"));
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "delete from operative_choices"));
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "truncate operative_choices"));
    }

    [Fact]
    public async Task The_app_may_bump_the_inventory_version_and_lock_the_row_as_a_pick_does()
    {
        var a = await NewAccountAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into inventories (account_id) values (@a)", new { a });
        await using var tx = await conn.BeginTransactionAsync();
        Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("select version from inventories where account_id = @a for update", new { a }, tx));
        await conn.ExecuteAsync("update inventories set version = version + 1 where account_id = @a", new { a }, tx);
        await tx.CommitAsync();
        Assert.Equal(2L, await conn.ExecuteScalarAsync<long>("select version from inventories where account_id = @a", new { a }));
    }

    // ---- audit ----

    [Fact]
    public async Task The_real_name_of_0001s_action_check_constraint_is_operator_actions_action_check()
    {
        await using var fresh = await (await TestCluster.Instance).CreateDatabaseAsync();
        await new Migrator(fresh.AsMigrator(), MigrationCatalog.Embedded).MigrateAsync(2);
        await using var conn = await fresh.AsSuperuser().OpenConnectionAsync();
        var checks = (await conn.QueryAsync<(string Name, string Definition)>("""
            select conname::text, pg_get_constraintdef(oid) from pg_constraint
            where conrelid = 'operator_actions'::regclass and contype = 'c' and pg_get_constraintdef(oid) like '%account_create%'
            """)).ToList();
        var only = Assert.Single(checks);
        Assert.Equal("operator_actions_action_check", only.Name);
        Assert.DoesNotContain("starter_backfill", only.Definition, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("account_create")]
    [InlineData("credential_rotate")]
    [InlineData("credential_revoke")]
    [InlineData("sessions_revoke")]
    [InlineData("account_suspend")]
    [InlineData("account_reactivate")]
    [InlineData("starter_backfill")]
    [InlineData("revalidate")]
    [InlineData("participant_void")]
    [InlineData("host_declared_lost")]
    [InlineData("host_drain")]
    [InlineData("host_enable")]
    [InlineData("build_disable")]
    [InlineData("energy_adjust")]
    public async Task The_starter_backfill_audit_action_is_accepted_with_all_of_0001s_actions(string action)
    {
        var a = await NewAccountAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("insert into operator_actions (correlation_id, operator, action, account_id, reason, checks, result) values (gen_random_uuid(), 'op', @action, @a, 'r', '{}', '{}')", new { a, action });
        Assert.Equal("23514", await SqlStateOf(db.AsApp(),
            "insert into operator_actions (correlation_id, operator, action, reason, checks, result) values (gen_random_uuid(), 'op', 'starter_backfill_all', 'r', '{}', '{}')"));
    }

    // ---- stamps ----

    [Fact]
    public async Task Rows_are_stamped_with_statement_timestamp()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        var defaults = (await conn.QueryAsync<string>("""
            select table_name || '.' || column_name || '=' || column_default from information_schema.columns
            where table_schema = 'public' and table_name = any(@Stage4Tables) and column_name in ('created_at', 'picked_at')
            """, new { Stage4Tables })).ToList();
        Assert.Equal(new[] { "item_instances.created_at=statement_timestamp()", "operative_choices.picked_at=statement_timestamp()", "operatives.created_at=statement_timestamp()" },
            defaults.OrderBy(d => d, StringComparer.Ordinal));

        // A statement that waited stamps what it creates after the wait, not at its transaction's start.
        var a = await NewAccountAsync();
        await using var app = await db.AsApp().OpenConnectionAsync();
        await using var tx = await app.BeginTransactionAsync();
        var started = await app.ExecuteScalarAsync<DateTime>("select now()", transaction: tx);
        await app.ExecuteAsync("select pg_sleep(0.05)", transaction: tx);
        await app.ExecuteAsync("insert into operatives (account_id, definition_id, origin, origin_ref) values (@a, @Operative, 'starter', 'stamp')", new { a, Operative }, tx);
        var stamped = await app.ExecuteScalarAsync<DateTime>("select created_at from operatives where origin_ref = 'stamp'", transaction: tx);
        Assert.True(stamped > started, $"stamped {stamped:O} should be after the transaction start {started:O}");
    }

    // ---- invariant ----

    [Fact]
    public async Task The_inventory_invariant_holds_and_catches_an_ungranted_owner_and_an_ordinal_gap()
    {
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
        const string Expected1 = "0004_inventory_consistency.sql: 1 violating row(s)";

        // An operative whose account has no inventory row.
        var a = await NewAccountAsync();
        var o = await NewOperativeAsync(a);
        Assert.Equal([Expected1], await AccountTestKit.InvariantFailuresAsync(db));
        // A row exists but the starter is not granted: still a violation.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("insert into inventories (account_id) values (@a)", new { a });
        Assert.Equal([Expected1], await AccountTestKit.InvariantFailuresAsync(db));
        await GrantRowAsync(a);
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));

        // Items and stacks of an ungranted account are violations too.
        var b = await NewAccountAsync();
        await NewItemAsync(b);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("insert into item_stacks (account_id, definition_id, quantity) values (@b, @Item, 2)", new { b, Item });
        Assert.Equal(["0004_inventory_consistency.sql: 2 violating row(s)"], await AccountTestKit.InvariantFailuresAsync(db));
        await GrantRowAsync(b, insert: true);
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));

        // Ordinals exactly 0..n-1 hold; a gap, or a first ordinal other than 0, does not.
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("insert into operative_choices (operative_id, ordinal, choice_id) values (@o, 0, 'first'), (@o, 1, 'second')", new { o });
            Assert.Empty(await AccountTestKit.InvariantFailuresAsync(db));
            await conn.ExecuteAsync("insert into operative_choices (operative_id, ordinal, choice_id) values (@o, 3, 'gap')", new { o });
        }
        Assert.Equal([Expected1], await AccountTestKit.InvariantFailuresAsync(db));

        var o2 = await NewOperativeAsync(b);
        await using (var conn = await db.AsApp().OpenConnectionAsync())
            await conn.ExecuteAsync("insert into operative_choices (operative_id, ordinal, choice_id) values (@o2, 1, 'starts-at-one')", new { o2 });
        Assert.Equal(["0004_inventory_consistency.sql: 2 violating row(s)"], await AccountTestKit.InvariantFailuresAsync(db));
    }

    async Task GrantRowAsync(long account, bool insert = false)
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        if (insert) await conn.ExecuteAsync("insert into inventories (account_id) values (@account)", new { account });
        await conn.ExecuteAsync(
            "update inventories set starter_granted = true, starter_catalogue_version = @Catalogue, starter_granted_at = now() where account_id = @account",
            new { account, Catalogue });
    }

    // ---- upgrade ----

    [Fact]
    public async Task Migration_0004_upgrades_a_database_with_stage_3_data()
    {
        await using var upgrade = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(upgrade.AsMigrator(), MigrationCatalog.Embedded);
        await migrator.MigrateAsync(2);

        long account;
        await using (var conn = await upgrade.AsApp().OpenConnectionAsync())
        {
            account = await conn.ExecuteScalarAsync<long>("insert into accounts (status, display_name) values ('active', 'Stage three') returning account_id");
            await conn.ExecuteAsync("insert into operator_actions (correlation_id, operator, action, account_id, reason, checks, result) values (gen_random_uuid(), 'op', 'account_create', @account, 'r', '{}', '{}')", new { account });
        }
        Assert.Equal([3, 4], (await migrator.MigrateAsync()).Applied);

        await using (var conn = await upgrade.AsSuperuser().OpenConnectionAsync())
        {
            Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("select count(*) from accounts where account_id = @account", new { account }));
            Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("select count(*) from operator_actions where account_id = @account"  , new { account }));
            // The account has no inventory row yet: it needs the backfill, and has no owned rows to violate the invariant.
            Assert.Equal(0L, await conn.ExecuteScalarAsync<long>("select count(*) from inventories"));
        }
        Assert.Empty(await AccountTestKit.InvariantFailuresAsync(upgrade));

        // The migrated database accepts the Stage 4 writes for that account, then the backfill audit action.
        await using (var conn = await upgrade.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("insert into inventories (account_id) values (@account)", new { account });
            await conn.ExecuteAsync("insert into operator_actions (correlation_id, operator, action, account_id, reason, checks, result) values (gen_random_uuid(), 'op', 'starter_backfill', @account, 'r', '{}', '{}')", new { account });
        }
    }
}
