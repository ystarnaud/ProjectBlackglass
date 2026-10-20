using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// The outbox's dedicated I/O thread (owner ruling D6). The simulation thread captures the immutable result (canonical bytes
    /// and hash), calls Submit, and reads handle status; it never writes a file, calls fsync or waits for the disk. This thread
    /// owns the IOutboxDirectory and runs every file operation, one job at a time: queued durable writes first (§H 1-2), then
    /// publications whose AllowPublication arrived (§H 4). Core has no engine references, so nothing here can touch a Unity
    /// object or API. No backend communication. No automatic retry: a failure is reported, and a resubmit runs the protocol again.
    /// </summary>
    public sealed class OutboxWorker : IDisposable
    {
        readonly object gate = new object();
        readonly List<PublicationHandle> jobs = new List<PublicationHandle>();
        readonly IOutboxDirectory directory;
        readonly OutboxWriter writer;
        readonly Thread thread;
        bool stopping;

        /// <summary>Starts the background thread "blackglass-outbox", which owns <paramref name="directory"/> and is its only closer.</summary>
        public OutboxWorker(IOutboxDirectory directory)
        {
            this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
            writer = new OutboxWriter(directory);
            thread = new Thread(Run) { IsBackground = true, Name = "blackglass-outbox" };
            thread.Start();
        }

        /// <summary>
        /// Queues one participant's durable write and returns at once. Arguments are checked on the caller's thread (account,
        /// 1..65,536 bytes, a sha256: hash) and the bytes copied. An active publication of the same account with the same result
        /// is returned as it is; one with a different result gives a new handle already in Conflict, with no file operation.
        /// </summary>
        public PublicationHandle Submit(string accountId, byte[] canonicalBytes, string manifestHash)
        {
            OutboxFileNames.Tmp(accountId);
            if (canonicalBytes == null || canonicalBytes.Length < 1 || canonicalBytes.Length > OutboxWriter.MaxBytes)
                throw new ArgumentException("A result is 1 to 65,536 bytes.", nameof(canonicalBytes));
            if (!CanonicalIds.IsSha256(manifestHash)) throw new ArgumentException("The manifest hash is not sha256:<hex>.", nameof(manifestHash));
            var copy = (byte[])canonicalBytes.Clone();
            lock (gate)
            {
                if (stopping) throw new InvalidOperationException("The outbox worker is stopped.");
                var active = jobs.LastOrDefault(j => j.AccountId == accountId && j.stage != PublicationStage.Failed && j.stage != PublicationStage.Cancelled);
                if (active != null)
                    return active.SameResult(copy, manifestHash) ? active : new PublicationHandle(gate, accountId, copy, manifestHash, PublicationStage.Conflict);
                var handle = new PublicationHandle(gate, accountId, copy, manifestHash, PublicationStage.Queued);
                jobs.Add(handle);
                Monitor.PulseAll(gate);
                return handle;
            }
        }

        /// <summary>
        /// No new submissions. The thread still completes every queued durable write and every allowed publication, then ends
        /// (closing the directory itself). Waits at most <paramref name="drain"/>; true when the thread has ended.
        /// </summary>
        public bool Stop(TimeSpan drain)
        {
            lock (gate)
            {
                stopping = true;
                Monitor.PulseAll(gate);
            }
            return thread.Join(drain);
        }

        /// <summary>Stop(TimeSpan.Zero). Never closes the directory from this thread: the worker does, after its last call.</summary>
        public void Dispose() => Stop(TimeSpan.Zero);

        void Run()
        {
            try
            {
                while (true)
                {
                    PublicationHandle job;
                    bool write;
                    lock (gate)
                    {
                        while (!TryNext(out job, out write))
                        {
                            if (stopping) return;
                            Monitor.Wait(gate);
                        }
                        job.stage = write ? PublicationStage.Writing : PublicationStage.Publishing;
                    }
                    Execute(job, write);
                }
            }
            finally { directory.Dispose(); } // the only closer of the descriptor, after its last file operation
        }

        bool TryNext(out PublicationHandle job, out bool write)
        {
            job = jobs.FirstOrDefault(j => j.stage == PublicationStage.Queued);
            write = job != null;
            if (job == null) job = jobs.FirstOrDefault(j => j.stage == PublicationStage.Written && j.allowed && !j.cancelRequested);
            return job != null;
        }

        void Execute(PublicationHandle job, bool write)
        {
            try
            {
                if (write)
                {
                    if (HashOf(job.Bytes) != job.ManifestHash) { Finish(job, PublicationStage.Failed, false, "hash_mismatch", 0); return; }
                    var result = writer.WriteDurable(job.AccountId, job.Bytes);
                    switch (result)
                    {
                        case WriteResult.Written:
                        case WriteResult.AlreadyWritten:
                            lock (gate) Finish(job, job.cancelRequested ? PublicationStage.Cancelled : PublicationStage.Written, true, null, 0);
                            break;
                        case WriteResult.AlreadyPublished:
                            Finish(job, PublicationStage.Published, true, null, 0);
                            break;
                        default:
                            Finish(job, PublicationStage.Conflict, false, null, 0);
                            break;
                    }
                }
                else
                {
                    var result = writer.Publish(job.AccountId, job.Bytes);
                    if (result == PublishResult.Published || result == PublishResult.AlreadyPublished) Finish(job, PublicationStage.Published, true, null, 0);
                    else if (result == PublishResult.Conflict) Finish(job, PublicationStage.Conflict, true, null, 0);
                    else Finish(job, PublicationStage.Failed, false, "nothing_written", 0);
                }
            }
            catch (OutboxIOException e)
            {
                Finish(job, PublicationStage.Failed, !write, e.Code, e.Errno); // a publish failure leaves the earlier .written durable
            }
            catch (Exception)
            {
                Finish(job, PublicationStage.Failed, !write, "unexpected", 0); // the thread never dies on a job
            }
        }

        void Finish(PublicationHandle job, PublicationStage stage, bool durable, string code, int errno)
        {
            lock (gate)
            {
                job.stage = stage;
                job.durable = durable;
                job.failureCode = code;
                job.errno = errno;
                Monitor.PulseAll(gate);
            }
        }

        static string HashOf(byte[] bytes)
        {
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(bytes);
            var text = new StringBuilder("sha256:", 71);
            foreach (var b in digest) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }
}
