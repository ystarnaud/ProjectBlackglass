using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class EnemyAITests
    {
        [Test]
        public void DeriveState_Dead_WhateverTheOrder()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(EnemyAI.DeriveState(false, null, AttackPhase.None), Is.EqualTo(EnemyState.Dead));
            Assert.That(EnemyAI.DeriveState(false, attack, AttackPhase.Attack), Is.EqualTo(EnemyState.Dead));
            Object.DestroyImmediate(host);
        }

        [Test]
        public void DeriveState_Idle_WithoutAnAttackOrder()
        {
            Assert.That(EnemyAI.DeriveState(true, null, AttackPhase.None), Is.EqualTo(EnemyState.Idle));
            Assert.That(EnemyAI.DeriveState(true, new MoveCommand(Vector3.zero), AttackPhase.None), Is.EqualTo(EnemyState.Idle));
        }

        [Test]
        public void DeriveState_FollowsTheAttackPhase()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(EnemyAI.DeriveState(true, attack, AttackPhase.Approach), Is.EqualTo(EnemyState.Chase));
            Assert.That(EnemyAI.DeriveState(true, attack, AttackPhase.Reposition), Is.EqualTo(EnemyState.Reposition));
            Assert.That(EnemyAI.DeriveState(true, attack, AttackPhase.Attack), Is.EqualTo(EnemyState.Attack));
            Object.DestroyImmediate(host);
        }

        [Test]
        public void DeriveState_AnAttackOrderThatHasNotTickedYet_IsChase()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(EnemyAI.DeriveState(true, attack, AttackPhase.None), Is.EqualTo(EnemyState.Chase));
            Object.DestroyImmediate(host);
        }

        [Test]
        public void RequiredComponentsAreAdded_AndDefaultsMatchThePrototype()
        {
            var host = new GameObject("Hostile");
            var ai = host.AddComponent<EnemyAI>();
            Assert.That(host.GetComponent<CommandableUnit>(), Is.Not.Null);
            Assert.That(host.GetComponent<Health>(), Is.Not.Null);
            Assert.That(host.GetComponent<UnitAttacker>(), Is.Not.Null);
            Assert.That(ai.DetectionRange, Is.EqualTo(12f));
            Assert.That(ai.CoverSearchRange, Is.EqualTo(8f));
            Assert.That(ai.IsCoverWired, Is.False);
            Assert.That(ai.Target, Is.Null);
            Object.DestroyImmediate(host);
        }

        [Test]
        public void DeriveState_Cover_WhileACoverOrderIsCurrent()
        {
            var host = new GameObject("Cover");
            var toCover = new MoveToCoverCommand(host.AddComponent<CoverPoint>());
            Assert.That(EnemyAI.DeriveState(true, toCover, AttackPhase.None), Is.EqualTo(EnemyState.Cover));
            Assert.That(EnemyAI.DeriveState(false, toCover, AttackPhase.None), Is.EqualTo(EnemyState.Dead));
            Object.DestroyImmediate(host);
        }
    }
}
