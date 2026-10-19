using System;
using System.Text;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// The production outbox directory (Linux x86_64). Opened once, then every operation is relative to the directory's
    /// descriptor, so a renamed or replaced path can't redirect a write, and names are outbox file names only.
    /// - Open: open(O_RDONLY|O_DIRECTORY|O_CLOEXEC) (following the root-owned symlinks systemd's DynamicUser= layout puts under
    ///   /var/lib), then fstat: a directory, owned by geteuid(), with no group or other write bit.
    /// - Probe/read: openat(O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC), fstat (regular, one link), read to the size.
    /// - Write: openat(O_WRONLY|O_CREAT|O_NOFOLLOW|O_CLOEXEC, 0644), fstat (regular, one link, ours) before anything changes,
    ///   ftruncate, fchmod 0644 (O_CREAT's mode applies only to a new file), write in a loop (partial writes continue), fsync, close.
    /// - Rename: renameat2(RENAME_NOREPLACE), so even a race can't replace a result. Directory sync: fsync(dirfd).
    /// EINTR is retried everywhere except close: Linux releases the descriptor even then, so EINTR from close counts as closed.
    /// A failed write or fsync closes the file and throws; nothing retries an fsync (a failed one may have dropped the pages).
    /// </summary>
    internal sealed class PosixOutboxDirectory : IOutboxDirectory
    {
        const int ORdOnly = 0, OWrOnly = 0x1, OCreat = 0x40, ONonBlock = 0x800, ODirectory = 0x10000, ONoFollow = 0x20000, OCloExec = 0x80000;
        const uint SIfMt = 0xF000, SIfReg = 0x8000, SIfDir = 0x4000, RenameNoReplaceFlag = 1;
        const int FileMode = 0x1A4; // 0644
        const int EINTR = 4, ENOENT = 2, ENOTDIR = 20, ELOOP = 40;

        readonly ILibc libc;
        int dirFd;

        PosixOutboxDirectory(ILibc libc, int dirFd)
        {
            this.libc = libc;
            this.dirFd = dirFd;
        }

        internal static PosixOutboxDirectory Open(string path, ILibc libc)
        {
            if (libc == null) throw new ArgumentNullException(nameof(libc));
            var fd = Retry((out int e) => libc.Open(Nul(path), ORdOnly | ODirectory | OCloExec, out e));
            if (fd.Result < 0) throw new OutboxIOException(fd.Errno == ENOTDIR ? "not_a_directory" : "open_failed", fd.Errno);
            var directory = new PosixOutboxDirectory(libc, (int)fd.Result);
            try
            {
                var status = directory.Stat((int)fd.Result, "open_failed");
                if ((status.Mode & SIfMt) != SIfDir) throw new OutboxIOException("not_a_directory", 0);
                if (status.Uid != libc.GetEuid()) throw new OutboxIOException("wrong_owner", 0);
                if ((status.Mode & 0x12) != 0) throw new OutboxIOException("writable_by_others", 0); // group or other write (0022)
                return directory;
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        }

        public EntryKind Probe(string name)
        {
            var bytes = Name(name);
            var fd = OpenForRead(bytes, out var errno);
            if (fd < 0)
            {
                if (errno == ENOENT) return EntryKind.Absent;
                if (errno == ELOOP) return EntryKind.Unsafe;
                throw new OutboxIOException("open_failed", errno);
            }
            try { return IsSafe(Stat(fd, "open_failed")) ? EntryKind.RegularFile : EntryKind.Unsafe; }
            finally { CloseQuietly(fd); }
        }

        public byte[] ReadRegular(string name, int maxBytes)
        {
            var bytes = Name(name);
            var fd = OpenForRead(bytes, out var errno);
            if (fd < 0) throw new OutboxIOException(errno == ELOOP ? "unsafe_entry" : "read_failed", errno);
            try
            {
                var status = Stat(fd, "read_failed");
                if (!IsSafe(status)) throw new OutboxIOException("unsafe_entry", 0);
                if (status.Size > maxBytes) throw new OutboxIOException("too_large", 0);
                var buffer = new byte[status.Size];
                var total = 0;
                while (total < buffer.Length)
                {
                    var n = Retry((out int e) => libc.Read(fd, buffer, total, buffer.Length - total, out e));
                    if (n.Result < 0) throw new OutboxIOException("read_failed", n.Errno);
                    if (n.Result == 0) throw new OutboxIOException("read_failed", 0); // shorter than fstat said: it changed while read
                    total += (int)n.Result;
                }
                var extra = new byte[1];
                var tail = Retry((out int e) => libc.Read(fd, extra, 0, 1, out e));
                if (tail.Result != 0) throw new OutboxIOException("read_failed", tail.Errno); // longer than fstat said
                return buffer;
            }
            finally { CloseQuietly(fd); }
        }

        public void WriteAndSync(string name, byte[] bytes)
        {
            var nameBytes = Name(name);
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var opened = Retry((out int e) => libc.OpenAt(dirFd, nameBytes, OWrOnly | OCreat | ONoFollow | OCloExec, FileMode, out e));
            if (opened.Result < 0) throw new OutboxIOException(opened.Errno == ELOOP ? "unsafe_entry" : "open_failed", opened.Errno);
            var fd = (int)opened.Result;
            try
            {
                var status = Stat(fd, "open_failed");
                if (!IsSafe(status) || status.Uid != libc.GetEuid()) throw new OutboxIOException("unsafe_entry", 0); // checked before anything changes
                Must(Retry((out int e) => libc.Ftruncate(fd, 0, out e)), "write_failed");
                Must(Retry((out int e) => libc.Fchmod(fd, FileMode, out e)), "write_failed");
                var written = 0;
                while (written < bytes.Length)
                {
                    var n = Retry((out int e) => libc.Write(fd, bytes, written, bytes.Length - written, out e));
                    if (n.Result < 0) throw new OutboxIOException("write_failed", n.Errno);
                    if (n.Result == 0) throw new OutboxIOException("write_failed", 0);
                    written += (int)n.Result;
                }
                Must(Retry((out int e) => libc.Fsync(fd, out e)), "fsync_failed");
            }
            catch
            {
                CloseQuietly(fd);
                throw;
            }
            if (libc.Close(fd, out var closeErrno) < 0 && closeErrno != EINTR) throw new OutboxIOException("close_failed", closeErrno);
        }

        public void RenameNoReplace(string fromName, string toName)
        {
            var from = Name(fromName);
            var to = Name(toName);
            Must(Retry((out int e) => libc.RenameAt2(dirFd, from, dirFd, to, RenameNoReplaceFlag, out e)), "rename_failed");
        }

        public void SyncDirectory() => Must(Retry((out int e) => libc.Fsync(dirFd, out e)), "directory_sync_failed");

        public void Dispose()
        {
            if (dirFd < 0) return;
            libc.Close(dirFd, out _);
            dirFd = -1;
        }

        int OpenForRead(byte[] name, out int errno)
        {
            var r = Retry((out int e) => libc.OpenAt(dirFd, name, ORdOnly | ONoFollow | ONonBlock | OCloExec, 0, out e));
            errno = r.Errno;
            return (int)r.Result;
        }

        FileStatus Stat(int fd, string code)
        {
            FileStatus status = default;
            var r = Retry((out int e) => libc.Fstat(fd, out status, out e));
            if (r.Result < 0) throw new OutboxIOException(code, r.Errno);
            return status;
        }

        static bool IsSafe(FileStatus s) => (s.Mode & SIfMt) == SIfReg && s.Links == 1;

        void CloseQuietly(int fd) => libc.Close(fd, out _);

        static void Must((long Result, int Errno) r, string code)
        {
            if (r.Result < 0) throw new OutboxIOException(code, r.Errno);
        }

        delegate long Call(out int errno);

        /// <summary>Repeats a call while it fails with EINTR; the result and the errno of the last attempt.</summary>
        static (long Result, int Errno) Retry(Call call)
        {
            while (true)
            {
                var r = call(out var errno);
                if (r >= 0 || errno != EINTR) return (r, r < 0 ? errno : 0);
            }
        }

        static byte[] Name(string name)
        {
            if (!OutboxFileNames.IsOutboxName(name)) throw new ArgumentException("Not an outbox file name.", nameof(name));
            return Nul(name);
        }

        static byte[] Nul(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var bytes = Encoding.UTF8.GetBytes(text);
            if (Array.IndexOf(bytes, (byte)0) >= 0) throw new ArgumentException("A path holds no NUL.", nameof(text));
            var result = new byte[bytes.Length + 1];
            Array.Copy(bytes, result, bytes.Length);
            return result;
        }
    }
}
