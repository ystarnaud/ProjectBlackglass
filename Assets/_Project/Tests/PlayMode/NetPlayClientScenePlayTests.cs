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
    /// <summary>The generated play-client scene holds the play client, wired to real assets, and nothing of the harness or the game.</summary>
    public class NetPlayClientScenePlayTests
    {
        const string ScenePath = "Assets/_Project/Scenes/NetPlayClient.unity";
        Scene scene;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            Assert.That(scene.isLoaded, Is.True);
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
        public void TheScene_HoldsThePlayClient_AndNoHarnessNorGame()
        {
            var all = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)).ToArray();
            foreach (var type in new[] { typeof(NetClient), typeof(NetMapView), typeof(NetPlayAvatars), typeof(NetPlayControls), typeof(NetPlayHud), typeof(NetPlayBootstrap), typeof(TacticalCameraController) })
                Assert.That(all.Count(c => c != null && c.GetType() == type), Is.EqualTo(1), type.Name);
            foreach (var type in new[] { typeof(NetHarness), typeof(NetTestControls), typeof(NetClientView), typeof(MissionDirector), typeof(NetServer), typeof(CommandableUnit) })
                Assert.That(all.Any(c => c != null && c.GetType() == type), Is.False, type.Name + " must not be in the play client");
        }

        [Test]
        public void TheSceneBuilder_WiredEveryAsset()
        {
            var map = Object.FindObjectsByType<NetMapView>(FindObjectsSortMode.None).Single();
            var serialized = new UnityEditor.SerializedObject(map);
            Assert.That(serialized.FindProperty("theme").objectReferenceValue, Is.Not.Null);
            var avatars = new UnityEditor.SerializedObject(Object.FindObjectsByType<NetPlayAvatars>(FindObjectsSortMode.None).Single());
            foreach (var field in new[] { "visualPrefab", "weapon", "tracerPrefab", "ringMaterial", "markerMaterial" })
                Assert.That(avatars.FindProperty(field).objectReferenceValue, Is.Not.Null, field);
            var controls = new UnityEditor.SerializedObject(Object.FindObjectsByType<NetPlayControls>(FindObjectsSortMode.None).Single());
            foreach (var field in new[] { "commandAction", "pointerAction", "queueAction", "stopAction", "cancelAction", "panAction", "cycleAction", "followAction" })
                Assert.That(controls.FindProperty(field).objectReferenceValue, Is.Not.Null, field);
        }
    }
}
#endif
