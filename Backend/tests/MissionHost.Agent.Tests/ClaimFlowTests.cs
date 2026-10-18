using System.Text.Json;
using Blackglass.MissionHost.Agent.Tests.Support;

namespace Blackglass.MissionHost.Agent.Tests;

/// <summary>Task 3: claim durability (the owner's requirements, Stage 8 R1), crash recovery at every step, and Task 4's credential.</summary>
public class ClaimFlowTests : IDisposable
{
    readonly AgentDirs dirs = new();
    readonly FakeBackend backend = new();
    readonly FakeSystemd systemd;
    readonly CapturedLog log = new();
    readonly StoreBroker broker;

    public ClaimFlowTests()
    {
        systemd = new FakeSystemd(dirs.Config.OutboxDir);
        broker = new StoreBroker(dirs.Root);
    }

    public void Dispose() => dirs.Dispose();

    AgentHost NewAgent() => Agents.Create(dirs, backend, systemd, log, broker: broker);

    ClaimRecord OnDisk(Guid key) =>
        JsonSerializer.Deserialize<ClaimRecord>(File.ReadAllBytes(Path.Combine(dirs.Config.StateDir, "claims", key.ToString("D") + ".json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public async Task Each_step_is_durable_before_the_next_and_the_unit_starts_once_with_the_acknowledged_generation()
    {
        var a = backend.Admit(participants: ["JG", "JH"]);
        backend.BeforeClaim = key => Assert.Equal(ClaimPhase.Keyed, OnDisk(key).Phase); // the key is on disk before it is sent
        backend.BeforeLaunching = (attempt, generation) =>
        {
            var record = OnDisk(a.Key!.Value);
            Assert.Equal((ClaimPhase.Claimed, attempt, generation, 7000), (record.Phase, record.Attempt, record.Generation, record.MissionPort));
            Assert.Equal(["JG", "JH"], record.Participants);
            Assert.NotNull(record.ProcessCredential);
        };
        systemd.OnStart = attempt =>
        {
            Assert.True(backend.Acknowledged(a.Id, a.Generation)); // launching acknowledged with the current generation first
            Assert.Equal(a.Credential, broker.Credential(attempt)); // the broker put it in place for LoadCredential
        };
        using var agent = NewAgent();
        var record = await agent.Claims.ClaimNewAsync(default);
        Assert.Equal(ClaimPhase.Started, record.Phase);
        Assert.Single(systemd.Calls, c => c.StartsWith("start:", StringComparison.Ordinal));
        Assert.Null(OnDisk(record.Key).ProcessCredential); // scrubbed once the unit has it
        Assert.Null(broker.Credential(Guid.Parse(a.Id))); // discarded once the unit has it
        Assert.DoesNotContain(a.Credential!, log.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claim:before")]
    [InlineData("claim:after")] // the claim was processed and its answer lost
    [InlineData("launching:before")]
    [InlineData("launching:after")] // acknowledged, answer lost
    [InlineData("start:after")] // started, then the agent died before recording it
    public async Task A_crash_at_any_step_resumes_without_a_second_unit_or_an_unacknowledged_generation(string crashAt)
    {
        var a = backend.Admit();
        if (crashAt.StartsWith("start", StringComparison.Ordinal)) systemd.CrashAt = crashAt; else backend.CrashAt = crashAt;
        systemd.OnStart = _ => Assert.True(backend.Acknowledged(a.Id, a.Generation));
        using (var first = NewAgent())
            Assert.False(await Agents.SurvivesAsync(() => first.Claims.ClaimNewAsync(default)));
        using var second = NewAgent(); // a restart: everything comes from disk
        foreach (var record in second.Claims.Unfinished()) await second.Claims.AdvanceAsync(record, default);
        var final = second.Store.All().Single();
        Assert.Equal((ClaimPhase.Started, a.Id), (final.Phase, final.Attempt));
        Assert.Single(systemd.Calls, c => c.StartsWith("start:", StringComparison.Ordinal));
        Assert.All(backend.Calls.Where(c => c.StartsWith("claim:", StringComparison.Ordinal)), c => Assert.Equal($"claim:{final.Key}", c)); // one key throughout
        Assert.Null(broker.Credential(Guid.Parse(a.Id)));
    }

    [Fact]
    public async Task A_superseded_generation_is_never_launched_the_same_key_recovers_the_attempt()
    {
        var a = backend.Admit();
        using var agent = NewAgent();
        backend.CrashAt = "claim:after";
        Assert.False(await Agents.SurvivesAsync(() => agent.Claims.ClaimNewAsync(default)));
        var record = agent.Store.All().Single();
        // Meanwhile another recovery moved the attempt on (generation 2); the agent's own retry gets generation 3.
        await backend.ClaimAsync(record.Key, default);
        systemd.OnStart = _ => Assert.True(backend.Acknowledged(a.Id, a.Generation));
        record = await agent.Claims.AdvanceAsync(record, default);
        Assert.Equal((ClaimPhase.Started, 3), (record.Phase, record.Generation));
        Assert.DoesNotContain(backend.Calls, c => c == $"launching:{a.Id}:1" || c == $"launching:{a.Id}:2");
    }

    [Fact]
    public async Task A_stale_claim_answer_is_superseded_and_reclaimed_before_any_start()
    {
        var a = backend.Admit();
        using var agent = NewAgent();
        backend.CrashAt = "launching:before";
        Assert.False(await Agents.SurvivesAsync(() => agent.Claims.ClaimNewAsync(default)));
        await backend.ClaimAsync(agent.Store.All().Single().Key, default); // a recovery elsewhere: generation 2 now current
        systemd.OnStart = _ => Assert.True(backend.Acknowledged(a.Id, a.Generation));
        var record = await agent.Claims.AdvanceAsync(agent.Store.All().Single(), default);
        Assert.Equal(ClaimPhase.Started, record.Phase);
        Assert.Contains(backend.Calls, c => c == $"launching:{a.Id}:1"); // refused as superseded
        Assert.Contains(backend.Calls, c => c == $"launching:{a.Id}:3"); // after the reclaim
    }

    [Fact]
    public async Task No_work_deletes_the_key_and_a_cancelled_attempt_or_passed_deadline_is_never_started()
    {
        using var agent = NewAgent();
        await agent.Claims.ClaimNewAsync(default);
        Assert.Empty(agent.Store.All()); // 204: nothing kept
        var cancelled = backend.Admit();
        backend.CrashAt = "launching:before";
        Assert.False(await Agents.SurvivesAsync(() => agent.Claims.ClaimNewAsync(default)));
        cancelled.State = "cancelled";
        Assert.Equal(ClaimPhase.Abandoned, (await agent.Claims.AdvanceAsync(agent.Store.All().Single(), default)).Phase);
        var late = backend.Admit();
        late.Deadline = DateTime.UtcNow.AddSeconds(-1);
        Assert.Equal(ClaimPhase.Abandoned, (await agent.Claims.ClaimNewAsync(default)).Phase);
        Assert.DoesNotContain(systemd.Calls, c => c.StartsWith("start:", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(broker.Root)); // no credential left behind
    }

    [Fact]
    public async Task A_unit_that_already_ran_is_never_started_again()
    {
        var a = backend.Admit();
        using var agent = NewAgent();
        backend.CrashAt = "launching:after";
        Assert.False(await Agents.SurvivesAsync(() => agent.Claims.ClaimNewAsync(default)));
        Directory.CreateDirectory(dirs.OutboxOf(a.Id)); // its root ExecStartPre ran (the unit started and was forgotten)
        var record = await agent.Claims.AdvanceAsync(agent.Store.All().Single(), default);
        Assert.Equal(ClaimPhase.Started, record.Phase);
        Assert.DoesNotContain(systemd.Calls, c => c.StartsWith("start:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_start_is_recorded_once_with_the_credential_removed()
    {
        var a = backend.Admit();
        systemd.FailStart = true;
        using var agent = NewAgent();
        var record = await agent.Claims.ClaimNewAsync(default);
        Assert.Equal(ClaimPhase.StartFailed, record.Phase);
        Assert.Null(record.ProcessCredential);
        Assert.Null(broker.Credential(Guid.Parse(a.Id)));
        Assert.Contains("unit_start_failed", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_or_refused_answer_is_kept_for_the_operator_and_nothing_launches()
    {
        var malformed = new MalformedBackend(backend);
        using var agent = Agents.Create(dirs, backend, systemd, log);
        var flow = new ClaimFlow(malformed, systemd, agent.Store, broker, agent.Auth, new LaunchGate(), new MissionSlots(7000, 64), TimeProvider.System,
            dirs.Config.OutboxDir, dirs.Config.ExitDir, log.Log);
        var record = await flow.ClaimNewAsync(default);
        Assert.Equal(ClaimPhase.Abandoned, record.Phase);
        Assert.StartsWith("malformed_answer", record.Note, StringComparison.Ordinal);
        Assert.Empty(systemd.Calls);
    }

    // ---- security correction C1: the slot unit, one live unit per slot ----

    [Fact]
    public async Task C1_the_unit_started_is_the_slot_of_the_attempts_port_with_the_full_attempt_id()
    {
        var a = backend.Admit();
        a.Port = 7012;
        using var agent = NewAgent();
        Assert.Equal(ClaimPhase.Started, (await agent.Claims.ClaimNewAsync(default)).Phase);
        Assert.Equal([$"blackglass-mission-12@{a.Id}.service"], systemd.StartedUnits);
    }

    [Theory]
    [InlineData(7064)] // slot 64 does not exist
    [InlineData(6999)]
    public async Task C1_a_port_outside_the_hosts_slots_is_never_launched(int port)
    {
        var a = backend.Admit();
        a.Port = port;
        using var agent = NewAgent();
        var record = await agent.Claims.ClaimNewAsync(default);
        Assert.Equal((ClaimPhase.Abandoned, "port_outside_slots"), (record.Phase, record.Note));
        Assert.Null(record.ProcessCredential);
        Assert.DoesNotContain(backend.Calls, c => c.StartsWith("launching:", StringComparison.Ordinal)); // never reported as launching
        Assert.Empty(systemd.StartedUnits);
        Assert.DoesNotContain(broker.Calls, c => c.StartsWith("prepare:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task C1_a_slot_with_another_live_unit_is_not_started_until_that_unit_is_gone()
    {
        var other = Guid.CreateVersion7();
        systemd.Units[other] = new FakeSystemd.Unit("active", "success", 0, 0, "x");
        systemd.UnitSlots[other] = 0;
        backend.Admit(); // port 7000: slot 0 as well
        using var agent = NewAgent();
        var record = await agent.Claims.ClaimNewAsync(default);
        Assert.Equal(ClaimPhase.CredentialReady, record.Phase);
        Assert.Empty(systemd.StartedUnits); // two live units in a slot would share its uid
        Assert.Contains("slot_busy", log.Text, StringComparison.Ordinal);
        systemd.Exit(other);
        Assert.Equal(ClaimPhase.Started, (await agent.Claims.AdvanceAsync(record, default)).Phase);
    }

    [Fact]
    public async Task C1_an_incomplete_unit_listing_never_starts_a_unit()
    {
        systemd.ListFails = true;
        backend.Admit();
        using var agent = NewAgent();
        Assert.Equal(ClaimPhase.CredentialReady, (await agent.Claims.ClaimNewAsync(default)).Phase);
        Assert.Empty(systemd.StartedUnits);
    }

    // ---- security correction I3: the root broker prepares the credential; nothing starts without its confirmation ----

    [Fact]
    public async Task I3_the_credential_is_prepared_with_the_acknowledged_generation_and_discarded_after_the_start()
    {
        var a = backend.Admit();
        using var agent = NewAgent();
        await agent.Claims.ClaimNewAsync(default);
        var prepares = broker.Calls.Where(c => c.StartsWith("prepare:", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(prepares);
        Assert.All(prepares, c => Assert.Equal($"prepare:{a.Id}:{a.Generation}", c));
        Assert.Equal($"discard:{a.Id}", broker.Calls[^1]);
    }

    [Theory]
    [InlineData("write_failed")]
    [InlineData("unsafe_store")]
    [InlineData("conflict")]
    [InlineData("stale")]
    [InlineData("unavailable")]
    public async Task I3_a_credential_the_broker_did_not_confirm_never_starts_a_unit(string code)
    {
        var a = backend.Admit();
        broker.Fail = code;
        using var agent = NewAgent();
        var record = await agent.Claims.ClaimNewAsync(default);
        Assert.Equal(ClaimPhase.Acknowledged, record.Phase);
        Assert.Empty(systemd.StartedUnits);
        Assert.Contains("credential_prepare_failed", log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(a.Credential!, log.Text, StringComparison.Ordinal);
        broker.Fail = null;
        Assert.Equal(ClaimPhase.Started, (await agent.Claims.AdvanceAsync(record, default)).Phase);
    }

    [Fact]
    public async Task I3_a_broker_refusal_at_the_start_itself_fails_closed()
    {
        backend.Admit();
        using var agent = NewAgent();
        systemd.ListFails = true; // hold the claim at CredentialReady
        var record = await agent.Claims.ClaimNewAsync(default);
        Assert.Equal(ClaimPhase.CredentialReady, record.Phase);
        systemd.ListFails = false;
        broker.Fail = "unsafe_store";
        Assert.Equal(ClaimPhase.CredentialReady, (await agent.Claims.AdvanceAsync(record, default)).Phase);
        Assert.Empty(systemd.StartedUnits);
    }

    sealed class MalformedBackend(FakeBackend inner) : IBackend
    {
        public Task<ClaimAnswer> ClaimAsync(Guid key, CancellationToken ct) => Task.FromResult<ClaimAnswer>(new ClaimAnswer.Malformed("attempt_id"));
        public Task<LaunchingAnswer> LaunchingAsync(string attempt, int generation, CancellationToken ct) => inner.LaunchingAsync(attempt, generation, ct);
        public Task<HeartbeatAnswer> HeartbeatAsync(HeartbeatBody body, CancellationToken ct) => inner.HeartbeatAsync(body, ct);
        public Task<ExitedAnswer> ExitedAsync(string attempt, ExitedBody body, CancellationToken ct) => inner.ExitedAsync(attempt, body, ct);
        public Task<RelayAnswer> RelayAsync(byte[] manifest, CancellationToken ct) => inner.RelayAsync(manifest, ct);
    }
}
