using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class HostileTargetsFilterTests
    {
        readonly List<GameObject> created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                Object.DestroyImmediate(go);
            created.Clear();
        }

        Health Hostile(float x, float z)
        {
            var go = new GameObject("Hostile");
            go.transform.position = new Vector3(x, 0f, z);
            created.Add(go);
            var health = go.AddComponent<Health>();
            health.Initialize(100);
            return health;
        }

        [Test]
        public void Best_SkipsACandidateTheFilterRejects()
        {
            var hidden = Hostile(0f, 2f);
            var shown = Hostile(0f, 9f);
            Assert.That(HostileTargets.Best(new[] { hidden, shown }, Vector3.zero, Vector3.zero, h => h == shown), Is.SameAs(shown));
            Assert.That(HostileTargets.Best(new[] { hidden, shown }, Vector3.zero, Vector3.zero, h => false), Is.Null);
            Assert.That(HostileTargets.Best(new[] { hidden, shown }, Vector3.zero, Vector3.zero), Is.SameAs(hidden), "no filter: as before");
        }

        [Test]
        public void Cycle_WalksOnlyTheCandidatesTheFilterAdmits()
        {
            var a = Hostile(0f, 2f);
            var b = Hostile(0f, 5f);
            var c = Hostile(0f, 9f);
            System.Func<Health, bool> onlyAC = h => h != b;
            Assert.That(HostileTargets.Cycle(new[] { a, b, c }, Vector3.zero, null, 1, onlyAC), Is.SameAs(a));
            Assert.That(HostileTargets.Cycle(new[] { a, b, c }, Vector3.zero, a, 1, onlyAC), Is.SameAs(c), "b is skipped");
            Assert.That(HostileTargets.Cycle(new[] { a, b, c }, Vector3.zero, c, 1, onlyAC), Is.SameAs(a), "and it wraps");
            Assert.That(HostileTargets.Cycle(new[] { a, b, c }, Vector3.zero, null, 1, h => false), Is.Null);
        }
    }
}
