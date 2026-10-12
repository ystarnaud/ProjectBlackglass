using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class NetGateIntentTests
    {
        const string Attempt = "attempt-1";

        readonly List<GameObject> hosts = new List<GameObject>();
        CommandableUnit unit;
        int id;
        ServerCommandGate gate;
        NetConnection owner;
        uint counter;

        [SetUp]
        public void SetUp()
        {
            unit = NetTestKit.MakeUnit(hosts, "FriendlyUnit_1");
            var units = new NetUnits();
            id = units.Register(unit, false);
            gate = new ServerCommandGate(units, Attempt);
            owner = new NetConnection("account-a", new[] { id });
            counter = 0;
        }

        [TearDown]
        public void TearDown() => NetTestKit.DestroyAll(hosts);

        void Steer(Vector3 direction, double now)
        {
            counter++;
            Assert.That(gate.Handle(owner, new NetEnvelope(Attempt, counter, counter, new NetMoveIntent(id, direction)), now).IsAccepted, Is.True);
        }

        [Test]
        public void AnIntentExpiresWithoutARefresh()
        {
            Steer(Vector3.right, 10d);
            gate.Tick(10.4d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.right), "within the 0.5 s expiry");
            gate.Tick(10.6d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "past it");
        }

        [Test]
        public void ARefreshKeepsTheIntent()
        {
            Steer(Vector3.right, 10d);
            Steer(Vector3.right, 10.4d);
            gate.Tick(10.8d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.right), "0.4 s since the refresh");
            gate.Tick(11.0d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero), "0.6 s since the refresh");
        }

        [Test]
        public void AZeroIntentClearsAtOnce_AndTheGateStopsTrackingIt()
        {
            Steer(Vector3.right, 10d);
            Steer(Vector3.zero, 10.1d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.zero));
            unit.SetMoveIntent(Vector3.right);
            gate.Tick(20d);
            Assert.That(unit.MoveIntent, Is.EqualTo(Vector3.right), "an intent the gate is not tracking is left alone");
        }

        [Test]
        public void TickIgnoresDestroyedUnits()
        {
            Steer(Vector3.right, 10d);
            Object.DestroyImmediate(unit.gameObject);
            Assert.DoesNotThrow(() => gate.Tick(11d));
        }
    }
}
