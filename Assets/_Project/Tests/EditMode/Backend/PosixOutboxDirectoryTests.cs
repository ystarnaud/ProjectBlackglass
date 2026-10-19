using System;
using System.Linq;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>PosixOutboxDirectory's syscall loops and error paths over FakeLibc (the real libc runs in UnityCore.Linux.Tests).</summary>
    public sealed class PosixOutboxDirectoryTests
    {
        const string Tmp = "JG.manifest.tmp", Written = "JG.manifest.written", Ready = "JG.manifest.ready";

        FakeLibc libc;

        [SetUp]
        public void SetUp() => libc = new FakeLibc();

        PosixOutboxDirectory Open() => PosixOutboxDirectory.Open(FakeLibc.DirectoryPath, libc);

        static string CodeOf(TestDelegate action) => Assert.Throws<OutboxIOException>(action).Code;

        [Test]
        public void The_open_checks_the_directory_and_closes_on_refusal()
        {
            libc.DirectoryUid = 1001;
            Assert.That(CodeOf(() => Open()), Is.EqualTo("wrong_owner"));
            libc.DirectoryUid = 1000;
            libc.DirectoryPermissions = 0x1FD; // 0775
            Assert.That(CodeOf(() => Open()), Is.EqualTo("writable_by_others"));
            libc.DirectoryPermissions = 0x1EF; // 0757
            Assert.That(CodeOf(() => Open()), Is.EqualTo("writable_by_others"));
            libc.DirectoryPermissions = 0x1ED;
            libc.DirectoryIsAFile = true;
            Assert.That(CodeOf(() => Open()), Is.EqualTo("not_a_directory"));
            Assert.That(libc.OpenDescriptors, Is.Zero);
            libc.DirectoryIsAFile = false;
            Assert.That(CodeOf(() => PosixOutboxDirectory.Open("/elsewhere", libc)), Is.EqualTo("open_failed"));
            using (Open()) Assert.That(libc.OpenDescriptors, Is.EqualTo(1));
            Assert.That(libc.OpenDescriptors, Is.Zero, "Dispose closes the directory");
        }

        [Test]
        public void A_write_split_into_partial_writes_is_exact()
        {
            libc.MaxWriteChunk = 3;
            var bytes = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();
            using (var d = Open()) d.WriteAndSync(Tmp, bytes);
            Assert.That(libc.Entries[Tmp].Data, Is.EqualTo(bytes));
            Assert.That(libc.Calls.Count(c => c == "write"), Is.EqualTo(4));
            Assert.That(libc.Entries[Tmp].Permissions, Is.EqualTo(0x1A4u), "0644");
            Assert.That(libc.OpenDescriptors, Is.Zero);
        }

        [Test]
        public void EINTR_is_retried_except_on_close()
        {
            foreach (var f in new[] { "open", "openat", "write", "fsync", "fsync-dir", "renameat2", "fstat", "fchmod", "ftruncate", "read" }) libc.Fail(f, FakeLibc.EINTR);
            libc.Fail("close", FakeLibc.EINTR);
            using (var d = Open())
            {
                d.WriteAndSync(Tmp, new byte[] { 1, 2, 3 });
                d.RenameNoReplace(Tmp, Written);
                d.SyncDirectory();
                Assert.That(d.ReadRegular(Written, 100), Is.EqualTo(new byte[] { 1, 2, 3 }));
            }
            Assert.That(libc.Calls.Count(c => c == "close"), Is.EqualTo(3), "close with EINTR counts as closed: never retried");
        }

        [Test]
        public void A_failed_fsync_closes_and_throws()
        {
            libc.Fail("fsync", FakeLibc.EIO);
            using (var d = Open())
            {
                var e = Assert.Throws<OutboxIOException>(() => d.WriteAndSync(Tmp, new byte[] { 1 }));
                Assert.That((e.Code, e.Errno), Is.EqualTo(("fsync_failed", FakeLibc.EIO)));
                Assert.That(libc.OpenDescriptors, Is.EqualTo(1), "only the directory is still open");
            }
        }

        [Test]
        public void A_failed_close_or_write_or_directory_sync_throws()
        {
            using (var d = Open())
            {
                libc.Fail("close", FakeLibc.EIO);
                Assert.That(CodeOf(() => d.WriteAndSync(Tmp, new byte[] { 1 })), Is.EqualTo("close_failed"));
                libc.Fail("write", FakeLibc.EIO);
                Assert.That(CodeOf(() => d.WriteAndSync(Tmp, new byte[] { 1 })), Is.EqualTo("write_failed"));
                libc.Fail("fsync-dir", FakeLibc.EIO);
                Assert.That(CodeOf(() => d.SyncDirectory()), Is.EqualTo("directory_sync_failed"));
            }
        }

        [Test]
        public void A_rename_never_replaces_and_fails_closed()
        {
            using (var d = Open())
            {
                d.WriteAndSync(Tmp, new byte[] { 1 });
                d.WriteAndSync(Written, new byte[] { 2 });
                var e = Assert.Throws<OutboxIOException>(() => d.RenameNoReplace(Tmp, Written));
                Assert.That((e.Code, e.Errno), Is.EqualTo(("rename_failed", FakeLibc.EEXIST)));
                Assert.That(libc.Entries[Written].Data, Is.EqualTo(new byte[] { 2 }));
                libc.Fail("renameat2", FakeLibc.EINVAL);
                Assert.That(Assert.Throws<OutboxIOException>(() => d.RenameNoReplace(Tmp, Ready)).Errno, Is.EqualTo(FakeLibc.EINVAL));
                Assert.That(libc.Entries.ContainsKey(Ready), Is.False);
            }
        }

        [Test]
        public void Unsafe_entries_probe_as_unsafe_and_are_never_written_through()
        {
            libc.Entries[Written] = new FakeLibc.Node { Symlink = true };
            libc.Entries[Ready] = new FakeLibc.Node { Fifo = true };
            libc.Entries[Tmp] = new FakeLibc.Node { Links = 2, Data = new byte[] { 9 } };
            using (var d = Open())
            {
                Assert.That(d.Probe(Written), Is.EqualTo(EntryKind.Unsafe));
                Assert.That(d.Probe(Ready), Is.EqualTo(EntryKind.Unsafe));
                Assert.That(d.Probe(Tmp), Is.EqualTo(EntryKind.Unsafe));
                Assert.That(d.Probe("JH.manifest.tmp"), Is.EqualTo(EntryKind.Absent));
                Assert.That(CodeOf(() => d.WriteAndSync(Tmp, new byte[] { 1 })), Is.EqualTo("unsafe_entry"));
                Assert.That(libc.Entries[Tmp].Data, Is.EqualTo(new byte[] { 9 }), "a doubly linked file is never truncated");
                Assert.That(CodeOf(() => d.WriteAndSync(Written, new byte[] { 1 })), Is.EqualTo("unsafe_entry"));
                Assert.That(CodeOf(() => d.ReadRegular(Ready, 100)), Is.EqualTo("unsafe_entry"));
            }
            Assert.That(libc.OpenDescriptors, Is.Zero);
        }

        [Test]
        public void Another_open_errno_is_a_failure_never_absent()
        {
            using (var d = Open())
            {
                libc.Fail("openat", FakeLibc.ENXIO);
                Assert.That(CodeOf(() => d.Probe(Tmp)), Is.EqualTo("open_failed"));
            }
        }

        [Test]
        public void A_zero_byte_write_is_a_failure()
        {
            var zero = new ZeroWriteLibc(libc);
            using (var d = PosixOutboxDirectory.Open(FakeLibc.DirectoryPath, zero))
                Assert.That(CodeOf(() => d.WriteAndSync(Tmp, new byte[] { 1, 2 })), Is.EqualTo("write_failed"));
        }

        [Test]
        public void A_read_of_a_file_larger_than_allowed_is_refused()
        {
            libc.Entries[Written] = new FakeLibc.Node { Data = new byte[11] };
            using (var d = Open()) Assert.That(CodeOf(() => d.ReadRegular(Written, 10)), Is.EqualTo("too_large"));
        }

        [TestCase("../x.manifest.tmp")]
        [TestCase("a/b")]
        [TestCase("")]
        [TestCase("1JG.manifest.tmp")]
        [TestCase("JG.manifest.other")]
        [TestCase("JG.manifest.tmp\0")]
        public void Every_method_refuses_a_name_outside_the_contract_before_any_call(string name)
        {
            using (var d = Open())
            {
                var before = libc.Calls.Count;
                Assert.Throws<ArgumentException>(() => d.Probe(name));
                Assert.Throws<ArgumentException>(() => d.ReadRegular(name, 1));
                Assert.Throws<ArgumentException>(() => d.WriteAndSync(name, new byte[] { 1 }));
                Assert.Throws<ArgumentException>(() => d.RenameNoReplace(name, Written));
                Assert.Throws<ArgumentException>(() => d.RenameNoReplace(Tmp, name));
                Assert.That(libc.Calls.Count, Is.EqualTo(before));
            }
        }

        /// <summary>FakeLibc, except that write reports 0 bytes written.</summary>
        sealed class ZeroWriteLibc : ILibc
        {
            readonly FakeLibc inner;
            public ZeroWriteLibc(FakeLibc inner) => this.inner = inner;
            public int Open(byte[] path, int flags, out int errno) => inner.Open(path, flags, out errno);
            public int OpenAt(int dirFd, byte[] name, int flags, int mode, out int errno) => inner.OpenAt(dirFd, name, flags, mode, out errno);
            public long Read(int fd, byte[] buffer, int offset, int count, out int errno) => inner.Read(fd, buffer, offset, count, out errno);
            public long Write(int fd, byte[] buffer, int offset, int count, out int errno) { errno = 0; return 0; }
            public int Fsync(int fd, out int errno) => inner.Fsync(fd, out errno);
            public int Close(int fd, out int errno) => inner.Close(fd, out errno);
            public int Fstat(int fd, out FileStatus status, out int errno) => inner.Fstat(fd, out status, out errno);
            public int Fchmod(int fd, int mode, out int errno) => inner.Fchmod(fd, mode, out errno);
            public int Ftruncate(int fd, long length, out int errno) => inner.Ftruncate(fd, length, out errno);
            public int RenameAt2(int fromDir, byte[] from, int toDir, byte[] to, uint flags, out int errno) => inner.RenameAt2(fromDir, from, toDir, to, flags, out errno);
            public uint GetEuid() => inner.GetEuid();
        }
    }
}
