#if UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Owner ruling: the largest supported map crosses the real two-process WebSocket transport. The server runs here in the test
    /// runner; the built NetPlayClient.exe runs as a separate process, connects over 127.0.0.1, and must log the map accepted at
    /// every protocol limit. It needs the built client, so it is ignored unless BLACKGLASS_NETPLAY_CLIENT_EXE names it
    /// (Tools/check-netplay-two-process.sh sets it).
    /// </summary>
    public class NetPlayTwoProcessPlayTests
    {
        const string ClientVariable = "BLACKGLASS_NETPLAY_CLIENT_EXE";
        NetLoopbackRig rig;
        Process client;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (client != null)
            {
                try
                {
                    if (!client.HasExited)
                        client.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                client.Dispose();
                client = null;
            }
            if (rig != null)
                yield return rig.Stop();
        }

        [UnityTest]
        public IEnumerator The_largest_map_crosses_to_a_separate_client_process()
        {
            var exe = Environment.GetEnvironmentVariable(ClientVariable);
            if (string.IsNullOrEmpty(exe))
                Assert.Ignore("two-process check: set " + ClientVariable + " to the built NetPlayClient.exe (Tools/check-netplay-two-process.sh)");
            Assert.That(File.Exists(exe), Is.True, exe + " does not exist");

            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet(quiet: true);
            var largest = NetMapSamples.Largest();
            Assert.That(NetMapValidation.Problem(largest), Is.Null);
            rig.Server.BriefingMap = largest;

            var log = Path.Combine(Application.temporaryCachePath, "netplay-two-process-" + rig.Port + ".log");
            File.Delete(log);
            client = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-batchmode -nographics -server 127.0.0.1 -port " + rig.Port + " -token " + NetLoopbackRig.TokenA + " -logFile \"" + log + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.That(client, Is.Not.Null, "the client process started");

            var expected = FormattableStringInvariant(largest);
            var deadline = Time.realtimeSinceStartup + 120f;
            string text = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                text = ReadShared(log);
                if (text != null && (text.Contains(expected) || text.Contains("[NetPlay] map refused") || text.Contains("[NetClient] map refused")))
                    break;
                Assert.That(client.HasExited, Is.False, "the client process exited early; see " + log);
                yield return new WaitForSecondsRealtime(0.5f);
            }
            Assert.That(text, Does.Contain(expected), "the separate client accepted the largest map; see " + log);
            Assert.That(rig.Logs, Has.Some.Contains("welcomed account=account-a"), "the server welcomed the separate process");
            Assert.That(rig.Server.ConnectionCount, Is.EqualTo(1), "exactly the one external connection");
        }

        static string FormattableStringInvariant(NetMap map) =>
            FormattableString.Invariant($"[NetPlay] map accepted floors={map.Floors.Count} boxes={map.Boxes.Count} containers={map.Containers.Count}");

        // The player keeps its log open; read it without locking it out.
        static string ReadShared(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
#endif
