#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XInput;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>Proves the action structure supports runtime rebinding; there is no rebinding UI yet.</summary>
    public class RebindingTests : InputTestFixture
    {
        InputActionAsset copy;
        Gamepad pad;

        public override void Setup()
        {
            base.Setup();
            pad = InputSystem.AddDevice<XInputController>();
            copy = InputActionAsset.FromJson(TestControls.Load().ToJson());
            copy.bindingMask = InputBinding.MaskByGroup("Xbox");
        }

        public override void TearDown()
        {
            copy.Disable();
            Object.DestroyImmediate(copy);
            base.TearDown();
        }

        static int XboxBindingIndex(InputAction action) =>
            Enumerable.Range(0, action.bindings.Count).First(i => action.bindings[i].groups == "Xbox");

        [UnityTest]
        public IEnumerator OverridingAGamepadBinding_ChangesWhichButtonFiresTheAction_AndThePrompt()
        {
            var attack = copy.FindAction("Commands/Attack", throwIfNotFound: true);
            attack.Enable();
            var fired = 0;
            attack.performed += _ => fired++;

            Press(pad.buttonWest);
            yield return null;
            Release(pad.buttonWest);
            yield return null;
            Assert.That(fired, Is.EqualTo(1), "West attacks by default");

            attack.ApplyBindingOverride(XboxBindingIndex(attack), "<Gamepad>/buttonNorth");
            yield return null;
            Press(pad.buttonWest);
            yield return null;
            Release(pad.buttonWest);
            yield return null;
            Assert.That(fired, Is.EqualTo(1), "West no longer attacks after the rebind");

            Press(pad.buttonNorth);
            yield return null;
            Release(pad.buttonNorth);
            yield return null;
            Assert.That(fired, Is.EqualTo(2), "North attacks after the rebind");
            Assert.That(PromptResolver.GetPrompt(attack, InputFamily.Xbox), Is.EqualTo("Y"));
        }

        [UnityTest]
        public IEnumerator InteractiveRebinding_AssignsThePressedButton()
        {
            var attack = copy.FindAction("Commands/Attack", throwIfNotFound: true);
            var index = XboxBindingIndex(attack);
            var completed = false;
            var operation = attack.PerformInteractiveRebinding(index)
                .WithControlsHavingToMatchPath("<Gamepad>")
                .WithCancelingThrough("<Keyboard>/escape")
                .OnComplete(_ => completed = true)
                .Start();
            yield return null;

            Press(pad.buttonSouth);
            yield return TestWorld.WaitUntil(() => completed, 1f);
            Release(pad.buttonSouth);
            yield return null;
            operation.Dispose();

            Assert.That(completed, Is.True, "The interactive rebind never completed");
            Assert.That(attack.bindings[index].overridePath, Is.Not.Null.And.Not.Empty);
            Assert.That(attack.bindings[index].effectivePath, Does.EndWith("buttonSouth"));
        }

        [Test]
        public void Overrides_RoundTripThroughJson()
        {
            var attack = copy.FindAction("Commands/Attack", throwIfNotFound: true);
            attack.ApplyBindingOverride(XboxBindingIndex(attack), "<Gamepad>/buttonNorth");
            var json = copy.SaveBindingOverridesAsJson();

            var fresh = InputActionAsset.FromJson(TestControls.Load().ToJson());
            try
            {
                fresh.LoadBindingOverridesFromJson(json);
                Assert.That(PromptResolver.GetPrompt(fresh.FindAction("Commands/Attack"), InputFamily.Xbox), Is.EqualTo("Y"));
                Assert.That(PromptResolver.GetPrompt(fresh.FindAction("Commands/Attack"), InputFamily.PlayStation), Is.EqualTo("Square"),
                    "Rebinding one family leaves the others alone");
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }
        }
    }
}
#endif
