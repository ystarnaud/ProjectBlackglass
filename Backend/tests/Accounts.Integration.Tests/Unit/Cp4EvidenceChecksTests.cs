using System.Text.Json;
using Blackglass.Accounts.IntegrationTests.E2E;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>
/// CP4 review I1 (with M2 and M8 re-graded): the host's judgement of what the Unity server and the relay observed may never pass
/// on missing evidence. The two passing vectors are the values the 2026-10-23 CP4 run actually recorded.
/// </summary>
public sealed class Cp4EvidenceChecksTests
{
    const string AttemptA = "01a16849-be35-7247-84b1-1fb11a3f08a5";
    const string AttemptB = "01a1684a-b6a9-723e-9624-3d2489780205";

    const string RunA = """
        {"scenario":"A","exitCode":0,"exitReason":"result published","manifestHash":"sha256:44653f7bcb193465650c8957867101cfe507818c4b8fd71cdf11f1da2c336cd0",
         "outcome":"Success","heldFrames":152,"heldUnchanged":true,"pausedWhileHeld":true,"droppedBeforeRunning":true,"writtenBeforeReady":true,
         "recordedSends":1,"recordedAnswer":"ok","notifyingSeconds":0.48,"startExchanges":["ConnectionFailed 0","Completed 200"],
         "recordedExchanges":["Completed 200"],"collected":2}
        """;

    const string RunB = """
        {"scenario":"B","exitCode":0,"exitReason":"result published","manifestHash":"sha256:2052f6388e778a27be0d7052ff99baefb606a040bff84bbd1d8e35bc8687c3d0",
         "outcome":"Success","heldFrames":104,"heldUnchanged":true,"pausedWhileHeld":true,"droppedBeforeRunning":false,"writtenBeforeReady":true,
         "recordedSends":6,"recordedAnswer":"gave_up","notifyingSeconds":28.15,"startExchanges":["Completed 200"],
         "recordedExchanges":["ConnectionFailed 0","ConnectionFailed 0","ConnectionFailed 0","ConnectionFailed 0","ConnectionFailed 0","ConnectionFailed 0"],"collected":1}
        """;

    static JsonElement With(string json, string member, object value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node[member] = JsonSerializer.SerializeToNode(value);
        return JsonDocument.Parse(node.ToJsonString()).RootElement;
    }

    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    static ProxyLogEntry E(string attempt, string route, int status, string outcome) =>
        new(DateTime.UtcNow, "POST", $"/server/v1/attempts/{attempt}/{route}", status, outcome);

    static List<ProxyLogEntry> LogA() =>
    [
        E(AttemptA, "sessions", 200, "answered"), E(AttemptA, "ready", 200, "answered"), E(AttemptA, "tickets/redeem", 200, "answered"),
        E(AttemptA, "participants/2/start", 200, "dropped"), E(AttemptA, "participants/2/start", 200, "answered"),
        E(AttemptA, "participants/2/recorded", 200, "answered"),
    ];

    static List<ProxyLogEntry> LogB() =>
    [
        E(AttemptB, "sessions", 200, "answered"), E(AttemptB, "ready", 200, "answered"), E(AttemptB, "tickets/redeem", 200, "answered"),
        E(AttemptB, "participants/3/start", 200, "answered"),
        .. Enumerable.Repeat(E(AttemptB, "participants/3/recorded", 0, "upstream_unreachable"), 6),
        E(AttemptB, "heartbeat", 0, "upstream_unreachable"),
    ];

    [Fact]
    public void The_recorded_run_passes_every_check()
    {
        Assert.Empty(Cp4EvidenceChecks.UnityReport(Json(RunA), outage: false));
        Assert.Empty(Cp4EvidenceChecks.UnityReport(Json(RunB), outage: true));
        Assert.Empty(Cp4EvidenceChecks.RelayLog(LogA(), AttemptA, "2", dropStart: true, outage: false));
        Assert.Empty(Cp4EvidenceChecks.RelayLog(LogB(), AttemptB, "3", dropStart: false, outage: true));
    }

    [Fact]
    public void A_report_with_no_recorded_notice_is_not_proof_of_durability()
    {
        Assert.Contains("no recorded notice was sent", Cp4EvidenceChecks.UnityReport(With(RunA, "recordedSends", 0), outage: false));
    }

