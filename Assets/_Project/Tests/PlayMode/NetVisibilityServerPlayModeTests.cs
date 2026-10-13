#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The networked server with the real intelligence model (normal -net mode). Host mode: these tests read the server's observer
    /// sets; that the client process holds nothing is proven by the VM harness.
    /// </summary>
    public class NetVisibilityServerPlayModeTests
    {
        const int Hostile = 3;

        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        int Client => rig.Server.ClientIds.Single();

        [UnityTest]
        public IEnumerator ADisabledIntelligenceService_KeepsTheNetServerFromStarting()
        {
            Object.FindAnyObjectByType<IntelligenceService>().enabled = false;
            rig.BeginNet();
            yield return null;
            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            Assert.That(rig.Server.Running, Is.False);
            Assert.That(rig.Logs.Any(l => l.Contains("visibility: the IntelligenceService is disabled")), Is.True, string.Join("\n", rig.Logs));
        }

        [UnityTest]
        public IEnumerator TheFixtureFlag_WithoutNetQuiet_KeepsTheNetServerFromStarting()
        {
            rig.BeginNet(quiet: false, fixture: true);
            yield return null;
            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            Assert.That(rig.Logs.Any(l => l.Contains("-netFixture needs -netQuiet")), Is.True);
        }

        [UnityTest]
        public IEnumerator TheOwner_ReceivesItsSquad_ButNotTheHiddenHostile()
        {
            rig.BeginNet();
            yield return rig.Connect(NetLoopbackRig.TokenA);
            yield return new WaitForSecondsRealtime(1f);
            Assert.That(rig.Server.IsObserver(Client, 1), Is.True);
            Assert.That(rig.Server.IsObserver(Client, 2), Is.True);
            Assert.That(rig.Server.IsObserver(Client, Hostile), Is.False, "16 m apart and out of sight: the real model hides it");
            Assert.That(rig.Server.Visibility.KnowledgeOf(rig.Server.Host.ConnectionOf(Client), Hostile), Is.EqualTo(NetKnowledge.Unknown));
        }

        [UnityTest]
        public IEnumerator AnAccountWithoutUnits_ReceivesNoUnitAtAll()
        {
            rig.BeginNet();
            yield return rig.Connect(NetLoopbackRig.TokenB);
            yield return new WaitForSecondsRealtime(1f);
            for (var id = 1; id <= 3; id++)
                Assert.That(rig.Server.IsObserver(Client, id), Is.False, "unit " + id);
        }

        [UnityTest]
        public IEnumerator EveryAvatar_IsServerOwned()
        {
            rig.BeginNet();
            yield return rig.Connect(NetLoopbackRig.TokenA);
            Assert.That(rig.Server.AvatarStates, Is.Not.Empty);
            foreach (var state in rig.Server.AvatarStates)
                Assert.That(state.Owner.IsValid, Is.False, state.name + " has an owner");
        }

        [UnityTest]
        public IEnumerator AHostileAvatar_NeverCarriesOrders()
        {
            rig.BeginNet();
            yield return null;
            var hostile = rig.Director.Hostiles[0];
            Assert.That(hostile.Issue(new MoveCommand(rig.Director.Friendlies[0].transform.position), IssueMode.Replace), Is.True);
            yield return null;
            yield return null;
            Assert.That(hostile.CurrentCommand, Is.Not.Null, "Precondition: the hostile has an order");
            var avatar = rig.Server.AvatarStates.Single(s => s.Hostile);
            Assert.That(avatar.Order, Is.EqualTo(NetOrderKind.None));
            Assert.That(avatar.Queue, Is.EqualTo(0));

            // Steering ends the order (CommandableUnit.SetMoveIntent), so it is checked on its own.
            hostile.SetMoveIntent(Vector3.right);
            yield return null;
            yield return null;
            Assert.That(hostile.MoveIntent, Is.Not.EqualTo(Vector3.zero), "Precondition: the hostile is steering");
            Assert.That(avatar.Steering, Is.False);
        }

        [UnityTest]
        public IEnumerator TheListeningLine_NamesTheVisibilitySource()
        {
            rig.BeginNet();
            yield return null;
            Assert.That(rig.Logs.Any(l => l.StartsWith(NetServer.Prefix + "listening") && l.Contains("visibility=squad")), Is.True);
        }
    }
}
#endif
