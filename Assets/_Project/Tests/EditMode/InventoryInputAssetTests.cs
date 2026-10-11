using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class InventoryInputAssetTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";
        static readonly string[] PadGroups = { "Xbox", "PlayStation", "Nintendo", "Gamepad" };

        static InputActionAsset Load() => AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);

        static string[] GroupsOf(InputBinding b) =>
            b.groups.Split(new[] { InputBinding.Separator }, System.StringSplitOptions.RemoveEmptyEntries);

        [Test]
        public void InventoryToggle_IsOnI_AndOnTheSelectButtonOfEveryPadFamily()
        {
            var toggle = Load().FindAction("Inventory/Toggle", throwIfNotFound: true);

            var keyboard = toggle.bindings.Where(b => GroupsOf(b).Contains("KeyboardMouse")).Select(b => b.path).ToArray();
            Assert.That(keyboard, Is.EqualTo(new[] { "<Keyboard>/i" }));
            foreach (var group in PadGroups)
            {
                var pad = toggle.bindings.Where(b => GroupsOf(b).Contains(group)).Select(b => b.path).ToArray();
                Assert.That(pad, Is.EqualTo(new[] { "<Gamepad>/select" }), group);
            }
        }

        [Test]
        public void TheSelectButton_IsBoundOnlyToTheInventoryToggle()
        {
            var users = Load().bindings.Where(b => b.path == "<Gamepad>/select").Select(b => b.action).Distinct().ToArray();
            Assert.That(users, Is.EqualTo(new[] { "Toggle" }));
        }

        [Test]
        public void TheKeyI_IsUsedByNothingElse()
        {
            var users = Load().bindings.Where(b => b.path == "<Keyboard>/i").ToArray();
            Assert.That(users, Has.Length.EqualTo(1));
        }

        [Test]
        public void TabActions_AndKeyboardNavigation_ExistInTheUiMap()
        {
            var asset = Load();
            foreach (var name in new[] { "UI/PreviousTab", "UI/NextTab" })
            {
                var action = asset.FindAction(name, throwIfNotFound: true);
                foreach (var group in PadGroups.Concat(new[] { "KeyboardMouse" }))
                    Assert.That(action.bindings.Any(b => GroupsOf(b).Contains(group)), Is.True, $"{name} has no {group} binding");
            }
            foreach (var name in new[] { "UI/Navigate", "UI/Submit", "UI/Cancel" })
                Assert.That(asset.FindAction(name, throwIfNotFound: true).bindings.Any(b => GroupsOf(b).Contains("KeyboardMouse")), Is.True, name);
        }

        [Test]
        public void EveryNewBinding_HasAGroup_AndAUniqueId()
        {
            var asset = Load();
            foreach (var path in new[] { "Inventory/Toggle", "UI/PreviousTab", "UI/NextTab" })
                foreach (var binding in asset.FindAction(path).bindings)
                    Assert.That(binding.groups, Is.Not.Empty, $"{path} {binding.path}");
            var ids = asset.bindings.Select(b => b.id).ToList();
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
        }
    }
}
