#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using FishNet.Managing;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The generated play-client scene holds the play client, wired to real assets, and nothing of the harness or the game.
    /// An InputTestFixture, like every scene test whose scene uses the project's shared input asset, and the scene loads inside
    /// the test and its objects go in the fixture's TearDown: [UnitySetUp] runs before the fixture's Setup and [UnityTearDown]
    /// after its TearDown, so a scene loaded there gives the shared asset an action state outside the fixture, which later
    /// input tests then inherit with no devices bound (they stopped seeing input).
    /// </summary>
    public class NetPlayClientScenePlayTests : InputTestFixture
    {
        const string ScenePath = "Assets/_Project/Scenes/NetPlayClient.unity";
        Scene scene;
        InputActionAsset actions;

        public override void Setup()
        {
            base.Setup();
            actions = TestControls.Load();
        }

        public override void TearDown()
        {
            if (scene.isLoaded)
                foreach (var root in scene.GetRootGameObjects())
                    Object.DestroyImmediate(root);
            TestControls.Reset(actions);
            base.TearDown();
        }

        IEnumerator LoadScene()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            Assert.That(scene.isLoaded, Is.True);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator UnloadScene()
        {
            NetManagerFactory.DestroyAll();
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        [UnityTest]
        public IEnumerator TheScene_HoldsThePlayClient_AndNoHarnessNorGame()
        {
            yield return LoadScene();
            var all = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)).ToArray();
            foreach (var type in new[] { typeof(NetClient), typeof(NetMapView), typeof(NetPlayAvatars), typeof(NetPlayControls), typeof(NetPlayHud), typeof(NetPlayBootstrap), typeof(TacticalCameraController) })
                Assert.That(all.Count(c => c != null && c.GetType() == type), Is.EqualTo(1), type.Name);
            foreach (var type in new[] { typeof(NetHarness), typeof(NetTestControls), typeof(NetClientView), typeof(MissionDirector), typeof(NetServer), typeof(CommandableUnit) })
                Assert.That(all.Any(c => c != null && c.GetType() == type), Is.False, type.Name + " must not be in the play client");
        }

        [UnityTest]
        public IEnumerator TheSceneBuilder_WiredEveryAsset()
        {
            yield return LoadScene();
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

        // The controls switch the project's shared actions on, so leaving the scene must switch them off again, as every offline
        // input component does; otherwise they stay live for whatever runs next (later input tests stopped seeing input).
        [UnityTest]
        public IEnumerator Leaving_the_scene_switches_off_every_action_the_controls_use()
        {
            yield return LoadScene();
            var controls = new UnityEditor.SerializedObject(Object.FindObjectsByType<NetPlayControls>(FindObjectsSortMode.None).Single());
            var actions = new[] { "commandAction", "pointerAction", "queueAction", "stopAction", "cancelAction", "panAction", "cycleAction", "followAction" }
                .Select(field => ((InputActionReference)controls.FindProperty(field).objectReferenceValue).action).ToArray();
            Assert.That(actions.All(a => a.enabled), Is.True, "the scene switches its actions on");
            yield return SceneManager.UnloadSceneAsync(scene);
            foreach (var action in actions)
                Assert.That(action.enabled, Is.False, action.actionMap.name + "/" + action.name);
        }
    }
}
#endif
