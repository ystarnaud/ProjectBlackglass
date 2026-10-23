#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Blackglass.Backend.Adapter;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 13 Task 10 on the real server scene: -attempt runs the backend lifecycle over a scripted transport, a temp credential and a
    /// portable outbox. The mission is paused from the Ready frame until the backend commits the start; the frozen result is written,
    /// notified and published in §H order; refusals, a 401, an outage and a quit end it with their exit codes; nothing secret is logged.
    /// </summary>
    public class BackendAttemptPlayTests
    {
        const string ScenePath = "Assets/_Project/Scenes/ServerMission.unity";
        const string Credential = "pc1_q7Xh3kP0vYzR8mN2bW5tLc9gJdF1sA4eU6iO_-VxyzQ";
        const string Ticket = "jt1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const int SecretSeed = 918273645;

        Scene scene;
        ServerBootstrap bootstrap;
        MissionDirector director;
        TacticalPause pause;
        FakeHttpTransport transport;
        string credentials, outbox, attemptId;
        SessionRegisterResponse registered;
        int exitCode;
        readonly List<string> logs = new List<string>();

        static string Fixtures => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Backend", "contracts", "attempt-v1");

        static string Stamp(double secondsFromNow) => DateTime.UtcNow.AddSeconds(secondsFromNow).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");

        void Capture(string text, string trace, LogType type) => logs.Add(text + "\n" + trace);

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            exitCode = -1;
            logs.Clear();
            Application.logMessageReceived += Capture;
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            var roots = scene.GetRootGameObjects();
            bootstrap = roots.Single(g => g.name == "Server").GetComponent<ServerBootstrap>();
            director = roots.Single(g => g.name == "Mission").GetComponent<MissionDirector>();
            pause = UnityEngine.Object.FindObjectsByType<TacticalPause>(FindObjectsSortMode.None).Single(p => p.gameObject.scene == scene);
            bootstrap.Exit = code => exitCode = code;
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 60f);
            director.Clear();
            yield return null;

            Assert.That(new FixtureAttemptSource(Path.Combine(Fixtures, "03-seeded-collect-and-use.json")).TryLoad(out attemptId, out registered, out var problem), Is.True, problem);
            credentials = Path.Combine(Application.temporaryCachePath, "bg-cred-" + Guid.NewGuid().ToString("N"));
            outbox = Path.Combine(Application.temporaryCachePath, "bg-outbox-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(credentials);
            Directory.CreateDirectory(outbox);
            File.WriteAllBytes(Path.Combine(credentials, "process"), Encoding.ASCII.GetBytes(Credential));
            transport = new FakeHttpTransport("/server/v1/attempts/" + attemptId + "/");
            bootstrap.CredentialsDirectory = () => credentials;
            bootstrap.OpenAttemptOutbox = id => OutboxDirectories.OpenForEditor(outbox);
            bootstrap.TransportFactory = (e, c) => transport;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Application.logMessageReceived -= Capture;
            if (bootstrap != null && bootstrap.Runner != null) bootstrap.Quit();
            Time.timeScale = 1f;
            if (director != null) director.Clear();
            yield return null;
            if (scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
            foreach (var path in new[] { credentials, outbox })
                if (path != null && Directory.Exists(path)) Directory.Delete(path, true);
        }

        string RegisterAnswer(int? seed = null)
        {
            registered.deadlines = new LaunchDeadlineDto { launch = Stamp(120) };
            if (seed.HasValue) registered.seed = seed.Value;
            return JsonUtility.ToJson(registered);
        }

        void Script(int? seed = null)
        {
            transport.Answer("sessions", 200, RegisterAnswer(seed)).Answer("ready", 200, "{\"joinDeadline\":\"" + Stamp(120) + "\"}")
                .AnswerAlways("heartbeat", 200, "{}").AnswerAlways("failed", 200, "{\"applied\":\"none\"}");
        }

        void Begin() => bootstrap.Begin(new[] { "-attempt", attemptId, "-backend", "http://127.0.0.1:5081", "-backendHeartbeat", "2", "-recordedBound", "5" });

        IEnumerator ToHolding(int? seed = null)
        {
            Script(seed);
            Begin();
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited || (bootstrap.Runner != null && bootstrap.Runner.Phase == RunnerPhase.Holding), 60f);
            Assert.That(bootstrap.HasExited, Is.False, bootstrap.ExitReason);
            Assert.That(bootstrap.Runner.Phase, Is.EqualTo(RunnerPhase.Holding));
        }

        IEnumerator ToRunning()
        {
            yield return ToHolding();
            transport.Answer("tickets/redeem", 200, "{\"accountId\":\"JG\",\"operativeIds\":[\"C\",\"D\"]}")
                .Answer("start", 200, "{\"started\":true,\"startedAt\":\"" + Stamp(0) + "\"}");
            Assert.That(bootstrap.Runner.AdmitTicket(Ticket).State, Is.EqualTo(TicketAdmissionState.Pending));
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited || bootstrap.Runner.Phase == RunnerPhase.Running, 30f);
            Assert.That(bootstrap.Runner.Phase, Is.EqualTo(RunnerPhase.Running), bootstrap.ExitReason);
        }

        void Finish()
        {
            foreach (var hostile in director.Hostiles)
                hostile.GetComponent<Health>().TakeDamage(100000);
            director.Friendlies[0].GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(director.Current.ExtractionZone.position);
        }

        string ReadyFile => Path.Combine(outbox, "JG.manifest.ready");

        [UnityTest]
        public IEnumerator The_mission_is_held_from_the_ready_frame_until_the_backend_commits_the_start()
        {
            yield return ToHolding();
            Assert.That(pause.IsPaused, Is.True);
            Assert.That(Time.timeScale, Is.EqualTo(0f));
            var units = director.Friendlies.Concat(director.Hostiles).ToArray();
            var positions = units.Select(u => u.transform.position).ToArray();
            var health = units.Select(u => u.GetComponent<Health>().Current).ToArray();
            var objectives = director.Runtime.Objectives.Select(o => o.State).ToArray();
            for (var frame = 0; frame < 300; frame++)
                yield return null;
            Assert.That(units.Select(u => u.transform.position), Is.EqualTo(positions), "no unit moved during the hold");
            Assert.That(units.Select(u => u.GetComponent<Health>().Current), Is.EqualTo(health), "no combat during the hold");
            Assert.That(bootstrap.Session.CommittedEvents, Is.Zero, "no committed gameplay event during the hold");
            Assert.That(director.Runtime.Objectives.Select(o => o.State), Is.EqualTo(objectives), "no objective progress");
            transport.Answer("tickets/redeem", 200, "{\"accountId\":\"JG\",\"operativeIds\":[\"C\",\"D\"]}")
                .Answer("start", 200, "{\"started\":true,\"startedAt\":\"" + Stamp(0) + "\"}");
            bootstrap.Runner.AdmitTicket(Ticket);
            yield return TestWorld.WaitUntil(() => bootstrap.Runner.Phase == RunnerPhase.Running, 30f);
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.GreaterThan(0f));
        }

        [UnityTest]
        public IEnumerator A_whole_run_writes_notifies_publishes_and_exits_0()
        {
            yield return ToRunning();
            var sawWritten = false;
            transport.OnSend = sent => { if (sent.Path.EndsWith("/recorded")) sawWritten = File.Exists(Path.Combine(outbox, "JG.manifest.written")) && !File.Exists(ReadyFile); };
            transport.Answer("recorded", 200, "{\"state\":\"result_pending\"}");
            Finish();
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited, 30f);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.Success), bootstrap.ExitReason);
            Assert.That(sawWritten, Is.True, "recorded named a durable, unpublished result");
            Assert.That(File.ReadAllBytes(ReadyFile), Is.EqualTo(bootstrap.Session.Outcome.Manifest.CanonicalBytes));
            Assert.That(transport.Routes.Distinct().Except(new[] { "sessions", "ready", "tickets/redeem", "start", "heartbeat", "recorded" }), Is.Empty);
            Assert.That(transport.To("recorded").Single().BodyText, Does.Contain(bootstrap.Session.Outcome.Manifest.ManifestHash));
        }

        [UnityTest]
        public IEnumerator A_refused_start_never_runs_the_mission_and_exits_5()
        {
            yield return ToHolding();
            transport.Answer("tickets/redeem", 200, "{\"accountId\":\"JG\",\"operativeIds\":[\"C\",\"D\"]}").Answer("start", 409, "{\"code\":\"Cancelled\"}");
            bootstrap.Runner.AdmitTicket(Ticket);
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited, 30f);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.NotStarted), bootstrap.ExitReason);
            Assert.That(pause.IsPaused, Is.True, "never resumed");
            Assert.That(Directory.GetFiles(outbox), Is.Empty);
        }

        [UnityTest]
        public IEnumerator A_heartbeat_401_while_running_stops_with_exit_6_and_no_outcome()
        {
            yield return ToRunning();
            transport.Clear("heartbeat").AnswerAlways("heartbeat", 401, "");
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited, 15f);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.Stopped), bootstrap.ExitReason);
            Assert.That(Directory.GetFiles(outbox), Is.Empty, "no outcome fabricated");
        }

        [UnityTest]
        public IEnumerator A_heartbeat_401_while_notifying_still_publishes_then_exits_6()
        {
            yield return ToRunning();
            transport.SilentAlways("recorded");
            Finish();
            yield return TestWorld.WaitUntil(() => bootstrap.Runner.Phase == RunnerPhase.Publishing, 30f);
            transport.Clear("heartbeat").AnswerAlways("heartbeat", 401, "");
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited, 30f);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.Stopped), bootstrap.ExitReason);
            Assert.That(File.ReadAllBytes(ReadyFile), Is.EqualTo(bootstrap.Session.Outcome.Manifest.CanonicalBytes));
        }

        [UnityTest]
        public IEnumerator An_outage_after_the_start_never_stops_play_or_publication()
        {
            yield return ToRunning();
            transport.Clear("heartbeat").RefuseAlways("heartbeat").RefuseAlways("recorded");
            Finish();
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited, 30f);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.Success), bootstrap.ExitReason);
            Assert.That(File.ReadAllBytes(ReadyFile), Is.EqualTo(bootstrap.Session.Outcome.Manifest.CanonicalBytes));
        }

        [UnityTest]
        public IEnumerator A_quit_while_notifying_publishes_on_the_drain()
        {
            yield return ToRunning();
            transport.SilentAlways("recorded");
            Finish();
            yield return TestWorld.WaitUntil(() => bootstrap.Runner.Publisher != null && bootstrap.Runner.Publisher.State == PublisherState.Notifying, 30f);
            bootstrap.Quit();
            Assert.That(File.ReadAllBytes(ReadyFile), Is.EqualTo(bootstrap.Session.Outcome.Manifest.CanonicalBytes));
        }

        [UnityTest]
        public IEnumerator A_quit_while_holding_leaves_no_result()
        {
            yield return ToHolding();
            bootstrap.Quit();
            yield return null;
            Assert.That(Directory.GetFiles(outbox), Is.Empty);
        }

        [UnityTest]
        public IEnumerator A_refused_outbox_exits_4_before_any_request()
        {
            bootstrap.OpenAttemptOutbox = id => throw new OutboxIOException("missing", 0);
            Script();
            Begin();
            yield return null;
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.BackendRefused));
            Assert.That(bootstrap.ExitReason, Is.EqualTo("outbox: missing"));
            Assert.That(transport.Requests, Is.Empty);
        }

        [UnityTest]
        public IEnumerator No_credential_ticket_or_seed_is_ever_logged()
        {
            yield return ToHolding(SecretSeed);
            transport.Answer("tickets/redeem", 401, "");
            bootstrap.Runner.AdmitTicket(Ticket);
            for (var frame = 0; frame < 30; frame++)
                yield return null;
            var text = string.Join("\n", logs);
            Assert.That(text, Does.Not.Contain(Credential.Substring(4)));
            Assert.That(text, Does.Not.Contain(Ticket));
            Assert.That(text, Does.Not.Contain(SecretSeed.ToString()));
            Assert.That(text, Does.Contain("credential=present"), "the credential's presence is logged, never its value");
        }
    }
}
#endif
