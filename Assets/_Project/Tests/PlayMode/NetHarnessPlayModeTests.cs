#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class NetHarnessPlayModeTests
    {
        NetLoopbackRig rig;
        NetHarness harness;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (harness != null)
                Object.DestroyImmediate(harness.gameObject);
            yield return rig.Stop();
        }

        // Starts the net server (unless the test wants none), connects a client with the token and runs a role to its end.
        IEnumerator Run(string role, string token, bool serverListening = true, float seconds = 40f)
        {
            if (serverListening)
                rig.BeginNet();
            var client = rig.AddClient();
            client.Connect("127.0.0.1", (ushort)rig.Port, token);
            harness = new GameObject("NetHarness").AddComponent<NetHarness>();
            harness.Initialize(client);
            harness.Exit = _ => { };
            harness.Begin(role);
            yield return TestWorld.WaitUntil(() => harness.ExitCode >= 0, seconds);
        }

        [UnityTest]
        public IEnumerator TheBadTokenRole_PassesWhenTheServerRefusesTheToken()
        {
            yield return Run("badtoken", "tok-secret-probe");
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitPass), harness.Summary);
            StringAssert.StartsWith("BLACKGLASS NET HARNESS PASS role=badtoken", harness.Summary);
        }

        [UnityTest]
        public IEnumerator TheTicketRole_PassesWhenTheServerRefusesTheAttempt()
        {
            yield return Run("ticket", NetLoopbackRig.TokenTicket);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitPass), harness.Summary);
        }

        [UnityTest]
        public IEnumerator TheObserverRole_PassesWhenEveryOrderForTheSquadIsRefused()
        {
            yield return Run("b", NetLoopbackRig.TokenB);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitPass), harness.Summary);
        }

        [UnityTest]
        public IEnumerator TheHarness_Fails_WhenTheExpectedRefusalDoesNotHappen()
        {
            yield return Run("ticket", NetLoopbackRig.TokenA);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitFailed), harness.Summary);
            StringAssert.Contains("FAIL role=ticket step=refusal", harness.Summary);
        }

        [UnityTest]
        public IEnumerator TheHarness_ReportsNoConnection_WhenNothingListens()
        {
            yield return Run("a", NetLoopbackRig.TokenA, serverListening: false, seconds: 30f);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitNoConnection), harness.Summary);
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator TheOwnerRole_PassesTheWholeScript()
        {
            yield return Run("a", NetLoopbackRig.TokenA, seconds: 240f);
            Assert.That(harness.ExitCode, Is.EqualTo(NetHarness.ExitPass), harness.Summary);
            StringAssert.Contains("latencyMedianMs=", harness.Summary);
            Assert.That(harness.LatencyMedianMs, Is.GreaterThan(0d));
        }
    }
}
#endif
