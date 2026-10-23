using System;
using System.Text;
using Blackglass.Backend.Adapter;
using Blackglass.Backend.Core;

namespace Blackglass
{
    public enum RunnerPhase { Registering, Generating, ReportingReady, Holding, Running, Publishing, Exiting }

    /// <summary>What the runner needs from the server (ServerBootstrap in production, a fake in tests).</summary>
    internal interface IBackendAttemptHost
    {
        /// <summary>Validates the register answer and starts generation; null, or the refusal code (nothing generated).</summary>
        string ContinueAttempt(string attemptId, SessionRegisterResponse response);
        bool IsPaused { get; }
        void Pause();
        void Resume();
        /// <summary>serverElapsedMs counts from the committed start, not from the hold.</summary>
        void RestartAttemptClock();
        void Log(string line);
        void Exit(int code, string reason);
    }

    /// <summary>
    /// The backend attempt's lifecycle on the mission process (Stage 13, plan §3): registration, generation, the ready report, the
    /// start hold (the mission paused until the backend commits the start), play, the freeze, publication through the outbox with the
    /// bounded recorded notice, and exit. It owns the exit decision and its code (ServerExitCodes 0 and 4–7), the failed reports, and
    /// heartbeats from registration on. It never polls settlement and never computes a reward. Holds no game rule: the host generates,
    /// pauses and resumes; the session freezes the outcome. Main thread only; every wait is a poll from Tick.
    /// </summary>
    public sealed class BackendAttemptRunner
    {
        readonly IBackendAttemptHost host;
        readonly IHttpTransport transport;
        readonly ProcessApi api;
        readonly OutboxWorker worker;
        readonly BackendTimings timings;
        readonly Func<DateTime> utcNow;
        readonly string attemptId;
        readonly BackendAttemptSource registration;
        SessionRegisterResponse registered;
        AttemptStart attempt;
        ProcessHeartbeat heartbeat;
        ParticipantStartHold hold;
        ResultPublisher publisher;
        BackendCall ready, failed;
        byte[] readyBytes;
        double lastNow, launchDeadline = double.PositiveInfinity, startDeadline;
        bool unauthorizedBeforePublished;
        int exitCode;
        string exitReason;

        internal BackendAttemptRunner(IBackendAttemptHost host, IHttpTransport transport, ProcessApi api, OutboxWorker worker, BackendTimings timings,
            string buildRevision, string catalogueVersion, Func<DateTime> utcNow, double now)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.worker = worker ?? throw new ArgumentNullException(nameof(worker));
            this.timings = timings ?? throw new ArgumentNullException(nameof(timings));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            attemptId = api.AttemptId;
            lastNow = now;
            registration = new BackendAttemptSource(api, buildRevision, catalogueVersion, timings.RegisterBoundSeconds, now);
        }

        public RunnerPhase Phase { get; private set; } = RunnerPhase.Registering;
        public bool BackendReachable => heartbeat != null && heartbeat.BackendReachable;
        public bool HasExited { get; private set; }
        /// <summary>The participant's result is durable and published for relay (.ready). Stage 14 tells the client.</summary>
        public event Action<string> ResultSaved;
        internal ParticipantStartHold Hold => hold;
        internal ResultPublisher Publisher => publisher;

        /// <summary>
        /// A participant's join ticket (Stage 14's NetTicketAdmission calls this from the hello; Stage 13's end-to-end test with a real
        /// ticket). Refused(AlreadyConnected) unless the mission is holding for its start.
        /// </summary>
        public TicketAdmission AdmitTicket(string ticket) =>
            Phase == RunnerPhase.Holding && hold != null ? hold.AdmitTicket(ticket, lastNow) : TicketAdmission.Refused(AdmissionRefusal.AlreadyConnected);

        internal void Tick(double now)
        {
            lastNow = now;
            if (HasExited) return;
            transport.Tick(now);
            heartbeat?.Tick(now);
            switch (Phase)
            {
                case RunnerPhase.Registering: Registering(now); break;
                case RunnerPhase.ReportingReady: ReportingReady(now); break;
                case RunnerPhase.Holding: Holding(now); break;
                case RunnerPhase.Publishing: Publishing(now); break;
                case RunnerPhase.Exiting: Exiting(now); return;
            }
            if (heartbeat != null && heartbeat.Unauthorized && Phase != RunnerPhase.Exiting) Unauthorized(now);
        }

