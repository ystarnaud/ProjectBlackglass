using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class TacticalPauseTests
    {
        GameObject host;
        TacticalPause pause;
        List<bool> changes;

        [SetUp]
        public void SetUp()
        {
            Time.timeScale = 1f;
            host = new GameObject("TacticalPauseTest");
            pause = host.AddComponent<TacticalPause>();
            changes = new List<bool>();
            pause.Changed += changes.Add;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(host);
            Time.timeScale = 1f;
        }

        [Test]
        public void StartsUnpaused()
        {
            Assert.That(pause.IsPaused, Is.False);
        }

        [Test]
        public void Pause_StopsTimeAndReportsPaused()
        {
            pause.Pause();
            Assert.That(pause.IsPaused, Is.True);
            Assert.That(Time.timeScale, Is.EqualTo(0f));
            Assert.That(changes, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void Resume_RestoresTheTimeScaleFromBeforePause()
        {
            Time.timeScale = 0.5f;
            pause.Pause();
            pause.Resume();
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(0.5f));
            Assert.That(changes, Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void Toggle_AlternatesBetweenPausedAndRunning()
        {
            pause.Toggle();
            Assert.That(pause.IsPaused, Is.True);
            pause.Toggle();
            Assert.That(pause.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [Test]
        public void PauseTwice_RaisesChangedOnce()
        {
            pause.Pause();
            pause.Pause();
            Assert.That(changes, Is.EqualTo(new[] { true }));
            Assert.That(Time.timeScale, Is.EqualTo(0f));
        }

        [Test]
        public void ResumeWhileRunning_DoesNothing()
        {
            Time.timeScale = 0.75f;
            pause.Resume();
            Assert.That(changes, Is.Empty);
            Assert.That(Time.timeScale, Is.EqualTo(0.75f));
        }
    }
}
