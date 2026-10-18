using System.Net;
using System.Net.Sockets;
using Dapper;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Hosts;
using Blackglass.MissionHost.Agent;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Agent;

/// <summary>
/// Stage 10, the agent against the real API and database (systemd faked, the mission process simulated with the credential the
/// agent handed to the unit): claim, launch, relay to settlement, exit evidence, a backend outage that only delays, and a disabled
/// host that blocks the agent without stopping a mission or losing a result.
/// </summary>
[Trait("Requires", "Postgres")]
public sealed class AgentBackendTests : IAsyncLifetime
{
    TestDatabase db = null!;
    MissionKit kit = null!;
    string secret = "";
    readonly string root = Path.Combine(Path.GetTempPath(), "bg-agent-it-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        kit = await MissionKit.CreateAsync(db);
        secret = (await new HostAdminStore(db.AsApp()).RotateSecretAsync("h1", Op)).Secret.Text;
        foreach (var d in new[] { "outbox", "exits", "server" }) Directory.CreateDirectory(Path.Combine(root, d));
        File.WriteAllText(Path.Combine(root, "server", "build-info.txt"), $"revision={MissionKit.Build.Revision}\ncatalogueVersion={MissionKit.Build.CatalogueVersion}\n");
        WriteSecret(secret);
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally
        {
            await db.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    void WriteSecret(string text)
    {
        var temp = Path.Combine(root, "host-secret.new");
        File.WriteAllText(temp, text + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, Path.Combine(root, "host-secret"), overwrite: true); // the documented atomic rotation
    }

    sealed class Running : IDisposable
    {
        public required AgentHost Agent { get; init; }
        public required HttpClient Http { get; init; }
        public void Dispose() { Agent.Dispose(); Http.Dispose(); }
    }

    Running StartAgent(string apiUrl, StubSystemd systemd)
    {
        var config = new AgentConfig
        {
            HostId = "h1", ApiBaseUrl = new Uri(apiUrl.TrimEnd('/') + "/"), HostSecretFile = Path.Combine(root, "host-secret"),
            StateDir = Path.Combine(root, "state"), RelayDir = Path.Combine(root, "relay"), OutboxDir = Path.Combine(root, "outbox"),
            ExitDir = Path.Combine(root, "exits"), LaunchDir = Path.Combine(root, "launch"), ServerBuildInfo = Path.Combine(root, "server", "build-info.txt"),
            RelayBackoffMinMs = 10, RelayBackoffMaxMs = 20, AuthRetrySeconds = 1,
        };
        var hostSecret = new HostSecret(config.HostSecretFile);
        Assert.Equal(HostSecret.ReloadResult.Changed, hostSecret.Reload());
        var http = new HttpClient { BaseAddress = config.ApiBaseUrl, Timeout = TimeSpan.FromSeconds(10) };
        return new Running { Agent = new AgentHost(config, new BackendClient(http, hostSecret), systemd, hostSecret, new AgentLog(TextWriter.Null)), Http = http };
    }

    /// <summary>systemd for these tests: start runs the root ExecStartPre's directory creation and keeps the credential the unit
    /// would receive through LoadCredential.</summary>
    sealed class StubSystemd(string root) : ISystemd
    {
        readonly Dictionary<Guid, (string State, int Code, int Status)> units = new();
        public readonly Dictionary<Guid, string> Credentials = new();
        public readonly List<string> Stops = [];

        public Task<bool> StartAsync(Guid attempt, CancellationToken ct)
        {
            Credentials[attempt] = File.ReadAllText(Path.Combine(root, "launch", attempt.ToString("D"), "process"));
            Directory.CreateDirectory(Path.Combine(root, "outbox", attempt.ToString("D")));
            units[attempt] = ("active", 0, 0);
            return Task.FromResult(true);
        }

        public Task<bool> StopAsync(Guid attempt, CancellationToken ct) { Stops.Add(attempt.ToString("D")); return Task.FromResult(true); }

        public void Exit(Guid attempt, int code = 0, int? signal = null) =>
            units[attempt] = signal is { } s ? ("failed", 2, s) : (code == 0 ? "inactive" : "failed", 1, code);

        public Task<UnitInventory> ListAsync(CancellationToken ct) =>
            Task.FromResult(new UnitInventory(true, units.Select(u => new UnitEntry(u.Key.ToString("D"), u.Value.State)).ToList()));

        public Task<UnitState?> ShowAsync(Guid attempt, CancellationToken ct) =>
            Task.FromResult<UnitState?>(units.TryGetValue(attempt, out var u)
                ? new UnitState("loaded", u.State, u.State, "x", u.Code, u.Status, "inv")
                : new UnitState("loaded", "inactive", "dead", "success", 0, 0, ""));
    }

    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    async Task<(ManifestKit.Running Mission, Guid Attempt)> LaunchedAsync(AgentHost agent, StubSystemd systemd)
    {
        var (issued, attempt) = await ManifestKit.AdmitAsync(kit, db);
        await agent.Heartbeat.BeatAsync(default);
        Assert.NotNull(agent.Heartbeat.LastOk);
        await agent.ClaimOnceAsync(default);
        var record = agent.Store.ForAttempt(attempt.ToString("D"));
        Assert.NotNull(record);
        Assert.Equal(ClaimPhase.Started, record.Phase);
        Assert.Equal([Base58Id.Format(issued.AccountId)], record.Participants); // A10-D2, persisted with the claim
        Assert.True(OpaqueSecret.TryParse(systemd.Credentials[attempt], OpaqueSecret.ProcessCredentialPrefix, out var credential));
        return (await ManifestKit.ProcessStartAsync(kit, db, issued, attempt, credential!), attempt);
    }

    string Outbox(Guid attempt, long account, string kind) => Path.Combine(root, "outbox", attempt.ToString("D"), $"{Base58Id.Format(account)}.manifest.{kind}");

    Task<string?> OutcomeAsync(Guid attempt) => ScalarAsSuperuserAsync<string?>(db, "select outcome from participant_settlements where attempt_id = @attempt", new { attempt });

    [Fact]
    public async Task The_agent_claims_launches_relays_a_settling_result_and_then_reports_the_exit()
    {
        await using var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_CLAIM_WAIT_SECONDS"] = "0" });
        var systemd = new StubSystemd(root);
        using var running = StartAgent(api.App.Urls.Single(), systemd);
        var agent = running.Agent;
        var (mission, attempt) = await LaunchedAsync(agent, systemd);
        Assert.False(File.Exists(Path.Combine(root, "launch", attempt.ToString("D"), "process"))); // the unit has it; the agent doesn't keep it

        File.WriteAllBytes(Outbox(attempt, mission.Account, "ready"), ManifestKit.Manifest(mission).ToCanonicalBytes()); // published while running
        await agent.SuperviseOnceAsync(default);
        Assert.Equal("success", await OutcomeAsync(attempt));
        Assert.Equal("done", agent.Store.RelayEntries(attempt.ToString("D"), Base58Id.Format(mission.Account)).Single().Outcome);
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from attempt_exit_reports where attempt_id = @attempt", new { attempt }));

        systemd.Exit(attempt, code: 0);
        await agent.SuperviseOnceAsync(default);
        Assert.Equal(ClaimPhase.Reported, agent.Store.ForAttempt(attempt.ToString("D"))!.Phase);
        Assert.Equal((0, true), await RowAsSuperuserAsync<(int, bool)>(db, "select exit_code, outbox_complete from attempt_exit_reports where attempt_id = @attempt", new { attempt }));
        Assert.True(await ScalarAsSuperuserAsync<bool>(db, "select present from attempt_exit_results where attempt_id = @attempt", new { attempt }));
        Assert.True(File.Exists(Outbox(attempt, mission.Account, "ready"))); // never removed
    }

    [Fact]
    public async Task A_backend_outage_only_delays_relays_and_exit_evidence_and_nothing_is_voided_meanwhile()
    {
        var url = $"http://127.0.0.1:{FreePort()}";
        var settings = new Dictionary<string, string?> { ["BLACKGLASS_CLAIM_WAIT_SECONDS"] = "0", ["ASPNETCORE_URLS"] = url };
        var systemd = new StubSystemd(root);
        using var running = StartAgent(url, systemd);
        var agent = running.Agent;
        (ManifestKit.Running Mission, Guid Attempt) withResult, withoutResult;
        await using (var api = await ApiServer.StartAsync(db, settings))
        {
            withResult = await LaunchedAsync(agent, systemd);
            withoutResult = await LaunchedAsync(agent, systemd);
        }
        // The backend is gone (API stopped: the tunnel or the service). One result is durable but unpublished (the process died
        // between publication steps 2 and 4); the other participant has none. Both units exit.
        File.WriteAllBytes(Outbox(withResult.Attempt, withResult.Mission.Account, "written"), ManifestKit.Manifest(withResult.Mission).ToCanonicalBytes());
        systemd.Exit(withResult.Attempt, signal: 9);
        systemd.Exit(withoutResult.Attempt, code: 1);
        for (var i = 0; i < 5; i++)
        {
            await agent.SuperviseOnceAsync(default);
            await Task.Delay(30);
        }
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from participant_settlements"));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from attempt_exit_reports"));
        Assert.All(agent.Store.All(), r => Assert.Equal(ClaimPhase.Started, r.Phase));

        await using (var api = await ApiServer.StartAsync(db, settings)) // the backend is back, same address
        {
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(30);
                await agent.SuperviseOnceAsync(default);
            }
        }
        Assert.Equal("success", await OutcomeAsync(withResult.Attempt)); // relayed in place after the verified exit (A10-D1), settled
        Assert.Equal("infra_void", await OutcomeAsync(withoutResult.Attempt)); // only now: a complete scan, the participant absent
        Assert.Equal("exit_evidence", await ScalarAsSuperuserAsync<string>(db, "select void_basis from participant_settlements where attempt_id = @a", new { a = withoutResult.Attempt }));
        Assert.True(File.Exists(Outbox(withResult.Attempt, withResult.Mission.Account, "written"))); // relayed as it stands, never renamed
    }

    [Fact]
    public async Task A_disabled_host_blocks_the_agent_which_keeps_results_and_stops_no_unit_until_rotation_and_reenable()
    {
        await using var api = await ApiServer.StartAsync(db, new() { ["BLACKGLASS_CLAIM_WAIT_SECONDS"] = "0" });
        var systemd = new StubSystemd(root);
        using var running = StartAgent(api.App.Urls.Single(), systemd);
        var agent = running.Agent;
        var (mission, attempt) = await LaunchedAsync(agent, systemd);
        var admin = new HostAdminStore(db.AsApp());
        await admin.DisableAsync("h1", Op);
        await agent.Heartbeat.BeatAsync(default);
        Assert.True(agent.Auth.Blocked); // the old secret, reloaded, is still refused
        Assert.False(agent.MayClaim());
        File.WriteAllBytes(Outbox(attempt, mission.Account, "ready"), ManifestKit.Manifest(mission).ToCanonicalBytes());
        await agent.SuperviseOnceAsync(default);
        Assert.Empty(agent.Store.RelayEntries(attempt.ToString("D"), Base58Id.Format(mission.Account)));
        Assert.Empty(systemd.Stops); // a 401 never stops a mission

        // The operator rotates and re-enables, installs the new secret atomically, and signals the agent.
        WriteSecret((await admin.RotateSecretAsync("h1", Op)).Secret.Text);
        await admin.SwitchAsync("h1", HostSwitch.Enable, Op);
        agent.Auth.OnReloadSignal();
        await agent.Heartbeat.BeatAsync(default);
        Assert.False(agent.Auth.Blocked);
        await Task.Delay(1100); // past the slow retry
        await agent.SuperviseOnceAsync(default);
        Assert.Equal("revoked", agent.Store.RelayEntries(attempt.ToString("D"), Base58Id.Format(mission.Account)).Single().Outcome); // 409 AttemptRevoked
        Assert.Null(await OutcomeAsync(attempt)); // never settled after the revocation
        Assert.True(File.Exists(Outbox(attempt, mission.Account, "ready"))); // kept for the operator's import
        Assert.Empty(systemd.Stops);
    }
}
