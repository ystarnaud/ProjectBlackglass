#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// Stage 12 Task 8: the fixture attempt is playable locally through the existing network client on loopback. The server runs
    /// -attemptFixture, then -net as before; the client is welcomed with the snapshot squad and its orders reach the attempt's units.
    /// </summary>
    public class AttemptNetPlayTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.Director.Clear();
            yield return null;
            var fixture = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Backend", "contracts", "attempt-v1", "03-seeded-collect-and-use.json");
            rig.Bootstrap.Begin(new[] { "-attemptFixture", fixture });
            yield return TestWorld.WaitUntil(() => rig.Bootstrap.Session != null || rig.Bootstrap.HasExited, 60f);
            Assert.That(rig.Bootstrap.Session, Is.Not.Null, rig.Bootstrap.ExitReason);
            rig.BeginNet();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => rig.Stop();

        [UnityTest]
        public IEnumerator The_client_is_welcomed_with_the_snapshot_squad_and_steers_it()
        {
            // The attempt's seed is never logged, and neither is the scene's default seed in its place. (The rig's own first,
            // non-attempt mission legitimately logged its seed; only the attempt's lines, from its options line on, are checked.)
            var logs = rig.Logs.ToList();
            var attemptLines = logs.Skip(logs.FindLastIndex(l => l.Contains("[Server] options "))).ToList();
            StringAssert.Contains("[Server] options seed=attempt ", attemptLines[0]);
            StringAssert.EndsWith(" attempt=fixture", attemptLines[0]);
            Assert.That(attemptLines.Where(l => Regex.IsMatch(l, @"seed=-?\d")), Is.Empty, "no numeric seed in the attempt's log");

            yield return rig.Connect(NetLoopbackRig.TokenA);
            Assert.That(rig.Client.State, Is.EqualTo(NetClientState.Ready), "state " + rig.Client.State + " " + rig.Client.Refusal);
            Assert.That(rig.Client.OwnedCount, Is.EqualTo(2), "C and D");
            Assert.That(rig.Director.Friendlies[0].GetComponent<UnitIdentity>().OperativeId, Is.EqualTo("C"));

            var unit = rig.Director.Friendlies[0];
            var command = rig.Client.Send(new NetMoveIntent(1, Vector3.right));
            yield return TestWorld.WaitUntil(() => rig.Client.TryGetResult(command, out _), 5f);
            Assert.That(rig.Client.TryGetResult(command, out var reason), Is.True);
            Assert.That(reason, Is.EqualTo(NetRejectReason.None));
            Assert.That(unit.MoveIntent, Is.Not.EqualTo(Vector3.zero), "the intent reached the attempt's unit");
        }
    }
}
#endif
