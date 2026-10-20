using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Blackglass.Backend.Core;

namespace Blackglass
{
    /// <summary>
    /// The -outboxSelftest diagnostic (Stage 12 plan, Task 1): Stage 11's native outbox worker in Unity's Linux Mono player, with
    /// no mission, no network and no credential. It publishes fixed bytes (manifest-v1's 01-minimal vector, and a variant for
    /// the conflict checks) through fresh OutboxWorkers over the checked $STATE_DIRECTORY, one worker per check so every check
    /// after the first meets the files on disk, never the previous worker's memory. Phases:
    ///   publish  a fresh directory: A is written then published; a new worker finds A already published; B is a conflict;
    ///            then, on the main thread, a fresh .tmp renamed onto the .ready must be refused (RENAME_NOREPLACE, EEXIST).
    ///   hold     a fresh directory: A is written and held, never published; the operator kills the process.
    ///   resume   the directory hold left: B is a conflict; A (already written) is published.
    /// Every phase first refuses to run as root. The simulation thread only submits and polls: Step never waits for the disk.
    /// Two bounds are checked: the time spent inside Step (main_thread, excluding the checks that open the directory or do
    /// I/O by design) and the longest frame interval while a job is in flight (frame_loop). A phase passes only when it ran
    /// to its end with exactly its checks and none failed; any exception fails it. The files themselves are checked by the
    /// operator as root (the host manifest), not by this process.
    /// </summary>
    public sealed class OutboxSelftest
    {
        public const string Prefix = "[Selftest] ";
        public const string Account = "JG";
        public const double MaxFrameMs = 250;
        public const double MaxStepMs = 50;
        public const double StepTimeoutSeconds = 30;
        const int Eexist = 17;

        /// <summary>Backend/contracts/manifest-v1/valid/01-minimal.canonical.json, byte for byte (SHA-256 0c66150575e4fd8efe83d199f4ba056183c3363609ad835d7ef02c94ddddd122).</summary>
        public const string ResultA =
            "{\"accountId\":\"JG\",\"attemptId\":\"0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41\",\"buildRevision\":\"r1\",\"catalogueVersion\":\"sha256:5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c\",\"formatVersion\":1,\"integrity\":\"ok\",\"layoutHash\":\"cf2e14dece9cc2a6\",\"lootHash\":\"sha256:a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2\",\"lootTableVersion\":1,\"objectives\":[],\"operatives\":[{\"alive\":true,\"collected\":[],\"consumed\":[],\"equipment\":[],\"held\":[],\"operativeId\":\"C\",\"slot\":0}],\"outcome\":\"success\",\"outcomeSequence\":8812,\"processSessionId\":\"0192f1a5-0000-7000-8000-000000000001\",\"serverElapsedMs\":1200000}";

        /// <summary>A different valid result for the same participant: A with outcomeSequence 8813.</summary>
        public static readonly string ResultB = ResultA.Replace("\"outcomeSequence\":8812", "\"outcomeSequence\":8813");

        /// <summary>The checks a phase must log, frame_loop and main_thread included; hold never finishes on its own.</summary>
        public static int ExpectedChecks(string phase) => phase == "publish" ? 7 : phase == "resume" ? 5 : -1;

        readonly string phase;
        readonly Func<IOutboxDirectory> open;
        readonly Func<long> euid;
        readonly Action<string> log;
        readonly IEnumerator<bool> steps;
        int checks;
        bool failed;
        bool completed;
        double longestFrameMs;
        double longestStepMs;
        double? lastFrame;
        double clock;
        bool busy;
        bool ioThisStep;
        OutboxWorker live;

        /// <param name="open">Opens the checked outbox directory, or throws.</param>
        /// <param name="euid">The process's effective uid (the self-test refuses 0), or throws.</param>
        public OutboxSelftest(string phase, Func<IOutboxDirectory> open, Func<long> euid, Action<string> log)
        {
            this.phase = phase ?? string.Empty;
            this.open = open ?? throw new ArgumentNullException(nameof(open));
            this.euid = euid ?? throw new ArgumentNullException(nameof(euid));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            steps = Run();
        }

        public static bool IsPhase(string value) => value == "publish" || value == "hold" || value == "resume";

        public bool IsFinished { get; private set; }
        public bool Passed { get; private set; }
        /// <summary>The bootstrap's exit code once finished: 0 pass, 3 fail.</summary>
        public int ExitCode => Passed ? 0 : 3;
        /// <summary>True once the hold phase has a durable result and is waiting to be killed.</summary>
        public bool Holding { get; private set; }
        /// <summary>The final line, once finished.</summary>
        public string Summary { get; private set; }

        /// <summary>One frame: advances at most one state, never waits. <paramref name="now"/> is unscaled seconds.</summary>
        public void Step(double now)
        {
            if (IsFinished) return;
            if (lastFrame.HasValue && busy) longestFrameMs = Math.Max(longestFrameMs, (now - lastFrame.Value) * 1000);
            lastFrame = now;
            clock = now;
            ioThisStep = false;
            var watch = Stopwatch.StartNew();
            bool more;
            try { more = steps.MoveNext(); }
            catch (Exception e)
            {
                // An OutboxIOException names its bounded code (a refused location, a failed native call); anything else (a
                // DllNotFoundException from Mono, say) only its type: never a path or content.
                Check("exception", false, e is OutboxIOException io
                    ? FormattableString.Invariant($"OutboxIOException code={io.Code} errno={io.Errno}")
                    : e.GetType().Name);
                more = false;
            }
            watch.Stop();
            if (!ioThisStep) longestStepMs = Math.Max(longestStepMs, watch.Elapsed.TotalMilliseconds);
            if (!more) Finish();
        }

