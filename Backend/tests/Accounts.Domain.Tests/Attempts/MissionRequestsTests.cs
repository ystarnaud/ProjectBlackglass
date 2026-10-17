using System.Text;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Hosts;

namespace Blackglass.Accounts.Domain.Tests.Attempts;

public class MissionRequestsTests
{
    const string A = "0192f0c1-0000-7000-8000-000000000001";
    const string B = "0192f0c1-0000-7000-8000-000000000002";
    const string Cat = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Valid_agent_and_process_requests_parse()
    {
        var hb = HeartbeatRequest.Parse(U($$"""{"buildRevision":"r1","catalogueVersion":"{{Cat}}","inventory":{"complete":true,"units":[{"attemptId":"{{A}}","activeState":"active"}]},"outboxPending":0}"""));
        Assert.Equal(new BuildIdentity("r1", Cat), hb.Build);
        Assert.True(hb.InventoryComplete);
        Assert.Equal([new UnitReport(Guid.Parse(A), "active")], hb.Units);

        Assert.Equal(3, LaunchingRequest.Parse(U("""{"credentialGeneration":3}""")).CredentialGeneration);

        var exited = ExitedRequest.Parse(U("""{"exitCode":null,"signal":"SIGKILL","results":[{"accountId":"2","present":false}]}"""));
        Assert.Null(exited.ExitCode);
        Assert.Equal("SIGKILL", exited.Signal);
        Assert.Equal([new ExitResult(1, false)], exited.Results);

        Assert.Equal(new BuildIdentity("r1", Cat), RegisterRequest.Parse(U($$"""{"buildRevision":"r1","catalogueVersion":"{{Cat}}"}""")).Build);
        Assert.Equal(new FailedRequest(Guid.Parse(A), "generation-failed"), FailedRequest.Parse(U($$"""{"processSessionId":"{{A}}","reason":"generation-failed"}""")));
        Assert.Equal(Guid.Parse(A), StartRequest.Parse(U($$"""{"processSessionId":"{{A}}"}""")).ProcessSessionId);
        var redeem = RedeemRequest.Parse(U($$"""{"processSessionId":"{{A}}","ticket":"jt1_x"}"""));
        Assert.Equal("jt1_x", redeem.Ticket);
        var phb = ProcessHeartbeatRequest.Parse(U($$"""{"processSessionId":"{{A}}","phase":"running","participants":[{"accountId":"2","connected":true},{"accountId":"21","connected":false}]}"""));
        Assert.Equal([new ParticipantConnection(1, true), new ParticipantConnection(58, false)], phb.Participants);
    }

