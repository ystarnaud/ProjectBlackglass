using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    public class HudInputAssetTests
    {
        const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static InputActionAsset Load()
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.That(asset, Is.Not.Null, $"Input actions not found at {AssetPath}");
            return asset;
        }

        [Test]
        public void TheDeveloperMap_HasToggleDebugOverlay_OnF1_ForKeyboardMouseOnly()
        {
            var found = Load().FindAction("Developer/ToggleDebugOverlay");
            Assert.That(found, Is.Not.Null);
            Assert.That(found.type, Is.EqualTo(InputActionType.Button));
            Assert.That(found.bindings.Select(b => b.path), Is.EqualTo(new[] { "<Keyboard>/f1" }));
            Assert.That(found.bindings.Single().groups, Is.EqualTo("KeyboardMouse"));
        }

        [Test]
        public void F1_IsBoundByNoOtherAction()
        {
            var users = Load().actionMaps.SelectMany(m => m.actions)
                .Where(a => a.bindings.Any(b => b.path == "<Keyboard>/f1")).Select(a => a.name).ToArray();
            Assert.That(users, Is.EqualTo(new[] { "ToggleDebugOverlay" }));
        }

        [Test]
        public void TheActionHasAnInputActionReference_ForSceneWiring()
        {
            var references = AssetDatabase.LoadAllAssetsAtPath(AssetPath).OfType<InputActionReference>().Select(r => r.action?.name).ToArray();
            Assert.That(references, Has.Member("ToggleDebugOverlay"));
        }
    }
}
