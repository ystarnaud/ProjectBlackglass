#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class HealPulsePlayModeTests
    {
        PresentationRig rig;

        [TearDown]
        public void TearDown() => rig?.Dispose();

        [UnityTest]
        public IEnumerator HealingARealWound_ShowsOnePulse_AndAtFullHealthShowsNothing()
        {
            rig = new PresentationRig();
            var unit = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;

            Assert.That(unit.Heal(10), Is.EqualTo(0), "Precondition: full health, nothing restored");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(0));

            unit.TakeDamage(30);
            Assert.That(unit.Heal(30), Is.EqualTo(30));
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Mend_PlaysThePulseWhenItSucceeds_NotWhenQueuedOrRefused_AndHealingIsUnchanged()
        {
            rig = new PresentationRig();
            var caster = rig.CreateShooter(new Vector3(0f, 0f, -4f));
            var ally = rig.CreateTarget(new Vector3(2f, 0f, -4f));
            var encounter = rig.World.CreateEncounter();
            encounter.Initialize(new[] { caster.GetComponent<Health>(), ally }, new Health[0]);
            var mend = rig.World.CreateMend();
            var abilities = rig.World.AddAbilities(caster, encounter, mend);
            ally.TakeDamage(50);
            yield return null;
            rig.Presenter.Clear();

            Assert.That(abilities.CanQueue(AbilityCommand.OnUnit(mend, ally)), Is.True, "queuing is only a check");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(0));

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.True);
            Assert.That(ally.Current, Is.EqualTo(ally.Max - 10), "Mend restores 40 hit points, exactly as before");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(1));

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.False, "on cooldown: refused");
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(1), "a refused use shows nothing");
        }

        [UnityTest]
        public IEnumerator PulseOnAHiddenUnit_IsNotShown()
        {
            rig = new PresentationRig();
            var unit = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            unit.TakeDamage(30);
            rig.Presenter.UseKnowledge(_ => PointVisibility.Visible, _ => false);
            unit.Heal(30);
            Assert.That(rig.Presenter.ActiveOf(EffectCategory.Pulse), Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Pulse_FreezesWithThePause()
        {
            rig = new PresentationRig();
            var unit = rig.CreateTarget(new Vector3(0f, 0f, 4f));
            yield return null;
            unit.TakeDamage(30);
            unit.Heal(30);
            var effect = Object.FindFirstObjectByType<PooledEffect>(FindObjectsInactive.Exclude);
            rig.Pause.Pause();
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(effect.IsActive, Is.True);
            Assert.That(effect.Expired, Is.False);
            rig.Pause.Resume();
        }
    }
}
#endif
