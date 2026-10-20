using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Blackglass.Backend.Core;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// The -outboxSelftest diagnostic (Stage 12 plan, Task 1) over the portable directory: the same phases the Linux Mono player
    /// runs on the mission VM with the native one. The VM run proves the libc calls; these prove the phases, the fail-closed
    /// rules and the routing.
    /// </summary>
    public sealed class OutboxSelftestTests
    {
        const long UnprivilegedUid = 61234;

        string dir;
        readonly List<string> lines = new List<string>();
        readonly List<OutboxSelftest> started = new List<OutboxSelftest>();

        static readonly string Tmp = OutboxFileNames.Tmp(OutboxSelftest.Account);
        static readonly string Ready = OutboxFileNames.Ready(OutboxSelftest.Account);
        static readonly string Written = OutboxFileNames.Written(OutboxSelftest.Account);

        static byte[] A => Encoding.UTF8.GetBytes(OutboxSelftest.ResultA);
        static byte[] B => Encoding.UTF8.GetBytes(OutboxSelftest.ResultB);

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "bg-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            lines.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var s in started) s.StopWorker();
            started.Clear();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        OutboxSelftest New(string phase, Func<IOutboxDirectory> open = null, Func<long> euid = null)
        {
            var s = new OutboxSelftest(phase, open ?? (() => OutboxDirectories.OpenForEditor(dir)), euid ?? (() => UnprivilegedUid), lines.Add);
            started.Add(s);
            return s;
        }

        // Steps one frame of 16 ms at a time (the worker runs on its own thread) until done, or the hold phase holds.
        static void Drive(OutboxSelftest s, double frameSeconds = 0.016)
        {
            var now = 0.0;
            var watch = Stopwatch.StartNew();
            while (!s.IsFinished && !s.Holding)
            {
                Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(20), "the self-test did not finish");
                s.Step(now);
                now += frameSeconds;
                Thread.Sleep(1);
            }
        }

        string[] Files() => Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        byte[] Read(string name) => File.ReadAllBytes(Path.Combine(dir, name));

        string Log => string.Join("\n", lines);

        [Test]
        public void ResultA_is_the_01_minimal_vector_and_ResultB_another_valid_manifest()
        {
            var vector = File.ReadAllBytes(Path.Combine(Application.dataPath, "..", "Backend", "contracts", "manifest-v1", "valid", "01-minimal.canonical.json"));
            Assert.That(A, Is.EqualTo(vector));
            Assert.That(B, Is.Not.EqualTo(A));
            Assert.That(ManifestCodec.Parse(B).Ok, Is.True, "B is a valid format-v1 manifest too");
        }

        [Test]
        public void Publish_passes_with_every_check_and_never_replaces_the_ready_result()
        {
            var s = New("publish");
            Drive(s);
            Assert.That(s.Passed, Is.True, Log);
            Assert.That(s.ExitCode, Is.EqualTo(0));
            Assert.That(lines, Has.Some.EqualTo("[Selftest] uid ok euid=61234"));
            Assert.That(lines, Has.Some.EqualTo("[Selftest] publish_a ok stage=Published durable=True failure=- errno=0"));
            Assert.That(lines, Has.Some.EqualTo("[Selftest] republish_a ok stage=Published durable=True failure=- errno=0"));
            Assert.That(lines, Has.Some.StartsWith("[Selftest] conflict_b ok stage=Conflict"));
            Assert.That(lines, Has.Some.EqualTo("[Selftest] noreplace ok code=rename_failed errno=17"));
            Assert.That(lines, Has.Some.StartsWith("[Selftest] main_thread ok maxStepMs="));
            Assert.That(lines, Has.Some.StartsWith("[Selftest] frame_loop ok maxFrameMs="));
            Assert.That(s.Summary, Does.StartWith("[Selftest] result=pass phase=publish checks=7 "));
            Assert.That(lines.Last(), Is.EqualTo(s.Summary));
            // the .ready still holds A; the no-replace check's .tmp (B) stays, never promoted
            Assert.That(Files(), Is.EqualTo(new[] { Tmp, Ready }.OrderBy(n => n, StringComparer.Ordinal).ToArray()));
            Assert.That(Read(Ready), Is.EqualTo(A));
            Assert.That(Read(Tmp), Is.EqualTo(B));
        }

        [Test]
        public void Hold_leaves_a_written_result_and_resume_publishes_it_once()
        {
            var hold = New("hold");
            Drive(hold);
            Assert.That(hold.Holding, Is.True, Log);
            Assert.That(hold.IsFinished, Is.False, "hold waits to be killed");
            Assert.That(lines.Last(), Is.EqualTo("[Selftest] held"));
            Assert.That(Files(), Is.EqualTo(new[] { Written }));
            Assert.That(Read(Written), Is.EqualTo(A));
            hold.StopWorker(); // stands in for the kill: the held job is never allowed, so nothing more happens

            var resume = New("resume");
            Drive(resume);
            Assert.That(resume.Passed, Is.True, Log);
            Assert.That(Files(), Is.EqualTo(new[] { Ready }));
            Assert.That(Read(Ready), Is.EqualTo(A), "the held bytes, not B");
            Assert.That(lines, Has.Some.StartsWith("[Selftest] conflict_b ok stage=Conflict"));
            Assert.That(resume.Summary, Does.StartWith("[Selftest] result=pass phase=resume checks=5 "));
        }

        [Test]
        public void Resume_without_a_held_result_fails()
        {
            var s = New("resume");
            Drive(s);
            Assert.That(s.Passed, Is.False);
            Assert.That(s.ExitCode, Is.EqualTo(3));
        }

        [Test]
        public void Running_as_root_is_refused_before_any_file_operation()
        {
            var opened = false;
            var s = New("publish", () => { opened = true; return OutboxDirectories.OpenForEditor(dir); }, () => 0);
            Drive(s);
            Assert.That(s.ExitCode, Is.EqualTo(3));
            Assert.That(lines, Has.Some.EqualTo("[Selftest] uid FAIL running_as_root"));
            Assert.That(opened, Is.False);
            Assert.That(Files(), Is.Empty);
        }

        [Test]
        public void A_native_library_failure_fails_the_phase_and_never_passes()
        {
            var s = New("publish", () => throw new DllNotFoundException("libc.so.6"));
            Drive(s);
            Assert.That(s.Passed, Is.False, Log);
            Assert.That(s.ExitCode, Is.EqualTo(3));
            Assert.That(lines, Has.Some.EqualTo("[Selftest] exception FAIL DllNotFoundException"));
            Assert.That(s.Summary, Does.StartWith("[Selftest] result=fail phase=publish "));
        }

        [Test]
        public void A_failing_uid_read_fails_the_phase()
        {
            var s = New("publish", euid: () => throw new EntryPointNotFoundException("geteuid"));
            Drive(s);
            Assert.That(s.ExitCode, Is.EqualTo(3));
            Assert.That(lines, Has.Some.EqualTo("[Selftest] exception FAIL EntryPointNotFoundException"));
        }

        [Test]
        public void A_long_frame_while_a_job_is_in_flight_fails_the_frame_check()
        {
            var s = New("publish");
            Drive(s, frameSeconds: 0.5);
            Assert.That(s.Passed, Is.False);
            Assert.That(lines, Has.Some.StartsWith("[Selftest] frame_loop FAIL maxFrameMs=500"));
        }

        [Test]
        public void A_refused_location_fails_with_its_code_and_writes_nothing()
        {
            var s = New("publish", () => throw new OutboxIOException("wrong_attempt", 0));
            Drive(s);
            Assert.That(s.ExitCode, Is.EqualTo(3));
            Assert.That(lines, Has.Some.EqualTo("[Selftest] exception FAIL OutboxIOException code=wrong_attempt errno=0"));
            Assert.That(Files(), Is.Empty);
        }

        [TestCase("crash")]
        [TestCase("")]
        public void An_unknown_or_missing_phase_fails(string phase)
        {
            var s = New(phase);
            Drive(s);
            Assert.That(s.ExitCode, Is.EqualTo(3));
            Assert.That(Files(), Is.Empty);
        }

        [Test]
        public void The_uid_reader_is_local_to_the_diagnostic_and_linux_x64_only()
        {
            // The Editor runs on Windows: the reader must refuse rather than guess (the self-test then fails, never passes).
            Assert.Throws<PlatformNotSupportedException>(() => OutboxSelftest.EffectiveUserId());
            Assert.That(typeof(OutboxDirectories).GetMethod("EffectiveUserId"), Is.Null, "no production API in the core for it");
        }

        [Test]
        public void The_options_are_read()
        {
            var o = ServerOptions.Parse(new[] { "-outboxSelftest", "hold", "-selftestParent", "/var/lib/x", "-selftestId", "019a0000-0000-7000-8000-00000000a001" }, out var warnings);
            Assert.That(warnings, Is.Empty);
            Assert.That((o.OutboxSelftest, o.SelftestParent, o.SelftestId), Is.EqualTo(("hold", "/var/lib/x", "019a0000-0000-7000-8000-00000000a001")));
            Assert.That(ServerOptions.Default.SelftestParent, Is.EqualTo("/var/lib/blackglass-selftest"));
            Assert.That(ServerOptions.Default.OutboxSelftest, Is.Null);
        }

        [TestCase("-outboxSelftest")]
        [TestCase("-outboxSelftest", "-net")]
        [TestCase("-selftestId", "019a0000-0000-7000-8000-00000000a001")]
        [TestCase("-selftestParent", "/var/lib/x")]
        public void A_selftest_request_without_a_phase_selects_a_failing_selftest_never_a_mission(params string[] args)
        {
            var o = ServerOptions.Parse(args, out _);
            Assert.That(o.OutboxSelftest, Is.EqualTo(string.Empty));
            Assert.That(OutboxSelftest.IsPhase(o.OutboxSelftest), Is.False);
        }

        [Test]
        public void The_production_outbox_parent_is_refused()
        {
            var o = ServerOptions.Parse(new[] { "-outboxSelftest", "publish", "-selftestParent", "/var/lib/blackglass-outbox", "-selftestId", "019a0000-0000-7000-8000-00000000a001" }, out _);
            var e = Assert.Throws<OutboxIOException>(() => ServerBootstrap.OpenSelftestOutbox(o, "/var/lib/blackglass-outbox/019a0000-0000-7000-8000-00000000a001"));
            Assert.That(e.Code, Is.EqualTo("production_outbox"));
        }

        [Test]
        public void The_bootstrap_runs_the_selftest_without_a_mission_or_the_network()
        {
            var go = new GameObject("Server");
            try
            {
                var bootstrap = go.AddComponent<ServerBootstrap>();
                var exit = -1;
                bootstrap.Exit = code => exit = code;
                bootstrap.OpenOutbox = _ => OutboxDirectories.OpenForEditor(dir);
                bootstrap.EffectiveUserId = () => UnprivilegedUid;
                // no MissionDirector, no TacticalPause, no NetServer: the self-test needs none and -net is never read
                bootstrap.Begin(new[] { "-batchmode", "-outboxSelftest", "publish", "-net", "-netPort", "7801" });
                Assert.That(bootstrap.HasExited, Is.False, bootstrap.ExitReason);
                var now = 0.0;
                var watch = Stopwatch.StartNew();
                while (!bootstrap.HasExited && watch.Elapsed.TotalSeconds < 20)
                {
                    bootstrap.Tick(now += 0.016);
                    Thread.Sleep(1);
                }
                Assert.That(exit, Is.EqualTo(0), bootstrap.ExitReason);
                Assert.That(bootstrap.ExitReason, Does.StartWith("[Selftest] result=pass phase=publish checks=7 "));
                Assert.That(Files(), Has.Member(Ready));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void The_bootstrap_exits_3_on_a_failed_selftest()
        {
            var go = new GameObject("Server");
            try
            {
                var bootstrap = go.AddComponent<ServerBootstrap>();
                var exit = -1;
                bootstrap.Exit = code => exit = code;
                bootstrap.OpenOutbox = _ => OutboxDirectories.OpenForEditor(dir);
                bootstrap.EffectiveUserId = () => 0;
                bootstrap.Begin(new[] { "-outboxSelftest", "publish" });
                for (var frame = 0; frame < 10 && !bootstrap.HasExited; frame++)
                    bootstrap.Tick(frame * 0.016);
                Assert.That(exit, Is.EqualTo(ServerExitCodes.SelftestFailed));
                Assert.That(Files(), Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
