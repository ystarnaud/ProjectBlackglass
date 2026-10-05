using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class FiringPositionFinderTests
    {
        static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        [Test]
        public void Candidates_SixteenPoints_NearRingFirst_ThenFarRing()
        {
            var origin = new Vector3(3f, 1f, -2f);
            var buffer = new Vector3[FiringPositionFinder.CandidateCount];
            var count = FiringPositionFinder.Candidates(origin, buffer);

            Assert.That(count, Is.EqualTo(16));
            for (var i = 0; i < 8; i++)
                Assert.That(Flat(buffer[i], origin), Is.EqualTo(2f).Within(0.001f), $"candidate {i} is on the 2 m ring");
            for (var i = 8; i < 16; i++)
                Assert.That(Flat(buffer[i], origin), Is.EqualTo(4f).Within(0.001f), $"candidate {i} is on the 4 m ring");
            foreach (var candidate in buffer)
                Assert.That(candidate.y, Is.EqualTo(origin.y), "candidates keep the origin's height");
        }

        [Test]
        public void Candidates_StartNorth_AndGoClockwise()
        {
            var buffer = new Vector3[FiringPositionFinder.CandidateCount];
            FiringPositionFinder.Candidates(Vector3.zero, buffer);

            Assert.That(buffer[0], Is.EqualTo(new Vector3(0f, 0f, 2f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 0.001f ? 0 : 1), "first is north");
            Assert.That(buffer[2], Is.EqualTo(new Vector3(2f, 0f, 0f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 0.001f ? 0 : 1), "third is east");
            Assert.That(buffer[4], Is.EqualTo(new Vector3(0f, 0f, -2f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 0.001f ? 0 : 1), "fifth is south");
            Assert.That(buffer[12], Is.EqualTo(new Vector3(0f, 0f, -4f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 0.001f ? 0 : 1), "far ring repeats the order");
        }

        [Test]
        public void Candidates_RejectsATooSmallBuffer()
        {
            Assert.That(() => FiringPositionFinder.Candidates(Vector3.zero, new Vector3[5]), Throws.ArgumentException);
        }

        [Test]
        public void TryChoose_ReturnsTheFirstValidCandidate_InRingOrder_AsTheValidatorsAcceptedPoint()
        {
            var buffer = new Vector3[FiringPositionFinder.CandidateCount];
            var count = FiringPositionFinder.Candidates(Vector3.zero, buffer);

            // Only east-ish points are valid; the 2 m east point (index 2) must beat the 4 m one (index 10). The
            // validator "snaps" by lowering y, and that snapped point is what comes back.
            bool EastOnly(Vector3 c, out Vector3 accepted)
            {
                accepted = c + Vector3.down * 0.5f;
                return c.x > 1.9f;
            }
            var found = FiringPositionFinder.TryChoose(buffer, count, EastOnly, out var chosen);

            Assert.That(found, Is.True);
            Assert.That(chosen, Is.EqualTo(buffer[2] + Vector3.down * 0.5f));
        }

        [Test]
        public void TryChoose_NoValidCandidate_IsFalse()
        {
            var buffer = new Vector3[FiringPositionFinder.CandidateCount];
            var count = FiringPositionFinder.Candidates(Vector3.zero, buffer);
            var calls = 0;
            bool Never(Vector3 c, out Vector3 accepted)
            {
                calls++;
                accepted = c;
                return false;
            }

            var found = FiringPositionFinder.TryChoose(buffer, count, Never, out _);

            Assert.That(found, Is.False);
            Assert.That(calls, Is.EqualTo(16), "every candidate is tried once");
        }
    }
}
