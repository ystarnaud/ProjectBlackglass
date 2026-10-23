using System.Collections.Generic;
using System.Linq;
using Blackglass.Backend.Adapter;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>Shared pieces of the Stage 13 server-side tests: a validated attempt, its answers, and a controlled clock.</summary>
    internal static class BackendTestKit
    {
        public const string Attempt = AttemptTestData.AttemptId;
        public const string Session = AttemptTestData.SessionId;
        public const string Ticket = "jt1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        public const string OtherTicket = "jt1_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBA";
        public static readonly string Prefix = "/server/v1/attempts/" + Attempt + "/";

        public static AttemptStart Start()
        {
            var snapshot = AttemptTestData.LedgerSnapshot("01-owned-swap-and-use.json");
            Assert.That(AttemptStart.TryCreate(Attempt, AttemptTestData.Response(snapshot), AttemptTestData.GoldenCatalogueVersion, AttemptTestData.KnownOperative,
                AttemptTestData.GoldenLedgerCatalogue(), out var start, out var refusal), Is.True, refusal);
            return start;
        }

        public static string RedeemAnswer(AttemptStart start, IEnumerable<string> operatives = null, string account = null) =>
            "{\"accountId\":\"" + (account ?? start.AccountId) + "\",\"operativeIds\":[" +
            string.Join(",", (operatives ?? start.Snapshot.Select(o => o.OperativeId)).Select(o => "\"" + o + "\"")) + "]}";

        public const string Started = "{\"started\":true,\"startedAt\":\"2026-10-23T12:00:01.5Z\"}";

        public static string Problem(string code) => "{\"type\":\"about:blank\",\"title\":\"x\",\"status\":409,\"code\":\"" + code + "\"}";

        public static ProcessApi Api(FakeHttpTransport transport) => new ProcessApi(transport, Attempt, new RetryPolicy(), () => 0.5);
    }

    /// <summary>A controlled clock that ticks a transport and its users in small steps.</summary>
    internal sealed class StepClock
    {
        public double Now = 100;
        public const double Step = 0.05;

        public void Run(FakeHttpTransport transport, System.Action<double> tick, double seconds, System.Func<bool> until = null)
        {
            var end = Now + seconds;
            while (Now < end && (until == null || !until()))
            {
                Now += Step;
                transport.Tick(Now);
                tick(Now);
            }
        }
    }
}
