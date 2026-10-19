using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Blackglass.Backend.Core;

namespace Blackglass.Tests
{
    /// <summary>A simulated process death at a chosen point: what is on disk afterwards is what a crash would leave.</summary>
    internal sealed class SimulatedCrash : Exception
    {
        public SimulatedCrash(string where) : base("simulated crash " + where) { }
    }

    /// <summary>
    /// Wraps PortableOutboxDirectory, records every call in order ("Probe(JG.manifest.ready)", "WriteAndSync(JG.manifest.tmp)",
    /// "RenameNoReplace(JG.manifest.tmp,JG.manifest.written)", "SyncDirectory", "Dispose"), and can crash before or after a
    /// numbered call (0-based), crash halfway through a WriteAndSync (a partial .tmp), fail a call with an OutboxIOException,
    /// or block a call on a gate (a slow fsync). Thread-safe enough for one worker plus a test thread.
    /// </summary>
    internal sealed class FaultingOutboxDirectory : IOutboxDirectory
    {
        readonly IOutboxDirectory inner;
        readonly string path;
        readonly object sync = new object();
        readonly List<string> calls = new List<string>();
        readonly Dictionary<string, (string Code, int Errno, int Times)> failures = new Dictionary<string, (string, int, int)>(StringComparer.Ordinal);
        readonly Dictionary<string, ManualResetEventSlim> gates = new Dictionary<string, ManualResetEventSlim>(StringComparer.Ordinal);

        public FaultingOutboxDirectory(string path)
        {
            this.path = path;
            inner = OutboxDirectories.OpenForEditor(path);
        }

        public int CrashBefore = -1, CrashAfter = -1, CrashHalfwayThroughWrite = -1;

        public IReadOnlyList<string> Calls { get { lock (sync) return calls.ToList(); } }

        /// <summary>The next <paramref name="times"/> calls whose name starts with <paramref name="call"/> ("SyncDirectory", "WriteAndSync") fail.</summary>
        public void Fail(string call, string code, int errno = 5, int times = 1)
        {
            lock (sync) failures[call] = (code, errno, times);
        }

        /// <summary>Calls whose name starts with <paramref name="call"/> wait until the returned gate is set.</summary>
        public ManualResetEventSlim Gate(string call)
        {
            var gate = new ManualResetEventSlim(false);
            lock (sync) gates[call] = gate;
            return gate;
        }

        public void OpenAllGates()
        {
            lock (sync) foreach (var g in gates.Values) g.Set();
        }

        int Begin(string call)
        {
            int index;
            ManualResetEventSlim gate = null;
            (string Code, int Errno, int Times) failure = default;
            var fail = false;
            lock (sync)
            {
                index = calls.Count;
                calls.Add(call);
                foreach (var g in gates) if (call.StartsWith(g.Key, StringComparison.Ordinal)) gate = g.Value;
                foreach (var f in failures.ToList())
                {
                    if (!call.StartsWith(f.Key, StringComparison.Ordinal) || f.Value.Times <= 0) continue;
                    failures[f.Key] = (f.Value.Code, f.Value.Errno, f.Value.Times - 1);
                    failure = f.Value;
                    fail = true;
                }
            }
            if (index == CrashBefore) throw new SimulatedCrash("before " + call);
            gate?.Wait();
            if (fail) throw new OutboxIOException(failure.Code, failure.Errno);
            return index;
        }

        void End(int index, string call)
        {
            if (index == CrashAfter) throw new SimulatedCrash("after " + call);
        }

        public EntryKind Probe(string name)
        {
            var i = Begin("Probe(" + name + ")");
            var r = inner.Probe(name);
            End(i, name);
            return r;
        }

        public byte[] ReadRegular(string name, int maxBytes)
        {
            var i = Begin("ReadRegular(" + name + ")");
            var r = inner.ReadRegular(name, maxBytes);
            End(i, name);
            return r;
        }

        public void WriteAndSync(string name, byte[] bytes)
        {
            var i = Begin("WriteAndSync(" + name + ")");
            if (i == CrashHalfwayThroughWrite)
            {
                File.WriteAllBytes(Path.Combine(path, name), bytes.Take(bytes.Length / 2).ToArray());
                throw new SimulatedCrash("halfway through " + name);
            }
            inner.WriteAndSync(name, bytes);
            End(i, name);
        }

        public void RenameNoReplace(string fromName, string toName)
        {
            var i = Begin("RenameNoReplace(" + fromName + "," + toName + ")");
            inner.RenameNoReplace(fromName, toName);
            End(i, toName);
        }

        public void SyncDirectory()
        {
            var i = Begin("SyncDirectory");
            inner.SyncDirectory();
            End(i, "SyncDirectory");
        }

        public void Dispose()
        {
            lock (sync) calls.Add("Dispose");
            inner.Dispose();
        }
    }

    /// <summary>What Stage 10's agent would make of a participant's outbox files (Outbox.cs, A10-D1).</summary>
    public static class AgentView
    {
        public enum Result { Absent, RelayableAfterVerifiedExit, RelayableNow }

        /// <summary>A .tmp is never a result; .ready is relayable now; .written only after a verified exit. Each result file must
        /// be a regular file whose content parses and names the attempt and account.</summary>
        public static Result Classify(string directory, string account, string attemptId)
        {
            var ready = Path.Combine(directory, account + ".manifest.ready");
            var written = Path.Combine(directory, account + ".manifest.written");
            if (File.Exists(ready)) { Identify(ready, account, attemptId); return Result.RelayableNow; }
            if (File.Exists(written)) { Identify(written, account, attemptId); return Result.RelayableAfterVerifiedExit; }
            return Result.Absent;
        }

        static void Identify(string file, string account, string attemptId)
        {
            NUnit.Framework.Assert.That((File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)), NUnit.Framework.Is.EqualTo((FileAttributes)0), file);
            var parsed = ManifestCodec.Parse(File.ReadAllBytes(file));
            NUnit.Framework.Assert.That(parsed.Ok, NUnit.Framework.Is.True, file + ": " + parsed.Code);
            NUnit.Framework.Assert.That(Base58Id.Format(parsed.Manifest.AccountId), NUnit.Framework.Is.EqualTo(account));
            NUnit.Framework.Assert.That(parsed.Manifest.AttemptId, NUnit.Framework.Is.EqualTo(attemptId));
        }
    }
}
