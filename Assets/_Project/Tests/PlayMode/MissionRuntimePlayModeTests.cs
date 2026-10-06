#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class MissionRuntimePlayModeTests
    {
        MissionRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        MissionDirector Director => rig.Director;
        MissionRuntime Runtime => rig.Director.Runtime;

        IEnumerator Start(MissionSettings settings = null, int seed = 12345)
        {
            rig = new MissionRig(settings ?? new MissionSettings { interactionSeconds = 0.3f });
            yield return rig.Generate(seed);
            Assert.That(Director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", Director.Report.Failures));
        }

        static void Kill(Component unit)
        {
            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
        }

        void DisarmHostiles()
        {
            foreach (var hostile in Director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
        }

        void KillAllHostiles()
        {
            foreach (var hostile in Director.Hostiles)
                Kill(hostile);
        }

        void Warp(CommandableUnit unit, Vector3 position) =>
            Assert.That(unit.GetComponent<NavMeshAgent>().Warp(position), Is.True);

        // Hacks the terminal with the first friendly: stands it next to the terminal and orders Interact.
        IEnumerator Hack()
        {
            var unit = Director.Friendlies[0];
            var terminal = Director.Current.Terminal;
            Assert.That(unit.GetComponent<UnitMover>().TrySnap(terminal.Position, out var stand), Is.True);
            Warp(unit, stand);
            Assert.That(unit.Issue(new InteractCommand(terminal)), Is.True);
            yield return TestWorld.WaitUntil(() => terminal.IsCompleted, 10f);
            Assert.That(terminal.IsCompleted, Is.True, "the terminal was hacked");
            yield return null;
        }

        [UnityTest]
        public IEnumerator AFreshMission_IsActive_WithTwoRequiredObjectivesAndALockedExtraction()
        {
            yield return Start();

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Objectives.Select(o => o.Type), Is.EqualTo(new[] { ObjectiveType.EliminateHostiles, ObjectiveType.Interact, ObjectiveType.ReachZone }));
            Assert.That(Runtime.Objectives.Select(o => o.IsRequired), Is.EqualTo(new[] { true, true, false }));
            Assert.That(Runtime.Objectives.Select(o => o.State), Is.EqualTo(new[] { ObjectiveState.Active, ObjectiveState.Active, ObjectiveState.Inactive }));
            Assert.That(Runtime.Objectives.All(o => o.IsKnown), Is.True);
            Assert.That(Director.Current.Terminal.IsAvailable, Is.True, "the active hack objective opens the terminal");
            Assert.That(Runtime.Extraction.TargetPosition, Is.EqualTo(Director.Current.ExtractionZone.position));
        }

        [UnityTest]
        public IEnumerator KillingEveryHostile_CompletesOnlyTheEliminateObjective_NotTheMission()
        {
            yield return Start();
            KillAllHostiles();
            yield return null;
            yield return null;

            Assert.That(rig.Encounter.Outcome, Is.EqualTo(EncounterOutcome.Victory), "the old kill-all rule would have ended it here");
            Assert.That(Runtime.Objectives[0].State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Inactive), "extraction stays locked");
            Assert.That(Director.Current.Terminal.IsAvailable, Is.True);
        }

        [UnityTest]
        public IEnumerator HackingFirst_AndThenKilling_OpensExtraction_OnlyWhenBothAreDone()
        {
            yield return Start();
            DisarmHostiles();
            yield return Hack();

            Assert.That(Runtime.Objectives[1].State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active), "one of two required objectives is not enough");
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Inactive));

            KillAllHostiles();
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Active));
        }

        [UnityTest]
        public IEnumerator EnteringTheZoneBeforeTheObjectivesAreDone_DoesNothing()
        {
            yield return Start();
            DisarmHostiles();
            Warp(Director.Friendlies[0], Director.Current.ExtractionZone.position);
            yield return new WaitForSeconds(0.3f);

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Inactive));
        }

        [UnityTest]
        public IEnumerator EnteringTheOpenZone_CompletesTheMission_AndClosesTheTerminal()
        {
            yield return Start();
            DisarmHostiles();
            yield return Hack();
            KillAllHostiles();
            yield return null;
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));

            Warp(Director.Friendlies[1], Director.Current.ExtractionZone.position);
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Success));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Completed));
            Assert.That(Director.Current.Terminal.IsAvailable, Is.False);
        }

        [UnityTest]
        public IEnumerator TheWholeSquadDead_IsFailure_EvenIfEveryHostileDiedFirst()
        {
            yield return Start();
            KillAllHostiles();
            yield return null;
            foreach (var friendly in Director.Friendlies)
                Kill(friendly);
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Failure));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Inactive));
            Assert.That(Director.Current.Terminal.IsAvailable, Is.False);
        }

        [UnityTest]
        public IEnumerator SuccessIsTerminal_LaterDeathsChangeNothing()
        {
            yield return Start();
            DisarmHostiles();
            yield return Hack();
            KillAllHostiles();
            yield return null;
            Warp(Director.Friendlies[0], Director.Current.ExtractionZone.position);
            yield return null;
            yield return null;
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Success));

            foreach (var friendly in Director.Friendlies)
                Kill(friendly);
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Success));
        }

        [UnityTest]
        public IEnumerator WithoutTheHackObjective_KillingTheHostilesAloneOpensExtraction()
        {
            yield return Start(new MissionSettings { hackTerminal = false });
            Assert.That(Runtime.Objectives.Select(o => o.Type), Is.EqualTo(new[] { ObjectiveType.EliminateHostiles, ObjectiveType.ReachZone }));

            KillAllHostiles();
            yield return null;
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
        }

        [UnityTest]
        public IEnumerator WithoutTheEliminateObjective_HackingAloneOpensExtraction_WhileHostilesLive()
        {
            yield return Start(new MissionSettings { eliminateHostiles = false, interactionSeconds = 0.3f });
            Assert.That(Runtime.Objectives.Select(o => o.Type), Is.EqualTo(new[] { ObjectiveType.Interact, ObjectiveType.ReachZone }));
            DisarmHostiles();

            yield return Hack();
            yield return null;

            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen));
            Assert.That(rig.Encounter.LivingHostiles, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator BothObjectivesOff_StillLeavesTheHack_BecauseAMissionNeedsARequiredObjective()
        {
            yield return Start(new MissionSettings { eliminateHostiles = false, hackTerminal = false });

            Assert.That(Runtime.Objectives.Select(o => o.Type), Is.EqualTo(new[] { ObjectiveType.Interact, ObjectiveType.ReachZone }));
        }

        [UnityTest]
        public IEnumerator Regeneration_ReplacesTheRuntime_AndTheOldOneNeverChangesAgain()
        {
            yield return Start();
            var oldRuntime = Runtime;
            var oldTerminal = Director.Current.Terminal;
            DisarmHostiles();

            yield return rig.Generate(7);
            yield return null;

            Assert.That(Runtime, Is.Not.SameAs(oldRuntime));
            Assert.That(oldRuntime.IsDetached, Is.True);
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Objectives.All(o => o.State != ObjectiveState.Completed), Is.True, "a fresh mission has nothing done");
            Assert.That(oldTerminal == null, Is.True);
            Assert.That(rig.Interactables.Items, Is.EqualTo(new[] { Director.Current.Terminal }));
            var phase = oldRuntime.Phase;
            oldRuntime.Tick();
            Assert.That(oldRuntime.Phase, Is.EqualTo(phase));
        }

        [UnityTest]
        public IEnumerator RegeneratingMidInteraction_LeavesNoStaleClaimOrProgress()
        {
            yield return Start(new MissionSettings { interactionSeconds = 5f });
            DisarmHostiles();
            var unit = Director.Friendlies[0];
            var terminal = Director.Current.Terminal;
            unit.GetComponent<UnitMover>().TrySnap(terminal.Position, out var stand);
            Warp(unit, stand);
            unit.Issue(new InteractCommand(terminal));
            yield return TestWorld.WaitUntil(() => terminal.Progress > 0.1f, 5f);
            Assert.That(terminal.Progress, Is.GreaterThan(0.1f));

            yield return rig.Generate(12345);
            yield return null;

            var fresh = Director.Current.Terminal;
            Assert.That(terminal == null, Is.True);
            Assert.That(fresh.Progress, Is.Zero);
            Assert.That(fresh.User == null, Is.True);
            Assert.That(fresh.IsAvailable, Is.True);
            Assert.That(rig.Interactables.Items, Has.Count.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RegeneratingWhilePaused_ResumesAndStartsAFreshMission()
        {
            yield return Start();
            rig.Pause.Pause();
            Assert.That(rig.Pause.IsPaused, Is.True);

            yield return rig.Generate(3);
            yield return null;

            Assert.That(rig.Pause.IsPaused, Is.False);
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Objectives.All(o => o.State != ObjectiveState.Completed), Is.True);
        }

        [UnityTest]
        public IEnumerator ClearingTheMission_LeavesNoRuntime()
        {
            yield return Start();
            Director.Clear();
            yield return null;

            Assert.That(Director.Runtime, Is.Null);
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Inactive));
            Assert.That(rig.Interactables.Items, Is.Empty);
        }

        [UnityTest]
        public IEnumerator AFailedGeneration_HasNoRuntime()
        {
            rig = new MissionRig(new MissionSettings { gridColumns = 2, gridRows = 2, cellSize = 12, minTeamSeparation = 60f, maxAttempts = 3 });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Mission generation failed"));
            yield return rig.Generate(12345);

            Assert.That(Director.State, Is.EqualTo(MissionState.Failed));
            Assert.That(Director.Runtime, Is.Null);
            Assert.That(Director.Phase, Is.EqualTo(MissionPhase.Inactive));
        }
    }
}
#endif
