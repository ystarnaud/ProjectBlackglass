#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XInput;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The objective flow in the real ProceduralMission scene: the scene generates its own mission at start, and these tests
    /// regenerate seed 12345 with a short interaction and play it through the real input components. A test that needs a
    /// unit somewhere warps it there; hostiles that must not act have their AI switched off. Only an Xbox-style simulated pad
    /// is used (see ProceduralMissionSceneTests).
    /// </summary>
    public class MissionObjectivesSceneTests : InputTestFixture
    {
        const int DeterministicSeed = 12345;

        Keyboard keyboard;
        Mouse mouse;
        InputActionAsset actions;
        MissionDirector director;
        CommandableUnit[] squad;
        TacticalPause pause;
        UnitSelection selection;
        ActiveCharacter active;
        TacticalCursor cursor;
        PlayerCommandInput commandInput;
        InteractableRegistry registry;
        TacticalCameraController cameraRig;
        Camera viewCamera;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            actions = TestControls.Load();
        }

        public override void TearDown()
        {
            Time.timeScale = 1f;
            if (SceneManager.GetActiveScene().name == "ProceduralMission")
                PrototypeSceneTests.DestroySceneObjects();
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator LoadMission(float interactionSeconds = 0.4f)
        {
            var loading = SceneManager.LoadSceneAsync("ProceduralMission", LoadSceneMode.Single);
            Assert.That(loading, Is.Not.Null, "The ProceduralMission scene is not in the build settings");
            yield return loading;
            director = Object.FindFirstObjectByType<MissionDirector>();
            Assert.That(director, Is.Not.Null);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            director.Settings.interactionSeconds = interactionSeconds;
            // The scene ships with fog (decision 037); these tests click enemies and count terminals, so they play the same mission with full knowledge.
            director.Settings.intelligence = IntelligenceSettings.Full();
            Assert.That(director.Generate(DeterministicSeed), Is.True);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 20f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), director.Report.Failure);
            Bind();
            yield return null;
        }

        void Bind()
        {
            squad = director.Friendlies.ToArray();
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
            active = Object.FindFirstObjectByType<ActiveCharacter>();
            cursor = Object.FindFirstObjectByType<TacticalCursor>();
            commandInput = Object.FindFirstObjectByType<PlayerCommandInput>();
            registry = Object.FindFirstObjectByType<InteractableRegistry>();
            cameraRig = Object.FindFirstObjectByType<TacticalCameraController>();
            viewCamera = Camera.main;
        }

        MissionRuntime Runtime => director.Runtime;
        MissionInteractable Terminal => director.Current.Terminal;
        Vector3 Zone => director.Current.ExtractionZone.position;

        static void Kill(Component unit)
        {
            var health = unit.GetComponent<Health>();
            health.TakeDamage(health.Max);
        }

        void KillAllHostiles()
        {
            foreach (var hostile in director.Hostiles)
            {
                hostile.GetComponent<EnemyAI>().enabled = false;
                Kill(hostile);
            }
        }

        void DisarmHostiles()
        {
            foreach (var hostile in director.Hostiles)
                hostile.GetComponent<EnemyAI>().enabled = false;
        }

        // Stands the unit on the walkable point next to the terminal (inside the interaction range).
        void WarpNextToTerminal(CommandableUnit unit)
        {
            Assert.That(unit.GetComponent<UnitMover>().TrySnap(Terminal.Position, out var stand), Is.True);
            Assert.That(unit.GetComponent<NavMeshAgent>().Warp(stand), Is.True);
        }

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        IEnumerator CursorOn(Vector3 worldPoint)
        {
            cursor.SetScreenPosition(viewCamera.WorldToScreenPoint(worldPoint));
            yield return null;
        }

        // Puts the camera where it sees the terminal under its own screen point. The terminal is a 1.2 m cube and the
        // generator can place a 3 m pillar or wall in front of it, so the camera is moved forward (closer, hence steeper)
        // until the ray through the terminal reaches it.
        void FocusToSeeTerminal()
        {
            var forward = Vector3.ProjectOnPlane(viewCamera.transform.forward, Vector3.up).normalized;
            foreach (var ahead in new[] { 0f, 2f, 4f, 6f, 8f, 10f })
            {
                cameraRig.FocusOn(Terminal.Position + forward * ahead);
                var screen = viewCamera.WorldToScreenPoint(Terminal.Position);
                if (screen.z <= 0f || screen.x < 20f || screen.x > viewCamera.pixelWidth - 20f || screen.y < 20f || screen.y > viewCamera.pixelHeight - 20f)
                    continue;
                if (PointerTargetResolver.Resolve(viewCamera, screen, 500f, ~0, null, 0f).Kind == PointerTargetKind.Interactable)
                    return;
            }
            Assert.Fail("No camera position within 10 m sees the terminal at seed " + DeterministicSeed);
        }

        IEnumerator WakeKeyboard()
        {
            // Any key switches the input family; the input that wakes a family only wakes it.
            yield return Tap(keyboard.leftAltKey);
        }

        [UnityTest]
        public IEnumerator Scene_WiresTheObjectiveSystems()
        {
            yield return LoadMission();

            Assert.That(registry, Is.Not.Null);
            Assert.That(registry.Items, Is.EqualTo(new[] { Terminal }));
            Assert.That(Object.FindFirstObjectByType<MissionHud>(), Is.Not.Null);
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(director.Hostiles.Count, Is.EqualTo(director.Current.Layout.HostileSpawns.Count + director.Current.Plan.GuardTiles.Count));
            Assert.That(squad.All(u => u.TryGetComponent<UnitInteractor>(out _)), Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_KeyboardMouse_ClickTheTerminal_TheSquadMemberWalksThereAndHacksIt_ThenExtractionOpensAndCompletes()
        {
            yield return LoadMission();
            KillAllHostiles();
            yield return WakeKeyboard();
            FocusToSeeTerminal();
            yield return null;
            var screen = viewCamera.WorldToScreenPoint(Terminal.Position);
            var seen = PointerTargetResolver.Resolve(viewCamera, screen, 500f, ~0, null, 0f);
            Assert.That(seen.Kind, Is.EqualTo(PointerTargetKind.Interactable), "the camera sees the terminal under its own screen point");

            Set(mouse.position, (Vector2)screen);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<InteractCommand>(), "no selection: the click orders the character being played");
            yield return TestWorld.WaitUntil(() => Terminal.IsCompleted, 90f);
            Assert.That(Terminal.IsCompleted, Is.True, "the unit walked to the terminal and hacked it");
            yield return null;
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.ExtractionOpen), "the kills and the hack are both done");

            Assert.That(squad[0].Issue(new MoveCommand(Zone)), Is.True);
            yield return TestWorld.WaitUntil(() => director.Phase == MissionPhase.Success, 90f);
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Success));
            Assert.That(MissionHudText.Banner(director.Phase), Does.Contain("SUCCESS"));
        }

        [UnityTest]
        public IEnumerator Scene_KeyboardInteract_OrdersTheNearbyTerminal_AndThePromptKnowsIt()
        {
            yield return LoadMission();
            DisarmHostiles();
            WarpNextToTerminal(squad[0]);
            yield return WakeKeyboard();
            yield return null;
            Assert.That(commandInput.NearbyInteractable, Is.SameAs(Terminal), "the HUD prompt condition");

            yield return Tap(keyboard.rKey);

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<InteractCommand>());
            yield return TestWorld.WaitUntil(() => Terminal.IsCompleted, 10f);
            Assert.That(Terminal.IsCompleted, Is.True);
            Assert.That(commandInput.NearbyInteractable, Is.Null, "a used-up terminal is no longer offered");
        }

        [UnityTest]
        public IEnumerator Scene_Pad_PlanInteractWhilePaused_NothingProgresses_ThenResumeCompletesIt()
        {
            yield return LoadMission();
            DisarmHostiles();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Tap(pad.selectButton);   // View is unbound: it only wakes the pad
            yield return Tap(pad.startButton);
            Assert.That(pause.IsPaused, Is.True);
            WarpNextToTerminal(squad[0]);

            cameraRig.FocusOn(squad[0].transform.position);
            yield return null;
            yield return CursorOn(squad[0].transform.position);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Friendly));
            yield return Tap(pad.buttonSouth);

            FocusToSeeTerminal();
            yield return null;
            yield return CursorOn(Terminal.Position);
            Assert.That(cursor.Target.Kind, Is.EqualTo(PointerTargetKind.Interactable), "the tactical cursor identifies the terminal");
            yield return Tap(pad.buttonSouth);

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<InteractCommand>());
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(Terminal.Progress, Is.Zero, "nothing runs while paused");

            yield return Tap(pad.startButton);
            Assert.That(pause.IsPaused, Is.False);
            yield return TestWorld.WaitUntil(() => Terminal.IsCompleted, 10f);
            Assert.That(Terminal.IsCompleted, Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_PadConfirm_WithTheCursorHidden_InteractsWithTheTerminalInReach()
        {
            yield return LoadMission();
            DisarmHostiles();
            var pad = InputSystem.AddDevice<XInputController>();
            yield return Tap(pad.selectButton);
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(cursor.IsActive, Is.False, "running: the camera owns the right stick");
            WarpNextToTerminal(squad[0]);
            yield return null;

            yield return Tap(pad.buttonSouth);

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<InteractCommand>());
            yield return TestWorld.WaitUntil(() => Terminal.IsCompleted, 10f);
            Assert.That(Terminal.IsCompleted, Is.True);
        }

        [UnityTest]
        public IEnumerator Scene_ExtractionStaysLocked_ForAUnitAlreadyInTheZone_UntilTheObjectivesAreDone_ThenCompletesAtOnce()
        {
            yield return LoadMission();
            DisarmHostiles();
            squad[1].GetComponent<CompanionAI>().enabled = false;   // it must stay where it is put, not follow the leader
            Assert.That(squad[1].GetComponent<NavMeshAgent>().Warp(Zone), Is.True);
            yield return new WaitForSeconds(0.3f);
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Extraction.State, Is.EqualTo(ObjectiveState.Inactive));

            KillAllHostiles();
            WarpNextToTerminal(squad[0]);
            squad[0].Issue(new InteractCommand(Terminal));
            yield return TestWorld.WaitUntil(() => director.Phase == MissionPhase.Success, 10f);

            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Success), "the unit that waited in the zone extracts the moment it opens");
        }

        [UnityTest]
        public IEnumerator Scene_TheWholeSquadDead_IsFailure_AndTheBannerSaysSo()
        {
            yield return LoadMission();
            foreach (var unit in squad)
                Kill(unit);
            yield return null;
            yield return null;

            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Failure));
            Assert.That(MissionHudText.Banner(director.Phase), Does.Contain("FAILED"));
            Assert.That(Terminal.IsAvailable, Is.False);
        }

        [UnityTest]
        public IEnumerator Scene_F6_DuringAnInteraction_AndWhilePaused_ResetsTheMissionCompletely()
        {
            yield return LoadMission(interactionSeconds: 5f);
            DisarmHostiles();
            WarpNextToTerminal(squad[0]);
            squad[0].Issue(new InteractCommand(Terminal));
            yield return TestWorld.WaitUntil(() => Terminal.Progress > 0.1f, 5f);
            var oldRuntime = Runtime;
            var oldTerminal = Terminal;

            Press(keyboard.f6Key);
            yield return null;
            Release(keyboard.f6Key);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready && director.Runtime != oldRuntime, 20f);
            yield return null;

            Assert.That(director.Runtime, Is.Not.SameAs(oldRuntime));
            Assert.That(oldTerminal == null, Is.True);
            Assert.That(Terminal.Progress, Is.Zero);
            Assert.That(Terminal.User == null, Is.True);
            Assert.That(registry.Items, Is.EqualTo(new[] { Terminal }));
            Assert.That(Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Count(t => t.name == "ExtractionZone"), Is.EqualTo(1));

            Bind();
            pause.Pause();
            var pausedRuntime = Runtime;
            Assert.That(director.RegenerateSame(), Is.True);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready && director.Runtime != pausedRuntime, 20f);
            yield return null;
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Active));
        }

        [UnityTest]
        public IEnumerator Scene_RegeneratingMidFight_WithLiveArmedHostiles_LeavesAFreshMissionAndNothingOfTheOldOne()
        {
            yield return LoadMission();
            // Nothing is disarmed: a hostile is put next to the leader and the two fight with the real AI.
            var hostile = director.Hostiles[0];
            Assert.That(squad[0].GetComponent<NavMeshAgent>().isOnNavMesh, Is.True);
            Assert.That(hostile.GetComponent<UnitMover>().TrySnap(squad[0].transform.position + squad[0].transform.right * 1.5f, out var beside), Is.True);
            Assert.That(hostile.GetComponent<NavMeshAgent>().Warp(beside), Is.True);
            System.Func<bool> fighting = () => director.Friendlies.Concat(director.Hostiles).Any(u => u.GetComponent<Health>().Current < u.GetComponent<Health>().Max);
            yield return TestWorld.WaitUntil(fighting, 10f);
            Assert.That(fighting(), Is.True, "precondition: the squad member and the hostile really fought (someone took damage)");
            Assert.That(director.Hostiles.Any(h => h.GetComponent<EnemyAI>().enabled), Is.True, "precondition: armed hostiles are still around");
            var oldRuntime = Runtime;
            var oldUnits = director.Friendlies.Concat(director.Hostiles).ToArray();

            Press(keyboard.f6Key);
            yield return null;
            Release(keyboard.f6Key);
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready && director.Runtime != oldRuntime, 20f);
            yield return null;
            Bind();

            Assert.That(director.Runtime, Is.Not.SameAs(oldRuntime));
            Assert.That(director.Phase, Is.EqualTo(MissionPhase.Active));
            Assert.That(Runtime.Objectives.Any(o => o.State == ObjectiveState.Completed), Is.False, "a fresh runtime: nothing completed");
            Assert.That(registry.Items, Is.EqualTo(new[] { Terminal }));
            Assert.That(Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Count(t => t.name == "ExtractionZone"), Is.EqualTo(1));
            Assert.That(oldUnits.All(u => u == null), Is.True, "no unit of the old mission survives");
            var unitsInScene = Object.FindObjectsByType<CommandableUnit>(FindObjectsSortMode.None);
            Assert.That(unitsInScene, Has.Length.EqualTo(director.Friendlies.Count + director.Hostiles.Count));
            Assert.That(director.Current.Actors.GetComponentsInChildren<CommandableUnit>(true), Has.Length.EqualTo(unitsInScene.Length),
                "every unit lives under the new mission's Actors");
        }
    }
}
#endif
