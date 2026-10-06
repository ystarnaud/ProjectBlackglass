using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class PromptResolverTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static InputActionAsset Load() => AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);

        static string Prompt(string actionPath, InputFamily family) =>
            PromptResolver.GetPrompt(Load().FindAction(actionPath, throwIfNotFound: true), family);

        [TestCase("Commands/Confirm", InputFamily.Xbox, "A")]
        [TestCase("Commands/Confirm", InputFamily.PlayStation, "Cross")]
        [TestCase("Commands/Confirm", InputFamily.Nintendo, "A")]
        [TestCase("Commands/Confirm", InputFamily.GenericGamepad, "South")]
        [TestCase("Commands/Cancel", InputFamily.Xbox, "B")]
        [TestCase("Commands/Cancel", InputFamily.PlayStation, "Circle")]
        [TestCase("Commands/Cancel", InputFamily.Nintendo, "B")]
        [TestCase("Commands/Cancel", InputFamily.GenericGamepad, "East")]
        [TestCase("Commands/Attack", InputFamily.Xbox, "X")]
        [TestCase("Commands/Attack", InputFamily.PlayStation, "Square")]
        [TestCase("Commands/Attack", InputFamily.Nintendo, "Y")]
        [TestCase("Character/ToggleCharacterControl", InputFamily.Xbox, "Y")]
        [TestCase("Character/ToggleCharacterControl", InputFamily.PlayStation, "Triangle")]
        [TestCase("Character/ToggleCharacterControl", InputFamily.Nintendo, "X")]
        [TestCase("Character/NextCharacter", InputFamily.Xbox, "RB")]
        [TestCase("Character/NextCharacter", InputFamily.PlayStation, "R1")]
        [TestCase("Character/NextCharacter", InputFamily.Nintendo, "R")]
        [TestCase("Commands/QueueModifier", InputFamily.Xbox, "LT")]
        [TestCase("Commands/QueueModifier", InputFamily.PlayStation, "L2")]
        [TestCase("Commands/QueueModifier", InputFamily.Nintendo, "ZL")]
        [TestCase("Camera/CameraModifier", InputFamily.Nintendo, "ZR")]
        [TestCase("Commands/ToggleTacticalPause", InputFamily.Xbox, "Menu")]
        [TestCase("Commands/ToggleTacticalPause", InputFamily.PlayStation, "Options")]
        [TestCase("Commands/ToggleTacticalPause", InputFamily.Nintendo, "+")]
        [TestCase("Character/ToggleFollow", InputFamily.Xbox, "D-pad Up")]
        [TestCase("Character/Move", InputFamily.Xbox, "Left Stick")]
        [TestCase("Commands/CursorMove", InputFamily.PlayStation, "Right Stick")]
        public void GetPrompt_UsesTheFamilysOwnLabels(string actionPath, InputFamily family, string expected)
        {
            Assert.That(Prompt(actionPath, family), Is.EqualTo(expected));
        }

        [Test]
        public void KeyboardMouse_ShowsTheHumanReadableBinding()
        {
            Assert.That(Prompt("Commands/Command", InputFamily.KeyboardMouse), Does.Contain("Left Button"));
            Assert.That(Prompt("Commands/ToggleTacticalPause", InputFamily.KeyboardMouse), Does.Contain("Space"));
            Assert.That(Prompt("Commands/QueueModifier", InputFamily.KeyboardMouse), Does.Contain("Shift"));
            Assert.That(Prompt("Character/Move", InputFamily.KeyboardMouse), Is.EqualTo("WASD"));
        }

        [Test]
        public void ActionWithoutABindingInTheFamily_ShowsADash()
        {
            Assert.That(Prompt("Commands/Command", InputFamily.Xbox), Is.EqualTo("-"));
            Assert.That(Prompt("Commands/Confirm", InputFamily.KeyboardMouse), Is.EqualTo("-"));
            Assert.That(PromptResolver.GetPrompt(null, InputFamily.Xbox), Is.EqualTo("-"));
        }

        [Test]
        public void UnknownControlName_FallsBackToTheName()
        {
            Assert.That(GamepadLabels.Label("buttonFoo", InputFamily.Xbox), Is.EqualTo("buttonFoo"));
        }

        [Test]
        public void ARebind_ChangesThePrompt_ForThatFamilyOnly()
        {
            var copy = InputActionAsset.FromJson(Load().ToJson());
            try
            {
                var attack = copy.FindAction("Commands/Attack", throwIfNotFound: true);
                var xboxIndex = Enumerable.Range(0, attack.bindings.Count).First(i => attack.bindings[i].groups == "Xbox");
                attack.ApplyBindingOverride(xboxIndex, "<Gamepad>/buttonNorth");

                Assert.That(PromptResolver.GetPrompt(attack, InputFamily.Xbox), Is.EqualTo("Y"));
                Assert.That(PromptResolver.GetPrompt(attack, InputFamily.PlayStation), Is.EqualTo("Square"));
            }
            finally
            {
                Object.DestroyImmediate(copy);
            }
        }
    }
}
