using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using FishNet.Observing;
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
        public void TheAvatarTransform_IsServerAuthoritative()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            var settings = new SerializedObject(prefab.GetComponent<NetworkTransform>());
            var property = settings.FindProperty("_clientAuthoritative");
            Assert.That(property, Is.Not.Null, "FishNet renamed the field; re-check the authority setting");
            Assert.That(property.boolValue, Is.False, "no client may write an avatar's transform, owner or not");
        }

        [Test]
        public void TheAvatar_IsObservedOnlyThroughTheVisibilityCondition()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            var observer = prefab.GetComponent<NetworkObserver>();
            Assert.That(observer, Is.Not.Null, "without a NetworkObserver every client observes every avatar");
            Assert.That(observer.OverrideType, Is.EqualTo(NetworkObserver.ConditionOverrideType.IgnoreManager));
            Assert.That(observer.ObserverConditions, Has.Count.EqualTo(1));
            Assert.That(observer.ObserverConditions[0], Is.InstanceOf<NetVisibilityCondition>());
        }

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
