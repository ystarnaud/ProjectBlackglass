using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>The process heartbeat (Stage 13, Task 8): cadence, one in flight, the body, reachability over the lease, and the 401 stop.</summary>
    public sealed class ProcessHeartbeatTests
    {
        FakeHttpTransport transport;
        StepClock clock;
        ProcessHeartbeat heartbeat;
        List<string> log;
        string phase;
        bool connected;

        [SetUp]
        public void SetUp()
        {
            transport = new FakeHttpTransport(BackendTestKit.Prefix);
            clock = new StepClock();
            log = new List<string>();
            phase = "holding";
            connected = false;
            heartbeat = new ProcessHeartbeat(BackendTestKit.Api(transport), BackendTestKit.Session, "JG", () => phase, () => connected, new BackendTimings(), log.Add, clock.Now);
        }

        void Run(double seconds) => clock.Run(transport, heartbeat.Tick, seconds);

        [Test]
        public void It_beats_every_interval_from_registration()
        {
            transport.AnswerAlways("heartbeat", 200, "{}");
            Run(35);
            var at = transport.To("heartbeat").Select(r => r.At - 100).ToArray();
            Assert.That(at.Length, Is.EqualTo(3));
            Assert.That(at[0], Is.EqualTo(10).Within(0.11));
            Assert.That(at[1], Is.EqualTo(20).Within(0.11));
            Assert.That(at[2], Is.EqualTo(30).Within(0.11));
        }

        [Test]
        public void The_body_names_the_session_the_phase_and_the_connection()
        {
            transport.AnswerAlways("heartbeat", 200, "{}");
            Run(10.5);
            phase = "running";
            connected = true;
            Run(10);
            Assert.That(transport.To("heartbeat").Select(r => r.BodyText), Is.EqualTo(new[]
            {
                "{\"processSessionId\":\"" + BackendTestKit.Session + "\",\"phase\":\"holding\",\"participants\":[{\"accountId\":\"JG\",\"connected\":false}]}",
                "{\"processSessionId\":\"" + BackendTestKit.Session + "\",\"phase\":\"running\",\"participants\":[{\"accountId\":\"JG\",\"connected\":true}]}",
            }));
        }

        [Test]
        public void A_slow_answer_never_stacks_a_second_beat()
        {
            transport.Answer("heartbeat", 200, "{}", delay: 9.5).AnswerAlways("heartbeat", 200, "{}");
            Run(21);
            var at = transport.To("heartbeat").Select(r => r.At - 100).ToArray();
            Assert.That(at.Length, Is.EqualTo(2));
            Assert.That(at[1] - at[0], Is.GreaterThanOrEqualTo(9.9));
        }

        [Test]
        public void Reachability_follows_the_lease()
        {
            transport.RefuseAlways("heartbeat");
            Run(29);
            Assert.That(heartbeat.BackendReachable, Is.True, "within the lease after registration");
            Run(2);
            Assert.That(heartbeat.BackendReachable, Is.False, "past the lease with no success");
            transport.Clear("heartbeat").AnswerAlways("heartbeat", 200, "{}");
            Run(11);
            Assert.That(heartbeat.BackendReachable, Is.True, "the next success");
        }

        [Test]
        public void A_401_stops_it_and_no_other_answer_does()
        {
            transport.Answer("heartbeat", 400, BackendTestKit.Problem("UnknownParticipant")).Answer("heartbeat", 409, BackendTestKit.Problem("WrongSession"))
                .Answer("heartbeat", 409, BackendTestKit.Problem("WrongSession")).Answer("heartbeat", 401, "");
            Run(60);
            Assert.That(heartbeat.Unauthorized, Is.True);
            Assert.That(transport.To("heartbeat").Count(), Is.EqualTo(4));
            Assert.That(log.Count(l => l.Contains("WrongSession")), Is.EqualTo(1), "each code is logged once");
            Assert.That(log.Count(l => l.Contains("UnknownParticipant")), Is.EqualTo(1));
        }

        [Test]
        public void Stop_aborts_the_beat_in_flight()
        {
            transport.SilentAlways("heartbeat");
            Run(10.5);
            heartbeat.Stop();
            Run(30);
            Assert.That(transport.To("heartbeat").Count(), Is.EqualTo(1));
        }

        [Test]
        public void A_beats_retries_never_pass_the_next_beat()
        {
            transport.RefuseAlways("heartbeat");
            Run(10.2);
            var first = transport.Requests.Count;
            Run(9.7);
            Assert.That(transport.To("heartbeat").Max(r => r.At), Is.LessThan(100 + 20), "the first beat's retries end by the next interval");
            Assert.That(transport.Requests.Count, Is.GreaterThan(first), "it retried within its interval");
        }
    }
}
