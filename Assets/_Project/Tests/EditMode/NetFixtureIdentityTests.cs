using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>Who may drive the development fixture (spec section 3.9): only a connection admitted with a `+fixture` token.</summary>
    public class NetFixtureIdentityTests
    {
        const string Attempt = "attempt-1";

        readonly List<GameObject> hosts = new List<GameObject>();
        NetSessionHost host;

        [SetUp]
        public void SetUp()
        {
            var units = NetUnits.FromMission(
                new List<CommandableUnit> { NetTestKit.MakeUnit(hosts, "FriendlyUnit_1"), NetTestKit.MakeUnit(hosts, "FriendlyUnit_2") },
                new List<CommandableUnit> { NetTestKit.MakeUnit(hosts, "HostileUnit_1") });
            var admission = new NetAdmission(new List<NetTokenEntry>
            {
                new NetTokenEntry("account-a", "tok-a", true, null, fixture: true),
                new NetTokenEntry("account-b", "tok-b", false, null),
            }, Attempt, friendlyCount: 2);
            host = new NetSessionHost(new ServerCommandGate(units, Attempt, NetSight.Everything), admission);
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        void Join(int clientId, string token)
        {
            host.Connected(clientId, 0d);
            Assert.That(host.Hello(clientId, token, 0d, out _, out _, out _), Is.EqualTo(NetRefusal.None));
        }

        [Test]
        public void AFixtureSuffix_MarksTheEntry()
        {
            var entries = NetTokenFile.Parse("account-a tok-a squad+fixture\naccount-b tok-b none+fixture\naccount-c tok-c squad\n", out var warnings);
            Assert.That(warnings, Is.Empty);
            Assert.That(entries, Has.Count.EqualTo(3));
            Assert.That(entries[0].OwnsSquad && entries[0].Fixture, Is.True);
            Assert.That(!entries[1].OwnsSquad && entries[1].Fixture, Is.True);
            Assert.That(entries[2].OwnsSquad && !entries[2].Fixture, Is.True);
        }

        [Test]
        public void AnUnknownSuffix_IsSkipped_WithoutEchoingTheToken()
        {
            var entries = NetTokenFile.Parse("account-a tok-secret squad+admin\n", out var warnings);
            Assert.That(entries, Is.Empty);
            Assert.That(warnings, Has.Count.EqualTo(1));
            StringAssert.DoesNotContain("tok-secret", warnings[0]);
            StringAssert.StartsWith("line 1", warnings[0]);
        }

        [Test]
        public void AFixtureEntry_GivesAConnectionThatDrivesTheFixture()
        {
            Join(1, "tok-a");
            Join(2, "tok-b");
            Assert.That(host.ConnectionOf(1).DrivesFixture, Is.True);
            Assert.That(host.ConnectionOf(2).DrivesFixture, Is.False);
            Assert.That(host.ConnectionOf(9), Is.Null);
        }

        [Test]
        public void AFixtureMessageBeforeHello_IsIgnored()
        {
            host.Connected(1, 0d);
            Assert.That(host.FixtureRequest(1, true, 0d, out var connection), Is.EqualTo(NetFixtureAdmission.Ignored));
            Assert.That(connection, Is.Null);
            Assert.That(host.FixtureRequest(7, true, 0d, out _), Is.EqualTo(NetFixtureAdmission.Ignored), "an unknown client too");
            Assert.That(host.Rejected + host.Dropped, Is.EqualTo(0));
        }

        [Test]
        public void TheTestIdentity_IsAllowed_AndBoundToItsOwnConnection()
        {
            Join(1, "tok-a");
            Assert.That(host.FixtureRequest(1, true, 0d, out var connection), Is.EqualTo(NetFixtureAdmission.Allowed));
            Assert.That(connection, Is.SameAs(host.ConnectionOf(1)));
        }

        [Test]
        public void WithTheFixtureOff_EvenTheTestIdentityIsRefused()
        {
            Join(1, "tok-a");
            Assert.That(host.FixtureRequest(1, false, 0d, out var connection), Is.EqualTo(NetFixtureAdmission.Refused));
            Assert.That(connection, Is.Null);
        }

        [Test]
        public void AnOrdinaryClient_IsRefused_AndCountsTowardsAbuse()
        {
            Join(2, "tok-b");
            for (var i = 0; i < NetSessionHost.AbuseLimit; i++)
                Assert.That(host.FixtureRequest(2, true, i * 0.02d, out _), Is.EqualTo(NetFixtureAdmission.Refused), "message " + i);
            Assert.That(host.FixtureRequest(2, true, NetSessionHost.AbuseLimit * 0.02d, out _), Is.EqualTo(NetFixtureAdmission.Disconnect));
            Assert.That(host.Rejected, Is.EqualTo(NetSessionHost.AbuseLimit + 1));
        }

        [Test]
        public void AFixtureFlood_IsDropped_ThenDisconnects()
        {
            Join(1, "tok-a");
            for (var i = 0; i < NetSessionHost.Burst; i++)
                Assert.That(host.FixtureRequest(1, true, 0d, out _), Is.EqualTo(NetFixtureAdmission.Allowed));
            for (var i = 0; i < NetSessionHost.AbuseLimit; i++)
                Assert.That(host.FixtureRequest(1, true, 0d, out _), Is.EqualTo(NetFixtureAdmission.Ignored), "over budget " + i);
            Assert.That(host.FixtureRequest(1, true, 0d, out _), Is.EqualTo(NetFixtureAdmission.Disconnect));
            Assert.That(host.Dropped, Is.EqualTo(NetSessionHost.AbuseLimit + 1));
        }
    }
}
