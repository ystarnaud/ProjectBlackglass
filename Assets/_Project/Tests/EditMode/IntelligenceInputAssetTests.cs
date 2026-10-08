using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class IntelligenceInputAssetTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static InputActionAsset Load()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.That(asset, Is.Not.Null, $"Input actions not found at {AssetPath}");
            return asset;
        }

        [TestCase("ToggleTruthView", "<Keyboard>/f12")]
        [TestCase("CycleIntelligencePreset", "<Keyboard>/f5")]
        [TestCase("ToggleIntelMap", "<Keyboard>/m")]
        public void TheDeveloperMap_HasTheIntelligenceActions_OnKeyboardKeys(string action, string path)
        {
            var found = Load().FindAction("Developer/" + action);
            Assert.That(found, Is.Not.Null, action);
            Assert.That(found.type, Is.EqualTo(InputActionType.Button));
            Assert.That(found.bindings.Select(b => b.path), Is.EqualTo(new[] { path }));
            Assert.That(found.bindings.Single().groups, Is.EqualTo("KeyboardMouse"));
        }

        [Test]
        public void TheIntelligenceKeys_DoNotClashWithAnyOtherBinding()
        {
            foreach (var key in new[] { "<Keyboard>/f12", "<Keyboard>/f5", "<Keyboard>/m" })
            {
                var users = Load().actionMaps.SelectMany(m => m.actions).Where(a => a.bindings.Any(b => b.path == key)).Select(a => a.name).ToArray();
                Assert.That(users, Has.Length.EqualTo(1), key + " is bound by: " + string.Join(", ", users));
            }
        }

        [Test]
        public void TheActionsHaveInputActionReferences_ForSceneWiring()
        {
            var references = AssetDatabase.LoadAllAssetsAtPath(AssetPath).OfType<InputActionReference>().Select(r => r.action?.name).ToArray();
            Assert.That(references, Has.Member("ToggleTruthView"));
            Assert.That(references, Has.Member("CycleIntelligencePreset"));
            Assert.That(references, Has.Member("ToggleIntelMap"));
        }
    }
}
