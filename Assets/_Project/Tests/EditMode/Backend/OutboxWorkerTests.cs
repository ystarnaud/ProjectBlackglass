using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// The I/O worker (owner ruling D6): every file operation on its own thread, the simulation thread only submitting and reading
    /// a status. Every wait is bounded (5 s); TearDown opens every gate and stops every worker, so a failing test never leaves a
    /// blocked thread behind across a domain reload.
    /// </summary>
    public sealed class OutboxWorkerTests
    {
        const string Account = OutboxWriterTests.Account, Attempt = OutboxWriterTests.Attempt;
        static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

        string folder;
        readonly List<FaultingOutboxDirectory> directories = new List<FaultingOutboxDirectory>();
        readonly List<OutboxWorker> workers = new List<OutboxWorker>();

        [SetUp]
        public void SetUp() => folder = OutboxWriterTests.NewFolder();

        [TearDown]
        public void TearDown()
        {
            foreach (var d in directories) d.OpenAllGates();
            foreach (var w in workers) w.Stop(Bound);
            directories.Clear();
            workers.Clear();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }

        FaultingOutboxDirectory Dir()
        {
            var d = new FaultingOutboxDirectory(folder);
            directories.Add(d);
            return d;
        }

        OutboxWorker Worker(IOutboxDirectory d)
        {
            var w = new OutboxWorker(d);
            workers.Add(w);
            return w;
        }

        static byte[] Manifest(string vector = "01-minimal") => OutboxWriterTests.Manifest(vector);

        static string Hash(byte[] bytes) => ManifestCodec.HashOf(bytes);

        static PublicationStatus WaitFor(PublicationHandle h, params PublicationStage[] stages)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < Bound)
            {
                var s = h.Status;
                if (stages.Contains(s.Stage)) return s;
                Thread.Sleep(1);
            }
            Assert.Fail("timed out in " + h.Status.Stage + ", waiting for " + string.Join("/", stages));
            return null;
        }

        static void WaitUntil(Func<bool> condition, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed > Bound) Assert.Fail("timed out waiting for " + what);
                Thread.Sleep(1);
            }
        }

        bool Exists(string suffix) => File.Exists(Path.Combine(folder, Account + suffix));
        byte[] Bytes(string suffix) => File.ReadAllBytes(Path.Combine(folder, Account + suffix));

        [Test]
        public void Submit_returns_before_any_file_operation_completes()
        {
            var d = Dir();
            var gate = d.Gate("WriteAndSync");
            var h = Worker(d).Submit(Account, Manifest(), Hash(Manifest()));
            Assert.That(new[] { PublicationStage.Queued, PublicationStage.Writing }, Has.Member(h.Status.Stage));
            Assert.That(Exists(".manifest.written"), Is.False);
            gate.Set();
            Assert.That(WaitFor(h, PublicationStage.Written).Durable, Is.True);
            Assert.That(Bytes(".manifest.written"), Is.EqualTo(Manifest()));
        }

        [Test]
        public void The_simulation_is_not_blocked_by_a_slow_fsync()
        {
            var d = Dir();
            var gate = d.Gate("WriteAndSync");
            var worker = Worker(d);
            var h = worker.Submit(Account, Manifest(), Hash(Manifest()));
            WaitUntil(() => d.Calls.Any(c => c.StartsWith("WriteAndSync", StringComparison.Ordinal)), "the worker to block in WriteAndSync");
            var slowest = TimeSpan.Zero;
            for (var tick = 0; tick < 1000; tick++)
            {
                var clock = Stopwatch.StartNew();
                Assert.That(h.Status.Stage, Is.EqualTo(PublicationStage.Writing));
                if (tick % 100 == 0) worker.Submit(Base58Id.Format(1000 + tick), Manifest(), Hash(Manifest()));
                if (clock.Elapsed > slowest) slowest = clock.Elapsed;
            }
            Assert.That(gate.IsSet, Is.False, "every call returned while the worker was still blocked in the fsync");
            Assert.That(slowest.TotalMilliseconds, Is.LessThan(250));
            gate.Set();
            WaitFor(h, PublicationStage.Written);
        }

        [Test]
        public void Submit_copies_the_bytes()
        {
            var d = Dir();
            var gate = d.Gate("WriteAndSync");
            var bytes = Manifest();
            var h = Worker(d).Submit(Account, bytes, Hash(bytes));
            bytes[0] = (byte)'[';
            gate.Set();
            WaitFor(h, PublicationStage.Written);
            Assert.That(Bytes(".manifest.written"), Is.EqualTo(Manifest()));
        }

        [Test]
        public void Written_waits_for_AllowPublication()
        {
            var h = Worker(Dir()).Submit(Account, Manifest(), Hash(Manifest()));
            WaitFor(h, PublicationStage.Written);
            Thread.Sleep(50);
            Assert.That(h.Status.Stage, Is.EqualTo(PublicationStage.Written));
            Assert.That(Exists(".manifest.ready"), Is.False);
            h.AllowPublication();
            WaitFor(h, PublicationStage.Published);
            Assert.That(Bytes(".manifest.ready"), Is.EqualTo(Manifest()));
            Assert.That(h.ManifestHash, Is.EqualTo(Hash(Manifest())));
        }

        [Test]
        public void AllowPublication_before_the_write_completes_publishes_after_it()
        {
            var d = Dir();
            var gate = d.Gate("WriteAndSync");
            var h = Worker(d).Submit(Account, Manifest(), Hash(Manifest()));
            h.AllowPublication();
            Assert.That(h.Status.PublicationAllowed, Is.True);
            gate.Set();
            WaitFor(h, PublicationStage.Published);
        }

        [Test]
        public void A_repeated_identical_Submit_returns_the_same_handle_and_a_different_one_is_Conflict()
        {
            var d = Dir();
            var worker = Worker(d);
            var h = worker.Submit(Account, Manifest(), Hash(Manifest()));
            Assert.That(worker.Submit(Account, Manifest(), Hash(Manifest())), Is.SameAs(h));
            WaitFor(h, PublicationStage.Written);
            var calls = d.Calls.Count;
            var other = worker.Submit(Account, Manifest("13-ledger-mismatch"), Hash(Manifest("13-ledger-mismatch")));
            Assert.That(other.Status.Stage, Is.EqualTo(PublicationStage.Conflict));
            Thread.Sleep(20);
            Assert.That(d.Calls.Count, Is.EqualTo(calls), "a conflicting request touches nothing");
            Assert.That(Bytes(".manifest.written"), Is.EqualTo(Manifest()));
        }

        static IEnumerable<TestCaseData> Failures()
        {
            yield return new TestCaseData("WriteAndSync", "fsync_failed", false, AgentView.Result.Absent).SetName("A failed tmp fsync");
            yield return new TestCaseData("RenameNoReplace(JG.manifest.tmp", "rename_failed", false, AgentView.Result.Absent).SetName("A failed written rename");
            yield return new TestCaseData("SyncDirectory", "directory_sync_failed", false, AgentView.Result.RelayableAfterVerifiedExit).SetName("A failed written directory sync");
        }

        [TestCaseSource(nameof(Failures))]
        public void A_failure_while_writing_is_reported(string call, string code, bool durable, AgentView.Result agent)
        {
            var d = Dir();
            d.Fail(call, code, 5);
            var h = Worker(d).Submit(Account, Manifest(), Hash(Manifest()));
            var s = WaitFor(h, PublicationStage.Failed);
            Assert.That((s.FailureCode, s.Errno, s.Durable), Is.EqualTo((code, 5, durable)));
            Assert.That(AgentView.Classify(folder, Account, Attempt), Is.EqualTo(agent));
        }

        [TestCase("RenameNoReplace(JG.manifest.written", "rename_failed", AgentView.Result.RelayableAfterVerifiedExit)]
        [TestCase("SyncDirectory", "directory_sync_failed", AgentView.Result.RelayableNow)]
        public void A_failure_while_publishing_is_reported(string call, string code, AgentView.Result agent)
        {
            var d = Dir();
            var h = Worker(d).Submit(Account, Manifest(), Hash(Manifest()));
            WaitFor(h, PublicationStage.Written);
            d.Fail(call, code, 5);
            h.AllowPublication();
            var s = WaitFor(h, PublicationStage.Failed);
            Assert.That((s.FailureCode, s.Durable), Is.EqualTo((code, true)), "the .written was durable before the publish failed");
            Assert.That(AgentView.Classify(folder, Account, Attempt), Is.EqualTo(agent));
        }

        [Test]
        public void A_resubmit_after_a_failed_directory_sync_becomes_Written()
        {
            var d = Dir();
            d.Fail("SyncDirectory", "directory_sync_failed");
            var worker = Worker(d);
            WaitFor(worker.Submit(Account, Manifest(), Hash(Manifest())), PublicationStage.Failed);
            var again = worker.Submit(Account, Manifest(), Hash(Manifest()));
            Assert.That(WaitFor(again, PublicationStage.Written).Durable, Is.True);
            Assert.That(d.Calls.Count(c => c == "SyncDirectory"), Is.EqualTo(2));
            Assert.That(d.Calls.Count(c => c.StartsWith("WriteAndSync", StringComparison.Ordinal)), Is.EqualTo(1), "the existing .written was kept");
        }

        [Test]
        public void A_hash_that_does_not_match_the_bytes_is_Failed()
        {
            var h = Worker(Dir()).Submit(Account, Manifest(), Hash(Manifest("13-ledger-mismatch")));
            Assert.That(WaitFor(h, PublicationStage.Failed).FailureCode, Is.EqualTo("hash_mismatch"));
            Assert.That(Exists(".manifest.tmp") || Exists(".manifest.written"), Is.False);
        }

        [Test]
        public void Submit_refuses_bad_arguments_on_the_callers_thread()
        {
            var worker = Worker(Dir());
            Assert.Throws<ArgumentException>(() => worker.Submit("1JG", Manifest(), Hash(Manifest())));
            Assert.Throws<ArgumentException>(() => worker.Submit(Account, new byte[0], Hash(Manifest())));
            Assert.Throws<ArgumentException>(() => worker.Submit(Account, new byte[OutboxWriter.MaxBytes + 1], Hash(Manifest())));
            Assert.Throws<ArgumentException>(() => worker.Submit(Account, Manifest(), "sha256:bad"));
        }

        [Test]
        public void Cancel_before_the_write_writes_nothing()
        {
            var d = Dir();
            var gate = d.Gate("WriteAndSync");
            var worker = Worker(d);
            var first = worker.Submit("JH", Manifest(), Hash(Manifest()));
            var second = worker.Submit(Account, Manifest(), Hash(Manifest()));
            second.Cancel();
            Assert.That(second.Status.Stage, Is.EqualTo(PublicationStage.Cancelled));
            Assert.That(second.Status.Durable, Is.False);
            gate.Set();
            WaitFor(first, PublicationStage.Written);
            Thread.Sleep(20);
            Assert.That(Exists(".manifest.tmp") || Exists(".manifest.written"), Is.False);
        }

        [Test]
        public void Cancel_during_the_write_finishes_it_and_never_publishes()
        {
            var d = Dir();
            var gate = d.Gate("WriteAndSync");
            var h = Worker(d).Submit(Account, Manifest(), Hash(Manifest()));
            WaitUntil(() => h.Status.Stage == PublicationStage.Writing, "the write to start");
            h.Cancel();
            h.AllowPublication();
            gate.Set();
            var s = WaitFor(h, PublicationStage.Cancelled);
            Assert.That(s.Durable, Is.True);
            Thread.Sleep(20);
            Assert.That(Exists(".manifest.ready"), Is.False);
            Assert.That(Bytes(".manifest.written"), Is.EqualTo(Manifest()), "the durable result stays for the agent");
        }

        [Test]
        public void Cancel_after_Written_never_publishes()
        {
            var h = Worker(Dir()).Submit(Account, Manifest(), Hash(Manifest()));
            WaitFor(h, PublicationStage.Written);
            h.Cancel();
            h.AllowPublication();
            Thread.Sleep(20);
            Assert.That(h.Status.Stage, Is.EqualTo(PublicationStage.Cancelled));
            Assert.That(Exists(".manifest.ready"), Is.False);
            Assert.That(Exists(".manifest.written"), Is.True);
        }

        [Test]
        public void Stop_completes_queued_writes_and_allowed_publications()
        {
            var d = Dir();
            var gate = d.Gate("WriteAndSync");
            var worker = Worker(d);
            var published = worker.Submit("JH", Manifest(), Hash(Manifest()));
            published.AllowPublication();
            var written = worker.Submit(Account, Manifest(), Hash(Manifest()));
            gate.Set();
            Assert.That(worker.Stop(Bound), Is.True);
            Assert.That(published.Status.Stage, Is.EqualTo(PublicationStage.Published));
            Assert.That(written.Status.Stage, Is.EqualTo(PublicationStage.Written), "an unreleased result stays written");
            Assert.That(Exists(".manifest.written"), Is.True);
            Assert.Throws<InvalidOperationException>(() => worker.Submit("JK", Manifest(), Hash(Manifest())));
        }

        [Test]
        public void Only_the_worker_closes_the_directory_after_its_last_call()
        {
            var d = Dir();
            var gate = d.Gate("WriteAndSync");
            var worker = Worker(d);
            var h = worker.Submit(Account, Manifest(), Hash(Manifest()));
            WaitUntil(() => d.Calls.Any(c => c.StartsWith("WriteAndSync", StringComparison.Ordinal)), "the worker to block");
            worker.Dispose();
            Assert.That(d.Calls, Has.No.Member("Dispose"), "Dispose never closes the directory under a busy worker");
            gate.Set();
            WaitUntil(() => d.Calls.Contains("Dispose"), "the worker to close the directory");
            Assert.That(d.Calls.Last(), Is.EqualTo("Dispose"));
            var calls = d.Calls.ToList();
            Assert.That(calls.IndexOf("SyncDirectory"), Is.LessThan(calls.IndexOf("Dispose")));
            Assert.That(Bytes(".manifest.written"), Is.EqualTo(Manifest()), "the write landed in the original directory");
            Assert.That(h.Status.Stage, Is.EqualTo(PublicationStage.Written));
        }

        [Test]
        public void An_unexpected_exception_does_not_kill_the_worker()
        {
            var d = Dir();
            d.CrashBefore = 0; // the first call of the first job throws something unexpected
            var worker = Worker(d);
            var first = worker.Submit("JH", Manifest(), Hash(Manifest()));
            Assert.That(WaitFor(first, PublicationStage.Failed).FailureCode, Is.EqualTo("unexpected"));
            var second = worker.Submit(Account, Manifest(), Hash(Manifest()));
            WaitFor(second, PublicationStage.Written);
        }

        [Test]
        public void No_backend_is_needed_to_preserve_a_result()
        {
            var h = Worker(Dir()).Submit(Account, Manifest(), Hash(Manifest()));
            WaitFor(h, PublicationStage.Written);
            Assert.That(Bytes(".manifest.written"), Is.EqualTo(Manifest()));
            Assert.That(AgentView.Classify(folder, Account, Attempt), Is.EqualTo(AgentView.Result.RelayableAfterVerifiedExit));
        }

        [Test]
        public void An_already_published_result_is_Published_at_once()
        {
            var writer = new OutboxWriter(OutboxDirectories.OpenForEditor(folder));
            writer.WriteDurable(Account, Manifest());
            writer.Publish(Account, Manifest());
            var h = Worker(Dir()).Submit(Account, Manifest(), Hash(Manifest()));
            Assert.That(WaitFor(h, PublicationStage.Published).Durable, Is.True);
        }
    }
}
