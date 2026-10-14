#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blackglass.EditorTools
{
    /// <summary>Builds the isolated Presentation Test Scene, and wires the presenter into the ProceduralMission scene.</summary>
    public static class CombatPresentationSceneBuilder
    {
        public const string PreviewScenePath = "Assets/_Project/Scenes/Presentation Test Scene.unity";
        const string ProfilesRoot = "Assets/_Project/Presentation/Profiles/";
        const string VisualsRoot = "Assets/_Project/Data/Items/Visuals/";
        const string MaterialsRoot = "Assets/_Project/Materials/";

        [MenuItem("Blackglass/Presentation/Build Preview Scene")]
        public static void BuildPreviewMenu() => BuildPreview();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildPreviewFromCommandLine() => BuildPreview();

        public static void BuildPreview()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            Block("Floor", new Vector3(0f, -0.1f, 0f), new Vector3(60f, 0.2f, 60f), ImpactSurfaceKind.Concrete, MaterialsRoot + "Ground.mat");
            var concrete = Block("ConcreteBlock", new Vector3(-5f, 1f, 8f), new Vector3(4f, 2f, 0.8f), ImpactSurfaceKind.Concrete, MaterialsRoot + "Obstacle.mat");
            var metal = Block("MetalBlock", new Vector3(5f, 1f, 8f), new Vector3(3f, 2f, 0.8f), ImpactSurfaceKind.Metal, MaterialsRoot + "Unit.mat");

            var systems = new GameObject("Systems");
            var pause = systems.AddComponent<TacticalPause>();
            var presenter = systems.AddComponent<CombatPresenter>();

            var rig = new GameObject("CameraRig");
            rig.transform.position = new Vector3(0f, 0f, 2f);
            var cameraObject = new GameObject("Camera") { tag = "MainCamera" };
            cameraObject.transform.SetParent(rig.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
            var controller = rig.AddComponent<TacticalCameraController>();
            var impulse = rig.AddComponent<CameraImpulse>();
            var controllerObject = new SerializedObject(controller);
            controllerObject.FindProperty("viewCamera").objectReferenceValue = camera;
            controllerObject.FindProperty("impulse").objectReferenceValue = impulse;
            controllerObject.ApplyModifiedPropertiesWithoutUndo();

            var rifle = Load<WeaponVisualAsset>(VisualsRoot + "RifleVisual.asset");
            var shooter = Unit("Shooter", new Vector3(-3f, 1f, -6f), rifle, presenter, ranged: true);
            var shooterRight = Unit("ShooterRight", new Vector3(6f, 1f, -6f), rifle, presenter, ranged: true);
            var target = Unit("Target", new Vector3(-3f, 1f, 4f), null, presenter, ranged: false);
            var targetRight = Unit("TargetRight", new Vector3(6f, 1f, 4f), null, presenter, ranged: false);
            var healee = Unit("Healee", new Vector3(6f, 1f, -3f), null, presenter, ranged: false);

            var presenterObject = new SerializedObject(presenter);
            presenterObject.FindProperty("pause").objectReferenceValue = pause;
            presenterObject.FindProperty("cameraImpulse").objectReferenceValue = impulse;
            presenterObject.FindProperty("fallbackProfile").objectReferenceValue = Load<CombatPresentationProfile>(ProfilesRoot + "DefaultCombatPresentation.asset");
            presenterObject.FindProperty("supportProfile").objectReferenceValue = Load<CombatPresentationProfile>(ProfilesRoot + "DefaultCombatPresentation.asset");
            presenterObject.ApplyModifiedPropertiesWithoutUndo();

            // The Blast demo: a real caster with the real ability (a cooldown-0 clone, so it can be cast repeatedly), two victims
            // standing between the blocks, and an Encounter so the blast has hostiles to hit (and so friendly units are never hit).
            var previewBlast = BuildPreviewBlast();
            var caster = Unit("Caster", new Vector3(0f, 1f, -2f), null, presenter, ranged: true);
            var victim1 = Unit("BlastVictim_1", new Vector3(-1f, 1f, 8f), null, presenter, ranged: false);
            var victim2 = Unit("BlastVictim_2", new Vector3(1.2f, 1f, 7.4f), null, presenter, ranged: false);
            var blastPoint = new GameObject("BlastPoint");
            blastPoint.transform.position = new Vector3(0f, 0f, 8f);
            var encounter = systems.AddComponent<Encounter>();
            encounter.Initialize(new[] { shooter.GetComponent<Health>(), caster.GetComponent<Health>() },
                new[] { target.GetComponent<Health>(), targetRight.GetComponent<Health>(), victim1.GetComponent<Health>(), victim2.GetComponent<Health>() });
            var casterAbilities = caster.AddComponent<UnitAbilities>();
            casterAbilities.Initialize(encounter, previewBlast);

            var panel = systems.AddComponent<PresentationPreviewPanel>();
            var panelObject = new SerializedObject(panel);
            Set(panelObject, "presenter", presenter);
            Set(panelObject, "pause", pause);
            Set(panelObject, "shooter", shooter.GetComponent<UnitAttacker>());
            Set(panelObject, "shooterRight", shooterRight.GetComponent<UnitAttacker>());
            Set(panelObject, "target", target.GetComponent<Health>());
            Set(panelObject, "targetRight", targetRight.GetComponent<Health>());
            Set(panelObject, "healee", healee.GetComponent<Health>());
            Set(panelObject, "concreteBlock", concrete.transform);
            Set(panelObject, "metalBlock", metal.transform);
            Set(panelObject, "caster", casterAbilities);
            Set(panelObject, "blast", previewBlast);
            Set(panelObject, "blastPoint", blastPoint.transform);
            panelObject.ApplyModifiedPropertiesWithoutUndo();

            if (!EditorSceneManager.SaveScene(scene, PreviewScenePath))
                throw new InvalidOperationException("Could not save " + PreviewScenePath + ".");
        }

        public const string MissionScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";

        [MenuItem("Blackglass/Presentation/Wire ProceduralMission Scene")]
        public static void WireProceduralMissionMenu() => WireProceduralMission();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void WireProceduralMissionFromCommandLine() => WireProceduralMission();

        public static void WireProceduralMission()
        {
            // Open the scene first: opening it in Single mode unloads unreferenced assets.
            var scene = EditorSceneManager.OpenScene(MissionScenePath, OpenSceneMode.Single);
            var director = FindOne<MissionDirector>();
            var pause = FindOne<TacticalPause>();
            var intelligence = FindOne<IntelligenceService>();
            var controller = FindOne<TacticalCameraController>();

            var presenter = UnityEngine.Object.FindFirstObjectByType<CombatPresenter>();
            if (presenter == null)
                presenter = new GameObject("Presentation").AddComponent<CombatPresenter>();
            var impulse = controller.GetComponent<CameraImpulse>();
            if (impulse == null)
                impulse = controller.gameObject.AddComponent<CameraImpulse>();

            var fallback = Load<CombatPresentationProfile>(ProfilesRoot + "DefaultCombatPresentation.asset");
            var presenterObject = new SerializedObject(presenter);
            Set(presenterObject, "pause", pause);
            Set(presenterObject, "intelligence", intelligence);
            Set(presenterObject, "cameraImpulse", impulse);
            Set(presenterObject, "fallbackProfile", fallback);
            Set(presenterObject, "supportProfile", fallback);
            presenterObject.ApplyModifiedPropertiesWithoutUndo();

            var controllerObject = new SerializedObject(controller);
            Set(controllerObject, "impulse", impulse);
            controllerObject.ApplyModifiedPropertiesWithoutUndo();

            var directorObject = new SerializedObject(director);
            Set(directorObject, "systems.presentation", presenter);
            directorObject.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException("Could not save " + MissionScenePath + ".");
        }

        static T FindOne<T>() where T : Component
        {
            var found = UnityEngine.Object.FindFirstObjectByType<T>();
            if (found == null)
                throw new InvalidOperationException($"ProceduralMission needs a {typeof(T).Name}.");
            return found;
        }

        // A clone of the shipped Blast with its cooldown at 0 (everything else, including the presentation profile, as shipped),
        // so the preview button can be pressed repeatedly. Updated in place on a rebuild so its GUID stays.
        static AbilityDefinition BuildPreviewBlast()
        {
            var blast = Load<AbilityDefinition>("Assets/_Project/Data/Abilities/Blast.asset");
            if (blast.Presentation == null)
                throw new InvalidOperationException("Blast has no presentation profile. Run Blackglass/Presentation/Build Effects first.");
            EffectPaths.EnsureFolder(EffectPaths.Preview);
            var path = EffectPaths.Preview + "/PreviewBlast.asset";
            var clone = AssetDatabase.LoadAssetAtPath<AbilityDefinition>(path);
            if (clone == null)
            {
                clone = UnityEngine.Object.Instantiate(blast);
                AssetDatabase.CreateAsset(clone, path);
            }
            else
            {
                EditorUtility.CopySerialized(blast, clone);
            }
            clone.name = "PreviewBlast";
            var serialized = new SerializedObject(clone);
            serialized.FindProperty("cooldown").floatValue = 0f;
            // The copy took Blast's permanent backend id too; a preview clone carries none, so the id is never duplicated.
            serialized.FindProperty("id").stringValue = "";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(clone);
            AssetDatabase.SaveAssets();
            return clone;
        }

        static GameObject Block(string name, Vector3 position, Vector3 scale, ImpactSurfaceKind surface, string materialPath)
        {
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.transform.position = position;
            block.transform.localScale = scale;
            block.AddComponent<ImpactSurface>().Set(surface);
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material != null)
                block.GetComponent<Renderer>().sharedMaterial = material;
            return block;
        }

        // A scene dummy: a capsule with Health, a (ranged) attacker, and, for shooters, a WeaponSocket + the rifle visual.
        static GameObject Unit(string name, Vector3 position, WeaponVisualAsset weapon, CombatPresenter presenter, bool ranged)
        {
            var unit = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            unit.name = name;
            unit.transform.position = position;
            unit.AddComponent<Health>().Initialize(100000);
            unit.AddComponent<UnitAttacker>().Initialize(ranged ? 40f : 2f, 5, 0f, ranged ? CombatRole.Ranged : CombatRole.Melee);
            if (weapon != null)
            {
                var socket = new GameObject(UnitWeaponVisual.SocketName);
                socket.transform.SetParent(unit.transform, false);
                // The rifle prefab is authored for a character hand socket: its model sits ~1 m from the root with the barrel
                // along -X. This pose puts the model at the capsule's right side at chest height, barrel facing +Z (the targets).
                socket.transform.localPosition = new Vector3(-0.17f, -0.5f, -0.6f);
                socket.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                var visual = unit.AddComponent<UnitWeaponVisual>();
                var visualObject = new SerializedObject(visual);
                visualObject.FindProperty("defaultVisual").objectReferenceValue = weapon;
                visualObject.ApplyModifiedPropertiesWithoutUndo();
            }
            var presentation = unit.AddComponent<UnitPresentation>();
            var presentationObject = new SerializedObject(presentation);
            presentationObject.FindProperty("presenter").objectReferenceValue = presenter;
            presentationObject.ApplyModifiedPropertiesWithoutUndo();
            return unit;
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path + ". Run Blackglass/Presentation/Build Effects first.");
            return asset;
        }

        static void Set(SerializedObject serialized, string field, UnityEngine.Object value)
        {
            var property = serialized.FindProperty(field);
            if (property == null)
                throw new InvalidOperationException($"No serialized field '{field}' on {serialized.targetObject.name}.");
            property.objectReferenceValue = value;
        }
    }
}
#endif
