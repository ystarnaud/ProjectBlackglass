using Blackglass.Accounts.Domain.Hosts;

namespace Blackglass.Accounts.Domain.Attempts;

// The agent and process request bodies (spec §16.2, §16.3; plan revision 2 §C). Each Parse is strict (StrictJson) and
// throws RequestFormatException. No body names its own host, attempt or account principal: those come from the credential
// and the route.

/// <summary>A body that must be exactly <c>{}</c>.</summary>
public static class EmptyRequest
{
    public static void Parse(ReadOnlySpan<byte> utf8Json) => StrictJson.Read(utf8Json, root => StrictJson.Members(root));
}

/// <summary>One mission unit the agent found: its attempt and systemd ActiveState.</summary>
public sealed record UnitReport(Guid AttemptId, string ActiveState);

/// <summary>
/// The agent heartbeat: {buildRevision, catalogueVersion, inventory: {complete, units: [{attemptId, activeState}]},
/// outboxPending}. complete is true only for a successful, complete, authoritative enumeration of the host's mission
/// units (plan revision 2 R2); only then can a missing unit free a slot.
/// </summary>
public sealed record HeartbeatRequest(BuildIdentity Build, bool InventoryComplete, IReadOnlyList<UnitReport> Units, int OutboxPending)
{
    public const int MaxUnits = 64;
    static readonly string[] ActiveStates = ["active", "reloading", "inactive", "failed", "activating", "deactivating", "maintenance", "refreshing"];

    public static HeartbeatRequest Parse(ReadOnlySpan<byte> utf8Json) => StrictJson.Read(utf8Json, root =>
    {
        var m = StrictJson.Members(root, "buildRevision", "catalogueVersion", "inventory", "outboxPending");
        if (!BuildIdentity.TryParse(StrictJson.String(m["buildRevision"], 64), StrictJson.String(m["catalogueVersion"], 71), out var build))
            throw StrictJson.Refuse();
        var inventory = StrictJson.Members(m["inventory"], "complete", "units");
        var units = StrictJson.Distinct(StrictJson.Array(inventory["units"], 0, MaxUnits, e =>
        {
            var u = StrictJson.Members(e, "attemptId", "activeState");
            var state = StrictJson.String(u["activeState"], 16);
            if (Array.IndexOf(ActiveStates, state) < 0) throw StrictJson.Refuse();
            return new UnitReport(StrictJson.Uuid(u["attemptId"]), state);
        }), u => u.AttemptId);
        return new HeartbeatRequest(build!, StrictJson.Bool(inventory["complete"]), units, StrictJson.Int(m["outboxPending"], 0, 1_000_000));
    });
}

/// <summary>{credentialGeneration}: the generation of the claim response the agent is about to launch with (R1).</summary>
public sealed record LaunchingRequest(int CredentialGeneration)
{
    public static LaunchingRequest Parse(ReadOnlySpan<byte> utf8Json) => StrictJson.Read(utf8Json, root =>
        new LaunchingRequest(StrictJson.Int(StrictJson.Members(root, "credentialGeneration")["credentialGeneration"], 1, int.MaxValue)));
}

/// <summary>One participant's result presence in an exit report: whether the agent found its result file.</summary>
public sealed record ExitResult(long AccountId, bool Present);

/// <summary>
/// The agent's exit report: {exitCode (0..255 or null), signal ("SIG…" or null), results: [{accountId, present}]} with each
/// account at most once (plan revision 2 R6).
/// </summary>
/// <param name="OutboxComplete">The agent declared a complete, successful outbox scan (S9-B7): the optional
/// <c>outbox: {complete: boolean}</c>, false when absent.</param>
public sealed record ExitedRequest(int? ExitCode, string? Signal, IReadOnlyList<ExitResult> Results, bool OutboxComplete = false)
{
    public const int MaxResults = 16;

    public static ExitedRequest Parse(ReadOnlySpan<byte> utf8Json) => StrictJson.Read(utf8Json, root =>
    {
        var hasOutbox = root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("outbox", out _);
        var m = hasOutbox ? StrictJson.Members(root, "exitCode", "signal", "results", "outbox") : StrictJson.Members(root, "exitCode", "signal", "results");
        var complete = hasOutbox && StrictJson.Bool(StrictJson.Members(m["outbox"], "complete")["complete"]);
        var signal = StrictJson.NullableString(m["signal"], 16);
        if (signal is not null && !IsSignal(signal)) throw StrictJson.Refuse();
        var results = StrictJson.Distinct(StrictJson.Array(m["results"], 0, MaxResults, e =>
        {
            var r = StrictJson.Members(e, "accountId", "present");
            return new ExitResult(StrictJson.AccountId(r["accountId"]), StrictJson.Bool(r["present"]));
        }), r => r.AccountId);
        return new ExitedRequest(StrictJson.NullableInt(m["exitCode"], 0, 255), signal, results, complete);
    });

