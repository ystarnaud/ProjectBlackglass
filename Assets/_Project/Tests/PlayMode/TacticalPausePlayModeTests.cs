using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class TacticalPausePlayModeTests
    {
        [TearDown]
        public void TearDown() => Time.timeScale = 1f;

        [UnityTest]
        public IEnumerator DisablingWhilePaused_RestoresTime()
        {
            var host = new GameObject("TacticalPause");
            var pause = host.AddComponent<TacticalPause>();
            pause.Pause();
            yield return null;

            host.SetActive(false);

            Assert.That(Time.timeScale, Is.EqualTo(1f));
            Assert.That(pause.IsPaused, Is.False);
            Object.Destroy(host);
        }

        [UnityTest]
        public IEnumerator WhilePaused_ScaledTimeStopsButRealTimeContinues()
        {
            var host = new GameObject("TacticalPause");
            var pause = host.AddComponent<TacticalPause>();
            pause.Pause();
            var scaledStart = Time.time;

            yield return new WaitForSecondsRealtime(0.2f);

            Assert.That(Time.time, Is.EqualTo(scaledStart));
            pause.Resume();
            Object.Destroy(host);
        }
    }
}
