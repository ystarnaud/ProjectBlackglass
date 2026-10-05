using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class CoverRegistryTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();
        CoverRegistry registry;

        [SetUp]
        public void SetUp() => registry = Host("Registry").AddComponent<CoverRegistry>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
                Object.DestroyImmediate(host);
            hosts.Clear();
        }

        GameObject Host(string name)
        {
            var host = new GameObject(name);
            hosts.Add(host);
            return host;
        }

        static CoverLocation At(float x) => new CoverLocation($"C{x}", new Vector3(x, 0f, 0f), Vector3.forward, null);

        [Test]
        public void Rebuild_ReplacesTheList_BumpsTheVersion_AndRaisesChanged()
        {
            var raised = 0;
            registry.Changed += () => raised++;
            var versionBefore = registry.Version;
            var a = At(0f);
            registry.Rebuild(new[] { a });
            Assert.That(registry.Points, Is.EqualTo(new[] { a }));
            Assert.That(registry.Version, Is.EqualTo(versionBefore + 1));
            Assert.That(raised, Is.EqualTo(1));
        }

        [Test]
        public void Rebuild_RetiresLocationsThatAreNotInTheReplacement_AndDropsTheirClaims()
        {
            var kept = At(0f);
            var dropped = At(5f);
            registry.Rebuild(new[] { kept, dropped });
            var unit = Host("Unit").AddComponent<UnitCover>();
            Assert.That(unit.TryReserve(dropped), Is.True);

            registry.Rebuild(new[] { kept });

            Assert.That(dropped.IsValid, Is.False);
            Assert.That(dropped.IsClaimed, Is.False, "A retired location holds no claim");
            Assert.That(kept.IsValid, Is.True, "A location that is still listed is left alone");
        }

        [Test]
        public void Rebuild_KeepsTheClaimOnALocationThatStaysListed()
        {
            var kept = At(0f);
            registry.Rebuild(new[] { kept });
            var unit = Host("Unit").AddComponent<UnitCover>();
            unit.TryReserve(kept);
            registry.Rebuild(new[] { kept, At(9f) });
            Assert.That(kept.Claimant, Is.SameAs(unit));
        }

        [Test]
        public void Rebuild_RejectsNull_AndInitializeIsRebuild()
        {
            Assert.Throws<System.ArgumentNullException>(() => registry.Rebuild(null));
            var a = At(1f);
            registry.Initialize(a);
            Assert.That(registry.Points, Is.EqualTo(new[] { a }));
        }
    }
}
