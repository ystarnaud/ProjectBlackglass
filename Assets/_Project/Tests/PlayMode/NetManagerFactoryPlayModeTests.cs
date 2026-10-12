#if UNITY_EDITOR
using FishNet.Managing;
using FishNet.Managing.Object;
using NUnit.Framework;
using UnityEditor;

namespace Blackglass.Tests
{
    public class NetManagerFactoryPlayModeTests
    {
        [TearDown]
        public void TearDown() => NetManagerFactory.DestroyAll();

        static PrefabObjects Prefabs() => AssetDatabase.LoadAssetAtPath<PrefabObjects>("Assets/DefaultPrefabObjects.asset");

        [Test]
        public void Acquire_BuildsAManager_ThatDoesNotStartItself()
        {
            var manager = NetManagerFactory.Acquire(Prefabs(), 60, out var transport);

            Assert.That(manager, Is.Not.Null);
            Assert.That(transport, Is.Not.Null);
            Assert.That(manager.Initialized, Is.True, "NetworkManager.Awake has run");
            Assert.That(manager.ServerManager.GetStartOnHeadless(), Is.False, "only -net may start the server");
            Assert.That(manager.IsServerStarted, Is.False);
            Assert.That(manager.SpawnablePrefabs, Is.Not.Null);
        }

        [Test]
        public void Acquire_ReturnsTheExistingManager()
        {
            var first = NetManagerFactory.Acquire(Prefabs(), 0, out _);
            var second = NetManagerFactory.Acquire(Prefabs(), 0, out var transport);
            Assert.That(second, Is.SameAs(first));
            Assert.That(transport, Is.Not.Null);
        }

        [Test]
        public void DestroyAll_RemovesTheManager()
        {
            NetManagerFactory.Acquire(Prefabs(), 0, out _);
            NetManagerFactory.DestroyAll();
            Assert.That(NetworkManager.Instances.Count, Is.EqualTo(0));
        }
    }
}
#endif
