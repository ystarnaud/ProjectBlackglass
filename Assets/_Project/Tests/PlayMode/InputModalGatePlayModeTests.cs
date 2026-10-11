#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class InputModalGatePlayModeTests : InputTestFixture
    {
        Keyboard keyboard;
        TestWorld world;
        InputActionAsset actions;
        CommandableUnit unit;
        ActiveCharacter active;
        DirectControlInput input;
        InputModalGate gate;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            world = new TestWorld();
            world.CreateEnvironment();
            unit = world.CreateFriendly(Vector3.zero).Unit;
            var cameraObject = world.Track(new GameObject("Camera"));
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 25f, -20f), Quaternion.Euler(50f, 0f, 0f));
            var viewCamera = cameraObject.AddComponent<Camera>();
            var systems = world.Track(new GameObject("Systems"));
            systems.SetActive(false);
            var pause = systems.AddComponent<TacticalPause>();
            var selection = systems.AddComponent<UnitSelection>();
            selection.Initialize(unit.GetComponent<SelectableUnit>());
            active = systems.AddComponent<ActiveCharacter>();
            active.Initialize(unit, pause, selection);
            actions = TestControls.Load();
            input = systems.AddComponent<DirectControlInput>();
            input.Initialize(active, viewCamera, TestControls.Ref(actions, "Character/Move"),
                TestControls.Ref(actions, "Character/ToggleCharacterControl"), selection);
            systems.SetActive(true);
            active.SetTakeover(true);
            gate = new InputModalGate(actions, "UI", "Inventory");
        }

        public override void TearDown()
        {
            gate.Close();
            world.Dispose();
            Time.timeScale = 1f;
            TestControls.Reset(actions);
            base.TearDown();
        }

        [UnityTest]
        public IEnumerator AHeldMoveKey_StopsSteering_WhileTheModalIsOpen_AndAKeyReleasedMeanwhileLeavesNoIntent()
        {
            Press(keyboard.dKey);
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.Not.EqualTo(Vector3.zero), "precondition: the key drives the unit");

            gate.Open();
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "no gameplay input while the modal is open");

            Release(keyboard.dKey);              // released while the modal was open
            yield return null;
            gate.Close();
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "a key released during the modal leaves no stuck intent");
        }

        [UnityTest]
        public IEnumerator AKeyStillHeldAtClose_DoesNotSteer_UntilReleasedAndPressedAgain()
        {
            Press(keyboard.dKey);
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.Not.EqualTo(Vector3.zero), "precondition: the key drives the unit");
            gate.Open();
            yield return null;
            gate.Close();
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "closing the modal never moves the character with a held key");

            Release(keyboard.dKey);
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero));

            Press(keyboard.dKey);
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.Not.EqualTo(Vector3.zero), "a fresh press steers again");
            Release(keyboard.dKey);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AStickPushedWhileOpen_DoesNotSteerAfterClose_UntilItIsCentred()
        {
            var pad = InputSystem.AddDevice<Gamepad>();
            Set(pad.leftStick, new Vector2(1f, 0f));
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.Not.EqualTo(Vector3.zero), "precondition: the stick drives the unit");
            Set(pad.leftStick, Vector2.zero);
            yield return null;
            yield return null;

            gate.Open();
            Set(pad.leftStick, new Vector2(1f, 0f));   // pushed while the modal navigates with it
            yield return null;
            gate.Close();
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "a stick held through the close does not steer");

            Set(pad.leftStick, Vector2.zero);
            yield return null;
            Set(pad.leftStick, new Vector2(1f, 0f));
            yield return null;
            yield return null;
            Assert.That(unit.MoveIntent, Is.Not.EqualTo(Vector3.zero), "centred and pushed again, it steers");
            Set(pad.leftStick, Vector2.zero);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PressingAGameplayKey_WhileOpen_DoesNothing()
        {
            var pause = actions.FindAction("Commands/ToggleTacticalPause");
            var performed = 0;
            void Count(InputAction.CallbackContext _) => performed++;
            pause.performed += Count;
            try
            {
                pause.Enable();                  // positive control: with the gate closed the key does fire the action
                Press(keyboard.spaceKey);
                yield return null;
                Release(keyboard.spaceKey);
                yield return null;
                Assert.That(performed, Is.EqualTo(1), "precondition: space performs the pause toggle while the gate is closed");

                gate.Open();
                Press(keyboard.spaceKey);
                yield return null;
                Release(keyboard.spaceKey);
                yield return null;

                Assert.That(performed, Is.EqualTo(1), "the same key does nothing while the gate is open");
            }
            finally
            {
                pause.performed -= Count;
            }
        }

        [UnityTest]
        public IEnumerator InventoryToggle_StillWorks_WhileOpen_SoTheModalCanBeClosed()
        {
            gate.Open();
            var toggle = actions.FindAction("Inventory/Toggle");

            Press(keyboard.iKey);
            yield return null;

            Assert.That(toggle.WasPressedThisFrame(), Is.True);
            Release(keyboard.iKey);
            yield return null;
        }
    }
}
#endif
