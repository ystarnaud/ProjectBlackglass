using Dapper;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Settlement;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Catalogue;
using Blackglass.Accounts.Persistence.Hosts;
using Blackglass.Accounts.Persistence.Insurance;
using Blackglass.Accounts.Persistence.Settlement;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>
/// Stage 9 test support on top of <see cref="MissionKit"/>: a participant brought through admission, claim, register, ready (a
/// chosen pool), ticket and start (energy 10 charged), its stored snapshot, and valid format-v1 manifests built from it. The
/// admitted operative holds its starter bag: a rifle (equipped as weapon) and 2 medkits.
/// </summary>
public static class ManifestKit
{
    public static readonly PoolLine[] DefaultPool = [new(0, 0, "item.medkit", 2), new(0, 1, "item.light-vest", 1)];

    public sealed record Running(MissionKit.Mission Mission, LoadoutSnapshot Snapshot, IReadOnlyList<PoolLine> Pool, string LayoutHash, string AccountKey)
    {
        public long Account => Mission.Account;
        public Guid Attempt => Mission.Attempt;
    }

    /// <summary>A started participant. <paramref name="insured"/> activates the standard policy before admission.</summary>
    public static async Task<Running> StartAsync(MissionKit kit, TestDatabase db, PoolLine[]? pool = null, bool insured = false)
    {
        pool ??= DefaultPool;
        var issued = await kit.Accounts.CreateAccountAsync(null, null, AccountTestKit.Op);
        if (insured)
            await new InsuranceAdminStore(db.AsApp()).ActivateAsync(issued.AccountId, "standard", 86_400, Guid.NewGuid(), AccountTestKit.Op);
        var operative = await AccountTestKit.ScalarAsSuperuserAsync<long>(db, "select min(operative_id) from operatives where account_id = @a", new { a = issued.AccountId });
        var admission = await kit.Admissions.AdmitAsync(issued.AccountId, Guid.CreateVersion7(), new DeployRequest("procedural-standard", [operative]));
        var attempt = Guid.Parse(System.Text.Json.JsonDocument.Parse(admission.Body).RootElement.GetProperty("attemptId").GetString()!);
        await kit.BeatAsync();
        var claim = (ClaimResult.Claimed)await kit.Hosts.ClaimAsync("h1", await AttemptFixtures.HostSecretHashAsync(db, "h1"), Guid.NewGuid(), 120, default);
        if (claim.AttemptId != attempt) throw new InvalidOperationException("The claim took another attempt.");
        var session = ((RegisterResult.Registered)await kit.Processes.RegisterAsync(attempt, claim.Credential.Hash(), new RegisterRequest(MissionKit.Build), default)).ProcessSessionId;
        const string layout = "cf2e14dece9cc2a6";
        var table = MissionKit.Table;
        var ready = await kit.Processes.ReadyAsync(attempt, new ReadyReport(session, layout, table.Version, LootHash.Compute(table.Version, pool),
            [new Objective("extract", true)], pool), kit.Catalogues, 120, default);
        if (ready is not ReadyResult.Ready) throw new InvalidOperationException($"Ready failed: {ready}");
        var mission = new MissionKit.Mission(issued.AccountId, attempt, session, claim.Credential);
        await kit.RedeemAsync(mission);
        if (await new StartStore(db.AsApp()).StartAsync(attempt, issued.AccountId, session, default) is not StartResult.Started)
            throw new InvalidOperationException("The start failed.");
        var snapshot = LoadoutSnapshot.Parse(await AccountTestKit.ScalarAsSuperuserAsync<string>(db,
            "select loadout_snapshot::text from attempt_participants where attempt_id = @attempt", new { attempt }));
        return new Running(mission, snapshot, pool, layout, issued.AccountKey.Text);
    }

    /// <summary>
    /// A manifest that validates against <paramref name="r"/>: the snapshot's items and equipment held as they were,
    /// <paramref name="consume"/> owned medkits used, and, with <paramref name="collectAll"/>, every pool line collected and held
    /// by the operative. A success completes the required objective.
    /// </summary>
    public static ResultManifest Manifest(Running r, string outcome = "success", int consume = 0, bool collectAll = true, int elapsedMs = 1000)
    {
        var o = r.Snapshot.Operatives[0];
        var stacks = o.Stacks.Select(s => s.DefinitionId == "item.medkit" ? s with { Quantity = s.Quantity - consume } : s).Where(s => s.Quantity > 0)
            .Select(s => new OwnedStack(s.DefinitionId, s.Quantity)).ToList();
        var equipment = new List<EquipmentRef>();
        if (o.Equipment.Weapon is { } w) equipment.Add(EquipmentRef.Item("weapon", w));
        if (o.Equipment.Armor is { } a) equipment.Add(EquipmentRef.Item("armor", a));
        if (o.Equipment.Utility is { } u) equipment.Add(EquipmentRef.Item("utility", u));
        var collected = collectAll ? r.Pool.Select(l => new LineCount(l.ContainerIndex, l.Line, l.Quantity)).ToList() : [];
        var held = collectAll ? r.Pool.Select(l => new LootLot(l.ContainerIndex, l.Line, l.Quantity)).ToList() : [];
        var operative = new ManifestOperative(o.OperativeId, o.Slot, true, consume > 0 ? [new OwnedConsumption("item.medkit", consume)] : [], [],
            [.. o.Instances.Select(i => i.ItemId)], stacks, held, collected, equipment);
        var table = MissionKit.Table;
        return new ResultManifest(r.Attempt, r.Account, r.Mission.Session, MissionKit.Build.Revision, MissionKit.Build.CatalogueVersion, "ok", outcome, 1, elapsedMs,
            r.LayoutHash, table.Version, LootHash.Compute(table.Version, r.Pool), [new ObjectiveState("extract", outcome == "success" ? "completed" : "active")], [operative]);
    }

    public static ResultBody Body(ResultManifest m) => ResultBody.Read(m.ToCanonicalBytes());

    public static ResultBody Body(string raw) => ResultBody.Read(System.Text.Encoding.UTF8.GetBytes(raw));

    public static SettlementStore Store(TestDatabase db, SettlementOptions? options = null) =>
        new(db.AsApp(), new CatalogueHistoryStore(db.AsApp()), options ?? SettlementOptions.Default);

    public static async Task<ResultOutcome> SubmitAsync(SettlementStore store, TestDatabase db, ResultBody body, string hostId = "h1") =>
        await store.SubmitAsync(hostId, await AttemptFixtures.HostSecretHashAsync(db, hostId), body, default);

    /// <summary>The account's inventory as rows: (location, definition, quantity or item id, equipped slot).</summary>
    public static async Task<List<string>> InventoryAsync(TestDatabase db, long account)
    {
        await using var conn = await db.AsSuperuser().OpenConnectionAsync();
        return (await conn.QueryAsync<string>("""
            select coalesce(operative_id::text, 'stash') || ':' || definition_id || ':item:' || coalesce(equipped_slot, '-') || ':' || origin from item_instances where account_id = @account
            union all
            select coalesce(operative_id::text, 'stash') || ':' || definition_id || ':x' || quantity from item_stacks where account_id = @account
            order by 1
            """, new { account })).ToList();
    }
}
