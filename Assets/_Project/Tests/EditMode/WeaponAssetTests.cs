using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>The shared rifle asset: it is self-contained, has a muzzle point at the barrel tip and can sit in a unit's hand socket.</summary>
    public class WeaponAssetTests
    {
        const string RiflePrefab = "Assets/Art/Weapons/Rifle/Prefabs/Rifle.prefab";
        const string EnemyPrefab = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Visual.prefab";
        const string EnemyUnitPrefab = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Friendly.prefab";
        // Phase 12: the rifle is the unit's managed weapon model (UnitWeaponVisual on the gameplay root), so it is the unit
        // prefab, not the visual, that carries it.
        const string DariusPrefab = "Assets/Art/Characters/Darius/Prefabs/Darius_Player.prefab";

        GameObject instance;

        [TearDown]
        public void TearDown()
        {
            if (instance != null) Object.DestroyImmediate(instance);
        }

        static GameObject Load(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(asset, Is.Not.Null, path);
            return asset;
        }

        static Bounds RendererBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<MeshRenderer>();
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            return bounds;
        }

        [Test]
        public void Rifle_prefab_is_a_rigid_mesh_with_its_own_material()
        {
            instance = (GameObject)PrefabUtility.InstantiatePrefab(Load(RiflePrefab));
            Assert.That(instance.GetComponentInChildren<MeshRenderer>(), Is.Not.Null);
            Assert.That(instance.GetComponentInChildren<SkinnedMeshRenderer>(), Is.Null);
            var material = instance.GetComponentInChildren<MeshRenderer>().sharedMaterial;
            Assert.That(AssetDatabase.GetAssetPath(material), Is.EqualTo("Assets/Art/Weapons/Rifle/Materials/Rifle.mat"));
        }

        [Test]
        public void Rifle_does_not_depend_on_any_character_folder()
        {
            var dependencies = AssetDatabase.GetDependencies(RiflePrefab, true);
            Assert.That(dependencies.Where(d => d.Contains("/Characters/")), Is.Empty);
            Assert.That(dependencies, Does.Contain("Assets/Art/Weapons/Rifle/Textures/Rifle_BaseMap.png"));
            Assert.That(dependencies, Does.Contain("Assets/Art/Weapons/Rifle/Textures/Rifle_Normal.png"));
        }

        [Test]
        public void Rifle_muzzle_is_at_the_barrel_tip_and_points_out_along_the_barrel()
        {
            instance = (GameObject)PrefabUtility.InstantiatePrefab(Load(RiflePrefab));
            var muzzle = instance.transform.Find("Muzzle");
            Assert.That(muzzle, Is.Not.Null, "the rifle prefab needs a child named Muzzle");

            var bounds = RendererBounds(instance);
            var longAxis = bounds.size.x >= bounds.size.y && bounds.size.x >= bounds.size.z ? 0 : bounds.size.y >= bounds.size.z ? 1 : 2;
            var axis = Vector3.zero; axis[longAxis] = 1f;

            Assert.That(Mathf.Abs(Vector3.Dot(muzzle.forward, axis)), Is.GreaterThan(0.99f), "muzzle forward runs along the barrel");
            var outward = Vector3.Dot(muzzle.position - bounds.center, muzzle.forward);
            Assert.That(outward, Is.GreaterThan(bounds.size[longAxis] * 0.4f), "muzzle sits at one end and points away from the rifle");
            Assert.That(Mathf.Abs(muzzle.position[longAxis] - (muzzle.forward[longAxis] > 0 ? bounds.max[longAxis] : bounds.min[longAxis])),
                Is.LessThan(bounds.size[longAxis] * 0.02f), "muzzle is at the very tip");
        }

        [Test]
        public void Rifle_is_about_a_metre_long()
        {
            instance = (GameObject)PrefabUtility.InstantiatePrefab(Load(RiflePrefab));
            var bounds = RendererBounds(instance);
            Assert.That(Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z), Is.InRange(0.8f, 1.0f));
        }

        static void RequireEnemyUnit()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(EnemyPrefab) == null)
                Assert.Ignore("The EnemyUnit assets are not in this checkout yet.");
        }

        [Test]
        public void EnemyUnit_holds_the_rifle_in_its_right_hand_at_world_size()
        {
            RequireEnemyUnit();
            instance = (GameObject)PrefabUtility.InstantiatePrefab(Load(EnemyUnitPrefab));
            var hand = instance.GetComponentInChildren<Animator>().GetBoneTransform(HumanBodyBones.RightHand);
            Assert.That(hand, Is.Not.Null);
            Assert.That(hand.Find(UnitWeaponVisual.SocketName), Is.Not.Null, "WeaponSocket under the right hand");
            // Phase 12: the unit's UnitWeaponVisual places its default rifle in the socket (in Awake; EditMode runs none, so show it here).
            var visual = instance.GetComponent<UnitWeaponVisual>();
            Assert.That(visual, Is.Not.Null);
            var standard = new SerializedObject(visual).FindProperty("defaultVisual").objectReferenceValue as WeaponVisualAsset;
            Assert.That(standard != null && standard.Prefab == Load(RiflePrefab), Is.True, "the default shows Rifle.prefab itself, not a copy");
            visual.Show(standard);
            var rifle = hand.Find(UnitWeaponVisual.SocketName + "/" + UnitWeaponVisual.ManagedName);
            Assert.That(rifle, Is.Not.Null, "the managed rifle under WeaponSocket");
            Assert.That(rifle.Find("Muzzle"), Is.Not.Null);
            Assert.That(rifle.lossyScale.x, Is.EqualTo(1f).Within(0.01f), "the character's x1.85 is cancelled so the rifle keeps its own size");
            var bounds = RendererBounds(rifle.gameObject);
            Assert.That(Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z), Is.InRange(0.8f, 1.0f));
        }

        [Test]
        public void EnemyUnit_does_not_depend_on_Darius_or_the_rifle_textures()
        {
            RequireEnemyUnit();
            var dependencies = AssetDatabase.GetDependencies(EnemyPrefab, true).Where(d => !d.StartsWith("Assets/Art/Weapons/Rifle/")).ToArray();
            Assert.That(dependencies.Where(d => d.Contains("/Characters/Darius/")), Is.Empty);
            // The rifle's own files are allowed (it is in the hand), but the enemy's body must use its own textures.
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Characters/EnemyUnit/Materials/modddif_material_0.mat");
            Assert.That(material, Is.Not.Null);
            foreach (var path in AssetDatabase.GetDependencies("Assets/Art/Characters/EnemyUnit/Materials/modddif_material_0.mat", true))
                Assert.That(path, Does.Not.Contain("/Weapons/").And.Not.Contain("/Darius/"));
        }

        [Test]
        public void Darius_still_carries_the_rifle_through_the_moved_files()
        {
            var dependencies = AssetDatabase.GetDependencies(DariusPrefab, true);
            Assert.That(dependencies, Does.Contain("Assets/Art/Weapons/Rifle/Models/Rifle.fbx"));
            Assert.That(dependencies, Does.Contain("Assets/Art/Weapons/Rifle/Materials/Rifle.mat"));
        }
    }
}
