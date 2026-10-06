using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class EncounterSidesTests
    {
        GameObject systems;
        Encounter encounter;
        readonly List<GameObject> hosts = new List<GameObject>();
        Health friendlyA;
        Health friendlyB;
        Health hostileA;
        Health hostileB;
        Health stranger;

        Health Unit(string name)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            return host.AddComponent<Health>();
        }

        [SetUp]
        public void SetUp()
        {
            systems = new GameObject("Systems");
            encounter = systems.AddComponent<Encounter>();
            friendlyA = Unit("FriendlyA");
            friendlyB = Unit("FriendlyB");
            hostileA = Unit("HostileA");
            hostileB = Unit("HostileB");
            stranger = Unit("Stranger");
            encounter.Initialize(new[] { friendlyA, friendlyB }, new[] { hostileA, hostileB });
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(systems);
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        [Test]
        public void OpponentsOf_AFriendlyIsTheHostiles_AHostileIsTheFriendlies_AStrangerIsNobody()
        {
            Assert.That(encounter.OpponentsOf(friendlyA), Is.EqualTo(new[] { hostileA, hostileB }));
            Assert.That(encounter.OpponentsOf(hostileB), Is.EqualTo(new[] { friendlyA, friendlyB }));
            Assert.That(encounter.OpponentsOf(stranger), Is.Empty);
            Assert.That(encounter.OpponentsOf(null), Is.Empty);
        }

        [Test]
        public void AreHostile_IsTrueOnlyAcrossSides()
        {
            Assert.That(encounter.AreHostile(friendlyA, hostileA), Is.True);
            Assert.That(encounter.AreHostile(hostileB, friendlyB), Is.True);
            Assert.That(encounter.AreHostile(friendlyA, friendlyB), Is.False);
            Assert.That(encounter.AreHostile(hostileA, hostileB), Is.False);
            Assert.That(encounter.AreHostile(friendlyA, friendlyA), Is.False);
            Assert.That(encounter.AreHostile(friendlyA, stranger), Is.False);
        }

        [Test]
        public void AreAllied_IsTrueForTheSameSideOrTheSameUnit()
        {
            Assert.That(encounter.AreAllied(friendlyA, friendlyB), Is.True);
            Assert.That(encounter.AreAllied(hostileA, hostileB), Is.True);
            Assert.That(encounter.AreAllied(friendlyA, friendlyA), Is.True);
            Assert.That(encounter.AreAllied(friendlyA, hostileA), Is.False);
            Assert.That(encounter.AreAllied(friendlyA, stranger), Is.False);
        }

        [Test]
        public void Nulls_AndDestroyedUnits_AreNeverHostileOrAllied()
        {
            Assert.That(encounter.AreHostile(null, hostileA), Is.False);
            Assert.That(encounter.AreHostile(friendlyA, null), Is.False);
            Assert.That(encounter.AreAllied(null, null), Is.False);

            Object.DestroyImmediate(hostileA.gameObject);

            Assert.That(encounter.AreHostile(friendlyA, hostileA), Is.False);
            Assert.That(encounter.AreAllied(hostileA, hostileB), Is.False);
            Assert.That(encounter.OpponentsOf(hostileA), Is.Empty);
        }
    }
}
