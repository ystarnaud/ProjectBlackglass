#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PlayerCommandInputTests : InputTestFixture
    {
        Keyboard keyboard;
        Mouse mouse;
        TestWorld world;
        Camera viewCamera;
        CommandableUnit unit;
        Health dummy;
        TacticalPause pause;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            world = new TestWorld();

            world.CreateEnvironment();
            unit = world.CreateUnit(new Vector3(-6f, 0f, -6f));
            dummy = world.CreateDummy(new Vector3(6f, 0f, 6f));

            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            viewCamera = cameraObject.AddComponent<Camera>();

            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            pause = systems.AddComponent<TacticalPause>();
            var actions = TestControls.Load();
            systems.AddComponent<PlayerCommandInput>().Initialize(viewCamera, unit, pause,
                TestControls.Ref(actions, "Commands/Command"),
                TestControls.Ref(actions, "Commands/PointerPosition"),
                TestControls.Ref(actions, "Commands/TogglePause"));
            systems.SetActive(true);
        }

        public override void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
            base.TearDown();
        }

        IEnumerator RightClickAt(Vector2 screenPoint)
        {
            Set(mouse.position, screenPoint);
            yield return null;
            Press(mouse.rightButton);
            yield return null;
            Release(mouse.rightButton);
            yield return null;
        }

        Vector2 ScreenPointOf(Vector3 world) => viewCamera.WorldToScreenPoint(world);

        [UnityTest]
        public IEnumerator RightClickOnDummy_IssuesAttackOnIt()
        {
            yield return null;
            yield return RightClickAt(ScreenPointOf(dummy.transform.position));

            Assert.That(unit.CurrentCommand, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)unit.CurrentCommand).Target, Is.SameAs(dummy));
        }

        [UnityTest]
        public IEnumerator RightClickOnGround_IssuesMoveToClickedPoint()
        {
            yield return null;
            var groundPoint = new Vector3(4f, 0f, -4f);
            yield return RightClickAt(ScreenPointOf(groundPoint));

            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());
            var destination = ((MoveCommand)unit.CurrentCommand).Destination;
            Assert.That(TestWorld.HorizontalDistance(destination, groundPoint), Is.LessThan(0.1f));
        }

        [UnityTest]
        public IEnumerator RightDrag_IssuesNoCommand()
        {
            yield return null;
            var start = ScreenPointOf(new Vector3(4f, 0f, -4f));
            Set(mouse.position, start);
            yield return null;
            Press(mouse.rightButton);
            yield return null;
            Set(mouse.position, start + new Vector2(40f, 0f));
            yield return null;
            Release(mouse.rightButton);
            yield return null;

            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator RightClickOnSky_IssuesNothing()
        {
            yield return null;
            var skyPoint = ScreenPointOf(new Vector3(0f, 500f, 2000f));
            yield return RightClickAt(skyPoint);

            Assert.That(unit.CurrentCommand, Is.Null);
        }

        [UnityTest]
        public IEnumerator Space_TogglesTacticalPause()
        {
            yield return null;
            Press(keyboard.spaceKey);
            yield return null;
            Release(keyboard.spaceKey);
            yield return null;
            Assert.That(pause.IsPaused, Is.True);

            Press(keyboard.spaceKey);
            yield return null;
            Release(keyboard.spaceKey);
            yield return null;
            Assert.That(pause.IsPaused, Is.False);
        }

        [UnityTest]
        public IEnumerator RightClickWhilePaused_IssuesCommandButUnitWaits()
        {
            yield return null;
            pause.Pause();
            var start = unit.transform.position;

            yield return RightClickAt(ScreenPointOf(new Vector3(4f, 0f, -4f)));
            Assert.That(unit.CurrentCommand, Is.TypeOf<MoveCommand>());

            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(TestWorld.HorizontalDistance(unit.transform.position, start), Is.LessThan(0.01f));
        }
    }
}
#endif
