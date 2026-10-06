using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class AbilityRulesTests
    {
        AbilityDefinition aimed;
        AbilityDefinition mend;
        AbilityDefinition blast;

        [SetUp]
        public void SetUp()
        {
            aimed = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, AbilityTargetSide.Hostile, 14f, true,
                AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            mend = AbilityDefinition.Create("Mend", AbilityTargetMode.Unit, AbilityTargetSide.Friendly, 8f, false,
                AbilityCoverRule.Ignored, 8f, AbilityEffect.Heal, 40);
            blast = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, AbilityTargetSide.Hostile, 12f, true,
                AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(aimed);
            Object.DestroyImmediate(mend);
            Object.DestroyImmediate(blast);
        }

        static AbilityFacts Facts(bool casterAlive = true, bool hasAbility = true, bool hasTarget = true,
            bool targetAlive = true, bool targetOnSide = true, bool hasPosition = true, float cooldown = 0f,
            float distance = 5f) =>
            new AbilityFacts(casterAlive, hasAbility, hasTarget, targetAlive, targetOnSide, hasPosition, cooldown, distance);

        const AbilityCheckScope Full = AbilityCheckScope.Full;
        const AbilityCheckScope Static = AbilityCheckScope.Static;

        [Test]
        public void EverythingInOrder_IsNone()
        {
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(), Full), Is.EqualTo(AbilityFailure.None));
            Assert.That(AbilityRules.CheckBasics(blast, Facts(), Full), Is.EqualTo(AbilityFailure.None));
        }

        [Test]
        public void EachFailure_IsReported()
        {
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(casterAlive: false), Full), Is.EqualTo(AbilityFailure.CasterDead));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(hasAbility: false), Full), Is.EqualTo(AbilityFailure.UnknownAbility));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(hasTarget: false), Full), Is.EqualTo(AbilityFailure.NoTarget));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(targetAlive: false), Full), Is.EqualTo(AbilityFailure.TargetDead));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(targetOnSide: false), Full), Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(AbilityRules.CheckBasics(blast, Facts(hasPosition: false), Full), Is.EqualTo(AbilityFailure.NoPosition));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(cooldown: 0.1f), Full), Is.EqualTo(AbilityFailure.OnCooldown));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(distance: 14.01f), Full), Is.EqualTo(AbilityFailure.OutOfRange));
        }

        [Test]
        public void TheFirstFailureInTheDocumentedOrderWins()
        {
            var everythingWrong = Facts(casterAlive: false, hasAbility: false, hasTarget: false, targetAlive: false,
                targetOnSide: false, cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, everythingWrong, Full), Is.EqualTo(AbilityFailure.CasterDead));

            var noAbility = Facts(hasAbility: false, hasTarget: false, cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, noAbility, Full), Is.EqualTo(AbilityFailure.UnknownAbility));

            var noTarget = Facts(hasTarget: false, targetAlive: false, cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, noTarget, Full), Is.EqualTo(AbilityFailure.NoTarget));

            var deadTarget = Facts(targetAlive: false, targetOnSide: false, cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, deadTarget, Full), Is.EqualTo(AbilityFailure.TargetDead));

            var wrongSide = Facts(targetOnSide: false, cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, wrongSide, Full), Is.EqualTo(AbilityFailure.WrongSide));

            var coolingAndFar = Facts(cooldown: 3f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, coolingAndFar, Full), Is.EqualTo(AbilityFailure.OnCooldown));
        }

        [Test]
        public void ARangeCheckIsInclusive()
        {
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(distance: 14f), Full), Is.EqualTo(AbilityFailure.None));
            Assert.That(AbilityRules.IsInRange(14f, 14f), Is.True);
            Assert.That(AbilityRules.IsInRange(14.0001f, 14f), Is.False);
        }

        [Test]
        public void TheStaticScope_OnlyChecksWhoAndWhat_NotWhenOrHowFar()
        {
            var late = Facts(cooldown: 5f, distance: 99f);
            Assert.That(AbilityRules.CheckBasics(aimed, late, Static), Is.EqualTo(AbilityFailure.None));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(targetAlive: false, cooldown: 5f), Static), Is.EqualTo(AbilityFailure.TargetDead));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(targetOnSide: false), Static), Is.EqualTo(AbilityFailure.WrongSide));
            Assert.That(AbilityRules.CheckBasics(aimed, Facts(casterAlive: false), Static), Is.EqualTo(AbilityFailure.CasterDead));
        }

        [Test]
        public void AGroundAbility_IgnoresTheTargetFacts_AndNeedsAPosition()
        {
            var noTargetAtAll = Facts(hasTarget: false, targetAlive: false, targetOnSide: false);
            Assert.That(AbilityRules.CheckBasics(blast, noTargetAtAll, Full), Is.EqualTo(AbilityFailure.None));
            Assert.That(AbilityRules.CheckBasics(blast, Facts(hasPosition: false), Static), Is.EqualTo(AbilityFailure.NoPosition));
        }

        [Test]
        public void ANullAbility_IsUnknown_NotACrash()
        {
            Assert.That(AbilityRules.CheckBasics(null, Facts(hasAbility: false), Full), Is.EqualTo(AbilityFailure.UnknownAbility));
        }

        [Test]
        public void Sight_IsOnlyRequiredWhenTheAbilityAsksForIt()
        {
            Assert.That(AbilityRules.CheckSight(aimed, false), Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(AbilityRules.CheckSight(aimed, true), Is.EqualTo(AbilityFailure.None));
            Assert.That(AbilityRules.CheckSight(blast, false), Is.EqualTo(AbilityFailure.NoLineOfSight));
            Assert.That(AbilityRules.CheckSight(mend, false), Is.EqualTo(AbilityFailure.None), "Mend ignores walls");
        }

        [Test]
        public void IsInArea_IsFlatAndInclusive()
        {
            var center = new Vector3(0f, 0f, 4f);
            Assert.That(AbilityRules.IsInArea(center, new Vector3(0f, 0f, 4f), 3f), Is.True);
            Assert.That(AbilityRules.IsInArea(center, new Vector3(3f, 0f, 4f), 3f), Is.True, "On the edge counts");
            Assert.That(AbilityRules.IsInArea(center, new Vector3(3.01f, 0f, 4f), 3f), Is.False);
            Assert.That(AbilityRules.IsInArea(center, new Vector3(0f, 1f, 4f), 0.5f), Is.True, "Height is ignored: a pivot is 1 m up");
            // (2, 6) is a flat distance of 2.828 from (0, 4).
            Assert.That(AbilityRules.IsInArea(center, new Vector3(2f, 0f, 6f), 2.83f), Is.True);
            Assert.That(AbilityRules.IsInArea(center, new Vector3(2f, 0f, 6f), 2.8f), Is.False);
        }
    }
}
