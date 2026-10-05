using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blackglass.Tests
{
    public class CoverRulesTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
            {
                if (host != null)
                    Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        CoverPoint Point(float x, float z)
        {
            var host = new GameObject($"Cover ({x}, {z})");
            host.transform.position = new Vector3(x, 0f, z);
            hosts.Add(host);
            return host.AddComponent<CoverPoint>();
        }

        [Test]
        public void ResolveHit_ExposedTarget_AlwaysHits()
        {
            Assert.That(CoverRules.ResolveHit(false, 0.5f, 0f), Is.True);
            Assert.That(CoverRules.ResolveHit(false, 0.5f, 0.5f), Is.True);
            Assert.That(CoverRules.ResolveHit(false, 0.5f, 0.999f), Is.True);
            Assert.That(CoverRules.ResolveHit(false, 0f, 0.999f), Is.True, "The chance only matters for a protected target");
        }

        [Test]
        public void ResolveHit_ProtectedTarget_HitsBelowTheChance_AndMissesAtOrAbove()
        {
            Assert.That(CoverRules.ResolveHit(true, 0.5f, 0f), Is.True);
            Assert.That(CoverRules.ResolveHit(true, 0.5f, 0.49f), Is.True);
            Assert.That(CoverRules.ResolveHit(true, 0.5f, 0.5f), Is.False, "A roll equal to the chance misses");
            Assert.That(CoverRules.ResolveHit(true, 0.5f, 0.999f), Is.False);
            Assert.That(CoverRules.ResolveHit(true, 1f, 0.999f), Is.True, "Chance 1 never misses");
            Assert.That(CoverRules.ResolveHit(true, 0f, 0f), Is.False, "Chance 0 never hits");
        }

        [Test]
        public void TryChooseNearest_PicksTheNearestAcceptedPoint()
        {
            var far = Point(0f, 6f);
            var near = Point(0f, 2f);
            var middle = Point(0f, 4f);
            Assert.That(CoverRules.TryChooseNearest(new[] { far, middle, near }, new Vector3(0f, 1f, 0f), 10f, _ => true, out var chosen), Is.True);
            Assert.That(chosen, Is.SameAs(near), "List order must not matter; height must be ignored");
        }

        [Test]
        public void TryChooseNearest_SkipsRejectedAndOutOfRangePoints_FalseWhenNone()
        {
            var near = Point(0f, 2f);
            var middle = Point(0f, 4f);
            var far = Point(0f, 12f);
            Assert.That(CoverRules.TryChooseNearest(new[] { near, middle, far }, Vector3.zero, 10f, p => p != near, out var chosen), Is.True);
            Assert.That(chosen, Is.SameAs(middle), "A rejected nearer point loses to the next one");
            Assert.That(CoverRules.TryChooseNearest(new[] { near, middle, far }, Vector3.zero, 3f, p => p != near, out chosen), Is.False, "Nothing accepted inside the radius");
            Assert.That(chosen, Is.Null);
            Assert.That(CoverRules.TryChooseNearest(new[] { far }, Vector3.zero, 10f, _ => true, out _), Is.False, "12 m is outside 10 m");
            Assert.That(CoverRules.TryChooseNearest(new CoverPoint[0], Vector3.zero, 10f, _ => true, out _), Is.False);
        }

        [Test]
        public void TryChooseNearest_SkipsNullAndDestroyedEntries_WithoutCallingAccept()
        {
            var live = Point(0f, 5f);
            var destroyed = Point(0f, 1f);
            Object.DestroyImmediate(destroyed.gameObject);
            var inactive = Point(0f, 2f);
            inactive.gameObject.SetActive(false);
            var seen = new List<CoverPoint>();
            Assert.That(CoverRules.TryChooseNearest(new[] { null, destroyed, inactive, live }, Vector3.zero, 10f, p => { seen.Add(p); return true; }, out var chosen), Is.True);
            Assert.That(chosen, Is.SameAs(live));
            Assert.That(seen, Is.EqualTo(new[] { live }), "Dead and inactive points must never reach the predicate");
        }

        [Test]
        public void TryChooseNearest_NeverCallsAcceptOnAFartherCandidateThanOneAccepted()
        {
            var near = Point(0f, 2f);
            var far = Point(0f, 6f);
            var seen = new List<CoverPoint>();
            CoverRules.TryChooseNearest(new[] { near, far }, Vector3.zero, 10f, p => { seen.Add(p); return true; }, out _);
            Assert.That(seen, Is.EqualTo(new[] { near }), "The dear predicate must not run on points that cannot win");
        }

        [Test]
        public void TryChooseNearest_RejectsNullArguments()
        {
            Assert.Throws<ArgumentNullException>(() => CoverRules.TryChooseNearest(null, Vector3.zero, 1f, _ => true, out _));
            Assert.Throws<ArgumentNullException>(() => CoverRules.TryChooseNearest(new CoverPoint[0], Vector3.zero, 1f, null, out _));
        }
    }
}
