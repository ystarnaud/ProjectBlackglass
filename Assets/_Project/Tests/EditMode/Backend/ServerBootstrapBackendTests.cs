using System;
using System.IO;
using System.Text;
using Blackglass.Backend.Adapter;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// The bootstrap's backend mode refuses before any request (Stage 13, Task 6): option conflicts (checked before the self-test's
    /// early return), a non-canonical attempt id, a non-loopback backend, an unstamped player build, a missing or malformed credential,
    /// and an unwired scene. Each ends with exit 4 and the fake transport sees nothing. The scene-backed paths are PlayMode's.
    /// </summary>
    public sealed class ServerBootstrapBackendTests
    {
        const string Attempt = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";
        GameObject go;
        ServerBootstrap bootstrap;
        FakeHttpTransport transport;
        string credentials;
        int exit;

        [SetUp]
        public void SetUp()
        {
            go = new GameObject("Server");
            bootstrap = go.AddComponent<ServerBootstrap>();
            exit = -1;
            bootstrap.Exit = code => exit = code;
            transport = new FakeHttpTransport();
            bootstrap.TransportFactory = (e, c) => transport;
            credentials = Path.Combine(Path.GetTempPath(), "bg-boot-cred-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(credentials);
            File.WriteAllBytes(Path.Combine(credentials, "process"), Encoding.ASCII.GetBytes(ProcessCredentialTests.Valid));
            bootstrap.CredentialsDirectory = () => credentials;
            bootstrap.OpenAttemptOutbox = _ => throw new InvalidOperationException("the outbox must not be opened by a refused start");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(go);
            if (Directory.Exists(credentials)) Directory.Delete(credentials, true);
        }

        void Refused(string reasonPart, params string[] extra)
        {
            var args = new System.Collections.Generic.List<string> { "-attempt", Attempt, "-backend", "http://127.0.0.1:5081" };
            args.AddRange(extra);
            bootstrap.Begin(args);
            Assert.That(exit, Is.EqualTo(ServerExitCodes.BackendRefused), bootstrap.ExitReason);
            Assert.That(bootstrap.ExitReason, Does.Contain(reasonPart));
            Assert.That(transport.Requests, Is.Empty, "no request before every local check passed");
            Assert.That(bootstrap.Runner, Is.Null);
        }

        [TestCase("-attemptFixture", "x.json")]
        [TestCase("-outboxSelftest", "publish")]
        [TestCase("-diagnostic", null)]
        [TestCase("-seed", "5")]
        [TestCase("-net", null)]
        [TestCase("-netTokens", "tokens.json")]
        [TestCase("-netQuiet", null)]
        [TestCase("-netFixture", null)]
        public void A_conflicting_option_is_refused(string option, string value)
        {
            if (value == null) Refused("-attempt can't be combined with " + option, option);
            else Refused("-attempt can't be combined with " + option, option, value);
        }

        [Test]
        public void The_selftest_never_runs_beside_an_attempt()
        {
            Refused("-outboxSelftest", "-outboxSelftest", "publish");
            Assert.That(bootstrap.ExitReason, Does.Not.Contain("[Selftest]"));
        }

        [Test]
        public void A_non_canonical_attempt_id_is_refused()
        {
            bootstrap.Begin(new[] { "-attempt", Attempt.ToUpperInvariant(), "-backend", "http://127.0.0.1:5081" });
            Assert.That(exit, Is.EqualTo(ServerExitCodes.BackendRefused));
            Assert.That(bootstrap.ExitReason, Does.Contain("canonical attempt id"));
            Assert.That(transport.Requests, Is.Empty);
        }

        [Test]
        public void A_missing_attempt_id_never_starts_the_plain_mission()
        {
            bootstrap.Begin(new[] { "-attempt", "-backend", "http://127.0.0.1:5081" });
            Assert.That(exit, Is.EqualTo(ServerExitCodes.BackendRefused));
        }

        [TestCase("http://10.0.0.1:5081", "not_loopback")]
        [TestCase("https://127.0.0.1:5081", "not_http")]
        [TestCase(null, "missing")]
        public void A_backend_that_is_not_loopback_http_is_refused(string backend, string code)
        {
            var args = backend == null ? new[] { "-attempt", Attempt } : new[] { "-attempt", Attempt, "-backend", backend };
            bootstrap.Begin(args);
            Assert.That(exit, Is.EqualTo(ServerExitCodes.BackendRefused));
            Assert.That(bootstrap.ExitReason, Is.EqualTo("-backend: " + code));
            Assert.That(transport.Requests, Is.Empty);
        }

        [Test]
        public void A_player_without_a_stamped_revision_is_refused()
        {
            bootstrap.BackendBuildRevision = () => null;
            Refused("stamped, well-formed build revision");
        }

        [Test]
        public void A_missing_credential_is_refused()
        {
            File.Delete(Path.Combine(credentials, "process"));
            Refused("credential: missing");
        }

        [Test]
        public void A_malformed_credential_is_refused_without_showing_it()
        {
            File.WriteAllText(Path.Combine(credentials, "process"), ProcessCredentialTests.Valid + "\n");
            Refused("credential: malformed");
            Assert.That(bootstrap.ExitReason, Does.Not.Contain(ProcessCredentialTests.Valid.Substring(4, 10)));
        }

        [Test]
        public void No_credentials_directory_is_refused()
        {
            bootstrap.CredentialsDirectory = () => null;
            Refused("credential: no_directory");
        }

        [Test]
        public void An_unwired_scene_is_refused_before_the_outbox_or_any_request()
        {
            Refused("not wired for attempts");
        }
    }
}
