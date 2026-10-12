#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The controls the diagnostic and the networked server share to remove chance from a run (decision 045).</summary>
    public class ServerDeterminismPlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        IEnumerator Ready()
        {
            rig = new MissionRig();
            var slots = MissionRig.FriendlySlots();
            rig.SetFriendlySlots(new[] { slots[2], slots[0] });
            yield return rig.Generate(12345);
            Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", rig.Director.Report.Failures));
        }

        [UnityTest]
        public IEnumerator Freeze_WithNoUnitKept_SwitchesOffEveryAutonomy()
        {
            yield return Ready();
            ServerDeterminism.Freeze(rig.Director, null);
            foreach (var hostile in rig.Director.Hostiles)
            {
                Assert.That(hostile.GetComponent<EnemyAI>().enabled, Is.False, hostile.name);
                Assert.That(hostile.GetComponent<AutoRetaliate>().enabled, Is.False, hostile.name);
            }
            foreach (var friendly in rig.Director.Friendlies)
            {
                Assert.That(friendly.GetComponent<CompanionAI>().enabled, Is.False, friendly.name);
                Assert.That(friendly.GetComponent<AutoRetaliate>().enabled, Is.False, friendly.name);
            }
        }

        [UnityTest]
        public IEnumerator Freeze_LeavesTheKeptUnitsAutonomyAlone()
        {
            yield return Ready();
            var kept = rig.Director.Friendlies[0];
            ServerDeterminism.Freeze(rig.Director, kept);
            Assert.That(kept.GetComponent<CompanionAI>().enabled, Is.True);
            Assert.That(rig.Director.Friendlies[1].GetComponent<CompanionAI>().enabled, Is.False);
        }

        [UnityTest]
        public IEnumerator ForceHits_LandsEvenInCover_ForThatUnitOnly()
        {
            yield return Ready();
            var forced = rig.Director.Friendlies[0];
            var other = rig.Director.Friendlies[1];
            ServerDeterminism.ForceHits(forced);
            Assert.That(forced.GetComponent<UnitAttacker>().HitRoll(), Is.EqualTo(0f));
            Assert.That(other.GetComponent<UnitAttacker>().HitRoll, Is.Not.SameAs(forced.GetComponent<UnitAttacker>().HitRoll),
                "only the named unit's roll is replaced");
        }
    }
}
#endif
