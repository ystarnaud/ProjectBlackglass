using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blackglass.Tests
{
    public class ProceduralMissionPresentationWiringTests
    {
        const string ScenePath = "Assets/_Project/Scenes/ProceduralMission.unity";
        Scene scene;
        bool wasOpen;

        // When the scene is already open in the Editor, OpenScene returns that live scene: closing it would discard the
        // owner's unsaved edits, so only a scene this fixture opened is closed.
        [SetUp]
        public void Open()
        {
            wasOpen = SceneManager.GetSceneByPath(ScenePath).isLoaded;
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }

        [TearDown]
        public void Close()
        {
            if (!wasOpen)
                EditorSceneManager.CloseScene(scene, true);
        }

        T One<T>() where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).Single();

        [Test]
        public void ThePresenter_IsWiredToTheScenesSystems()
        {
            var presenter = new SerializedObject(One<CombatPresenter>());
            Assert.That(presenter.FindProperty("pause").objectReferenceValue, Is.EqualTo(One<TacticalPause>()));
            Assert.That(presenter.FindProperty("intelligence").objectReferenceValue, Is.EqualTo(One<IntelligenceService>()));
            Assert.That(presenter.FindProperty("cameraImpulse").objectReferenceValue, Is.EqualTo(One<CameraImpulse>()));
            Assert.That(presenter.FindProperty("fallbackProfile").objectReferenceValue, Is.Not.Null);
            Assert.That(presenter.FindProperty("supportProfile").objectReferenceValue, Is.Not.Null);
        }

        [Test]
        public void TheDirector_AndTheCameraController_KnowThePresentation()
        {
            var director = new SerializedObject(One<MissionDirector>());
            Assert.That(director.FindProperty("systems.presentation").objectReferenceValue, Is.EqualTo(One<CombatPresenter>()));
            var controller = new SerializedObject(One<TacticalCameraController>());
            Assert.That(controller.FindProperty("impulse").objectReferenceValue, Is.EqualTo(One<CameraImpulse>()));
        }

        [Test]
        public void TheSceneStillHasExactlyOneAudioListener()
        {
            Assert.That(scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<AudioListener>(true)).Count(), Is.EqualTo(1));
        }
    }
}
