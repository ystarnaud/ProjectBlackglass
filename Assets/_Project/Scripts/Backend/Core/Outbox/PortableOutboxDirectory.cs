using System;
using System.IO;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// Tests and Editor tools only, never a real outbox: FileStream with Flush(true), File.Move, and a SyncDirectory that does
    /// nothing (directory durability is a Linux concern, proven by PosixOutboxDirectory). Same name rules as the real one.
    /// </summary>
    internal sealed class PortableOutboxDirectory : IOutboxDirectory
    {
        readonly string path;

        internal PortableOutboxDirectory(string path) => this.path = path;

        string Full(string name)
        {
            if (!OutboxFileNames.IsOutboxName(name)) throw new ArgumentException("Not an outbox file name.", nameof(name));
            return Path.Combine(path, name);
        }

        public EntryKind Probe(string name)
        {
            var full = Full(name);
            if (Directory.Exists(full)) return EntryKind.Unsafe;
            if (!File.Exists(full)) return EntryKind.Absent;
            return (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 ? EntryKind.Unsafe : EntryKind.RegularFile;
        }

        public byte[] ReadRegular(string name, int maxBytes)
        {
            if (Probe(name) != EntryKind.RegularFile) throw new OutboxIOException("unsafe_entry", 0);
            var full = Full(name);
            if (new FileInfo(full).Length > maxBytes) throw new OutboxIOException("too_large", 0);
            return File.ReadAllBytes(full);
        }

        public void WriteAndSync(string name, byte[] bytes)
        {
            if (Probe(name) == EntryKind.Unsafe) throw new OutboxIOException("unsafe_entry", 0);
            using (var stream = new FileStream(Full(name), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        public void RenameNoReplace(string fromName, string toName)
        {
            var from = Full(fromName);
            var to = Full(toName);
            if (File.Exists(to) || Directory.Exists(to)) throw new OutboxIOException("rename_failed", 17);
            if (!File.Exists(from)) throw new OutboxIOException("rename_failed", 2);
            File.Move(from, to);
        }

        public void SyncDirectory() { }

        public void Dispose() { }
    }
}
