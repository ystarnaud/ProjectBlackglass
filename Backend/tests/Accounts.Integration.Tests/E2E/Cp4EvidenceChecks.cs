using System.Globalization;
using System.Text.Json;

namespace Blackglass.Accounts.IntegrationTests.E2E;

/// <summary>
/// The CP4 host's judgement of what the Unity server reported (S/unity-result.json), what the relay saw and what the receipt
/// says. Each check names every missing fact; an empty list is the only pass, so nothing can pass on absent evidence (CP4
/// review I1, M2, M8).
/// </summary>
public static class Cp4EvidenceChecks
{
    public static IReadOnlyList<string> UnityReport(JsonElement unity, bool outage)
    {
        var problems = new List<string>();
        var exit = unity.GetProperty("exitCode").GetInt32();
        if (exit != 0) problems.Add($"the process exited {exit}");
        if (!unity.GetProperty("heldUnchanged").GetBoolean()) problems.Add("gameplay changed before the confirmed start");
        if (!unity.GetProperty("pausedWhileHeld").GetBoolean()) problems.Add("not paused for every held frame");
        if (unity.GetProperty("recordedSends").GetInt32() < 1) problems.Add("no recorded notice was sent");
        if (!unity.GetProperty("writtenBeforeReady").GetBoolean()) problems.Add("a recorded notice did not follow the durable write or preceded publication");
        if (unity.GetProperty("collected").GetInt32() < 1) problems.Add("no loot was collected");
        var answer = unity.GetProperty("recordedAnswer").GetString();
        var expected = outage ? "gave_up" : "ok";
        if (answer != expected) problems.Add($"the recorded notice answered {answer}, not {expected}");
        if (outage)
        {
            var seconds = unity.GetProperty("notifyingSeconds").GetDouble();
            if (seconds is < 0 or > 35) problems.Add($"the recorded notice was not bounded ({seconds.ToString(CultureInfo.InvariantCulture)} s)");
        }
        else if (!unity.GetProperty("droppedBeforeRunning").GetBoolean()) problems.Add("the mission ran before the lost start answer");
        return problems;
    }

    public static IReadOnlyList<string> RelayLog(IEnumerable<ProxyLogEntry> log, string attempt, string account, bool dropStart, bool outage)
    {
        var mine = log.Where(e => e.Path.StartsWith($"/server/v1/attempts/{attempt}/", StringComparison.Ordinal)).ToList();
        static string Join(IEnumerable<ProxyLogEntry> entries) => "[" + string.Join(", ", entries.Select(e => $"{e.Status} {e.Outcome}")) + "]";
        var problems = new List<string>();

        var redeems = mine.Where(e => e.Path.EndsWith("/tickets/redeem", StringComparison.Ordinal)).ToList();
        if (redeems.Count != 1) problems.Add($"1 ticket redemption(s) expected, {redeems.Count} relayed");
        else if (redeems[0] is not { Status: 200, Outcome: "answered" }) problems.Add($"the ticket redemption was {Join(redeems)}");

        var starts = mine.Where(e => e.Path.EndsWith($"/participants/{account}/start", StringComparison.Ordinal)).ToList();
        var expectedStarts = dropStart ? "[200 dropped, 200 answered]" : "[200 answered]";
        if (Join(starts) != expectedStarts) problems.Add($"start requests were {Join(starts)}");

        var recorded = mine.Where(e => e.Path.EndsWith($"/participants/{account}/recorded", StringComparison.Ordinal)).ToList();
        if (outage)
        {
            if (recorded.Count == 0) problems.Add("no recorded attempt was relayed");
            else if (recorded.Any(e => e.Outcome != "upstream_unreachable")) problems.Add("a recorded attempt reached the API during the outage");
        }
        else if (Join(recorded) != "[200 answered]") problems.Add($"recorded requests were {Join(recorded)}");
        return problems;
    }

    public static IReadOnlyList<string> Receipt(JsonElement receipt)
    {
        var problems = new List<string>();
        if (receipt.GetProperty("outcome").GetString() != "success") problems.Add($"the receipt's outcome is {receipt.GetProperty("outcome").GetString()}");
        var energy = receipt.GetProperty("energy");
        if (energy.GetProperty("state").GetString() != "settled" || energy.GetProperty("refunded").GetInt32() != 0) problems.Add("the receipt's energy is not settled with nothing refunded");
        if (receipt.GetProperty("granted").GetArrayLength() == 0) problems.Add("the receipt grants nothing");
        if (receipt.GetProperty("xp").GetArrayLength() == 0) problems.Add("the receipt awards no XP");
        return problems;
    }
}
