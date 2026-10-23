using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Blackglass.Backend.Adapter;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// The backend attempt runner (Stage 13, Task 10) over a fake host, a scripted transport and a real outbox: the phases, the ready
    /// report, the hold, the failed reports and every exit code (0, 4–7), and the exact set of routes a run may use (never a status,
    /// receipt or result route).
    /// </summary>
    public sealed class BackendAttemptRunnerTests
    {
        sealed class FakeHost : IBackendAttemptHost
        {
            public string Refusal;
            public bool IsPaused { get; set; }
            public int Pauses, Resumes, ClockRestarts, Continued;
            public readonly List<string> Lines = new List<string>();
            public int? ExitCode;
            public string ExitReason;

            public string ContinueAttempt(string attemptId, SessionRegisterResponse response) { Continued++; return Refusal; }
            public void Pause() { IsPaused = true; Pauses++; }
            public void Resume() { IsPaused = false; Resumes++; }
            public void RestartAttemptClock() => ClockRestarts++;
            public void Log(string line) => Lines.Add(line);
            public void Exit(int code, string reason) { ExitCode = code; ExitReason = reason; }
        }

        static readonly DateTime Utc = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        const string ReadyJson = "{\"processSessionId\":\"x\",\"layoutHash\":\"0123456789abcdef\"}";
        static readonly byte[] Manifest = Encoding.UTF8.GetBytes("{\"manifestVersion\":1,\"r\":1}");

        string dir;
        FakeHttpTransport transport;
        FakeHost host;
        AttemptStart attempt;
        OutboxWorker worker;
        BackendAttemptRunner runner;
        double now;
        BackendTimings timings;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "bg-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            transport = new FakeHttpTransport(BackendTestKit.Prefix);
            host = new FakeHost();
            attempt = BackendTestKit.Start();
            now = 100;
            timings = new BackendTimings();
        }

        [TearDown]
        public void TearDown()
        {
            worker?.Stop(TimeSpan.FromSeconds(5));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        string RegisterAnswer(string launch = "2026-10-23T12:02:00Z")
        {
            var response = AttemptTestData.Response(AttemptTestData.LedgerSnapshot("01-owned-swap-and-use.json"));
            response.deadlines.launch = launch;
            return JsonUtility.ToJson(response);
        }

        static string ReadyAnswer(string joinDeadline = "2026-10-23T12:02:00Z") => "{\"joinDeadline\":\"" + joinDeadline + "\"}";

        void Make()
        {
            worker = new OutboxWorker(OutboxDirectories.OpenForEditor(dir));
            runner = new BackendAttemptRunner(host, transport, BackendTestKit.Api(transport), worker, timings, "editor", AttemptTestData.GoldenCatalogueVersion, () => Utc, now);
        }

        void Run(double seconds, Func<bool> until = null)
        {
            var end = now + seconds;
            while (now < end && !runner.HasExited && (until == null || !until()))
            {
                now += 0.05;
                System.Threading.Thread.Sleep(1);
                runner.Tick(now);
            }
        }

        // Registered, generated (the host paused it), ready acknowledged: holding.
        void ToHolding()
        {
            transport.Answer("sessions", 200, RegisterAnswer()).Answer("ready", 200, ReadyAnswer()).AnswerAlways("heartbeat", 200, "{}");
            Make();
            Run(1, () => runner.Phase == RunnerPhase.Generating);
            Assert.That(runner.Phase, Is.EqualTo(RunnerPhase.Generating), host.ExitReason);
            host.Pause();
            runner.MissionReady(attempt, ReadyJson, now);
            Run(1, () => runner.Phase == RunnerPhase.Holding);
            Assert.That(runner.Phase, Is.EqualTo(RunnerPhase.Holding), host.ExitReason);
        }

        void ToRunning()
        {
            ToHolding();
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).Answer("start", 200, BackendTestKit.Started);
            Assert.That(runner.AdmitTicket(BackendTestKit.Ticket).State, Is.EqualTo(TicketAdmissionState.Pending));
            Run(1, () => runner.Phase == RunnerPhase.Running);
            Assert.That(runner.Phase, Is.EqualTo(RunnerPhase.Running));
        }

        static string HashOf(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return "sha256:" + BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        static ManifestBuildOutcome Outcome() =>
            new ManifestBuildOutcome(ManifestBuildKind.Normal, new BuiltManifest(Manifest, HashOf(Manifest), null), new string[0]);

        string[] FailedReasons() => transport.To("failed").Select(r => JsonUtility.FromJson<FailedRequest>(r.BodyText).reason).ToArray();

        [Test]
        public void A_whole_run_uses_exactly_the_allowed_routes_and_exits_0_after_publication()
        {
            ToRunning();
            Assert.That(host.Resumes, Is.EqualTo(1));
            Assert.That(host.ClockRestarts, Is.EqualTo(1));
            var saved = 0;
            runner.ResultSaved += _ => saved++;
            transport.Answer("recorded", 200, "{\"state\":\"result_pending\"}");
            Run(15); // heartbeats while running
            runner.Frozen(Outcome(), now);
            Run(10);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.Success), host.ExitReason);
            Assert.That(saved, Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(Path.Combine(dir, attempt.AccountId + ".manifest.ready")), Is.EqualTo(Manifest));
            var routes = transport.Routes.Where(r => r != "heartbeat").ToArray();
            Assert.That(routes, Is.EqualTo(new[] { "sessions", "ready", "tickets/redeem", "start", "recorded" }));
            Assert.That(transport.Routes.Count(r => r == "heartbeat"), Is.GreaterThanOrEqualTo(1));
            Assert.That(transport.Routes.Distinct().Except(new[] { "sessions", "ready", "tickets/redeem", "start", "recorded", "heartbeat" }), Is.Empty);
        }

        [Test]
        public void The_ready_body_is_the_given_bytes_identical_on_every_retry()
        {
            transport.Answer("sessions", 200, RegisterAnswer()).Drop("ready").Answer("ready", 200, ReadyAnswer()).AnswerAlways("heartbeat", 200, "{}");
            Make();
            Run(1, () => runner.Phase == RunnerPhase.Generating);
            host.Pause();
            runner.MissionReady(attempt, ReadyJson, now);
            Run(20, () => runner.Phase == RunnerPhase.Holding);
            Assert.That(runner.Phase, Is.EqualTo(RunnerPhase.Holding));
            Assert.That(transport.To("ready").Select(r => r.BodyText), Is.EqualTo(new[] { ReadyJson, ReadyJson }));
        }

        [Test]
        public void An_invalid_pool_exits_4_with_no_failed_report_and_no_retry()
        {
            transport.Answer("sessions", 200, RegisterAnswer()).Answer("ready", 422, BackendTestKit.Problem("InvalidLootPool"));
            Make();
            Run(1, () => runner.Phase == RunnerPhase.Generating);
            host.Pause();
            runner.MissionReady(attempt, ReadyJson, now);
            Run(5);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.BackendRefused));
            Assert.That(transport.To("failed"), Is.Empty);
            Assert.That(transport.To("ready").Count(), Is.EqualTo(1));
        }

        [TestCase(422, "InvalidObjectives")]
        [TestCase(409, "CatalogueMismatch")]
        [TestCase(409, "InvalidState")]
        [TestCase(409, "WrongSession")]
        public void Another_ready_refusal_reports_failed_then_exits_4(int status, string code)
        {
            transport.Answer("sessions", 200, RegisterAnswer()).Answer("ready", status, BackendTestKit.Problem(code)).AnswerAlways("failed", 200, "{\"applied\":\"cancelled\"}");
            Make();
            Run(1, () => runner.Phase == RunnerPhase.Generating);
            host.Pause();
            runner.MissionReady(attempt, ReadyJson, now);
            Run(5);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.BackendRefused));
            Assert.That(FailedReasons(), Is.EqualTo(new[] { "ready_refused" }));
            Assert.That(host.ExitReason, Does.Contain(code));
        }

        [Test]
        public void The_ready_bound_reports_failed_then_exits_4()
        {
            transport.Answer("sessions", 200, RegisterAnswer()).RefuseAlways("ready").AnswerAlways("failed", 200, "{}");
            Make();
            Run(1, () => runner.Phase == RunnerPhase.Generating);
            host.Pause();
            runner.MissionReady(attempt, ReadyJson, now);
            Run(40);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.BackendRefused));
            Assert.That(FailedReasons(), Is.EqualTo(new[] { "ready_gave_up" }));
        }

        [Test]
        public void The_ready_bound_never_passes_the_registered_launch_deadline()
        {
            transport.Answer("sessions", 200, RegisterAnswer(launch: "2026-10-23T12:00:10Z")).RefuseAlways("ready").AnswerAlways("failed", 200, "{}");
            Make();
            Run(1, () => runner.Phase == RunnerPhase.Generating);
            host.Pause();
            runner.MissionReady(attempt, ReadyJson, now);
            Run(40);
            Assert.That(transport.To("ready").Last().At, Is.LessThan(100 + 10.5), "the launch deadline (10 s after registration) bounds the ready retries");
            Assert.That(FailedReasons(), Is.EqualTo(new[] { "ready_gave_up" }));
        }

        [Test]
        public void An_unparsable_join_deadline_never_holds()
        {
            transport.Answer("sessions", 200, RegisterAnswer()).Answer("ready", 200, ReadyAnswer("soon")).AnswerAlways("failed", 200, "{}");
            Make();
            Run(1, () => runner.Phase == RunnerPhase.Generating);
            host.Pause();
            runner.MissionReady(attempt, ReadyJson, now);
            Run(5);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.BackendRefused));
            Assert.That(FailedReasons(), Is.EqualTo(new[] { "ready_bad_answer" }));
        }

        [Test]
        public void A_ready_401_stops_with_exit_6()
        {
            transport.Answer("sessions", 200, RegisterAnswer()).Answer("ready", 401, "");
            Make();
            Run(1, () => runner.Phase == RunnerPhase.Generating);
            host.Pause();
            runner.MissionReady(attempt, ReadyJson, now);
            Run(5);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.Stopped));
            Assert.That(transport.To("failed"), Is.Empty);
        }

        [Test]
        public void A_registration_failure_exits_4_with_nothing_to_report_and_never_a_fixture()
        {
            transport.Answer("sessions", 409, BackendTestKit.Problem("BuildNotAllowed"));
            Make();
            Run(5);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.BackendRefused));
            Assert.That(host.ExitReason, Does.Contain("BuildNotAllowed"));
            Assert.That(host.Continued, Is.Zero, "nothing generated");
            Assert.That(transport.Routes, Is.EqualTo(new[] { "sessions" }));
        }

        [Test]
        public void A_refused_register_answer_reports_failed_then_exits_4()
        {
            host.Refusal = "attempt: unsupported_template";
            transport.Answer("sessions", 200, RegisterAnswer()).AnswerAlways("failed", 200, "{}");
            Make();
            Run(5);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.BackendRefused));
            Assert.That(FailedReasons(), Is.EqualTo(new[] { "attempt_refused" }));
            Assert.That(host.ExitReason, Does.Contain("unsupported_template"));
        }

        [TestCase("generation_failed")]
        [TestCase("generation_timeout")]
        [TestCase("session_refused")]
        public void A_startup_failure_reports_failed_once_then_exits_4(string reason)
        {
            transport.Answer("sessions", 200, RegisterAnswer()).AnswerAlways("failed", 200, "{}");
            Make();
            Run(1, () => runner.Phase == RunnerPhase.Generating);
            runner.StartupFailed(reason, now);
            runner.StartupFailed(reason, now);
            Run(5);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.BackendRefused));
            Assert.That(FailedReasons(), Is.EqualTo(new[] { reason }));
        }

        [Test]
        public void A_401_to_the_failed_report_never_changes_the_exit_code()
        {
            transport.Answer("sessions", 200, RegisterAnswer()).Answer("failed", 401, "");
            Make();
            Run(1, () => runner.Phase == RunnerPhase.Generating);
            runner.StartupFailed("generation_failed", now);
            Run(5);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.BackendRefused));
        }

        [Test]
        public void No_ticket_by_the_join_hold_reports_failed_and_exits_5()
        {
            ToHolding();
            transport.AnswerAlways("failed", 200, "{}");
            Run(160); // the join deadline (120 s) plus the 30 s grace
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.NotStarted));
            Assert.That(FailedReasons(), Is.EqualTo(new[] { "join_deadline" }));
            Assert.That(host.Resumes, Is.Zero, "the mission never ran");
        }

        // Final review M1 (re-graded): a backend join window longer than ten minutes (the backend allows up to 3600 s) is honoured,
        // never cut short by the process (owner ruling S13-D6: backend deadlines preserved).
        [Test]
        public void A_long_backend_join_window_is_never_cut_short()
        {
            transport.Answer("sessions", 200, RegisterAnswer(launch: "2026-10-23T13:00:00Z")).Answer("ready", 200, ReadyAnswer("2026-10-23T12:20:00Z"))
                .AnswerAlways("heartbeat", 200, "{}").AnswerAlways("failed", 200, "{}");
            Make();
            for (var i = 0; i < 100 && runner.Phase != RunnerPhase.Generating; i++) { now += 0.05; runner.Tick(now); }
            host.Pause();
            runner.MissionReady(attempt, ReadyJson, now);
            for (var i = 0; i < 100 && runner.Phase != RunnerPhase.Holding; i++) { now += 0.05; runner.Tick(now); }
            Assert.That(runner.Phase, Is.EqualTo(RunnerPhase.Holding));
            var holdingAt = now;
            while (now < holdingAt + 1200 + 25 && !runner.HasExited) { now += 1; runner.Tick(now); } // 20 minutes and most of the grace
            Assert.That(runner.HasExited, Is.False, "still holding inside the backend's 20-minute join window plus grace");
            while (now < holdingAt + 1200 + 40 && !runner.HasExited) { now += 1; runner.Tick(now); }
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.NotStarted));
        }

        [Test]
        public void A_refused_start_reports_failed_and_exits_5_without_ever_running()
        {
            ToHolding();
            transport.Answer("tickets/redeem", 200, BackendTestKit.RedeemAnswer(attempt)).Answer("start", 409, BackendTestKit.Problem("Cancelled")).AnswerAlways("failed", 200, "{}");
            runner.AdmitTicket(BackendTestKit.Ticket);
            Run(5);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.NotStarted));
            Assert.That(FailedReasons(), Is.EqualTo(new[] { "start_refused" }));
            Assert.That(host.Resumes, Is.Zero);
            Assert.That(host.IsPaused, Is.True);
        }

        [Test]
        public void A_bad_ticket_never_stops_the_mission()
        {
            ToHolding();
            transport.Answer("tickets/redeem", 401, "");
            Assert.That(runner.AdmitTicket(BackendTestKit.Ticket).State, Is.EqualTo(TicketAdmissionState.Pending));
            Run(5);
            Assert.That(host.ExitCode, Is.Null);
            Assert.That(runner.Phase, Is.EqualTo(RunnerPhase.Holding));
            Assert.That(runner.AdmitTicket("garbage").Refusal, Is.EqualTo(AdmissionRefusal.UnknownToken));
            Assert.That(runner.Phase, Is.EqualTo(RunnerPhase.Holding));
        }

        [Test]
        public void A_ticket_before_the_hold_is_refused()
        {
            transport.Answer("sessions", 200, RegisterAnswer());
            Make();
            Assert.That(runner.AdmitTicket(BackendTestKit.Ticket).Refusal, Is.EqualTo(AdmissionRefusal.AlreadyConnected));
            Assert.That(transport.To("tickets/redeem"), Is.Empty);
        }

        [Test]
        public void The_hold_is_restored_if_something_lifts_the_pause()
        {
            ToHolding();
            host.Resume(); // as MissionDirector.ResetSystems or TacticalPause.OnDisable would
            Run(0.2);
            Assert.That(host.IsPaused, Is.True);
            Assert.That(host.Lines.Any(l => l.Contains("paused again")), Is.True);
        }

        [Test]
        public void The_hold_is_restored_while_the_ready_report_is_in_flight()
        {
            transport.Answer("sessions", 200, RegisterAnswer()).SilentAlways("ready").AnswerAlways("heartbeat", 200, "{}");
            Make();
            Run(1, () => runner.Phase == RunnerPhase.Generating);
            host.Pause();
            runner.MissionReady(attempt, ReadyJson, now);
            Run(0.2);
            Assert.That(runner.Phase, Is.EqualTo(RunnerPhase.ReportingReady));
            host.Resume();
            Run(0.2);
            Assert.That(host.IsPaused, Is.True, "the hold covers the ready report too");
        }

        [Test]
        public void A_recorded_401_publishes_then_exits_6()
        {
            ToRunning();
            transport.Answer("recorded", 401, "");
            runner.Frozen(Outcome(), now);
            Run(10);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.Stopped), host.ExitReason);
            Assert.That(File.ReadAllBytes(Path.Combine(dir, attempt.AccountId + ".manifest.ready")), Is.EqualTo(Manifest));
        }

        [Test]
        public void A_heartbeat_401_while_running_stops_with_exit_6_and_no_outcome()
        {
            ToRunning();
            transport.Clear("heartbeat").AnswerAlways("heartbeat", 401, "");
            Run(15);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.Stopped));
            Assert.That(transport.To("recorded"), Is.Empty);
            Assert.That(transport.To("failed"), Is.Empty);
            Assert.That(Directory.GetFiles(dir), Is.Empty, "no outcome is fabricated");
        }

        [Test]
        public void A_heartbeat_401_while_notifying_still_publishes_then_exits_6()
        {
            ToRunning();
            transport.Clear("heartbeat").AnswerAlways("heartbeat", 401, "").SilentAlways("recorded");
            runner.Frozen(Outcome(), now);
            Run(60);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.Stopped));
            Assert.That(File.ReadAllBytes(Path.Combine(dir, attempt.AccountId + ".manifest.ready")), Is.EqualTo(Manifest), "the durable result is still published");
        }

        [Test]
        public void An_outage_after_the_start_still_publishes_and_exits_0()
        {
            ToRunning();
            transport.Clear("heartbeat").RefuseAlways("heartbeat").RefuseAlways("recorded");
            Run(40);
            Assert.That(runner.BackendReachable, Is.False);
            runner.Frozen(Outcome(), now);
            Run(40);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.Success), host.ExitReason);
            Assert.That(File.ReadAllBytes(Path.Combine(dir, attempt.AccountId + ".manifest.ready")), Is.EqualTo(Manifest));
        }

        [Test]
        public void A_failed_manifest_reports_failed_and_exits_7_with_no_file()
        {
            ToRunning();
            transport.AnswerAlways("failed", 200, "{\"applied\":\"none\"}");
            runner.Frozen(ManifestBuildOutcome.CallerError(), now);
            Run(5);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.ResultNotPublished));
            Assert.That(FailedReasons(), Is.EqualTo(new[] { "manifest_build_failed" }));
            Assert.That(Directory.GetFiles(dir), Is.Empty);
        }

        [Test]
        public void A_conflicting_result_reports_failed_and_exits_7_without_overwriting()
        {
            ToRunning();
            File.WriteAllText(Path.Combine(dir, attempt.AccountId + ".manifest.written"), "{\"other\":1}");
            transport.AnswerAlways("failed", 200, "{}");
            runner.Frozen(Outcome(), now);
            Run(5);
            Assert.That(host.ExitCode, Is.EqualTo(ServerExitCodes.ResultNotPublished));
            Assert.That(FailedReasons(), Is.EqualTo(new[] { "outbox_conflict" }));
            Assert.That(File.ReadAllText(Path.Combine(dir, attempt.AccountId + ".manifest.written")), Is.EqualTo("{\"other\":1}"));
        }

        [Test]
        public void Shutdown_while_notifying_publishes_on_the_drain_and_sends_nothing_new()
        {
            ToRunning();
            transport.SilentAlways("recorded");
            runner.Frozen(Outcome(), now);
            Run(3, () => runner.Publisher.State == PublisherState.Notifying);
            var sent = transport.Requests.Count;
            runner.Shutdown();
            Assert.That(File.ReadAllBytes(Path.Combine(dir, attempt.AccountId + ".manifest.ready")), Is.EqualTo(Manifest));
            Run(5);
            Assert.That(transport.Requests.Count, Is.EqualTo(sent));
            Assert.That(host.ExitCode, Is.Null, "a quit is not an exit decision");
        }

        [Test]
        public void Shutdown_while_holding_leaves_no_outbox_file()
        {
            ToHolding();
            runner.Shutdown();
            Assert.That(Directory.GetFiles(dir), Is.Empty);
        }
    }
}
