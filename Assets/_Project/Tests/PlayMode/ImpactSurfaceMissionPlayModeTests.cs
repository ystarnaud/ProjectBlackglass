#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class ImpactSurfaceMissionPlayModeTests
    {
        [UnityTest]
        public IEnumerator GeneratedMission_TagsEveryBoxAndFloorWithItsSurface()
        {
            using (var rig = new MissionRig())
            {
                yield return rig.Generate(7);
                var mission = rig.Director.Current;
                Assert.That(mission, Is.Not.Null, "Precondition: the mission generated");
                var children = mission.Geometry.Cast<Transform>().ToDictionary(t => t.name, t => t, System.StringComparer.Ordinal);
                foreach (var box in mission.Layout.Boxes)
                {
                    Assert.That(children.TryGetValue(box.Name, out var cube), Is.True, box.Name);
                    Assert.That(cube.GetComponent<ImpactSurface>().Kind, Is.EqualTo(ImpactSurfaces.ForBox(box.Kind)), box.Name);
                }
                var floors = children.Where(p => p.Key.StartsWith("Floor_")).ToList();
                Assert.That(floors, Is.Not.Empty);
                foreach (var floor in floors)
                    Assert.That(floor.Value.GetComponent<ImpactSurface>().Kind, Is.EqualTo(ImpactSurfaceKind.Concrete));
            }
        }
    }
}
#endif