    static bool IsSignal(string s) =>
        s.Length is > 3 and <= 16 && s.StartsWith("SIG", StringComparison.Ordinal) && s.Skip(3).All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));
}

/// <summary>The process session request: {buildRevision, catalogueVersion}.</summary>
public sealed record RegisterRequest(BuildIdentity Build)
{
    public static RegisterRequest Parse(ReadOnlySpan<byte> utf8Json) => StrictJson.Read(utf8Json, root =>
    {
        var m = StrictJson.Members(root, "buildRevision", "catalogueVersion");
        return BuildIdentity.TryParse(StrictJson.String(m["buildRevision"], 64), StrictJson.String(m["catalogueVersion"], 71), out var build)
            ? new RegisterRequest(build!)
            : throw StrictJson.Refuse();
    });
}

/// <summary>{processSessionId, reason}: reason is a lowercase word of up to 64 characters.</summary>
public sealed record FailedRequest(Guid ProcessSessionId, string Reason)
{
    public static FailedRequest Parse(ReadOnlySpan<byte> utf8Json) => StrictJson.Read(utf8Json, root =>
    {
        var m = StrictJson.Members(root, "processSessionId", "reason");
        return new FailedRequest(StrictJson.Uuid(m["processSessionId"]), StrictJson.Word(m["reason"], 64));
    });
}

/// <summary>{manifestHash}: the canonical manifest hash the process recorded (S9-B5, plan §H step 3).</summary>
public sealed record RecordedRequest(string ManifestHash)
{
    public static RecordedRequest Parse(ReadOnlySpan<byte> utf8Json) => StrictJson.Read(utf8Json, root =>
    {
        var hash = StrictJson.String(StrictJson.Members(root, "manifestHash")["manifestHash"], 71);
        return Hosts.BuildIdentity.IsWellFormedCatalogueVersion(hash) ? new RecordedRequest(hash) : throw StrictJson.Refuse(); // the same sha256:<hex> form
    });
}

/// <summary>{processSessionId}.</summary>
public sealed record StartRequest(Guid ProcessSessionId)
{
    public static StartRequest Parse(ReadOnlySpan<byte> utf8Json) => StrictJson.Read(utf8Json, root =>
        new StartRequest(StrictJson.Uuid(StrictJson.Members(root, "processSessionId")["processSessionId"])));
}

/// <summary>{processSessionId, ticket}: the ticket's text is checked by the store (a malformed one is the uniform 401).</summary>
public sealed record RedeemRequest(Guid ProcessSessionId, string Ticket)
{
    public static RedeemRequest Parse(ReadOnlySpan<byte> utf8Json) => StrictJson.Read(utf8Json, root =>
    {
        var m = StrictJson.Members(root, "processSessionId", "ticket");
        return new RedeemRequest(StrictJson.Uuid(m["processSessionId"]), StrictJson.String(m["ticket"], 64));
    });
}

/// <summary>One participant's connection state in a process heartbeat.</summary>
public sealed record ParticipantConnection(long AccountId, bool Connected);

/// <summary>{processSessionId, phase (a lowercase word of up to 32 characters), participants: [{accountId, connected}]} with each account at most once.</summary>
public sealed record ProcessHeartbeatRequest(Guid ProcessSessionId, string Phase, IReadOnlyList<ParticipantConnection> Participants)
{
    public const int MaxParticipants = 16;

    public static ProcessHeartbeatRequest Parse(ReadOnlySpan<byte> utf8Json) => StrictJson.Read(utf8Json, root =>
    {
        var m = StrictJson.Members(root, "processSessionId", "phase", "participants");
        var participants = StrictJson.Distinct(StrictJson.Array(m["participants"], 0, MaxParticipants, e =>
        {
            var p = StrictJson.Members(e, "accountId", "connected");
            return new ParticipantConnection(StrictJson.AccountId(p["accountId"]), StrictJson.Bool(p["connected"]));
        }), p => p.AccountId);
        return new ProcessHeartbeatRequest(StrictJson.Uuid(m["processSessionId"]), StrictJson.Word(m["phase"], 32), participants);
    });
}
