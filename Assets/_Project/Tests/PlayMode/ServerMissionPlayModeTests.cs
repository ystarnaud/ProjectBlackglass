#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>Checkpoint A's server scene in the Editor: startup, composition, frame cap, diagnostic and exit codes.</summary>
    public class ServerMissionPlayModeTests
    {
        const string ScenePath = "Assets/_Project/Scenes/ServerMission.unity";

        static readonly Type[] Allowed =
        {
            typeof(ServerBootstrap), typeof(TacticalPause), typeof(Encounter), typeof(UnitSelection), typeof(ActiveCharacter),
            typeof(CoverRegistry), typeof(CoverDiscovery), typeof(InteractableRegistry), typeof(MissionDirector),
        };

        Scene scene;
        ServerBootstrap bootstrap;
        MissionDirector director;
        int frameRateBefore;
        int vSyncBefore;
        int exitCode;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            frameRateBefore = Application.targetFrameRate;
            vSyncBefore = QualitySettings.vSyncCount;
            exitCode = -1;
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            Assert.That(scene.isLoaded, Is.True);
            var roots = scene.GetRootGameObjects();
            bootstrap = roots.Single(g => g.name == "Server").GetComponent<ServerBootstrap>();
            director = roots.Single(g => g.name == "Mission").GetComponent<MissionDirector>();
            bootstrap.Exit = code => exitCode = code;
            yield return TestWorld.WaitUntil(() => director.State == MissionState.Ready || director.State == MissionState.Failed, 60f);
            Assert.That(director.State, Is.EqualTo(MissionState.Ready), string.Join("\n", director.Report.Failures));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (director != null)
                director.Clear();
            yield return null;
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
            Application.targetFrameRate = frameRateBefore;
            QualitySettings.vSyncCount = vSyncBefore;
            Time.timeScale = 1f;
        }

        [Test]
        public void Startup_GeneratesTheFixtureOnTheFirstAttempt_WithTheFrameCapAndNoOverride()
        {
            Assert.That(bootstrap.Options.Seed, Is.Null, "the Editor's own arguments carry no -seed");
            Assert.That(director.Report.Seed, Is.EqualTo(director.Settings.seed), "the scene's fixture seed");
            Assert.That(director.Report.Attempt, Is.EqualTo(1));
            Assert.That(director.Friendlies, Has.Count.EqualTo(2));
            Assert.That(director.Hostiles, Has.Count.EqualTo(1));
            Assert.That(director.Report.NavMeshBakeMs, Is.GreaterThan(0d));
            Assert.That(Application.targetFrameRate, Is.EqualTo(60));
            Assert.That(QualitySettings.vSyncCount, Is.EqualTo(0));
            Assert.That(bootstrap.HasExited, Is.False);
        }

        [Test]
        public void TheScene_HoldsOnlySimulationSystems_AndNoCameraAudioCanvasOrEventSystem()
        {
            var behaviours = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MonoBehaviour>(true)).ToArray();
            foreach (var behaviour in behaviours)
                Assert.That(Allowed, Has.Member(behaviour.GetType()), behaviour.GetType().Name + " on " + behaviour.name);
            Assert.That(scene.GetRootGameObjects().Select(g => g.name).OrderBy(n => n), Is.EqualTo(new[] { "Mission", "Server", "Systems" }));
            var all = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)).ToArray();
            Assert.That(all.OfType<Camera>(), Is.Empty);
            Assert.That(all.OfType<AudioListener>(), Is.Empty);
            Assert.That(all.OfType<Canvas>(), Is.Empty);
            Assert.That(all.OfType<EventSystem>(), Is.Empty);
        }

        [Test]
        public void AtReady_EveryUnitViewIsDisabled_AndTheSimulationComponentsAreNot()
        {
            Assert.That(bootstrap.UnitViewsDisabled, Is.GreaterThan(0));
            Assert.That(bootstrap.UnitViewsActive, Is.EqualTo(0));
            foreach (var unit in director.Friendlies.Concat(director.Hostiles))
            {
                foreach (var behaviour in unit.GetComponentsInChildren<Behaviour>(true).Where(ServerBootstrap.IsView))
                    Assert.That(behaviour.enabled, Is.False, behaviour.GetType().Name + " on " + unit.name);
                Assert.That(unit.enabled, Is.True, unit.name);
                Assert.That(unit.GetComponent<UnitAttacker>().enabled, Is.True, unit.name);
                Assert.That(unit.GetComponent<UnitMover>().enabled, Is.True, unit.name);
                Assert.That(unit.GetComponent<Collider>().enabled, Is.True, unit.name);
            }
        }

        [UnityTest]
        public IEnumerator Diagnostic_OnTheFixture_ExitsZero()
        {
            bootstrap.BeginDiagnostic(bootstrap.Options.With(diagnostic: true));
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited, 120f);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.Success), bootstrap.ExitReason);
            StringAssert.StartsWith("BLACKGLASS DIAGNOSTIC PASS", bootstrap.ExitReason);
        }

        [UnityTest]
        public IEnumerator Diagnostic_WhilePaused_ExitsOne_ByTheRealTimeDeadline()
        {
            var pause = scene.GetRootGameObjects().Single(g => g.name == "Systems").GetComponent<TacticalPause>();
            pause.Pause();
            var started = Time.realtimeSinceStartupAsDouble;
            bootstrap.BeginDiagnostic(bootstrap.Options.With(diagnostic: true, diagnosticTimeoutSeconds: 5));
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited, 15f);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.DiagnosticFailed), bootstrap.ExitReason);
            StringAssert.Contains("timed out", bootstrap.ExitReason);
            Assert.That(Time.realtimeSinceStartupAsDouble - started, Is.LessThan(10d));
        }

        [UnityTest]
        public IEnumerator ADiagnosticThatCannotPrepare_ExitsOne_InsteadOfRunningOn()
        {
            UnityEngine.Object.DestroyImmediate(director.Friendlies[0].gameObject);
            bootstrap.BeginDiagnostic(bootstrap.Options.With(diagnostic: true));
            yield return TestWorld.WaitUntil(() => bootstrap.HasExited, 10f);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.DiagnosticFailed), bootstrap.ExitReason);
            StringAssert.Contains("exception", bootstrap.ExitReason);
        }

        [Test]
        public void AFailedGeneration_ExitsTwo()
        {
            bootstrap.OnStateChanged(MissionState.Failed);
            Assert.That(exitCode, Is.EqualTo(ServerExitCodes.StartupFailed));
            StringAssert.Contains("generation failed", bootstrap.ExitReason);
        }
    }
}
#endif
