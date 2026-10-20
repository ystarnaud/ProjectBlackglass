using System;

namespace Blackglass.Backend.Core
{
    /// <summary>Absent; a regular, singly linked file; or Unsafe: a symbolic link, a non-regular file, or more than one hard link.</summary>
    public enum EntryKind { Absent, RegularFile, Unsafe }

    /// <summary>
    /// One approved outbox directory, addressed by outbox file names only (never a path): every method refuses a name that fails
    /// OutboxFileNames.IsOutboxName with an ArgumentException before touching anything, so no operation can leave the directory.
    /// Not thread-safe: the OutboxWorker's thread is its only user, and only that thread disposes it.
    /// </summary>
    public interface IOutboxDirectory : IDisposable
    {
        EntryKind Probe(string name);

        /// <summary>A regular, singly linked file of at most <paramref name="maxBytes"/>; anything else is an OutboxIOException.</summary>
        byte[] ReadRegular(string name, int maxBytes);

        /// <summary>Create or truncate (never following a symbolic link), mode 0644, write everything, fsync the file, close.</summary>
        void WriteAndSync(string name, byte[] bytes);

        /// <summary>One atomic rename that never replaces an existing name (an existing target is an OutboxIOException).</summary>
        void RenameNoReplace(string fromName, string toName);

        /// <summary>fsync the directory itself, so a rename inside it is durable.</summary>
        void SyncDirectory();
    }
}
