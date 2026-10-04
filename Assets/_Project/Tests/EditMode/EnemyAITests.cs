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
            Assert.That(EnemyAI.DeriveState(false, null, false), Is.EqualTo(EnemyState.Dead));
            Assert.That(EnemyAI.DeriveState(false, attack, true), Is.EqualTo(EnemyState.Dead));
            Object.DestroyImmediate(host);
        }

        [Test]
        public void DeriveState_Idle_WithoutAnAttackOrder()
        {
            Assert.That(EnemyAI.DeriveState(true, null, false), Is.EqualTo(EnemyState.Idle));
            Assert.That(EnemyAI.DeriveState(true, new MoveCommand(Vector3.zero), false), Is.EqualTo(EnemyState.Idle));
        }

        [Test]
        public void DeriveState_ChaseOutOfRange_AttackInRange()
        {
            var host = new GameObject("Target");
            var attack = new AttackCommand(host.AddComponent<Health>());
            Assert.That(EnemyAI.DeriveState(true, attack, false), Is.EqualTo(EnemyState.Chase));
            Assert.That(EnemyAI.DeriveState(true, attack, true), Is.EqualTo(EnemyState.Attack));
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
            Assert.That(ai.Target, Is.Null);
            Object.DestroyImmediate(host);
        }
    }
}
