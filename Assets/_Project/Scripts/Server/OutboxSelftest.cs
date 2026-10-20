using System;
using System.Collections.Generic;
using System.Text;
using Blackglass.Backend.Core;

namespace Blackglass
{
    /// <summary>
    /// The -outboxSelftest diagnostic (Stage 12 plan, Task 1): Stage 11's native outbox worker in Unity's Linux Mono player, with
    /// no mission, no network and no credential. It publishes fixed bytes (manifest-v1's 01-minimal vector, and a variant for
    /// the conflict checks) through fresh OutboxWorkers over the checked $STATE_DIRECTORY, one worker per check so every check
    /// after the first meets the files on disk, never the previous worker's memory. Phases:
    ///   publish  a fresh directory: A is written then published; a new worker finds A already published; B is a conflict.
    ///   hold     a fresh directory: A is written and held, never published; the operator kills the process.
    ///   resume   the directory hold left: B is a conflict; A (already written) is published.
    /// The simulation thread only submits and polls: Step is called once per frame and never waits for the disk; the longest
    /// frame interval while a job is in flight is checked against MaxFrameMs. The files themselves are checked by the
    /// operator as root (the host manifest), not by this process.
    /// </summary>
    public sealed class OutboxSelftest
    {
        public const string Prefix = "[Selftest] ";
        public const string Account = "JG";
        public const double MaxFrameMs = 250;
        public const double StepTimeoutSeconds = 30;

        /// <summary>Backend/contracts/manifest-v1/valid/01-minimal.canonical.json, byte for byte (SHA-256 0c66150575e4fd8efe83d199f4ba056183c3363609ad835d7ef02c94ddddd122).</summary>
        public const string ResultA =
            "{\"accountId\":\"JG\",\"attemptId\":\"0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41\",\"buildRevision\":\"r1\",\"catalogueVersion\":\"sha256:5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c5f2c\",\"formatVersion\":1,\"integrity\":\"ok\",\"layoutHash\":\"cf2e14dece9cc2a6\",\"lootHash\":\"sha256:a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2a1b2\",\"lootTableVersion\":1,\"objectives\":[],\"operatives\":[{\"alive\":true,\"collected\":[],\"consumed\":[],\"equipment\":[],\"held\":[],\"operativeId\":\"C\",\"slot\":0}],\"outcome\":\"success\",\"outcomeSequence\":8812,\"processSessionId\":\"0192f1a5-0000-7000-8000-000000000001\",\"serverElapsedMs\":1200000}";

        /// <summary>A different valid result for the same participant: A with outcomeSequence 8813.</summary>
        public static readonly string ResultB = ResultA.Replace("\"outcomeSequence\":8812", "\"outcomeSequence\":8813");

        readonly string phase;
        readonly Func<IOutboxDirectory> open;
        readonly Action<string> log;
        readonly IEnumerator<bool> steps;
        int checks;
        bool failed;
        double longestFrameMs;
        double? lastFrame;
        OutboxWorker live;

        /// <param name="open">Opens the checked outbox directory, or throws (OutboxIOException, PlatformNotSupportedException).</param>
        public OutboxSelftest(string phase, Func<IOutboxDirectory> open, Action<string> log)
        {
            this.phase = phase;
            this.open = open ?? throw new ArgumentNullException(nameof(open));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            steps = Run();
        }

        public static bool IsPhase(string value) => value == "publish" || value == "hold" || value == "resume";

        public bool IsFinished { get; private set; }
        public bool Passed => IsFinished && !failed;
        /// <summary>The bootstrap's exit code once finished: 0 pass, 3 fail.</summary>
        public int ExitCode => Passed ? 0 : 3;
        /// <summary>True once the hold phase has a durable result and is waiting to be killed.</summary>
        public bool Holding { get; private set; }
        public double LongestFrameMs => longestFrameMs;
        /// <summary>The final line, once finished.</summary>
        public string Summary { get; private set; }

        /// <summary>One frame: advances at most one state, never waits. <paramref name="now"/> is unscaled seconds.</summary>
        public void Step(double now)
        {
            if (IsFinished) return;
            if (lastFrame.HasValue && busy) longestFrameMs = Math.Max(longestFrameMs, (now - lastFrame.Value) * 1000);
            lastFrame = now;
            clock = now;
            if (!steps.MoveNext() || IsFinished)
                Finish();
        }

        double clock;
        bool busy;

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
            if (lastFrame.HasValue) Check("frame_loop", longestFrameMs <= MaxFrameMs, FormattableString.Invariant($"maxFrameMs={longestFrameMs:0}"));
            IsFinished = true;
            Summary = Prefix + FormattableString.Invariant($"result={(failed ? "fail" : "pass")} phase={phase} checks={checks} maxFrameMs={longestFrameMs:0}");
            log(Summary);
        }

        IEnumerator<bool> Run()
        {
            if (!IsPhase(phase))
            {
                Check("phase", false, "unknown phase '" + phase + "'");
                yield break;
            }
            var a = Bytes(ResultA);
            var b = Bytes(ResultB);
            if (phase == "publish")
            {
                foreach (var s in Publication("publish_a", a, PublicationStage.Published, true)) yield return s;
                if (failed) yield break;
                foreach (var s in Publication("republish_a", a, PublicationStage.Published, false)) yield return s;
                if (failed) yield break;
                foreach (var s in Publication("conflict_b", b, PublicationStage.Conflict, false)) yield return s;
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
        }

        /// <summary>
        /// One check on a fresh worker: submit, poll until <paramref name="expected"/> (allowing publication once Written when
        /// <paramref name="allow"/>), then stop the worker and poll until its thread has ended (it closes the directory). The hold
        /// check keeps its worker running.
        /// </summary>
        IEnumerable<bool> Publication(string name, byte[] bytes, PublicationStage expected, bool allow)
        {
            IOutboxDirectory directory;
            try { directory = open(); }
            catch (Exception e) when (e is OutboxIOException || e is PlatformNotSupportedException || e is UnauthorizedAccessException || e is System.IO.IOException)
            {
                Check("location", false, e is OutboxIOException io ? io.Code : e.GetType().Name);
                yield break;
            }
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
