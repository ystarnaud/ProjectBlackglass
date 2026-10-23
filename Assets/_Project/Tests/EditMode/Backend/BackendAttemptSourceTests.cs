using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The authenticated registration (Stage 13, Task 6): retried under its own bound, polled, validated, never logged, never a fixture.</summary>
    public sealed class BackendAttemptSourceTests
    {
        const string Catalogue = "sha256:e83352b1c9d8764a92281551a25bea42621da2b592bc30d35986a5f0a86c496b";
        const int SecretSeed = 918273645;

        FakeHttpTransport transport;
        StepClock clock;

        [SetUp]
        public void SetUp()
        {
            transport = new FakeHttpTransport(BackendTestKit.Prefix);
            clock = new StepClock();
        }

        static string Answer() => JsonUtility.ToJson(new SessionRegisterResponse
        {
            processSessionId = BackendTestKit.Session,
            seed = SecretSeed,
            missionTemplate = new MissionTemplateDto { id = "procedural-standard", version = 1 },
            catalogueVersion = Catalogue,
            participants = new[] { new RegisterParticipantDto { accountId = "JG", loadout = new LoadoutSnapshotDto { accountId = "JG" } } },
            deadlines = new LaunchDeadlineDto { launch = "2026-10-23T12:02:00Z" },
        });

        RegistrationStatus Run(BackendAttemptSource source, double seconds, out SessionRegisterResponse response, out string problem)
        {
            var status = RegistrationStatus.Pending;
            SessionRegisterResponse r = null;
            string p = null;
            clock.Run(transport, now => status = source.Poll(now, out r, out p), seconds, () => status != RegistrationStatus.Pending);
            response = r;
            problem = p;
            return status;
        }

        [Test]
        public void Transient_failures_are_retried_until_the_answer()
        {
            transport.Answer("sessions", 503).Answer("sessions", 503).Answer("sessions", 200, Answer());
            var source = new BackendAttemptSource(BackendTestKit.Api(transport), "editor", Catalogue, 60, clock.Now);
            Assert.That(Run(source, 10, out var response, out var problem), Is.EqualTo(RegistrationStatus.Loaded), problem);
            Assert.That(response.processSessionId, Is.EqualTo(BackendTestKit.Session));
            Assert.That(response.seed, Is.EqualTo(SecretSeed));
            Assert.That(transport.To("sessions").Count(), Is.EqualTo(3));
            Assert.That(transport.To("sessions").First().BodyText, Is.EqualTo("{\"buildRevision\":\"editor\",\"catalogueVersion\":\"" + Catalogue + "\"}"));
        }

        [TestCase(409, "BuildNotAllowed", "register_refused 409 BuildNotAllowed")]
        [TestCase(409, "InvalidState", "register_refused 409 InvalidState")]
        public void A_refusal_fails_with_its_code(int status, string code, string expected)
        {
            transport.Answer("sessions", status, BackendTestKit.Problem(code));
            var source = new BackendAttemptSource(BackendTestKit.Api(transport), "editor", Catalogue, 60, clock.Now);
            Assert.That(Run(source, 5, out var response, out var problem), Is.EqualTo(RegistrationStatus.Failed));
            Assert.That(response, Is.Null);
            Assert.That(problem, Is.EqualTo(expected));
        }

        [Test]
        public void A_401_fails_unauthorized()
        {
            transport.Answer("sessions", 401, "");
            var source = new BackendAttemptSource(BackendTestKit.Api(transport), "editor", Catalogue, 60, clock.Now);
            Assert.That(Run(source, 5, out _, out var problem), Is.EqualTo(RegistrationStatus.Failed));
            Assert.That(problem, Is.EqualTo("register_unauthorized"));
        }

        [Test]
        public void The_bound_ends_it()
        {
            transport.RefuseAlways("sessions");
            var source = new BackendAttemptSource(BackendTestKit.Api(transport), "editor", Catalogue, 20, clock.Now);
            Assert.That(Run(source, 30, out _, out var problem), Is.EqualTo(RegistrationStatus.Failed));
            Assert.That(problem, Does.StartWith("register_gave_up"));
            Assert.That(transport.To("sessions").Last().At, Is.LessThan(100 + 20));
        }

        [Test]
        public void An_answer_that_does_not_hold_fails_and_never_shows_its_body()
        {
            transport.Answer("sessions", 200, Answer().Replace(BackendTestKit.Session, "not-a-session"));
            var source = new BackendAttemptSource(BackendTestKit.Api(transport), "editor", Catalogue, 60, clock.Now);
            Assert.That(Run(source, 5, out var response, out var problem), Is.EqualTo(RegistrationStatus.Failed));
            Assert.That(response, Is.Null);
            Assert.That(problem, Is.EqualTo("register_bad_answer bad_session_id"));
            Assert.That(problem, Does.Not.Contain(SecretSeed.ToString()));
        }

        [Test]
        public void Nothing_is_logged_and_the_seed_never_appears()
        {
            var lines = new System.Collections.Generic.List<string>();
            Application.LogCallback capture = (text, trace, type) => lines.Add(text);
            Application.logMessageReceived += capture;
            try
            {
                transport.Answer("sessions", 200, Answer());
                var source = new BackendAttemptSource(BackendTestKit.Api(transport), "editor", Catalogue, 60, clock.Now);
                Run(source, 5, out _, out _);
            }
            finally { Application.logMessageReceived -= capture; }
            Assert.That(lines.Any(l => l.Contains(SecretSeed.ToString())), Is.False);
        }
    }
}
