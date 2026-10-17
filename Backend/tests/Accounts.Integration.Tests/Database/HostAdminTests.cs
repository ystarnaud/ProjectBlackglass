using System.Text.RegularExpressions;
using Dapper;
using Blackglass.Accounts.Api;
using Blackglass.Accounts.Api.Admin;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.Accounts.Domain.Hosts;
using Blackglass.Accounts.IntegrationTests.Support;
using Blackglass.Accounts.Persistence.Attempts;
using Blackglass.Accounts.Persistence.Hosts;
using static Blackglass.Accounts.IntegrationTests.Support.AccountTestKit;

namespace Blackglass.Accounts.IntegrationTests.Database;

/// <summary>
/// The operator verbs for mission hosts and builds (owner rulings B3, B4): <c>host register</c> (agent hosts only through it;
/// the secret is generated, stored as SHA-256 and printed once), <c>host rotate-secret</c>, <c>host drain|enable|disable</c>,
/// <c>host capacity</c>, <c>build allow</c> and <c>build disable</c>. Each is confirmed, audited in its own transaction, and
/// never prints or audits a hash; none loads the catalogue.
/// </summary>
[Trait("Requires", "Postgres")]
public class HostAdminTests : IAsyncLifetime
{
    TestDatabase db = null!;
    HostAdminStore hosts = null!;
    const string Catalogue = "sha256:abababababababababababababababababababababababababababababababab";

    public async Task InitializeAsync()
    {
        db = await MigratedAsync();
        hosts = new HostAdminStore(db.AsApp());
    }

    public async Task DisposeAsync()
    {
        try { Assert.Empty(await InvariantFailuresAsync(db)); }
        finally { await db.DisposeAsync(); }
    }

    Task<long> CountAsync(string table) => ScalarAsSuperuserAsync<long>(db, $"select count(*) from {table}");

    Task<string> RowAsync(string hostId) => ScalarAsSuperuserAsync<string>(db,
        "select row(host_id, kind, enabled, drained, capacity, port_base, encode(secret_hash, 'hex'), secret_rotated_at, last_heartbeat_at, created_at)::text from mission_hosts where host_id = @hostId",
        new { hostId });

    async Task<List<(string Action, string? HostId, string Result)>> AuditAsync()
    {
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        return (await su.QueryAsync<(string, string?, string)>("select action, host_id, result::text from operator_actions order by id")).ToList();
    }

    static void AssertNoSecret(string text, OpaqueSecret secret)
    {
        Assert.DoesNotContain(secret.Text, text);
        Assert.DoesNotContain(Convert.ToHexString(secret.Hash()), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(secret.Hash()), text);
    }

    // ---- store ----

