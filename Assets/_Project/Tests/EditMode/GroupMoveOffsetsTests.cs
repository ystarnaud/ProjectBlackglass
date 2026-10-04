using System;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class GroupMoveOffsetsTests
    {
        const float Spacing = 1.5f;
        const float Tolerance = 1e-4f;

        [Test]
        public void ZeroUnits_GetNoOffsets()
        {
            Assert.That(GroupMoveOffsets.Compute(0, Spacing), Is.Empty);
        }

        [Test]
        public void OneUnit_GoesToTheClickedPoint()
        {
            Assert.That(GroupMoveOffsets.Compute(1, Spacing), Is.EqualTo(new[] { Vector3.zero }));
        }

        [Test]
        public void ThreeUnits_CentreThenTwoNeighboursAtSpacing()
        {
            var offsets = GroupMoveOffsets.Compute(3, Spacing);
            Assert.That(offsets[0], Is.EqualTo(Vector3.zero));
            Assert.That(offsets[1].magnitude, Is.EqualTo(Spacing).Within(Tolerance));
            Assert.That(offsets[2].magnitude, Is.EqualTo(Spacing).Within(Tolerance));
            Assert.That(Vector3.Distance(offsets[1], offsets[2]), Is.EqualTo(Spacing).Within(Tolerance));
        }

        [Test]
        public void SevenUnits_FillTheFirstRing()
        {
            var offsets = GroupMoveOffsets.Compute(7, Spacing);
            for (var i = 1; i < 7; i++)
                Assert.That(offsets[i].magnitude, Is.EqualTo(Spacing).Within(Tolerance), $"offset {i}");
        }

        [Test]
        public void EighthUnit_StartsTheSecondRing()
        {
            var offsets = GroupMoveOffsets.Compute(8, Spacing);
            Assert.That(offsets[7].magnitude, Is.EqualTo(2f * Spacing).Within(Tolerance));
        }

        [Test]
        public void Offsets_StayOnTheGroundPlane()
        {
            foreach (var offset in GroupMoveOffsets.Compute(19, Spacing))
                Assert.That(offset.y, Is.EqualTo(0f));
        }

        [TestCase(19)]
        [TestCase(37)]
        public void NoTwoOffsetsAreCloserThanTheSpacing(int count)
        {
            var offsets = GroupMoveOffsets.Compute(count, Spacing);
            Assert.That(offsets, Has.Length.EqualTo(count));
            for (var i = 0; i < count; i++)
            for (var j = i + 1; j < count; j++)
                Assert.That(Vector3.Distance(offsets[i], offsets[j]), Is.GreaterThanOrEqualTo(Spacing - Tolerance), $"offsets {i} and {j}");
        }

        [Test]
        public void NegativeCount_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => GroupMoveOffsets.Compute(-1, Spacing));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        public void NonPositiveSpacing_Throws(float spacing)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => GroupMoveOffsets.Compute(3, spacing));
        }
    }
}
