#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>Adds the play client's components to a test's NetClient host, wired to the project's real assets.</summary>
    internal static class NetPlayKit
    {
        public const string ThemePath = "Assets/_Project/Environment/Themes/CorporatePrototype/CorporatePrototype.asset";

        public static T Load<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert(asset != null, "missing " + path);
            return asset;
        }

        public static NetMapView AddMapView(GameObject host, NetClient client, bool themed)
        {
            var view = host.AddComponent<NetMapView>();
            view.Initialize(client, themed ? Load<EnvironmentTheme>(ThemePath) : null,
                Load<Material>("Assets/_Project/Materials/Ground.mat"), Load<Material>("Assets/_Project/Materials/Obstacle.mat"));
            return view;
        }

        static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new System.InvalidOperationException(message);
        }
    }
}
#endif
