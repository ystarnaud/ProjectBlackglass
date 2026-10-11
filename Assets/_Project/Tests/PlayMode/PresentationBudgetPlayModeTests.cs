#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>The presentation budgets under stress, and a broken tracer prefab: cosmetic degradation only, never gameplay.</summary>
    public class PresentationBudgetPlayModeTests
    {
        PresentationRig rig;

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose();
            rig = null;
        }

        [UnityTest]
        public IEnumerator HundredsOfShots_StayInsideEveryCap_AndNeverChangeTheDamageDealt()
        {
            rig = new PresentationRig();
            var target = rig.CreateTarget(new Vector3(0f, 0f, 12f));
            target.Initialize(1000);   // survives all 250 shots, so every one of them is fired
            var shooters = new CommandableUnit[10];
            for (var i = 0; i < shooters.Length; i++)
                shooters[i] = rig.CreateShooter(new Vector3(-9f + 2f * i, 0f, -6f), damage: 1, cooldown: 0f);
            yield return null;

            var shots = 0;
            for (var round = 0; round < 25; round++)
                foreach (var shooter in shooters)
                    if (shooter.GetComponent<UnitAttacker>().TryAttack(target))
                        shots++;

            Assert.That(shots, Is.EqualTo(250));
            Assert.That(target.Current, Is.EqualTo(target.Max - 250), "every shot dealt exactly its damage whatever the budgets did");
            foreach (EffectCategory category in System.Enum.GetValues(typeof(EffectCategory)))
                Assert.That(rig.Presenter.ActiveOf(category), Is.LessThanOrEqualTo(rig.Presenter.Budget.Cap(category)), category.ToString());
            Assert.That(rig.Presenter.Budget.LightsActive, Is.LessThanOrEqualTo(rig.Presenter.Budget.LightCap));
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.LessThanOrEqualTo(rig.Presenter.Audio.VoiceCount));
            Assert.That(rig.Presenter.Budget.Refused, Is.GreaterThan(0), "the caps were actually hit");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.EqualTo(rig.Presenter.Budget.Cap(EffectCategory.Muzzle)),
                "the muzzle cap was filled, not undershot");
        }

        [UnityTest]
        public IEnumerator QualityLow_HasSmallerCaps_AndSwitchingClearsCleanly()
        {
            rig = new PresentationRig();
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f), cooldown: 0f);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 12f));
            target.Initialize(1000);
            yield return null;
            rig.Presenter.SetQuality(PresentationQuality.Low);
            for (var i = 0; i < 30; i++)
                Assert.That(shooter.GetComponent<UnitAttacker>().TryAttack(target), Is.True);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.LessThanOrEqualTo(rig.Presenter.Budget.Cap(EffectCategory.Muzzle)));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.GreaterThan(0), "Precondition: low quality still shows muzzle flashes");
            Assert.That(rig.Presenter.Budget.Cap(EffectCategory.Muzzle), Is.LessThan(new EffectBudget(PresentationQuality.High).Cap(EffectCategory.Muzzle)));
            rig.Presenter.SetQuality(PresentationQuality.High);
            Assert.That(rig.Presenter.ActiveEffects, Is.EqualTo(0));
            Assert.That(rig.Presenter.Budget.LightsActive, Is.EqualTo(0));
            Assert.That(rig.Presenter.Audio.ActiveVoices, Is.EqualTo(0));
            Assert.That(rig.Presenter.Budget.Cap(EffectCategory.Muzzle), Is.EqualTo(new EffectBudget(PresentationQuality.High).Cap(EffectCategory.Muzzle)));
        }

        [UnityTest]
        public IEnumerator ATracerPrefabWithoutATracerView_WarnsOnce_AndTheShotStillResolves()
        {
            rig = new PresentationRig();
            var brokenTracer = rig.Fx("BrokenTracer");   // a particle system, no TracerView
            rig.RifleProfile.SetTracer(TracerStyle.Create(brokenTracer));
            var shooter = rig.CreateShooter(new Vector3(0f, 0f, -6f), cooldown: 0f);
            var target = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("no TracerView"));
            for (var i = 0; i < 5; i++)
                Assert.That(shooter.GetComponent<UnitAttacker>().TryAttack(target), Is.True);
            Assert.That(target.Current, Is.EqualTo(target.Max - 50));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Tracer), Is.EqualTo(0), "the broken tracer is never kept");
            Assert.That(rig.Presenter.Budget.Active(EffectCategory.Tracer), Is.EqualTo(0), "and its budget slot was given back");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Muzzle), Is.GreaterThan(0), "the rest of the shot still shows");
            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
