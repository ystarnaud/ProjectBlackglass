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
        [TestCase(EnemyState.Cover, "FriendlyUnit_1", 0f, "Cover -> FriendlyUnit_1")]
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

        [TestCase("", CompanionState.Idle, null, "Idle [parked]")]
        [TestCase("Attack +1", CompanionState.Orders, null, "Attack +1 | Orders [parked]")]
        [TestCase("Attack", CompanionState.Assist, "HostileUnit_1", "Attack | Assist -> HostileUnit_1 [parked]")]
        [TestCase("Move", CompanionState.Follow, null, "Move | Follow [parked]")]
        [TestCase("", CompanionState.Dead, null, "Dead")]
        [TestCase("", CompanionState.Controlled, null, "Controlled")]
        public void DescribeCompanion_Parked_IsMarkedExceptForDeadAndControlled(string orders, CompanionState state, string assistTarget, string expected)
        {
            Assert.That(PrototypeHud.DescribeCompanion(orders, state, assistTarget, true), Is.EqualTo(expected));
        }

        [TestCase("", CompanionState.Idle, null, false, "Idle [held]")]
        [TestCase("", CompanionState.Idle, null, true, "Idle [parked] [held]")]
        [TestCase("Attack", CompanionState.Assist, "HostileUnit_1", false, "Attack | Assist -> HostileUnit_1 [held]")]
        [TestCase("", CompanionState.Dead, null, false, "Dead")]
        [TestCase("", CompanionState.Controlled, null, true, "Controlled")]
        public void DescribeCompanion_Held_IsMarkedExceptForDeadAndControlled(string orders, CompanionState state, string assistTarget, bool parked, string expected)
        {
            Assert.That(PrototypeHud.DescribeCompanion(orders, state, assistTarget, parked, true), Is.EqualTo(expected));
        }

        [Test]
        public void DescribeCompanion_NotParked_HasNoMarker()
        {
            Assert.That(PrototypeHud.DescribeCompanion("", CompanionState.Idle, null, false), Is.EqualTo("Idle"));
        }

        [Test]
        public void DescribeFollow_ShowsTheFlagAndItsKey()
        {
            Assert.That(PrototypeHud.DescribeFollow(true), Is.EqualTo("Follow: ON (F)"));
            Assert.That(PrototypeHud.DescribeFollow(false), Is.EqualTo("Follow: OFF (F)"));
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

        [TestCase(CoverStatus.None, "Cover_LowWall_L_S1", false, "")]
        [TestCase(CoverStatus.Reserved, "Cover_LowWall_L_S1", false, "Cover: Cover_LowWall_L_S1 (reserved)")]
        [TestCase(CoverStatus.Occupied, "Cover_LowWall_L_S1", false, "Cover: Cover_LowWall_L_S1 (occupied)")]
        [TestCase(CoverStatus.Occupied, "Cover_LowWall_L_S1", true, "Cover: Cover_LowWall_L_S1 (occupied, ordered)")]
        [TestCase(CoverStatus.Occupied, null, true, "")]
        public void DescribeCover_StatusAndPoint(CoverStatus status, string pointName, bool byOrder, string expected)
        {
            Assert.That(PrototypeHud.DescribeCover(status, pointName, byOrder), Is.EqualTo(expected));
        }

        [Test]
        public void AppendTargetCover_SaysInCoverWithTheChance_OrExposed()
        {
            Assert.That(PrototypeHud.AppendTargetCover("Attack", true, 0.5f), Is.EqualTo("Attack target in cover 50%"));
            Assert.That(PrototypeHud.AppendTargetCover("Attack LOS clear", true, 0.25f), Is.EqualTo("Attack LOS clear target in cover 25%"));
            Assert.That(PrototypeHud.AppendTargetCover("", false, 1f), Is.EqualTo("target exposed"));
        }

        [Test]
        public void AppendHits_OnlyAfterTheFirstShot()
        {
            Assert.That(PrototypeHud.AppendHits("Attack", 0, 0), Is.EqualTo("Attack"));
            Assert.That(PrototypeHud.AppendHits("Attack", 3, 7), Is.EqualTo("Attack hits 3/7"));
            Assert.That(PrototypeHud.AppendHits("", 0, 2), Is.EqualTo("hits 0/2"));
        }
    }
}
