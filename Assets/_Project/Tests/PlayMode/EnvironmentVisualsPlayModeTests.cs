using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class EnvironmentVisualsPlayModeTests
    {
        GameObject root;
        EnvironmentTheme theme;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
                Object.DestroyImmediate(root);
            TestTheme.Dispose(theme);
        }

        static MissionLayout Layout(int seed) => MissionGenerator.Generate(new MissionSettings { seed = seed }).Layout;

        [UnityTest]
        public IEnumerator Build_InstantiatesOneModulePerPlacement_UnderAVisualsRoot_WithNoColliders()
        {
            var layout = Layout(12345);
            theme = TestTheme.Create(withColliders: true);
            root = new GameObject("Root");

            var visuals = EnvironmentVisualBuilder.Build(layout, theme, root.transform);
            yield return null;

            Assert.That(visuals.name, Is.EqualTo(EnvironmentVisualBuilder.RootName));
            Assert.That(visuals.parent, Is.EqualTo(root.transform));
            var expected = EnvironmentVisualPlanner.Plan(layout);
            Assert.That(visuals.GetComponentsInChildren<MeshRenderer>(), Has.Length.EqualTo(expected.Count));
            Assert.That(visuals.GetComponentsInChildren<Collider>(true), Is.Empty, "visuals never collide");
        }

        [UnityTest]
        public IEnumerator Build_PlacesAModuleAtItsPlannedPositionAndYaw()
        {
            var layout = Layout(12345);
            theme = TestTheme.Create();
            root = new GameObject("Root");
            var wall = EnvironmentVisualPlanner.Plan(layout).First(p => p.Element == EnvironmentElement.WallEnd);

            var visuals = EnvironmentVisualBuilder.Build(layout, theme, root.transform);
            yield return null;

            var group = visuals.Find(EnvironmentElement.WallEnd.ToString());
            var match = group.Cast<Transform>().First(t => (t.position - wall.Position).sqrMagnitude < 1e-6f
                && Mathf.Approximately(Mathf.DeltaAngle(t.eulerAngles.y, wall.YawDegrees), 0f));
            Assert.That(match, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator AThemeMissingAnElement_GetsAPlaceholderAndOneWarning_AndStillBuilds()
        {
            var layout = Layout(12345);
            theme = TestTheme.Create(true, EnvironmentElement.Crate, EnvironmentElement.Floor);
            root = new GameObject("Root");
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Floor"));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Crate"));

            var visuals = EnvironmentVisualBuilder.Build(layout, theme, root.transform);
            yield return null;

            Assert.That(visuals.GetComponentsInChildren<MeshRenderer>(), Has.Length.EqualTo(EnvironmentVisualPlanner.Plan(layout).Count));
            Assert.That(visuals.GetComponentsInChildren<Collider>(true), Is.Empty);
        }
    }
}
