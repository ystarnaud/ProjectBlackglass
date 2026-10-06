using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class HostileTargetsTests
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
            var health = go.AddComponent<Health>();
            health.Initialize(100);
            created.Add(go);
            return health;
        }

        static readonly Vector3 Origin = Vector3.zero;

        [Test]
        public void Best_WithNoFacing_IsTheNearest()
        {
            var far = Hostile(0f, 9f);
            var near = Hostile(0f, -3f);
            Assert.That(HostileTargets.Best(new[] { far, near }, Origin, Vector3.zero), Is.SameAs(near));
        }

        [Test]
        public void Best_PrefersTheOneAheadOverANearerOneBehind_UpToAPoint()
        {
            var behind = Hostile(0f, -5f);
            var ahead = Hostile(0f, 8f);
            Assert.That(HostileTargets.Best(new[] { behind, ahead }, Origin, Vector3.forward), Is.SameAs(ahead));

            var veryNearBehind = Hostile(0f, -2f);
            var farAhead = Hostile(0f, 20f);
            Assert.That(HostileTargets.Best(new[] { veryNearBehind, farAhead }, Origin, Vector3.forward), Is.SameAs(veryNearBehind));
        }

        [Test]
        public void Best_IgnoresHeight_WhenFacing()
        {
            var near = Hostile(0f, 4f);
            near.transform.position += Vector3.up * 5f;
            var far = Hostile(0f, 9f);
            Assert.That(HostileTargets.Best(new[] { far, near }, Origin, new Vector3(0f, 3f, 1f)), Is.SameAs(near));
        }

        [Test]
        public void Best_SkipsDeadInactiveAndNullEntries()
        {
            var dead = Hostile(0f, 1f);
            dead.TakeDamage(1000);
            var hidden = Hostile(0f, 2f);
            hidden.gameObject.SetActive(false);
            var alive = Hostile(0f, 10f);
            Assert.That(HostileTargets.Best(new[] { dead, null, hidden, alive }, Origin, Vector3.zero), Is.SameAs(alive));
        }

        [Test]
        public void Best_OfNothing_IsNull()
        {
            Assert.That(HostileTargets.Best(new Health[0], Origin, Vector3.forward), Is.Null);
            var dead = Hostile(0f, 1f);
            dead.TakeDamage(1000);
            Assert.That(HostileTargets.Best(new[] { dead }, Origin, Vector3.forward), Is.Null);
        }

        [Test]
        public void Cycle_GoesByDistance_AndWrapsBothWays()
        {
            var far = Hostile(9f, 0f);
            var near = Hostile(2f, 0f);
            var mid = Hostile(0f, 5f);
            var list = new[] { far, near, mid };

            Assert.That(HostileTargets.Cycle(list, Origin, null, 1), Is.SameAs(near), "Forward from nothing: nearest");
            Assert.That(HostileTargets.Cycle(list, Origin, near, 1), Is.SameAs(mid));
            Assert.That(HostileTargets.Cycle(list, Origin, mid, 1), Is.SameAs(far));
            Assert.That(HostileTargets.Cycle(list, Origin, far, 1), Is.SameAs(near), "Wraps forward");
            Assert.That(HostileTargets.Cycle(list, Origin, null, -1), Is.SameAs(far), "Backward from nothing: farthest");
            Assert.That(HostileTargets.Cycle(list, Origin, near, -1), Is.SameAs(far), "Wraps backward");
        }

        [Test]
        public void Cycle_SkipsDeadEntries_AndRecoversFromADeadCurrent()
        {
            var near = Hostile(2f, 0f);
            var mid = Hostile(0f, 5f);
            var far = Hostile(9f, 0f);
            var list = new[] { near, mid, far };
            mid.TakeDamage(1000);

            Assert.That(HostileTargets.Cycle(list, Origin, near, 1), Is.SameAs(far));
            Assert.That(HostileTargets.Cycle(list, Origin, mid, 1), Is.SameAs(near), "A dead current starts from the beginning");
        }

        [Test]
        public void Cycle_WithOneOrNone()
        {
            var only = Hostile(3f, 0f);
            Assert.That(HostileTargets.Cycle(new[] { only }, Origin, only, 1), Is.SameAs(only));
            Assert.That(HostileTargets.Cycle(new Health[0], Origin, null, 1), Is.Null);
            only.TakeDamage(1000);
            Assert.That(HostileTargets.Cycle(new[] { only }, Origin, null, -1), Is.Null);
        }

        [Test]
        public void Cycle_KeepsListOrderForEqualDistances()
        {
            var a = Hostile(3f, 0f);
            var b = Hostile(-3f, 0f);
            Assert.That(HostileTargets.Cycle(new[] { a, b }, Origin, null, 1), Is.SameAs(a));
            Assert.That(HostileTargets.Cycle(new[] { a, b }, Origin, a, 1), Is.SameAs(b));
        }
    }
}
