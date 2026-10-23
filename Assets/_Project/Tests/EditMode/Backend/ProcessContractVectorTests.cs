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
    /// The process-v1 contract vectors (Stage 13, Task 4), Unity's side: the request bodies the mission process sends are the vectors'
    /// bytes exactly (the backend's ProcessRequestVectorTests parse the same files strictly), and the response vectors (the API's own
    /// records serialized with its own options, ProcessResponseVectorTests) parse into the DTOs with the expected values. Key order in
    /// a response is not pinned: the stored loadout comes back from PostgreSQL jsonb in its own order.
    /// </summary>
    public sealed class ProcessContractVectorTests
    {
        const string Folder = "Backend/contracts/process-v1";
        const string Session = "0192f1a5-0000-7000-8000-000000000003";
        const string Catalogue = "sha256:e83352b1c9d8764a92281551a25bea42621da2b592bc30d35986a5f0a86c496b";
        const string Attempt = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e03";

        static string Vector(string name) => File.ReadAllText(Path.Combine(Folder, name), new UTF8Encoding(false));

        static FakeHttpTransport transport;
        static ProcessApi api;

        [SetUp]
        public void SetUp()
        {
            transport = new FakeHttpTransport();
            api = new ProcessApi(transport, Attempt, new RetryPolicy(), () => 0.5);
        }

        static string LastBody() => transport.Requests[transport.Requests.Count - 1].BodyText;

        [Test]
        public void The_vectors_are_utf8_without_bom_or_newline()
        {
            foreach (var path in Directory.GetFiles(Folder, "*.json"))
            {
                var bytes = File.ReadAllBytes(path);
                Assert.That(bytes[0], Is.EqualTo((byte)'{'), path);
                Assert.That(bytes[bytes.Length - 1], Is.EqualTo((byte)'}'), path);
            }
        }

        [Test]
        public void Register_request()
        {
            api.Register(new SessionRegisterRequest { buildRevision = "editor", catalogueVersion = Catalogue }, 10, 0);
            Assert.That(LastBody(), Is.EqualTo(Vector("register.request.json")));
        }

        [Test]
        public void Ready_request_is_fixture_03s_ready_bytes()
        {
            var bytes = File.ReadAllBytes("Backend/contracts/attempt-v1/03-seeded-collect-and-use.ready.json");
            var dto = JsonUtility.FromJson<ReadyRequest>(Encoding.UTF8.GetString(bytes));
            api.Ready(Encoding.UTF8.GetBytes(JsonUtility.ToJson(dto)), 10, 0);
            Assert.That(transport.Requests[0].Body, Is.EqualTo(bytes));
        }

        [Test]
        public void Redeem_start_recorded_and_failed_requests()
        {
            api.Redeem(Session, ProcessApiTests.Ticket, 10, 0);
            Assert.That(LastBody(), Is.EqualTo(Vector("redeem.request.json")));
            api.Start(Session, "JG", 10, 0);
            Assert.That(LastBody(), Is.EqualTo(Vector("start.request.json")));
            var manifest = File.ReadAllBytes("Backend/contracts/attempt-v1/03-seeded-collect-and-use.manifest.json");
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = "sha256:" + BitConverter.ToString(sha.ComputeHash(manifest)).Replace("-", "").ToLowerInvariant();
            api.Recorded("JG", hash, 10, 0);
            Assert.That(LastBody(), Is.EqualTo(Vector("recorded.request.json")));
            api.Failed(Session, "start_refused", 10, 0);
            Assert.That(LastBody(), Is.EqualTo(Vector("failed.request.json")));
        }

        [Test]
        public void Heartbeat_request()
        {
            api.Heartbeat(new ProcessHeartbeatRequest
            {
                processSessionId = Session,
                phase = "running",
                participants = new[] { new ParticipantConnectionDto { accountId = "JG", connected = true } },
            }, 10, 0);
            Assert.That(LastBody(), Is.EqualTo(Vector("heartbeat.request.json")));
        }

        BackendCall Answered(string suffix, string vector, Func<BackendCall> start)
        {
            transport.Answer(suffix, 200, Vector(vector));
            var call = start();
            for (var t = 0.05; t < 1 && !call.IsFinished; t += 0.05)
            {
                transport.Tick(t);
                call.Tick(t);
            }
            return call;
        }

        [TestCase("register.response.json")]
        [TestCase("register.response.shuffled.json")]
        public void Register_response_in_any_key_order(string vector)
        {
            var call = Answered("sessions", vector, () => api.Register(new SessionRegisterRequest(), 10, 0));
            Assert.That(ProcessApi.TryRegistered(call, out var r, out var problem), Is.True, problem);
            Assert.That(r.processSessionId, Is.EqualTo(Session));
            Assert.That(r.seed, Is.EqualTo(1));
            Assert.That(r.missionTemplate.id, Is.EqualTo("procedural-standard"));
            Assert.That(r.missionTemplate.version, Is.EqualTo(1));
            Assert.That(r.catalogueVersion, Is.EqualTo(Catalogue));
            var loadout = r.participants[0].loadout;
            Assert.That(r.participants[0].accountId, Is.EqualTo("JG"));
            Assert.That(loadout.accountId, Is.EqualTo("JG"));
            Assert.That(loadout.inventoryVersion, Is.EqualTo(3));
            Assert.That(loadout.operatives.Length, Is.EqualTo(2));
            Assert.That(loadout.operatives[0].operativeId, Is.EqualTo("C"));
            Assert.That(loadout.operatives[0].equipment.Weapon, Is.EqualTo("2k"));
            Assert.That(loadout.operatives[0].equipment.Armor, Is.Null);
            Assert.That(loadout.operatives[0].bag.stacks[0].quantity, Is.EqualTo(2));
            Assert.That(loadout.operatives[1].bag.instances[0].itemId, Is.EqualTo("2m"));
        }

        [Test]
        public void Both_register_vectors_give_the_same_snapshot()
        {
            var a = Answered("sessions", "register.response.json", () => api.Register(new SessionRegisterRequest(), 10, 0));
            var b = Answered("sessions", "register.response.shuffled.json", () => api.Register(new SessionRegisterRequest(), 10, 0));
            ProcessApi.TryRegistered(a, out var ra, out _);
            ProcessApi.TryRegistered(b, out var rb, out _);
            Assert.That(JsonUtility.ToJson(rb), Is.EqualTo(JsonUtility.ToJson(ra)));
        }

        [Test]
        public void Ready_redeem_start_and_problem_responses()
        {
            Assert.That(ProcessApi.TryReady(Answered("ready", "ready.response.json", () => api.Ready(new byte[] { 0x7b, 0x7d }, 10, 0)), out var deadline, out _), Is.True);
            Assert.That(deadline, Is.EqualTo(new DateTime(2026, 10, 23, 12, 2, 0, 500, DateTimeKind.Utc)));
            Assert.That(ProcessApi.TryRedeemed(Answered("tickets/redeem", "redeem.response.json", () => api.Redeem(Session, ProcessApiTests.Ticket, 10, 0)), out var redeemed, out _), Is.True);
            Assert.That(redeemed.AccountId, Is.EqualTo("JG"));
            Assert.That(redeemed.OperativeIds, Is.EqualTo(new[] { "C", "D" }));
            Assert.That(ProcessApi.TryStarted(Answered("start", "start.response.json", () => api.Start(Session, "JG", 10, 0)), out var started, out _), Is.True);
            Assert.That(started.started, Is.True);
            transport.Answer("start", 409, Vector("problem.409.json"));
            var refused = api.Start(Session, "JG", 10, 0);
            for (var t = 0.05; t < 1 && !refused.IsFinished; t += 0.05) { transport.Tick(t); refused.Tick(t); }
            Assert.That(refused.ProblemCode, Is.EqualTo("Cancelled"));
            Assert.That(JsonUtility.FromJson<StateResponse>(Vector("recorded.response.json")).state, Is.EqualTo("result_pending"));
        }

        [Serializable]
        sealed class Timestamps
        {
            public string[] valid;
            public string[] invalid;
        }

        [Test]
        public void Every_timestamp_shape_parses_and_no_other()
        {
            var t = JsonUtility.FromJson<Timestamps>(Vector("timestamps.json"));
            var first = default(DateTime);
            foreach (var text in t.valid)
            {
                Assert.That(ProcessApi.TryParseTimestamp(text, out var utc), Is.True, text);
                if (first == default) first = utc;
                Assert.That(utc.Date, Is.EqualTo(first.Date));
            }
            foreach (var text in t.invalid)
                Assert.That(ProcessApi.TryParseTimestamp(text, out _), Is.False, text);
        }
    }
}
