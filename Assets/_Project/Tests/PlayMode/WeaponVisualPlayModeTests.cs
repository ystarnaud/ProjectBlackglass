#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class WeaponVisualPlayModeTests
    {
        const string Friendly = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Friendly.prefab";
        const string Hostile = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Hostile.prefab";
        const string Darius = "Assets/Art/Characters/Darius/Prefabs/Darius_Player.prefab";
        const string Visuals = "Assets/_Project/Data/Items/Visuals/";

        // Measured on the repository before the baked rifles were replaced (Phase 12 Task 9, Step 4): the baked rifle's
        // renderer bounds in the unit root's local space, relative to the right hand, on the spawned EnemyUnit_Friendly and
        // Darius_Player one frame after spawning (the animated idle pose, exactly as AssertGeometry measures; stable to
        // 1e-4 m over 30 frames). A zero size means no measurement.
        static readonly Vector3 EnemyCenter = new Vector3(-0.26562f, 0.13164f, 0.12841f);
        static readonly Vector3 EnemySize = new Vector3(0.93436f, 0.60507f, 0.55055f);
        static readonly Vector3 DariusCenter = new Vector3(-0.25250f, 0.14412f, 0.14086f);
        static readonly Vector3 DariusSize = new Vector3(1.01472f, 0.64050f, 0.67283f);

        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        GameObject Spawn(string path)
        {
            var instance = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            return world.Track(instance);
        }

        static Transform Socket(GameObject unit) =>
            unit.GetComponentsInChildren<Transform>(true).First(t => t.name == UnitWeaponVisual.SocketName);

        static Transform[] Managed(GameObject unit) =>
            Socket(unit).Cast<Transform>().Where(t => t.name == UnitWeaponVisual.ManagedName).ToArray();

        static WeaponVisualAsset Visual(string name) => AssetDatabase.LoadAssetAtPath<WeaponVisualAsset>(Visuals + name + ".asset");

        [UnityTest]
        public IEnumerator Units_WithoutALoadout_KeepTheirDefaultRifle_ExactlyOneManagedChild()
        {
            foreach (var path in new[] { Friendly, Hostile, Darius })
            {
                var unit = Spawn(path);
                yield return null;
                Assert.That(Managed(unit), Has.Length.EqualTo(1), path);
                Assert.That(Socket(unit).Cast<Transform>().Count(), Is.EqualTo(1), $"{path}: no baked rifle remains beside the managed one");
            }
        }

        [UnityTest]
        public IEnumerator Show_ReplacesOnlyTheManagedChild_AndLeavesTheRootAlone()
        {
            var unit = Spawn(Friendly);
            yield return null;
            var rootScale = unit.transform.lossyScale;
            var rootPosition = unit.transform.position;
            var visual = unit.GetComponent<UnitWeaponVisual>();
            var first = visual.Current;

            visual.Show(Visual("MarksmanRifleVisual"));

            Assert.That(Managed(unit), Has.Length.EqualTo(1));
            Assert.That(visual.Current != first, Is.True);
            Assert.That(visual.Current.transform.localScale.z, Is.EqualTo(1.25f).Within(1e-4f));
            Assert.That(unit.transform.lossyScale, Is.EqualTo(rootScale));
            Assert.That(unit.transform.position, Is.EqualTo(rootPosition));
            Assert.That(unit.GetComponent<Health>() != null && unit.GetComponent<CommandableUnit>() != null, Is.True);
        }

        [UnityTest]
        public IEnumerator Show_Null_RemovesTheWeapon_AndShowingTheSameVisualTwiceKeepsOneChild()
        {
            var unit = Spawn(Friendly);
            yield return null;
            var visual = unit.GetComponent<UnitWeaponVisual>();

            visual.Show(null);
            Assert.That(Managed(unit), Is.Empty);
            Assert.That(visual.Current == null, Is.True);

            visual.Show(Visual("RifleVisual"));
            visual.Show(Visual("RifleVisual"));
            Assert.That(Managed(unit), Has.Length.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ManagedChild_HasNoCollider_AndTheGameplayRootKeepsItsOwn()
        {
            var unit = Spawn(Friendly);
            yield return null;

            Assert.That(unit.GetComponent<UnitWeaponVisual>().Current.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(unit.GetComponent<Collider>() != null, Is.True);
        }

        [UnityTest]
        public IEnumerator Capsule_WithTheComponentButNoSocket_DoesNothingAndDoesNotThrow()
        {
            var unit = world.CreateFighter(Vector3.zero);
            var visual = unit.gameObject.AddComponent<UnitWeaponVisual>();

            Assert.DoesNotThrow(() => visual.Show(Visual("RifleVisual")));
            yield return null;
            Assert.That(visual.Current == null, Is.True);
        }

        [UnityTest]
        public IEnumerator TheReplacedRifle_SitsWhereTheBakedOneDid()
        {
            Assert.That(EnemySize.magnitude, Is.GreaterThan(0.1f), "the baked-rifle measurement was not pasted in");
            yield return AssertGeometry(Friendly, EnemyCenter, EnemySize);
            yield return AssertGeometry(Darius, DariusCenter, DariusSize);
        }

        IEnumerator AssertGeometry(string path, Vector3 center, Vector3 size)
        {
            var unit = Spawn(path);
            yield return null;
            var renderer = unit.GetComponent<UnitWeaponVisual>().Current.GetComponentInChildren<Renderer>();
            var hand = Socket(unit).parent;
            var measuredCenter = unit.transform.InverseTransformVector(renderer.bounds.center - hand.position);
            var measuredSize = unit.transform.InverseTransformVector(renderer.bounds.size);
            Assert.That(Vector3.Distance(measuredCenter, center), Is.LessThan(0.02f), $"{path} centre {measuredCenter} vs {center}");
            Assert.That(Vector3.Distance(measuredSize, size), Is.LessThan(0.02f), $"{path} size {measuredSize} vs {size}");
        }
    }
}
#endif
