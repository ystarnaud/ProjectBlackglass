namespace Blackglass.Backend.Core
{
    /// <summary>The fields of struct stat the outbox checks.</summary>
    internal readonly struct FileStatus
    {
        public FileStatus(ulong links, uint mode, uint uid, long size)
        {
            Links = links;
            Mode = mode;
            Uid = uid;
            Size = size;
        }

        public ulong Links { get; }
        public uint Mode { get; }
        public uint Uid { get; }
        public long Size { get; }
    }

    /// <summary>
    /// The libc calls PosixOutboxDirectory makes, behind a seam so their loops and error paths are tested with a fake on any
    /// platform (EditMode) and with the real libc on Linux (UnityCore.Linux.Tests). Each returns libc's value (-1 on failure) and
    /// the errno captured right after the call. Paths and names are NUL-terminated ASCII bytes.
    /// </summary>
    internal interface ILibc
    {
        int Open(byte[] path, int flags, out int errno);
        int OpenAt(int dirFd, byte[] name, int flags, int mode, out int errno);
        long Read(int fd, byte[] buffer, int offset, int count, out int errno);
        long Write(int fd, byte[] buffer, int offset, int count, out int errno);
        int Fsync(int fd, out int errno);
        int Close(int fd, out int errno);
        int Fstat(int fd, out FileStatus status, out int errno);
        int Fchmod(int fd, int mode, out int errno);
        int Ftruncate(int fd, long length, out int errno);
        int RenameAt2(int fromDir, byte[] from, int toDir, byte[] to, uint flags, out int errno);
        uint GetEuid();
    }
}
