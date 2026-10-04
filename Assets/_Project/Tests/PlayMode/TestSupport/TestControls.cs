#if UNITY_EDITOR
using System;
using UnityEngine.InputSystem;

namespace Blackglass.Tests
{
    /// <summary>Gives tests the project's real input actions so bindings are tested too.</summary>
    internal static class TestControls
    {
        public const string AssetPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        public static InputActionAsset Load()
        {
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            if (asset == null)
                throw new InvalidOperationException($"Input actions not found at {AssetPath}");
            return asset;
        }

        public static InputActionReference Ref(InputActionAsset asset, string actionPath) =>
            InputActionReference.Create(asset.FindAction(actionPath, throwIfNotFound: true));
    }
}
#endif
