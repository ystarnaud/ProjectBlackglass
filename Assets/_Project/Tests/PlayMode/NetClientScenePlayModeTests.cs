#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using FishNet.Managing;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class NetClientScenePlayModeTests
    {
        const string ScenePath = "Assets/_Project/Scenes/NetTestClient.unity";

        Scene scene;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            Assert.That(scene.isLoaded, Is.True);
            yield return null;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            NetManagerFactory.DestroyAll();
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        [Test]
        public void TheScene_HoldsTheClientAndNothingOfTheGame()
        {
            var all = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)).ToArray();
            Assert.That(all.OfType<Camera>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<NetClient>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<NetHarness>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<NetClientView>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<NetTestControls>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<NetClientBootstrap>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<MissionDirector>(), Is.Empty, "the client holds no simulation");
            Assert.That(all.OfType<CommandableUnit>(), Is.Empty);
            Assert.That(all.OfType<NetworkManager>(), Is.Empty, "the manager is built at runtime, when a connection is asked for");
        }

        [Test]
        public void WithoutAToken_TheClientDoesNotConnect()
        {
            var client = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<NetClient>(true)).Single();
            Assert.That(client.State, Is.EqualTo(NetClientState.Idle));
            Assert.That(NetworkManager.Instances.Count, Is.EqualTo(0));
        }
    }
}
#endif
