#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Creates the prototype Corporate environment kit from Unity primitives: six shared materials, the module prefabs and the
    /// theme asset. Existing assets are kept unless `overwrite` is set, so replacing a prefab with real art is not undone by
    /// running the menu again. Conventions: Docs/EnvironmentAssetGuide.md.
    /// </summary>
    public static class EnvironmentKitBuilder
    {
        const string Root = "Assets/_Project/Environment";
        const string MaterialDir = Root + "/Materials";
        const string PrefabDir = Root + "/Prefabs";
        const string ThemeDir = Root + "/Themes/CorporatePrototype";
        public const string ThemePath = ThemeDir + "/CorporatePrototype.asset";
        const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
        const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        static bool overwrite;
        static Material concrete, metal, floorMat, prop, accent, display;

        [MenuItem("Blackglass/Environment/Create Prototype Kit (keeps existing assets)")]
        public static void CreateKit() => Build(false);

        [MenuItem("Blackglass/Environment/Rebuild Prototype Kit (overwrites)")]
        public static void RebuildKit() => Build(true);

        [MenuItem("Blackglass/Environment/Wire ProceduralMission Scene")]
        public static void WireSceneMenu() => WireScene();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildAndWireFromCommandLine()
        {
            Build(false);
            WireScene();
        }

        public static void Build(bool force)
        {
            overwrite = force;
            Directory.CreateDirectory(MaterialDir);
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(ThemeDir);
            AssetDatabase.Refresh();

            concrete = Mat("BW_Concrete", new Color(0.14f, 0.15f, 0.17f), 0f, 0.25f, null);
            metal = Mat("BW_Metal", new Color(0.07f, 0.08f, 0.09f), 0.7f, 0.45f, null);
            floorMat = Mat("BW_Floor", new Color(0.09f, 0.10f, 0.11f), 0f, 0.35f, null);
            prop = Mat("BW_Prop", new Color(0.30f, 0.32f, 0.35f), 0f, 0.3f, null);
            accent = Mat("BW_Accent", new Color(0.05f, 0.6f, 0.8f), 0f, 0.5f, new Color(0.1f, 1.0f, 1.4f));
            display = Mat("BW_Display", new Color(0.05f, 0.2f, 0.3f), 0f, 0.6f, new Color(0f, 0.5f, 0.8f));

            var floor = Prefab("Floor", r =>
            {
                Part(r, "Slab", new Vector3(0f, -0.1f, 0f), new Vector3(1f, 0.2f, 1f), floorMat, castShadows: false);
                Part(r, "Panel", new Vector3(0f, 0.002f, 0f), new Vector3(0.9f, 0.004f, 0.9f), metal, castShadows: false);
            });
            var straight = Prefab("WallStraight", r =>
            {
                WallBody(r, concrete);
                Part(r, "ConduitN", new Vector3(0f, 2.35f, 0.5f), new Vector3(1f, 0.08f, 0.08f), metal);
                Part(r, "ConduitS", new Vector3(0f, 2.35f, -0.5f), new Vector3(1f, 0.08f, 0.08f), metal);
                Part(r, "StripN", new Vector3(0f, 0.9f, 0.505f), new Vector3(0.5f, 0.04f, 0.01f), accent);
                Part(r, "StripS", new Vector3(0f, 0.9f, -0.505f), new Vector3(0.5f, 0.04f, 0.01f), accent);
            });
            var end = Prefab("WallEnd", r =>
            {
                WallBody(r, concrete);
                Part(r, "EndPlate", new Vector3(0f, 1.6f, -0.51f), new Vector3(0.8f, 2.4f, 0.02f), metal);
            });
            var corner = Prefab("WallCorner", r =>
            {
                WallBody(r, concrete);
                Part(r, "Post", new Vector3(-0.47f, 1.6f, -0.47f), new Vector3(0.12f, 2.64f, 0.12f), metal);
            });
            var junction = Prefab("WallJunction", r =>
            {
                WallBody(r, concrete);
                Part(r, "ConduitFree", new Vector3(0f, 2.35f, -0.5f), new Vector3(1f, 0.08f, 0.08f), metal);
            });
            var doorFrame = Prefab("DoorFrame", r =>
            {
                Part(r, "JambE", new Vector3(0.51f, 1.2f, -0.35f), new Vector3(0.04f, 2.4f, 0.3f), metal);
                Part(r, "JambW", new Vector3(-0.51f, 1.2f, -0.35f), new Vector3(0.04f, 2.4f, 0.3f), metal);
                Part(r, "Header", new Vector3(0f, 2.4f, -0.51f), new Vector3(0.8f, 0.05f, 0.02f), accent);
                Part(r, "StripE", new Vector3(0.4f, 1.2f, -0.515f), new Vector3(0.04f, 2.0f, 0.01f), accent);
                Part(r, "StripW", new Vector3(-0.4f, 1.2f, -0.515f), new Vector3(0.04f, 2.0f, 0.01f), accent);
            });
            var pillar = Prefab("Pillar", r =>
            {
                WallBody(r, metal);
                Part(r, "BandLow", new Vector3(0f, 0.9f, 0f), new Vector3(1.02f, 0.06f, 1.02f), accent);
                Part(r, "BandHigh", new Vector3(0f, 2.2f, 0f), new Vector3(1.02f, 0.06f, 1.02f), accent);
            });
            var lowA = Prefab("LowCover_A", r => LowBody(r, 1f, false));
            var lowB = Prefab("LowCover_B", r => LowBody(r, 1f, true));
            var lowLong = Prefab("LowCoverLong", r => LowBody(r, 2f, true));
            var crate = Prefab("Crate", r =>
            {
                Part(r, "Body", new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f), prop);
                Part(r, "BandLow", new Vector3(0f, 0.1f, 0f), new Vector3(1.02f, 0.08f, 1.02f), metal);
                Part(r, "BandHigh", new Vector3(0f, 0.9f, 0f), new Vector3(1.02f, 0.08f, 1.02f), metal);
            });
            var cabinet = Prefab("Cabinet", r =>
            {
                Part(r, "Body", new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f), metal);
                Part(r, "DoorStrip", new Vector3(0f, 0.55f, 0.505f), new Vector3(0.02f, 0.7f, 0.01f), accent);
                Part(r, "Top", new Vector3(0f, 0.97f, 0f), new Vector3(1f, 0.06f, 1f), prop);
            });
            var terminal = Prefab("Terminal", r =>
            {
                Part(r, "Base", new Vector3(0f, 0.45f, 0f), new Vector3(0.8f, 0.9f, 0.8f), metal);
                Part(r, "Top", new Vector3(0f, 1.15f, 0f), new Vector3(0.8f, 0.1f, 0.8f), prop);
                Part(r, "Neck", new Vector3(0f, 0.97f, 0f), new Vector3(0.7f, 0.25f, 0.7f), concrete);
                Part(r, "Display", new Vector3(0f, 1.0f, 0.36f), new Vector3(0.6f, 0.3f, 0.06f), display);
                Part(r, "Display", new Vector3(0f, 1.0f, -0.36f), new Vector3(0.6f, 0.3f, 0.06f), display);
            });
            var light = Prefab("LightFixture", r =>
            {
                Part(r, "Housing", new Vector3(0f, 2.45f, 0.55f), new Vector3(0.5f, 0.12f, 0.1f), metal);
                Part(r, "Lens", new Vector3(0f, 2.45f, 0.605f), new Vector3(0.44f, 0.05f, 0.02f), accent);
            });

            var theme = AssetDatabase.LoadAssetAtPath<EnvironmentTheme>(ThemePath);
            if (theme == null || overwrite)
            {
                var fresh = EnvironmentTheme.Create(1, new[]
                {
                    Entry(EnvironmentElement.Floor, floor),
                    Entry(EnvironmentElement.WallStraight, straight),
                    Entry(EnvironmentElement.WallEnd, end),
                    Entry(EnvironmentElement.WallCorner, corner),
                    Entry(EnvironmentElement.WallJunction, junction),
                    Entry(EnvironmentElement.DoorFrame, doorFrame),
                    Entry(EnvironmentElement.Pillar, pillar),
                    Entry(EnvironmentElement.LowCover, lowA, lowB),
                    Entry(EnvironmentElement.LowCoverLong, lowLong),
                    Entry(EnvironmentElement.Crate, crate, cabinet),
                    Entry(EnvironmentElement.Terminal, terminal),
                    Entry(EnvironmentElement.LightFixture, light),
                });
                fresh.name = Path.GetFileNameWithoutExtension(ThemePath);
                if (theme == null)
                    AssetDatabase.CreateAsset(fresh, ThemePath);
                else
                {
                    EditorUtility.CopySerialized(fresh, theme);
                    Object.DestroyImmediate(fresh);
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static EnvironmentTheme.Entry Entry(EnvironmentElement element, params GameObject[] variants) =>
            new EnvironmentTheme.Entry { element = element, variants = variants };

        static void WallBody(GameObject root, Material body)
        {
            Part(root, "Plinth", new Vector3(0f, 0.15f, 0f), new Vector3(1f, 0.3f, 1f), metal);
            Part(root, "Body", new Vector3(0f, 1.61f, 0f), new Vector3(1f, 2.62f, 1f), body);
            Part(root, "Cap", new Vector3(0f, 2.96f, 0f), new Vector3(1f, 0.08f, 1f), prop);
        }

        // Low cover: 1 m tall in total, `length` long along X, the top is the light prop material so it reads from the gameplay camera and differs from the dark floor panels.
        static void LowBody(GameObject root, float length, bool stripe)
        {
            Part(root, "Body", new Vector3(0f, 0.45f, 0f), new Vector3(length, 0.9f, 1f), prop);
            Part(root, "Top", new Vector3(0f, 0.95f, 0f), new Vector3(length, 0.1f, 1f), prop);
            if (!stripe)
                return;
            Part(root, "StripN", new Vector3(0f, 0.5f, 0.505f), new Vector3(length * 0.6f, 0.04f, 0.01f), accent);
            Part(root, "StripS", new Vector3(0f, 0.5f, -0.505f), new Vector3(length * 0.6f, 0.04f, 0.01f), accent);
        }

        static GameObject Part(GameObject parent, string name, Vector3 position, Vector3 size, Material material, bool castShadows = true)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent.transform, false);
            part.transform.localPosition = position;
            part.transform.localScale = size;
            part.GetComponent<Renderer>().sharedMaterial = material;
            if (!castShadows)
                part.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return part;
        }

        static GameObject Prefab(string name, System.Action<GameObject> build)
        {
            var path = $"{PrefabDir}/{name}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null && !overwrite)
                return existing;
            var root = new GameObject(name);
            build(root);
            var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        static Material Mat(string name, Color colour, float metallic, float smoothness, Color? emission)
        {
            var path = $"{MaterialDir}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null && !overwrite)
                return existing;
            var material = existing != null ? existing : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", colour);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            if (emission.HasValue)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission.Value);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else
            {
                material.DisableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", Color.black);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }
            if (existing == null)
                AssetDatabase.CreateAsset(material, path);
            else
                EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>Assigns the theme and the F8 action in ProceduralMission, and darkens the lighting a little.</summary>
        public static void WireScene()
        {
            var theme = AssetDatabase.LoadAssetAtPath<EnvironmentTheme>(ThemePath);
            if (theme == null)
                throw new System.InvalidOperationException("Run Build first: no theme at " + ThemePath);
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var director = Object.FindFirstObjectByType<MissionDirector>();
            var directorObject = new SerializedObject(director);
            // objectReferenceValue is rejected for a freshly created asset in batch mode; the instance id is accepted.
            var themeProperty = directorObject.FindProperty("environmentTheme");
            themeProperty.objectReferenceValue = theme;
            if (themeProperty.objectReferenceValue == null)
                themeProperty.objectReferenceInstanceIDValue = theme.GetInstanceID();
            directorObject.ApplyModifiedPropertiesWithoutUndo();

            var toggle = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
                .FirstOrDefault(r => r.action != null && r.action.name == "ToggleVisuals");
            var developer = Object.FindFirstObjectByType<MissionDeveloperInput>();
            if (toggle != null && developer != null)
            {
                var inputObject = new SerializedObject(developer);
                inputObject.FindProperty("toggleVisualsAction").objectReferenceValue = toggle;
                inputObject.ApplyModifiedPropertiesWithoutUndo();
            }
            else
                Debug.LogWarning("ToggleVisuals action reference or MissionDeveloperInput not found; F8 is not wired.");

            // Minimal lighting pass: a dimmer cool key light and a flat dark-blue ambient. Not final lighting.
            foreach (var sun in Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Where(l => l.type == LightType.Directional))
            {
                sun.color = new Color(0.78f, 0.84f, 1f);
                sun.intensity = 0.9f;
                EditorUtility.SetDirty(sun);
            }
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.20f, 0.22f, 0.28f);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }
    }
}
#endif
