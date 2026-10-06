using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class AbilityCommandTests
    {
        AbilityDefinition aimed;
        AbilityDefinition blast;
        GameObject host;
        Health target;

        [SetUp]
        public void SetUp()
        {
            aimed = AbilityDefinition.Create("Aimed Shot", AbilityTargetMode.Unit, AbilityTargetSide.Hostile, 14f, true,
                AbilityCoverRule.Applies, 6f, AbilityEffect.Damage, 45);
            blast = AbilityDefinition.Create("Blast", AbilityTargetMode.Ground, AbilityTargetSide.Hostile, 12f, true,
                AbilityCoverRule.Ignored, 10f, AbilityEffect.Damage, 35, 3f);
            host = new GameObject("Target");
            host.transform.position = new Vector3(2f, 1f, 3f);
            target = host.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(aimed);
            Object.DestroyImmediate(blast);
            Object.DestroyImmediate(host);
        }

        [Test]
        public void OnUnit_KeepsTheDefinitionAndTheTarget_AndAimsAtIt()
        {
            var command = AbilityCommand.OnUnit(aimed, target);

            Assert.That(command.Definition, Is.SameAs(aimed));
            Assert.That(command.Target, Is.SameAs(target));
            Assert.That(command.AimPoint, Is.EqualTo(new Vector3(2f, 1f, 3f)));
        }

        [Test]
        public void AtGround_KeepsTheDefinitionAndThePoint_AndAimsAtIt()
        {
            var command = AbilityCommand.AtGround(blast, new Vector3(5f, 0f, -2f));

            Assert.That(command.Definition, Is.SameAs(blast));
            Assert.That(command.Target, Is.Null);
            Assert.That(command.Point, Is.EqualTo(new Vector3(5f, 0f, -2f)));
            Assert.That(command.AimPoint, Is.EqualTo(new Vector3(5f, 0f, -2f)));
        }

        [Test]
        public void TheFactories_RejectNullsAndTheWrongTargetMode()
        {
            Assert.Throws<ArgumentNullException>(() => AbilityCommand.OnUnit(null, target));
            Assert.Throws<ArgumentNullException>(() => AbilityCommand.OnUnit(aimed, null));
            Assert.Throws<ArgumentNullException>(() => AbilityCommand.AtGround(null, Vector3.zero));
            Assert.Throws<ArgumentException>(() => AbilityCommand.OnUnit(blast, target));
            Assert.Throws<ArgumentException>(() => AbilityCommand.AtGround(aimed, Vector3.zero));
        }

        [Test]
        public void AUnitCommand_WhoseTargetWasDestroyed_StillAimsWithoutThrowing()
        {
            var command = AbilityCommand.OnUnit(aimed, target);
            Object.DestroyImmediate(host);

            Assert.That(() => command.AimPoint, Throws.Nothing);
        }

        [Test]
        public void ItIsAnOrderLikeTheOthers()
        {
            UnitCommand command = AbilityCommand.OnUnit(aimed, target);
            Assert.That(command, Is.InstanceOf<AbilityCommand>());
        }
    }
}
