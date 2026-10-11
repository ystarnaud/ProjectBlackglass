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
    /// <summary>The tactical pause against the real generated effect prefabs and sounds of the Presentation Test Scene.</summary>
    public class PresentationPauseRealAssetsPlayModeTests
    {
        Scene scene;

        [UnitySetUp]
        public IEnumerator Load()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(PresentationPreviewScenePlayModeTests.ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            scene = SceneManager.GetSceneByPath(PresentationPreviewScenePlayModeTests.ScenePath);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Unload()
        {
            Time.timeScale = 1f;
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        T Find<T>() where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).First();

        [UnityTest]
        public IEnumerator EverythingFreezesAtPause_AndContinuesAfterwards_NothingRestartsOrIsReleasedEarly()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.FireAtCharacter();
            panel.CastBlast();
            panel.HealDummy();
            yield return new WaitForSeconds(0.08f);
            var effects = Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var count = effects.Length;
            Assert.That(count, Is.GreaterThan(3), "Precondition: muzzle, tracer, impact, explosion and pulse are playing");
            var smoke = effects.Where(e => e.Category == EffectCategory.Area).SelectMany(e => e.Particles).First(p => p.name == "Smoke");
            var smokeBeforePause = smoke.time;

            panel.TogglePause();
            // The frame on which the pause is requested has already fixed its delta time, so the particle systems take one more
            // step after the request. Everything is sampled once the pause has taken effect, then compared after a real wait.
            yield return null;
            Assert.That(Time.timeScale, Is.EqualTo(0f));
            var smokeTime = smoke.time;
            var smokeCount = smoke.particleCount;
            Assert.That(smokeTime, Is.GreaterThanOrEqualTo(smokeBeforePause), "pausing did not restart the smoke");
            Assert.That(smokeCount, Is.GreaterThan(0), "Precondition: the explosion's smoke is up");
            var lightIntensity = effects.SelectMany(e => e.Lights).Where(l => l.enabled).Select(l => l.intensity).ToArray();
            var voices = presenter.Audio.ActiveVoices;
            Assert.That(voices, Is.GreaterThan(0), "Precondition: sounds are held by the pause");
            var impulse = Find<CameraImpulse>().LocalOffset;
            yield return new WaitForSecondsRealtime(0.6f);

            Assert.That(Object.FindObjectsByType<PooledEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length, Is.EqualTo(count),
                "no effect was released or created during the pause");
            Assert.That(smoke.time, Is.EqualTo(smokeTime).Within(0.03f));
            Assert.That(smoke.particleCount, Is.EqualTo(smokeCount));
            Assert.That(effects.SelectMany(e => e.Lights).Where(l => l.enabled).Select(l => l.intensity).ToArray(), Is.EqualTo(lightIntensity).Within(0.01f));
            Assert.That(presenter.Audio.ActiveVoices, Is.EqualTo(voices));
            Assert.That(Find<CameraImpulse>().LocalOffset, Is.EqualTo(impulse));

            for (var i = 0; i < 20; i++)
                panel.TogglePause();   // an even number: still paused, repeatedly toggled
            Assert.That(Time.timeScale, Is.EqualTo(0f));
            Assert.That(presenter.ActiveEffects, Is.EqualTo(count), "pause spam creates nothing");

            panel.TogglePause();
            yield return new WaitForSeconds(0.25f);
            Assert.That(smoke.time, Is.GreaterThan(smokeTime), "the explosion's smoke continued, it did not restart");
            Assert.That(smoke.particleCount, Is.GreaterThanOrEqualTo(1));
        }

        [UnityTest]
        public IEnumerator AfterTheLifetimes_EverythingIsReleased_AndBudgetsAreBackToZero()
        {
            var panel = Find<PresentationPreviewPanel>();
            var presenter = Find<CombatPresenter>();
            panel.FireAtCharacter();
            panel.CastBlast();
            panel.HealDummy();
            Assert.That(presenter.ActiveEffects, Is.GreaterThan(3), "Precondition: the effects are playing");
            yield return new WaitForSeconds(5f);
            Assert.That(presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(presenter.Budget.LightsActive, Is.EqualTo(0));
            foreach (EffectCategory category in System.Enum.GetValues(typeof(EffectCategory)))
                Assert.That(presenter.Budget.Active(category), Is.EqualTo(0), category.ToString());
        }
    }
}
#endif
