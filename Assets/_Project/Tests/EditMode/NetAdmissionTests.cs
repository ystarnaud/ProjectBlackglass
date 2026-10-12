using System.Collections.Generic;
using NUnit.Framework;

namespace Blackglass.Tests
{
    public class NetAdmissionTests
    {
        const string Attempt = "attempt-1";

        static NetAdmission Make() => new NetAdmission(new List<NetTokenEntry>
        {
            new NetTokenEntry("account-a", "tok-a", true, null),
            new NetTokenEntry("account-b", "tok-b", false, null),
            new NetTokenEntry("account-t", "tok-t", false, "other-attempt"),
            new NetTokenEntry("account-s", "tok-s", true, Attempt),
        }, Attempt, friendlyCount: 2);

        [Test]
        public void ASquadToken_OwnsEveryFriendly()
        {
            var admission = Make();
            var refusal = admission.TryAdmit(1, "tok-a", out var connection, out var first, out var count);
            Assert.That(refusal, Is.EqualTo(NetRefusal.None));
            Assert.That(first, Is.EqualTo(1));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(connection.Account, Is.EqualTo("account-a"));
            Assert.That(connection.Owns(1), Is.True);
            Assert.That(connection.Owns(2), Is.True);
            Assert.That(connection.Owns(3), Is.False, "the hostile is nobody's");
            Assert.That(admission.Admitted, Is.EqualTo(1));
        }

        [Test]
        public void ANoneToken_OwnsNothing()
        {
            var refusal = Make().TryAdmit(1, "tok-b", out var connection, out var first, out var count);
            Assert.That(refusal, Is.EqualTo(NetRefusal.None));
            Assert.That(count, Is.EqualTo(0));
            Assert.That(connection.Owns(1), Is.False);
        }

        [TestCase("nope")]
        [TestCase("")]
        [TestCase(null)]
        public void AnUnknownOrEmptyToken_IsRefused(string token)
        {
            var admission = Make();
            var refusal = admission.TryAdmit(1, token, out var connection, out _, out _);
            Assert.That(refusal, Is.EqualTo(NetRefusal.UnknownToken));
            Assert.That(connection, Is.Null);
            Assert.That(admission.Admitted, Is.EqualTo(0));
        }

        [Test]
        public void AnEntryBoundToAnotherAttempt_IsRefused()
        {
            var refusal = Make().TryAdmit(1, "tok-t", out var connection, out _, out _);
            Assert.That(refusal, Is.EqualTo(NetRefusal.WrongAttempt));
            Assert.That(connection, Is.Null);
        }

        [Test]
        public void AnEntryBoundToThisAttempt_IsAdmitted()
        {
            Assert.That(Make().TryAdmit(1, "tok-s", out _, out _, out _), Is.EqualTo(NetRefusal.None));
        }

        [Test]
        public void ASecondClientWithTheSameToken_IsRefused_AndTheFirstIsUnaffected()
        {
            var admission = Make();
            admission.TryAdmit(1, "tok-a", out var first, out _, out _);
            var refusal = admission.TryAdmit(2, "tok-a", out var second, out _, out _);
            Assert.That(refusal, Is.EqualTo(NetRefusal.AlreadyConnected));
            Assert.That(second, Is.Null);
            Assert.That(admission.Admitted, Is.EqualTo(1));
            Assert.That(first.Owns(1), Is.True);
        }

        [Test]
        public void ReleasingAClient_FreesItsToken()
        {
            var admission = Make();
            admission.TryAdmit(1, "tok-a", out _, out _, out _);
            admission.Release(1);
            Assert.That(admission.Admitted, Is.EqualTo(0));
            Assert.That(admission.TryAdmit(2, "tok-a", out _, out _, out _), Is.EqualTo(NetRefusal.None));
        }

        [Test]
        public void ReleasingAnUnknownClient_IsHarmless()
        {
            var admission = Make();
            admission.TryAdmit(1, "tok-a", out _, out _, out _);
            Assert.DoesNotThrow(() => admission.Release(99));
            Assert.That(admission.Admitted, Is.EqualTo(1));
        }

        [Test]
        public void TheSameClientAdmittedTwice_IsRefused()
        {
            var admission = Make();
            admission.TryAdmit(1, "tok-a", out _, out _, out _);
            Assert.That(admission.TryAdmit(1, "tok-b", out _, out _, out _), Is.EqualTo(NetRefusal.AlreadyConnected));
        }
    }
}
