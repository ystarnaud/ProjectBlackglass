#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FishNet.Managing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class NetServerPlayModeTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        [UnityTest]
        public IEnumerator ANetServer_ListensOnLoopbackOnly_AndSpawnsOneAvatarPerUnit()
        {
            rig.BeginNet();
            yield return null;
            yield return null;

            Assert.That(rig.ExitCode, Is.EqualTo(-1), rig.Bootstrap.ExitReason);
            Assert.That(rig.Server.Running, Is.True);
            Assert.That(rig.Server.BindAddress, Is.EqualTo("127.0.0.1"), "never a public address");
            Assert.That(rig.Server.Manager.IsServerStarted, Is.True);
            Assert.That(rig.Server.Manager.ServerManager.GetStartOnHeadless(), Is.False);
            Assert.That(rig.Server.AvatarCount, Is.EqualTo(3), "two friendlies and one hostile");
            Assert.That(rig.Server.AvatarStates.Select(s => s.UnitId).OrderBy(i => i), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(rig.Server.AvatarStates.Single(s => s.Hostile).UnitId, Is.EqualTo(3));
        }

        [UnityTest]
        public IEnumerator TheAvatars_MirrorTheirUnits()
        {
            rig.BeginNet();
            for (var i = 0; i < 5; i++)
                yield return null;

            var units = rig.Director.Friendlies.Concat(rig.Director.Hostiles).ToList();
            for (var i = 0; i < units.Count; i++)
            {
                var state = rig.Server.AvatarStates.Single(s => s.UnitId == i + 1);
                var health = units[i].GetComponent<Health>();
                Assert.That(state.Health, Is.EqualTo(health.Current), "unit " + (i + 1));
                Assert.That(state.MaxHealth, Is.EqualTo(health.Max));
                Assert.That(state.Alive, Is.True);
                Assert.That(state.Order, Is.EqualTo(NetOrder.KindOf(units[i].CurrentCommand)));
                Assert.That(state.Queue, Is.EqualTo(units[i].PendingCommands.Count));
                Assert.That((state.transform.position - units[i].transform.position).magnitude, Is.LessThan(0.01f), "unit " + (i + 1));
            }
        }

        [UnityTest]
        public IEnumerator NetQuiet_FreezesTheAutonomy_AndForcesTheFriendliesHits()
        {
            rig.BeginNet(quiet: true);
            yield return null;

            Assert.That(rig.Director.Hostiles[0].GetComponent<EnemyAI>().enabled, Is.False);
            foreach (var friendly in rig.Director.Friendlies)
            {
                Assert.That(friendly.GetComponent<CompanionAI>().enabled, Is.False, friendly.name);
                Assert.That(friendly.GetComponent<UnitAttacker>().HitRoll(), Is.EqualTo(0f), friendly.name);
            }
        }

        [UnityTest]
        public IEnumerator WithoutNetQuiet_TheAutonomyIsLeftAlone()
        {
            rig.BeginNet(quiet: false);
            yield return null;
            Assert.That(rig.Director.Hostiles[0].GetComponent<EnemyAI>().enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator ANetServerWithoutATokenFile_ExitsTwo_AndDoesNotListen()
        {
            var missing = Path.Combine(Application.temporaryCachePath, "does-not-exist-tokens.txt");
            rig.Bootstrap.BeginNet(rig.Bootstrap.Options.With(net: true, netPort: rig.Port, netTokensPath: missing));
            yield return null;

            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("token file", rig.Bootstrap.ExitReason);
            Assert.That(rig.Server.Running, Is.False);
            Assert.That(NetworkManager.Instances.Count, Is.EqualTo(0), "no manager, so nothing can listen");
        }

        [UnityTest]
        public IEnumerator ANetServerWithoutAnyTokenFileArgument_ExitsTwo()
        {
            rig.Bootstrap.BeginNet(rig.Bootstrap.Options.With(net: true, netPort: rig.Port));
            yield return null;

            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("-netTokens", rig.Bootstrap.ExitReason);
            Assert.That(rig.Server.Running, Is.False);
        }

        [UnityTest]
        public IEnumerator ATokenFileWithNoValidEntry_ExitsTwo()
        {
            File.WriteAllText(rig.TokenPath, "# nothing usable\nonly-two fields\n");
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Net\] token file: line 2"));
            rig.BeginNet();
            yield return null;

            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("no valid entry", rig.Bootstrap.ExitReason);
            Assert.That(rig.Server.Running, Is.False);
        }

        [UnityTest]
        public IEnumerator ADiagnosticRun_WinsOverNet_AndTheNetServerDoesNotStart()
        {
            LogAssert.Expect(LogType.Warning, new Regex(@"-net is ignored with -diagnostic"));
            rig.Bootstrap.StartConfiguredMode(rig.Bootstrap.Options.With(diagnostic: true, net: true, netPort: rig.Port, netTokensPath: rig.TokenPath));
            yield return TestWorld.WaitUntil(() => rig.Bootstrap.HasExited, 120f);

            Assert.That(rig.ExitCode, Is.EqualTo(ServerExitCodes.Success), rig.Bootstrap.ExitReason);
            Assert.That(rig.Server.Running, Is.False);
            Assert.That(NetworkManager.Instances.Count, Is.EqualTo(0));
        }
    }
}
#endif