        /// <summary>Tests and a clean quit: stops a held worker (its job isn't allowed, so nothing more is written). A killed
        /// process never gets here, which is the point of the hold phase.</summary>
        internal void StopWorker() => live?.Stop(TimeSpan.Zero);

        static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        void Check(string name, bool ok, string detail = "")
        {
            checks++;
            if (!ok) failed = true;
            log(Prefix + name + (ok ? " ok" : " FAIL") + (detail.Length > 0 ? " " + detail : ""));
        }

        void Finish()
        {
            if (IsFinished) return;
            if (completed)
            {
                Check("main_thread", longestStepMs <= MaxStepMs, FormattableString.Invariant($"maxStepMs={longestStepMs:0}"));
                Check("frame_loop", longestFrameMs <= MaxFrameMs, FormattableString.Invariant($"maxFrameMs={longestFrameMs:0}"));
            }
            IsFinished = true;
            Passed = completed && !failed && checks == ExpectedChecks(phase);
            Summary = Prefix + FormattableString.Invariant(
                $"result={(Passed ? "pass" : "fail")} phase={(phase.Length > 0 ? phase : "-")} checks={checks} maxStepMs={longestStepMs:0} maxFrameMs={longestFrameMs:0}");
            log(Summary);
        }

        IEnumerator<bool> Run()
        {
            if (!IsPhase(phase))
            {
                Check("phase", false, "unknown phase '" + phase + "'");
                yield break;
            }
            var uid = euid();
            Check("uid", uid != 0, uid == 0 ? "running_as_root" : FormattableString.Invariant($"euid={uid}"));
            if (failed) yield break;
            var a = Bytes(ResultA);
            var b = Bytes(ResultB);
            if (phase == "publish")
            {
                foreach (var s in Publication("publish_a", a, PublicationStage.Published, true)) yield return s;
                if (failed) yield break;
                foreach (var s in Publication("republish_a", a, PublicationStage.Published, false)) yield return s;
                if (failed) yield break;
                foreach (var s in Publication("conflict_b", b, PublicationStage.Conflict, false)) yield return s;
                if (failed) yield break;
                NoReplace(b);
            }
            else if (phase == "hold")
            {
                foreach (var s in Publication("hold_a", a, PublicationStage.Written, false)) yield return s;
                if (failed) yield break;
                Holding = true;
                log(Prefix + "held");
                while (true) yield return true; // until the operator kills the process
            }
            else
            {
                foreach (var s in Publication("conflict_b", b, PublicationStage.Conflict, false)) yield return s;
                if (failed) yield break;
                foreach (var s in Publication("resume_a", a, PublicationStage.Published, true)) yield return s;
            }
            if (!failed) completed = true;
        }

        /// <summary>
        /// The native no-replace rename (renameat2 with RENAME_NOREPLACE), which the writer's own checks never reach: with no
        /// worker alive, a fresh .tmp holding B is renamed onto the published .ready, and must be refused with EEXIST. The .tmp
        /// stays behind (the writer rewrites a leftover .tmp, never promotes it); the .ready keeps A.
        /// </summary>
        void NoReplace(byte[] b)
        {
            ioThisStep = true;
            using (var directory = open())
            {
                directory.WriteAndSync(OutboxFileNames.Tmp(Account), b);
                try
                {
                    directory.RenameNoReplace(OutboxFileNames.Tmp(Account), OutboxFileNames.Ready(Account));
                    Check("noreplace", false, "the rename replaced the published result");
                }
                catch (OutboxIOException e)
                {
                    Check("noreplace", e.Code == "rename_failed" && e.Errno == Eexist, FormattableString.Invariant($"code={e.Code} errno={e.Errno}"));
                }
            }
        }

        /// <summary>
        /// One check on a fresh worker: submit, poll until <paramref name="expected"/> (allowing publication once Written when
        /// <paramref name="allow"/>), then stop the worker and poll until its thread has ended (it closes the directory). The hold
        /// check keeps its worker running.
        /// </summary>
        IEnumerable<bool> Publication(string name, byte[] bytes, PublicationStage expected, bool allow)
        {
            ioThisStep = true; // the directory open is main-thread I/O by design (Stage 11: once, before the loop)
            var directory = open();
            var worker = new OutboxWorker(directory);
            live = worker;
            var handle = worker.Submit(Account, bytes, ManifestCodec.HashOf(bytes));
            var deadline = clock + StepTimeoutSeconds;
            busy = true;
            PublicationStatus status;
            while (true)
            {
                status = handle.Status;
                if (allow && status.Stage == PublicationStage.Written) handle.AllowPublication();
                if (status.Stage == expected || IsTerminal(status.Stage) || clock > deadline) break;
                if (status.Stage == PublicationStage.Written && !allow) break; // written where a conflict was expected: final
                yield return true;
            }
            busy = false;
            Check(name, status.Stage == expected && (expected == PublicationStage.Conflict || status.Durable),
                FormattableString.Invariant($"stage={status.Stage} durable={status.Durable} failure={status.FailureCode ?? "-"} errno={status.Errno}"));
            if (expected == PublicationStage.Written && !failed) yield break; // hold: the worker stays up until the kill
            worker.Stop(TimeSpan.Zero);
            deadline = clock + StepTimeoutSeconds;
            while (!worker.Stop(TimeSpan.Zero))
            {
                if (clock > deadline) { Check(name + "_stop", false, "the worker thread did not end"); yield break; }
                yield return true;
            }
            live = null;
        }

        static bool IsTerminal(PublicationStage stage) =>
            stage == PublicationStage.Published || stage == PublicationStage.Conflict || stage == PublicationStage.Failed || stage == PublicationStage.Cancelled;
    }
}
