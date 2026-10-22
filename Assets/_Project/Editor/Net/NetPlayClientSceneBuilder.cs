#if UNITY_EDITOR
using System;
using System.Linq;
using FishNet.Managing.Object;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the play client's scene (decision 052): a camera rig with the offline TacticalCameraController, a light, and one
    /// object holding the client, map view, avatars, controls, HUD and bootstrap. No ground plane: the projected map is the floor.
    /// Rebuilt from scratch every run; no other scene is opened or changed.
    /// </summary>
    public static class NetPlayClientSceneBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/NetPlayClient.unity";
        const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        [MenuItem("Blackglass/Net/Build Net Play Client Scene")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build();

        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var light = new GameObject("Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(55f, 35f, 0f);

            var rig = new GameObject("CameraRig");
            var cameraObject = new GameObject("Camera") { tag = "MainCamera" };
            cameraObject.transform.SetParent(rig.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.06f, 0.08f);
            cameraObject.AddComponent<AudioListener>();
            var cameraRig = rig.AddComponent<TacticalCameraController>();
            cameraRig.Initialize(camera, Action("Camera", "Pan"), Action("Camera", "Rotate"), Action("Camera", "RotateDrag"),
                Action("Camera", "PointerPosition"), Action("Camera", "Zoom"));

            var holder = new GameObject("NetPlayClient");
            var client = holder.AddComponent<NetClient>();
            client.Initialize(AssetDatabase.LoadAssetAtPath<PrefabObjects>("Assets/DefaultPrefabObjects.asset"));
            var map = holder.AddComponent<NetMapView>();
            map.Initialize(client, Load<EnvironmentTheme>("Assets/_Project/Environment/Themes/CorporatePrototype/CorporatePrototype.asset"),
                Load<Material>("Assets/_Project/Materials/Ground.mat"), Load<Material>("Assets/_Project/Materials/Obstacle.mat"));
            var controls = holder.AddComponent<NetPlayControls>();
            controls.Initialize(client, camera, cameraRig, map, Action("Commands", "Command"), Action("Commands", "PointerPosition"),
                Action("Commands", "QueueModifier"), Action("Commands", "Stop"), Action("Commands", "Cancel"), Action("Camera", "Pan"),
                Action("Character", "NextCharacter"), Action("Character", "ToggleFollow"));
            var avatars = holder.AddComponent<NetPlayAvatars>();
            avatars.Initialize(client, null, Load<GameObject>("Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Visual.prefab"),
                Load<WeaponVisualAsset>("Assets/_Project/Data/Items/Visuals/RifleVisual.asset"),
                Load<GameObject>("Assets/_Project/Presentation/Prefabs/TracerRifle.prefab"),
                Load<Material>("Assets/_Project/Materials/SelectionRing.mat"), Load<Material>("Assets/_Project/Materials/IntelMarker.mat"));
            holder.AddComponent<NetPlayHud>().Initialize(client, controls, avatars, map, camera);
            holder.AddComponent<NetPlayBootstrap>().Initialize(client);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Could not save " + ScenePath + ".");
            Debug.Log("NetPlayClientSceneBuilder: saved " + ScenePath + ".");
        }

        static InputActionReference Action(string map, string action)
        {
            var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
                .FirstOrDefault(r => r.action != null && r.action.name == action && r.action.actionMap != null && r.action.actionMap.name == map);
            if (reference == null)
                throw new InvalidOperationException($"No InputActionReference for '{map}/{action}' in {ControlsPath}.");
            return reference;
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path);
            return asset;
        }
    }
}
#endif
