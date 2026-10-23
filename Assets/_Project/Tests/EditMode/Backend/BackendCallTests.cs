using System.Linq;
using System.Text;
using Blackglass.Backend.Adapter;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>One retrying backend call (Stage 13, plan §3.3): definitive answers stop it, transient ones are retried with the same bytes.</summary>
    public sealed class BackendCallTests
    {
        const string Path = "/server/v1/attempts/0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41/ready";
        static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"a\":1}");

        FakeHttpTransport transport;
        double now;

        [SetUp]
        public void SetUp()
        {
            transport = new FakeHttpTransport();
            now = 100;
        }

        BackendCall Start(double deadlineIn = 60, RetryPolicy policy = null) => new BackendCall(transport, Path, Body, policy ?? new RetryPolicy(), now + deadlineIn, () => 0.5, now);

        // Advances the clock in small steps, ticking the transport then the call, until the call finishes or the time is up.
        void Run(BackendCall call, double seconds)
        {
            var end = now + seconds;
            while (now < end && !call.IsFinished)
            {
                now += 0.05;
                transport.Tick(now);
                call.Tick(now);
            }
        }

        [Test]
        public void A_2xx_succeeds_after_one_attempt()
        {
            transport.Answer("ready", 200, "{\"joinDeadline\":\"x\"}");
            var call = Start();
            Run(call, 1);
            Assert.That(call.State, Is.EqualTo(CallState.Succeeded));
            Assert.That(call.Attempts, Is.EqualTo(1));
            Assert.That(Encoding.UTF8.GetString(call.Body), Is.EqualTo("{\"joinDeadline\":\"x\"}"));
        }

        [Test]
        public void Transient_answers_are_retried_with_identical_bytes()
        {
            transport.Answer("ready", 503).Answer("ready", 503).Answer("ready", 200);
            var call = Start();
            Run(call, 10);
            Assert.That(call.State, Is.EqualTo(CallState.Succeeded));
            Assert.That(call.Attempts, Is.EqualTo(3));
            Assert.That(transport.Requests.Select(r => r.BodyText).Distinct().Single(), Is.EqualTo("{\"a\":1}"));
        }

        [Test]
        public void Backoff_doubles_from_half_a_second_to_eight()
        {
            transport.RefuseAlways("ready");
            var call = Start(deadlineIn: 40);
            Run(call, 40);
            var gaps = transport.Requests.Zip(transport.Requests.Skip(1), (a, b) => b.At - a.At).Take(6).ToArray();
            var expected = new[] { 0.5, 1, 2, 4, 8, 8 };
            for (var i = 0; i < expected.Length; i++)
                Assert.That(gaps[i], Is.EqualTo(expected[i]).Within(0.11), "gap " + i);
        }

        [Test]
        public void Jitter_spreads_the_backoff_by_twenty_percent()
        {
            transport.RefuseAlways("ready");
            var call = new BackendCall(transport, Path, Body, new RetryPolicy(), now + 60, () => 1.0, now);
            Run(call, 3);
            Assert.That(transport.Requests[1].At - transport.Requests[0].At, Is.EqualTo(0.6).Within(0.11));
        }

        [Test]
        public void Retry_after_is_honoured_up_to_the_cap()
        {
            transport.Answer("ready", 429, "{}", "3").Answer("ready", 429, "{}", "600").Answer("ready", 200);
            var call = Start();
            Run(call, 30);
            Assert.That(call.State, Is.EqualTo(CallState.Succeeded));
            Assert.That(transport.Requests[1].At - transport.Requests[0].At, Is.EqualTo(3).Within(0.11));
            Assert.That(transport.Requests[2].At - transport.Requests[1].At, Is.EqualTo(8).Within(0.11));
        }

        [Test]
        public void A_lost_answer_is_a_timeout_then_the_same_request_again()
        {
            transport.Drop("ready").Answer("ready", 200);
            var call = Start(policy: new RetryPolicy { AttemptTimeoutSeconds = 2 });
            Run(call, 10);
            Assert.That(call.State, Is.EqualTo(CallState.Succeeded));
            Assert.That(call.Attempts, Is.EqualTo(2));
            Assert.That(transport.Requests[1].At - transport.Requests[0].At, Is.EqualTo(2.5).Within(0.11));
        }

        [Test]
        public void A_401_is_unauthorized_at_once()
        {
            transport.Answer("ready", 401, "");
            var call = Start();
            Run(call, 5);
            Assert.That(call.State, Is.EqualTo(CallState.Unauthorized));
            Assert.That(call.Attempts, Is.EqualTo(1));
        }

        [TestCase(400)]
        [TestCase(404)]
        [TestCase(409)]
        [TestCase(413)]
        [TestCase(415)]
        [TestCase(422)]
        [TestCase(403)]
        [TestCase(405)]
        public void Other_answers_are_refused_with_the_problem_code(int status)
        {
            transport.Answer("ready", status, "{\"type\":\"about:blank\",\"title\":\"Conflict\",\"status\":" + status + ",\"code\":\"Cancelled\"}");
            var call = Start();
            Run(call, 5);
            Assert.That(call.State, Is.EqualTo(CallState.Refused));
            Assert.That(call.Status, Is.EqualTo(status));
            Assert.That(call.ProblemCode, Is.EqualTo("Cancelled"));
            Assert.That(call.Attempts, Is.EqualTo(1));
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{\"title\":\"x\"}")]
        [TestCase("{\"code\":\"has space\"}")]
        [TestCase("{\"code\":\"Abcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcde\"}")]
        public void A_missing_or_malformed_code_is_none(string body)
        {
            transport.Answer("ready", 409, body);
            var call = Start();
            Run(call, 5);
            Assert.That(call.ProblemCode, Is.EqualTo("none"));
        }

        [Test]
        public void A_redirect_or_an_oversize_body_is_refused_once()
        {
            transport.Redirect("ready");
            var redirected = Start();
            Run(redirected, 5);
            Assert.That(redirected.State, Is.EqualTo(CallState.Refused));
            Assert.That(redirected.ProblemCode, Is.EqualTo("redirect"));
            transport.Oversize("ready");
            var oversize = Start();
            Run(oversize, 5);
            Assert.That(oversize.State, Is.EqualTo(CallState.Refused));
            Assert.That(oversize.ProblemCode, Is.EqualTo("oversize"));
            Assert.That(transport.Requests.Count, Is.EqualTo(2));
        }

        [Test]
        public void The_deadline_ends_it_without_another_send()
        {
            transport.RefuseAlways("ready");
            var call = Start(deadlineIn: 3);
            Run(call, 10);
            Assert.That(call.State, Is.EqualTo(CallState.GaveUp));
            Assert.That(transport.Requests.Last().At, Is.LessThan(100 + 3));
        }

        [Test]
        public void Abort_gives_up_at_once()
        {
            transport.SilentAlways("ready");
            var call = Start();
            Run(call, 0.2);
            call.Abort();
            Assert.That(call.State, Is.EqualTo(CallState.GaveUp));
            Assert.That(transport.Aborted, Is.EqualTo(1), "the request on the wire is aborted, not only forgotten");
            Assert.That(transport.Requests.Single().Exchange.Outcome, Is.EqualTo(HttpOutcome.Aborted));
        }

        // CP1 I1: a late tick after the deadline (a main-thread stall during backoff) must never send again.
        [Test]
        public void A_late_tick_past_the_deadline_never_sends_again()
        {
            transport.RefuseAlways("ready");
            var call = Start(deadlineIn: 3);
            Run(call, 0.2);
            var sent = transport.Requests.Count;
            now += 10; // the main thread stalled through the rest of the bound
            transport.Tick(now);
            call.Tick(now);
            Assert.That(call.State, Is.EqualTo(CallState.GaveUp));
            Assert.That(transport.Requests.Count, Is.EqualTo(sent));
        }

        [Test]
        public void An_attempts_timeout_never_passes_the_deadline()
        {
            transport.SilentAlways("ready");
            var call = Start(deadlineIn: 4, policy: new RetryPolicy { AttemptTimeoutSeconds = 10 });
            Run(call, 10);
            Assert.That(call.State, Is.EqualTo(CallState.GaveUp));
            Assert.That(transport.Requests.Count, Is.EqualTo(1), "one attempt whose timeout is the time left");
        }

        // CP1 I3: a transport that throws (an insecure-HTTP refusal, an invalid request) is a definitive refusal, never an exception per frame.
        [Test]
        public void A_transport_that_throws_is_a_definitive_refusal()
        {
            transport.Throw("ready");
            BackendCall call = null;
            Assert.DoesNotThrow(() => call = Start());
            Assert.DoesNotThrow(() => Run(call, 2));
            Assert.That(call.State, Is.EqualTo(CallState.Refused));
            Assert.That(call.ProblemCode, Is.EqualTo("transport"));
        }

        [Test]
        public void A_transport_that_throws_on_a_retry_is_a_definitive_refusal()
        {
            transport.Answer("ready", 503).Throw("ready");
            var call = Start();
            Assert.DoesNotThrow(() => Run(call, 3));
            Assert.That(call.State, Is.EqualTo(CallState.Refused));
            Assert.That(call.ProblemCode, Is.EqualTo("transport"));
            Assert.That(call.Attempts, Is.EqualTo(2));
        }

        [Test]
        public void A_transport_error_exchange_is_a_definitive_refusal()
        {
            transport.TransportError("ready");
            var call = Start();
            Run(call, 2);
            Assert.That(call.State, Is.EqualTo(CallState.Refused));
            Assert.That(call.ProblemCode, Is.EqualTo("transport"));
            Assert.That(call.Attempts, Is.EqualTo(1));
        }

        // CP1 M1: Retry-After is a floor on the backoff, never a way to resend every frame.
        [Test]
        public void Retry_after_zero_never_resends_faster_than_the_backoff()
        {
            transport.Answer("ready", 429, "{}", "0").Answer("ready", 429, "{}", "0").Answer("ready", 200);
            var call = Start();
            Run(call, 5);
            Assert.That(call.State, Is.EqualTo(CallState.Succeeded));
            Assert.That(transport.Requests[1].At - transport.Requests[0].At, Is.EqualTo(0.5).Within(0.11));
            Assert.That(transport.Requests[2].At - transport.Requests[1].At, Is.EqualTo(1).Within(0.11));
        }

        [TestCase(408)]
        [TestCase(500)]
        [TestCase(502)]
        [TestCase(504)]
        public void Other_transient_statuses_are_retried(int status)
        {
            transport.Answer("ready", status).Answer("ready", 200);
            var call = Start();
            Run(call, 5);
            Assert.That(call.State, Is.EqualTo(CallState.Succeeded));
            Assert.That(call.Attempts, Is.EqualTo(2));
        }

        [Test]
        public void A_bare_302_is_refused_once()
        {
            transport.Answer("ready", 302, "");
            var call = Start();
            Run(call, 2);
            Assert.That(call.State, Is.EqualTo(CallState.Refused));
            Assert.That(call.Attempts, Is.EqualTo(1));
        }

        [Test]
        public void The_jitter_never_touches_unity_random()
        {
            var before = UnityEngine.Random.state;
            transport.RefuseAlways("ready");
            var call = new BackendCall(transport, Path, Body, new RetryPolicy(), now + 10, BackendRandom.Create(), now);
            Run(call, 10);
            Assert.That(UnityEngine.Random.state, Is.EqualTo(before));
            Assert.That(transport.Requests.Count, Is.GreaterThan(2));
        }
    }
}
