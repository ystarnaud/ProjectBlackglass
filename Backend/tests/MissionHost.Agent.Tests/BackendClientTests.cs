using System.Net;
using System.Text;
using Blackglass.Accounts.Domain.Attempts;
using Blackglass.Accounts.Domain.Auth;
using Blackglass.MissionHost.Agent.Tests.Support;

namespace Blackglass.MissionHost.Agent.Tests;

/// <summary>Task 2: the HTTP client's requests, answer classification (§C) and claim validation (A10-D2).</summary>
public class BackendClientTests : IDisposable
{
    readonly AgentDirs dirs = new();
    readonly List<HttpRequestMessage> requests = [];
    readonly List<string> bodies = [];
    Func<HttpRequestMessage, HttpResponseMessage> respond = _ => new HttpResponseMessage(HttpStatusCode.OK);

    public void Dispose() => dirs.Dispose();

    sealed class Handler(BackendClientTests t) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            t.requests.Add(request);
            t.bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return t.respond(request);
        }
    }

    BackendClient Client()
    {
        var secret = new HostSecret(dirs.Config.HostSecretFile);
        secret.Reload();
        return new BackendClient(new HttpClient(new Handler(this)) { BaseAddress = new Uri("http://127.0.0.1:5081/") }, secret);
    }

    static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    static HttpResponseMessage Problem(HttpStatusCode status, string code) => Json(status, $"{{\"status\":{(int)status},\"code\":\"{code}\"}}");

    static string Grant(string attempt = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", string? credential = null, string participants = "[\"JG\"]") =>
        $"{{\"attemptId\":\"{attempt}\",\"missionPort\":7000,\"processCredential\":\"{credential ?? OpaqueSecret.New(OpaqueSecret.ProcessCredentialPrefix).Text}\","
        + $"\"credentialGeneration\":1,\"launchDeadline\":\"2026-10-17T12:00:00Z\",\"recovered\":false,\"participants\":{participants}}}";

    [Fact]
    public async Task A_claim_sends_the_host_secret_and_the_key_and_validates_the_grant()
    {
        respond = _ => Json(HttpStatusCode.OK, Grant());
        var key = Guid.NewGuid();
        var granted = Assert.IsType<ClaimAnswer.Granted>(await Client().ClaimAsync(key, default));
        Assert.Equal(("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", 7000, 1), (granted.Grant.Attempt, granted.Grant.MissionPort, granted.Grant.Generation));
        Assert.Equal(["JG"], granted.Grant.Participants);
        var request = requests.Single();
        Assert.Equal(("POST", "/agent/v1/claim"), (request.Method.Method, request.RequestUri!.AbsolutePath));
        Assert.Equal("Bearer " + dirs.SecretText, request.Headers.GetValues("Authorization").Single());
        Assert.Equal(key.ToString("D"), request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("{}", bodies.Single());
    }

    [Theory]
    [InlineData("0192F1A4-5C2E-7B1D-9A0E-3F6C1D2B8E41", null, "[\"JG\"]", "attempt_id")]
    [InlineData("../x", null, "[\"JG\"]", "attempt_id")]
    [InlineData("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", "hs1_AAAA", "[\"JG\"]", "process_credential")]
    [InlineData("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", null, "[]", "participant_count")]
    [InlineData("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", null, "[\"JG\",\"JG\"]", "participant_count")]
    [InlineData("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", null, "[\"1JG\"]", "participant_id")]
    [InlineData("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", null, "[42]", "participant_id")]
    public async Task A_grant_the_agent_cannot_trust_is_malformed_and_never_launched(string attempt, string? credential, string participants, string reason)
    {
        respond = _ => Json(HttpStatusCode.OK, Grant(attempt, credential, participants));
        Assert.Equal(reason, Assert.IsType<ClaimAnswer.Malformed>(await Client().ClaimAsync(Guid.NewGuid(), default)).Reason);
    }

    [Fact]
    public async Task Claim_answers_map_to_their_outcomes_and_failures_never_look_definitive()
    {
        var client = Client();
        respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        Assert.IsType<ClaimAnswer.NoWork>(await client.ClaimAsync(Guid.NewGuid(), default));
        respond = _ => Problem(HttpStatusCode.Conflict, "HostUnavailable");
        Assert.IsType<ClaimAnswer.HostUnavailable>(await client.ClaimAsync(Guid.NewGuid(), default));
        respond = _ => Problem(HttpStatusCode.Conflict, "InvalidState");
        Assert.IsType<ClaimAnswer.InvalidState>(await client.ClaimAsync(Guid.NewGuid(), default));
        respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        Assert.Equal(Transport.Unauthorized, Assert.IsType<ClaimAnswer.Failed>(await client.ClaimAsync(Guid.NewGuid(), default)).Kind);
        foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.TooManyRequests, HttpStatusCode.RequestTimeout })
        {
            respond = _ => new HttpResponseMessage(status);
            Assert.Equal(Transport.Unavailable, Assert.IsType<ClaimAnswer.Failed>(await client.ClaimAsync(Guid.NewGuid(), default)).Kind);
        }
        respond = _ => throw new HttpRequestException("connection refused"); // the tunnel is down
        Assert.Equal(Transport.Unavailable, Assert.IsType<ClaimAnswer.Failed>(await client.ClaimAsync(Guid.NewGuid(), default)).Kind);
        respond = _ => throw new TaskCanceledException("timeout");
        Assert.Equal(Transport.Unavailable, Assert.IsType<ClaimAnswer.Failed>(await client.ClaimAsync(Guid.NewGuid(), default)).Kind);
    }

    [Theory]
    [InlineData(200, null, "{\"disposition\":\"settled\"}", "done")]
    [InlineData(200, null, "{\"disposition\":\"duplicate\"}", "done")]
    [InlineData(202, null, "{\"disposition\":\"quarantined\"}", "under_review")]
    [InlineData(202, null, "{\"disposition\":\"held\"}", "under_review")]
    [InlineData(409, "Conflict", null, "conflict")]
    [InlineData(409, "AlreadySettledAsVoid", null, "conflict")]
    [InlineData(409, "AttemptRevoked", null, "revoked")]
    [InlineData(409, "SubmissionLimit", null, "limited")]
    [InlineData(409, "WrongAttempt", null, "refused")]
    [InlineData(409, "InvalidState", null, "refused")]
    [InlineData(422, "NotJson", null, "rejected")]
    [InlineData(413, null, null, "transport_refused")]
    [InlineData(415, null, null, "transport_refused")]
    public async Task Every_definitive_relay_answer_maps_to_its_delivery_state(int status, string? code, string? body, string outcome)
    {
        respond = _ => code is not null ? Problem((HttpStatusCode)status, code) : Json((HttpStatusCode)status, body ?? "{}");
        Assert.Equal(outcome, Assert.IsType<RelayAnswer.Definitive>(await Client().RelayAsync("{}"u8.ToArray(), default)).Outcome);
    }

    [Theory]
    [InlineData(401, Transport.Unauthorized)]
    [InlineData(500, Transport.Unavailable)]
    [InlineData(503, Transport.Unavailable)]
    [InlineData(429, Transport.Unavailable)]
    public async Task A_relay_without_a_definitive_answer_is_retried(int status, Transport kind)
    {
        respond = _ => new HttpResponseMessage((HttpStatusCode)status);
        Assert.Equal(kind, Assert.IsType<RelayAnswer.Failed>(await Client().RelayAsync("{}"u8.ToArray(), default)).Kind);
    }

    [Fact]
    public async Task The_exit_report_and_heartbeat_bodies_are_exactly_what_the_backend_parses()
    {
        var client = Client();
        respond = _ => Json(HttpStatusCode.OK, "{\"applied\":\"voided\"}");
        Assert.Equal("voided", Assert.IsType<ExitedAnswer.Applied>(await client.ExitedAsync("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41",
            new ExitedBody(null, "SIGKILL", [("JG", false), ("JH", true)], true), default)).Outcome);
        var exited = ExitedRequest.Parse(Encoding.UTF8.GetBytes(bodies.Last()));
        Assert.Equal((null, "SIGKILL", true, 2), (exited.ExitCode, exited.Signal, exited.OutboxComplete, exited.Results.Count));
        Assert.EndsWith("/agent/v1/attempts/0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41/exited", requests.Last().RequestUri!.AbsolutePath, StringComparison.Ordinal);

        respond = _ => Json(HttpStatusCode.OK, "{\"drained\":true,\"stop\":[\"0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41\"]}");
        var ok = Assert.IsType<HeartbeatAnswer.Ok>(await client.HeartbeatAsync(new HeartbeatBody("r1", "sha256:" + new string('5', 64), true,
            [new UnitEntry("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", "active")], 3), default));
        Assert.True(ok.Drained);
        var beat = HeartbeatRequest.Parse(Encoding.UTF8.GetBytes(bodies.Last()));
        Assert.Equal((true, 1, 3), (beat.InventoryComplete, beat.Units.Count, beat.OutboxPending));

        respond = _ => Problem(HttpStatusCode.BadRequest, "IncompleteResults");
        Assert.Equal("IncompleteResults", Assert.IsType<ExitedAnswer.Rejected>(await client.ExitedAsync("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41",
            new ExitedBody(0, null, [], true), default)).Code);
    }

    [Fact]
    public async Task Launching_carries_the_generation_and_maps_its_answers()
    {
        var client = Client();
        respond = _ => Json(HttpStatusCode.OK, "{\"state\":\"launching\"}");
        Assert.Equal("launching", Assert.IsType<LaunchingAnswer.Acknowledged>(await client.LaunchingAsync("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", 3, default)).State);
        Assert.Equal("{\"credentialGeneration\":3}", bodies.Last());
        respond = _ => Problem(HttpStatusCode.Conflict, "CredentialSuperseded");
        Assert.IsType<LaunchingAnswer.Superseded>(await client.LaunchingAsync("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", 1, default));
        respond = _ => Problem(HttpStatusCode.Conflict, "InvalidState");
        Assert.IsType<LaunchingAnswer.InvalidState>(await client.LaunchingAsync("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41", 1, default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.LaunchingAsync("../../x", 1, default)); // never a path from an unvalidated id
    }
}
