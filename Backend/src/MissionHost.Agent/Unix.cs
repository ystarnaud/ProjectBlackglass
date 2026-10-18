using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Blackglass.MissionHost.Agent;

/// <summary>
/// The few libc calls the agent needs that .NET doesn't expose: a directory fsync (durable renames), opening a result file
/// without following a symbolic link or blocking on a FIFO, and fstat to prove it is one regular, singly linked file whose
/// size and modification time didn't change while it was read. linux-x64 only (the published runtime, A10-D6): the stat
/// layout below is x86_64's.
/// </summary>
public static class Unix
{
    const int ORdOnly = 0, ONonBlock = 0x800, ODirectory = 0x10000, ONoFollow = 0x20000, OCloExec = 0x80000;
    const uint SIfMt = 0xF000, SIfReg = 0x8000;
    public const int ELoop = 40;

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "fsync")]
    static extern int Fsync(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    static extern int Close(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "fstat")]
    static extern int Fstat(int fd, out Stat buffer);

    [DllImport("libc", EntryPoint = "geteuid")]
    static extern uint GetEuid();

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

    public static bool IsRoot => OperatingSystem.IsLinux() && GetEuid() == 0;

    /// <summary>fsyncs a directory, so a rename inside it is durable.</summary>
    public static void SyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) return; // durability is a Linux concern; other systems only build the agent
        var fd = Open(directory, ORdOnly | ODirectory | OCloExec);
        if (fd < 0) throw new IOException($"Opening the directory for fsync failed (errno {Marshal.GetLastPInvokeError()}).");
        try
        {
            if (Fsync(fd) != 0) throw new IOException($"fsync of the directory failed (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally { Close(fd); }
    }

    /// <summary>The result of reading one outbox file.</summary>
    public abstract record ReadResult
    {
        public sealed record Read(byte[] Bytes) : ReadResult;
        /// <summary>Not a regular file, a symbolic link, more than one link, or too large: never relayed.</summary>
        public sealed record Refused(string Reason) : ReadResult;
        /// <summary>Unreadable or changing while read: a scan that met it is incomplete.</summary>
        public sealed record Failed(string Reason) : ReadResult;
    }

    /// <summary>
    /// Reads a file the agent doesn't own (a mission process's result) without trusting it: O_NOFOLLOW (a symbolic link is
    /// refused, so a process can't point the agent at its own secret or another attempt's file), O_NONBLOCK (a FIFO can't
    /// block it), then fstat: a regular file with exactly one link and at most <paramref name="maxBytes"/>, unchanged in size
    /// and modification time across the read.
    /// </summary>
    public static ReadResult ReadUntrusted(string path, long maxBytes)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The agent reads outbox files on linux-x64 only (the stat layout is x86_64's)."); // security review M11
        var fd = Open(path, ORdOnly | ONoFollow | ONonBlock | OCloExec);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            return errno == ELoop ? new ReadResult.Refused("symbolic_link") : new ReadResult.Failed($"open_errno_{errno}");
        }
        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (Fstat(fd, out var before) != 0) return new ReadResult.Failed($"fstat_errno_{Marshal.GetLastPInvokeError()}");
        if ((before.Mode & SIfMt) != SIfReg) return new ReadResult.Refused("not_a_regular_file");
        if (before.Nlink != 1) return new ReadResult.Refused("hard_linked");
        if (before.Size > maxBytes) return new ReadResult.Refused("too_large");
        var bytes = new byte[before.Size];
        var total = 0;
        try
        {
            while (total < bytes.Length)
            {
                var read = RandomAccess.Read(handle, bytes.AsSpan(total), total);
                if (read == 0) break;
                total += read;
            }
            // Anything past the size fstat gave means the file grew while it was read.
            Span<byte> extra = stackalloc byte[1];
            if (RandomAccess.Read(handle, extra, total) != 0) return new ReadResult.Failed("changed_while_read");
        }
        catch (IOException) { return new ReadResult.Failed("read_error"); }
        if (total != bytes.Length) return new ReadResult.Failed("changed_while_read");
        if (Fstat(fd, out var after) != 0) return new ReadResult.Failed($"fstat_errno_{Marshal.GetLastPInvokeError()}");
        if (after.Size != before.Size || after.MtimeSec != before.MtimeSec || after.MtimeNsec != before.MtimeNsec || after.Ino != before.Ino)
            return new ReadResult.Failed("changed_while_read");
        return new ReadResult.Read(bytes);
    }
}
