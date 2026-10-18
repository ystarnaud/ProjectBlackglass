using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Blackglass.MissionHost.CredentialBroker;

/// <summary>
/// The libc calls the broker needs that .NET doesn't expose: opening without following links (O_NOFOLLOW, O_PATH for an lstat
/// that also sees links), fstat for owner, mode and link count, flock, fsync, and the connecting peer's uid (SO_PEERCRED).
/// linux-x64 only: the stat layout is x86_64's, and every entry point refuses another platform.
/// </summary>
internal static class Native
{
    const int ORdOnly = 0, OWrOnly = 1, OCreat = 0x40, OExcl = 0x80, ONonBlock = 0x800, ODirectory = 0x10000, ONoFollow = 0x20000,
        OCloExec = 0x80000, OPath = 0x200000;
    const uint SIfMt = 0xF000, SIfReg = 0x8000, SIfDir = 0x4000;
    const int LockEx = 2, SolSocket = 1, SoPeerCred = 17;
    public const int ENoEnt = 2, EExist = 17;

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    static extern int Close(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "fstat")]
    static extern int Fstat(int fd, out Stat buffer);

    [DllImport("libc", SetLastError = true, EntryPoint = "fsync")]
    static extern int Fsync(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "flock")]
    static extern int Flock(int fd, int operation);

    [DllImport("libc", SetLastError = true, EntryPoint = "mkdir")]
    static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    static extern int LinkNative([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);

    [DllImport("libc", SetLastError = true, EntryPoint = "mkfifo")]
    static extern int MkFifoNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "lchown")]
    static extern int LChown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid);

    [DllImport("libc", SetLastError = true, EntryPoint = "getsockopt")]
    static extern int GetSockOpt(int fd, int level, int name, out UCred value, ref uint length);

    [DllImport("libc", EntryPoint = "geteuid")]
    static extern uint GetEuid();

    [DllImport("libc", EntryPoint = "umask")]
    static extern uint Umask(uint mask);

    /// <summary>struct stat on x86_64 Linux (144 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Stat
    {
        public ulong Dev, Ino, Nlink;
        public uint Mode, Uid, Gid;
        int pad0;
        public ulong Rdev;
        public long Size, BlkSize, Blocks;
        public long AtimeSec, AtimeNsec, MtimeSec, MtimeNsec, CtimeSec, CtimeNsec;
        long reserved0, reserved1, reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UCred
    {
        public int Pid;
        public uint Uid, Gid;
    }

    /// <summary>What lstat reports about one name: the entry itself, never a link's target.</summary>
    public readonly record struct Entry(uint Mode, uint Uid, ulong Nlink, long Size)
    {
        public bool IsRegular => (Mode & SIfMt) == SIfReg;
        public bool IsDirectory => (Mode & SIfMt) == SIfDir;
        /// <summary>No group or other permission bit at all.</summary>
        public bool Private => (Mode & 0x3F) == 0;
    }

    static void RequireLinuxX64()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The credential broker runs on linux-x64 only (the stat layout is x86_64's).");
    }

    public static uint EffectiveUid { get { RequireLinuxX64(); return GetEuid(); } }

    /// <summary>Nothing the broker creates is ever readable by anyone else, whatever umask it inherits.</summary>
    public static void RestrictUmask() { RequireLinuxX64(); Umask(0x3F); }

    /// <summary>lstat through O_PATH|O_NOFOLLOW: a link is reported as a link, never followed. Null when the name is absent
    /// (<paramref name="errno"/> says why).</summary>
    public static Entry? LStat(string path) => LStat(path, out _);

    public static Entry? LStat(string path, out int errno)
    {
        RequireLinuxX64();
        errno = 0;
        var fd = Open(path, OPath | ONoFollow | OCloExec, 0);
        if (fd < 0) { errno = Marshal.GetLastPInvokeError(); return null; }
        try
        {
            if (Fstat(fd, out var s) != 0) { errno = Marshal.GetLastPInvokeError(); return null; }
            return new Entry(s.Mode, s.Uid, s.Nlink, s.Size);
        }
        finally { Close(fd); }
    }

    /// <summary>A directory opened without following a link, for flock and fsync; its fstat proves what was opened.</summary>
    public static SafeFileHandle? OpenDirectory(string path, out Entry entry)
    {
        RequireLinuxX64();
        entry = default;
        var fd = Open(path, ORdOnly | ODirectory | ONoFollow | OCloExec, 0);
        if (fd < 0) return null;
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (Fstat(fd, out var s) != 0) { handle.Dispose(); return null; }
        entry = new Entry(s.Mode, s.Uid, s.Nlink, s.Size);
        return handle;
    }

    public static void LockExclusive(SafeFileHandle directory)
    {
        if (Flock((int)directory.DangerousGetHandle(), LockEx) != 0) throw new IOException($"flock failed (errno {Marshal.GetLastPInvokeError()}).");
    }

    public static void Sync(SafeFileHandle handle)
    {
        if (Fsync((int)handle.DangerousGetHandle()) != 0) throw new IOException($"fsync failed (errno {Marshal.GetLastPInvokeError()}).");
    }

    /// <summary>mkdir with the exact mode; false with errno when the name exists (as anything) or can't be created.</summary>
    public static bool MakeDirectory(string path, uint mode, out int errno)
    {
        RequireLinuxX64();
        errno = 0;
        if (Mkdir(path, mode) == 0) return true;
        errno = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>Reads a small file without following a link or blocking on a FIFO: null unless it is one regular, singly linked,
    /// private file of the expected owner, no larger than <paramref name="maxBytes"/>.</summary>
    public static byte[]? ReadPrivate(string path, uint owner, int maxBytes)
    {
        RequireLinuxX64();
        var fd = Open(path, ORdOnly | ONoFollow | ONonBlock | OCloExec, 0);
        if (fd < 0) return null;
        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (Fstat(fd, out var s) != 0) return null;
        var entry = new Entry(s.Mode, s.Uid, s.Nlink, s.Size);
        if (!entry.IsRegular || entry.Nlink != 1 || entry.Uid != owner || !entry.Private || entry.Size > maxBytes) return null;
        var bytes = new byte[entry.Size];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(total), total);
            if (read == 0) return null;
            total += read;
        }
        return bytes;
    }

    /// <summary>Creates a new file exclusively (never through an existing name or link) with the exact mode, writes it and fsyncs it.</summary>
    public static void WriteNew(string path, ReadOnlySpan<byte> bytes, uint mode, Action? afterCreate = null)
    {
        RequireLinuxX64();
        var fd = Open(path, OWrOnly | OCreat | OExcl | ONoFollow | OCloExec, mode);
        if (fd < 0) throw new IOException($"Creating the file failed (errno {Marshal.GetLastPInvokeError()}).");
        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        afterCreate?.Invoke();
        RandomAccess.Write(handle, bytes, 0);
        Sync(handle);
    }

    /// <summary>The uid of the process at the other end of a connected Unix socket.</summary>
    public static uint? PeerUid(int socketFd)
    {
        RequireLinuxX64();
        var length = (uint)Marshal.SizeOf<UCred>();
        return GetSockOpt(socketFd, SolSocket, SoPeerCred, out var cred, ref length) == 0 && length == Marshal.SizeOf<UCred>() ? cred.Uid : null;
    }

    // Test helpers (hostile layouts): a second hard link, a FIFO, another owner.
    public static void Link(string from, string to) { if (LinkNative(from, to) != 0) throw new IOException($"link failed ({Marshal.GetLastPInvokeError()})."); }
    public static void MkFifo(string path) { if (MkFifoNative(path, 0x180) != 0) throw new IOException($"mkfifo failed ({Marshal.GetLastPInvokeError()})."); }
    public static void Chown(string path, uint uid, uint gid) { if (LChown(path, uid, gid) != 0) throw new IOException($"lchown failed ({Marshal.GetLastPInvokeError()})."); }
}
