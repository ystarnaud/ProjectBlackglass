#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class EnvironmentKitTests
    {
        const string ThemePath = "Assets/_Project/Environment/Themes/CorporatePrototype/CorporatePrototype.asset";

        static EnvironmentTheme Theme() => AssetDatabase.LoadAssetAtPath<EnvironmentTheme>(ThemePath);

        [Test]
        public void TheTheme_HasAPrefabForEveryElement_AndTwoVariantsOfLowCover()
        {
            var theme = Theme();
            Assert.That(theme, Is.Not.Null);
            foreach (EnvironmentElement element in System.Enum.GetValues(typeof(EnvironmentElement)))
                Assert.That(theme.Has(element), Is.True, element.ToString());
            var variants = Enumerable.Range(0, 200).Select(i => theme.Resolve(EnvironmentElement.LowCover, new Vector2Int(i, 0), 1)).Distinct().Count();
            Assert.That(variants, Is.EqualTo(2));
        }

        [Test]
        public void EveryPrefab_HasNoCollider_AndTheMaterialsAreShared()
        {
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Project/Environment/Prefabs" });
            Assert.That(guids.Length, Is.InRange(10, 20));
            var materials = new System.Collections.Generic.HashSet<Material>();
            foreach (var guid in guids)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                Assert.That(prefab.GetComponentsInChildren<Collider>(true), Is.Empty, prefab.name);
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.sharedMaterials)
                        materials.Add(material);
            }
            Assert.That(materials.Count, Is.LessThanOrEqualTo(8), "a modest palette");
            Assert.That(materials.All(m => AssetDatabase.GetAssetPath(m).StartsWith("Assets/_Project/Environment/Materials")), Is.True);
        }

        [Test]
        public void ModuleBounds_MatchTheGridConvention()
        {
            Bounds BoundsOf(string name)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/_Project/Environment/Prefabs/{name}.prefab");
                var renderers = prefab.GetComponentsInChildren<Renderer>();
                var b = renderers[0].bounds;
                foreach (var r in renderers.Skip(1))
                    b.Encapsulate(r.bounds);
                return b;
            }

            foreach (var wall in new[] { "WallStraight", "WallCorner", "WallEnd", "WallJunction", "Pillar" })
            {
                var b = BoundsOf(wall);
                Assert.That(b.min.y, Is.EqualTo(0f).Within(0.01f), wall + " base on the ground");
                Assert.That(b.max.y, Is.EqualTo(3f).Within(0.02f), wall + " is 3 m tall");
                Assert.That(b.size.x, Is.InRange(0.99f, 1.1f), wall);
                Assert.That(b.size.z, Is.InRange(0.99f, 1.1f), wall);
            }
            var floor = BoundsOf("Floor");
            Assert.That(floor.max.y, Is.EqualTo(0f).Within(0.01f), "floor top at y = 0");
            Assert.That(floor.size.x, Is.EqualTo(1f).Within(0.01f));
            Assert.That(BoundsOf("LowCover_A").max.y, Is.EqualTo(1f).Within(0.02f));
            Assert.That(BoundsOf("LowCoverLong").size.x, Is.EqualTo(2f).Within(0.02f));
            Assert.That(BoundsOf("Terminal").size.y, Is.EqualTo(1.2f).Within(0.05f));
        }

        static float NavArea()
        {
            var t = NavMesh.CalculateTriangulation();
            var area = 0f;
            for (var i = 0; i < t.indices.Length; i += 3)
                area += Vector3.Cross(t.vertices[t.indices[i + 1]] - t.vertices[t.indices[i]],
                    t.vertices[t.indices[i + 2]] - t.vertices[t.indices[i]]).magnitude * 0.5f;
            return area;
        }

        [UnityTest]
        public IEnumerator ARealThemedMission_HasTheSameLayoutCoverAndHostilesAsTheCubes()
        {
            Assert.That(Theme(), Is.Not.Null);
            var rig = new MissionRig();
            yield return rig.Generate(12345);
            var plainHash = rig.Director.Report.LayoutHash;
            var plainCover = rig.Registry.Points.Count;
            var plainHostiles = rig.Director.Hostiles.Count;
            var plainArea = NavArea();
            rig.Dispose();

            var themed = new MissionRig(null, false, Theme());
            try
            {
                yield return themed.Generate(12345);
                Assert.That(themed.Director.State, Is.EqualTo(MissionState.Ready));
                Assert.That(themed.Director.Current.Visuals, Is.Not.Null);
                Assert.That(themed.Director.Current.Visuals.GetComponentsInChildren<Renderer>().Length, Is.GreaterThan(0));
                Assert.That(NavArea(), Is.EqualTo(plainArea).Within(0.01f), "NavMesh area");
                Assert.That(themed.Director.Report.LayoutHash, Is.EqualTo(plainHash));
                Assert.That(themed.Registry.Points.Count, Is.EqualTo(plainCover));
                Assert.That(themed.Director.Hostiles.Count, Is.EqualTo(plainHostiles));
            }
            finally
            {
                themed.Dispose();
            }
        }
    }
}
#endif
