using System;
using System.Linq;

namespace Blackglass.Backend.Core
{
    public enum WriteResult { Written, AlreadyWritten, AlreadyPublished, Conflict }

    public enum PublishResult { Published, AlreadyPublished, NothingWritten, Conflict }

    /// <summary>
    /// The §H protocol for one participant over one directory, synchronous: only the OutboxWorker's thread (and tests) call it.
    /// A durable result is never replaced, truncated or deleted: identical bytes are a no-op (after another directory fsync,
    /// covering a retry after a failed one), different bytes are a Conflict. A leftover .tmp is rewritten, never promoted.
    /// No backend communication: §H step 3 (the recorded notice) belongs to Stage 13, between WriteDurable and Publish.
    /// </summary>
    public sealed class OutboxWriter
    {
        public const int MaxBytes = 65536;

        readonly IOutboxDirectory directory;

        public OutboxWriter(IOutboxDirectory directory) => this.directory = directory ?? throw new ArgumentNullException(nameof(directory));

        /// <summary>§H 1-2: .tmp written and fsynced, renamed to .written, the directory fsynced.</summary>
        public WriteResult WriteDurable(string accountId, byte[] canonicalBytes)
        {
            Check(accountId, canonicalBytes);
            var tmp = OutboxFileNames.Tmp(accountId);
            var written = OutboxFileNames.Written(accountId);
            var ready = Existing(OutboxFileNames.Ready(accountId), canonicalBytes);
            if (ready == true) { directory.SyncDirectory(); return WriteResult.AlreadyPublished; }
            if (ready == false) return WriteResult.Conflict;
            var durable = Existing(written, canonicalBytes);
            if (durable == true) { directory.SyncDirectory(); return WriteResult.AlreadyWritten; }
            if (durable == false) return WriteResult.Conflict;
            if (directory.Probe(tmp) == EntryKind.Unsafe) throw new OutboxIOException("unsafe_entry", 0);
            directory.WriteAndSync(tmp, canonicalBytes);
            directory.RenameNoReplace(tmp, written);
            directory.SyncDirectory();
            return WriteResult.Written;
        }

        /// <summary>§H 4: .written renamed to .ready, the directory fsynced. The bytes identify the result it may publish.</summary>
        public PublishResult Publish(string accountId, byte[] canonicalBytes)
        {
            Check(accountId, canonicalBytes);
            var written = OutboxFileNames.Written(accountId);
            var ready = OutboxFileNames.Ready(accountId);
            var published = Existing(ready, canonicalBytes);
            if (published == true) { directory.SyncDirectory(); return PublishResult.AlreadyPublished; }
            if (published == false) return PublishResult.Conflict;
            var durable = Existing(written, canonicalBytes);
            if (durable == null) return PublishResult.NothingWritten;
            if (durable == false) return PublishResult.Conflict;
            directory.RenameNoReplace(written, ready);
            directory.SyncDirectory();
            return PublishResult.Published;
        }

        /// <summary>null: absent; true: a regular file with these bytes; false: a regular file with other bytes. Unsafe throws.</summary>
        bool? Existing(string name, byte[] bytes)
        {
            switch (directory.Probe(name))
            {
                case EntryKind.Absent: return null;
                case EntryKind.Unsafe: throw new OutboxIOException("unsafe_entry", 0);
                default: return directory.ReadRegular(name, MaxBytes).SequenceEqual(bytes);
            }
        }

        static void Check(string accountId, byte[] bytes)
        {
            OutboxFileNames.Tmp(accountId); // ArgumentException unless canonical Base58
            if (bytes == null || bytes.Length < 1 || bytes.Length > MaxBytes) throw new ArgumentException("A result is 1 to 65,536 bytes.", nameof(bytes));
        }
    }
}
