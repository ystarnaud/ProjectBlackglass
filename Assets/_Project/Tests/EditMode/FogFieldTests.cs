using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// The fog-of-war brightness grid: what the player may see at each point, feathered so the edge of the dark is a smooth
    /// ramp. The feathering must never brighten a cell above what the knowledge allows (that would show hidden ground).
    /// </summary>
    public class FogFieldTests
    {
        // A 20 x 10 m field at 4 cells per metre: the cell centres of a metre are at .125, .375, .625 and .875.
        static FogField NewField() => new FogField(new Rect(0f, 0f, 20f, 10f), 4f);

        // The left half (x 0..10) hidden, the right half known.
        static FogField HalfHidden(float feather = 1.5f, float hiddenLevel = 0f)
        {
            var field = NewField();
            field.Paint(new Rect(0f, 0f, 10f, 10f), hiddenLevel);
            field.Feather(feather);
            return field;
        }

        [Test]
        public void ACellsStartClear_AndPaintingSetsOnlyTheCellsWhoseCentreIsInsideTheRect()
        {
            var field = NewField();
            Assert.That(field.RawAt(3f, 3f), Is.EqualTo(1f));
            field.Paint(new Rect(2f, 2f, 2f, 2f), 0.25f);
            Assert.That(field.RawAt(2.125f, 2.125f), Is.EqualTo(0.25f), "first cell inside");
            Assert.That(field.RawAt(3.875f, 3.875f), Is.EqualTo(0.25f), "last cell inside");
            Assert.That(field.RawAt(1.875f, 3f), Is.EqualTo(1f), "just outside on the left");
            Assert.That(field.RawAt(4.125f, 3f), Is.EqualTo(1f), "just outside on the right");
        }

        [Test]
        public void PaintingOutsideTheField_IsClipped_AndDoesNotThrow()
        {
            var field = NewField();
            Assert.DoesNotThrow(() => field.Paint(new Rect(-50f, -50f, 200f, 200f), 0f));
            Assert.That(field.RawAt(10f, 5f), Is.EqualTo(0f));
            Assert.DoesNotThrow(() => field.Paint(new Rect(500f, 500f, 5f, 5f), 0.5f));
        }

        [Test]
        public void HiddenGround_StaysExactlyHidden_AfterFeathering()
        {
            var field = HalfHidden();
            foreach (var x in new[] { 0.125f, 3f, 9.875f })
                Assert.That(field.TargetAt(x, 5f), Is.EqualTo(0f), $"x = {x}");
        }

        [Test]
        public void TheDarknessFadesOutWithDistanceFromTheHiddenGround()
        {
            var field = HalfHidden(feather: 1.5f);
            Assert.That(field.TargetAt(10.125f, 5f), Is.EqualTo(0.25f / 1.5f).Within(0.01f), "a quarter metre from the hidden edge");
            Assert.That(field.TargetAt(10.625f, 5f), Is.EqualTo(0.5f).Within(0.01f), "three quarters of a metre: half way");
            Assert.That(field.TargetAt(11.375f, 5f), Is.EqualTo(1f).Within(0.01f), "past the feather width: fully clear");
            Assert.That(field.TargetAt(15f, 5f), Is.EqualTo(1f));
        }

        [Test]
        public void TheRamp_NeverGetsDarkerGoingAwayFromTheHiddenGround()
        {
            var field = HalfHidden();
            var previous = -1f;
            for (var x = 9.875f; x < 20f; x += 0.25f)
            {
                var value = field.TargetAt(x, 5f);
                Assert.That(value, Is.GreaterThanOrEqualTo(previous - 0.0001f), $"x = {x}");
                previous = value;
            }
        }

        [Test]
        public void ANarrowerFeather_GivesASteeperRamp()
        {
            var narrow = HalfHidden(feather: 0.5f);
            var wide = HalfHidden(feather: 3f);
            Assert.That(narrow.TargetAt(10.625f, 5f), Is.EqualTo(1f).Within(0.01f));
            Assert.That(wide.TargetAt(10.625f, 5f), Is.EqualTo(0.25f).Within(0.01f));
        }

        [Test]
        public void NoCell_IsEverBrighterThanTheKnowledgeAllows()
        {
            var field = NewField();
            field.Paint(new Rect(0f, 0f, 7f, 10f), 0f);
            field.Paint(new Rect(7f, 0f, 6f, 4f), 0.45f);
            field.Paint(new Rect(4f, 6f, 3f, 3f), 0.2f);
            field.Feather(2f);
            for (var z = 0.125f; z < 10f; z += 0.25f)
                for (var x = 0.125f; x < 20f; x += 0.25f)
                    Assert.That(field.TargetAt(x, z), Is.LessThanOrEqualTo(field.RawAt(x, z) + 0.0001f), $"({x}, {z})");
        }

        [Test]
        public void ARevealedRegion_NextToADiscoveredOne_RampsUpSmoothly_NotInAStep()
        {
            var field = NewField();
            field.Paint(new Rect(0f, 0f, 10f, 10f), 0.45f);
            field.Feather(1.5f);
            Assert.That(field.TargetAt(9.875f, 5f), Is.EqualTo(0.45f), "the dimmer side keeps its level");
            Assert.That(field.TargetAt(10.125f, 5f), Is.EqualTo(0.45f + 0.25f / 1.5f).Within(0.01f), "the clear side starts at the dim level");
            Assert.That(field.TargetAt(11.5f, 5f), Is.EqualTo(1f).Within(0.01f));
        }

        [Test]
        public void TheDarkness_RunsAcrossAWall_SoItsTopFadesIntoTheBlackInsteadOfStoppingAtAHardEdge()
        {
            var field = NewField();
            field.Paint(new Rect(0f, 0f, 10f, 10f), 0f);
            field.Paint(new Rect(10f, 0f, 1f, 10f), 1f);   // a wall of a known room, 1 m thick
            field.Feather(1.5f);
            Assert.That(field.TargetAt(10.125f, 5f), Is.EqualTo(0.25f / 1.5f).Within(0.01f), "the wall edge beside the hidden room is nearly black");
            Assert.That(field.TargetAt(10.875f, 5f), Is.EqualTo(1f / 1.5f).Within(0.01f), "the far edge of the wall is mostly lit");
            Assert.That(field.TargetAt(11.625f, 5f), Is.EqualTo(1f), "the floor beyond is clear");
        }

        [Test]
        public void TheCurrentBrightness_EasesTowardTheTarget_AndSettles()
        {
            var field = NewField();
            field.Paint(new Rect(0f, 0f, 20f, 10f), 0f);
            field.Feather(1.5f);
            field.SnapToTarget();
            Assert.That(field.CurrentAt(5f, 5f), Is.EqualTo(0f));
            Assert.That(field.IsSettled, Is.True);

            field.Clear();
            field.Feather(1.5f);
            Assert.That(field.IsSettled, Is.False, "the target moved");
            Assert.That(field.Step(0.2f, 0.4f), Is.True);
            Assert.That(field.CurrentAt(5f, 5f), Is.EqualTo(0.5f).Within(0.001f), "half way after half the fade time");
            Assert.That(field.Step(0.3f, 0.4f), Is.True);
            Assert.That(field.CurrentAt(5f, 5f), Is.EqualTo(1f), "never overshoots");
            Assert.That(field.IsSettled, Is.True);
            Assert.That(field.Step(0.1f, 0.4f), Is.False, "nothing left to change");
        }

        [Test]
        public void TheCurrentBrightness_CanFadeBackToDark()
        {
            var field = NewField();
            field.Feather(1.5f);
            field.SnapToTarget();
            field.Paint(new Rect(0f, 0f, 20f, 10f), 0f);
            field.Feather(1.5f);
            field.Step(0.2f, 0.4f);
            Assert.That(field.CurrentAt(5f, 5f), Is.EqualTo(0.5f).Within(0.001f));
        }

        [Test]
        public void CopyTo_WritesTheCurrentBrightness_OneByteACell()
        {
            var field = NewField();
            field.Paint(new Rect(0f, 0f, 20f, 10f), 0.5f);
            field.Feather(1.5f);
            field.SnapToTarget();
            var bytes = new byte[field.Width * field.Height];
            field.CopyTo(bytes);
            Assert.That(bytes[0], Is.EqualTo(128).Within(1));
            Assert.That(bytes[bytes.Length - 1], Is.EqualTo(128).Within(1));
            Assert.That(field.Width, Is.EqualTo(80));
            Assert.That(field.Height, Is.EqualTo(40));
        }

        [Test]
        public void AFeatherOfZero_MeansNoRamp_TheTargetIsTheRawLevels()
        {
            var field = HalfHidden(feather: 0f);
            Assert.That(field.TargetAt(10.125f, 5f), Is.EqualTo(1f));
            Assert.That(field.TargetAt(9.875f, 5f), Is.EqualTo(0f));
        }
    }
}
