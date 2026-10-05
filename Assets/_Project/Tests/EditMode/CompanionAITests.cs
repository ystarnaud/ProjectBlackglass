using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CompanionAITests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
                Object.DestroyImmediate(host);
            hosts.Clear();
        }

        Health MakeHealth(string name, Vector3 position, bool alive = true, bool active = true)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            host.transform.position = position;
            var health = host.AddComponent<Health>();
            health.Initialize(10);
            if (!alive)
                health.TakeDamage(10);   // disableOnDeath deactivates it too
            if (!active)
                host.SetActive(false);
            return health;
        }

        static bool Always(Health _) => true;
        static bool Never(Health _) => false;
        static bool Reachable(Vector3 _) => true;

        // --- DeriveState ---

        [Test]
        public void DeriveState_DeadBeatsEverything()
        {
            var own = new MoveCommand(Vector3.zero);
            Assert.That(CompanionAI.DeriveState(false, false, own, 0, own), Is.EqualTo(CompanionState.Dead));
            Assert.That(CompanionAI.DeriveState(false, true, null, 0, null), Is.EqualTo(CompanionState.Dead));
        }

        [Test]
        public void DeriveState_ControlledBeatsOrders()
        {
            var order = new MoveCommand(Vector3.zero);
            Assert.That(CompanionAI.DeriveState(true, true, order, 2, null), Is.EqualTo(CompanionState.Controlled));
            Assert.That(CompanionAI.DeriveState(true, true, null, 0, null), Is.EqualTo(CompanionState.Controlled));
        }

        [Test]
        public void DeriveState_IdleWithoutOrders()
        {
            Assert.That(CompanionAI.DeriveState(true, false, null, 0, null), Is.EqualTo(CompanionState.Idle));
        }

        [Test]
        public void DeriveState_OrdersWhenTheCurrentOrderIsNotOurs_OrAnythingIsPending()
        {
            var own = new MoveCommand(Vector3.zero);
            var theirs = new MoveCommand(Vector3.one);
            Assert.That(CompanionAI.DeriveState(true, false, theirs, 0, own), Is.EqualTo(CompanionState.Orders), "someone replaced our order");
            Assert.That(CompanionAI.DeriveState(true, false, theirs, 0, null), Is.EqualTo(CompanionState.Orders), "an explicit order while we had none");
            Assert.That(CompanionAI.DeriveState(true, false, own, 1, own), Is.EqualTo(CompanionState.Orders), "a queued order behind ours");
        }

        [Test]
        public void DeriveState_AssistForOurAttack_FollowForOurMove()
        {
            var target = MakeHealth("Hostile", Vector3.zero);
            var attack = new AttackCommand(target);
            var move = new MoveCommand(Vector3.zero);
            Assert.That(CompanionAI.DeriveState(true, false, attack, 0, attack), Is.EqualTo(CompanionState.Assist));
            Assert.That(CompanionAI.DeriveState(true, false, move, 0, move), Is.EqualTo(CompanionState.Follow));
        }

        // --- ChooseAssistTarget ---

        [Test]
        public void ChooseAssistTarget_PrefersTheLeadersTarget_WhenItIsAValidHostile()
        {
            var near = MakeHealth("Near", new Vector3(2f, 1f, 0f));
            var leaders = MakeHealth("Leaders", new Vector3(8f, 1f, 0f));
            var hostiles = new[] { near, leaders };

            var chosen = CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, leaders, hostiles, Always, Reachable);

            Assert.That(chosen, Is.SameAs(leaders));
        }

        [Test]
        public void ChooseAssistTarget_LeadersTargetOutsideTheList_OutOfRange_OrDead_IsIgnored()
        {
            var near = MakeHealth("Near", new Vector3(2f, 1f, 0f));
            var notAHostile = MakeHealth("Friendly", new Vector3(3f, 1f, 0f));
            var farHostile = MakeHealth("Far", new Vector3(12f, 1f, 0f));
            var deadHostile = MakeHealth("Dead", new Vector3(4f, 1f, 0f), alive: false);
            var hostiles = new[] { near, farHostile, deadHostile };

            Assert.That(CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, notAHostile, hostiles, Always, Reachable), Is.SameAs(near));
            Assert.That(CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, farHostile, hostiles, Always, Reachable), Is.SameAs(near));
            Assert.That(CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, deadHostile, hostiles, Always, Reachable), Is.SameAs(near));
        }

        [Test]
        public void ChooseAssistTarget_OtherwiseTheNearestEngagedHostile_InRangeAndReachable()
        {
            var idle = MakeHealth("Idle", new Vector3(1f, 1f, 0f));
            var engagedFar = MakeHealth("EngagedFar", new Vector3(9f, 1f, 0f));
            var engagedNear = MakeHealth("EngagedNear", new Vector3(5f, 1f, 0f));
            var engagedOutOfRange = MakeHealth("OutOfRange", new Vector3(11f, 1f, 0f));
            var engagedUnreachable = MakeHealth("Unreachable", new Vector3(3f, 1f, 0f));
            var dead = MakeHealth("Dead", new Vector3(0.5f, 1f, 0f), alive: false);
            var inactive = MakeHealth("Inactive", new Vector3(0.5f, 1f, 0f), active: false);
            var hostiles = new[] { idle, engagedFar, engagedNear, engagedOutOfRange, engagedUnreachable, dead, inactive, null };

            var chosen = CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, null, hostiles, h => h != idle, p => p.x != 3f);

            Assert.That(chosen, Is.SameAs(engagedNear));
        }

        [Test]
        public void ChooseAssistTarget_NothingEngaged_IsNull()
        {
            var idle = MakeHealth("Idle", new Vector3(1f, 1f, 0f));
            Assert.That(CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, null, new[] { idle }, Never, Reachable), Is.Null);
            Assert.That(CompanionAI.ChooseAssistTarget(Vector3.zero, 10f, null, new Health[0], Always, Reachable), Is.Null);
        }

        // --- FollowPoint ---

        [Test]
        public void FollowPoint_LiesOnTheCompanionsSideOfTheLeader_AtTheFollowDistance()
        {
            var leader = new Vector3(10f, 1f, 10f);
            var companion = new Vector3(10f, 1f, 0f);
            var point = CompanionAI.FollowPoint(leader, companion, 3.5f, Vector3.forward);
            Assert.That(point.x, Is.EqualTo(10f).Within(0.001f));
            Assert.That(point.z, Is.EqualTo(6.5f).Within(0.001f));
            Assert.That(point.y, Is.EqualTo(leader.y), "the point keeps the leader's height");
        }

        [Test]
        public void FollowPoint_OnTopOfTheLeader_FallsBehindIt()
        {
            var leader = new Vector3(0f, 1f, 0f);
            var point = CompanionAI.FollowPoint(leader, leader, 3.5f, Vector3.right);
            Assert.That(point.x, Is.EqualTo(-3.5f).Within(0.001f), "behind a leader facing +x");
            Assert.That(point.z, Is.EqualTo(0f).Within(0.001f));
        }

        // --- Component ---

        [Test]
        public void RequiredComponentsAreAdded_AndDefaultsMatchThePrototype()
        {
            var host = new GameObject("Companion");
            hosts.Add(host);
            var ai = host.AddComponent<CompanionAI>();
            Assert.That(host.GetComponent<CommandableUnit>(), Is.Not.Null);
            Assert.That(host.GetComponent<Health>(), Is.Not.Null);
            Assert.That(host.GetComponent<UnitAttacker>(), Is.Not.Null);
            Assert.That(ai.FollowDistance, Is.EqualTo(3.5f));
            Assert.That(ai.FollowStartDistance, Is.EqualTo(6f));
            Assert.That(ai.AssistRange, Is.EqualTo(10f));
            Assert.That(ai.IsWired, Is.False);
            Assert.That(ai.AssistTarget, Is.Null);
            Assert.That(ai.IsFollowing, Is.False);
            Assert.That(ai.State, Is.EqualTo(CompanionState.Idle), "unwired and idle: nobody controls it, it has no orders");
        }
    }
}
