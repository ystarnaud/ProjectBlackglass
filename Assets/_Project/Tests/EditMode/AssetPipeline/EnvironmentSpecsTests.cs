#if UNITY_EDITOR
using Blackglass.AssetPipeline;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests.AssetPipeline
{
    public class EnvironmentSpecsTests
    {
        [Test]
        public void PerfectWallPassesWithNoWarnings()
        {
            var w = EnvironmentSpecs.Check(EnvironmentElement.WallStraight, new Vector3(1f, 3f, 1f), new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 3f, 0.5f));
            Assert.IsEmpty(w);
        }

        [Test]
        public void WrongHeightIsReportedWithTheNumbers()
        {
            var w = EnvironmentSpecs.Check(EnvironmentElement.WallStraight, new Vector3(1f, 2.4f, 1f), new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 2.4f, 0.5f));
            Assert.AreEqual(1, w.Count);
            StringAssert.Contains("Y", w[0]);
            StringAssert.Contains("2.4", w[0]);
            StringAssert.Contains("3", w[0]);
        }

        [Test]
        public void SmallOverhangIsAllowedButLargeIsNot()
        {
            Assert.IsEmpty(EnvironmentSpecs.Check(EnvironmentElement.WallStraight, new Vector3(1.1f, 3f, 1.1f), new Vector3(-0.55f, 0f, -0.55f), new Vector3(0.55f, 3f, 0.55f)));
            Assert.IsNotEmpty(EnvironmentSpecs.Check(EnvironmentElement.WallStraight, new Vector3(1.4f, 3f, 1f), new Vector3(-0.7f, 0f, -0.5f), new Vector3(0.7f, 3f, 0.5f)));
        }

        [Test]
        public void PivotChecksDifferForFloorAndWalls()
        {
            // A wall whose pivot is at its centre (hovering): lowest point -1.5 instead of 0.
            var hovering = EnvironmentSpecs.Check(EnvironmentElement.WallStraight, new Vector3(1f, 3f, 1f), new Vector3(-0.5f, -1.5f, -0.5f), new Vector3(0.5f, 1.5f, 0.5f));
            Assert.IsTrue(hovering.Exists(m => m.Contains("pivot")));
            // A floor has its pivot at the top face.
            Assert.IsEmpty(EnvironmentSpecs.Check(EnvironmentElement.Floor, new Vector3(1f, 0.2f, 1f), new Vector3(-0.5f, -0.2f, -0.5f), new Vector3(0.5f, 0f, 0.5f)));
        }

        [Test]
        public void CrateOnlyHasAnUpperLimit()
        {
            Assert.IsEmpty(EnvironmentSpecs.Check(EnvironmentElement.Crate, new Vector3(0.6f, 0.5f, 0.6f), new Vector3(-0.3f, 0f, -0.3f), new Vector3(0.3f, 0.5f, 0.3f)));
            Assert.IsNotEmpty(EnvironmentSpecs.Check(EnvironmentElement.Crate, new Vector3(1.6f, 1f, 1f), new Vector3(-0.8f, 0f, -0.5f), new Vector3(0.8f, 1f, 0.5f)));
        }

        [Test]
        public void EveryElementHasASpecOrIsDeliberatelyUnchecked()
        {
            foreach (EnvironmentElement e in System.Enum.GetValues(typeof(EnvironmentElement)))
                if (e != EnvironmentElement.LightFixture)
                    Assert.IsNotNull(EnvironmentSpecs.For(e), e.ToString());
            Assert.IsNull(EnvironmentSpecs.For(EnvironmentElement.LightFixture));
        }
    }
}
#endif
