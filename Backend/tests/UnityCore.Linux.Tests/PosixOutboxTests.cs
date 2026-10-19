using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Blackglass.Backend.Core;
using Blackglass.MissionHost.Agent;

namespace Blackglass.UnityCore.Linux.Tests;

/// <summary>
/// The Unity core's outbox on Linux with the real libc (owner ruling D8): written by OutboxWorker through PosixOutboxDirectory,
/// then read back by the Stage 10 agent's own Unix.ReadUntrusted and OutboxScanner. Each test works in a fresh directory under
/// BLACKGLASS_TEST_TMP (the runner's /work volume, the VM's own filesystem rather than the container's overlay), with umask 0022.
/// </summary>
public sealed class PosixOutboxTests : IDisposable
{
    const string Account = "JG";
    const string AttemptText = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41";
    static readonly Guid Attempt = Guid.Parse(AttemptText);
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [DllImport("libc.so.6", EntryPoint = "umask")] static extern uint Umask(uint mask);
    [DllImport("libc.so.6", EntryPoint = "symlink", SetLastError = true)] static extern int Symlink([MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("libc.so.6", EntryPoint = "mkfifo", SetLastError = true)] static extern int Mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc.so.6", EntryPoint = "chmod", SetLastError = true)] static extern int Chmod([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc.so.6", EntryPoint = "stat", SetLastError = true)] static extern int StatPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out RawStat buffer);

    [StructLayout(LayoutKind.Sequential)]
    struct RawStat
    {
        public ulong Dev, Ino, Nlink;
        public uint Mode, Uid, Gid;
        int pad0;
        public ulong Rdev;
        public long Size, BlkSize, Blocks;
        public long AtimeSec, AtimeNsec, MtimeSec, MtimeNsec, CtimeSec, CtimeNsec;
        long reserved0, reserved1, reserved2;
    }

    readonly string root;
    readonly string outbox;
    readonly List<OutboxWorker> workers = [];

    public PosixOutboxTests()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "linux-x64 only");
        Umask(0x12); // 0022, as the mission unit's UMask=0022
        var baseDir = Environment.GetEnvironmentVariable("BLACKGLASS_TEST_TMP") ?? Path.GetTempPath();
        root = Path.Combine(baseDir, "bg-outbox-" + Guid.NewGuid().ToString("N"));
        outbox = Path.Combine(root, AttemptText);
        Directory.CreateDirectory(outbox); // 0777 & ~0022 = 0755, as StateDirectoryMode=0755
    }

    public void Dispose()
    {
        foreach (var w in workers) w.Stop(Bound);
        Chmod(outbox, 0x1ED);
        Directory.Delete(root, true);
    }

    static byte[] Vector(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", name));

    static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    IOutboxDirectory OpenServer() => OutboxDirectories.OpenForServer(outbox, root, AttemptText);

    OutboxWorker Worker(IOutboxDirectory directory)
    {
        var w = new OutboxWorker(directory);
        workers.Add(w);
        return w;
    }

    static PublicationStatus WaitFor(PublicationHandle h, PublicationStage stage)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Bound)
        {
            if (h.Status.Stage == stage) return h.Status;
            Thread.Sleep(2);
        }
        throw new TimeoutException($"{h.Status.Stage}, waiting for {stage} ({h.Status.FailureCode})");
    }

    string FileOf(string suffix) => Path.Combine(outbox, Account + suffix);

    RawStat StatOf(string path)
    {
        Assert.Equal(0, StatPath(path, out var s));
        return s;
    }

    [Fact]
    public void A_result_written_and_published_reads_back_through_the_agent()
    {
        var bytes = Vector("minimal.json");
        var worker = Worker(OpenServer());
        var h = worker.Submit(Account, bytes, Hash(bytes));
        WaitFor(h, PublicationStage.Written);

        // The .written: a regular file, one link, 0644, the exact bytes; the agent relays it only after a verified exit.
        var written = Assert.IsType<Unix.ReadResult.Read>(Unix.ReadUntrusted(FileOf(".manifest.written"), 65536));
        Assert.Equal(bytes, written.Bytes);
        var status = StatOf(FileOf(".manifest.written"));
        Assert.Equal(1UL, status.Nlink);
        Assert.Equal(0x81A4u, status.Mode & 0xF1FF); // regular, 0644
        var scanner = new OutboxScanner(root, 65536);
        var running = scanner.Scan(Attempt, [Account], includeWritten: false);
        Assert.True(running.Complete);
        Assert.False(running.Present(Account), "while the process runs, a .written is not relayed");
        var exited = scanner.Scan(Attempt, [Account], includeWritten: true);
        Assert.True(exited.Present(Account));
        Assert.Equal("written", exited.Files.Single().Kind);

        h.AllowPublication();
        WaitFor(h, PublicationStage.Published);
        var ready = scanner.Scan(Attempt, [Account], includeWritten: false);
        Assert.True(ready.Complete);
        Assert.Equal("ready", Assert.Single(ready.Files).Kind);
        Assert.Equal(bytes, ready.Files.Single().Bytes);
        Assert.False(File.Exists(FileOf(".manifest.written")));
        Assert.Equal(1UL, StatOf(FileOf(".manifest.ready")).Nlink);
    }

    [Fact]
    public void A_leftover_tmp_is_ignored_by_the_agent_and_rewritten_by_the_writer()
    {
        File.WriteAllBytes(FileOf(".manifest.tmp"), "{\"partial\":"u8.ToArray());
        var scan = new OutboxScanner(root, 65536).Scan(Attempt, [Account], includeWritten: true);
        Assert.True(scan.Complete);
        Assert.False(scan.Present(Account));
        using var directory = OpenServer();
        var bytes = Vector("minimal.json");
        Assert.Equal(WriteResult.Written, new OutboxWriter(directory).WriteDurable(Account, bytes));
        Assert.Equal(bytes, File.ReadAllBytes(FileOf(".manifest.written")));
    }

    [Fact]
    public void Rename_no_replace_refuses_an_existing_target_and_leaves_it_unchanged()
    {
        using var directory = OpenServer();
        directory.WriteAndSync(Account + ".manifest.tmp", [1, 2, 3]);
        directory.WriteAndSync(Account + ".manifest.written", [9]);
        var e = Assert.Throws<OutboxIOException>(() => directory.RenameNoReplace(Account + ".manifest.tmp", Account + ".manifest.written"));
        Assert.Equal(("rename_failed", 17), (e.Code, e.Errno));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(FileOf(".manifest.written")));
        var bytes = Vector("minimal.json");
        Assert.Equal(WriteResult.Conflict, new OutboxWriter(directory).WriteDurable(Account, bytes));
    }

    [Fact]
    public void A_symlink_and_a_fifo_probe_as_unsafe_without_blocking()
    {
        File.WriteAllBytes(Path.Combine(root, "secret"), [1]);
        Assert.Equal(0, Symlink(Path.Combine(root, "secret"), FileOf(".manifest.written")));
        Assert.Equal(0, Mkfifo(FileOf(".manifest.ready"), 0x1A4));
        using var directory = OpenServer();
        var clock = Stopwatch.StartNew();
        Assert.Equal(EntryKind.Unsafe, directory.Probe(Account + ".manifest.written"));
        Assert.Equal(EntryKind.Unsafe, directory.Probe(Account + ".manifest.ready"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "a FIFO never blocks the probe");
        Assert.Equal("unsafe_entry", Assert.Throws<OutboxIOException>(() => new OutboxWriter(directory).WriteDurable(Account, Vector("minimal.json"))).Code);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(root, "secret"))); // nothing was written through the link
    }

    [Fact]
    public void A_hard_linked_tmp_is_never_truncated()
    {
        var other = Path.Combine(root, "other");
        File.WriteAllBytes(other, [7, 7]);
        Assert.Equal(0, Link(other, FileOf(".manifest.tmp")));
        using var directory = OpenServer();
        Assert.Equal("unsafe_entry", Assert.Throws<OutboxIOException>(() => directory.WriteAndSync(Account + ".manifest.tmp", [1])).Code);
        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(other));
    }

    [DllImport("libc.so.6", EntryPoint = "link", SetLastError = true)] static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [Fact]
    public void The_open_refuses_a_writable_directory_a_file_and_a_bad_location()
    {
        Assert.Equal(0, Chmod(outbox, 0x1FF)); // 0777
        Assert.Equal("writable_by_others", Assert.Throws<OutboxIOException>(() => OpenServer()).Code);
        Assert.Equal(0, Chmod(outbox, 0x1ED));
        var file = Path.Combine(root, "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e42");
        File.WriteAllBytes(file, [1]);
        Assert.Equal("not_a_directory", Assert.Throws<OutboxIOException>(() => OutboxDirectories.OpenForServer(file, root, "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e42")).Code);
        Assert.Equal("wrong_attempt", Assert.Throws<OutboxIOException>(() => OutboxDirectories.OpenForServer(outbox, root, "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e42")).Code);
        Assert.Equal("missing", Assert.Throws<OutboxIOException>(() => OutboxDirectories.OpenForServer(null, root, AttemptText)).Code);
    }

    [Fact]
    public void Partial_writes_and_EINTR_on_every_call_still_give_the_exact_file()
    {
        var libc = new InterruptingLibc(NativeLibc.Instance, maxWriteChunk: 7);
        using var directory = PosixOutboxDirectory.Open(outbox, libc);
        var bytes = Vector("minimal.json");
        var writer = new OutboxWriter(directory);
        Assert.Equal(WriteResult.Written, writer.WriteDurable(Account, bytes));
        Assert.Equal(PublishResult.Published, writer.Publish(Account, bytes));
        Assert.Equal(bytes, File.ReadAllBytes(FileOf(".manifest.ready")));
        Assert.True(libc.Interrupted >= 8, "every kind of call was interrupted once");
        Assert.True(libc.Writes > bytes.Length / 7, "the write was split");
    }

    [Fact]
    public void The_directory_fsync_succeeds_and_a_second_write_is_idempotent()
    {
        using var directory = OpenServer();
        directory.SyncDirectory();
        var bytes = Vector("mismatch.json");
        var writer = new OutboxWriter(directory);
        Assert.Equal(WriteResult.Written, writer.WriteDurable(Account, bytes));
        Assert.Equal(WriteResult.AlreadyWritten, writer.WriteDurable(Account, bytes));
        Assert.Equal(WriteResult.Conflict, writer.WriteDurable(Account, Vector("minimal.json")));
    }

    /// <summary>The real libc, except that the first call of each function fails with EINTR and writes are split.</summary>
    sealed class InterruptingLibc(ILibc inner, int maxWriteChunk) : ILibc
    {
        readonly HashSet<string> seen = [];
        public int Interrupted, Writes;

        bool Interrupt(string function, out int errno)
        {
            errno = 0;
            if (function == "close" || !seen.Add(function)) return false;
            errno = 4;
            Interrupted++;
            return true;
        }

        public int Open(byte[] path, int flags, out int errno) => Interrupt("open", out errno) ? -1 : inner.Open(path, flags, out errno);
        public int OpenAt(int dirFd, byte[] name, int flags, int mode, out int errno) => Interrupt("openat", out errno) ? -1 : inner.OpenAt(dirFd, name, flags, mode, out errno);
        public long Read(int fd, byte[] buffer, int offset, int count, out int errno) => Interrupt("read", out errno) ? -1 : inner.Read(fd, buffer, offset, count, out errno);
        public long Write(int fd, byte[] buffer, int offset, int count, out int errno)
        {
            if (Interrupt("write", out errno)) return -1;
            Writes++;
            return inner.Write(fd, buffer, offset, Math.Min(count, maxWriteChunk), out errno);
        }
        public int Fsync(int fd, out int errno) => Interrupt("fsync", out errno) ? -1 : inner.Fsync(fd, out errno);
        public int Close(int fd, out int errno) => inner.Close(fd, out errno);
        public int Fstat(int fd, out FileStatus status, out int errno)
        {
            status = default;
            return Interrupt("fstat", out errno) ? -1 : inner.Fstat(fd, out status, out errno);
        }
        public int Fchmod(int fd, int mode, out int errno) => Interrupt("fchmod", out errno) ? -1 : inner.Fchmod(fd, mode, out errno);
        public int Ftruncate(int fd, long length, out int errno) => Interrupt("ftruncate", out errno) ? -1 : inner.Ftruncate(fd, length, out errno);
        public int RenameAt2(int fromDir, byte[] from, int toDir, byte[] to, uint flags, out int errno) =>
            Interrupt("renameat2", out errno) ? -1 : inner.RenameAt2(fromDir, from, toDir, to, flags, out errno);
        public uint GetEuid() => inner.GetEuid();
    }
}