        /// <summary>The ready mission and its session (the host paused it in the same frame, before any simulation).</summary>
        internal void MissionReady(AttemptStart start, string readyJson, double now)
        {
            if (Phase != RunnerPhase.Generating) return;
            attempt = start;
            readyBytes = Encoding.UTF8.GetBytes(readyJson);
            ready = api.Ready(readyBytes, Math.Min(now + timings.ReadyBoundSeconds, launchDeadline), now);
            Phase = RunnerPhase.ReportingReady;
        }

        /// <summary>Generation failed, timed out, or the session refused the ready mission: pre-start, so the backend is told.</summary>
        internal void StartupFailed(string reason, double now, string detail = null)
        {
            if (Phase == RunnerPhase.Registering || Phase == RunnerPhase.Generating || Phase == RunnerPhase.ReportingReady)
                FailAndExit(ServerExitCodes.BackendRefused, reason, now, detail);
        }

        /// <summary>The participant's outcome froze (the first terminal event).</summary>
        internal void Frozen(ManifestBuildOutcome outcome, double now)
        {
            if (Phase != RunnerPhase.Running) return;
            if (outcome == null || outcome.Manifest == null)
            {
                FailAndExit(ServerExitCodes.ResultNotPublished, "manifest_build_failed", now);
                return;
            }
            publisher = new ResultPublisher(worker, api, timings);
            publisher.ResultSaved += account => ResultSaved?.Invoke(account);
            publisher.Begin(attempt.AccountId, outcome.Manifest.CanonicalBytes, outcome.Manifest.ManifestHash, now);
            Phase = RunnerPhase.Publishing;
        }

        /// <summary>The process is quitting (SIGTERM): end the notice, let a durable result publish, drain the outbox. No new request.</summary>
        internal void Shutdown()
        {
            if (HasExited) return;
            publisher?.Shutdown();
            heartbeat?.Stop();
            transport.AbortAll();
            worker.Stop(TimeSpan.FromSeconds(timings.DrainSeconds));
            HasExited = true;
        }

        string HeartbeatPhase()
        {
            switch (Phase)
            {
                case RunnerPhase.Generating: return "generating";
                case RunnerPhase.ReportingReady: return "ready";
                case RunnerPhase.Holding: return "holding";
                case RunnerPhase.Running: return "running";
                case RunnerPhase.Publishing: return "publishing";
                default: return "exiting";
            }
        }

        void Registering(double now)
        {
            var status = registration.Poll(now, out var response, out var problem);
            if (status == RegistrationStatus.Pending) return;
            if (status == RegistrationStatus.Failed)
            {
                Exit(ServerExitCodes.BackendRefused, problem); // no session: nothing to report to
                return;
            }
            registered = response;
            ProcessApi.TryParseTimestamp(response.deadlines.launch, out var launchUtc);
            launchDeadline = now + Clamp((launchUtc - utcNow()).TotalSeconds, 0, timings.MaxJoinWindowSeconds);
            var account = response.participants[0].accountId;
            heartbeat = new ProcessHeartbeat(api, response.processSessionId, account, HeartbeatPhase, () => hold != null && hold.Connected, timings, host.Log, now);
            host.Log("registered attempt=" + attemptId + " session=" + response.processSessionId);
            Phase = RunnerPhase.Generating;
            var refusal = host.ContinueAttempt(attemptId, response);
            if (refusal != null) FailAndExit(ServerExitCodes.BackendRefused, "attempt_refused", now, refusal);
        }

