using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// The §H protocol (.tmp → .written → .ready) over a FaultingOutboxDirectory in a temporary folder: the exact call order,
    /// every crash boundary as the agent would see it afterwards, idempotence, refusals and the retry after a failed sync.
    /// </summary>
    public sealed class OutboxWriterTests
    {
        internal const string Account = "JG", Attempt = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";
        const string Tmp = "JG.manifest.tmp", Written = "JG.manifest.written", Ready = "JG.manifest.ready";

        string folder;

        internal static byte[] Manifest(string vector = "01-minimal") =>
            File.ReadAllBytes(Path.Combine(ContractVectors.ProjectRoot, "Backend", "contracts", "manifest-v1", "valid", vector + ".canonical.json"));

        internal static string NewFolder()
        {
            var path = Path.Combine(Path.GetTempPath(), "bg-outbox-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        [SetUp]
        public void SetUp() => folder = NewFolder();

        [TearDown]
        public void TearDown() => Directory.Delete(folder, true);

        byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(folder, name));
        bool Exists(string name) => File.Exists(Path.Combine(folder, name));
        AgentView.Result Agent() => AgentView.Classify(folder, Account, Attempt);

        [Test]
        public void WriteDurable_calls_in_order()
        {
            var d = new FaultingOutboxDirectory(folder);
            Assert.That(new OutboxWriter(d).WriteDurable(Account, Manifest()), Is.EqualTo(WriteResult.Written));
            Assert.That(d.Calls, Is.EqualTo(new[] { "Probe(" + Ready + ")", "Probe(" + Written + ")", "Probe(" + Tmp + ")", "WriteAndSync(" + Tmp + ")",
                "RenameNoReplace(" + Tmp + "," + Written + ")", "SyncDirectory" }));
            Assert.That(Bytes(Written), Is.EqualTo(Manifest()));
            Assert.That(Exists(Tmp), Is.False);
        }

        [Test]
        public void Publish_calls_in_order()
        {
            new OutboxWriter(OutboxDirectories.OpenForEditor(folder)).WriteDurable(Account, Manifest());
            var d = new FaultingOutboxDirectory(folder);
            Assert.That(new OutboxWriter(d).Publish(Account, Manifest()), Is.EqualTo(PublishResult.Published));
            Assert.That(d.Calls, Is.EqualTo(new[] { "Probe(" + Ready + ")", "Probe(" + Written + ")", "ReadRegular(" + Written + ")",
                "RenameNoReplace(" + Written + "," + Ready + ")", "SyncDirectory" }));
            Assert.That(Bytes(Ready), Is.EqualTo(Manifest()));
            Assert.That(Exists(Written), Is.False);
        }

        // ---- crash boundaries: what a fresh process and the agent find afterwards ----

        void CrashDuringWrite(Action<FaultingOutboxDirectory> arm)
        {
            var d = new FaultingOutboxDirectory(folder);
            arm(d);
            Assert.Throws<SimulatedCrash>(() => new OutboxWriter(d).WriteDurable(Account, Manifest()));
        }

        [Test]
        public void A_crash_halfway_through_the_tmp_write_leaves_no_result()
        {
            CrashDuringWrite(d => d.CrashHalfwayThroughWrite = 3);
            Assert.That(Bytes(Tmp).Length, Is.EqualTo(Manifest().Length / 2), "a partial .tmp");
            Assert.That(Exists(Written), Is.False);
            Assert.That(Agent(), Is.EqualTo(AgentView.Result.Absent));
        }

        [Test]
        public void A_crash_after_the_tmp_write_leaves_no_result()
        {
            CrashDuringWrite(d => d.CrashAfter = 3);
            Assert.That(Bytes(Tmp), Is.EqualTo(Manifest()), "a complete .tmp is still not a result");
            Assert.That(Agent(), Is.EqualTo(AgentView.Result.Absent));
        }

        [Test]
        public void A_crash_after_the_rename_before_the_directory_sync_leaves_a_complete_written()
        {
            // On the portable directory the rename is already visible; on Linux the directory fsync is what makes it durable.
            CrashDuringWrite(d => d.CrashBefore = 5);
            Assert.That(Bytes(Written), Is.EqualTo(Manifest()));
            Assert.That(Agent(), Is.EqualTo(AgentView.Result.RelayableAfterVerifiedExit));
        }

        [Test]
        public void A_crash_after_the_written_sync_is_recoverable_after_exit_and_the_next_write_finishes_it()
        {
            CrashDuringWrite(d => d.CrashAfter = 5);
            Assert.That(Agent(), Is.EqualTo(AgentView.Result.RelayableAfterVerifiedExit));
            Assert.That(new OutboxWriter(OutboxDirectories.OpenForEditor(folder)).WriteDurable(Account, Manifest()), Is.EqualTo(WriteResult.AlreadyWritten));
        }

        [Test]
        public void A_crash_after_the_publish_rename_leaves_a_ready_result()
        {
            new OutboxWriter(OutboxDirectories.OpenForEditor(folder)).WriteDurable(Account, Manifest());
            var d = new FaultingOutboxDirectory(folder) { CrashBefore = 4 };
            Assert.Throws<SimulatedCrash>(() => new OutboxWriter(d).Publish(Account, Manifest()));
            Assert.That(Agent(), Is.EqualTo(AgentView.Result.RelayableNow));
            Assert.That(new OutboxWriter(OutboxDirectories.OpenForEditor(folder)).Publish(Account, Manifest()), Is.EqualTo(PublishResult.AlreadyPublished));
        }

        // ---- idempotence and refusals ----

        [Test]
        public void A_second_write_of_identical_bytes_is_AlreadyWritten()
        {
            var w = new OutboxWriter(OutboxDirectories.OpenForEditor(folder));
            w.WriteDurable(Account, Manifest());
            Assert.That(w.WriteDurable(Account, Manifest()), Is.EqualTo(WriteResult.AlreadyWritten));
        }

        [Test]
        public void A_second_write_of_different_bytes_is_Conflict_and_changes_nothing()
        {
            var w = new OutboxWriter(OutboxDirectories.OpenForEditor(folder));
            w.WriteDurable(Account, Manifest());
            Assert.That(w.WriteDurable(Account, Manifest("13-ledger-mismatch")), Is.EqualTo(WriteResult.Conflict));
            Assert.That(Bytes(Written), Is.EqualTo(Manifest()));
            Assert.That(Exists(Tmp), Is.False);
            w.Publish(Account, Manifest());
            Assert.That(w.WriteDurable(Account, Manifest("13-ledger-mismatch")), Is.EqualTo(WriteResult.Conflict));
            Assert.That(w.WriteDurable(Account, Manifest()), Is.EqualTo(WriteResult.AlreadyPublished));
        }

        [Test]
        public void A_leftover_tmp_is_rewritten_never_promoted()
        {
            File.WriteAllBytes(Path.Combine(folder, Tmp), Encoding.UTF8.GetBytes("{\"partial\":"));
            Assert.That(Agent(), Is.EqualTo(AgentView.Result.Absent));
            new OutboxWriter(OutboxDirectories.OpenForEditor(folder)).WriteDurable(Account, Manifest());
            Assert.That(Bytes(Written), Is.EqualTo(Manifest()));
        }

        [Test]
        public void Publish_without_written_is_NothingWritten_and_twice_is_AlreadyPublished()
        {
            var w = new OutboxWriter(OutboxDirectories.OpenForEditor(folder));
            Assert.That(w.Publish(Account, Manifest()), Is.EqualTo(PublishResult.NothingWritten));
            w.WriteDurable(Account, Manifest());
            Assert.That(w.Publish(Account, Manifest("13-ledger-mismatch")), Is.EqualTo(PublishResult.Conflict));
            Assert.That(w.Publish(Account, Manifest()), Is.EqualTo(PublishResult.Published));
            Assert.That(w.Publish(Account, Manifest()), Is.EqualTo(PublishResult.AlreadyPublished));
            Assert.That(w.Publish(Account, Manifest("13-ledger-mismatch")), Is.EqualTo(PublishResult.Conflict));
        }

        [Test]
        public void An_unsafe_entry_is_refused()
        {
            Directory.CreateDirectory(Path.Combine(folder, Written));
            var e = Assert.Throws<OutboxIOException>(() => new OutboxWriter(OutboxDirectories.OpenForEditor(folder)).WriteDurable(Account, Manifest()));
            Assert.That(e.Code, Is.EqualTo("unsafe_entry"));
        }

        [Test]
        public void A_bad_account_or_empty_or_oversized_bytes_is_refused_with_no_call()
        {
            var d = new FaultingOutboxDirectory(folder);
            var w = new OutboxWriter(d);
            Assert.Throws<ArgumentException>(() => w.WriteDurable("1JG", Manifest()));
            Assert.Throws<ArgumentException>(() => w.WriteDurable("../JG", Manifest()));
            Assert.Throws<ArgumentException>(() => w.WriteDurable(Account, new byte[0]));
            Assert.Throws<ArgumentException>(() => w.WriteDurable(Account, new byte[OutboxWriter.MaxBytes + 1]));
            Assert.Throws<ArgumentException>(() => w.Publish("1JG", Manifest()));
            Assert.That(d.Calls, Is.Empty);
        }

        [Test]
        public void The_written_file_survives_and_a_new_writer_sees_it()
        {
            new OutboxWriter(OutboxDirectories.OpenForEditor(folder)).WriteDurable(Account, Manifest());
            Assert.That(new OutboxWriter(OutboxDirectories.OpenForEditor(folder)).WriteDurable(Account, Manifest()), Is.EqualTo(WriteResult.AlreadyWritten));
            Assert.That(Agent(), Is.EqualTo(AgentView.Result.RelayableAfterVerifiedExit));
        }

        [Test]
        public void File_names_match_the_agents_pattern()
        {
            var agent = new Regex(@"^([1-9A-HJ-NP-Za-km-z]{1,11})\.manifest\.(tmp|written|ready)\z");
            foreach (var name in new[] { OutboxFileNames.Tmp(Account), OutboxFileNames.Written(Account), OutboxFileNames.Ready(Account), OutboxFileNames.Ready("NQm6nKp8qFC") })
                Assert.That(agent.IsMatch(name), Is.True, name);
            Assert.That(OutboxFileNames.IsOutboxName("JG.manifest.ready\n"), Is.False);
            Assert.Throws<ArgumentException>(() => OutboxFileNames.Ready("1JG"));
        }

        // ---- the retry after a failed directory fsync (M4) ----

        [Test]
        public void A_retry_after_a_failed_directory_sync_syncs_again()
        {
            var d = new FaultingOutboxDirectory(folder);
            d.Fail("SyncDirectory", "directory_sync_failed");
            var w = new OutboxWriter(d);
            Assert.That(Assert.Throws<OutboxIOException>(() => w.WriteDurable(Account, Manifest())).Code, Is.EqualTo("directory_sync_failed"));
            Assert.That(w.WriteDurable(Account, Manifest()), Is.EqualTo(WriteResult.AlreadyWritten));
            Assert.That(d.Calls.Count(c => c == "SyncDirectory"), Is.EqualTo(2));
            d.Fail("SyncDirectory", "directory_sync_failed");
            Assert.Throws<OutboxIOException>(() => w.Publish(Account, Manifest()));
            Assert.That(w.Publish(Account, Manifest()), Is.EqualTo(PublishResult.AlreadyPublished));
            Assert.That(d.Calls.Count(c => c == "SyncDirectory"), Is.EqualTo(4));
        }
    }
}
