#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class AbilityMenuGateTests : InputTestFixture
    {
        Gamepad pad;
        InputActionAsset actions;
        TestWorld world;
        AbilityMenuGate gate;
        InputAction stop;
        InputAction follow;
        InputAction ability1;
        InputAction ability3;
        int stops;
        int ability3Uses;
        int ability1Uses;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<XInputController>();
            actions = TestControls.Load();
            TestControls.UseGroup(actions, "Xbox");
            world = new TestWorld();
            stop = actions.FindAction("Commands/Stop", true);
            follow = actions.FindAction("Character/ToggleFollow", true);
            ability1 = actions.FindAction("Commands/Ability1", true);
            ability3 = actions.FindAction("Commands/Ability3", true);
            stop.Enable();
            follow.Enable();
            ability1.Enable();
            ability3.Enable();
            stop.performed += OnStop;
            ability1.performed += OnAbility1;
            ability3.performed += OnAbility3;
            stops = 0;
            ability1Uses = 0;
            ability3Uses = 0;

            var host = world.Track(new GameObject("Gate"));
            host.SetActive(false);
            gate = host.AddComponent<AbilityMenuGate>();
            gate.Initialize(TestControls.Ref(actions, "Commands/AbilityMenu"),
                TestControls.Ref(actions, "Commands/Stop"), TestControls.Ref(actions, "Character/ToggleFollow"),
                TestControls.Ref(actions, "Commands/NextTarget"), TestControls.Ref(actions, "Commands/PreviousTarget"));
            host.SetActive(true);
        }

        public override void TearDown()
        {
            // The actions belong to the shared project asset: drop this fixture's handlers or they pile up across tests.
            stop.performed -= OnStop;
            ability1.performed -= OnAbility1;
            ability3.performed -= OnAbility3;
            world.Dispose();
            TestControls.Reset(actions);
            base.TearDown();
        }

        void OnStop(InputAction.CallbackContext context) => stops++;
        void OnAbility1(InputAction.CallbackContext context) => ability1Uses++;
        void OnAbility3(InputAction.CallbackContext context) => ability3Uses++;

        IEnumerator Tap(ButtonControl button)
        {
            Press(button);
            yield return null;
            Release(button);
            yield return null;
        }

        [UnityTest]
        public IEnumerator WithoutTheTrigger_TheDpadStopsAsBefore_AndNoAbilityIsPicked()
        {
            yield return Tap(pad.dpad.down);

            Assert.That(stops, Is.EqualTo(1));
            Assert.That(ability3Uses, Is.EqualTo(0));
            Assert.That(gate.IsSuppressing, Is.False);
        }

        [UnityTest]
        public IEnumerator HoldingTheTrigger_ADpadDirectionPicksTheAbility_AndDoesNotAlsoStop()
        {
            Press(pad.rightTrigger);
            yield return null;
            Assert.That(gate.IsSuppressing, Is.True);

            yield return Tap(pad.dpad.down);
            yield return Tap(pad.dpad.up);

            Assert.That(ability3Uses, Is.EqualTo(1), "RT + down is Ability3");
            Assert.That(ability1Uses, Is.EqualTo(1), "RT + up is Ability1");
            Assert.That(stops, Is.EqualTo(0), "The held trigger turns the D-pad into ability slots only");
            Release(pad.rightTrigger);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ReleasingTheTrigger_GivesTheDpadItsOrdinaryJobsBack()
        {
            Press(pad.rightTrigger);
            yield return null;
            yield return Tap(pad.dpad.down);
            Release(pad.rightTrigger);
            yield return null;
            yield return null;

            Assert.That(gate.IsSuppressing, Is.False);
            Assert.That(stop.enabled, Is.True);
            yield return Tap(pad.dpad.down);
            Assert.That(stops, Is.EqualTo(1), "Stop works again after the trigger is released");
            Assert.That(ability3Uses, Is.EqualTo(1), "and only the held-trigger press picked an ability");
        }

        [UnityTest]
        public IEnumerator ADisabledAction_IsNotEnabledByTheGate()
        {
            follow.Disable();
            Press(pad.rightTrigger);
            yield return null;
            Release(pad.rightTrigger);
            yield return null;
            yield return null;

            Assert.That(follow.enabled, Is.False, "The gate only gives back what it took");
            Assert.That(stop.enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator UnpluggingThePadWhileTheTriggerIsHeld_RestoresTheSuppressedActions()
        {
            Press(pad.rightTrigger);
            yield return null;
            Assert.That(stop.enabled, Is.False, "Precondition: suppressed while held");

            InputSystem.RemoveDevice(pad);
            yield return null;
            yield return null;

            Assert.That(gate.IsSuppressing, Is.False);
            Assert.That(stop.enabled, Is.True);
            Assert.That(follow.enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator SwitchingBindingGroupsWhileTheTriggerIsHeld_NeverLeavesActionsOff()
        {
            Press(pad.rightTrigger);
            yield return null;
            TestControls.UseGroup(actions, "KeyboardMouse");
            yield return null;
            yield return null;

            Assert.That(gate.IsSuppressing, Is.False, "No pad binding is active, so the menu is not held");
            Assert.That(stop.enabled, Is.True);
        }
    }
}
#endif
