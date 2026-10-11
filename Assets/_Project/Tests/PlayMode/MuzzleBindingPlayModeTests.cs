#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class MuzzleBindingPlayModeTests
    {
        const string FriendlyPath = "Assets/Art/Characters/EnemyUnit/Prefabs/EnemyUnit_Friendly.prefab";
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        GameObject WeaponPrefab(bool withMuzzle)
        {
            var prefab = world.Track(new GameObject("TestWeapon"));
            prefab.SetActive(false);
            var body = new GameObject("Body");
            body.transform.SetParent(prefab.transform, false);
            if (withMuzzle)
            {
                var muzzle = new GameObject("Muzzle");
                muzzle.transform.SetParent(prefab.transform, false);
                muzzle.transform.localPosition = new Vector3(0f, 0f, 0.5f);
            }
            return prefab;
        }

        UnitWeaponVisual UnitWithSocket()
        {
            var unit = world.Track(new GameObject("Unit"));
            var socket = new GameObject(UnitWeaponVisual.SocketName);
            socket.transform.SetParent(unit.transform, false);
            socket.transform.localScale = Vector3.one * 0.01f;   // an imported hierarchy with a scale correction
            return unit.AddComponent<UnitWeaponVisual>();
        }

        [UnityTest]
        public IEnumerator Muzzle_IsFoundWhenTheWeaponIsPlaced_ClearedWhenReplacedOrRemoved_AndFollowsTheWorldPosition()
        {
            var visual = UnitWithSocket();
            yield return null;
            Assert.That(visual.Muzzle, Is.Null, "nothing shown yet");

            var withMuzzle = WeaponVisualAsset.Create(WeaponPrefab(true), Vector3.zero, Vector3.zero, Vector3.one);
            visual.Show(withMuzzle);
            Assert.That(visual.Muzzle, Is.Not.Null);
            Assert.That(visual.Shown, Is.EqualTo(withMuzzle));
            Assert.That(visual.Muzzle.position.z, Is.EqualTo(0.005f).Within(1e-5f), "world position follows the 0.01 scale of the socket");

            var withoutMuzzle = WeaponVisualAsset.Create(WeaponPrefab(false), Vector3.zero, Vector3.zero, Vector3.one);
            visual.Show(withoutMuzzle);
            Assert.That(visual.Muzzle, Is.Null, "a weapon without a Muzzle child has no muzzle");
            LogAssert.NoUnexpectedReceived();

            visual.Show(withMuzzle);
            Assert.That(visual.Muzzle, Is.Not.Null);
            visual.Show(null);
            Assert.That(visual.Muzzle, Is.Null);
            Assert.That(visual.Shown, Is.Null);
        }

        [UnityTest]
        public IEnumerator TheRealRifleOnEnemyUnit_HasAMuzzle()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FriendlyPath);
            var unit = world.Track(Object.Instantiate(prefab));
            yield return null;
            var visual = unit.GetComponent<UnitWeaponVisual>();
            Assert.That(visual.Muzzle, Is.Not.Null);
            Assert.That(visual.Muzzle.name, Is.EqualTo("Muzzle"));
            Assert.That(visual.Shown, Is.Not.Null);
        }
    }
}
#endif