        void ReportingReady(double now)
        {
            KeepHeld(); // the hold starts at the Ready frame, not at the acknowledgement
            ready.Tick(now);
            if (!ready.IsFinished) return;
            var call = ready;
            ready = null;
            switch (call.State)
            {
                case CallState.Succeeded:
                    if (!ProcessApi.TryReady(call, out var joinUtc, out _)) { FailAndExit(ServerExitCodes.BackendRefused, "ready_bad_answer", now); return; }
                    startDeadline = now + Clamp((joinUtc - utcNow()).TotalSeconds, 0, timings.MaxJoinWindowSeconds) + timings.StartGraceSeconds;
                    hold = new ParticipantStartHold(api, registered.processSessionId, attempt, timings, startDeadline);
                    hold.Started += () => { host.Resume(); host.RestartAttemptClock(); Phase = RunnerPhase.Running; host.Log("participant started: the mission runs"); };
                    Phase = RunnerPhase.Holding;
                    host.Log("ready acknowledged: holding for the participant's start");
                    return;
                case CallState.Unauthorized:
                    Unauthorized(now);
                    return;
                case CallState.Refused:
                    // An invalid pool cancelled the attempt already: nothing to report (any report would meet the uniform 401).
                    if (call.ProblemCode == "InvalidLootPool") { Exit(ServerExitCodes.BackendRefused, "ready_refused InvalidLootPool"); return; }
                    FailAndExit(ServerExitCodes.BackendRefused, "ready_refused", now, call.Status + " " + call.ProblemCode);
                    return;
                default:
                    FailAndExit(ServerExitCodes.BackendRefused, "ready_gave_up", now);
                    return;
            }
        }

        void KeepHeld()
        {
            if (host.IsPaused) return;
            host.Pause();
            host.Log("the hold was lifted by something else before the start commit: paused again");
        }

        void Holding(double now)
        {
            KeepHeld();
            hold.Tick(now);
            if (Phase != RunnerPhase.Holding) return; // Started moved it to Running
            if (hold.State == HoldState.Stopped) { Unauthorized(now); return; }
            if (hold.State == HoldState.CouldNotStart) { FailAndExit(ServerExitCodes.NotStarted, "start_refused", now, hold.Reason); return; }
            if (now > startDeadline && (hold.State == HoldState.Waiting || hold.State == HoldState.Redeeming))
                FailAndExit(ServerExitCodes.NotStarted, "join_deadline", now);
        }

        void Publishing(double now)
        {
            publisher.Tick(now);
            if (publisher.RecordedAnswer == "unauthorized") unauthorizedBeforePublished = true; // the notice met the credential's end
            if (publisher.State == PublisherState.Published)
                Exit(unauthorizedBeforePublished ? ServerExitCodes.Stopped : ServerExitCodes.Success, "result published" + (unauthorizedBeforePublished ? " after the credential ended" : string.Empty));
            else if (publisher.State == PublisherState.NotPublished)
                FailAndExit(ServerExitCodes.ResultNotPublished, publisher.Reason, now);
        }

        // A 401 on a session call: the credential ended (cancelled, settled or revoked). Stop without fabricating an outcome; a
        // publication in flight finishes first (its notice already counts the 401 as an answer).
        void Unauthorized(double now)
        {
            if (Phase == RunnerPhase.Publishing)
            {
                if (publisher.State != PublisherState.Published) unauthorizedBeforePublished = true;
                return;
            }
            Exit(ServerExitCodes.Stopped, "unauthorized: the process credential ended");
        }

        void FailAndExit(int code, string reason, double now, string detail = null)
        {
            if (Phase == RunnerPhase.Exiting || HasExited) return;
            exitCode = code;
            exitReason = reason + (detail != null ? " " + detail : string.Empty);
            if (registered != null)
                failed = api.Failed(registered.processSessionId, reason, now + timings.FailedReportBoundSeconds, now);
            Phase = RunnerPhase.Exiting;
            if (failed == null) Finish();
        }

        void Exiting(double now)
        {
            failed.Tick(now);
            if (failed.IsFinished) Finish(); // any answer, a 401 (the credential already ended) included, changes nothing
        }

        void Exit(int code, string reason)
        {
            exitCode = code;
            exitReason = reason;
            Phase = RunnerPhase.Exiting;
            Finish();
        }

        void Finish()
        {
            if (HasExited) return;
            HasExited = true;
            heartbeat?.Stop();
            transport.AbortAll();
            worker.Stop(TimeSpan.FromSeconds(timings.DrainSeconds));
            host.Exit(exitCode, exitReason);
        }

        static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
    }
}
