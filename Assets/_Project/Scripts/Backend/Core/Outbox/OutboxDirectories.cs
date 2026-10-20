using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Blackglass.Backend.Core
{
    public static class OutboxDirectories
    {
        /// <summary>
        /// Production: Linux x86_64 only (anything else is a PlatformNotSupportedException: fail closed). The $STATE_DIRECTORY value
        /// must pass OutboxLocation (a refusal is an OutboxIOException with its code), then the directory is opened and checked
        /// (PosixOutboxDirectory). Called once at process start, before the mission loop; the OutboxWorker then owns it.
        /// </summary>
        public static IOutboxDirectory OpenForServer(string stateDirectory, string requiredParent, string attemptId)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("The outbox is written on linux-x64 only.");
            if (!OutboxLocation.TryResolve(stateDirectory, requiredParent, attemptId, out var path, out var refusal))
                throw new OutboxIOException(refusal, 0);
            return PosixOutboxDirectory.Open(path, NativeLibc.Instance);
        }

        /// <summary>The process's effective uid (geteuid), linux-x64 only: the outbox self-test refuses to run as root.</summary>
        public static long EffectiveUserId()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("The effective uid is read on linux-x64 only.");
            return NativeLibc.Instance.GetEuid();
        }

        /// <summary>Tests and Editor tools only, never a real outbox. A missing directory is never created.</summary>
        public static IOutboxDirectory OpenForEditor(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) throw new DirectoryNotFoundException("The outbox directory does not exist.");
            return new PortableOutboxDirectory(path);
        }
    }
}
