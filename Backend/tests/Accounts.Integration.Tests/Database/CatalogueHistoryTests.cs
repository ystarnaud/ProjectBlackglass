using Dapper;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Catalogue;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>The catalogue history (S9-B20, revision 3 corrections): idempotent registration, retirement allowed, an identity-kind
/// change refused (serve exits 5), verified lookups, `ready` judged under the admission catalogue whatever the API runs, and the
/// audited import.</summary>
[Trait("Requires", "Postgres")]
public class CatalogueHistoryTests : IAsyncLifetime
{
    TestDatabase db = null!;
    CatalogueHistoryStore history = null!;

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        history = new CatalogueHistoryStore(db.AsApp());
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    /// <summary>A valid catalogue derived from the embedded one.</summary>
    internal static GameCatalogue Variant(Func<CatalogueDocument, CatalogueDocument> change) =>
        GameCatalogue.Load(CanonicalCatalogueJson.ToBytes(change(GameCatalogue.Embedded.Document)));

    /// <summary>The embedded catalogue with servo boots retired (out of the items and the loot table), a 6-entry bag and medkits
    /// stacking to 2.</summary>
    internal static GameCatalogue Retiring() => Variant(d => d with
    {
        Inventory = new InventorySettings(6),
        Items = [.. d.Items.Where(i => i.Id != "item.servo-boots").Select(i => i.Id == "item.medkit" ? i with { MaxStack = 2 } : i)],
        LootTable = d.LootTable with { Entries = [.. d.LootTable.Entries.Where(e => e.ItemId != "item.servo-boots")] },
        Progression = d.Progression with { MissionCompletionXp = 90 },
        Starter = new Starter([.. d.Starter.Operatives.Select(o => o with { Grants = [.. o.Grants.Where(g => g.ItemId != "item.servo-boots")] })],
            [.. d.Starter.Stash.Where(g => g.ItemId != "item.servo-boots")]),
    });

    [Fact]
    public async Task Registration_is_idempotent_and_verified()
    {
        Assert.True(await history.RegisterAsync(GameCatalogue.Embedded, null, default));
        Assert.False(await history.RegisterAsync(GameCatalogue.Embedded, null, default));
        Assert.Equal(1L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from catalogue_versions"));
        var fresh = new CatalogueHistoryStore(db.AsApp());
        var found = Assert.IsType<CatalogueLookup.Found>(await fresh.LookupAsync(GameCatalogue.Embedded.Version, default));
        Assert.Equal(GameCatalogue.Embedded.Version, found.Catalogue.Version);
        Assert.Equal(new CatalogueLookup.Unavailable("missing"), await fresh.LookupAsync("sha256:" + new string('9', 64), default));
    }

    [Fact]
    public async Task A_newer_catalogue_may_retire_a_definition_but_never_change_an_ids_kind()
    {
        await history.RegisterAsync(GameCatalogue.Embedded, null, default);
        Assert.True(await history.RegisterAsync(Retiring(), null, default));
        // Both versions stay available for historical settlement.
        Assert.IsType<CatalogueLookup.Found>(await new CatalogueHistoryStore(db.AsApp()).LookupAsync(GameCatalogue.Embedded.Version, default));
        Assert.IsType<CatalogueLookup.Found>(await new CatalogueHistoryStore(db.AsApp()).LookupAsync(Retiring().Version, default));

        var flipped = Variant(d => d with
        {
            Items = [.. d.Items.Select(i => i.Id == "item.medkit" ? i with { Stackable = false, MaxStack = 1, Category = ItemCategory.Consumable } : i)],
        });
        var e = await Assert.ThrowsAsync<CatalogueRegistrationException>(() => history.RegisterAsync(flipped, null, default));
        Assert.Contains("item.medkit", e.Message, StringComparison.Ordinal);
        Assert.Equal(2L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from catalogue_versions"));
    }

    [Fact]
    public async Task Serve_refuses_to_start_with_a_catalogue_that_changes_an_ids_kind()
    {
        await history.RegisterAsync(GameCatalogue.Embedded, null, default);
        var flipped = Variant(d => d with { Items = [.. d.Items.Select(i => i.Id == "item.medkit" ? i with { Stackable = false, MaxStack = 1 } : i)] });
        var error = new StringWriter();
        var env = db.EnvFor("app");
        env["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        var start = await ServeCommand.StartAsync(env, error, default, loadCatalogue: () => flipped);
        Assert.Equal(ExitCodes.CatalogueIdentityChanged, start.ExitCode);
        Assert.Null(start.App);
        Assert.Contains("item.medkit", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_registers_its_catalogue_at_startup()
    {
        await using var api = await ApiServer.StartAsync(db);
        Assert.Equal(GameCatalogue.Embedded.Version, await ScalarAsSuperuserAsync<string>(db, "select version from catalogue_versions"));
    }

    [Fact]
    public async Task Ready_judges_the_pool_under_the_admission_catalogue_whatever_the_api_runs()
    {
        var kit = await MissionKit.CreateAsync(db);
        var (account, attempt) = await kit.AdmitAsync(); // admitted under the embedded catalogue X
        // The API is upgraded to Y (registered by its serve), whose loot table no longer lists servo boots.
        await history.RegisterAsync(Retiring(), null, default);
        await kit.BeatAsync();
        var claim = Assert.IsType<Blackglass.Accounts.Persistence.Hosts.ClaimResult.Claimed>(
            await kit.Hosts.ClaimAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), Guid.NewGuid(), 120, default));
        var session = Assert.IsType<RegisterResult.Registered>(await kit.Processes.RegisterAsync(attempt, claim.Credential.Hash(), new RegisterRequest(MissionKit.Build), default)).ProcessSessionId;
        PoolLine[] pool = [new(0, 0, "item.servo-boots", 1)]; // valid under X only
        var report = new ReadyReport(session, "cf2e14dece9cc2a6", MissionKit.Table.Version, LootHash.Compute(MissionKit.Table.Version, pool), [new Objective("extract", true)], pool);
        Assert.IsType<ReadyResult.Ready>(await kit.Processes.ReadyAsync(attempt, report, new CatalogueHistoryStore(db.AsApp()), 120, default));
        _ = account;
    }

    [Fact]
    public async Task An_import_verifies_inserts_and_audits()
    {
        var op = Op;
        Assert.True(await history.RegisterAsync(Retiring(), op, default));
        Assert.False(await history.RegisterAsync(Retiring(), op, default));
        Assert.Equal(new[] { "already_registered", "registered" }, (await ScalarsAsync("select result ->> 'result' from operator_actions where action = 'catalogue_import' order by 1")));
        Assert.Throws<CatalogueException>(() => GameCatalogue.Load("{\"not\":\"a catalogue\"}\n"u8.ToArray())); // a mismatched file never reaches the store
    }

    async Task<string[]> ScalarsAsync(string sql)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<string>(sql)).ToArray();
    }
}
