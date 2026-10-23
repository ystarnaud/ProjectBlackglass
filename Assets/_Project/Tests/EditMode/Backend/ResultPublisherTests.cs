using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// Result publication (Stage 13, Task 9; Stage 9 §H steps 3–5) over a real OutboxWorker and a real portable outbox directory: the
    /// recorded notice only once the result is durable, publication only after the notice ended (any answer or its 30 s bound), never
    /// blocked by an unreachable backend, never over a different result, and never a settlement call.
    /// </summary>
    public sealed class ResultPublisherTests
    {
        const string Account = "JG";
        static readonly byte[] Bytes = Encoding.UTF8.GetBytes("{\"manifestVersion\":1,\"x\":\"result\"}");
        static readonly string Hash = HashOf(Bytes);

        string dir;
        FakeHttpTransport transport;
        StepClock clock;
        OutboxWorker worker;
        ResultPublisher publisher;
        int saved;

        static string HashOf(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return "sha256:" + BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "bg-publisher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            transport = new FakeHttpTransport(BackendTestKit.Prefix);
            clock = new StepClock();
            saved = 0;
        }

        [TearDown]
        public void TearDown()
        {
            worker?.Stop(TimeSpan.FromSeconds(5));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        void Make(IOutboxDirectory directory = null)
        {
            worker = new OutboxWorker(directory ?? OutboxDirectories.OpenForEditor(dir));
            publisher = new ResultPublisher(worker, BackendTestKit.Api(transport), new BackendTimings());
            publisher.ResultSaved += _ => saved++;
        }

        string Ready => Path.Combine(dir, Account + ".manifest.ready");
        string Written => Path.Combine(dir, Account + ".manifest.written");

        // Real time passes too: the worker's thread does the file work.
        void Run(double seconds, Func<bool> until = null) =>
            clock.Run(transport, now => { Thread.Sleep(1); publisher.Tick(now); }, seconds, until ?? (() => publisher.State == PublisherState.Published || publisher.State == PublisherState.NotPublished));

        [Test]
        public void Durable_first_then_the_notice_then_publication()
        {
            Make();
            var sawWrittenAtNotice = false;
            transport.OnSend = sent => sawWrittenAtNotice = File.Exists(Written) && !File.Exists(Ready);
            transport.Answer("recorded", 200, "{\"state\":\"result_pending\"}");
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(10);
            Assert.That(publisher.State, Is.EqualTo(PublisherState.Published));
            Assert.That(sawWrittenAtNotice, Is.True, "the notice names a durable, unpublished result");
            Assert.That(transport.To("recorded").Single().BodyText, Is.EqualTo("{\"manifestHash\":\"" + Hash + "\"}"));
            Assert.That(File.ReadAllBytes(Ready), Is.EqualTo(Bytes));
            Assert.That(File.Exists(Written), Is.False);
            Assert.That(publisher.RecordedAnswer, Is.EqualTo("ok"));
            Assert.That(saved, Is.EqualTo(1));
        }

        [Test]
        public void An_unreachable_backend_publishes_at_the_bound()
        {
            Make();
            transport.RefuseAlways("recorded");
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(31);
            Assert.That(publisher.State, Is.EqualTo(PublisherState.Published), "published within the 30 s bound");
            Assert.That(publisher.RecordedAnswer, Is.EqualTo("gave_up"));
            Assert.That(File.ReadAllBytes(Ready), Is.EqualTo(Bytes));
            var notices = transport.To("recorded").ToArray();
            Assert.That(notices.Length, Is.GreaterThanOrEqualTo(5), "it kept trying while the bound allowed");
            Assert.That(notices.Last().At - notices.First().At, Is.LessThan(30), "never past the bound");
        }

        [Test]
        public void A_backend_that_never_answers_publishes_at_the_bound()
        {
            Make();
            transport.SilentAlways("recorded");
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(35);
            Assert.That(publisher.State, Is.EqualTo(PublisherState.Published));
            Assert.That(publisher.RecordedAnswer, Is.EqualTo("gave_up"));
        }

        [TestCase(401, "", "unauthorized")]
        [TestCase(409, "ConflictingReport", "conflicting_report")]
        [TestCase(409, "InvalidState", "invalid_state")]
        [TestCase(404, "ParticipantNotFound", "not_found")]
        public void Any_answer_ends_the_notice_and_publishes(int status, string code, string answer)
        {
            Make();
            transport.Answer("recorded", status, code.Length == 0 ? "" : BackendTestKit.Problem(code));
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(5);
            Assert.That(publisher.State, Is.EqualTo(PublisherState.Published));
            Assert.That(publisher.RecordedAnswer, Is.EqualTo(answer));
            Assert.That(transport.To("recorded").Count(), Is.EqualTo(1));
        }

        [Test]
        public void A_lost_answer_repeats_the_identical_notice_and_publishes_once()
        {
            Make();
            transport.Drop("recorded").Answer("recorded", 200, "{\"state\":\"result_pending\"}");
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(20);
            Assert.That(publisher.State, Is.EqualTo(PublisherState.Published));
            Assert.That(transport.To("recorded").Select(r => r.BodyText).Distinct().Count(), Is.EqualTo(1));
            Assert.That(transport.To("recorded").Count(), Is.EqualTo(2));
            Assert.That(saved, Is.EqualTo(1));
        }

        [Test]
        public void A_different_result_already_written_is_never_overwritten_and_never_notified()
        {
            File.WriteAllBytes(Written, Encoding.UTF8.GetBytes("{\"other\":true}"));
            Make();
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(5);
            Assert.That(publisher.State, Is.EqualTo(PublisherState.NotPublished));
            Assert.That(publisher.Reason, Is.EqualTo("outbox_conflict"));
            Assert.That(File.ReadAllText(Written), Is.EqualTo("{\"other\":true}"));
            Assert.That(transport.Requests, Is.Empty);
        }

        [Test]
        public void An_identical_ready_result_is_already_published()
        {
            File.WriteAllBytes(Ready, Bytes);
            Make();
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(5);
            Assert.That(publisher.State, Is.EqualTo(PublisherState.Published));
            Assert.That(transport.Requests, Is.Empty);
        }

        [Test]
        public void A_failing_write_is_resubmitted_then_not_published()
        {
            var faulting = new FaultingOutboxDirectory(dir);
            faulting.Fail("WriteAndSync", "io", 5, times: 10);
            Make(faulting);
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(20);
            Assert.That(publisher.State, Is.EqualTo(PublisherState.NotPublished));
            Assert.That(publisher.Reason, Is.EqualTo("outbox_failed"));
            Assert.That(faulting.Calls.Count(c => c.StartsWith("WriteAndSync")), Is.EqualTo(4), "the first write and three resubmissions");
            Assert.That(transport.Requests, Is.Empty);
        }

        [Test]
        public void A_write_that_fails_once_succeeds_on_resubmission()
        {
            var faulting = new FaultingOutboxDirectory(dir);
            faulting.Fail("WriteAndSync", "io", 5, times: 1);
            Make(faulting);
            transport.Answer("recorded", 200, "{\"state\":\"result_pending\"}");
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(20);
            Assert.That(publisher.State, Is.EqualTo(PublisherState.Published));
            Assert.That(File.ReadAllBytes(Ready), Is.EqualTo(Bytes));
        }

        [Test]
        public void A_failing_publish_leaves_the_durable_result_for_the_agent()
        {
            var faulting = new FaultingOutboxDirectory(dir);
            Make(faulting);
            transport.Answer("recorded", 200, "{\"state\":\"result_pending\"}");
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(10, () => publisher.State == PublisherState.Notifying);
            faulting.Fail("RenameNoReplace", "io", 5, times: 10);
            Run(10);
            Assert.That(publisher.State, Is.EqualTo(PublisherState.NotPublished));
            Assert.That(publisher.Durable, Is.True);
            Assert.That(File.ReadAllBytes(Written), Is.EqualTo(Bytes), "the complete .written stays for the agent (A10-D1)");
        }

        [Test]
        public void Shutdown_while_notifying_ends_the_notice_and_publishes_on_the_drain()
        {
            Make();
            transport.SilentAlways("recorded");
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(5, () => publisher.State == PublisherState.Notifying);
            publisher.Shutdown();
            Assert.That(publisher.RecordedAnswer, Is.EqualTo("aborted"));
            Assert.That(worker.Stop(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(File.ReadAllBytes(Ready), Is.EqualTo(Bytes));
        }

        [Test]
        public void Shutdown_while_writing_completes_the_durable_write_never_half()
        {
            var faulting = new FaultingOutboxDirectory(dir);
            var gate = faulting.Gate("WriteAndSync");
            Make(faulting);
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Thread.Sleep(50);
            publisher.Shutdown();
            gate.Set();
            Assert.That(worker.Stop(TimeSpan.FromSeconds(5)), Is.True);
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
            Assert.That(files.Any(f => f.EndsWith(".tmp")), Is.False, "never a half-written result");
            var result = File.Exists(Ready) ? Ready : Written;
            Assert.That(File.ReadAllBytes(result), Is.EqualTo(Bytes));
        }

        [Test]
        public void Shutdown_during_a_pending_resubmission_still_writes_the_result()
        {
            var faulting = new FaultingOutboxDirectory(dir);
            faulting.Fail("WriteAndSync", "io", 5, times: 1);
            Make(faulting);
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(1, () => false); // the first write fails; a resubmission is pending (2 s later)
            Assert.That(publisher.State, Is.EqualTo(PublisherState.Writing));
            publisher.Shutdown();
            Assert.That(worker.Stop(TimeSpan.FromSeconds(5)), Is.True);
            var result = File.Exists(Ready) ? Ready : Written;
            Assert.That(File.Exists(result), Is.True, "the result held in memory reached the outbox before the exit");
            Assert.That(File.ReadAllBytes(result), Is.EqualTo(Bytes));
        }

        // Final review M2 (re-graded): a quit in the frame where a write has just failed (no resubmission scheduled yet) still writes
        // the result held in memory, so a valid outcome is never reported absent.
        [Test]
        public void Shutdown_right_after_a_failed_write_still_writes_the_result()
        {
            var faulting = new FaultingOutboxDirectory(dir);
            faulting.Fail("WriteAndSync", "io", 5, times: 1);
            Make(faulting);
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (publisher.HandleStage != PublicationStage.Failed && watch.Elapsed.TotalSeconds < 5) Thread.Sleep(1); // no Tick: nothing scheduled
            Assert.That(publisher.HandleStage, Is.EqualTo(PublicationStage.Failed));
            publisher.Shutdown();
            Assert.That(worker.Stop(TimeSpan.FromSeconds(5)), Is.True);
            var result = File.Exists(Ready) ? Ready : Written;
            Assert.That(File.Exists(result), Is.True);
            Assert.That(File.ReadAllBytes(result), Is.EqualTo(Bytes));
        }

        [Test]
        public void Only_the_recorded_route_is_ever_called()
        {
            Make();
            transport.Answer("recorded", 503).Answer("recorded", 200, "{}");
            publisher.Begin(Account, Bytes, Hash, clock.Now);
            Run(10);
            Assert.That(transport.Routes.Distinct(), Is.EqualTo(new[] { "recorded" }));
        }
    }
}
