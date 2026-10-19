using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Blackglass.Backend.Core;

namespace Blackglass.Tests
{
    /// <summary>
    /// An in-memory stand-in for libc behind ILibc: one directory (DirectoryPath) with named entries and open descriptors, plus
    /// scripted faults (an errno for the next calls of a function, short writes, a symlink, FIFO or doubly linked entry, a
    /// directory with the wrong owner or mode). Lets PosixOutboxDirectory's loops and error paths run on Windows.
    /// </summary>
    internal sealed class FakeLibc : ILibc
    {
        public const string DirectoryPath = "/var/lib/blackglass-outbox/0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";
        public const int EINTR = 4, ENOENT = 2, EIO = 5, EEXIST = 17, ENOTDIR = 20, EINVAL = 22, ELOOP = 40, ENXIO = 6;

        public sealed class Node
        {
            public byte[] Data = new byte[0];
            public bool Symlink, Fifo;
            public ulong Links = 1;
            public uint Permissions = 0x1A4; // 0644
            public uint Uid = 1000;
        }

        sealed class Descriptor
        {
            public string Name; // null: the directory
            public Node Node;
            public long Position;
        }

        public readonly Dictionary<string, Node> Entries = new Dictionary<string, Node>(StringComparer.Ordinal);
        readonly Dictionary<int, Descriptor> descriptors = new Dictionary<int, Descriptor>();
        readonly Dictionary<string, Queue<int>> faults = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        int nextFd = 3;

        public uint DirectoryPermissions = 0x1ED; // 0755
        public uint DirectoryUid = 1000;
        public uint Euid = 1000;
        public bool DirectoryIsAFile;
        /// <summary>Above 0: each write writes at most this many bytes.</summary>
        public int MaxWriteChunk;
        public readonly List<string> Calls = new List<string>();

        public int OpenDescriptors => descriptors.Count;

        /// <summary>The next <paramref name="times"/> calls of <paramref name="function"/> fail with <paramref name="errno"/>.</summary>
        public void Fail(string function, int errno, int times = 1)
        {
            if (!faults.TryGetValue(function, out var queue)) faults[function] = queue = new Queue<int>();
            for (var i = 0; i < times; i++) queue.Enqueue(errno);
        }

        bool Faulted(string function, out int errno)
        {
            Calls.Add(function);
            errno = 0;
            if (!faults.TryGetValue(function, out var queue) || queue.Count == 0) return false;
            errno = queue.Dequeue();
            return true;
        }

        static string Text(byte[] nulTerminated) => Encoding.ASCII.GetString(nulTerminated, 0, Array.IndexOf(nulTerminated, (byte)0));

        public int Open(byte[] path, int flags, out int errno)
        {
            if (Faulted("open", out errno)) return -1;
            if (Text(path) != DirectoryPath) { errno = ENOENT; return -1; }
            if (DirectoryIsAFile && (flags & 0x10000) != 0) { errno = ENOTDIR; return -1; }
            descriptors.Add(nextFd, new Descriptor());
            return nextFd++;
        }

        public int OpenAt(int dirFd, byte[] name, int flags, int mode, out int errno)
        {
            if (Faulted("openat", out errno)) return -1;
            var text = Text(name);
            if (!descriptors.TryGetValue(dirFd, out var dir) || dir.Name != null) { errno = 9; return -1; }
            if (!Entries.TryGetValue(text, out var node))
            {
                if ((flags & 0x40) == 0) { errno = ENOENT; return -1; }
                Entries[text] = node = new Node { Permissions = (uint)mode & 0x1ED };
            }
            else if (node.Symlink && (flags & 0x20000) != 0) { errno = ELOOP; return -1; }
            if ((flags & 0x200) != 0) node.Data = new byte[0];
            descriptors.Add(nextFd, new Descriptor { Name = text, Node = node });
            return nextFd++;
        }

        public long Read(int fd, byte[] buffer, int offset, int count, out int errno)
        {
            if (Faulted("read", out errno)) return -1;
            var open = descriptors[fd];
            var n = (int)Math.Max(0, Math.Min(count, open.Node.Data.Length - open.Position));
            Array.Copy(open.Node.Data, open.Position, buffer, offset, n);
            open.Position += n;
            return n;
        }

        public long Write(int fd, byte[] buffer, int offset, int count, out int errno)
        {
            if (Faulted("write", out errno)) return -1;
            var open = descriptors[fd];
            var n = MaxWriteChunk > 0 ? Math.Min(count, MaxWriteChunk) : count;
            var data = open.Node.Data.ToList();
            while (data.Count < open.Position + n) data.Add(0);
            for (var i = 0; i < n; i++) data[(int)open.Position + i] = buffer[offset + i];
            open.Node.Data = data.ToArray();
            open.Position += n;
            return n;
        }

        public int Fsync(int fd, out int errno)
        {
            if (Faulted(descriptors.TryGetValue(fd, out var o) && o.Name == null ? "fsync-dir" : "fsync", out errno)) return -1;
            return 0;
        }

        public int Close(int fd, out int errno)
        {
            var faulted = Faulted("close", out errno);
            descriptors.Remove(fd); // Linux releases the descriptor even when close reports an error
            return faulted ? -1 : 0;
        }

        public int Fstat(int fd, out FileStatus status, out int errno)
        {
            status = default;
            if (Faulted("fstat", out errno)) return -1;
            var open = descriptors[fd];
            if (open.Name == null)
            {
                status = new FileStatus(1, (DirectoryIsAFile ? 0x8000u : 0x4000u) | DirectoryPermissions, DirectoryUid, 4096);
                return 0;
            }
            var node = open.Node;
            status = new FileStatus(node.Links, (node.Fifo ? 0x1000u : 0x8000u) | node.Permissions, node.Uid, node.Data.Length);
            return 0;
        }

        public int Fchmod(int fd, int mode, out int errno)
        {
            if (Faulted("fchmod", out errno)) return -1;
            descriptors[fd].Node.Permissions = (uint)mode;
            return 0;
        }

        public int Ftruncate(int fd, long length, out int errno)
        {
            if (Faulted("ftruncate", out errno)) return -1;
            var node = descriptors[fd].Node;
            node.Data = node.Data.Take((int)length).ToArray();
            return 0;
        }

        public int RenameAt2(int fromDir, byte[] from, int toDir, byte[] to, uint flags, out int errno)
        {
            if (Faulted("renameat2", out errno)) return -1;
            var source = Text(from);
            var target = Text(to);
            if (flags != 1) { errno = EINVAL; return -1; }
            if (Entries.ContainsKey(target)) { errno = EEXIST; return -1; }
            if (!Entries.TryGetValue(source, out var node)) { errno = ENOENT; return -1; }
            Entries.Remove(source);
            Entries[target] = node;
            return 0;
        }

        public uint GetEuid() => Euid;
    }
}
