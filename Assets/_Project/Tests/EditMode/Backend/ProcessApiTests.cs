using System;
using System.Linq;
using System.Text;
using Blackglass.Backend.Adapter;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>The process routes as typed calls (Stage 13, Task 5): exact routes and bytes, and answers validated after parsing.</summary>
    public sealed class ProcessApiTests
    {
        public const string Attempt = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";
        public const string Session = "0192f1a5-0000-7000-8000-000000000001";
        public const string Account = "BukQL";
        public const string Ticket = "jt1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string Hash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        static readonly string Prefix = "/server/v1/attempts/" + Attempt + "/";

        FakeHttpTransport transport;
        ProcessApi api;
        double now;

        [SetUp]
        public void SetUp()
        {
            transport = new FakeHttpTransport(Prefix);
            api = new ProcessApi(transport, Attempt, new RetryPolicy(), () => 0.5);
            now = 10;
        }

        BackendCall Finish(BackendCall call)
        {
            for (var i = 0; i < 200 && !call.IsFinished; i++)
            {
                now += 0.05;
                transport.Tick(now);
                call.Tick(now);
            }
            return call;
        }

        BackendCall Answered(string suffix, string body, Func<BackendCall> start)
        {
            transport.Answer(suffix, 200, body);
            return Finish(start());
        }

        [Test]
        public void Each_call_posts_to_exactly_its_route()
        {
            transport.AnswerAlways("sessions", 200).AnswerAlways("ready", 200).AnswerAlways("tickets/redeem", 200).AnswerAlways("start", 200)
                .AnswerAlways("heartbeat", 200).AnswerAlways("recorded", 200).AnswerAlways("failed", 200);
            api.Register(new SessionRegisterRequest { buildRevision = "editor", catalogueVersion = Hash }, now + 5, now);
            api.Ready(Encoding.UTF8.GetBytes("{}"), now + 5, now);
            api.Redeem(Session, Ticket, now + 5, now);
            api.Start(Session, Account, now + 5, now);
            api.Heartbeat(new ProcessHeartbeatRequest { processSessionId = Session, phase = "running", participants = new ParticipantConnectionDto[0] }, now + 5, now);
            api.Recorded(Account, Hash, now + 5, now);
            api.Failed(Session, "start_refused", now + 5, now);
            Assert.That(transport.Requests.Select(r => r.Path.Substring(Prefix.Length)), Is.EqualTo(new[]
            {
                "sessions", "ready", "tickets/redeem", "participants/BukQL/start", "heartbeat", "participants/BukQL/recorded", "failed",
            }));
        }

        [Test]
        public void Bodies_are_the_dtos_json()
        {
            api.Start(Session, Account, now + 5, now);
            api.Recorded(Account, Hash, now + 5, now);
            api.Failed(Session, "join_deadline", now + 5, now);
            api.Redeem(Session, Ticket, now + 5, now);
            Assert.That(transport.Requests.Select(r => r.BodyText), Is.EqualTo(new[]
            {
                "{\"processSessionId\":\"" + Session + "\"}",
                "{\"manifestHash\":\"" + Hash + "\"}",
                "{\"processSessionId\":\"" + Session + "\",\"reason\":\"join_deadline\"}",
                "{\"processSessionId\":\"" + Session + "\",\"ticket\":\"" + Ticket + "\"}",
            }));
        }

        [Test]
        public void Ready_sends_the_given_bytes_unchanged()
        {
            var bytes = Encoding.UTF8.GetBytes("{\"processSessionId\":\"x\",\"layoutHash\":\"0123456789abcdef\"}");
            api.Ready(bytes, now + 5, now);
            Assert.That(transport.Requests.Single().Body, Is.EqualTo(bytes));
        }

        [Test]
        public void Malformed_arguments_are_refused_before_any_request()
        {
            Assert.Throws<ArgumentException>(() => new ProcessApi(transport, "0192F1A4-5C2E-7B1D-9A0E-3F6C1D2B8E41", null, () => 0.5));
            Assert.Throws<ArgumentException>(() => api.Start(Session, "1BukQL", now + 5, now));
            Assert.Throws<ArgumentException>(() => api.Start("not-a-uuid", Account, now + 5, now));
            Assert.Throws<ArgumentException>(() => api.Recorded(Account, "sha256:abc", now + 5, now));
            Assert.Throws<ArgumentException>(() => api.Failed(Session, "Start Refused", now + 5, now));
            var e = Assert.Throws<ArgumentException>(() => api.Redeem(Session, Ticket + "A", now + 5, now));
            Assert.That(e.Message, Does.Not.Contain(Ticket));
            Assert.That(transport.Requests, Is.Empty);
        }

        [TestCase(Ticket, true)]
        [TestCase("jt1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB", false)]
        [TestCase("pc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
        [TestCase("jt1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void A_ticket_has_its_shape(string ticket, bool wellFormed) => Assert.That(ProcessApi.IsTicket(ticket), Is.EqualTo(wellFormed));

        [TestCase("2026-10-23T12:00:00Z", true)]
        [TestCase("2026-10-23T12:00:00.1Z", true)]
        [TestCase("2026-10-23T12:00:00.123Z", true)]
        [TestCase("2026-10-23T12:00:00.12345Z", true)]
        [TestCase("2026-10-23T12:00:00.123456Z", true)]
        [TestCase("2026-10-23T12:00:00.1234567Z", true)]
        [TestCase("2026-10-23T12:00:00.12345678Z", false)]
        [TestCase("2026-10-23T12:00:00", false)]
        [TestCase("2026-10-23T12:00:00+00:00", false)]
        [TestCase("2026-10-23 12:00:00Z", false)]
        [TestCase("", false)]
        public void Backend_timestamps_parse_round_trip_utc_only(string text, bool ok)
        {
            Assert.That(ProcessApi.TryParseTimestamp(text, out var utc), Is.EqualTo(ok));
            if (ok)
            {
                Assert.That(utc.Kind, Is.EqualTo(DateTimeKind.Utc));
                Assert.That(utc.Hour, Is.EqualTo(12));
            }
        }

        const string Registered = "{\"processSessionId\":\"" + Session + "\",\"seed\":5,\"missionTemplate\":{\"id\":\"procedural-standard\",\"version\":1},"
            + "\"catalogueVersion\":\"" + Hash + "\",\"participants\":[{\"accountId\":\"BukQL\",\"loadout\":{\"accountId\":\"BukQL\"}}],\"deadlines\":{\"launch\":\"2026-10-23T12:00:00.12345Z\"}}";

        [Test]
        public void A_register_answer_is_validated()
        {
            var call = Answered("sessions", Registered, () => api.Register(new SessionRegisterRequest(), now + 5, now));
            Assert.That(ProcessApi.TryRegistered(call, out var response, out var problem), Is.True, problem);
            Assert.That(response.processSessionId, Is.EqualTo(Session));
        }

        [TestCase("{\"processSessionId\":\"x\"}", "bad_session_id")]
        [TestCase("{\"processSessionId\":\"" + Session + "\"}", "no_template")]
        [TestCase("{\"processSessionId\":\"" + Session + "\",\"missionTemplate\":{\"id\":\"p\",\"version\":1},\"participants\":[]}", "no_participants")]
        [TestCase("<html>bad gateway</html>", "bad_json")]
        [TestCase("", "bad_json")]
        public void A_register_answer_that_does_not_hold_is_refused(string body, string code)
        {
            var call = Answered("sessions", body, () => api.Register(new SessionRegisterRequest(), now + 5, now));
            Assert.That(ProcessApi.TryRegistered(call, out var response, out var problem), Is.False);
            Assert.That(response, Is.Null);
            Assert.That(problem, Is.EqualTo(code));
        }

        [Test]
        public void A_register_answer_with_no_launch_deadline_is_refused()
        {
            var call = Answered("sessions", Registered.Replace("2026-10-23T12:00:00.12345Z", "soon"), () => api.Register(new SessionRegisterRequest(), now + 5, now));
            Assert.That(ProcessApi.TryRegistered(call, out _, out var problem), Is.False);
            Assert.That(problem, Is.EqualTo("bad_deadline"));
        }

        [Test]
        public void A_ready_answer_gives_the_join_deadline()
        {
            var call = Answered("ready", "{\"joinDeadline\":\"2026-10-23T12:02:00.5Z\"}", () => api.Ready(new byte[] { 0x7b, 0x7d }, now + 5, now));
            Assert.That(ProcessApi.TryReady(call, out var deadline, out var problem), Is.True, problem);
            Assert.That(deadline, Is.EqualTo(new DateTime(2026, 10, 23, 12, 2, 0, 500, DateTimeKind.Utc)));
            var bad = Answered("ready", "{\"joinDeadline\":\"later\"}", () => api.Ready(new byte[] { 0x7b, 0x7d }, now + 5, now));
            Assert.That(ProcessApi.TryReady(bad, out _, out var badProblem), Is.False);
            Assert.That(badProblem, Is.EqualTo("bad_join_deadline"));
        }

        [Test]
        public void A_redeem_answer_names_a_base58_account_and_operatives()
        {
            var call = Answered("tickets/redeem", "{\"accountId\":\"BukQL\",\"operativeIds\":[\"2\",\"3\"]}", () => api.Redeem(Session, Ticket, now + 5, now));
            Assert.That(ProcessApi.TryRedeemed(call, out var redeemed, out var problem), Is.True, problem);
            Assert.That(redeemed.AccountId, Is.EqualTo("BukQL"));
            Assert.That(redeemed.OperativeIds, Is.EqualTo(new[] { "2", "3" }));
        }

        [TestCase("{\"accountId\":\"1x\",\"operativeIds\":[\"2\"]}", "bad_account")]
        [TestCase("{\"accountId\":\"BukQL\",\"operativeIds\":[]}", "no_operatives")]
        [TestCase("{\"accountId\":\"BukQL\"}", "no_operatives")]
        [TestCase("{\"accountId\":\"BukQL\",\"operativeIds\":[\"0\"]}", "bad_operative")]
        public void A_redeem_answer_that_does_not_hold_is_refused(string body, string code)
        {
            var call = Answered("tickets/redeem", body, () => api.Redeem(Session, Ticket, now + 5, now));
            Assert.That(ProcessApi.TryRedeemed(call, out var redeemed, out var problem), Is.False);
            Assert.That(redeemed, Is.Null);
            Assert.That(problem, Is.EqualTo(code));
        }

        [Test]
        public void A_start_answer_must_say_started()
        {
            var ok = Answered("start", "{\"started\":true,\"startedAt\":\"2026-10-23T12:00:01.25Z\"}", () => api.Start(Session, Account, now + 5, now));
            Assert.That(ProcessApi.TryStarted(ok, out _, out var problem), Is.True, problem);
            var no = Answered("start", "{\"started\":false,\"startedAt\":\"2026-10-23T12:00:01Z\"}", () => api.Start(Session, Account, now + 5, now));
            Assert.That(ProcessApi.TryStarted(no, out _, out var noProblem), Is.False);
            Assert.That(noProblem, Is.EqualTo("not_started"));
            var empty = Answered("start", "{}", () => api.Start(Session, Account, now + 5, now));
            Assert.That(ProcessApi.TryStarted(empty, out _, out _), Is.False);
        }

        [Test]
        public void An_unfinished_or_refused_call_parses_nothing()
        {
            transport.Answer("start", 409, "{\"code\":\"Cancelled\"}");
            var call = Finish(api.Start(Session, Account, now + 5, now));
            Assert.That(ProcessApi.TryStarted(call, out _, out var problem), Is.False);
            Assert.That(problem, Is.EqualTo("not_succeeded"));
        }

        [Test]
        public void There_is_no_status_receipt_or_result_route()
        {
            var names = typeof(ProcessApi).GetMethods().Select(m => m.Name.ToLowerInvariant()).ToArray();
            Assert.That(names.Any(n => n.Contains("status") || n.Contains("receipt") || n.Contains("result") || n.Contains("settle")), Is.False);
        }
    }
}
