#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Creates the Recon Scan ability and the three intelligence materials, gives Kestrel the scan, and wires the
    /// ProceduralMission scene for battlefield uncertainty (the Blind preset, the service, the presenters, the overlay and
    /// the developer keys, and every consumer's reference). Idempotent; existing assets are kept so hand tuning survives.
    /// </summary>
    public static class IntelligenceSceneBuilder
    {
        const string DataRoot = "Assets/_Project/Data";
        const string ScanPath = DataRoot + "/Abilities/ReconScan.asset";
        const string KestrelPath = DataRoot + "/Operatives/Definitions/Kestrel.asset";
        const string AimedShotPath = DataRoot + "/Abilities/AimedShot.asset";
        const string MaterialRoot = "Assets/_Project/Materials";
        const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
        const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        [MenuItem("Blackglass/Intelligence/Wire ProceduralMission Scene")]
        public static void WireSceneMenu() => BuildAndWire();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildAndWireFromCommandLine() => BuildAndWire();

        public static void BuildAndWire()
        {
            var scan = CreateScan();
            GiveKestrelTheScan(scan);
            var fog = MakeMaterial(MaterialRoot + "/IntelFog.mat", new Color(0.02f, 0.02f, 0.03f, 1f), transparent: false);
            var veil = MakeMaterial(MaterialRoot + "/IntelVeil.mat", new Color(0f, 0f, 0.02f, 0.55f), transparent: true);
            var marker = MakeMaterial(MaterialRoot + "/IntelMarker.mat", new Color(1f, 0.6f, 0.1f, 1f), transparent: false);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            WireScene(fog, veil, marker);
        }

        static AbilityDefinition CreateScan()
        {
            var existing = AssetDatabase.LoadAssetAtPath<AbilityDefinition>(ScanPath);
            if (existing != null)
                return existing;
            var asset = ScriptableObject.CreateInstance<AbilityDefinition>();
            AssetDatabase.CreateAsset(asset, ScanPath);
            var serialized = new SerializedObject(asset);
            serialized.FindProperty("displayName").stringValue = "Recon Scan";
            serialized.FindProperty("targetMode").enumValueIndex = (int)AbilityTargetMode.Ground;
            serialized.FindProperty("range").floatValue = 18f;
            serialized.FindProperty("requiresLineOfSight").boolValue = false;
            serialized.FindProperty("coverRule").enumValueIndex = (int)AbilityCoverRule.Ignored;
            serialized.FindProperty("cooldown").floatValue = 25f;
            serialized.FindProperty("effect").enumValueIndex = (int)AbilityEffect.Reveal;
            serialized.FindProperty("amount").intValue = 0;
            serialized.FindProperty("radius").floatValue = 12f;
            serialized.FindProperty("revealSeconds").floatValue = 6f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // Kestrel (Recon) already has the Aimed Shot; the scan becomes her second ability.
        static void GiveKestrelTheScan(AbilityDefinition scan)
        {
            var kestrel = AssetDatabase.LoadAssetAtPath<OperativeDefinition>(KestrelPath);
            if (kestrel == null)
                throw new InvalidOperationException("Kestrel's definition is missing: run Blackglass/Operatives first.");
            var serialized = new SerializedObject(kestrel);
            var abilities = serialized.FindProperty("abilities");
            for (var i = 0; i < abilities.arraySize; i++)
            {
                if (abilities.GetArrayElementAtIndex(i).objectReferenceValue == scan)
                    return;
            }
            abilities.arraySize++;
            abilities.GetArrayElementAtIndex(abilities.arraySize - 1).objectReferenceValue = scan;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(kestrel);
        }

        static Material MakeMaterial(string path, Color color, bool transparent)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
                return existing;
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                throw new InvalidOperationException("The URP Unlit shader is missing.");
            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            material.SetColor("_BaseColor", color);
            if (transparent)
            {
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_Blend", 0f);
                material.SetFloat("_ZWrite", 0f);
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetOverrideTag("RenderType", "Transparent");
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)RenderQueue.Transparent;
                material.SetShaderPassEnabled("ShadowCaster", false);
            }
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static void WireScene(Material fog, Material veil, Material marker)
        {
            // Open the scene first: opening it in Single mode unloads unreferenced assets (see OperativeDataBuilder).
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = UnityEngine.Object.FindFirstObjectByType<MissionDirector>();
            var encounter = UnityEngine.Object.FindFirstObjectByType<Encounter>();
            if (director == null || encounter == null)
                throw new InvalidOperationException("ProceduralMission needs a MissionDirector and an Encounter.");

            var host = FindOrAdd<IntelligenceService>("Intelligence");
            Set(host, "markerMaterial", marker);

            var fogPresenter = GetOrAdd<FogPresenter>(host.gameObject);
            Set(fogPresenter, "intelligence", host);
            Set(fogPresenter, "fogMaterial", fog);
            Set(fogPresenter, "veilMaterial", veil);

            var map = GetOrAdd<IntelMapView>(host.gameObject);
            Set(map, "intelligence", host);
            Set(map, "encounter", encounter);
            Set(map, "director", director);

            var debug = GetOrAdd<IntelligenceDebugView>(host.gameObject);
            Set(debug, "intelligence", host);
            Set(debug, "encounter", encounter);
            Set(debug, "director", director);

            var gizmos = GetOrAdd<IntelligenceGizmoView>(host.gameObject);
            Set(gizmos, "intelligence", host);

            var input = GetOrAdd<IntelligenceDeveloperInput>(host.gameObject);
            Set(input, "intelligence", host);
            Set(input, "director", director);
            Set(input, "map", map);
            Set(input, "truthViewAction", ActionReference("ToggleTruthView"));
            Set(input, "cyclePresetAction", ActionReference("CycleIntelligencePreset"));
            Set(input, "toggleMapAction", ActionReference("ToggleIntelMap"));

            // The consumers: each takes the service through a serialized `intelligence` reference.
            Set(director, "systems.intelligence", host);
            Consumer<TacticalCursor>(host);
            Consumer<AbilityTargeting>(host);
            Consumer<PlayerCommandInput>(host);
            Consumer<PrototypeHud>(host);
            Consumer<MissionHud>(host);
            Consumer<MissionDebugView>(host);
            Consumer<MissionGizmoView>(host);
            Consumer<CoverView>(host);

            // The scene ships with fog: nothing known but the extraction and what the squad can see.
            director.Settings.intelligence = IntelligenceSettings.Blind();
            EditorUtility.SetDirty(director);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static void Consumer<T>(IntelligenceService service) where T : Component
        {
            var component = UnityEngine.Object.FindFirstObjectByType<T>();
            if (component == null)
            {
                Debug.LogWarning($"IntelligenceSceneBuilder: no {typeof(T).Name} in ProceduralMission; skipped.");
                return;
            }
            Set(component, "intelligence", service);
        }

        static T FindOrAdd<T>(string objectName) where T : Component
        {
            var existing = UnityEngine.Object.FindFirstObjectByType<T>();
            return existing != null ? existing : new GameObject(objectName).AddComponent<T>();
        }

        static T GetOrAdd<T>(GameObject host) where T : Component
        {
            var existing = host.GetComponent<T>();
            return existing != null ? existing : host.AddComponent<T>();
        }

        static void Set(UnityEngine.Object target, string path, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(path);
            if (property == null)
                throw new InvalidOperationException($"{target.GetType().Name} has no serialized field '{path}'.");
            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static InputActionReference ActionReference(string actionName)
        {
            var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
                .FirstOrDefault(r => r.action != null && r.action.name == actionName);
            if (reference == null)
                throw new InvalidOperationException($"No InputActionReference for '{actionName}' in {ControlsPath}.");
            return reference;
        }
    }
}
#endif
