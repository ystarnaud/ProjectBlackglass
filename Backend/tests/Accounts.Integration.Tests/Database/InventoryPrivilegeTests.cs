using Dapper;
using Npgsql;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Migrations;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>Migration 0005 (Stage 5; rulings S5-D3, S5-D4) against a real PostgreSQL 17: the app role may move and equip an
/// instance (operative_id, equipped_slot) and change or delete a stack (quantity, delete), and nothing more: it can never
/// delete an instance or change what an instance is, whose it is or where it came from. The schema's own rules (composite
/// ownership, the slot index, the bag-only slot check, positive quantities) still hold for those updates.</summary>
[Trait("Requires", "Postgres")]
public class InventoryPrivilegeTests : IAsyncLifetime
{
    TestDatabase db = null!;
    const string Operative = "00000000-0000-4000-8000-000000000001";
    const string Rifle = "item.rifle";
    const string Kit = "item.kit";
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

    async Task<T> SuperScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.ExecuteScalarAsync<T>(sql, args))!;
    }

    /// <summary>An account with a granted starter, two operatives, one item in the stash and one stack in the stash.</summary>
    async Task<Seed> SeedAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        var account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
        await conn.ExecuteAsync("insert into inventories (account_id) values (@account)", new { account });
        await conn.ExecuteAsync(
            "update inventories set starter_granted = true, starter_catalogue_version = @Catalogue, starter_granted_at = now() where account_id = @account",
            new { account, Catalogue });
        const string insertOperative = "insert into operatives (account_id, definition_id, origin, origin_ref) values (@account, @Operative, 'starter', @r) returning operative_id";
        var first = await conn.ExecuteScalarAsync<long>(insertOperative, new { account, Operative, r = Guid.NewGuid().ToString() });
        var second = await conn.ExecuteScalarAsync<long>(insertOperative, new { account, Operative, r = Guid.NewGuid().ToString() });
        var item = await conn.ExecuteScalarAsync<long>(
            "insert into item_instances (account_id, definition_id, origin, origin_ref) values (@account, @Rifle, 'starter', @r) returning item_id",
            new { account, Rifle, r = Guid.NewGuid().ToString() });
        await conn.ExecuteAsync("insert into item_stacks (account_id, definition_id, quantity) values (@account, @Kit, 5)", new { account, Kit });
        return new Seed(account, first, second, item);
    }

    record Seed(long Account, long FirstOperative, long SecondOperative, long Item);

    // ---- grants ----

    [Fact]
    public async Task The_app_can_move_and_equip_an_instance_but_not_change_what_or_whose_it_is()
    {
        var s = await SeedAsync();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("update item_instances set operative_id = @o where item_id = @Item", new { o = s.FirstOperative, s.Item });           // stash -> bag
            await conn.ExecuteAsync("update item_instances set equipped_slot = 'weapon' where item_id = @Item", new { s.Item });                           // bag -> slot
            await conn.ExecuteAsync("update item_instances set equipped_slot = null where item_id = @Item", new { s.Item });                               // slot -> bag
            await conn.ExecuteAsync("update item_instances set operative_id = @o, equipped_slot = 'armor' where item_id = @Item", new { o = s.SecondOperative, s.Item }); // both together
            await conn.ExecuteAsync("update item_instances set operative_id = null, equipped_slot = null where item_id = @Item", new { s.Item });          // back to the stash
        }

        foreach (var sql in new[]
        {
            "update item_instances set definition_id = 'item.other' where item_id = @Item",
            "update item_instances set account_id = 999 where item_id = @Item",
            "update item_instances set origin = 'settlement' where item_id = @Item",
            "update item_instances set origin_ref = 'changed' where item_id = @Item",
            "update item_instances set created_at = '2000-01-01' where item_id = @Item",
            "update item_instances set operative_id = null, origin_ref = 'changed' where item_id = @Item",   // one forbidden column spoils the statement
        })
            Assert.Equal("42501", await SqlStateOf(db.AsApp(), sql, new { s.Item }));
        // The identity is GENERATED ALWAYS: PostgreSQL refuses to set a value (428C9) before it checks privileges, but for
        // "= default" it reaches the privilege check, and the app holds no UPDATE on item_id (The_grants_are_exactly_these).
        Assert.Equal("428C9", await SqlStateOf(db.AsApp(), "update item_instances set item_id = 999 where item_id = @Item", new { s.Item }));
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "update item_instances set item_id = default where item_id = @Item", new { s.Item }));
    }

    [Fact]
    public async Task The_app_cannot_delete_an_instance()
    {
        var s = await SeedAsync();
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "delete from item_instances where item_id = @Item", new { s.Item }));
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "delete from item_instances"));
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "truncate item_instances"));
        Assert.Equal(1L, await SuperScalarAsync<long>("select count(*) from item_instances where item_id = @Item", new { s.Item }));
    }

    [Fact]
    public async Task The_app_can_change_a_stack_quantity_and_delete_a_stack_but_not_move_its_row()
    {
        var s = await SeedAsync();
        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("update item_stacks set quantity = 3 where account_id = @Account and definition_id = @Kit", new { s.Account, Kit });
            Assert.Equal(3, await conn.ExecuteScalarAsync<int>("select quantity from item_stacks where account_id = @Account and definition_id = @Kit", new { s.Account, Kit }));
        }

        foreach (var sql in new[]
        {
            "update item_stacks set operative_id = @o where account_id = @Account",
            "update item_stacks set definition_id = 'item.other' where account_id = @Account",
            "update item_stacks set account_id = 999 where account_id = @Account",
            "update item_stacks set quantity = 1, operative_id = @o where account_id = @Account",   // one forbidden column spoils the statement
        })
            Assert.Equal("42501", await SqlStateOf(db.AsApp(), sql, new { s.Account, o = s.FirstOperative }));

        await using (var conn = await db.AsApp().OpenConnectionAsync())
        {
            Assert.Equal(1, await conn.ExecuteAsync("delete from item_stacks where account_id = @Account and definition_id = @Kit", new { s.Account, Kit }));
            Assert.Equal(0L, await conn.ExecuteScalarAsync<long>("select count(*) from item_stacks where account_id = @Account", new { s.Account }));
        }
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "truncate item_stacks"));
    }

    [Fact]
    public async Task The_app_still_has_no_sequence_privilege()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        Assert.Empty(await conn.QueryAsync<string>(
            "select relname::text from pg_class where case when relkind = 'S' then has_sequence_privilege('accounts_app', oid, 'USAGE, SELECT, UPDATE') else false end"));
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "select nextval('item_instances_item_id_seq')"));
        Assert.Equal("42501", await SqlStateOf(db.AsApp(), "select setval('item_instances_item_id_seq', 1)"));
    }

    [Fact]
    public async Task The_grants_are_exactly_these()
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();

        // Table-level grants, as the catalogue lists them for the role itself.
        var tables = (await conn.QueryAsync<string>("""
            select table_name || ':' || string_agg(privilege_type, ',' order by privilege_type)
            from information_schema.role_table_grants
            where grantee = 'accounts_app' and table_name in ('item_instances', 'item_stacks')
            group by table_name order by table_name
            """)).ToList();
        Assert.Equal(new[] { "item_instances:SELECT", "item_stacks:DELETE,SELECT" }, tables.OrderBy(t => t, StringComparer.Ordinal));

        // The effective privilege on every column (table-level or column-level, from any grant the role inherits or PUBLIC holds).
        var columns = (await conn.QueryAsync<string>("""
            select c.relname || '.' || a.attname || ':' || string_agg(p.priv, ',' order by p.priv)
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace and n.nspname = 'public'
            join pg_attribute a on a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped
            cross join (values ('SELECT'), ('INSERT'), ('UPDATE'), ('REFERENCES')) as p(priv)
            where c.relname in ('item_instances', 'item_stacks') and c.relkind = 'r'
              and has_column_privilege('accounts_app', c.oid, a.attnum, p.priv)
            group by c.relname, a.attname
            """)).ToList();
        Assert.Equal(new[]
        {
            "item_instances.account_id:INSERT,SELECT",
            "item_instances.created_at:SELECT",
            "item_instances.definition_id:INSERT,SELECT",
            "item_instances.equipped_slot:INSERT,SELECT,UPDATE",
            "item_instances.item_id:SELECT",
            "item_instances.operative_id:INSERT,SELECT,UPDATE",
            "item_instances.origin:INSERT,SELECT",
            "item_instances.origin_ref:INSERT,SELECT",
            "item_stacks.account_id:INSERT,SELECT",
            "item_stacks.definition_id:INSERT,SELECT",
            "item_stacks.operative_id:INSERT,SELECT",
            "item_stacks.quantity:INSERT,SELECT,UPDATE",
        }, columns.OrderBy(c => c, StringComparer.Ordinal));

        // The other table privileges, effective, on each of the two tables.
        var others = (await conn.QueryAsync<string>("""
            select c.relname || ':' || p.priv
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace and n.nspname = 'public'
            cross join (values ('DELETE'), ('TRUNCATE'), ('TRIGGER')) as p(priv)
            where c.relname in ('item_instances', 'item_stacks') and c.relkind = 'r' and has_table_privilege('accounts_app', c.oid, p.priv)
            """)).ToList();
        Assert.Equal(new[] { "item_stacks:DELETE" }, others);
    }

    // ---- the schema still holds for the permitted updates ----

    [Fact]
    public async Task A_cross_account_move_is_refused_by_the_composite_key()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync();
        Assert.Equal("23503", await SqlStateOf(db.AsApp(), "update item_instances set operative_id = @o where item_id = @Item", new { o = theirs.FirstOperative, mine.Item }));
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("update item_instances set operative_id = @o where item_id = @Item", new { o = mine.FirstOperative, mine.Item });   // its own operative is fine
    }

    [Fact]
    public async Task An_equipped_item_cannot_go_to_the_stash_in_the_database()
    {
        var s = await SeedAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        await conn.ExecuteAsync("update item_instances set operative_id = @o, equipped_slot = 'weapon' where item_id = @Item", new { o = s.FirstOperative, s.Item });
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "update item_instances set operative_id = null where item_id = @Item", new { s.Item }));
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "update item_instances set equipped_slot = 'helmet' where item_id = @Item", new { s.Item }));
    }

    [Fact]
    public async Task The_slot_index_still_holds_for_updates()
    {
        var s = await SeedAsync();
        await using var conn = await db.AsApp().OpenConnectionAsync();
        var second = await conn.ExecuteScalarAsync<long>(
            "insert into item_instances (account_id, definition_id, operative_id, origin, origin_ref) values (@Account, @Rifle, @o, 'starter', @r) returning item_id",
            new { s.Account, Rifle, o = s.FirstOperative, r = Guid.NewGuid().ToString() });
        await conn.ExecuteAsync("update item_instances set operative_id = @o, equipped_slot = 'weapon' where item_id = @Item", new { o = s.FirstOperative, s.Item });
        Assert.Equal("23505", await SqlStateOf(db.AsApp(), "update item_instances set equipped_slot = 'weapon' where item_id = @second", new { second }));
        await conn.ExecuteAsync("update item_instances set equipped_slot = 'armor' where item_id = @second", new { second });   // another slot is fine
        // Moving an equipped item to another operative's occupied slot is refused too.
        await conn.ExecuteAsync("insert into item_instances (account_id, definition_id, operative_id, equipped_slot, origin, origin_ref) values (@Account, @Rifle, @o, 'weapon', 'starter', @r)",
            new { s.Account, Rifle, o = s.SecondOperative, r = Guid.NewGuid().ToString() });
        Assert.Equal("23505", await SqlStateOf(db.AsApp(), "update item_instances set operative_id = @o where item_id = @Item", new { o = s.SecondOperative, s.Item }));
    }

    // ---- upgrade ----

    [Fact]
    public async Task Upgrade_from_0004_with_data_keeps_every_row()
    {
        await using var upgrade = await (await TestCluster.Instance).CreateDatabaseAsync();
        var migrator = new Migrator(upgrade.AsMigrator(), MigrationCatalog.Embedded);
        await migrator.MigrateAsync(4);

        // A provisioned account exactly as Stage 4 leaves it: granted starter, an operative, an equipped item, a bagged item, a stash stack.
        long account, operative, equipped, bagged;
        await using (var conn = await upgrade.AsApp().OpenConnectionAsync())
        {
            account = await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
            await conn.ExecuteAsync("insert into inventories (account_id) values (@account)", new { account });
            await conn.ExecuteAsync(
                "update inventories set starter_granted = true, starter_catalogue_version = @Catalogue, starter_granted_at = now(), version = version + 1 where account_id = @account",
                new { account, Catalogue });
            operative = await conn.ExecuteScalarAsync<long>(
                "insert into operatives (account_id, definition_id, origin, origin_ref) values (@account, @Operative, 'starter', 'starter:op') returning operative_id", new { account, Operative });
            const string insertItem = "insert into item_instances (account_id, definition_id, operative_id, equipped_slot, origin, origin_ref) values (@account, @Rifle, @operative, @slot, 'starter', @r) returning item_id";
            equipped = await conn.ExecuteScalarAsync<long>(insertItem, new { account, Rifle, operative, slot = "weapon", r = "starter:a" });
            bagged = await conn.ExecuteScalarAsync<long>(insertItem, new { account, Rifle, operative, slot = (string?)null, r = "starter:b" });
            await conn.ExecuteAsync("insert into item_stacks (account_id, definition_id, quantity) values (@account, @Kit, 5)", new { account, Kit });
            // At version 4 the edit statements are still refused.
            Assert.Equal("42501", await SqlStateOf(upgrade.AsApp(), "update item_instances set operative_id = null where item_id = @bagged", new { bagged }));
            Assert.Equal("42501", await SqlStateOf(upgrade.AsApp(), "delete from item_stacks"));
        }

        Assert.Equal([5], (await migrator.MigrateAsync(5)).Applied);

        await using (var conn = await upgrade.AsSuperuser().OpenConnectionAsync())
        {
            Assert.Equal((1L, 1L, 2L, 1L), await conn.QuerySingleAsync<(long, long, long, long)>(
                "select (select count(*) from accounts), (select count(*) from operatives), (select count(*) from item_instances), (select count(*) from item_stacks)"));
            Assert.Equal(("weapon", operative), await conn.QuerySingleAsync<(string, long)>("select equipped_slot, operative_id from item_instances where item_id = @equipped", new { equipped }));
            Assert.Equal(5, await conn.ExecuteScalarAsync<int>("select quantity from item_stacks where account_id = @account", new { account }));
        }
        Assert.Empty(await InvariantRunner.RunAsync(upgrade.AsSuperuser(), 5, InvariantRunner.RealDirectory));   // every invariant that holds at version 5

        // The upgraded database accepts the edits the grants now allow, and keeps its invariants.
        await using (var conn = await upgrade.AsApp().OpenConnectionAsync())
        {
            await conn.ExecuteAsync("update item_instances set equipped_slot = null where item_id = @equipped", new { equipped });
            await conn.ExecuteAsync("update item_instances set operative_id = null where item_id = @equipped", new { equipped });
            await conn.ExecuteAsync("update item_stacks set quantity = 4 where account_id = @account", new { account });
            await conn.ExecuteAsync("delete from item_stacks where account_id = @account", new { account });
        }
        Assert.Empty(await InvariantRunner.RunAsync(upgrade.AsSuperuser(), 5, InvariantRunner.RealDirectory));   // every invariant that holds at version 5
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_zero_stack_quantity_is_refused(int quantity)
    {
        var s = await SeedAsync();
        Assert.Equal("23514", await SqlStateOf(db.AsApp(), "update item_stacks set quantity = @quantity where account_id = @Account", new { quantity, s.Account }));
        Assert.Equal(5, await SuperScalarAsync<int>("select quantity from item_stacks where account_id = @Account", new { s.Account }));
    }
}
