using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetAvatarPrefabTests
    {
        const string AvatarPath = "Assets/_Project/Prefabs/Net/NetUnitAvatar.prefab";
        const string CollectionPath = "Assets/DefaultPrefabObjects.asset";

        [Test]
        public void TheAvatarPrefab_HasTheNetworkComponents_AndNothingVisual()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            Assert.That(prefab, Is.Not.Null, "run Blackglass/Net/Build Net Assets");
            Assert.That(prefab.GetComponent<NetworkObject>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<NetworkTransform>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<NetUnitState>(), Is.Not.Null);
            Assert.That(prefab.GetComponentInChildren<Renderer>(true), Is.Null, "the client builds the visuals; the server has none");
            Assert.That(prefab.GetComponentInChildren<Collider>(true), Is.Null);
        }

        [Test]
        public void TheAvatarPrefab_IsInFishNetsSpawnableCollection()
        {
            var collection = AssetDatabase.LoadAssetAtPath<PrefabObjects>(CollectionPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            Assert.That(collection, Is.Not.Null);
            Assert.That(prefab, Is.Not.Null, "run Blackglass/Net/Build Net Assets");
            var found = false;
            for (var i = 0; i < collection.GetObjectCount(); i++)
                found |= collection.GetObject(true, i) == prefab.GetComponent<NetworkObject>();
            Assert.That(found, Is.True, "FishNet adds a prefab with a NetworkObject to the collection when it is imported");
        }
    }
}
