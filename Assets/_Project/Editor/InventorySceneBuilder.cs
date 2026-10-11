#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Wires the inventory into the ProceduralMission scene: the SquadInventory (catalogue, starter loadout, roster, director),
    /// the loot table on the director, MissionSystems.inventory, and the InventoryModal with its toggle action and the scene's
    /// input device and intelligence service. Idempotent: objects already there are reused and re-pointed. The Prototype scene
    /// is left alone (it keeps its capsule units and the old flow).
    /// </summary>
    public static class InventorySceneBuilder
    {
        const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
        const string ControlsPath = "Assets/_Project/Input/BlackglassControls.inputactions";

        [MenuItem("Blackglass/Inventory/Wire ProceduralMission Scene")]
        public static void WireMenu() => Wire();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void WireFromCommandLine() => Wire();

        public static void Wire()
        {
            // The scene is opened first: opening it in Single mode unloads unreferenced assets, which would turn assets
            // loaded before it into destroyed objects (saved as missing references).
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = FindOne<MissionDirector>();
            var roster = FindOne<SquadRoster>();
            var catalogue = Load<ItemCatalogue>(InventoryDataBuilder.CataloguePath);
            var starter = Load<StarterLoadout>(InventoryDataBuilder.StarterPath);
            var loot = Load<LootTable>(InventoryDataBuilder.LootTablePath);
            var controls = Load<InputActionAsset>(ControlsPath);
            var intelligence = FindOne<IntelligenceService>();
            var device = FindOne<ActiveInputDevice>();

            var inventory = FindOrAdd<SquadInventory>("Inventory");
            var inventoryObject = new SerializedObject(inventory);
            Set(inventoryObject, "catalogue", catalogue);
            Set(inventoryObject, "starter", starter);
            Set(inventoryObject, "roster", roster);
            Set(inventoryObject, "director", director);
            inventoryObject.FindProperty("bagCapacity").intValue = 8;
            inventoryObject.FindProperty("startInLoadout").boolValue = true;
            inventoryObject.ApplyModifiedPropertiesWithoutUndo();

            var directorObject = new SerializedObject(director);
            Set(directorObject, "systems.inventory", inventory);
            Set(directorObject, "lootTable", loot);
            directorObject.ApplyModifiedPropertiesWithoutUndo();

            var modal = FindOrAdd<InventoryModal>("InventoryModal");
            var modalObject = new SerializedObject(modal);
            Set(modalObject, "inventory", inventory);
            Set(modalObject, "roster", roster);
            Set(modalObject, "director", director);
            Set(modalObject, "intelligence", intelligence);
            Set(modalObject, "controls", controls);
            Set(modalObject, "toggle", ActionReference("Inventory", "Toggle"));
            Set(modalObject, "inputDevice", device);
            modalObject.FindProperty("openOnStart").boolValue = true;
            modalObject.ApplyModifiedPropertiesWithoutUndo();

            // The HUD's Bag chip opens the panel; a scene without the HUD simply has no chip.
            var hud = UnityEngine.Object.FindFirstObjectByType<TacticalHud>();
            if (hud != null)
            {
                var hudObject = new SerializedObject(hud);
                Set(hudObject, "sources.inventoryModal", modal);
                hudObject.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException("Could not save " + ScenePath + ".");
        }

        static T FindOne<T>() where T : Component
        {
            var found = UnityEngine.Object.FindFirstObjectByType<T>();
            if (found == null)
                throw new InvalidOperationException($"ProceduralMission needs a {typeof(T).Name}: run the scene builder that adds it first.");
            return found;
        }

        static T FindOrAdd<T>(string objectName) where T : Component
        {
            var existing = UnityEngine.Object.FindFirstObjectByType<T>();
            return existing != null ? existing : new GameObject(objectName).AddComponent<T>();
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Missing asset " + path + ". Run Blackglass/Inventory/Create Prototype Items first.");
            return asset;
        }

        static void Set(SerializedObject serialized, string path, UnityEngine.Object value)
        {
            var property = serialized.FindProperty(path);
            if (property == null)
                throw new InvalidOperationException($"No serialized field '{path}' on {serialized.targetObject.name}.");
            if (value == null)
                throw new InvalidOperationException($"Cannot wire '{path}' on {serialized.targetObject.name}: the value is missing.");
            property.objectReferenceValue = value;
        }

        static InputActionReference ActionReference(string map, string action)
        {
            var reference = FindReference(map, action);
            if (reference == null)
            {
                AssetDatabase.ImportAsset(ControlsPath, ImportAssetOptions.ForceUpdate);
                reference = FindReference(map, action);
            }
            if (reference == null)
                throw new InvalidOperationException($"No InputActionReference for '{map}/{action}' in {ControlsPath}.");
            return reference;
        }

        static InputActionReference FindReference(string map, string action) =>
            AssetDatabase.LoadAllAssetsAtPath(ControlsPath).OfType<InputActionReference>()
                .FirstOrDefault(r => r.action != null && r.action.actionMap.name == map && r.action.name == action);
    }
}
#endif
