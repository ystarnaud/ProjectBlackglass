using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class AbilityInputAssetTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";
        static readonly string[] PadGroups = { "Xbox", "PlayStation", "Nintendo", "Gamepad" };
        static readonly string[] Abilities = { "Ability1", "Ability2", "Ability3", "Ability4" };
        static readonly string[] Directions = { "up", "right", "down", "left" };

        static InputActionAsset Load()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.That(asset, Is.Not.Null, $"Input actions not found at {AssetPath}");
            return asset;
        }

        static InputBinding[] BindingsIn(string actionPath, string group) =>
            Load().FindAction(actionPath, throwIfNotFound: true).bindings
                .Where(b => b.groups.Split(InputBinding.Separator).Contains(group)).ToArray();

        [Test]
        public void TheAbilityActionsAndTheMenuAction_ExistAsButtons()
        {
            foreach (var name in Abilities.Concat(new[] { "AbilityMenu" }))
            {
                var action = Load().FindAction("Commands/" + name);
                Assert.That(action, Is.Not.Null, name);
                Assert.That(action.type, Is.EqualTo(InputActionType.Button), name);
            }
        }

        [Test]
        public void Keyboard_BindsTheDigitsOneToFour()
        {
            for (var i = 0; i < Abilities.Length; i++)
            {
                var bindings = BindingsIn("Commands/" + Abilities[i], "KeyboardMouse");
                Assert.That(bindings.Select(b => b.path), Is.EqualTo(new[] { $"<Keyboard>/{i + 1}" }), Abilities[i]);
            }
            Assert.That(BindingsIn("Commands/AbilityMenu", "KeyboardMouse"), Is.Empty, "The menu is a controller thing");
        }

        [Test]
        public void EveryPadFamily_BindsAbilitiesAsTheRightTriggerPlusADpadDirection()
        {
            foreach (var group in PadGroups)
            {
                for (var i = 0; i < Abilities.Length; i++)
                {
                    var bindings = BindingsIn("Commands/" + Abilities[i], group);
                    Assert.That(bindings, Has.Length.EqualTo(3), $"{Abilities[i]} in {group}: a composite and two parts");
                    Assert.That(bindings[0].isComposite, Is.True, $"{Abilities[i]} in {group}");
                    Assert.That(bindings[0].path, Is.EqualTo("ButtonWithOneModifier"), $"{Abilities[i]} in {group}");
                    Assert.That(bindings[1].name, Is.EqualTo("modifier"));
                    Assert.That(bindings[1].path, Is.EqualTo("<Gamepad>/rightTrigger"), $"{Abilities[i]} in {group}");
                    Assert.That(bindings[2].name, Is.EqualTo("button"));
                    Assert.That(bindings[2].path, Is.EqualTo($"<Gamepad>/dpad/{Directions[i]}"), $"{Abilities[i]} in {group}");
                }
            }
        }

        [Test]
        public void TheMenuAction_IsTheSameTriggerAsTheCameraModifier_InEveryPadFamily()
        {
            foreach (var group in PadGroups)
            {
                var menu = BindingsIn("Commands/AbilityMenu", group).Select(b => b.path).ToArray();
                var modifier = BindingsIn("Camera/CameraModifier", group).Select(b => b.path).ToArray();
                Assert.That(menu, Is.EqualTo(modifier), group);
                Assert.That(menu, Is.EqualTo(new[] { "<Gamepad>/rightTrigger" }), group);
            }
        }

        [Test]
        public void TheDpadActionsTheChordClashesWith_AreStillBoundPlain()
        {
            foreach (var group in PadGroups)
            {
                Assert.That(BindingsIn("Commands/Stop", group).Select(b => b.path), Is.EqualTo(new[] { "<Gamepad>/dpad/down" }), group);
                Assert.That(BindingsIn("Character/ToggleFollow", group).Select(b => b.path), Is.EqualTo(new[] { "<Gamepad>/dpad/up" }), group);
                Assert.That(BindingsIn("Commands/NextTarget", group).Select(b => b.path), Is.EqualTo(new[] { "<Gamepad>/dpad/right" }), group);
                Assert.That(BindingsIn("Commands/PreviousTarget", group).Select(b => b.path), Is.EqualTo(new[] { "<Gamepad>/dpad/left" }), group);
            }
        }
    }
}
