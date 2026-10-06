using System;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class AbilityFailureTextTests
    {
        [Test]
        public void EveryFailure_HasAReadableDescription()
        {
            foreach (AbilityFailure failure in Enum.GetValues(typeof(AbilityFailure)))
                Assert.That(failure.Describe(), Is.Not.Empty, failure.ToString());
        }

        [TestCase(AbilityFailure.None, "ready")]
        [TestCase(AbilityFailure.OutOfRange, "out of range")]
        [TestCase(AbilityFailure.NoLineOfSight, "no line of sight")]
        [TestCase(AbilityFailure.OnCooldown, "on cooldown")]
        [TestCase(AbilityFailure.TargetDead, "target is down")]
        public void TheCommonReasons_ReadAsExpected(AbilityFailure failure, string text)
        {
            Assert.That(failure.Describe(), Is.EqualTo(text));
        }
    }
}
