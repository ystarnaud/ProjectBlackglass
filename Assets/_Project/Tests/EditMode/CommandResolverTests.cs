using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CommandResolverTests
    {
        GameObject host;
        Health health;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Target");
            health = host.AddComponent<Health>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void ClickOnLivingTarget_ResolvesToAttack()
        {
            var command = CommandResolver.Resolve(health, new Vector3(1f, 0f, 2f));
            Assert.That(command, Is.TypeOf<AttackCommand>());
            Assert.That(((AttackCommand)command).Target, Is.SameAs(health));
        }

        [Test]
        public void ClickOnGround_ResolvesToMoveToClickedPoint()
        {
            var point = new Vector3(3f, 0f, -4f);
            var command = CommandResolver.Resolve(null, point);
            Assert.That(command, Is.TypeOf<MoveCommand>());
            Assert.That(((MoveCommand)command).Destination, Is.EqualTo(point));
        }

        [Test]
        public void ClickOnDeadTarget_ResolvesToMove()
        {
            health.TakeDamage(health.Max);
            var point = new Vector3(5f, 0f, 5f);
            var command = CommandResolver.Resolve(health, point);
            Assert.That(command, Is.TypeOf<MoveCommand>());
        }

        [Test]
        public void AttackCommand_RejectsNullTarget()
        {
            Assert.Throws<System.ArgumentNullException>(() => new AttackCommand(null));
        }
    }
}
