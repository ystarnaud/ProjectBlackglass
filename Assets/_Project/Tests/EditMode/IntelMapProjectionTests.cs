using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class IntelMapProjectionTests
    {
        // World bounds x 0..10, z 0..5 into a 106x56 panel with 6 px padding: scale 8.8, content 88x44, origin (9, 6).
        static readonly Rect Panel = new Rect(0f, 0f, 106f, 56f);
        static readonly Rect Bounds = Rect.MinMaxRect(0f, 0f, 10f, 5f);

        [Test]
        public void ToPanel_PutsTheWorldCornersOnTheContentCorners_WithZPointingUp()
        {
            var bottomLeft = IntelMapProjection.ToPanel(Panel, Bounds, new Vector3(0f, 0f, 0f));
            var topRight = IntelMapProjection.ToPanel(Panel, Bounds, new Vector3(10f, 0f, 5f));
            Assert.That(bottomLeft.x, Is.EqualTo(9f).Within(1e-3f));
            Assert.That(bottomLeft.y, Is.EqualTo(50f).Within(1e-3f), "low z is low on screen (large y)");
            Assert.That(topRight.x, Is.EqualTo(97f).Within(1e-3f));
            Assert.That(topRight.y, Is.EqualTo(6f).Within(1e-3f));
        }

        [Test]
        public void ToPanel_ForARect_KeepsItsSizeInScale_AndItsTopLeftCorner()
        {
            var rect = IntelMapProjection.ToPanel(Panel, Bounds, Rect.MinMaxRect(2f, 1f, 4f, 3f));
            Assert.That(rect.width, Is.EqualTo(17.6f).Within(1e-3f));
            Assert.That(rect.height, Is.EqualTo(17.6f).Within(1e-3f));
            Assert.That(rect.x, Is.EqualTo(9f + 2f * 8.8f).Within(1e-3f));
            Assert.That(rect.y, Is.EqualTo(6f + (5f - 3f) * 8.8f).Within(1e-3f));
        }

        [Test]
        public void ToPanel_TheCentreMapsToThePanelCentre()
        {
            var centre = IntelMapProjection.ToPanel(Panel, Bounds, new Vector3(5f, 0f, 2.5f));
            Assert.That(centre.x, Is.EqualTo(53f).Within(1e-3f));
            Assert.That(centre.y, Is.EqualTo(28f).Within(1e-3f));
        }

        [Test]
        public void ADegenerateBounds_DoesNotDivideByZero()
        {
            var point = IntelMapProjection.ToPanel(Panel, Rect.MinMaxRect(1f, 1f, 1f, 1f), new Vector3(1f, 0f, 1f));
            Assert.That(float.IsNaN(point.x) || float.IsInfinity(point.x), Is.False);
            Assert.That(float.IsNaN(point.y) || float.IsInfinity(point.y), Is.False);
        }
    }
}
