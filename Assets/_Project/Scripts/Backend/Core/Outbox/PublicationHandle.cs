using System;

namespace Blackglass.Backend.Core
{
    public enum PublicationStage
    {
        /// <summary>Submitted; nothing written yet.</summary>
        Queued,
        Writing,
        /// <summary>Durable as .written; waiting for AllowPublication (Stage 13, once the recorded-notice attempt has ended).</summary>
        Written,
        Publishing,
        /// <summary>.ready: relayable by the agent at once.</summary>
        Published,
        /// <summary>A different result exists for this participant; nothing was changed.</summary>
        Conflict,
        Failed,
        /// <summary>Cancelled: never published. Durable says whether a .written was completed first (it stays for the agent).</summary>
        Cancelled,
    }

    /// <summary>A snapshot of one publication, safe to read from the simulation thread.</summary>
    public sealed class PublicationStatus
    {
        internal PublicationStatus(PublicationStage stage, bool durable, bool allowed, string failureCode, int errno)
        {
            Stage = stage;
            Durable = durable;
            PublicationAllowed = allowed;
            FailureCode = failureCode;
            Errno = errno;
        }

        public PublicationStage Stage { get; }
        /// <summary>A complete .written (or .ready) exists and its directory fsync succeeded.</summary>
        public bool Durable { get; }
        public bool PublicationAllowed { get; }
        /// <summary>Set only when Failed: a bounded word (OutboxIOException.Code, "hash_mismatch", "nothing_written", "unexpected").</summary>
        public string FailureCode { get; }
        public int Errno { get; }
    }

    /// <summary>
    /// One participant's publication, shared by the simulation thread and the I/O worker. Every member takes only the
    /// worker's short lock (never held across I/O), so none of them waits for the disk.
    /// </summary>
    public sealed class PublicationHandle
    {
        readonly object gate;
        readonly byte[] bytes;

        internal PublicationStage stage;
        internal bool durable, allowed, cancelRequested;
        internal string failureCode;
        internal int errno;

        internal PublicationHandle(object gate, string accountId, byte[] bytes, string manifestHash, PublicationStage stage)
        {
            this.gate = gate;
            this.bytes = bytes;
            AccountId = accountId;
            ManifestHash = manifestHash;
            this.stage = stage;
        }

        public string AccountId { get; }
        /// <summary>What Stage 13 sends to recorded, once the stage is Written (the hash must name a durable result).</summary>
        public string ManifestHash { get; }

        internal byte[] Bytes => bytes;

        internal bool SameResult(byte[] other, string hash) =>
            hash == ManifestHash && other.Length == bytes.Length && System.Linq.Enumerable.SequenceEqual(other, bytes);

        public PublicationStatus Status
        {
            get { lock (gate) return new PublicationStatus(stage, durable, allowed, failureCode, errno); }
        }

        /// <summary>
        /// Stage 13: the recorded-notice attempt has ended (any answer, or its time bound), so publication may advance. Before
        /// the write completes, publication follows it. Idempotent.
        /// </summary>
        public void AllowPublication()
        {
            lock (gate)
            {
                allowed = true;
                System.Threading.Monitor.PulseAll(gate);
            }
        }

        /// <summary>
        /// Queued: becomes Cancelled with nothing written. Writing: the durable write finishes (never half), then Cancelled.
        /// Written: Cancelled, never published (its .written stays for the agent). Publishing and the terminal stages: no effect.
        /// Never deletes anything. Idempotent.
        /// </summary>
        public void Cancel()
        {
            lock (gate)
            {
                cancelRequested = true;
                if (stage == PublicationStage.Queued || stage == PublicationStage.Written) stage = PublicationStage.Cancelled;
                System.Threading.Monitor.PulseAll(gate);
            }
        }
    }
}
