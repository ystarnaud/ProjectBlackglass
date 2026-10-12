#if UNITY_EDITOR
using System;
using FishNet.Managing.Object;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Builds the net-test client's scene (networking experiment): a camera, a light, a ground plane and one object holding the
    /// client components. Rebuilt from scratch every run, so it is idempotent; no game scene is opened or changed.
    /// </summary>
    public static class NetClientSceneBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/NetTestClient.unity";
        const string Root = "Assets/_Project/";

        [MenuItem("Blackglass/Net/Build Net Test Client Scene")]
        public static void BuildMenu() => Build();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine() => Build();

        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraObject = new GameObject("Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
            camera.orthographic = true;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 40f, 0f), Quaternion.Euler(90f, 0f, 0f));

            var light = new GameObject("Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(60f, 30f, 0f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            UnityEngine.Object.DestroyImmediate(ground.GetComponent<Collider>());
            ground.GetComponent<Renderer>().sharedMaterial = Load<Material>("Materials/Ground.mat");

            var holder = new GameObject("NetClient");
            var client = holder.AddComponent<NetClient>();
            client.Initialize(AssetDatabase.LoadAssetAtPath<PrefabObjects>("Assets/DefaultPrefabObjects.asset"));
            var harness = holder.AddComponent<NetHarness>();
            harness.Initialize(client);
            var view = holder.AddComponent<NetClientView>();
            view.Initialize(client, camera, Load<Material>("Materials/Obstacle.mat"));
            var controls = holder.AddComponent<NetTestControls>();
            controls.Initialize(client, camera);
            holder.AddComponent<NetClientBootstrap>().Initialize(client, harness, controls);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Could not save " + ScenePath + ".");
            Debug.Log("NetClientSceneBuilder: saved " + ScenePath + ".");
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(Root + path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + Root + path);
            return asset;
        }
    }
}
#endif
