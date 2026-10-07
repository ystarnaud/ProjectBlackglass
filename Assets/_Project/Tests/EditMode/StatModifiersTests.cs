using NUnit.Framework;

namespace Blackglass.Tests
{
    public class StatModifiersTests
    {
        [Test]
        public void Default_ChangesNothing()
        {
            var none = default(StatModifiers);
            Assert.That(none.maxHealth, Is.EqualTo(0));
            Assert.That(none.moveSpeed, Is.EqualTo(0f));
            Assert.That(none.attackDamage, Is.EqualTo(0f));
            Assert.That(none.abilityPower, Is.EqualTo(0f));
            Assert.That(none.abilityCooldownReduction, Is.EqualTo(0f));
        }

        [Test]
        public void Combine_AddsEveryField()
        {
            var a = new StatModifiers { maxHealth = 25, moveSpeed = 0.5f, attackDamage = 0.1f, abilityPower = 0.2f, abilityCooldownReduction = 0.05f };
            var b = new StatModifiers { maxHealth = 10, moveSpeed = 0.25f, attackDamage = 0.15f, abilityPower = 0.1f, abilityCooldownReduction = 0.1f };

            var sum = StatModifiers.Combine(a, b);

            Assert.That(sum.maxHealth, Is.EqualTo(35));
            Assert.That(sum.moveSpeed, Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(sum.attackDamage, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(sum.abilityPower, Is.EqualTo(0.3f).Within(0.0001f));
            Assert.That(sum.abilityCooldownReduction, Is.EqualTo(0.15f).Within(0.0001f));
        }

        [Test]
        public void Combine_WithDefault_ReturnsTheOther()
        {
            var a = new StatModifiers { maxHealth = 25, attackDamage = 0.15f };
            var sum = StatModifiers.Combine(a, default);
            Assert.That(sum.maxHealth, Is.EqualTo(25));
            Assert.That(sum.attackDamage, Is.EqualTo(0.15f));
        }
    }
}
