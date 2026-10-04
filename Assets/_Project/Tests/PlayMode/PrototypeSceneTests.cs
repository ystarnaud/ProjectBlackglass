#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PrototypeSceneTests : InputTestFixture
    {
        CommandableUnit unit;
        Health dummy;
        TacticalPause pause;

        // Loaded from each test rather than [UnitySetUp]: the scene must load after InputTestFixture has isolated the
        // input system, otherwise the scene's actions (shared InputActionAsset) leak into later fixtures.
        IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            unit = Object.FindFirstObjectByType<CommandableUnit>();
            dummy = Object.FindFirstObjectByType<Health>();
            pause = Object.FindFirstObjectByType<TacticalPause>();
        }

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
        public IEnumerator Scene_ContainsWiredPrototypeObjects_AndRunsWithoutErrors()
        {
            yield return LoadScene();
            Assert.That(unit, Is.Not.Null, "PlayerUnit missing");
            Assert.That(unit.name, Is.EqualTo("PlayerUnit"));
            Assert.That(dummy, Is.Not.Null, "TrainingDummy missing");
            Assert.That(dummy.name, Is.EqualTo("TrainingDummy"));
            Assert.That(pause, Is.Not.Null, "TacticalPause missing");
            Assert.That(Object.FindFirstObjectByType<PlayerCommandInput>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<TacticalCameraController>(), Is.Not.Null);
            Assert.That(Object.FindFirstObjectByType<PrototypeHud>(), Is.Not.Null);
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
    }

    public class PrototypeSceneInputTests : InputTestFixture
    {
        public override void TearDown()
        {
            PrototypeSceneTests.DestroySceneObjects();
            base.TearDown();
        }

        [UnityTest]
        public IEnumerator RightClickOnDummy_InScene_IssuesAttack()
        {
            var mouse = InputSystem.AddDevice<Mouse>();
            InputSystem.AddDevice<Keyboard>();
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            var unit = Object.FindFirstObjectByType<CommandableUnit>();
            var dummy = Object.FindFirstObjectByType<Health>();

            Set(mouse.position, (Vector2)Camera.main.WorldToScreenPoint(dummy.transform.position));
            yield return null;
            Press(mouse.rightButton);
            yield return null;
            Release(mouse.rightButton);
            yield return null;

            Assert.That(unit.CurrentCommand, Is.TypeOf<AttackCommand>());
        }

        [UnityTest]
        public IEnumerator HoldingW_InScene_PansTheCameraRig()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            yield return SceneManager.LoadSceneAsync("Prototype", LoadSceneMode.Single);
            yield return null;
            var rig = Object.FindFirstObjectByType<TacticalCameraController>();
            var start = rig.transform.position;

            Press(keyboard.wKey);
            yield return new WaitForSecondsRealtime(0.2f);
            Release(keyboard.wKey);
            yield return null;

            Assert.That(rig.transform.position.z, Is.GreaterThan(start.z + 0.5f));
        }
    }
}
#endif
