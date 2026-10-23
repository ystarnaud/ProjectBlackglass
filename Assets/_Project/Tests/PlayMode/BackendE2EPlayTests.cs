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
    /// Stage 13 CP4 (owner authorization 2026-10-23), the Unity half: the real -attempt server in the Editor against the real API
    /// hosted by Backend/tests/Accounts.Integration.Tests/E2E/BackendE2EHost.cs (which plays the player, the agent and the network
    /// relay, and owns the disposable database). The real UnityWebRequest transport, the claimed credential through the credential
    /// seam (never a command line), a real portable outbox. Explicit and opt-in: it never runs in the normal suites and ignores
    /// itself without BLACKGLASS_E2E_DIR (the private exchange directory the host created). The files exchanged are listed in
    /// E2EExchange.cs.
    /// </summary>
    [Explicit("Stage 13 CP4: owner-approved runs against a local API only")]
    [Category("BackendE2E")]
    public class BackendE2EPlayTests
    {
        const string ScenePath = "Assets/_Project/Scenes/ServerMission.unity";

        Scene scene;
        ServerBootstrap bootstrap;
        MissionDirector director;
        TacticalPause pause;
        ObservingTransport transport;
        string root, dir, credential, ticket;
        int exitCode;
        readonly List<string> logs = new List<string>();

        [Serializable]
        class HostInfo { public string baseUrl; }

        [Serializable]
        class StartInfo { public string attemptId; public string accountId; }

        [Serializable]
        class UnityResult
        {
            public string scenario;
            public int exitCode;
            public string exitReason;
            public string manifestHash;
            public string outcome;
            public int heldFrames;
            public bool heldUnchanged;
            public bool pausedWhileHeld;
            public bool droppedBeforeRunning;
            public bool writtenBeforeReady;
            public int recordedSends;
            public string recordedAnswer;
            public double notifyingSeconds;
            public string[] startExchanges;
            public string[] recordedExchanges;
            public int collected;
        }

        /// <summary>The real transport, observed: each send is seen before it leaves (the outbox state at that moment), each
        /// exchange's end is kept. Never a header or a body.</summary>
        sealed class ObservingTransport : IHttpTransport
        {
            readonly IHttpTransport inner;
            readonly List<(string Path, HttpExchange Exchange)> sent = new List<(string, HttpExchange)>();
            public Action<string> OnSend;

            public ObservingTransport(IHttpTransport inner) => this.inner = inner;

            public HttpExchange Send(HttpMethodKind method, string path, byte[] jsonBody, double timeoutSeconds, double now)
            {
                OnSend?.Invoke(path);
                var exchange = inner.Send(method, path, jsonBody, timeoutSeconds, now);
                sent.Add((path, exchange));
                return exchange;
            }

            public void Tick(double now) => inner.Tick(now);
            public void Abort(HttpExchange exchange) => inner.Abort(exchange);
            public void AbortAll() => inner.AbortAll();

            public string[] Ends(string suffix) => sent.Where(s => s.Path.EndsWith(suffix, StringComparison.Ordinal))
                .Select(s => s.Exchange.IsDone ? s.Exchange.Outcome + " " + s.Exchange.Status : "Pending").ToArray();
        }

        void Capture(string text, string trace, LogType type) => logs.Add(text + "\n" + trace);

        string PathOf(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        void WriteJson(string relative, object value)
        {
            var path = PathOf(relative);
            File.WriteAllText(path + ".tmp", JsonUtility.ToJson(value, true));
            File.Move(path + ".tmp", path);
        }

        IEnumerator WaitForFile(string relative, float seconds)
        {
            yield return TestWorld.WaitUntil(() => File.Exists(PathOf(relative)), seconds);
            Assert.That(File.Exists(PathOf(relative)), Is.True, relative + " did not appear (is the CP4 host running?)");
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            root = Environment.GetEnvironmentVariable("BLACKGLASS_E2E_DIR");
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                Assert.Ignore("Stage 13 CP4: BLACKGLASS_E2E_DIR is not an existing exchange directory.");
            exitCode = -1;
            credential = ticket = null;
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
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Application.logMessageReceived -= Capture;
            var all = string.Join("\n", logs);
            var leaked = new List<string>();
            if (credential != null && all.Contains(credential.Substring(4))) leaked.Add("the process credential");
            if (ticket != null && all.Contains(ticket)) leaked.Add("the join ticket");
            var seed = bootstrap != null && bootstrap.Session != null ? bootstrap.Session.Attempt.Seed.ToString() : null;
            if (seed != null && seed.Length >= 6 && all.Contains(seed)) leaked.Add("the seed"); // a very short seed would match by chance
            if (bootstrap != null && bootstrap.Runner != null && !bootstrap.HasExited) bootstrap.Quit();
            Time.timeScale = 1f;
            if (director != null) director.Clear();
            yield return null;
            if (scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
            Assert.That(leaked, Is.Empty, "a secret reached the Editor log");
        }

        [UnityTest, Timeout(1800000)]
        public IEnumerator A_a_lost_start_answer_keeps_the_mission_held_until_the_retry_gets_the_committed_start() => Run("A", outage: false);

        [UnityTest, Timeout(1800000)]
        public IEnumerator B_an_api_outage_after_the_start_never_stops_play_or_publication() => Run("B", outage: true);

        IEnumerator Run(string s, bool outage)
        {
            dir = PathOf(s);
            Directory.CreateDirectory(dir); // the host may still be verifying the previous scenario
            WriteJson(s + "/unity-waiting.json", new HostInfo());
            yield return WaitForFile(s + "/start.json", 600f);
            var host = JsonUtility.FromJson<HostInfo>(File.ReadAllText(PathOf("host.json")));
            var start = JsonUtility.FromJson<StartInfo>(File.ReadAllText(PathOf(s + "/start.json")));
            credential = File.ReadAllText(PathOf(s + "/credentials/process"), Encoding.ASCII);
            var written = PathOf(s + "/outbox/" + start.accountId + ".manifest.written");
            var ready = PathOf(s + "/outbox/" + start.accountId + ".manifest.ready");
            var result = new UnityResult { scenario = s, writtenBeforeReady = true };

            bootstrap.CredentialsDirectory = () => PathOf(s + "/credentials");
            bootstrap.OpenAttemptOutbox = id => OutboxDirectories.OpenForEditor(PathOf(s + "/outbox"));
            Action<string> onSend = path =>
            {
                if (!path.EndsWith("/recorded", StringComparison.Ordinal)) return;
                result.recordedSends++;
                result.writtenBeforeReady &= File.Exists(written) && !File.Exists(ready); // the notice names a durable, unpublished result
            };
            bootstrap.TransportFactory = (endpoint, cred) => transport = new ObservingTransport(new UnityWebRequestTransport(endpoint, cred)) { OnSend = onSend };
            var reported = false;
            try
            {
                bootstrap.Begin(new[] { "-attempt", start.attemptId, "-backend", host.baseUrl });

                // Registered, generated, ready: held from the Ready frame.
                yield return TestWorld.WaitUntil(() => bootstrap.HasExited || bootstrap.Runner.Phase == RunnerPhase.Holding, 300f);
                Assert.That(bootstrap.HasExited, Is.False, bootstrap.ExitReason);
                Assert.That(bootstrap.Runner.Phase, Is.EqualTo(RunnerPhase.Holding));
                var units = director.Friendlies.Select(f => f.gameObject).Concat(director.Hostiles.Select(h => h.gameObject)).ToArray();
                var positions = units.Select(u => u.transform.position).ToArray();
                var health = units.Select(u => u.GetComponent<Health>().Current).ToArray();
                var objectives = director.Runtime.Objectives.Select(o => o.State).ToArray();
                bool Unchanged() => units.Select(u => u.transform.position).SequenceEqual(positions)
                    && units.Select(u => u.GetComponent<Health>().Current).SequenceEqual(health)
                    && director.Runtime.Objectives.Select(o => o.State).SequenceEqual(objectives) && bootstrap.Session.CommittedEvents == 0;
                WriteJson(s + "/holding.json", new HostInfo());

                // The ticket from the player's route, admitted; held (paused, nothing moves) until the backend's start answer arrives.
                result.heldUnchanged = true;
                result.pausedWhileHeld = true;
                var deadline = Time.realtimeSinceStartup + 240f;
                while (ticket == null && Time.realtimeSinceStartup < deadline)
                {
                    if (File.Exists(PathOf(s + "/ticket")))
                    {
                        ticket = File.ReadAllText(PathOf(s + "/ticket"), Encoding.ASCII);
                        File.Delete(PathOf(s + "/ticket"));
                    }
                    result.heldUnchanged &= Unchanged();
                    result.pausedWhileHeld &= pause.IsPaused;
                    result.heldFrames++;
                    yield return null;
                }
                Assert.That(ticket, Is.Not.Null, "no ticket arrived");
                Assert.That(bootstrap.Runner.AdmitTicket(ticket).State, Is.EqualTo(TicketAdmissionState.Pending));
                deadline = Time.realtimeSinceStartup + 240f;
                while (!bootstrap.HasExited && bootstrap.Runner.Phase != RunnerPhase.Running && Time.realtimeSinceStartup < deadline)
                {
                    result.heldUnchanged &= Unchanged();
                    result.pausedWhileHeld &= pause.IsPaused;
                    result.heldFrames++;
                    yield return null;
                }
                Assert.That(bootstrap.Runner.Phase, Is.EqualTo(RunnerPhase.Running), bootstrap.ExitReason);
                result.droppedBeforeRunning = File.Exists(PathOf(s + "/start-dropped.json"));
                result.startExchanges = transport.Ends("/start");
                Assert.That(pause.IsPaused, Is.False, "resumed by the committed start");

                // Play: the hostiles down, one container searched and collected, then (B: once the API is stopped) every open
                // objective and the extraction.
                foreach (var hostile in director.Hostiles)
                    hostile.GetComponent<Health>().TakeDamage(100000);
                var lead = director.Friendlies[0];
                var container = director.Current.LootContainers.FirstOrDefault();
                if (container != null)
                {
                    container.transform.position = lead.transform.position + Vector3.right;
                    Physics.SyncTransforms();
                    if (container.Interactable.TryBegin(lead)) container.Interactable.Advance(lead, 10f);
                    result.collected = lead.GetComponent<UnitItems>().TryCollect(container, null).Taken;
                }
                if (outage)
                    yield return WaitForFile(s + "/api-stopped.json", 120f);
                for (var round = 0; round < 40 && !bootstrap.Session.IsFrozen; round++)
                {
                    foreach (var objective in director.Runtime.Objectives.Where(o => o.State == ObjectiveState.Active).ToArray())
                    {
                        if (objective is InteractObjective interact && interact.Interactable != null && interact.Interactable.IsAvailable)
                        {
                            interact.Interactable.transform.position = lead.transform.position + Vector3.right;
                            Physics.SyncTransforms();
                            if (interact.Interactable.TryBegin(lead)) interact.Interactable.Advance(lead, 120f);
                        }
                        else if (objective is ReachZoneObjective zone)
                            foreach (var f in director.Friendlies) f.GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(zone.TargetPosition);
                    }
                    foreach (var hostile in director.Hostiles.Where(h => h.GetComponent<Health>().IsAlive))
                        hostile.GetComponent<Health>().TakeDamage(100000);
                    yield return null;
                    foreach (var f in director.Friendlies.Where(f => f.GetComponent<Health>().IsAlive))
                        f.GetComponent<UnityEngine.AI.NavMeshAgent>().Warp(director.Current.ExtractionZone.position);
                    yield return new WaitForSecondsRealtime(0.25f);
                }
                Assert.That(bootstrap.Session.IsFrozen, Is.True, "the mission ended");

                // The §H steps: durable, the bounded notice, published; then the exit.
                double notifyingFrom = double.NaN, notifyingTo = double.NaN;
                deadline = Time.realtimeSinceStartup + 120f;
                while (!bootstrap.HasExited && Time.realtimeSinceStartup < deadline)
                {
                    var state = bootstrap.Runner.Publisher != null ? bootstrap.Runner.Publisher.State : PublisherState.Idle;
                    if (state == PublisherState.Notifying && double.IsNaN(notifyingFrom)) notifyingFrom = Time.realtimeSinceStartupAsDouble;
                    if (state >= PublisherState.Publishing && !double.IsNaN(notifyingFrom) && double.IsNaN(notifyingTo)) notifyingTo = Time.realtimeSinceStartupAsDouble;
                    yield return null;
                }
                Assert.That(bootstrap.HasExited, Is.True, "the process exited");
                result.exitCode = exitCode;
                result.exitReason = bootstrap.ExitReason;
                result.manifestHash = bootstrap.Session.Outcome.Manifest.ManifestHash;
                result.outcome = bootstrap.Session.Outcome.Manifest.Manifest.Outcome.ToString();
                result.recordedAnswer = bootstrap.Runner.Publisher.RecordedAnswer;
                result.notifyingSeconds = double.IsNaN(notifyingTo) || double.IsNaN(notifyingFrom) ? -1 : notifyingTo - notifyingFrom;
                result.recordedExchanges = transport.Ends("/recorded");
                WriteJson(s + "/unity-result.json", result);
                reported = true;

                Assert.That(exitCode, Is.EqualTo(ServerExitCodes.Success), bootstrap.ExitReason);
                Assert.That(result.heldUnchanged, Is.True, "no gameplay before the confirmed start");
                Assert.That(result.pausedWhileHeld, Is.True, "paused for every held frame");
                Assert.That(result.recordedSends, Is.GreaterThan(0));
                Assert.That(result.writtenBeforeReady, Is.True, "each recorded notice followed the durable write and preceded publication");
                Assert.That(File.ReadAllBytes(ready), Is.EqualTo(bootstrap.Session.Outcome.Manifest.CanonicalBytes));
                Assert.That(File.Exists(written), Is.False);
                if (outage)
                {
                    Assert.That(result.recordedAnswer, Is.EqualTo("gave_up"), "the notice could not reach the stopped API");
                    Assert.That(result.notifyingSeconds, Is.InRange(0d, 35d), "the notice is bounded (30 s)");
                }
                else
                {
                    Assert.That(result.recordedAnswer, Is.EqualTo("ok"));
                    Assert.That(result.droppedBeforeRunning, Is.True, "the first start answer was lost before the mission ran");
                    Assert.That(result.startExchanges.Length, Is.GreaterThanOrEqualTo(2), "the start was retried");
                    Assert.That(result.startExchanges[0], Is.EqualTo("ConnectionFailed 0"));
                    Assert.That(result.startExchanges.Last(), Is.EqualTo("Completed 200"));
                }
            }
            finally
            {
                if (!reported) // a failure before the exit: tell the host at once instead of letting it wait out its bound
                {
                    result.exitCode = bootstrap.HasExited ? exitCode : -1;
                    result.exitReason = bootstrap.HasExited ? bootstrap.ExitReason : "unity_test_failed_before_exit";
                    WriteJson(s + "/unity-result.json", result);
                }
            }
        }
    }
}
#endif
