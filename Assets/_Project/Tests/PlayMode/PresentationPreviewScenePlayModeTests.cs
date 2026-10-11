#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class PresentationPreviewScenePlayModeTests
    {
        public const string ScenePath = "Assets/_Project/Scenes/Presentation Test Scene.unity";
        Scene scene;

        [UnitySetUp]
        public IEnumerator Load()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(ScenePath);
            Assert.That(scene.isLoaded, Is.True);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Unload()
        {
            Time.timeScale = 1f;
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        T Find<T>() where T : Component =>
            scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).First();

        [UnityTest]
        public IEnumerator Scene_HasOneListener_NoMissionAndNoCampaignSystems()
        {
            var listeners = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<AudioListener>(true)).ToArray();
            Assert.That(listeners.Length, Is.EqualTo(1));
            Assert.That(scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MissionDirector>(true)), Is.Empty);
            Assert.That(scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<SquadInventory>(true)), Is.Empty);
            yield return null;
        }

        [UnityTest]
        public IEnumerator FireAtCharacter_UsesTheRealAttackPath_AndShowsMuzzleTracerAndImpact()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            var target = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Health>(true)).First(h => h.name == "Target");
            var before = target.Current;
            panel.FireAtCharacter();
            Assert.That(presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1));
            Assert.That(presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(1));
            Assert.That(presenter.ActiveOf(EffectCategory.Impact), Is.EqualTo(1));
            Assert.That(target.Current, Is.LessThan(before), "the shot is a real attack on a scene dummy");
            yield return null;
        }

        [UnityTest]
        public IEnumerator FireAtConcrete_AndMetal_ShowDifferentImpacts()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.FireAtConcrete();
            var concrete = Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .First(e => e.Category == EffectCategory.Impact).name;
            presenter.Clear();
            panel.FireAtMetal();
            var metal = Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .First(e => e.Category == EffectCategory.Impact).name;
            Assert.That(concrete, Is.EqualTo("ImpactConcrete"));
            Assert.That(metal, Is.EqualTo("ImpactMetal"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator ShotPastTarget_ShowsATracerButNoCharacterImpact()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.FireShotPastTarget();
            Assert.That(presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(1));
            Assert.That(Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Any(e => e.Category == EffectCategory.Impact && e.name == "ImpactCharacter"), Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Pause_FreezesTheEffects_AndRepeatedPauseResumeLeavesNoDuplicates()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.FireAtCharacter();
            var muzzle = Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .First(e => e.Category == EffectCategory.Muzzle);
            var ps = muzzle.Particles.First(p => p.name == "Smoke");
            yield return new WaitForSeconds(0.1f);
            panel.TogglePause();
            var time = ps.time;
            // Counted once paused: the 300 m/s tracer lives about 0.1 s, so whether it is still up after the 0.1 s wait is a coin toss.
            var count = presenter.ActiveEffects;
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(ps.time, Is.EqualTo(time).Within(0.02f));
            for (var i = 0; i < 6; i++)
                panel.TogglePause();
            Assert.That(presenter.ActiveEffects, Is.EqualTo(count), "pause and resume create nothing");
            Time.timeScale = 1f;
        }

        [UnityTest]
        public IEnumerator FogSimulation_HidesTheRightHalf_AndPresentationOffShowsNothing()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.ToggleFogSimulation();
            Assert.That(panel.FogSimulated, Is.True);
            panel.FireRightShooter();
            Assert.That(presenter.ActiveEffects, Is.EqualTo(0), "a shooter in hidden ground shows nothing");
            panel.FireAtCharacter();
            Assert.That(presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(1), "the visible half still works");
            presenter.Clear();
            panel.ToggleFogSimulation();
            panel.TogglePresentation();
            panel.FireAtCharacter();
            Assert.That(presenter.ActiveEffects, Is.EqualTo(0));
            yield return null;
        }

        [UnityTest]
        public IEnumerator CastBlast_IsARealAbility_ShowsTheExplosion_AndDamagesOnlyTheVictims()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            var healths = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Health>(true)).ToList();
            var victim = healths.First(h => h.name == "BlastVictim_1");
            var bystander = healths.First(h => h.name == "Target");
            var victimBefore = victim.Current;
            var bystanderBefore = bystander.Current;
            panel.CastBlast();
            Assert.That(presenter.ActiveOf(EffectCategory.Area), Is.EqualTo(1));
            Assert.That(victim.Current, Is.LessThan(victimBefore));
            Assert.That(bystander.Current, Is.EqualTo(bystanderBefore));
            yield return null;
        }

        [UnityTest]
        public IEnumerator HealDummy_ShowsAPulse_ThroughTheRealHealingPath()
        {
            var panel = Find<PresentationPreviewPanel>();
            panel.HealDummy();
            Assert.That(Find<CombatPresenter>().ActiveOf(EffectCategory.Pulse), Is.EqualTo(1));
            yield return null;
        }

        [UnityTest]
        public IEnumerator Stress_StaysInsideTheBudgets_AndDoesNotThrow()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.Stress();
            foreach (EffectCategory category in System.Enum.GetValues(typeof(EffectCategory)))
                Assert.That(presenter.ActiveOf(category), Is.LessThanOrEqualTo(presenter.Budget.Cap(category)), category.ToString());
            Assert.That(presenter.Budget.LightsActive, Is.LessThanOrEqualTo(presenter.Budget.LightCap));
            yield return null;
        }
    }
}
#endif
