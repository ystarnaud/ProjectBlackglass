using System;
using System.Linq;
using System.Reflection;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>The library-free admission boundary (spec §14.6, owner ruling D5): shape only, pollable, fitting the harness and tickets.</summary>
    public sealed class NetAdmissionContractTests
    {
        [Test]
        public void Admit_requires_a_non_empty_account_and_distinct_keys()
        {
            Assert.Throws<ArgumentException>(() => AdmissionDecision.Admit("", new string[0], new string[0]));
            Assert.Throws<ArgumentException>(() => AdmissionDecision.Admit(null, new string[0], new string[0]));
            Assert.Throws<ArgumentException>(() => AdmissionDecision.Admit("a", new[] { "1", "1" }, new string[0]));
            Assert.Throws<ArgumentException>(() => AdmissionDecision.Admit("a", new[] { "" }, new string[0]));
            Assert.Throws<ArgumentException>(() => AdmissionDecision.Admit("a", Enumerable.Range(1, 9).Select(i => i.ToString()).ToArray(), new string[0]));
            Assert.Throws<ArgumentException>(() => AdmissionDecision.Admit("a", new string[0], new[] { "" }));
            Assert.Throws<ArgumentException>(() => AdmissionDecision.Admit("a", new string[0], new[] { "fixture", "fixture" }));
            Assert.That(AdmissionDecision.Admit("a", Enumerable.Range(1, 8).Select(i => i.ToString()).ToArray(), new string[0]).OwnedUnitKeys.Count, Is.EqualTo(8));
        }

        [Test]
        public void The_harness_shape_fits()
        {
            var owner = AdmissionDecision.Admit("owner", new[] { "1", "2", "3" }, new[] { "fixture" });
            Assert.That(owner.Status, Is.EqualTo(AdmissionStatus.Admitted));
            Assert.That(owner.Refusal, Is.EqualTo(AdmissionRefusal.None));
            Assert.That(owner.Account, Is.EqualTo("owner"));
            Assert.That(owner.OwnedUnitKeys, Is.EqualTo(new[] { "1", "2", "3" }));
            Assert.That(owner.Capabilities, Is.EqualTo(new[] { "fixture" }));
            var spectator = AdmissionDecision.Admit("spectator", new string[0], new string[0]);
            Assert.That(spectator.OwnedUnitKeys, Is.Empty);
            var ticket = AdmissionDecision.Admit("JG", new[] { "C", "D" }, new string[0]);
            Assert.That(ticket.OwnedUnitKeys, Is.EqualTo(new[] { "C", "D" }));
        }

        [Test]
        public void Pending_and_refused_decisions()
        {
            var pending = AdmissionDecision.Pending();
            Assert.That((pending.Status, pending.Refusal, pending.Account), Is.EqualTo((AdmissionStatus.Pending, AdmissionRefusal.None, (string)null)));
            Assert.That(pending.OwnedUnitKeys, Is.Empty);
            var refused = AdmissionDecision.Refuse(AdmissionRefusal.WrongAttempt);
            Assert.That((refused.Status, refused.Refusal), Is.EqualTo((AdmissionStatus.Refused, AdmissionRefusal.WrongAttempt)));
        }

        [Test]
        public void Refuse_requires_a_reason() => Assert.Throws<ArgumentException>(() => AdmissionDecision.Refuse(AdmissionRefusal.None));

        [TestCase(AdmissionRefusal.None, NetRefusal.None)]
        [TestCase(AdmissionRefusal.UnknownToken, NetRefusal.UnknownToken)]
        [TestCase(AdmissionRefusal.WrongAttempt, NetRefusal.WrongAttempt)]
        [TestCase(AdmissionRefusal.AlreadyConnected, NetRefusal.AlreadyConnected)]
        [TestCase(AdmissionRefusal.Timeout, NetRefusal.UnknownToken)]
        [TestCase(AdmissionRefusal.Unavailable, NetRefusal.UnknownToken)]
        public void Every_refusal_maps_to_an_existing_wire_byte(AdmissionRefusal refusal, NetRefusal wire) =>
            Assert.That(AdmissionRefusals.WireByte(refusal), Is.EqualTo((byte)wire));

        [Test]
        public void Every_refusal_is_mapped() =>
            Assert.That(Enum.GetValues(typeof(AdmissionRefusal)).Cast<AdmissionRefusal>().Select(AdmissionRefusals.WireByte).All(b => Enum.IsDefined(typeof(NetRefusal), b)), Is.True);

        [Test]
        public void Decisions_are_immutable()
        {
            var keys = new[] { "1", "2" };
            var capabilities = new[] { "fixture" };
            var d = AdmissionDecision.Admit("owner", keys, capabilities);
            keys[0] = "9";
            capabilities[0] = "other";
            Assert.That(d.OwnedUnitKeys, Is.EqualTo(new[] { "1", "2" }));
            Assert.That(d.Capabilities, Is.EqualTo(new[] { "fixture" }));
            Assert.That(d.OwnedUnitKeys, Is.Not.InstanceOf<string[]>(), "not castable back to a mutable array");
            Assert.That(typeof(AdmissionDecision).GetProperties().All(p => p.SetMethod == null || !p.SetMethod.IsPublic), Is.True);
        }

        [Test]
        public void The_interface_mentions_no_connection_or_transport_type()
        {
            var allowed = new[] { typeof(string), typeof(int), typeof(void) };
            foreach (var member in typeof(INetAdmission).GetMethods())
            {
                foreach (var type in member.GetParameters().Select(p => p.ParameterType).Concat(new[] { member.ReturnType }))
                    Assert.That(allowed.Contains(type) || type.Namespace == "Blackglass.Backend.Core", Is.True, member.Name + ": " + type.FullName);
            }
            Assert.That(typeof(INetAdmission).GetMethods().Select(m => m.Name), Is.SupersetOf(new[] { "Begin", "Poll", "Release", "get_AttemptId", "get_Admitted" }));
        }
    }
}
