using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ScreenBoxTests
    {
        [Test]
        public void FromCorners_AcceptsCornersInAnyOrder()
        {
            var box = ScreenBox.FromCorners(new Vector2(100f, 20f), new Vector2(10f, 80f));
            Assert.That(box, Is.EqualTo(Rect.MinMaxRect(10f, 20f, 100f, 80f)));
        }

        [Test]
        public void Contains_PointInside()
        {
            var box = ScreenBox.FromCorners(new Vector2(10f, 10f), new Vector2(100f, 100f));
            Assert.That(ScreenBox.Contains(box, new Vector3(50f, 50f, 5f)), Is.True);
        }

        [Test]
        public void Contains_PointOutside()
        {
            var box = ScreenBox.FromCorners(new Vector2(10f, 10f), new Vector2(100f, 100f));
            Assert.That(ScreenBox.Contains(box, new Vector3(150f, 50f, 5f)), Is.False);
            Assert.That(ScreenBox.Contains(box, new Vector3(50f, 5f, 5f)), Is.False);
        }

        [Test]
        public void Contains_EdgesAreInside()
        {
            var box = ScreenBox.FromCorners(new Vector2(10f, 10f), new Vector2(100f, 100f));
            Assert.That(ScreenBox.Contains(box, new Vector3(10f, 10f, 5f)), Is.True);
            Assert.That(ScreenBox.Contains(box, new Vector3(100f, 100f, 5f)), Is.True);
        }

        [Test]
        public void Contains_PointBehindTheCamera_IsOutside()
        {
            var box = ScreenBox.FromCorners(new Vector2(10f, 10f), new Vector2(100f, 100f));
            Assert.That(ScreenBox.Contains(box, new Vector3(50f, 50f, -5f)), Is.False);
        }

        [Test]
        public void ToGuiRect_FlipsTheVerticalAxis()
        {
            var screen = Rect.MinMaxRect(10f, 20f, 110f, 70f);
            var gui = ScreenBox.ToGuiRect(screen, 480f);
            Assert.That(gui, Is.EqualTo(new Rect(10f, 410f, 100f, 50f)));
        }
    }
}
