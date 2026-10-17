using System.Text.Json;
using Dapper;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Hosts;
using Blackglass.Accounts.Persistence.Accounts;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Hosts;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>
/// The real Stage 8 lifecycle through the stores, for tests: an agent host <c>h1</c> reporting build <c>r1</c> of the embedded
/// catalogue (allowlisted for procedural-standard v1), real admissions (energy reserved from a full wallet of 50), and an
/// attempt taken through claim, register and ready, with a ticket redeemed by its session.
/// </summary>
public sealed class MissionKit
{
    public static readonly GameCatalogue Catalogue = GameCatalogue.Embedded;
    public static readonly BuildIdentity Build = new("r1", GameCatalogue.Embedded.Version);
    public static readonly LootTable Table = GameCatalogue.Embedded.Document.LootTable;

    readonly TestDatabase db;
    public AccountAdminStore Accounts { get; }
    public AdmissionStore Admissions { get; }
    public HostStore Hosts { get; }
    public ProcessStore Processes { get; }
    public TicketStore Tickets { get; }
    public Blackglass.Accounts.Persistence.Catalogue.CatalogueHistoryStore Catalogues { get; }

    MissionKit(TestDatabase db)
    {
        this.db = db;
        Accounts = new AccountAdminStore(db.AsApp());
        Admissions = new AdmissionStore(db.AsApp(), new ServiceHealth(devHostsEnabled: false, leaseSeconds: 30), new CryptoAttemptSecrets(), Catalogue, 60);
        Hosts = new HostStore(db.AsApp(), leaseSeconds: 30);
        Processes = new ProcessStore(db.AsApp());
        Tickets = new TicketStore(db.AsApp());
        Catalogues = new Blackglass.Accounts.Persistence.Catalogue.CatalogueHistoryStore(db.AsApp());
    }

    public static async Task<MissionKit> CreateAsync(TestDatabase db, int capacity = 16)
    {
        var kit = new MissionKit(db);
        var admin = new HostAdminStore(db.AsApp());
        await admin.AllowBuildAsync(Build, [new TemplateRef("procedural-standard", 1)], AccountTestKit.Op);
        await admin.RegisterAsync("h1", capacity, 7000, AccountTestKit.Op);
        await kit.BeatAsync();
        return kit;
    }

    public async Task BeatAsync() => await Hosts.HeartbeatAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), new HeartbeatRequest(Build, true, [], 0), default);

    public sealed record Mission(long Account, Guid Attempt, Guid Session, OpaqueSecret Credential);

    /// <summary>A new account's admission of its first operative (energy 10 reserved).</summary>
    public async Task<(long Account, Guid Attempt)> AdmitAsync()
    {
        var issued = await Accounts.CreateAccountAsync(null, null, AccountTestKit.Op);
        var operative = await AccountTestKit.ScalarAsSuperuserAsync<long>(db, "select min(operative_id) from operatives where account_id = @a", new { a = issued.AccountId });
        var admission = await Admissions.AdmitAsync(issued.AccountId, Guid.CreateVersion7(), new DeployRequest("procedural-standard", [operative]));
        return (issued.AccountId, Guid.Parse(JsonDocument.Parse(admission.Body).RootElement.GetProperty("attemptId").GetString()!));
    }

    /// <summary>An admitted attempt claimed by h1, registered and ready (a one-line pool, one required objective).</summary>
    public async Task<Mission> ReadyAsync()
    {
        var (account, attempt) = await AdmitAsync();
        await BeatAsync();
        var claim = (ClaimResult.Claimed)await Hosts.ClaimAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), Guid.NewGuid(), 120, default);
        if (claim.AttemptId != attempt) throw new InvalidOperationException("The claim took another attempt.");
        var session = ((RegisterResult.Registered)await Processes.RegisterAsync(attempt, claim.Credential.Hash(), new RegisterRequest(Build), default)).ProcessSessionId;
        PoolLine[] pool = [new(0, 0, "item.medkit", 1)];
        var ready = await Processes.ReadyAsync(attempt, new ReadyReport(session, "cf2e14dece9cc2a6", Table.Version, LootHash.Compute(Table.Version, pool),
            [new Objective("extract", true)], pool), Catalogues, 120, default);
        if (ready is not ReadyResult.Ready) throw new InvalidOperationException($"Ready failed: {ready}");
        return new Mission(account, attempt, session, claim.Credential);
    }

    /// <summary>A ticket minted for the participant and redeemed by the attempt's session.</summary>
    public async Task RedeemAsync(Mission m, long? account = null)
    {
        var minted = (MintResult.Minted)await Tickets.MintAsync(account ?? m.Account, m.Attempt, 120, default);
        if (await Tickets.RedeemAsync(m.Attempt, m.Session, minted.Ticket.Text, default) is not RedeemResult.Redeemed)
            throw new InvalidOperationException("The ticket was not redeemed.");
    }
}
