#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The rule the whole feature rests on: sight needs a line, range alone never discovers anything.</summary>
    public class IntelligenceServiceGeometryTests
    {
        IntelRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator AClosedWall_DoesNotRevealTheRoomBehindIt_EvenInRange()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);   // 13 m away, inside the 14 m observation range
            rig.Begin(IntelRig.Fog());
            yield return null;

            Assert.That(rig.Service.StateOfRegion(0), Is.EqualTo(KnowledgeState.Observed), "the room the squad stands in");
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown), "the room behind the wall");
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(rig.Service.CanTarget(hostile), Is.False);
            Assert.That(rig.Service.IsUnitShown(hostile), Is.False);
        }

        [UnityTest]
        public IEnumerator ALongerRange_StillDoesNotSeeThroughAClosedWall()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog(s => s.observationRange = 40f));
            yield return null;
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(rig.Service.CanTarget(hostile), Is.False);
        }

        [UnityTest]
        public IEnumerator AnOpenDoor_RevealsTheCorridorAndTheEnemyInLine()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            yield return null;

            Assert.That(rig.Service.StateOfRegion(2), Is.EqualTo(KnowledgeState.Observed), "the corridor");
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed), "part of room B is visible down the corridor");
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(rig.Service.CanTarget(hostile), Is.True);
        }

        [UnityTest]
        public IEnumerator AnOpenDoor_ShowsOnlyTheEnemiesTheOpeningAllows()
        {
            rig = new IntelRig(corridor: true);
            var inLine = rig.AddHostile(IntelRig.InLineGround);
            var hidden = rig.AddHostile(IntelRig.OffAxisGround);   // in the same, partly seen room, behind the corridor wall
            rig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(rig.Service.CanTarget(inLine), Is.True);
            Assert.That(rig.Service.CanTarget(hidden), Is.False, "room B is partly seen but this enemy is not");
        }

        [UnityTest]
        public IEnumerator DiscoveryPersists_WhenTheSquadLooksAway_ButLiveSightDoesNot()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            yield return null;
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Observed));

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);   // north end of room A: the corridor is out of line
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Discovered), "the layout stays discovered");
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Discovered), "the enemy is only last known now");
            Assert.That(rig.Service.CanTarget(hostile), Is.False);
            Assert.That(rig.Service.TryLastKnown(hostile, out var at), Is.True);
            Assert.That(Vector3.Distance(at, hostile.transform.position), Is.LessThan(0.01f));
        }

        [UnityTest]
        public IEnumerator ALastKnownMarker_DoesNotFollowTheHiddenEnemy()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            yield return null;
            var seenAt = hostile.transform.position;

            rig.Friendly.transform.position = new Vector3(-6.5f, 1f, 1.5f);
            Physics.SyncTransforms();
            rig.Service.RunPass();
            hostile.transform.position = new Vector3(8f, 1f, 1.5f);   // walks off while unseen
            Physics.SyncTransforms();
            rig.Service.RunPass();
            Assert.That(rig.Service.TryLastKnown(hostile, out var at), Is.True);
            Assert.That(Vector3.Distance(at, seenAt), Is.LessThan(0.01f));
        }

        [UnityTest]
        public IEnumerator ADeadEnemy_DropsOutWithoutAMarkerOrAnException()
        {
            rig = new IntelRig(corridor: true);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(IntelRig.Fog());
            yield return null;
            hostile.TakeDamage(1000);
            Assert.DoesNotThrow(() => rig.Service.RunPass());
            Assert.That(rig.Service.StateOfEnemy(hostile), Is.EqualTo(KnowledgeState.Unknown));
            Assert.That(rig.Service.TryLastKnown(hostile, out _), Is.False);
            Assert.That(rig.Service.CanTarget(hostile), Is.False);
        }

        [UnityTest]
        public IEnumerator FogOff_MakesEverythingKnown_AndPassesDoNothing()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(new IntelligenceSettings());   // fog off
            yield return null;
            Assert.That(rig.Service.IsFogActive, Is.False);
            Assert.That(rig.Service.CanTarget(hostile), Is.True);
            Assert.That(rig.Service.IsUnitShown(hostile), Is.True);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed));
        }

        [Test]
        public void NoMissionYet_MeansEverythingKnown()
        {
            rig = new IntelRig(corridor: false);
            var hostile = rig.AddHostile(IntelRig.InLineGround);
            Assert.That(rig.Service.HasMission, Is.False);
            Assert.That(rig.Service.CanTarget(hostile), Is.True);
            Assert.That(Knowledge.CanTarget(null, hostile), Is.True, "a null service is everything-known too");
            Assert.That(Knowledge.IsShown(null, hostile), Is.True);
            Assert.That(Knowledge.CanInteract(null, null), Is.True);
            Assert.That(Knowledge.CanSeeCover(null, null), Is.True);
        }
    }
}
#endif
