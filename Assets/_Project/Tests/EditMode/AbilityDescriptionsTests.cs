using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class AbilityDescriptionsTests
    {
        AbilityDefinition aimed;
        AbilityDefinition blast;
        AbilityDefinition mend;
        GameObject bandit;
        GameObject friend;
        Health banditHealth;
        Health friendHealth;

        [SetUp]
        public void SetUp()
        {
            aimed = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, 14f, true,
                AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            blast = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, 12f, true,
                AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);
            mend = AbilityDefinition.Create("Mend", AbilityTargetMode.Unit, 8f, false,
                AbilityCoverRule.Ignored, 8f, AbilityEffect.Heal, 40);
            bandit = new GameObject("Bandit");
            banditHealth = bandit.AddComponent<Health>();
            friend = new GameObject("Ally");
            friendHealth = friend.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(aimed);
            Object.DestroyImmediate(blast);
            Object.DestroyImmediate(mend);
            Object.DestroyImmediate(bandit);
            Object.DestroyImmediate(friend);
        }

        static AbilityCheck Check(AbilityFailure failure, float distance, bool sight = true, bool inCover = false, float chance = 1f) =>
            new AbilityCheck(failure, distance, sight, inCover, chance);

        AbilityPreview OnUnit(AbilityDefinition ability, Health target, AbilityCheck check, bool queued = false) =>
            new AbilityPreview(ability, PointerTargetKind.Hostile, target, Vector3.zero, true, check, queued);

        [Test]
        public void Slot_ShowsTheKeyNameAndReadinessOrTheCooldown()
        {
            Assert.That(AbilityDescriptions.Slot(0, "1", "Aimed Shot", 0f, true), Is.EqualTo("> [1] Aimed Shot  1  ready"));
            Assert.That(AbilityDescriptions.Slot(2, "RT + D-pad Down", "Mend", 3.24f, false), Is.EqualTo("  [3] Mend  RT + D-pad Down  CD 3.2"));
        }

        [Test]
        public void Preview_AValidUnitShot_ShowsDistanceSightCoverAndOk()
        {
            var text = AbilityDescriptions.Preview(OnUnit(aimed, banditHealth, Check(AbilityFailure.None, 8.544f)), new Health[0]);
            Assert.That(text, Is.EqualTo("Aimed Shot -> Bandit | 8.5/14.0 m | LOS clear | exposed | OK"));
        }

        [Test]
        public void Preview_ACoveredTarget_ShowsItsHitChance()
        {
            var text = AbilityDescriptions.Preview(OnUnit(aimed, banditHealth, Check(AbilityFailure.None, 6f, true, true, 0.5f)), new Health[0]);
            Assert.That(text, Is.EqualTo("Aimed Shot -> Bandit | 6.0/14.0 m | LOS clear | cover 50% | OK"));
        }

        [Test]
        public void Preview_AFailure_NamesTheReasonInsteadOfOk()
        {
            Assert.That(AbilityDescriptions.Preview(OnUnit(aimed, banditHealth, Check(AbilityFailure.OutOfRange, 17f)), new Health[0]),
                Is.EqualTo("Aimed Shot -> Bandit | 17.0/14.0 m | LOS clear | exposed | out of range"));
            Assert.That(AbilityDescriptions.Preview(OnUnit(aimed, banditHealth, Check(AbilityFailure.NoLineOfSight, 9f, false)), new Health[0]),
                Is.EqualTo("Aimed Shot -> Bandit | 9.0/14.0 m | LOS blocked | exposed | no line of sight"));
            Assert.That(AbilityDescriptions.Preview(OnUnit(aimed, friendHealth, Check(AbilityFailure.WrongSide, 4f)), new Health[0]),
                Does.EndWith("| wrong side for this ability"));
        }

        [Test]
        public void Preview_AGroundAbility_ShowsThePointTheRuleAboutCoverAndWhoItWouldHit()
        {
            var ground = new AbilityPreview(blast, PointerTargetKind.Ground, null, new Vector3(5f, 0f, 2f), true,
                Check(AbilityFailure.None, 9.43f), false);

            Assert.That(AbilityDescriptions.Preview(ground, new[] { banditHealth }),
                Is.EqualTo("Blast @ (5.0, 2.0) | 9.4/12.0 m | LOS clear | ignores cover | hits 1 (Bandit) | OK"));
            Assert.That(AbilityDescriptions.Preview(ground, new Health[0]),
                Is.EqualTo("Blast @ (5.0, 2.0) | 9.4/12.0 m | LOS clear | ignores cover | hits 0 | OK"));
        }

        [Test]
        public void Preview_AHeal_NeedsNoSightAndMentionsNoCover()
        {
            var text = AbilityDescriptions.Preview(
                new AbilityPreview(mend, PointerTargetKind.Friendly, friendHealth, Vector3.zero, true, Check(AbilityFailure.None, 4f), false),
                new Health[0]);
            Assert.That(text, Is.EqualTo("Mend -> Ally | 4.0/8.0 m | no LOS needed | OK"));
        }

        [Test]
        public void Preview_NoAimYet_AsksForOne_AndAQueuedPreviewSaysItIsCheckedLater()
        {
            Assert.That(AbilityDescriptions.Preview(new AbilityPreview(aimed, PointerTargetKind.Ground, null, Vector3.zero, false, default, false), new Health[0]),
                Is.EqualTo("Aimed Shot: choose a target"));
            Assert.That(AbilityDescriptions.Preview(new AbilityPreview(blast, PointerTargetKind.None, null, Vector3.zero, false, default, false), new Health[0]),
                Is.EqualTo("Blast: choose a position"));
            Assert.That(AbilityDescriptions.Preview(AbilityPreview.None, new Health[0]), Is.Empty);

            var queued = AbilityDescriptions.Preview(OnUnit(aimed, banditHealth, Check(AbilityFailure.None, 17f), true), new Health[0]);
            Assert.That(queued, Does.EndWith("| OK (range, sight and cooldown are checked when it runs)"));
        }

        [Test]
        public void Failure_AndOrder_ReadAsShortSentences()
        {
            Assert.That(AbilityDescriptions.Failure("Aimed Shot", AbilityFailure.OnCooldown), Is.EqualTo("Aimed Shot: on cooldown"));
            Assert.That(AbilityDescriptions.Order(AbilityCommand.OnUnit(aimed, banditHealth)), Is.EqualTo("Aimed Shot -> Bandit"));
            Assert.That(AbilityDescriptions.Order(AbilityCommand.AtGround(blast, new Vector3(5f, 0f, 2f))), Is.EqualTo("Blast @ (5.0, 2.0)"));
        }

        [Test]
        public void TheHudUnitLabel_ShowsTheArchetypeNameWhenThereIsOne()
        {
            Assert.That(PrototypeHud.DescribeUnit("FriendlyUnit_2", 100, 100, CombatRole.Ranged, "Marksman"), Is.EqualTo("FriendlyUnit_2 100/100 [Marksman]"));
            Assert.That(PrototypeHud.DescribeUnit("FriendlyUnit_2", 100, 100, CombatRole.Ranged, null), Is.EqualTo("FriendlyUnit_2 100/100 [Ranged]"));
            Assert.That(PrototypeHud.DescribeUnit("FriendlyUnit_2", 100, 100, CombatRole.Ranged, ""), Is.EqualTo("FriendlyUnit_2 100/100 [Ranged]"));
        }
    }
}
