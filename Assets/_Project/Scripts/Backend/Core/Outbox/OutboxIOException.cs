using System;
using System.IO;

namespace Blackglass.Backend.Core
{
    /// <summary>An outbox operation failed: a bounded code ([a-z_]{1,40}) and the libc errno (0 when not from libc). Never a path or content.</summary>
    public sealed class OutboxIOException : IOException
    {
        public OutboxIOException(string code, int errno) : base("Outbox operation failed: " + code + (errno != 0 ? " (errno " + errno + ")" : "") + ".")
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Errno = errno;
        }

        public string Code { get; }
        public int Errno { get; }
    }
}
