#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ReconScanPlayModeTests
    {
        IntelRig rig;
        AbilityDefinition scan;
        UnitAbilities abilities;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
            Time.timeScale = 1f;
        }

        void Build(bool corridor, IntelligenceSettings settings)
        {
            rig = new IntelRig(corridor);
            rig.AddHostile(IntelRig.InLineGround);
            rig.Begin(settings);
            scan = rig.World.CreateReconScan();
            abilities = rig.World.AddAbilities(rig.Friendly.Unit, rig.Encounter, scan);
            abilities.SetIntelligence(rig.Service);
        }

        [UnityTest]
        public IEnumerator UsingTheScan_RevealsTheRoomBehindAWall_StartsTheCooldown_AndMakesTheEnemyATargetForAWhile()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            var hostile = rig.Hostiles[0];
            Assert.That(rig.Service.CanTarget(hostile), Is.False, "Precondition");

            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, IntelRig.InLineGround)), Is.True, abilities.LastFailure.ToString());
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Observed));
            Assert.That(rig.Service.CanTarget(hostile), Is.True);
            Assert.That(abilities.CooldownRemaining(0), Is.EqualTo(25f).Within(0.5f));
            Assert.That(abilities.UsedCount, Is.EqualTo(1));
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, IntelRig.InLineGround)), Is.False, "on cooldown");
            Assert.That(abilities.LastFailure, Is.EqualTo(AbilityFailure.OnCooldown));
        }

        [UnityTest]
        public IEnumerator TheScan_RespectsItsRange_ButNeverNeedsLineOfSight()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            var tooFar = abilities.Check(scan, null, new Vector3(30f, 0f, -1.5f));   // 36.5 m from the friendly
            Assert.That(tooFar.Failure, Is.EqualTo(AbilityFailure.OutOfRange));
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, new Vector3(6.5f, 0f, -1.5f))), Is.True,
                "13 m, through a closed wall: no line of sight is needed");
        }

        [UnityTest]
        public IEnumerator TheScan_AimedIntoUndiscoveredGround_IsAllowed()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown));
            var check = abilities.Check(scan, null, IntelRig.InLineGround);
            Assert.That(check.IsValid, Is.True, "undiscovered terrain may be targeted by a ground ability");
        }

        [UnityTest]
        public IEnumerator TheScan_WithFogOff_IsHarmless()
        {
            Build(false, new IntelligenceSettings());
            yield return null;
            Assert.That(abilities.TryUse(AbilityCommand.AtGround(scan, IntelRig.InLineGround)), Is.True);
            Assert.That(rig.Service.IsFogActive, Is.False);
        }

        [UnityTest]
        public IEnumerator TheScan_WithoutAService_DoesNothingAndDoesNotThrow()
        {
            rig = new IntelRig(false);
            rig.Begin(IntelRig.Fog());
            scan = rig.World.CreateReconScan();
            abilities = rig.World.AddAbilities(rig.Friendly.Unit, rig.Encounter, scan);   // SetIntelligence never called
            yield return null;
            Assert.DoesNotThrow(() => abilities.TryUse(AbilityCommand.AtGround(scan, IntelRig.InLineGround)));
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown));
        }

        [UnityTest]
        public IEnumerator AnOrderGivenWhilePaused_RunsOnlyWhenTheGameResumes_ThroughTheNormalOrderPath()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            Time.timeScale = 0f;   // tactical pause: the unit's order path does not run
            Assert.That(rig.Friendly.Unit.Issue(AbilityCommand.AtGround(scan, IntelRig.InLineGround)), Is.True);
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(rig.Service.StateOfRegion(1), Is.EqualTo(KnowledgeState.Unknown), "nothing happens while paused");

            Time.timeScale = 1f;
            yield return TestWorld.WaitUntil(() => rig.Service.StateOfRegion(1) != KnowledgeState.Unknown, 3f);
            Assert.That(rig.Service.StateOfRegion(1), Is.Not.EqualTo(KnowledgeState.Unknown));
            Assert.That(abilities.UsedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AQueuedScan_RunsAfterTheOrdersBeforeIt()
        {
            Build(false, IntelRig.Fog());
            yield return null;
            var move = new MoveCommand(rig.Friendly.transform.position + new Vector3(0f, 0f, 1.2f));
            Assert.That(rig.Friendly.Unit.Issue(move), Is.True);
            Assert.That(rig.Friendly.Unit.Issue(AbilityCommand.AtGround(scan, IntelRig.InLineGround), IssueMode.Append), Is.True);
            yield return TestWorld.WaitUntil(() => abilities.UsedCount == 1, 6f);
            Assert.That(abilities.UsedCount, Is.EqualTo(1));
            Assert.That(rig.Service.StateOfRegion(1), Is.Not.EqualTo(KnowledgeState.Unknown));
        }
    }
}
#endif
