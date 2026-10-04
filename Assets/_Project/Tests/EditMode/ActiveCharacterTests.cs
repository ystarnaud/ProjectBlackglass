using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ActiveCharacterTests
    {
        GameObject systems;
        GameObject unitHost;
        TacticalPause pause;
        ActiveCharacter active;
        CommandableUnit unit;

        [SetUp]
        public void SetUp()
        {
            Time.timeScale = 1f;
            systems = new GameObject("Systems");
            pause = systems.AddComponent<TacticalPause>();
            active = systems.AddComponent<ActiveCharacter>();
            unitHost = new GameObject("Primary");
            unit = unitHost.AddComponent<CommandableUnit>();
            active.Initialize(unit, pause);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(systems);
            Object.DestroyImmediate(unitHost);
            Time.timeScale = 1f;
        }

        [Test]
        public void StartsInFreeMode()
        {
            Assert.That(active.IsTakeoverOn, Is.False);
            Assert.That(active.IsDriving, Is.False);
        }

        [Test]
        public void ExposesItsUnit()
        {
            Assert.That(active.Unit, Is.SameAs(unit));
            Assert.That(active.HasUnit, Is.True);
        }

        [Test]
        public void ToggleTakeover_TurnsItOnAndOff()
        {
            active.ToggleTakeover();
            Assert.That(active.IsTakeoverOn, Is.True);
            active.ToggleTakeover();
            Assert.That(active.IsTakeoverOn, Is.False);
        }

        [Test]
        public void SetTakeover_SetsTheMode()
        {
            active.SetTakeover(true);
            active.SetTakeover(true);
            Assert.That(active.IsTakeoverOn, Is.True);
            active.SetTakeover(false);
            Assert.That(active.IsTakeoverOn, Is.False);
        }

        [Test]
        public void IsDriving_WithTakeoverOn_Unpaused_AndAnActiveUnit()
        {
            active.SetTakeover(true);
            Assert.That(active.IsDriving, Is.True);
        }

        [Test]
        public void IsDriving_FalseWhilePaused_AndBackOnResume()
        {
            active.SetTakeover(true);
            pause.Pause();
            Assert.That(active.IsPaused, Is.True);
            Assert.That(active.IsDriving, Is.False);
            pause.Resume();
            Assert.That(active.IsDriving, Is.True);
        }

        [Test]
        public void ToggleTakeover_WhilePaused_TakesEffectOnResume()
        {
            pause.Pause();
            active.ToggleTakeover();
            Assert.That(active.IsTakeoverOn, Is.True);
            Assert.That(active.IsDriving, Is.False);
            pause.Resume();
            Assert.That(active.IsDriving, Is.True);
        }

        [Test]
        public void HasUnit_FalseWithoutAUnit()
        {
            active.Initialize(null, pause);
            active.SetTakeover(true);
            Assert.That(active.HasUnit, Is.False);
            Assert.That(active.IsDriving, Is.False);
        }

        [Test]
        public void HasUnit_FalseWhenTheUnitIsDisabled()
        {
            active.SetTakeover(true);
            unit.enabled = false;
            Assert.That(active.HasUnit, Is.False);
            Assert.That(active.IsDriving, Is.False);
        }

        [Test]
        public void HasUnit_FalseWhenTheUnitIsInactive()
        {
            active.SetTakeover(true);
            unitHost.SetActive(false);
            Assert.That(active.HasUnit, Is.False);
            Assert.That(active.IsDriving, Is.False);
        }

        [Test]
        public void IsPaused_FalseWithoutAPauseService()
        {
            active.Initialize(unit, null);
            active.SetTakeover(true);
            Assert.That(active.IsPaused, Is.False);
            Assert.That(active.IsDriving, Is.True);
        }
    }
}
