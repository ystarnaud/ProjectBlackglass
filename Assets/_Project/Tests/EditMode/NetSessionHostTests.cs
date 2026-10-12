using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class NetSessionHostTests
    {
        const string Attempt = "attempt-1";

        readonly List<GameObject> hosts = new List<GameObject>();
        CommandableUnit friendA;
        NetSessionHost host;
        uint counter;

        [SetUp]
        public void SetUp()
        {
            friendA = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            var friendB = NetTestKit.MakeUnit(hosts, "FriendlyUnit_2");
            var hostile = NetTestKit.MakeUnit(hosts, "HostileUnit_1");
            var units = NetUnits.FromMission(new List<CommandableUnit> { friendA, friendB }, new List<CommandableUnit> { hostile });
            var gate = new ServerCommandGate(units, Attempt);
            var admission = new NetAdmission(new List<NetTokenEntry>
            {
                new NetTokenEntry("account-a", "tok-a", true, null),
                new NetTokenEntry("account-b", "tok-b", false, null),
                new NetTokenEntry("account-c", "tok-c", true, null),
            }, Attempt, friendlyCount: 2);
            host = new NetSessionHost(gate, admission);
            counter = 0;
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        NetEnvelope Steer(int unitId = 1)
        {
            counter++;
            return new NetEnvelope(Attempt, counter, counter, new NetMoveIntent(unitId, Vector3.right));
        }

        NetEnvelope BadStop()
        {
            counter++;
            return new NetEnvelope(Attempt, counter, counter, new NetStop(1));
        }

        void Join(int clientId, string token, double now = 0d)
        {
            host.Connected(clientId, now);
            Assert.That(host.Hello(clientId, token, now, out _, out _, out _), Is.EqualTo(NetRefusal.None));
        }

        [Test]
        public void ARequestBeforeHello_IsNotAuthenticated_AndChangesNothing()
        {
            host.Connected(1, 0d);
            var outcome = host.Request(1, Steer(), 0d);
            Assert.That(outcome.Disposition, Is.EqualTo(NetDisposition.NotAuthenticated));
            Assert.That(friendA.MoveIntent, Is.EqualTo(Vector3.zero));
            Assert.That(host.Accepted, Is.EqualTo(0));
        }

        [Test]
        public void AWelcomedClient_CanSteer()
        {
            host.Connected(1, 0d);
            var refusal = host.Hello(1, "tok-a", 0d, out var attempt, out var first, out var count);
            Assert.That(refusal, Is.EqualTo(NetRefusal.None));
            Assert.That(attempt, Is.EqualTo(Attempt));
            Assert.That(first, Is.EqualTo(1));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(host.AccountOf(1), Is.EqualTo("account-a"));

            var outcome = host.Request(1, Steer(), 0d);
            Assert.That(outcome.Disposition, Is.EqualTo(NetDisposition.Handled));
            Assert.That(outcome.Result.IsAccepted, Is.True);
            Assert.That((friendA.MoveIntent - Vector3.right).magnitude, Is.LessThan(1e-4f));
        }

        [Test]
        public void ARefusedHello_LeavesTheClientUnauthenticated()
        {
            host.Connected(1, 0d);
            Assert.That(host.Hello(1, "nope", 0d, out _, out _, out _), Is.EqualTo(NetRefusal.UnknownToken));
            Assert.That(host.AccountOf(1), Is.Null);
            Assert.That(host.Request(1, Steer(), 0d).Disposition, Is.EqualTo(NetDisposition.NotAuthenticated));
        }

        [Test]
        public void AClientThatNeverSaysHello_ExpiresAfterTheTimeout()
        {
            host.Connected(1, 10d);
            host.Connected(2, 10d);
            host.Hello(2, "tok-b", 11d, out _, out _, out _);
            var expired = new List<int>();

            host.CollectExpired(14.9d, expired);
            Assert.That(expired, Is.Empty, "inside the 5 s timeout");

            host.CollectExpired(15.1d, expired);
            Assert.That(expired, Is.EqualTo(new[] { 1 }), "client 2 said hello in time");

            expired.Clear();
            host.CollectExpired(20d, expired);
            Assert.That(expired, Is.Empty, "an expired client is forgotten");
        }

        [Test]
        public void AFlood_IsDropped_ThenTheClientIsDisconnected()
        {
            Join(1, "tok-a");
            var dispositions = new List<NetDisposition>();
            for (var i = 0; i < 1000 && (dispositions.Count == 0 || dispositions.Last() != NetDisposition.Disconnect); i++)
                dispositions.Add(host.Request(1, Steer(), 0d).Disposition);

            Assert.That(dispositions.Take(NetSessionHost.Burst).All(d => d == NetDisposition.Handled), Is.True, "the burst is served");
            Assert.That(dispositions.Skip(NetSessionHost.Burst).Take(NetSessionHost.AbuseLimit).All(d => d == NetDisposition.OverBudget), Is.True,
                "then requests are dropped");
            Assert.That(dispositions.Last(), Is.EqualTo(NetDisposition.Disconnect));
            Assert.That(dispositions, Has.Count.EqualTo(NetSessionHost.Burst + NetSessionHost.AbuseLimit + 1));
            Assert.That(host.Dropped, Is.EqualTo(NetSessionHost.AbuseLimit + 1));
        }

        [Test]
        public void AFloodingClient_DoesNotStarveAnother()
        {
            Join(1, "tok-a");
            Join(2, "tok-c");
            for (var i = 0; i < 400; i++)
                host.Request(1, Steer(), 0d);

            Assert.That(host.Request(2, Steer(), 0d).Disposition, Is.EqualTo(NetDisposition.Handled));
        }

        [Test]
        public void RejectedRequests_CountTowardsAbuse()
        {
            Join(1, "tok-b");
            NetOutcome? first = null;
            var sent = 0;
            for (var second = 0; second < 5 && first == null; second++)
            {
                for (var i = 0; i < 60 && first == null; i++)
                {
                    var outcome = host.Request(1, BadStop(), second);
                    sent++;
                    if (outcome.Disposition == NetDisposition.Disconnect)
                        first = outcome;
                    else
                        Assert.That(outcome.Disposition, Is.EqualTo(NetDisposition.Handled), "request " + sent);
                }
            }

            Assert.That(first.HasValue, Is.True);
            Assert.That(sent, Is.EqualTo(NetSessionHost.AbuseLimit + 1), "the 201st rejection inside 10 s ends the connection");
            Assert.That(first.Value.Result.Reason, Is.EqualTo(NetRejectReason.NotOwner));
        }

        [Test]
        public void ADisconnectedClients_TokenCanBeUsedAgain()
        {
            Join(1, "tok-a");
            host.Disconnected(1);
            host.Connected(2, 1d);
            Assert.That(host.Hello(2, "tok-a", 1d, out _, out _, out _), Is.EqualTo(NetRefusal.None));
        }

        [Test]
        public void TheCounters_FollowTheOutcomes()
        {
            Join(1, "tok-b");
            host.Request(1, Steer(), 0d);
            host.Request(1, BadStop(), 0d);
            Assert.That(host.Accepted, Is.EqualTo(0));
            Assert.That(host.Rejected, Is.EqualTo(2), "tok-b owns nothing, so both are refused");

            Join(2, "tok-a");
            host.Request(2, Steer(), 0d);
            Assert.That(host.Accepted, Is.EqualTo(1));
        }
    }
}
