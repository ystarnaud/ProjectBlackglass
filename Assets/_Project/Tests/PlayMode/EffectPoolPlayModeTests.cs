using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class EffectPoolPlayModeTests
    {
        TestWorld world;
        TacticalPause pause;
        Transform container;
        EffectPool pool;
        GameObject prefab;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            pause = world.Track(new GameObject("Pause")).AddComponent<TacticalPause>();
            container = world.Track(new GameObject("Container")).transform;
            pool = new EffectPool(container);
            prefab = world.Track(new GameObject("FxPrefab"));
            prefab.SetActive(false);
            var ps = prefab.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.startLifetime = 5f;
            // The test scene has no camera, so under the default Automatic culling a looping system counts as off-screen and
            // never simulates; the test is about the pause, not culling.
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var emission = ps.emission;
            emission.rateOverTime = 50f;
            var lightObject = new GameObject("Light");
            lightObject.transform.SetParent(prefab.transform, false);
            var light = lightObject.AddComponent<Light>();
            light.intensity = 2f;
            light.enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        PooledEffect Spawn(float lifetime = 5f, bool lights = true)
        {
            var effect = pool.Rent(prefab);
            effect.Begin(EffectCategory.Muzzle, new Vector3(1f, 2f, 3f), Quaternion.identity, 2f, lifetime, lights);
            effect.HoldsLight = lights;
            pool.Track(effect);
            return effect;
        }

        [UnityTest]
        public IEnumerator Begin_PlacesScalesAndStartsTheEffect_AndReturnResetsEverything()
        {
            var effect = Spawn();
            yield return null;
            Assert.That(effect.IsActive, Is.True);
            Assert.That(effect.transform.position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(effect.transform.localScale, Is.EqualTo(Vector3.one * 2f));
            Assert.That(effect.Particles[0].isPlaying, Is.True);
            Assert.That(effect.Lights[0].enabled, Is.True);

            effect.SetGate(Vector3.one);
            effect.SetVisible(false);
            pool.Return(effect);

            Assert.That(effect.IsActive, Is.False);
            Assert.That(effect.gameObject.activeSelf, Is.False);
            Assert.That(effect.Particles[0].particleCount, Is.EqualTo(0));
            Assert.That(effect.Lights[0].enabled, Is.False);
            Assert.That(effect.Lights[0].intensity, Is.EqualTo(2f), "the light's authored intensity is restored");
            Assert.That(effect.transform.localScale, Is.EqualTo(Vector3.one));
            Assert.That(effect.transform.parent, Is.EqualTo(container));
            Assert.That(effect.IsVisible, Is.True, "the gate state is reset too");
            Assert.That(effect.HasGatePoint, Is.False);
            Assert.That(effect.HoldsLight, Is.False);
            Assert.That(pool.ActiveCount, Is.EqualTo(0));
            Assert.That(pool.IdleCount, Is.EqualTo(1));
            Assert.That(pool.Rent(prefab), Is.SameAs(effect), "the instance is reused");
        }

        [UnityTest]
        public IEnumerator WithoutLights_TheLightStaysOff()
        {
            var effect = Spawn(lights: false);
            yield return null;
            Assert.That(effect.Lights[0].enabled, Is.False);
        }

        [UnityTest]
        public IEnumerator Lifetime_IsScaledTime_ItDoesNotRunWhilePaused_AndResumesAfterwards()
        {
            var effect = Spawn(lifetime: 0.4f);
            yield return new WaitForSeconds(0.15f);
            Assert.That(effect.Expired, Is.False);

            pause.Pause();
            yield return new WaitForSecondsRealtime(0.8f);
            Assert.That(effect.Expired, Is.False, "wall-clock time must not age an effect during a pause");

            pause.Resume();
            yield return new WaitForSeconds(0.4f);
            Assert.That(effect.Expired, Is.True);
        }

        [UnityTest]
        public IEnumerator Particles_FreezeDuringPause_AndContinueFromWhereTheyWere_NotRestart()
        {
            var effect = Spawn();
            yield return new WaitForSeconds(0.3f);
            var ps = effect.Particles[0];
            var before = ps.time;
            Assert.That(ps.particleCount, Is.GreaterThan(0), "Precondition: particles were emitted");

            pause.Pause();
            // The frame on which the pause is requested has already fixed its delta time, so the particle system still takes
            // that frame's step (and may emit one more particle). The frozen state is sampled once the pause is in effect.
            yield return null;
            var time = ps.time;
            var count = ps.particleCount;
            Assert.That(time, Is.GreaterThanOrEqualTo(before), "pausing did not restart the system");
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(ps.time, Is.EqualTo(time).Within(0.02f));
            Assert.That(ps.particleCount, Is.EqualTo(count));
            Assert.That(effect.IsActive, Is.True, "a paused effect is not released");

            pause.Resume();
            yield return new WaitForSeconds(0.2f);
            Assert.That(ps.time, Is.GreaterThan(time), "it continued rather than restarted");
            Assert.That(ps.isPlaying, Is.True);
        }

        [UnityTest]
        public IEnumerator LightEnvelope_FadesOnScaledTime_AndFreezesWhilePaused()
        {
            var effect = Spawn(lifetime: 3f);   // the default light envelope is 0.1 s
            yield return null;
            pause.Pause();
            yield return null;
            var intensity = effect.Lights[0].intensity;
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(effect.Lights[0].intensity, Is.EqualTo(intensity));
            pause.Resume();
            yield return new WaitForSeconds(0.3f);
            Assert.That(effect.Lights[0].enabled, Is.False, "the light's short envelope ended");
        }

        [UnityTest]
        public IEnumerator Tracer_MovesOnlyWhileTheGameRuns_AndEnds()
        {
            var tracerPrefab = world.Track(new GameObject("TracerPrefab"));
            tracerPrefab.SetActive(false);
            var line = tracerPrefab.AddComponent<LineRenderer>();
            line.positionCount = 2;
            tracerPrefab.AddComponent<TracerView>();
            var style = TracerStyle.Create(tracerPrefab, 0.04f, 2f, 20f);

            var effect = pool.Rent(tracerPrefab);
            effect.Begin(EffectCategory.Tracer, Vector3.zero, Quaternion.identity, 1f, 10f, false);
            var lifetime = effect.Tracer.Launch(Vector3.zero, new Vector3(0f, 0f, 20f), style);
            pool.Track(effect);
            Assert.That(lifetime, Is.EqualTo((20f + 2f) / 20f + 0.05f).Within(0.01f));

            yield return new WaitForSeconds(0.2f);
            var head = effect.Tracer.HeadPoint.z;
            Assert.That(head, Is.GreaterThan(0.5f));

            pause.Pause();
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(effect.Tracer.HeadPoint.z, Is.EqualTo(head).Within(0.001f));

            pause.Resume();
            yield return new WaitForSeconds(lifetime + 0.2f);
            Assert.That(effect.Tracer.IsRunning, Is.False);
        }

        [UnityTest]
        public IEnumerator Tracer_ZeroLengthOrNaN_IsOmittedWithoutThrowing()
        {
            var tracerPrefab = world.Track(new GameObject("TracerPrefab"));
            tracerPrefab.SetActive(false);
            tracerPrefab.AddComponent<LineRenderer>();
            tracerPrefab.AddComponent<TracerView>();
            var style = TracerStyle.Create(tracerPrefab);
            var effect = pool.Rent(tracerPrefab);
            effect.Tracer.Launch(Vector3.one, Vector3.one, style);
            Assert.That(effect.Tracer.IsRunning, Is.False);
            effect.Tracer.Launch(Vector3.zero, new Vector3(float.NaN, 0f, 1f), style);
            Assert.That(effect.Tracer.IsRunning, Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ADestroyedActiveEffect_IsPrunedByReturn()
        {
            var effect = Spawn();
            yield return null;
            Assert.That(pool.ActiveCount, Is.EqualTo(1));
            Object.DestroyImmediate(effect.gameObject);
            Assert.DoesNotThrow(() => pool.Return(effect));
            Assert.That(pool.ActiveCount, Is.EqualTo(0));
            Assert.That(pool.IdleCount, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator ReturningTheSameEffectTwice_PoolsItOnlyOnce()
        {
            var effect = Spawn();
            yield return null;
            pool.Return(effect);
            pool.Return(effect);
            Assert.That(pool.IdleCount, Is.EqualTo(1));
            var first = pool.Rent(prefab);
            var second = pool.Rent(prefab);
            Assert.That(first, Is.SameAs(effect));
            Assert.That(second, Is.Not.SameAs(effect), "the instance was handed out once");
            pool.Return(first);
            Assert.That(pool.IdleCount, Is.EqualTo(1), "a rented instance can be returned again after being rented");
        }

        [UnityTest]
        public IEnumerator ADestroyedIdleEffect_IsNeverHandedOut()
        {
            var first = Spawn();
            pool.Return(first);
            Object.DestroyImmediate(first.gameObject);
            yield return null;
            var second = pool.Rent(prefab);
            Assert.That(second, Is.Not.Null);
            Assert.That(second.gameObject, Is.Not.Null);
        }
    }
}
