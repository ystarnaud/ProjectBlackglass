using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class AbilityPromptTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static string Prompt(string actionPath, InputFamily family) =>
            PromptResolver.GetPrompt(AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath).FindAction(actionPath, throwIfNotFound: true), family);

        [TestCase("Commands/Ability1", InputFamily.Xbox, "RT + D-pad Up")]
        [TestCase("Commands/Ability2", InputFamily.Xbox, "RT + D-pad Right")]
        [TestCase("Commands/Ability3", InputFamily.PlayStation, "R2 + D-pad Down")]
        [TestCase("Commands/Ability4", InputFamily.Nintendo, "ZR + D-pad Left")]
        [TestCase("Commands/Ability1", InputFamily.GenericGamepad, "Right Trigger + D-pad Up")]
        [TestCase("Commands/AbilityMenu", InputFamily.Xbox, "RT")]
        public void AChordReadsAsModifierPlusButton(string actionPath, InputFamily family, string expected)
        {
            Assert.That(Prompt(actionPath, family), Is.EqualTo(expected));
        }

        [Test]
        public void KeyboardShowsTheDigit_AndOtherCompositesKeepTheirName()
        {
            Assert.That(Prompt("Commands/Ability2", InputFamily.KeyboardMouse), Is.EqualTo("2"));
            Assert.That(Prompt("Character/Move", InputFamily.KeyboardMouse), Is.EqualTo("WASD"));
            Assert.That(Prompt("Commands/Ability1", InputFamily.Xbox), Does.Not.Contain("ButtonWithOneModifier"));
        }

        [Test]
        public void AFamilyWithoutABinding_StillShowsADash()
        {
            Assert.That(Prompt("Commands/AbilityMenu", InputFamily.KeyboardMouse), Is.EqualTo("-"));
        }
    }
}