    public static TheoryData<string, Func<byte[], object>, string> Refusals => new()
    {
        { "heartbeat unknown member", b => HeartbeatRequest.Parse(b), $$"""{"buildRevision":"r1","catalogueVersion":"{{Cat}}","inventory":{"complete":true,"units":[]},"outboxPending":0,"hostId":"h"}""" },
        { "heartbeat units without inventory wrapper", b => HeartbeatRequest.Parse(b), $$"""{"buildRevision":"r1","catalogueVersion":"{{Cat}}","units":[],"outboxPending":0}""" },
        { "heartbeat complete missing", b => HeartbeatRequest.Parse(b), $$"""{"buildRevision":"r1","catalogueVersion":"{{Cat}}","inventory":{"units":[]},"outboxPending":0}""" },
        { "heartbeat complete not bool", b => HeartbeatRequest.Parse(b), $$"""{"buildRevision":"r1","catalogueVersion":"{{Cat}}","inventory":{"complete":"yes","units":[]},"outboxPending":0}""" },
        { "heartbeat bad build", b => HeartbeatRequest.Parse(b), $$"""{"buildRevision":"-r1","catalogueVersion":"{{Cat}}","inventory":{"complete":true,"units":[]},"outboxPending":0}""" },
        { "heartbeat duplicate unit", b => HeartbeatRequest.Parse(b), $$"""{"buildRevision":"r1","catalogueVersion":"{{Cat}}","inventory":{"complete":true,"units":[{"attemptId":"{{A}}","activeState":"active"},{"attemptId":"{{A}}","activeState":"failed"}]},"outboxPending":0}""" },
        { "heartbeat unknown active state", b => HeartbeatRequest.Parse(b), $$"""{"buildRevision":"r1","catalogueVersion":"{{Cat}}","inventory":{"complete":true,"units":[{"attemptId":"{{A}}","activeState":"exploded"}]},"outboxPending":0}""" },
        { "heartbeat uppercase uuid", b => HeartbeatRequest.Parse(b), $$"""{"buildRevision":"r1","catalogueVersion":"{{Cat}}","inventory":{"complete":true,"units":[{"attemptId":"{{A.ToUpperInvariant()}}","activeState":"active"}]},"outboxPending":0}""" },
        { "heartbeat negative outbox", b => HeartbeatRequest.Parse(b), $$"""{"buildRevision":"r1","catalogueVersion":"{{Cat}}","inventory":{"complete":true,"units":[]},"outboxPending":-1}""" },
        { "launching zero generation", b => LaunchingRequest.Parse(b), """{"credentialGeneration":0}""" },
        { "launching empty", b => LaunchingRequest.Parse(b), "{}" },
        { "exited code out of range", b => ExitedRequest.Parse(b), """{"exitCode":256,"signal":null,"results":[]}""" },
        { "exited bad signal", b => ExitedRequest.Parse(b), """{"exitCode":null,"signal":"kill","results":[]}""" },
        { "exited duplicate result", b => ExitedRequest.Parse(b), """{"exitCode":0,"signal":null,"results":[{"accountId":"2","present":true},{"accountId":"2","present":false}]}""" },
        { "exited bad account", b => ExitedRequest.Parse(b), """{"exitCode":0,"signal":null,"results":[{"accountId":"0","present":true}]}""" },
        { "exited missing signal", b => ExitedRequest.Parse(b), """{"exitCode":0,"results":[]}""" },
        { "register extra", b => RegisterRequest.Parse(b), $$"""{"buildRevision":"r1","catalogueVersion":"{{Cat}}","seed":1}""" },
        { "failed bad reason", b => FailedRequest.Parse(b), $$"""{"processSessionId":"{{A}}","reason":"Out Of Memory"}""" },
        { "start other member", b => StartRequest.Parse(b), $$"""{"processSessionId":"{{A}}","accountId":"2"}""" },
        { "redeem ticket not string", b => RedeemRequest.Parse(b), $$"""{"processSessionId":"{{A}}","ticket":1}""" },
        { "redeem ticket oversized", b => RedeemRequest.Parse(b), $$"""{"processSessionId":"{{A}}","ticket":"{{new string('a', 200)}}"}""" },
        { "process heartbeat duplicate participant", b => ProcessHeartbeatRequest.Parse(b), $$"""{"processSessionId":"{{A}}","phase":"running","participants":[{"accountId":"2","connected":true},{"accountId":"2","connected":false}]}""" },
        { "process heartbeat invalid account", b => ProcessHeartbeatRequest.Parse(b), $$"""{"processSessionId":"{{A}}","phase":"running","participants":[{"accountId":"l","connected":true}]}""" },
        { "process heartbeat bad phase", b => ProcessHeartbeatRequest.Parse(b), $$"""{"processSessionId":"{{A}}","phase":"","participants":[]}""" },
        { "process heartbeat session braces", b => ProcessHeartbeatRequest.Parse(b), $$"""{"processSessionId":"{{{B}}}","phase":"running","participants":[]}""" },
        { "not json", b => StartRequest.Parse(b), "start" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Agent_and_process_requests_parse_strictly(string label, Func<byte[], object> parse, string json)
    {
        var refused = Record.Exception(() => parse(U(json)));
        Assert.True(refused is RequestFormatException, $"{label}: expected RequestFormatException, got {refused?.GetType().Name ?? "none"}");
    }

    [Fact]
    public void An_empty_object_body_is_the_only_accepted_empty_request()
    {
        EmptyRequest.Parse(U("{}"));
        EmptyRequest.Parse(U(" { } "));
        Assert.Throws<RequestFormatException>(() => EmptyRequest.Parse(U("""{"x":1}""")));
        Assert.Throws<RequestFormatException>(() => EmptyRequest.Parse(U("")));
        Assert.Throws<RequestFormatException>(() => EmptyRequest.Parse(U("[]")));
    }
}
