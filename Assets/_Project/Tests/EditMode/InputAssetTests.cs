using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class InputAssetTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";
        static readonly string[] PadGroups = { "Xbox", "PlayStation", "Nintendo", "Gamepad" };
        static readonly string[] KnownGroups = { "KeyboardMouse", "Xbox", "PlayStation", "Nintendo", "Gamepad" };

        // Every action a controller must be able to drive, as "Map/Action".
        static readonly string[] PadActions =
        {
            "Camera/Pan", "Camera/Look", "Camera/CameraModifier", "Camera/Zoom",
            "Commands/CursorMove", "Commands/Confirm", "Commands/Cancel", "Commands/Attack", "Commands/QueueModifier",
            "Commands/ToggleTacticalPause", "Commands/Stop", "Commands/NextTarget", "Commands/PreviousTarget",
            "Character/Move", "Character/ToggleCharacterControl", "Character/NextCharacter", "Character/PreviousCharacter",
            "Character/ToggleFollow", "UI/Navigate", "UI/Submit", "UI/Cancel",
            "Commands/Ability1", "Commands/Ability2", "Commands/Ability3", "Commands/Ability4", "Commands/AbilityMenu",
            "Inventory/Toggle", "UI/PreviousTab", "UI/NextTab",
        };

        static InputActionAsset Load()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.That(asset, Is.Not.Null, $"Input actions not found at {AssetPath}");
            return asset;
        }

        static string[] GroupsOf(InputBinding binding) =>
            binding.groups.Split(new[] { InputBinding.Separator }, System.StringSplitOptions.RemoveEmptyEntries);

        static IEnumerable<InputBinding> BindingsOf(string actionPath, string group) =>
            Load().FindAction(actionPath, throwIfNotFound: true).bindings
                .Where(b => !b.isComposite && !b.isPartOfComposite && GroupsOf(b).Contains(group));

        static string[] PathsOf(string actionPath, string group) =>
            BindingsOf(actionPath, group).Select(b => b.path).OrderBy(p => p).ToArray();

        [Test]
        public void EveryBindingBelongsToExactlyOneKnownGroup()
        {
            foreach (var binding in Load().bindings)
            {
                var groups = GroupsOf(binding);
                Assert.That(groups, Has.Length.EqualTo(1), $"{binding.action}: {binding.path} must be in exactly one group");
                Assert.That(KnownGroups, Does.Contain(groups[0]), $"{binding.action}: {binding.path}");
            }
        }

        [Test]
        public void KeyboardMouseGroup_HoldsOnlyKeyboardMouseAndPointerPaths_AndPadGroupsOnlyGamepadPaths()
        {
            foreach (var binding in Load().bindings.Where(b => !b.isComposite))
            {
                var group = GroupsOf(binding)[0];
                var isPad = binding.path.StartsWith("<Gamepad>");
                Assert.That(isPad, Is.EqualTo(group != "KeyboardMouse"), $"{binding.action}: {binding.path} is in {group}");
            }
        }

        [Test]
        public void BindingAndActionIds_AreUnique()
        {
            var asset = Load();
            var bindingIds = asset.bindings.Select(b => b.id).ToList();
            Assert.That(bindingIds.Distinct().Count(), Is.EqualTo(bindingIds.Count), "Duplicate binding ID");
            var actionIds = asset.actionMaps.SelectMany(m => m.actions).Select(a => a.id).ToList();
            Assert.That(actionIds.Distinct().Count(), Is.EqualTo(actionIds.Count), "Duplicate action ID");
        }

        [Test]
        public void RenamedActionsExist_AndTheOldNamesAreGone()
        {
            var asset = Load();
            foreach (var renamed in new[]
                     {
                         "Commands/ToggleTacticalPause", "Commands/QueueModifier", "Commands/Cancel",
                         "Character/ToggleCharacterControl", "Character/NextCharacter",
                     })
                Assert.That(asset.FindAction(renamed), Is.Not.Null, renamed);
            foreach (var old in new[]
                     {
                         "Commands/TogglePause", "Commands/Modifier", "Commands/ClearSelection",
                         "Character/Takeover", "Character/CycleCharacter",
                     })
                Assert.That(asset.FindAction(old), Is.Null, old);
        }

        [Test]
        public void ExistingKeyboardMouseBindings_AreUnchanged()
        {
            var expected = new Dictionary<string, string[]>
            {
                ["Camera/Pan"] = new[] { "<Keyboard>/a", "<Keyboard>/d", "<Keyboard>/s", "<Keyboard>/w" },
                ["Camera/Rotate"] = new[] { "<Keyboard>/e", "<Keyboard>/q" },
                ["Camera/RotateDrag"] = new[] { "<Mouse>/rightButton" },
                ["Camera/PointerPosition"] = new[] { "<Pointer>/position" },
                ["Camera/Zoom"] = new[] { "<Mouse>/scroll/y" },
                ["Commands/Command"] = new[] { "<Mouse>/leftButton" },
                ["Commands/PointerPosition"] = new[] { "<Pointer>/position" },
                ["Commands/ToggleTacticalPause"] = new[] { "<Keyboard>/space" },
                ["Commands/QueueModifier"] = new[] { "<Keyboard>/leftShift", "<Keyboard>/rightShift" },
                ["Commands/Stop"] = new[] { "<Keyboard>/x" },
                ["Commands/Cancel"] = new[] { "<Keyboard>/escape" },
                ["Character/Move"] = new[] { "<Keyboard>/a", "<Keyboard>/d", "<Keyboard>/s", "<Keyboard>/w" },
                ["Character/ToggleCharacterControl"] = new[] { "<Keyboard>/v" },
                ["Character/NextCharacter"] = new[] { "<Keyboard>/tab" },
                ["Character/CycleReverse"] = new[] { "<Keyboard>/leftShift", "<Keyboard>/rightShift" },
                ["Character/ToggleFollow"] = new[] { "<Keyboard>/f" },
            };
            foreach (var pair in expected)
            {
                var actual = BindingsOf(pair.Key, "KeyboardMouse").Concat(
                    Load().FindAction(pair.Key).bindings.Where(b => b.isPartOfComposite && GroupsOf(b).Contains("KeyboardMouse")))
                    .Select(b => b.path).OrderBy(p => p).ToArray();
                Assert.That(actual, Is.EqualTo(pair.Value), pair.Key);
            }
        }

        [Test]
        public void EveryPadFamily_BindsEverySemanticAction()
        {
            var asset = Load();
            foreach (var group in PadGroups)
            {
                foreach (var path in PadActions)
                {
                    var bound = asset.FindAction(path, throwIfNotFound: true).bindings.Any(b => GroupsOf(b).Contains(group));
                    Assert.That(bound, Is.True, $"{path} has no {group} binding");
                }
            }
        }

        [Test]
        public void Nintendo_SwapsConfirmAndCancel_OtherFamiliesKeepSouthEast()
        {
            foreach (var action in new[] { "Commands/Confirm", "UI/Submit" })
            {
                Assert.That(PathsOf(action, "Nintendo"), Is.EqualTo(new[] { "<Gamepad>/buttonEast" }), action);
                foreach (var group in new[] { "Xbox", "PlayStation", "Gamepad" })
                    Assert.That(PathsOf(action, group), Is.EqualTo(new[] { "<Gamepad>/buttonSouth" }), $"{action} {group}");
            }
            foreach (var action in new[] { "Commands/Cancel", "UI/Cancel" })
            {
                Assert.That(PathsOf(action, "Nintendo"), Is.EqualTo(new[] { "<Gamepad>/buttonSouth" }), action);
                foreach (var group in new[] { "Xbox", "PlayStation", "Gamepad" })
                    Assert.That(PathsOf(action, group), Is.EqualTo(new[] { "<Gamepad>/buttonEast" }), $"{action} {group}");
            }
        }

        [Test]
        public void ProvisionalLayout_MatchesTheSpec_InEveryFamilyExceptTheNintendoSwap()
        {
            var layout = new Dictionary<string, string>
            {
                ["Commands/Attack"] = "<Gamepad>/buttonWest",
                ["Character/ToggleCharacterControl"] = "<Gamepad>/buttonNorth",
                ["Character/NextCharacter"] = "<Gamepad>/rightShoulder",
                ["Character/PreviousCharacter"] = "<Gamepad>/leftShoulder",
                ["Commands/QueueModifier"] = "<Gamepad>/leftTrigger",
                ["Camera/CameraModifier"] = "<Gamepad>/rightTrigger",
                ["Commands/ToggleTacticalPause"] = "<Gamepad>/start",
                ["Character/ToggleFollow"] = "<Gamepad>/dpad/up",
                ["Commands/Stop"] = "<Gamepad>/dpad/down",
                ["Commands/PreviousTarget"] = "<Gamepad>/dpad/left",
                ["Commands/NextTarget"] = "<Gamepad>/dpad/right",
                ["Character/Move"] = "<Gamepad>/leftStick",
                ["Camera/Pan"] = "<Gamepad>/leftStick",
                ["Camera/Look"] = "<Gamepad>/rightStick",
                ["Commands/CursorMove"] = "<Gamepad>/rightStick",
            };
            foreach (var group in PadGroups)
            {
                foreach (var pair in layout)
                    Assert.That(PathsOf(pair.Key, group), Is.EqualTo(new[] { pair.Value }), $"{pair.Key} {group}");
            }
        }

        [Test]
        public void SelectButton_IsOnlyTheInventoryToggle()
        {
            var users = Load().bindings.Where(b => b.path == "<Gamepad>/select").Select(b => b.action).Distinct();
            Assert.That(users, Is.EqualTo(new[] { "Toggle" }), "owner ruling, Phase 12: the reserved button opens the inventory (decision 043)");
        }

        [Test]
        public void StickBindings_CarryTheDeadzoneProcessor()
        {
            var sticks = Load().bindings.Where(b => b.path == "<Gamepad>/leftStick" || b.path == "<Gamepad>/rightStick").ToList();
            Assert.That(sticks, Is.Not.Empty);
            foreach (var binding in sticks)
                Assert.That(binding.processors, Does.Contain("stickDeadzone"), $"{binding.action} {binding.groups}");
        }

        [Test]
        public void Zoom_HasStickClickBindingsInEveryPadFamily()
        {
            var asset = Load();
            foreach (var group in PadGroups)
            {
                var paths = asset.FindAction("Camera/Zoom").bindings
                    .Where(b => b.isPartOfComposite && GroupsOf(b).Contains(group)).Select(b => b.path).OrderBy(p => p).ToArray();
                Assert.That(paths, Is.EqualTo(new[] { "<Gamepad>/leftStickPress", "<Gamepad>/rightStickPress" }), group);
            }
        }

        [Test]
        public void ControlSchemesExistForEveryGroup()
        {
            var schemes = Load().controlSchemes.Select(s => s.bindingGroup).OrderBy(s => s).ToArray();
            Assert.That(schemes, Is.EqualTo(KnownGroups.OrderBy(s => s).ToArray()));
        }
    }
}
