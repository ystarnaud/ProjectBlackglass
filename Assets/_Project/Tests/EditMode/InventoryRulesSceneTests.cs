using Blackglass.BackendTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Blackglass.Tests
{
    /// <summary>
    /// The backend catalogue's bag capacity comes from the InventoryRules asset; the offline scene keeps its own serialized
    /// value. This only reads the scene (additively, never saved), so the offline gameplay is untouched, and it fails when
    /// either side is changed without the other.
    /// </summary>
    public class InventoryRulesSceneTests
    {
        const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
        const string RulesPath = "Assets/_Project/Data/Items/InventoryRules.asset";

        [Test]
        public void The_InventoryRules_asset_exists_and_is_in_the_backends_range()
        {
            var rules = AssetDatabase.LoadAssetAtPath<InventoryRules>(RulesPath);
            Assert.That(rules != null, Is.True, "missing " + RulesPath + ": run Blackglass > Inventory > Create Prototype Items");
            Assert.That(rules.BagCapacity, Is.InRange(InventoryRules.MinBagCapacity, InventoryRules.MaxBagCapacity));
        }

        [Test]
        public void The_project_loader_finds_the_InventoryRules_asset()
        {
            Assert.That(CatalogueSources.InventoryRulesPath, Is.EqualTo(RulesPath));
            Assert.That(CatalogueSources.LoadProject().InventoryRules != null, Is.True);
        }

        [Test]
        public void The_ProceduralMission_scene_bag_capacity_equals_InventoryRules()
        {
            var rules = AssetDatabase.LoadAssetAtPath<InventoryRules>(RulesPath);
            Assert.That(rules != null, Is.True, "missing " + RulesPath);

            // Read the scene if the Editor already has it loaded (and leave it open); only close a scene this test opened.
            var scene = SceneManager.GetSceneByPath(ScenePath);
            var openedHere = !scene.isLoaded;
            if (openedHere)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                var found = 0;
                foreach (var inventory in UnityEngine.Object.FindObjectsByType<SquadInventory>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None))
                {
                    if (inventory.gameObject.scene != scene)
                        continue;
                    found++;
                    var serialized = new SerializedObject(inventory);
                    var capacity = serialized.FindProperty("bagCapacity");
                    Assert.That(capacity, Is.Not.Null, "SquadInventory no longer serializes bagCapacity");
                    Assert.That(capacity.intValue, Is.EqualTo(rules.BagCapacity),
                        "The offline scene's SquadInventory.bagCapacity and the InventoryRules asset (the backend's authority) disagree.");
                }
                Assert.That(found, Is.EqualTo(1), "expected exactly one SquadInventory in " + ScenePath);
            }
            finally
            {
                if (openedHere)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