    [Fact]
    public async Task Register_stores_only_the_hash_and_returns_the_secret_once()
    {
        var registered = await hosts.RegisterAsync("bg-dev-mission01", 4, 7000, Op);
        Assert.Equal(("bg-dev-mission01", 4, 7000), (registered.HostId, registered.Capacity, registered.PortBase));
        Assert.Equal(OpaqueSecret.HostSecretPrefix, registered.Secret.Prefix);
        await using var su = await db.AsSuperuser().OpenConnectionAsync();
        var row = await su.QuerySingleAsync<(string Kind, bool Enabled, bool Drained, int Capacity, int PortBase, byte[] Hash, DateTime? Heartbeat)>(
            "select kind, enabled, drained, capacity, port_base, secret_hash, last_heartbeat_at from mission_hosts where host_id = 'bg-dev-mission01'");
        Assert.Equal(("agent", true, false, 4, 7000, (DateTime?)null), (row.Kind, row.Enabled, row.Drained, row.Capacity, row.PortBase, row.Heartbeat));
        Assert.Equal(registered.Secret.Hash(), row.Hash);
        var (action, hostId, result) = Assert.Single(await AuditAsync());
        Assert.Equal(("host_register", "bg-dev-mission01"), (action, hostId));
        AssertNoSecret(result, registered.Secret);
        Assert.DoesNotContain("secret", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_refuses_a_taken_id_and_writes_nothing()
    {
        await hosts.RegisterAsync("h1", 2, 7000, Op);
        var before = await RowAsync("h1");
        var e = await Assert.ThrowsAsync<MissionAdminException>(() => hosts.RegisterAsync("h1", 3, 8000, Op));
        Assert.Equal(MissionAdminError.HostExists, e.Error);
        Assert.Equal(before, await RowAsync("h1"));
        Assert.Equal((1L, 1L), (await CountAsync("mission_hosts"), await CountAsync("operator_actions")));
    }

    [Theory]
    [InlineData("Bad_Host", 1, 7000)]
    [InlineData("h1", 0, 7000)]
    [InlineData("h1", 1001, 7000)]
    [InlineData("h1", 65, 7000)] // Checkpoint 1 M4: at most the units one heartbeat can list
    [InlineData("h1", 1, 1023)]
    [InlineData("h1", 2, 65535)]
    public void Registration_values_are_checked_before_any_connection(string hostId, int capacity, int portBase) =>
        Assert.Equal(MissionAdminError.InvalidHost, Assert.Throws<MissionAdminException>(() => HostAdminStore.ValidateRegistration(hostId, capacity, portBase)).Error);

    [Fact]
    public async Task Rotate_invalidates_the_old_secret_and_only_for_an_agent_host()
    {
        var first = (await hosts.RegisterAsync("h1", 2, 7000, Op)).Secret;
        var rotated = await hosts.RotateSecretAsync("h1", Op);
        Assert.NotEqual(first.Text, rotated.Secret.Text);
        Assert.Equal(rotated.Secret.Hash(), await ScalarAsSuperuserAsync<byte[]>(db, "select secret_hash from mission_hosts where host_id = 'h1'"));
        Assert.Equal(rotated.RotatedAt, await ScalarAsSuperuserAsync<DateTime>(db, "select secret_rotated_at from mission_hosts where host_id = 'h1'"));
        Assert.Equal(0L, await ScalarAsSuperuserAsync<long>(db, "select count(*) from mission_hosts where secret_hash = @hash", new { hash = first.Hash() }));
        // A development fixture or an unknown id: UnknownHost, nothing written.
        await ExecuteAsSuperuserAsync(db, "insert into mission_hosts (host_id, kind, secret_hash, capacity, enabled) values ('dev-1', 'dev_fixture', decode(repeat('01', 32), 'hex'), 1, true)");
        foreach (var id in new[] { "dev-1", "nope" })
            Assert.Equal(MissionAdminError.UnknownHost, (await Assert.ThrowsAsync<MissionAdminException>(() => hosts.RotateSecretAsync(id, Op))).Error);
        var audits = await AuditAsync();
        Assert.Equal(["host_register", "host_rotate_secret"], audits.Select(a => a.Action));
        foreach (var (_, _, result) in audits) { AssertNoSecret(result, first); AssertNoSecret(result, rotated.Secret); }
    }

    [Fact]
    public async Task Drain_enable_disable_and_capacity_change_only_their_columns()
    {
        await hosts.RegisterAsync("h1", 2, 7000, Op);
        const string Fixed = "select row(host_id, kind, port_base, secret_hash, secret_rotated_at, last_heartbeat_at, build_revision, created_at)::text from mission_hosts";
        var fixedBefore = await ScalarAsSuperuserAsync<string>(db, Fixed);
        async Task<(bool, bool, int)> SwitchesAsync()
        {
            await using var conn = await db.AsSuperuser().OpenConnectionAsync();
            return await conn.QuerySingleAsync<(bool, bool, int)>("select enabled, drained, capacity from mission_hosts where host_id = 'h1'");
        }

        var drained = await hosts.SwitchAsync("h1", HostSwitch.Drain, Op);
        Assert.Equal((true, true, 2), (drained.Enabled, drained.Drained, drained.Capacity));
        Assert.Equal((true, true, 2), await SwitchesAsync());
        await hosts.SwitchAsync("h1", HostSwitch.Enable, Op); // enable also undrains
        Assert.Equal((true, false, 2), await SwitchesAsync());
        await hosts.SetCapacityAsync("h1", 5, Op);
        Assert.Equal((true, false, 5), await SwitchesAsync());
        Assert.Equal(fixedBefore, await ScalarAsSuperuserAsync<string>(db, Fixed)); // drain, enable and capacity change only their columns
        // Disable is emergency isolation (R8): disabled, the secret revoked (enable refused until a rotation), and audited.
        await hosts.SwitchAsync("h1", HostSwitch.Disable, Op);
        Assert.Equal((false, false, 5), await SwitchesAsync());
        Assert.Equal(MissionAdminError.SecretRevoked, (await Assert.ThrowsAsync<MissionAdminException>(() => hosts.SwitchAsync("h1", HostSwitch.Enable, Op))).Error);
        await hosts.RotateSecretAsync("h1", Op);
        await hosts.SwitchAsync("h1", HostSwitch.Enable, Op);
        Assert.Equal((true, false, 5), await SwitchesAsync());
        Assert.Equal(["host_register", "host_drain", "host_enable", "host_capacity", "host_disable", "host_rotate_secret", "host_enable"], (await AuditAsync()).Select(a => a.Action));
        Assert.All(await AuditAsync(), a => Assert.Equal("h1", a.HostId));

        Assert.Equal(MissionAdminError.UnknownHost, (await Assert.ThrowsAsync<MissionAdminException>(() => hosts.SwitchAsync("nope", HostSwitch.Drain, Op))).Error);
        // A capacity past the port space (port base + capacity > 65536) is refused by 0008's CHECK: InvalidHost, nothing written.
        await hosts.RegisterAsync("high", 1, 65000, Op);
        Assert.Equal(MissionAdminError.InvalidHost, (await Assert.ThrowsAsync<MissionAdminException>(() => hosts.SetCapacityAsync("high", 1000, Op))).Error);
        Assert.Equal(1, await ScalarAsSuperuserAsync<int>(db, "select capacity from mission_hosts where host_id = 'high'"));
    }

    [Fact]
    public async Task Build_allow_is_idempotent_adds_templates_and_re_enables()
    {
        var build = new BuildIdentity("r1", Catalogue);
        var first = await hosts.AllowBuildAsync(build, [new TemplateRef("procedural-standard", 1)], Op);
        Assert.Equal((true, true, false, 1), (first.Enabled, first.Created, first.ReEnabled, first.TemplatesAdded));
        var again = await hosts.AllowBuildAsync(build, [new TemplateRef("procedural-standard", 1)], Op);
        Assert.Equal((true, false, false, 0), (again.Enabled, again.Created, again.ReEnabled, again.TemplatesAdded));
        Assert.Equal((1L, 1L), (await CountAsync("allowed_builds"), await CountAsync("allowed_build_templates")));

        await hosts.DisableBuildAsync(build, Op);
        Assert.False(await ScalarAsSuperuserAsync<bool>(db, "select enabled from allowed_builds"));
        var reEnabled = await hosts.AllowBuildAsync(build, [new TemplateRef("procedural-standard", 1)], Op);
        Assert.Equal((true, false, true), (reEnabled.Enabled, reEnabled.Created, reEnabled.ReEnabled));
        Assert.Equal([new TemplateRef("procedural-standard", 1)], reEnabled.Templates);

        // An unknown template version: UnknownTemplate, nothing written (no build, no audit).
        var before = await CountAsync("operator_actions");
        var e = await Assert.ThrowsAsync<MissionAdminException>(() => hosts.AllowBuildAsync(new BuildIdentity("r2", Catalogue), [new TemplateRef("procedural-standard", 9)], Op));
        Assert.Equal(MissionAdminError.UnknownTemplate, e.Error);
        Assert.Equal((1L, before), (await CountAsync("allowed_builds"), await CountAsync("operator_actions")));
        Assert.Equal(MissionAdminError.UnknownBuild, (await Assert.ThrowsAsync<MissionAdminException>(() => hosts.DisableBuildAsync(new BuildIdentity("r9", Catalogue), Op))).Error);
        Assert.Equal(["build_allow", "build_allow", "build_disable", "build_allow"], (await AuditAsync()).Select(a => a.Action));
    }

    [Theory]
    [InlineData("procedural-standard:1", true)]
    [InlineData("procedural-standard:1,zero-cost:2", true)]
    [InlineData("", false)]
    [InlineData("procedural-standard", false)]
    [InlineData("procedural-standard:0", false)]
    [InlineData("procedural-standard:x", false)]
    [InlineData("Bad:1", false)]
    [InlineData("procedural-standard:1,procedural-standard:1", false)]
    [InlineData("procedural-standard:1,", false)]
    public void Template_lists_parse_strictly(string text, bool ok)
    {
        if (ok) Assert.NotEmpty(HostAdminStore.ParseTemplates(text));
        else Assert.Equal(MissionAdminError.InvalidBuild, Assert.Throws<MissionAdminException>(() => HostAdminStore.ParseTemplates(text)).Error);
    }

    [Fact]
    public async Task Build_disable_keeps_running_attempts()
    {
        var account = await NewAccountAsync();
        var attempt = await AttemptFixtures.AdmitAsync(db, account);
        foreach (var s in new[] { "assigned", "launching", "ready", "running" }) await AttemptFixtures.MoveAsync(db, attempt, s);
        const string Row = "select row(state, build_revision, host_id, started_at)::text from mission_attempts where attempt_id = @attempt";
        var before = await ScalarAsSuperuserAsync<string>(db, Row, new { attempt });
        var catalogue = await ScalarAsSuperuserAsync<string>(db, "select catalogue_version from mission_attempts where attempt_id = @attempt", new { attempt });
        var disabled = await hosts.DisableBuildAsync(new BuildIdentity(AttemptFixtures.FixtureBuild, catalogue), Op);
        Assert.False(disabled.Enabled);
        Assert.Equal(before, await ScalarAsSuperuserAsync<string>(db, Row, new { attempt }));
    }

    async Task<long> NewAccountAsync()
    {
        await using var conn = await db.AsApp().OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("insert into accounts (status) values ('active') returning account_id");
    }

    // ---- CLI ----

    async Task<(int Code, string Out, string Err)> CliAsync(string? typed, params string[] args) => await CliAsync(db.EnvFor("app"), typed, args);

    static async Task<(int Code, string Out, string Err)> CliAsync(Dictionary<string, string?> env, string? typed, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await AdminCommand.RunAsync(args, env, output, error, default, input: typed is null ? null : new StringReader(typed),
            loadCatalogue: () => throw new InvalidOperationException("The host and build verbs never load the catalogue."));
        return (code, output.ToString(), error.ToString());
    }

    static string[] Verb(params string[] words) => [.. words, "--operator", "ops", "--reason", "test"];
    static string Field(string output, string name) => Regex.Match(output, $"(?m)^{Regex.Escape(name)}=(.*)$").Groups[1].Value.TrimEnd('\r');
    static string[] Names(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').Split('=')[0]).ToArray();

    [Fact]
    public async Task Host_register_shows_its_target_acts_only_when_confirmed_and_prints_the_secret_once()
    {
        var args = Verb("host", "register", "--host-id", "h1", "--capacity", "3", "--port-base", "7100");
        var preview = await CliAsync(null, args);
        Assert.Equal(ExitCodes.Usage, preview.Code);
        Assert.Equal(["target.hostId", "target.hostExists", "target.kind", "target.capacity", "target.portBase"], Names(preview.Out));
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync("h1\n", args)).Code);
        Assert.Equal((0L, 0L), (await CountAsync("mission_hosts"), await CountAsync("operator_actions")));

        var (code, output, error) = await CliAsync("register\n", args);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(["hostId", "kind", "capacity", "portBase", "hostSecret", "correlationId"], Names(output).Where(n => !n.StartsWith("target.", StringComparison.Ordinal)));
        Assert.True(OpaqueSecret.TryParse(Field(output, "hostSecret"), OpaqueSecret.HostSecretPrefix, out var secret));
        Assert.Equal(secret!.Hash(), await ScalarAsSuperuserAsync<byte[]>(db, "select secret_hash from mission_hosts where host_id = 'h1'"));
        Assert.Single(Regex.Matches(output + error, "hs1_"));
        AssertNoSecret(error, secret);
        // Values are checked before any connection: a 64 against an unreachable database.
        var offline = db.EnvFor("app");
        offline["BLACKGLASS_DB_PORT"] = "1";
        foreach (var bad in new[] { Verb("host", "register", "--host-id", "H_1", "--capacity", "3", "--port-base", "7100", "--yes"),
                                     Verb("host", "register", "--host-id", "h2", "--capacity", "3", "--port-base", "80", "--yes"),
                                     Verb("build", "allow", "--revision", "r1", "--catalogue-version", "sha256:AB", "--templates", "procedural-standard:1", "--yes"),
                                     Verb("build", "allow", "--revision", "r1", "--catalogue-version", Catalogue, "--templates", "procedural-standard", "--yes") })
            Assert.Equal((ExitCodes.Usage, ""), ((await CliAsync(offline, null, bad)) is var r ? (r.Code, r.Out) : default));
    }

    [Fact]
    public async Task Host_switch_verbs_are_confirmed_by_the_host_id_and_audited()
    {
        await hosts.RegisterAsync("h1", 2, 7000, Op);
        var drain = Verb("host", "drain", "--host-id", "h1");
        var preview = await CliAsync(null, drain);
        Assert.Equal(ExitCodes.Usage, preview.Code);
        Assert.Equal(["target.hostId", "target.kind", "target.enabled", "target.drained", "target.capacity", "target.portBase", "target.liveSlots",
                      "target.lastHeartbeatAt", "target.build"], Names(preview.Out));
        Assert.Equal(ExitCodes.NotConfirmed, (await CliAsync("register\n", drain)).Code);
        var (code, output, _) = await CliAsync("h1\n", drain);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(("h1", "true", "true", "2"), (Field(output, "hostId"), Field(output, "enabled"), Field(output, "drained"), Field(output, "capacity")));
        Assert.Equal(ExitCodes.Ok, (await CliAsync(null, [.. Verb("host", "capacity", "--host-id", "h1", "--capacity", "4"), "--yes"])).Code);
        Assert.Equal(ExitCodes.NotFound, (await CliAsync(null, [.. Verb("host", "enable", "--host-id", "nope"), "--yes"])).Code);

        var rotate = await CliAsync("h1\n", Verb("host", "rotate-secret", "--host-id", "h1"));
        Assert.Equal(ExitCodes.Ok, rotate.Code);
        Assert.True(OpaqueSecret.TryParse(Field(rotate.Out, "hostSecret"), OpaqueSecret.HostSecretPrefix, out var rotated));
        Assert.Equal(rotated!.Hash(), await ScalarAsSuperuserAsync<byte[]>(db, "select secret_hash from mission_hosts where host_id = 'h1'"));
        foreach (var (_, hostId, result) in await AuditAsync()) { Assert.Equal("h1", hostId); AssertNoSecret(result, rotated); }
    }

    [Fact]
    public async Task Build_verbs_are_confirmed_by_the_revision()
    {
        var allow = Verb("build", "allow", "--revision", "r1", "--catalogue-version", Catalogue, "--templates", "procedural-standard:1");
        var preview = await CliAsync(null, allow);
        Assert.Equal(ExitCodes.Usage, preview.Code);
        Assert.Equal(["target.buildRevision", "target.catalogueVersion", "target.buildExists", "target.enabled", "target.templates", "target.proposedTemplates"], Names(preview.Out));
        Assert.Equal(0L, await CountAsync("allowed_builds"));
        var (code, output, _) = await CliAsync("r1\n", allow);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(("r1", Catalogue, "true", "true", "procedural-standard:1"), (Field(output, "buildRevision"), Field(output, "catalogueVersion"),
            Field(output, "enabled"), Field(output, "created"), Field(output, "templates")));
        var disable = await CliAsync("r1\n", Verb("build", "disable", "--revision", "r1", "--catalogue-version", Catalogue));
        Assert.Equal((ExitCodes.Ok, "false"), (disable.Code, Field(disable.Out, "enabled")));
        Assert.Equal(ExitCodes.NotFound, (await CliAsync(null, [.. Verb("build", "disable", "--revision", "r9", "--catalogue-version", Catalogue), "--yes"])).Code);
    }
}
