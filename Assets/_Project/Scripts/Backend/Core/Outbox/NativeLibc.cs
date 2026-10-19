using System;
using System.Runtime.InteropServices;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// glibc on Linux x86_64 (the mission VM is Ubuntu 24.04, glibc 2.39): "libc.so.6" named explicitly, so Mono and CoreCLR
    /// resolve it alike without a dllmap. fstat needs glibc 2.33 and renameat2 2.28. The struct stat layout is x86_64's (as in
    /// the agent's Unix.cs). openat's mode is passed as a fourth integer, which the x86_64 variadic convention accepts. No
    /// unsafe code: buffers are pinned with GCHandle. Unity's Linux Mono player must still prove these calls in Stage 12
    /// (-outbox-selftest on the mission VM) before a real result depends on them.
    /// </summary>
    internal sealed class NativeLibc : ILibc
    {
        public static readonly NativeLibc Instance = new NativeLibc();

        const string Lib = "libc.so.6";

        [StructLayout(LayoutKind.Sequential)]
        struct Stat
        {
            public ulong Dev, Ino, Nlink;
            public uint Mode, Uid, Gid;
            public int Pad0;
            public ulong Rdev;
            public long Size, BlkSize, Blocks;
            public long AtimeSec, AtimeNsec, MtimeSec, MtimeNsec, CtimeSec, CtimeNsec;
            public long Reserved0, Reserved1, Reserved2;
        }

        [DllImport(Lib, EntryPoint = "open", SetLastError = true)] static extern int open(byte[] path, int flags);
        [DllImport(Lib, EntryPoint = "openat", SetLastError = true)] static extern int openat(int dirfd, byte[] name, int flags, int mode);
        [DllImport(Lib, EntryPoint = "read", SetLastError = true)] static extern IntPtr read(int fd, IntPtr buffer, UIntPtr count);
        [DllImport(Lib, EntryPoint = "write", SetLastError = true)] static extern IntPtr write(int fd, IntPtr buffer, UIntPtr count);
        [DllImport(Lib, EntryPoint = "fsync", SetLastError = true)] static extern int fsync(int fd);
        [DllImport(Lib, EntryPoint = "close", SetLastError = true)] static extern int close(int fd);
        [DllImport(Lib, EntryPoint = "fstat", SetLastError = true)] static extern int fstat(int fd, out Stat buffer);
        [DllImport(Lib, EntryPoint = "fchmod", SetLastError = true)] static extern int fchmod(int fd, uint mode);
        [DllImport(Lib, EntryPoint = "ftruncate", SetLastError = true)] static extern int ftruncate(int fd, long length);
        [DllImport(Lib, EntryPoint = "renameat2", SetLastError = true)] static extern int renameat2(int olddirfd, byte[] oldpath, int newdirfd, byte[] newpath, uint flags);
        [DllImport(Lib, EntryPoint = "geteuid")] static extern uint geteuid();

        NativeLibc() { }

        static int Errno(long result) => result < 0 ? Marshal.GetLastWin32Error() : 0;

        public int Open(byte[] path, int flags, out int errno) { var r = open(path, flags); errno = Errno(r); return r; }

        public int OpenAt(int dirFd, byte[] name, int flags, int mode, out int errno) { var r = openat(dirFd, name, flags, mode); errno = Errno(r); return r; }

        public long Read(int fd, byte[] buffer, int offset, int count, out int errno) => Pinned(buffer, offset, count, (p, n) => read(fd, p, n), out errno);

        public long Write(int fd, byte[] buffer, int offset, int count, out int errno) => Pinned(buffer, offset, count, (p, n) => write(fd, p, n), out errno);

        static long Pinned(byte[] buffer, int offset, int count, Func<IntPtr, UIntPtr, IntPtr> call, out int errno)
        {
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var r = (long)call(IntPtr.Add(handle.AddrOfPinnedObject(), offset), (UIntPtr)(uint)count);
                errno = Errno(r);
                return r;
            }
            finally { handle.Free(); }
        }

        public int Fsync(int fd, out int errno) { var r = fsync(fd); errno = Errno(r); return r; }

        public int Close(int fd, out int errno) { var r = close(fd); errno = Errno(r); return r; }

        public int Fstat(int fd, out FileStatus status, out int errno)
        {
            var r = fstat(fd, out var s);
            errno = Errno(r);
            status = new FileStatus(s.Nlink, s.Mode, s.Uid, s.Size);
            return r;
        }

        public int Fchmod(int fd, int mode, out int errno) { var r = fchmod(fd, (uint)mode); errno = Errno(r); return r; }

        public int Ftruncate(int fd, long length, out int errno) { var r = ftruncate(fd, length); errno = Errno(r); return r; }

        public int RenameAt2(int fromDir, byte[] from, int toDir, byte[] to, uint flags, out int errno)
        {
            var r = renameat2(fromDir, from, toDir, to, flags);
            errno = Errno(r);
            return r;
        }

        public uint GetEuid() => geteuid();
    }
}
