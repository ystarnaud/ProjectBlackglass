using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    public class ImpactSurfaceTests
    {
        GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
                Object.DestroyImmediate(root);
        }

        [Test]
        public void ForBox_MapsStructureToConcrete_AndLowCoverAndCratesToMetal()
        {
            Assert.That(ImpactSurfaces.ForBox(MissionBoxKind.Wall), Is.EqualTo(ImpactSurfaceKind.Concrete));
            Assert.That(ImpactSurfaces.ForBox(MissionBoxKind.Baffle), Is.EqualTo(ImpactSurfaceKind.Concrete));
            Assert.That(ImpactSurfaces.ForBox(MissionBoxKind.Pillar), Is.EqualTo(ImpactSurfaceKind.Concrete));
            Assert.That(ImpactSurfaces.ForBox(MissionBoxKind.LowWall), Is.EqualTo(ImpactSurfaceKind.Metal));
            Assert.That(ImpactSurfaces.ForBox(MissionBoxKind.Crate), Is.EqualTo(ImpactSurfaceKind.Metal));
        }

        [Test]
        public void Classify_UsesTheNearestImpactSurface_ThenHealthThenDefault()
        {
            root = new GameObject("Root");
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.SetParent(root.transform);
            wall.AddComponent<ImpactSurface>().Set(ImpactSurfaceKind.Metal);
            Assert.That(ImpactSurfaces.Classify(wall.GetComponent<Collider>()), Is.EqualTo(ImpactSurfaceKind.Metal));

            var child = GameObject.CreatePrimitive(PrimitiveType.Cube);
            child.transform.SetParent(wall.transform);
            Assert.That(ImpactSurfaces.Classify(child.GetComponent<Collider>()), Is.EqualTo(ImpactSurfaceKind.Metal), "a child collider takes its parent's surface");

            var unit = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            unit.transform.SetParent(root.transform);
            unit.AddComponent<Health>();
            Assert.That(ImpactSurfaces.Classify(unit.GetComponent<Collider>()), Is.EqualTo(ImpactSurfaceKind.Character));

            var plain = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plain.transform.SetParent(root.transform);
            Assert.That(ImpactSurfaces.Classify(plain.GetComponent<Collider>()), Is.EqualTo(ImpactSurfaceKind.Default));
            Assert.That(ImpactSurfaces.Classify(null), Is.EqualTo(ImpactSurfaceKind.Default));
        }
    }
}
