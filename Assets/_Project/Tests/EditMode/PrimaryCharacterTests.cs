using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PrimaryCharacterTests
    {
        GameObject systems;
        GameObject unitHost;
        TacticalPause pause;
        PrimaryCharacter primary;
        CommandableUnit unit;

        [SetUp]
        public void SetUp()
        {
            Time.timeScale = 1f;
            systems = new GameObject("Systems");
            pause = systems.AddComponent<TacticalPause>();
            primary = systems.AddComponent<PrimaryCharacter>();
            unitHost = new GameObject("Primary");
            unit = unitHost.AddComponent<CommandableUnit>();
            primary.Initialize(unit, pause);
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
            Assert.That(primary.IsTakeoverOn, Is.False);
            Assert.That(primary.IsDriving, Is.False);
        }

        [Test]
        public void ExposesItsUnit()
        {
            Assert.That(primary.Unit, Is.SameAs(unit));
            Assert.That(primary.HasUnit, Is.True);
        }

        [Test]
        public void ToggleTakeover_TurnsItOnAndOff()
        {
            primary.ToggleTakeover();
            Assert.That(primary.IsTakeoverOn, Is.True);
            primary.ToggleTakeover();
            Assert.That(primary.IsTakeoverOn, Is.False);
        }

        [Test]
        public void SetTakeover_SetsTheMode()
        {
            primary.SetTakeover(true);
            primary.SetTakeover(true);
            Assert.That(primary.IsTakeoverOn, Is.True);
            primary.SetTakeover(false);
            Assert.That(primary.IsTakeoverOn, Is.False);
        }

        [Test]
        public void IsDriving_WithTakeoverOn_Unpaused_AndAnActiveUnit()
        {
            primary.SetTakeover(true);
            Assert.That(primary.IsDriving, Is.True);
        }

        [Test]
        public void IsDriving_FalseWhilePaused_AndBackOnResume()
        {
            primary.SetTakeover(true);
            pause.Pause();
            Assert.That(primary.IsPaused, Is.True);
            Assert.That(primary.IsDriving, Is.False);
            pause.Resume();
            Assert.That(primary.IsDriving, Is.True);
        }

        [Test]
        public void ToggleTakeover_WhilePaused_TakesEffectOnResume()
        {
            pause.Pause();
            primary.ToggleTakeover();
            Assert.That(primary.IsTakeoverOn, Is.True);
            Assert.That(primary.IsDriving, Is.False);
            pause.Resume();
            Assert.That(primary.IsDriving, Is.True);
        }

        [Test]
        public void HasUnit_FalseWithoutAUnit()
        {
            primary.Initialize(null, pause);
            primary.SetTakeover(true);
            Assert.That(primary.HasUnit, Is.False);
            Assert.That(primary.IsDriving, Is.False);
        }

        [Test]
        public void HasUnit_FalseWhenTheUnitIsDisabled()
        {
            primary.SetTakeover(true);
            unit.enabled = false;
            Assert.That(primary.HasUnit, Is.False);
            Assert.That(primary.IsDriving, Is.False);
        }

        [Test]
        public void HasUnit_FalseWhenTheUnitIsInactive()
        {
            primary.SetTakeover(true);
            unitHost.SetActive(false);
            Assert.That(primary.HasUnit, Is.False);
            Assert.That(primary.IsDriving, Is.False);
        }

        [Test]
        public void IsPaused_FalseWithoutAPauseService()
        {
            primary.Initialize(unit, null);
            primary.SetTakeover(true);
            Assert.That(primary.IsPaused, Is.False);
            Assert.That(primary.IsDriving, Is.True);
        }
    }
}
