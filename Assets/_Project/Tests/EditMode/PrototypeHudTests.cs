using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class PrototypeHudTests
    {
        [Test]
        public void DescribeOrders_Idle_IsEmpty()
        {
            Assert.That(PrototypeHud.DescribeOrders(null, 0), Is.Empty);
        }

        [Test]
        public void DescribeOrders_NamesTheCurrentOrder()
        {
            Assert.That(PrototypeHud.DescribeOrders(new MoveCommand(Vector3.zero), 0), Is.EqualTo("Move"));
        }

        [Test]
        public void DescribeOrders_CountsPendingOrders()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(PrototypeHud.DescribeOrders(attack, 2), Is.EqualTo("Attack +2"));
            Object.DestroyImmediate(host);
        }

        [TestCase(false, false, false, "Controlled: Hero | Takeover OFF (V) | Idle")]
        [TestCase(false, false, true, "Controlled: Hero | Takeover OFF (V) | Following orders")]
        [TestCase(false, true, false, "Controlled: Hero | Takeover OFF (V) | Idle")]
        [TestCase(false, true, true, "Controlled: Hero | Takeover OFF (V) | Following orders")]
        [TestCase(true, false, false, "Controlled: Hero | Takeover ON (V) | Manual control")]
        [TestCase(true, false, true, "Controlled: Hero | Takeover ON (V) | Following orders")]
        [TestCase(true, true, false, "Controlled: Hero | Takeover ON (after pause) | Idle")]
        [TestCase(true, true, true, "Controlled: Hero | Takeover ON (after pause) | Following orders")]
        public void DescribeActive_ShowsModeAndActivity(bool takeoverOn, bool isPaused, bool hasOrders, string expected)
        {
            Assert.That(PrototypeHud.DescribeActive("Hero", takeoverOn, isPaused, hasOrders), Is.EqualTo(expected));
        }

        [Test]
        public void DescribeSides_CountsBothSides()
        {
            Assert.That(PrototypeHud.DescribeSides(2, 3, 0, 3), Is.EqualTo("Friendlies alive 2/3 | Hostiles alive 0/3"));
        }

        [Test]
        public void DescribeNoActive_SaysNone()
        {
            Assert.That(PrototypeHud.DescribeNoActive(), Is.EqualTo("Controlled: none"));
        }

        [Test]
        public void DescribeUnit_NameHealthAndRole()
        {
            Assert.That(PrototypeHud.DescribeUnit("FriendlyUnit_2", 75, 100, CombatRole.Melee), Is.EqualTo("FriendlyUnit_2 75/100 [Melee]"));
            Assert.That(PrototypeHud.DescribeUnit("FriendlyUnit_3", 100, 100, CombatRole.Ranged), Is.EqualTo("FriendlyUnit_3 100/100 [Ranged]"));
        }

        [TestCase(EnemyState.Idle, null, 0f, "Idle")]
        [TestCase(EnemyState.Chase, "FriendlyUnit_1", 0f, "Chase -> FriendlyUnit_1")]
        [TestCase(EnemyState.Attack, "FriendlyUnit_1", 0.44f, "Attack -> FriendlyUnit_1 CD 0.4")]
        [TestCase(EnemyState.Reposition, "FriendlyUnit_3", 0f, "Reposition -> FriendlyUnit_3")]
        [TestCase(EnemyState.Dead, null, 0f, "Dead")]
        public void DescribeEnemy_StateTargetAndCooldown(EnemyState state, string target, float cooldown, string expected)
        {
            Assert.That(PrototypeHud.DescribeEnemy(state, target, cooldown), Is.EqualTo(expected));
        }

        [TestCase("", CompanionState.Idle, null, "Idle")]
        [TestCase("", CompanionState.Controlled, null, "Controlled")]
        [TestCase("Move", CompanionState.Follow, null, "Move | Follow")]
        [TestCase("Attack", CompanionState.Assist, "HostileUnit_1", "Attack | Assist -> HostileUnit_1")]
        [TestCase("Attack +1", CompanionState.Orders, null, "Attack +1 | Orders")]
        [TestCase("", CompanionState.Dead, null, "Dead")]
        public void DescribeCompanion_OrdersAndState(string orders, CompanionState state, string assistTarget, string expected)
        {
            Assert.That(PrototypeHud.DescribeCompanion(orders, state, assistTarget), Is.EqualTo(expected));
        }

        [Test]
        public void AppendSight_SaysClearOrBlocked()
        {
            Assert.That(PrototypeHud.AppendSight("Attack", true), Is.EqualTo("Attack LOS clear"));
            Assert.That(PrototypeHud.AppendSight("Reposition -> FriendlyUnit_1", false), Is.EqualTo("Reposition -> FriendlyUnit_1 LOS blocked"));
            Assert.That(PrototypeHud.AppendSight("", false), Is.EqualTo("LOS blocked"));
        }

        [Test]
        public void AppendCooldown_OnlyWhileCoolingDown()
        {
            Assert.That(PrototypeHud.AppendCooldown("Attack", 0f), Is.EqualTo("Attack"));
            Assert.That(PrototypeHud.AppendCooldown("Attack", 0.96f), Is.EqualTo("Attack CD 1.0"));
            Assert.That(PrototypeHud.AppendCooldown("", 0.5f), Is.EqualTo("CD 0.5"));
        }

        [TestCase(EncounterOutcome.Ongoing, "")]
        [TestCase(EncounterOutcome.Victory, "VICTORY - all hostiles are down")]
        [TestCase(EncounterOutcome.Defeat, "DEFEAT - the squad is down")]
        public void DescribeOutcome_BannerText(EncounterOutcome outcome, string expected)
        {
            Assert.That(PrototypeHud.DescribeOutcome(outcome), Is.EqualTo(expected));
        }
    }
}
