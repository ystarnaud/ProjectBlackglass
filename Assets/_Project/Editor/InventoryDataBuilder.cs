#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blackglass.EditorTools
{
    /// <summary>
    /// Creates the prototype item data: six item definitions, their weapon visuals, the catalogue and the starter loadout.
    /// Existing assets are kept (hand tuning is never overwritten). Ids are authored here once and never change.
    /// Also migrates the character prefabs from a baked rifle to the managed UnitWeaponVisual (MigrateRifles).
    /// </summary>
    public static class InventoryDataBuilder
    {
        public const string Root = "Assets/_Project/Data/Items";
        const string VisualDir = Root + "/Visuals";
        const string ArchetypeDir = "Assets/_Project/Data/Archetypes";
        const string DefinitionDir = "Assets/_Project/Data/Operatives/Definitions";
        const string RiflePrefab = "Assets/Art/Weapons/Rifle/Prefabs/Rifle.prefab";
        const string PlaceholderDir = "Assets/Art/Weapons/Placeholders";
        public const string CataloguePath = Root + "/ItemCatalogue.asset";
        public const string StarterPath = Root + "/StarterLoadout.asset";

        [MenuItem("Blackglass/Inventory/Create Prototype Items (keeps existing assets)")]
        public static void CreateAssetsMenu() => CreateAssets();

        /// <summary>Unity -batchmode -executeMethod entry.</summary>
        public static void BuildFromCommandLine()
        {
            CreateAssets();
            AssetDatabase.SaveAssets();
        }

        public static void CreateAssets()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(VisualDir);
            Directory.CreateDirectory(PlaceholderDir);

            var rifleVisual = EnsureVisual("RifleVisual", Load<GameObject>(RiflePrefab), Vector3.zero, Vector3.zero, Vector3.one);
            // Placeholder: the same model, 25% longer. Documented in decision 043; replace with real art later.
            var marksmanVisual = EnsureVisual("MarksmanRifleVisual", Load<GameObject>(RiflePrefab), Vector3.zero, Vector3.zero, new Vector3(1f, 1f, 1.25f));
            var bladeVisual = EnsureVisual("BladeVisual", EnsureBladePrefab(), Vector3.zero, Vector3.zero, Vector3.one);

            var rifle = Ensure("ServiceRifle", "item.service-rifle", "Service Rifle", ItemCategory.Weapon, ItemSlot.Weapon, 1, default,
                Load<CombatArchetype>(ArchetypeDir + "/Ranged.asset"), 0, rifleVisual, "Standard issue. Reliable at medium range.");
            var marksman = Ensure("MarksmanRifle", "item.marksman-rifle", "Marksman Rifle", ItemCategory.Weapon, ItemSlot.Weapon, 1, default,
                Load<CombatArchetype>(ArchetypeDir + "/Marksman.asset"), 0, marksmanVisual, "Long range and heavy hits, but slow to fire.");
            var blade = Ensure("CombatBlade", "item.combat-blade", "Combat Blade", ItemCategory.Weapon, ItemSlot.Weapon, 1, default,
                Load<CombatArchetype>(ArchetypeDir + "/Melee.asset"), 0, bladeVisual, "Close combat only. Ignores cover and sight.");
            var vest = Ensure("LightVest", "item.light-vest", "Light Vest", ItemCategory.Armor, ItemSlot.Armor, 1,
                new StatModifiers { maxHealth = 20 }, null, 0, null, "+20 maximum health.");
            var boots = Ensure("ServoBoots", "item.servo-boots", "Servo Boots", ItemCategory.Utility, ItemSlot.Utility, 1,
                new StatModifiers { moveSpeed = 0.5f }, null, 0, null, "+0.5 m/s movement speed.");
            var medkit = Ensure("Medkit", "item.medkit", "Medkit", ItemCategory.Consumable, ItemSlot.None, 3, default, null,
                Load<AbilityDefinition>("Assets/_Project/Data/Abilities/Mend.asset").Amount, null, "Restores health to the user. Not used at full health.");

            var catalogue = EnsureCatalogue(new[] { rifle, marksman, blade, vest, boots, medkit });
            EnsureStarter(rifle, marksman, vest, boots, blade, medkit);
            EnsureLootTable(rifle, marksman, blade, vest, boots, medkit);
            EditorUtility.SetDirty(catalogue);
        }

        static ItemDefinition Ensure(string file, string id, string displayName, ItemCategory category, ItemSlot slot, int maxStack,
            StatModifiers modifiers, CombatArchetype weapon, int heal, WeaponVisualAsset visual, string description)
        {
            var path = $"{Root}/{file}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
            if (existing != null)
                return existing;
            var item = ItemDefinition.Create(id, displayName, category, slot, maxStack, modifiers, weapon, heal);
            item.SetWeaponVisual(visual);
            item.SetDescription(description);
            if (!item.IsValid(out var problem))
                throw new InvalidOperationException($"{file}: {problem}");
            AssetDatabase.CreateAsset(item, path);
            return item;
        }

        static WeaponVisualAsset EnsureVisual(string file, GameObject prefab, Vector3 position, Vector3 euler, Vector3 scale)
        {
            var path = $"{VisualDir}/{file}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<WeaponVisualAsset>(path);
            if (existing != null)
                return existing;
            var visual = WeaponVisualAsset.Create(prefab, position, euler, scale);
            AssetDatabase.CreateAsset(visual, path);
            return visual;
        }

        // A thin grey box, 0.45 m long, pointing along +Z: the stand-in blade. No collider.
        static GameObject EnsureBladePrefab()
        {
            var path = PlaceholderDir + "/BladePlaceholder.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null)
                return existing;
            var materialPath = PlaceholderDir + "/BladePlaceholder.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                material = new Material(shader) { name = "BladePlaceholder", color = new Color(0.6f, 0.62f, 0.66f) };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            var root = new GameObject("BladePlaceholder");
            var blade = GameObject.CreatePrimitive(PrimitiveType.Cube);
            blade.name = "Blade";
            UnityEngine.Object.DestroyImmediate(blade.GetComponent<Collider>());
            blade.transform.SetParent(root.transform, false);
            blade.transform.localPosition = new Vector3(0f, 0f, 0.25f);
            blade.transform.localScale = new Vector3(0.04f, 0.1f, 0.45f);
            blade.GetComponent<Renderer>().sharedMaterial = material;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            return prefab;
        }

        static ItemCatalogue EnsureCatalogue(ItemDefinition[] items)
        {
            var catalogue = AssetDatabase.LoadAssetAtPath<ItemCatalogue>(CataloguePath);
            if (catalogue == null)
            {
                catalogue = ScriptableObject.CreateInstance<ItemCatalogue>();
                AssetDatabase.CreateAsset(catalogue, CataloguePath);
            }
            var serialized = new SerializedObject(catalogue);
            var list = serialized.FindProperty("items");
            list.arraySize = items.Length;
            for (var i = 0; i < items.Length; i++)
                list.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return catalogue;
        }

        public const string LootTablePath = Root + "/LootTable.asset";

        static void EnsureLootTable(ItemDefinition rifle, ItemDefinition marksman, ItemDefinition blade, ItemDefinition vest,
            ItemDefinition boots, ItemDefinition medkit)
        {
            if (AssetDatabase.LoadAssetAtPath<LootTable>(LootTablePath) != null)
                return;
            var table = LootTable.Create(1, 3, 1, 2, new[]
            {
                Entry(medkit, 1, 2, 4), Entry(rifle, 1, 1, 1), Entry(marksman, 1, 1, 1),
                Entry(blade, 1, 1, 1), Entry(vest, 1, 1, 2), Entry(boots, 1, 1, 2),
            });
            AssetDatabase.CreateAsset(table, LootTablePath);
        }

        static LootEntry Entry(ItemDefinition item, int min, int max, int weight) =>
            new LootEntry { item = item, minQuantity = min, maxQuantity = max, weight = weight };

        static void EnsureStarter(ItemDefinition rifle, ItemDefinition marksman, ItemDefinition vest, ItemDefinition boots,
            ItemDefinition blade, ItemDefinition medkit)
        {
            if (AssetDatabase.LoadAssetAtPath<StarterLoadout>(StarterPath) != null)
                return;
            var starter = StarterLoadout.Create(new[]
            {
                Operative("Darius", Grant(rifle, 1, true), Grant(medkit, 2, false)),
                Operative("Kestrel", Grant(marksman, 1, true), Grant(medkit, 2, false)),
                Operative("Sable", Grant(rifle, 1, true), Grant(medkit, 2, false)),
            }, new[]
            {
                Grant(blade, 1, false), Grant(rifle, 1, false), Grant(vest, 1, false), Grant(boots, 1, false), Grant(medkit, 4, false),
            });
            AssetDatabase.CreateAsset(starter, StarterPath);
        }

        static StarterGrant Grant(ItemDefinition item, int quantity, bool equip) => new StarterGrant { item = item, quantity = quantity, equip = equip };

        static StarterOperative Operative(string name, params StarterGrant[] items) => new StarterOperative
        {
            operative = Load<OperativeDefinition>($"{DefinitionDir}/{name}.asset"),
            items = items,
        };

        const string DariusVisualPrefab = "Assets/Art/Characters/Darius/Prefabs/Darius_Visual.prefab";
        const string EnemyVisualPrefab = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Visual.prefab";
        const string CharacterTestScene = "Assets/_Project/Scenes/Character Test Scene.unity";
        static readonly string[] UnitPrefabs =
        {
            "Assets/Art/Characters/Darius/Prefabs/Darius_Player.prefab",
            "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Friendly.prefab",
            "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Hostile.prefab",
        };

        [MenuItem("Blackglass/Inventory/Migrate Baked Rifles To Managed Weapon Visual")]
        public static void MigrateRiflesMenu() => MigrateRifles();

        public static void MigrateRiflesFromCommandLine() => MigrateRifles();

        /// <summary>
        /// Replaces the rifle baked into each character visual with the managed child: the old child's pose moves to the
        /// WeaponSocket (the per-character alignment), the baked child is removed, and every unit prefab gets a
        /// UnitWeaponVisual whose default is the standard rifle. Idempotent: a prefab with no baked rifle is left alone.
        /// </summary>
        public static void MigrateRifles()
        {
            var rifle = Load<WeaponVisualAsset>(VisualDir + "/RifleVisual.asset");
            MigrateVisual(EnemyVisualPrefab, "Rifle", viaFbxInstance: false);
            MigrateVisual(DariusVisualPrefab, "Darius_Rifle", viaFbxInstance: true);
            foreach (var path in UnitPrefabs)
            {
                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var component = contents.GetComponent<UnitWeaponVisual>();
                    if (component == null)
                        component = contents.AddComponent<UnitWeaponVisual>();
                    var serialized = new SerializedObject(component);
                    serialized.FindProperty("defaultVisual").objectReferenceValue = rifle;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            MigrateCharacterTestScene(rifle);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// The Character Test Scene bench holds a bare Darius_Visual (no gameplay root, so no UnitWeaponVisual). It gets the
        /// shared rifle as a scene-level Rifle.prefab instance in his WeaponSocket, named and posed exactly as UnitWeaponVisual
        /// places the standard rifle, so the bench shows him armed in the Scene view and in Play mode alike. Idempotent: a
        /// socket that already holds something is left alone.
        /// </summary>
        static void MigrateCharacterTestScene(WeaponVisualAsset rifle)
        {
            var scene = SceneManager.GetSceneByPath(CharacterTestScene);
            var opened = !scene.isLoaded;
            if (opened)
                scene = EditorSceneManager.OpenScene(CharacterTestScene, OpenSceneMode.Additive);
            try
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != "Darius_Visual")
                        continue;
                    var socket = FindSocket(root.transform);
                    if (socket == null || socket.childCount > 0)
                        continue;
                    var held = (GameObject)PrefabUtility.InstantiatePrefab(rifle.Prefab, socket);
                    held.name = UnitWeaponVisual.ManagedName;
                    held.transform.localPosition = rifle.LocalPosition;
                    held.transform.localRotation = Quaternion.Euler(rifle.LocalEuler);
                    held.transform.localScale = rifle.LocalScale;
                    EditorSceneManager.MarkSceneDirty(scene);
                }
                if (scene.isDirty)
                    EditorSceneManager.SaveScene(scene);
            }
            finally
            {
                if (opened)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        static Transform FindSocket(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == UnitWeaponVisual.SocketName)
                    return t;
            }
            return null;
        }

        static void MigrateVisual(string path, string bakedName, bool viaFbxInstance)
        {
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform socket = null;
                foreach (var t in contents.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == UnitWeaponVisual.SocketName)
                    {
                        socket = t;
                        break;
                    }
                }
                var baked = socket != null ? socket.Find(bakedName) : null;
                if (baked == null)
                    return;   // already migrated

                if (!viaFbxInstance)
                {
                    // The baked rifle is a Rifle.prefab instance: its pose is exactly what the socket should carry.
                    socket.localPosition = baked.localPosition;
                    socket.localRotation = baked.localRotation;
                    socket.localScale = baked.localScale;
                }
                else
                {
                    // The baked rifle is the FBX instance. Place a Rifle.prefab instance so that its RifleModel lands
                    // exactly where the baked rifle's root was, and give the socket that pose.
                    var probe = (GameObject)PrefabUtility.InstantiatePrefab(Load<GameObject>(RiflePrefab), socket);
                    var model = probe.transform.Find("RifleModel");
                    var rotation = baked.rotation * Quaternion.Inverse(model.localRotation);
                    probe.transform.rotation = rotation;
                    probe.transform.position = baked.position - rotation * Vector3.Scale(model.localPosition, probe.transform.lossyScale);
                    socket.position = probe.transform.position;
                    socket.rotation = probe.transform.rotation;
                    UnityEngine.Object.DestroyImmediate(probe);
                }
                UnityEngine.Object.DestroyImmediate(baked.gameObject);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
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