    [Theory]
    [InlineData("pausedWhileHeld", false, "not paused for every held frame")]
    [InlineData("heldUnchanged", false, "gameplay changed before the confirmed start")]
    [InlineData("writtenBeforeReady", false, "a recorded notice did not follow the durable write or preceded publication")]
    [InlineData("collected", 0, "no loot was collected")]
    [InlineData("exitCode", 4, "the process exited 4")]
    [InlineData("droppedBeforeRunning", false, "the mission ran before the lost start answer")]
    [InlineData("recordedAnswer", "gave_up", "the recorded notice answered gave_up, not ok")]
    public void Each_missing_fact_of_scenario_A_is_named(string member, object value, string problem)
    {
        Assert.Contains(problem, Cp4EvidenceChecks.UnityReport(With(RunA, member, value), outage: false));
    }

    [Theory]
    [InlineData("recordedAnswer", "ok", "the recorded notice answered ok, not gave_up")]
    [InlineData("notifyingSeconds", 40.0, "the recorded notice was not bounded (40 s)")]
    [InlineData("notifyingSeconds", -1.0, "the recorded notice was not bounded (-1 s)")]
    public void Each_missing_fact_of_scenario_B_is_named(string member, object value, string problem)
    {
        Assert.Contains(problem, Cp4EvidenceChecks.UnityReport(With(RunB, member, value), outage: true));
    }

    [Fact]
    public void The_ticket_must_have_been_redeemed_exactly_once_by_this_attempt()
    {
        Assert.Contains("1 ticket redemption(s) expected, 0 relayed", Cp4EvidenceChecks.RelayLog(LogA().Where(e => !e.Path.EndsWith("/tickets/redeem", StringComparison.Ordinal)), AttemptA, "2", true, false));
        Assert.Contains("1 ticket redemption(s) expected, 2 relayed", Cp4EvidenceChecks.RelayLog([.. LogA(), E(AttemptA, "tickets/redeem", 200, "answered")], AttemptA, "2", true, false));
    }

    [Fact]
    public void The_start_requests_must_be_one_lost_then_one_answered_or_just_one_answered()
    {
        Assert.Contains("start requests were [200 answered]", Cp4EvidenceChecks.RelayLog(LogA().Where(e => e.Outcome != "dropped"), AttemptA, "2", true, false));
        Assert.Contains("start requests were [200 dropped, 200 answered]", Cp4EvidenceChecks.RelayLog(LogA(), AttemptA, "2", false, false));
    }

    [Fact]
    public void An_outage_needs_recorded_attempts_that_all_met_the_stopped_api()
    {
        Assert.Contains("no recorded attempt was relayed", Cp4EvidenceChecks.RelayLog(LogB().Where(e => !e.Path.EndsWith("/recorded", StringComparison.Ordinal)), AttemptB, "3", false, true));
        Assert.Contains("a recorded attempt reached the API during the outage", Cp4EvidenceChecks.RelayLog([.. LogB(), E(AttemptB, "participants/3/recorded", 200, "answered")], AttemptB, "3", false, true));
        Assert.Contains("recorded requests were [0 upstream_unreachable]", Cp4EvidenceChecks.RelayLog([.. LogA().Take(5), E(AttemptA, "participants/2/recorded", 0, "upstream_unreachable")], AttemptA, "2", true, false));
    }

    [Fact]
    public void A_receipt_that_grants_nothing_is_not_proof_of_rewards()
    {
        const string receipt = """{"outcome":"success","energy":{"cost":10,"refunded":0,"state":"settled"},"granted":[],"consumed":[],"xp":[{"amount":150,"operativeId":"2"}]}""";
        Assert.Contains("the receipt grants nothing", Cp4EvidenceChecks.Receipt(Json(receipt)));
        Assert.Contains("the receipt awards no XP", Cp4EvidenceChecks.Receipt(Json(receipt.Replace("[{\"amount\":150,\"operativeId\":\"2\"}]", "[]", StringComparison.Ordinal))));
        Assert.Empty(Cp4EvidenceChecks.Receipt(Json(receipt.Replace("\"granted\":[]", "\"granted\":[{\"definitionId\":\"item.light-vest\",\"itemIds\":[\"9\"],\"operativeId\":\"2\",\"quantity\":1}]", StringComparison.Ordinal))));
    }
}
