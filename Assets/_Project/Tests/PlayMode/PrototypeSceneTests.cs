#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PrototypeSceneTests : InputTestFixture
    {
        static readonly string[] FriendlyNames = { "FriendlyUnit_1", "FriendlyUnit_2", "FriendlyUnit_3" };

        CommandableUnit unit;
        Health dummy;
        TacticalPause pause;
        UnitSelection selection;

        // Loaded from each test rather than [UnitySetUp]: the scene must load after InputTestFixture has isolated the
        // input system, otherwise the scene's actions (shared InputActionAsset) leak into later fixtures.
        IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            unit = GameObject.Find(FriendlyNames[0]).GetComponent<CommandableUnit>();
            dummy = Object.FindFirstObjectByType<Health>();
            pause = Object.FindFirstObjectByType<TacticalPause>();
            selection = Object.FindFirstObjectByType<UnitSelection>();
        }

        internal static CommandableUnit[] FindSquad() =>
            FriendlyNames.Select(n => GameObject.Find(n).GetComponent<CommandableUnit>()).ToArray();

        public override void TearDown()
        {
            Time.timeScale = 1f;
            DestroySceneObjects();
            base.TearDown();
        }

        // The scene's components share the project's InputActionAsset; destroy them while the input isolation is
        // still active so their actions are disabled and do not leak into later tests.
        internal static void DestroySceneObjects()
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                Object.DestroyImmediate(root);
        }

        [UnityTest]
        public IEnumerator Scene_ContainsWiredSquad_AndRunsWithoutErrors()
        {
            yield return LoadScene();
            var friendlies = Object.FindObjectsByType<SelectableUnit>(FindObjectsSortMode.None);
            Assert.That(friendlies.Select(f => f.name), Is.EquivalentTo(FriendlyNames));
            Assert.That(selection, Is.Not.Null, "UnitSelection missing");
            Assert.That(selection.Roster, Is.EquivalentTo(friendlies));
            Assert.That(selection.Selected, Is.Empty, "Nothing should be selected at start");
            foreach (var friendly in friendlies)
            {
                Assert.That(friendly.GetComponent<SelectionIndicator>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponent<CommandQueueView>(), Is.Not.Null, friendly.name);
                Assert.That(friendly.GetComponentsInChildren<Collider>(true), Has.Length.EqualTo(1),
                    $"{friendly.name}: only the capsule may have a collider, so debug visuals never block clicks");
            }
            Assert.That(dummy, Is.Not.Null, "TrainingDummy missing");
            Assert.That(dummy.name, Is.EqualTo("TrainingDummy"));
            Assert.That(pause, Is.Not.Null, "TacticalPause missing");
            Assert.That(Object.FindFirstObjectByType<PlayerCommandInput>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<TacticalCameraController>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<PrototypeHud>(), Is.Not.Null);
            var primary = Object.FindFirstObjectByType<PrimaryCharacter>();
            Assert.That(primary, Is.Not.Null, "PrimaryCharacter missing");
            Assert.That(primary.Unit, Is.Not.Null, "PrimaryCharacter has no unit");
            Assert.That(primary.Unit.name, Is.EqualTo(FriendlyNames[0]));
            Assert.That(primary.HasUnit, Is.True);
            Assert.That(primary.IsTakeoverOn, Is.False, "The game starts in free mode");
            Assert.That(primary.Unit.transform.Find("PrimaryMarker"), Is.Not.Null, "Primary marker missing");
            Assert.That(Object.FindFirstObjectByType<DirectControlInput>(), Is.Not.Null, "DirectControlInput missing");
            Assert.That(Camera.main, Is.Not.Null);

            // Any error or exception logged during this second fails the test automatically.
            yield return new WaitForSeconds(1f);
        }

        [UnityTest]
        public IEnumerator Move_AroundTheCentralWall_Arrives()
        {
            yield return LoadScene();
            var destination = new Vector3(6f, 0f, 6f);
            Assert.That(unit.Issue(new MoveCommand(destination)), Is.True);

            yield return TestWorld.WaitUntil(() => unit.CurrentCommand == null, 15f);

            Assert.That(unit.CurrentCommand, Is.Null, "Move did not finish in time");
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, destination), Is.LessThan(0.3f));
        }

        [UnityTest]
        public IEnumerator Attack_DestroysTrainingDummy()
        {
            yield return LoadScene();
            Assert.That(unit.Issue(new AttackCommand(dummy)), Is.True);

            yield return TestWorld.WaitUntil(() => !dummy.IsAlive, 20f);

            Assert.That(dummy.IsAlive, Is.False);
            Assert.That(dummy.gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator Pause_HoldsTheUnitUntilResume()
        {
            yield return LoadScene();
            var start = unit.transform.position;
            pause.Pause();
            unit.Issue(new MoveCommand(new Vector3(-10f, 0f, 0f)));

            yield return new WaitForSecondsRealtime(1f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f));

            pause.Resume();
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(unit.transform.position, start) > 1f, 5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.GreaterThan(1f));
        }

        [UnityTest]
        public IEnumerator GroupMove_TheSquadArrivesAtDistinctPoints()
        {
            yield return LoadScene();
            var squad = FindSquad();
            var destination = new Vector3(-6f, 0f, 0f);
            Assert.That(GroupOrders.Issue(squad, new MoveCommand(destination), IssueMode.Replace), Is.EqualTo(3));

            yield return TestWorld.WaitUntil(() => squad.All(u => u.CurrentCommand == null), 15f);

            Assert.That(squad.All(u => u.CurrentCommand == null), Is.True, "Group move did not finish in time");
            for (var i = 0; i < squad.Length; i++)
            for (var j = i + 1; j < squad.Length; j++)
                Assert.That(TestWorld.HorizontalDistance(squad[i].transform.position, squad[j].transform.position), Is.GreaterThan(1f));
        }
    }

    public class PrototypeSceneInputTests : InputTestFixture
    {
        public override void TearDown()
        {
            Time.timeScale = 1f;
            PrototypeSceneTests.DestroySceneObjects();
            base.TearDown();
        }

        static IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
        }

        IEnumerator LeftClickAt(Mouse mouse, Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        IEnumerator Tap(KeyControl key)
        {
            Press(key);
            yield return null;
            Release(key);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PausedClickCompanionThenDummy_InScene_OnlyThatUnitAttacks()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var dummy = Object.FindFirstObjectByType<Health>();

            yield return Tap(keyboard.spaceKey);
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(squad[1].transform.position));
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(dummy.transform.position));

            Assert.That(squad[1].CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(squad[0].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        // Drags a selection box around every squad member.
        IEnumerator BoxSelect(Mouse mouse, CommandableUnit[] squad)
        {
            var screenPoints = squad.Select(u => (Vector2)Camera.main.WorldToScreenPoint(u.transform.position)).ToArray();
            var margin = new Vector2(25f, 25f);
            var from = screenPoints.Aggregate(Vector2.Min) - margin;
            var to = screenPoints.Aggregate(Vector2.Max) + margin;

            Set(mouse.position, from);
            yield return null;
            Press(mouse.leftButton);
            yield return null;
            Set(mouse.position, to);
            yield return null;
            Release(mouse.leftButton);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PausedBoxSelectSquadThenClickGround_InScene_AllThreeGetMoves()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();

            yield return Tap(keyboard.spaceKey);
            yield return BoxSelect(mouse, squad);
            Assert.That(Object.FindFirstObjectByType<UnitSelection>().Selected, Has.Count.EqualTo(3));

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 0f)));

            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), member.name);
        }

        [UnityTest]
        public IEnumerator StopAndClearSelection_InScene_UseTheSceneBindings()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var selection = Object.FindFirstObjectByType<UnitSelection>();
            yield return Tap(keyboard.spaceKey);   // paused: clicks order the selection

            yield return BoxSelect(mouse, squad);
            Assert.That(selection.Selected, Has.Count.EqualTo(3));
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 0f)));
            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), member.name);

            Press(keyboard.leftShiftKey);
            yield return null;
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 4f)));
            Release(keyboard.leftShiftKey);
            yield return null;
            foreach (var member in squad)
                Assert.That(member.PendingCommands, Has.Count.EqualTo(1), $"{member.name}: Shift-click did not queue an order");

            Press(keyboard.xKey);
            yield return null;
            Release(keyboard.xKey);
            yield return null;
            foreach (var member in squad)
            {
                Assert.That(member.CurrentCommand, Is.Null, $"{member.name}: X did not stop the unit");
                Assert.That(member.PendingCommands, Is.Empty, member.name);
            }

            Press(keyboard.escapeKey);
            yield return null;
            Release(keyboard.escapeKey);
            yield return null;
            Assert.That(selection.Selected, Is.Empty, "Esc did not clear the selection");
        }

        [UnityTest]
        public IEnumerator HoldingW_InScene_PansTheCameraRig()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var rig = Object.FindFirstObjectByType<TacticalCameraController>();
            var start = rig.transform.position;

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(rig.transform.position.z, Is.GreaterThan(start.z + 0.5f));
        }

        [UnityTest]
        public IEnumerator RealTimeClickOnDummy_InScene_ThePrimaryAttacks()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();
            var dummy = Object.FindFirstObjectByType<Health>();

            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(dummy.transform.position));

            Assert.That(squad[0].CurrentCommand, Is.TypeOf<AttackCommand>(), "The primary character did not attack");
            Assert.That(((AttackCommand)squad[0].CurrentCommand).Target, Is.SameAs(dummy));
            Assert.That(squad[1].CurrentCommand, Is.Null);
            Assert.That(squad[2].CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator VThenW_InScene_DrivesThePrimary_AndTheCameraFollows()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var primary = PrototypeSceneTests.FindSquad()[0];
            var rig = Object.FindFirstObjectByType<TacticalCameraController>();
            var start = primary.transform.position;

            yield return Tap(keyboard.vKey);
            Press(keyboard.wKey);
            yield return TestWorld.WaitUntil(() => TestWorld.HorizontalDistance(primary.transform.position, start) > 1.5f, 3f);
            Release(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.That(TestWorld.HorizontalDistance(primary.transform.position, start), Is.GreaterThan(1.5f),
                "V then W did not drive the primary character");
            Assert.That(TestWorld.HorizontalDistance(rig.transform.position, primary.transform.position), Is.LessThan(0.5f),
                "The camera did not follow the primary character");
        }

        [UnityTest]
        public IEnumerator PausedSquadOrder_ThenTakeover_OnlyThePrimaryDropsItsOrders()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return LoadScene();
            var squad = PrototypeSceneTests.FindSquad();

            yield return Tap(keyboard.spaceKey);
            yield return BoxSelect(mouse, squad);
            yield return LeftClickAt(mouse, Camera.main.WorldToScreenPoint(new Vector3(-6f, 0f, 0f)));
            foreach (var member in squad)
                Assert.That(member.CurrentCommand, Is.TypeOf<MoveCommand>(), $"{member.name}: precondition");
            yield return Tap(keyboard.vKey);
            yield return Tap(keyboard.spaceKey);   // resume: the plan starts running

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.3f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(squad[0].CurrentCommand, Is.Null, "Manual input must take the primary character back");
            Assert.That(squad[1].CurrentCommand, Is.TypeOf<MoveCommand>(), "Takeover cancelled a companion's order");
            Assert.That(squad[2].CurrentCommand, Is.TypeOf<MoveCommand>(), "Takeover cancelled a companion's order");
        }
    }
}
#endif
