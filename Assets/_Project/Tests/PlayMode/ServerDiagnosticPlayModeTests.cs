#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The Checkpoint A diagnostic on a MissionRig mission: real generation, real units, real Issue path.</summary>
    public class ServerDiagnosticPlayModeTests
    {
        MissionRig rig;
        readonly List<string> lines = new List<string>();

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            lines.Clear();
        }

        // Ranged first, as in the server fixture: the mover then needs line of sight to hit.
        IEnumerator Ready()
        {
            rig = new MissionRig();
            var slots = MissionRig.FriendlySlots();
            rig.SetFriendlySlots(new[] { slots[2], slots[0] });
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
        }

        static double Now => Time.realtimeSinceStartupAsDouble;

        IEnumerator RunToEnd(ServerDiagnostic diagnostic, float limitSeconds)
        {
            var limit = Now + limitSeconds;
            while (!diagnostic.IsFinished && Now < limit)
            {
                diagnostic.Tick(Now);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Diagnostic_MovesThenHits_ThroughTheCommandPath_AndPasses()
        {
            yield return Ready();
            var diagnostic = new ServerDiagnostic(rig.Director, 120, lines.Add);
            diagnostic.Begin(Now);
            var target = diagnostic.Target;
            Assert.That(target, Is.Not.Null, string.Join("\n", lines));
            var before = target.Current;

            yield return RunToEnd(diagnostic, 120f);

            Assert.That(diagnostic.IsFinished, Is.True, "finished within the deadline");
            Assert.That(diagnostic.Passed, Is.True, diagnostic.Summary + "\n" + string.Join("\n", lines));
            StringAssert.StartsWith("BLACKGLASS DIAGNOSTIC PASS", diagnostic.Summary);
            Assert.That(target.Current, Is.LessThan(before));
            Assert.That(lines.Any(l => l.Contains("move PASS")), Is.True);
            Assert.That(lines.Any(l => l.Contains("attack PASS")), Is.True);
        }

        [UnityTest]
        public IEnumerator Diagnostic_FreezesOnlyTheSpawnedInstances_AndLeavesTheMoverAlone()
        {
            yield return Ready();
            var diagnostic = new ServerDiagnostic(rig.Director, 120, lines.Add);
            diagnostic.Begin(Now);

            foreach (var hostile in rig.Director.Hostiles)
            {
                Assert.That(hostile.GetComponent<EnemyAI>().enabled, Is.False, hostile.name);
                Assert.That(hostile.GetComponent<AutoRetaliate>().enabled, Is.False, hostile.name);
            }
            foreach (var friendly in rig.Director.Friendlies.Skip(1))
            {
                Assert.That(friendly.GetComponent<CompanionAI>().enabled, Is.False, friendly.name);
                Assert.That(friendly.GetComponent<AutoRetaliate>().enabled, Is.False, friendly.name);
            }
            var mover = rig.Director.Friendlies[0];
            Assert.That(diagnostic.Mover, Is.EqualTo(mover));
            Assert.That(mover.GetComponent<CompanionAI>().enabled, Is.True, "the mover's own components are untouched");
            Assert.That(mover.GetComponent<UnitAttacker>().HitRoll(), Is.EqualTo(0f), "the forced roll lands even in cover");
            Assert.That(rig.Director.Friendlies[1].GetComponent<UnitAttacker>().HitRoll, Is.Not.SameAs(mover.GetComponent<UnitAttacker>().HitRoll),
                "only the mover's roll is replaced");
        }

        [UnityTest]
        public IEnumerator Diagnostic_WhileSimulationIsPaused_FailsByItsRealTimeDeadline_InsteadOfHanging()
        {
            yield return Ready();
            rig.Pause.Pause();
            var diagnostic = new ServerDiagnostic(rig.Director, 5, lines.Add);
            var started = Now;
            diagnostic.Begin(started);

            yield return RunToEnd(diagnostic, 15f);

            Assert.That(diagnostic.IsFinished, Is.True);
            Assert.That(diagnostic.Passed, Is.False);
            Assert.That(Now - started, Is.LessThan(10d), "ended near the 5 s overall timeout");
            StringAssert.StartsWith("BLACKGLASS DIAGNOSTIC FAIL step=move", diagnostic.Summary);
            StringAssert.Contains("timed out", diagnostic.Summary);
            StringAssert.Contains("simulationRunning=False", diagnostic.Summary);
        }

        [UnityTest]
        public IEnumerator Diagnostic_OnAMissionThatIsNotReady_FailsAtPrepare()
        {
            rig = new MissionRig();
            yield return null;
            var diagnostic = new ServerDiagnostic(rig.Director, 120, lines.Add);
            diagnostic.Begin(Now);

            Assert.That(diagnostic.IsFinished, Is.True);
            Assert.That(diagnostic.Passed, Is.False);
            StringAssert.StartsWith("BLACKGLASS DIAGNOSTIC FAIL step=prepare", diagnostic.Summary);
        }
    }
}
#endif
