#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitAbilitiesModifiersPlayModeTests
    {
        TestWorld world;
        Encounter encounter;
        AbilityDefinition aimed;
        AbilityDefinition mend;
        UnitAbilities abilities;
        Health casterHealth;
        Health ally;
        Health hostile;

        [SetUp]
        public void SetUp()
        {
            world = new TestWorld();
            world.CreateEnvironment((new Vector3(-3f, 1.5f, -2f), new Vector3(4f, 3f, 0.5f)));
            encounter = world.CreateEncounter();
            aimed = world.CreateAimedShot();
            mend = world.CreateMend();
            var caster = world.CreateFighter(new Vector3(0f, 0f, -6f));
            casterHealth = caster.GetComponent<Health>();
            abilities = world.AddAbilities(caster, encounter, aimed, mend);
            var allyUnit = world.CreateFighter(new Vector3(4f, 0f, -6f));
            ally = allyUnit.GetComponent<Health>();
            hostile = world.CreateDummy(new Vector3(3f, 0f, 2f));
            encounter.Initialize(new[] { casterHealth, ally }, new[] { hostile });
        }

        [TearDown]
        public void TearDown()
        {
            world.Dispose();
            Time.timeScale = 1f;
        }

        [Test]
        public void Defaults_AreOne_AndChangeNothing()
        {
            Assert.That(abilities.PowerMultiplier, Is.EqualTo(1f));
            Assert.That(abilities.CooldownMultiplier, Is.EqualTo(1f));
            Assert.That(abilities.EffectiveAmount(aimed), Is.EqualTo(aimed.Amount));
            Assert.That(abilities.EffectiveCooldown(aimed), Is.EqualTo(aimed.Cooldown));
        }

        [UnityTest]
        public IEnumerator Power_ScalesDamage_AndCooldownMultiplier_ShortensTheCooldown()
        {
            yield return null;
            abilities.SetModifiers(1.2f, 0.5f);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(aimed, hostile)), Is.True);

            Assert.That(hostile.Max - hostile.Current, Is.EqualTo(54), "45 * 1.2");
            Assert.That(abilities.CooldownRemaining(0), Is.EqualTo(3f).Within(0.1f), "6 s * 0.5");
        }

        [UnityTest]
        public IEnumerator Power_ScalesHealing()
        {
            yield return null;
            ally.TakeDamage(80);
            abilities.SetModifiers(1.5f, 1f);

            Assert.That(abilities.TryUse(AbilityCommand.OnUnit(mend, ally)), Is.True);

            Assert.That(ally.Current, Is.EqualTo(ally.Max - 80 + 60), "40 * 1.5 = 60 restored");
        }

        [Test]
        public void SetModifiers_NeverGoesNegative()
        {
            abilities.SetModifiers(-1f, -1f);
            Assert.That(abilities.PowerMultiplier, Is.EqualTo(0f));
            Assert.That(abilities.CooldownMultiplier, Is.EqualTo(0f));
        }
    }
}
#endif
