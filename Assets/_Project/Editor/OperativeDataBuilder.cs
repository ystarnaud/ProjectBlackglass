#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Creates the prototype operative data: three roles, four advancement choices, the progression track and the three
    /// definitions. Existing assets are kept (so hand tuning is never overwritten). Fixed ids are authored here once.
    /// </summary>
    public static class OperativeDataBuilder
    {
        const string Root = "Assets/_Project/Data/Operatives";
        public const string TrackPath = Root + "/Progression.asset";
        const string RoleDir = Root + "/Roles";
        const string ChoiceDir = Root + "/Choices";
        const string DefinitionDir = Root + "/Definitions";
        const string CapsulePrefab = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Friendly.prefab"; // the generic squad member until new designs exist
        const string DariusPrefab = "Assets/Art/Characters/Darius/Prefabs/Darius_Player.prefab";
        const string DataRoot = "Assets/_Project/Data";
        const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
        const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        [MenuItem("Blackglass/Operatives/Create Prototype Operatives (keeps existing assets)")]
        public static void CreateAssetsMenu() => CreateAssets();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildAndWireFromCommandLine()
        {
            CreateAssets();
            WireScene();
        }

        [MenuItem("Blackglass/Operatives/Wire ProceduralMission Scene")]
        public static void WireSceneMenu() => WireScene();

        /// <summary>
        /// Adds the squad roster, the operative panel and the developer input to ProceduralMission and points the director's
        /// systems at the roster. Idempotent: objects already there are reused and re-pointed.
        /// </summary>
        public static void WireScene()
        {
            // The scene is opened first: opening it in Single mode unloads unreferenced assets, which would turn
            // definitions loaded before it into destroyed objects (saved as missing references).
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var track = Load<ProgressionTrack>(TrackPath);
            var squad = new[]
            {
                Load<OperativeDefinition>($"{DefinitionDir}/Darius.asset"),
                Load<OperativeDefinition>($"{DefinitionDir}/Kestrel.asset"),
                Load<OperativeDefinition>($"{DefinitionDir}/Sable.asset"),
            };
            var director = UnityEngine.Object.FindFirstObjectByType<MissionDirector>();
            var active = UnityEngine.Object.FindFirstObjectByType<ActiveCharacter>();
            if (director == null || active == null)
                throw new InvalidOperationException("ProceduralMission needs a MissionDirector and an ActiveCharacter.");

            var roster = FindOrAdd<SquadRoster>("Squad");
            var rosterObject = new SerializedObject(roster);
            SetRefs(rosterObject.FindProperty("startingSquad"), squad);
            SetRef(rosterObject.FindProperty("track"), track);
            SetRef(rosterObject.FindProperty("director"), director);
            rosterObject.ApplyModifiedPropertiesWithoutUndo();

            var panel = FindOrAdd<OperativePanelView>("OperativePanel");
            var panelObject = new SerializedObject(panel);
            SetRef(panelObject.FindProperty("roster"), roster);
            SetRef(panelObject.FindProperty("activeCharacter"), active);
            SetRef(panelObject.FindProperty("director"), director);
            panelObject.ApplyModifiedPropertiesWithoutUndo();

            var input = FindOrAdd<OperativeDeveloperInput>("OperativeDeveloper");
            var inputObject = new SerializedObject(input);
            SetRef(inputObject.FindProperty("roster"), roster);
            SetRef(inputObject.FindProperty("activeCharacter"), active);
            SetRef(inputObject.FindProperty("panel"), panel);
            SetRef(inputObject.FindProperty("toggleAction"), ActionReference("ToggleOperativePanel"));
            SetRef(inputObject.FindProperty("addExperienceAction"), ActionReference("AddExperience"));
            SetRef(inputObject.FindProperty("resetAction"), ActionReference("ResetProgression"));
            inputObject.ApplyModifiedPropertiesWithoutUndo();

            var directorObject = new SerializedObject(director);
            SetRef(directorObject.FindProperty("systems.roster"), roster);
            directorObject.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static T FindOrAdd<T>(string objectName) where T : Component
        {
            var existing = UnityEngine.Object.FindFirstObjectByType<T>();
            if (existing != null)
                return existing;
            return new GameObject(objectName).AddComponent<T>();
        }

        static InputActionReference ActionReference(string actionName)
        {
            var reference = AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
                .FirstOrDefault(r => r.action != null && r.action.name == actionName);
            if (reference == null)
                throw new InvalidOperationException($"No InputActionReference for '{actionName}' in {ControlsPath}.");
            return reference;
        }

        public static void CreateAssets()
        {
            Directory.CreateDirectory(RoleDir);
            Directory.CreateDirectory(ChoiceDir);
            Directory.CreateDirectory(DefinitionDir);
            AssetDatabase.Refresh();

            var assault = Role("Assault", "Front-line fire and area control.", s => s.FindProperty("bonus.attackDamage").floatValue = 0.20f);
            var recon = Role("Recon", "Fast scouting and long-range precision.", s => s.FindProperty("bonus.moveSpeed").floatValue = 0.5f);
            var support = Role("Support", "Keeps the squad standing.", s => s.FindProperty("bonus.abilityPower").floatValue = 0.15f);

            var combat = Choice("Combat", "combat", "Combat Training", "+15% attack damage",
                s => s.FindProperty("modifiers.attackDamage").floatValue = 0.15f);
            var survivability = Choice("Survivability", "survivability", "Reinforced", "+25 max health",
                s => s.FindProperty("modifiers.maxHealth").intValue = 25);
            var mobility = Choice("Mobility", "mobility", "Fleet-footed", "+0.75 m/s move speed",
                s => s.FindProperty("modifiers.moveSpeed").floatValue = 0.75f);
            var ability = Choice("Ability", "ability", "Focus", "+20% ability power, -15% ability cooldown", s =>
            {
                s.FindProperty("modifiers.abilityPower").floatValue = 0.20f;
                s.FindProperty("modifiers.abilityCooldownReduction").floatValue = 0.15f;
            });

            Make<ProgressionTrack>(TrackPath, s =>
            {
                var thresholds = s.FindProperty("xpThresholds");
                var values = new[] { 0, 100, 250, 450, 700 };
                thresholds.arraySize = values.Length;
                for (var i = 0; i < values.Length; i++)
                    thresholds.GetArrayElementAtIndex(i).intValue = values[i];
                SetRefs(s.FindProperty("choices"), combat, survivability, mobility, ability);
                s.FindProperty("missionCompletionXp").intValue = 150;
                s.FindProperty("debugXpStep").intValue = 50;
            });

            var ranged = Load<CombatArchetype>(DataRoot + "/Archetypes/Ranged.asset");
            var marksman = Load<CombatArchetype>(DataRoot + "/Archetypes/Marksman.asset");
            var aimed = Load<AbilityDefinition>(DataRoot + "/Abilities/AimedShot.asset");
            var blast = Load<AbilityDefinition>(DataRoot + "/Abilities/Blast.asset");
            var mend = Load<AbilityDefinition>(DataRoot + "/Abilities/Mend.asset");

            Definition("Darius", "6f1d2a40-3b7c-4e95-8a1f-52c0d9e7b301", assault, Load<GameObject>(DariusPrefab), 130, 5f, ranged, aimed, blast);
            Definition("Kestrel", "a93e5c17-0d48-4f2b-b6a3-7e1c8d4f9a02", recon, Load<GameObject>(CapsulePrefab), 80, 6.5f, marksman, aimed);
            Definition("Sable", "2c84b7e9-51a6-4d03-9f7e-c3a0165d8b03", support, Load<GameObject>(CapsulePrefab), 100, 5.5f, ranged, mend, aimed);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static OperativeRole Role(string name, string description, Action<SerializedObject> fill) =>
            Make<OperativeRole>($"{RoleDir}/{name}.asset", s =>
            {
                s.FindProperty("displayName").stringValue = name;
                s.FindProperty("description").stringValue = description;
                fill(s);
            });

        static AdvancementChoice Choice(string file, string id, string name, string description, Action<SerializedObject> fill) =>
            Make<AdvancementChoice>($"{ChoiceDir}/{file}.asset", s =>
            {
                s.FindProperty("id").stringValue = id;
                s.FindProperty("displayName").stringValue = name;
                s.FindProperty("description").stringValue = description;
                fill(s);
            });

        static OperativeDefinition Definition(string name, string id, OperativeRole role, GameObject prefab, int health, float speed,
            CombatArchetype archetype, params AbilityDefinition[] abilities) =>
            Make<OperativeDefinition>($"{DefinitionDir}/{name}.asset", s =>
            {
                s.FindProperty("id").stringValue = id;
                s.FindProperty("displayName").stringValue = name;
                SetRef(s.FindProperty("role"), role);
                SetRef(s.FindProperty("unitPrefab"), prefab);
                s.FindProperty("baseMaxHealth").intValue = health;
                s.FindProperty("baseMoveSpeed").floatValue = speed;
                SetRef(s.FindProperty("archetype"), archetype);
                SetRefs(s.FindProperty("abilities"), abilities);
            });

        // Creates the asset when absent and fills it; an existing asset is returned untouched.
        static T Make<T>(string path, Action<SerializedObject> fill) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
                return existing;
            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            var serialized = new SerializedObject(asset);
            fill(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // objectReferenceValue is rejected for a freshly created asset in batch mode; the instance id is accepted.
        static void SetRef(SerializedProperty property, UnityEngine.Object value)
        {
            property.objectReferenceValue = value;
            if (property.objectReferenceValue == null && value != null)
                property.objectReferenceInstanceIDValue = value.GetInstanceID();
        }

        static void SetRefs(SerializedProperty array, params UnityEngine.Object[] values)
        {
            array.arraySize = values.Length;
            for (var i = 0; i < values.Length; i++)
                SetRef(array.GetArrayElementAtIndex(i), values[i]);
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
