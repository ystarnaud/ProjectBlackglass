using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class OperativeInputAssetTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static InputActionAsset Load()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.That(asset, Is.Not.Null, $"Input actions not found at {AssetPath}");
            return asset;
        }

        [TestCase("ToggleOperativePanel", "<Keyboard>/f9")]
        [TestCase("AddExperience", "<Keyboard>/f10")]
        [TestCase("ResetProgression", "<Keyboard>/f11")]
        public void TheDeveloperMap_HasTheOperativeActions_OnKeyboardKeys(string action, string path)
        {
            var found = Load().FindAction("Developer/" + action);
            Assert.That(found, Is.Not.Null, action);
            Assert.That(found.type, Is.EqualTo(InputActionType.Button));
            Assert.That(found.bindings.Select(b => b.path), Is.EqualTo(new[] { path }));
            Assert.That(found.bindings.Single().groups, Is.EqualTo("KeyboardMouse"));
        }

        [Test]
        public void TheOperativeKeys_DoNotClashWithAnyOtherBinding()
        {
            var keys = new[] { "<Keyboard>/f9", "<Keyboard>/f10", "<Keyboard>/f11" };
            foreach (var key in keys)
            {
                var users = Load().actionMaps.SelectMany(m => m.actions).Where(a => a.bindings.Any(b => b.path == key)).Select(a => a.name).ToArray();
                Assert.That(users, Has.Length.EqualTo(1), key + " is bound by: " + string.Join(", ", users));
            }
        }
    }
}
