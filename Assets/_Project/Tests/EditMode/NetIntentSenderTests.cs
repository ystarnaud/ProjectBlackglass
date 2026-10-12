using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetIntentSenderTests
    {
        [Test]
        public void NothingIsSent_WhileNothingIsHeld()
        {
            var sender = new NetIntentSender();
            Assert.That(sender.Next(Vector3.zero, 0d, out _), Is.False);
            Assert.That(sender.Next(Vector3.zero, 5d, out _), Is.False);
        }

        [Test]
        public void AHeldDirection_IsSentAtOnce_AndRefreshedEveryTenthOfASecond()
        {
            var sender = new NetIntentSender();
            Assert.That(sender.Next(Vector3.right, 1d, out var first), Is.True);
            Assert.That(first, Is.EqualTo(Vector3.right));
            Assert.That(sender.Next(Vector3.right, 1.05d, out _), Is.False, "too soon");
            Assert.That(sender.Next(Vector3.right, 1.1d, out _), Is.True, "refresh");
            Assert.That(sender.Next(Vector3.right, 1.15d, out _), Is.False);
            Assert.That(sender.Next(Vector3.right, 1.21d, out _), Is.True);
        }

        [Test]
        public void AChangedDirection_IsSentImmediately()
        {
            var sender = new NetIntentSender();
            sender.Next(Vector3.right, 1d, out _);
            Assert.That(sender.Next(Vector3.forward, 1.01d, out var send), Is.True);
            Assert.That(send, Is.EqualTo(Vector3.forward));
        }

        [Test]
        public void ReleasingSendsOneZero()
        {
            var sender = new NetIntentSender();
            sender.Next(Vector3.right, 1d, out _);
            Assert.That(sender.Next(Vector3.zero, 1.02d, out var send), Is.True);
            Assert.That(send, Is.EqualTo(Vector3.zero));
            Assert.That(sender.Next(Vector3.zero, 1.5d, out _), Is.False, "released: nothing more to send");
        }

        [Test]
        public void ReleaseBeforeAnOrder_SendsAZeroOnlyWhileSteering()
        {
            var sender = new NetIntentSender();
            Assert.That(sender.ReleaseBeforeOrder(out _), Is.False, "never steered");

            sender.Next(Vector3.right, 1d, out _);
            Assert.That(sender.ReleaseBeforeOrder(out var zero), Is.True);
            Assert.That(zero, Is.EqualTo(Vector3.zero));
            Assert.That(sender.ReleaseBeforeOrder(out _), Is.False, "already released");
            Assert.That(sender.Next(Vector3.zero, 1.2d, out _), Is.False, "the zero was already sent");
        }
    }
}
