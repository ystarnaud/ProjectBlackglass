using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class DeveloperOverlayTests
    {
        GameObject go;

        [TearDown] public void TearDown() { if (go != null) Object.DestroyImmediate(go); }

        [Test]
        public void ANullOverlay_MeansVisible_SoExistingScenesAreUnchanged() =>
            Assert.That(DeveloperOverlay.Shows(null), Is.True);

        [Test]
        public void ANewOverlay_IsHidden_AndToggleFlipsIt()
        {
            go = new GameObject("overlay");
            var overlay = go.AddComponent<DeveloperOverlay>();
            Assert.That(DeveloperOverlay.Shows(overlay), Is.False);
            overlay.Toggle();
            Assert.That(overlay.IsVisible, Is.True);
            overlay.SetVisible(false);
            Assert.That(DeveloperOverlay.Shows(overlay), Is.False);
        }
    }
}
