using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
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
            DestroyMission();
            world.Dispose();
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

        [UnityTest]
        public IEnumerator Build_RemovesPhysicsAndNavigationComponents_FromNestedAndInactiveChildren()
        {
            var layout = Layout(12345);
            theme = TestTheme.Create(withColliders: true);
            var source = theme.Resolve(EnvironmentElement.Crate, Vector2Int.zero, 1);
            var nested = new GameObject("Nested");
            nested.transform.SetParent(source.transform, false);
            nested.AddComponent<BoxCollider>();
            nested.AddComponent<NavMeshObstacle>();
            nested.AddComponent<Rigidbody>();
            nested.AddComponent<NavMeshModifier>();
            nested.AddComponent<CoverSurface>();
            var inactive = new GameObject("Inactive");
            inactive.transform.SetParent(nested.transform, false);
            inactive.AddComponent<BoxCollider>();
            inactive.AddComponent<NavMeshObstacle>();
            inactive.SetActive(false);
            root = new GameObject("Root");

            var visuals = EnvironmentVisualBuilder.Build(layout, theme, root.transform);
            yield return null;

            Assert.That(visuals.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(visuals.GetComponentsInChildren<NavMeshObstacle>(true), Is.Empty);
            Assert.That(visuals.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(visuals.GetComponentsInChildren<NavMeshModifier>(true), Is.Empty);
            Assert.That(visuals.GetComponentsInChildren<CoverSurface>(true), Is.Empty);
        }

        TestWorld world;
        GeneratedMission mission;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        void DestroyMission()
        {
            if (mission == null || mission.Root == null)
                return;
            var data = mission.Surface != null ? mission.Surface.navMeshData : null;
            Object.DestroyImmediate(mission.Root);
            if (data != null)
                Object.DestroyImmediate(data);
            mission = null;
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

        System.Collections.Generic.List<string> CoverSignature(GeneratedMission built)
        {
            var registry = world.CreateRegistry();
            var discovery = world.Track(new GameObject("Discovery")).AddComponent<CoverDiscovery>();
            discovery.Initialize(registry, discoverAtStart: false);
            discovery.Discover(built.Geometry, MissionNavigation.ReachableFrom(built.Layout.TileCenter(built.Layout.FriendlySpawns[0])));
            return registry.Points.Select(p => $"{p.Placement}/{p.Height}/{p.Position.x:F2},{p.Position.z:F2}").OrderBy(s => s).ToList();
        }

        [UnityTest]
        public IEnumerator WithATheme_NavMeshAndCoverAreIdenticalToWithout_EvenWhenPrefabsCarryColliders([ValueSource(nameof(Seeds))] int seed)
        {
            var layout = Layout(seed);
            mission = MissionBuilder.Build(layout, null, null);
            yield return null;
            var plainArea = NavArea();
            var plainCover = CoverSignature(mission);
            DestroyMission();

            theme = TestTheme.Create(withColliders: true);
            mission = MissionBuilder.Build(layout, null, null, null, theme);
            yield return null;

            Assert.That(mission.Visuals, Is.Not.Null);
            Assert.That(mission.Visuals.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(NavArea(), Is.EqualTo(plainArea).Within(0.01f), $"seed {seed}: NavMesh area");
            Assert.That(CoverSignature(mission), Is.EqualTo(plainCover), $"seed {seed}: cover");
        }

        static readonly int[] Seeds = { 12345, 7, 31 };

        [UnityTest]
        public IEnumerator WithATheme_TheGameplayCubesStayAsCollidersButAreNotRendered()
        {
            var layout = Layout(12345);
            theme = TestTheme.Create();
            mission = MissionBuilder.Build(layout, null, null, null, theme);
            yield return null;

            Assert.That(mission.Geometry.childCount, Is.EqualTo(layout.FloorRects.Count + layout.Boxes.Count));
            foreach (Transform child in mission.Geometry)
            {
                Assert.That(child.GetComponent<BoxCollider>(), Is.Not.Null, child.name);
                Assert.That(child.GetComponent<MeshRenderer>().enabled, Is.False, child.name);
            }
            Assert.That(mission.Geometry.GetComponentsInChildren<CoverSurface>().Length, Is.EqualTo(layout.Boxes.Count));
        }

        [UnityTest]
        public IEnumerator WithoutATheme_NothingChanges_NoVisualsRoot_CubesStayVisible()
        {
            mission = MissionBuilder.Build(Layout(12345), null, null);
            yield return null;

            Assert.That(mission.Visuals == null, Is.True);
            Assert.That(mission.Root.transform.Find(EnvironmentVisualBuilder.RootName) == null, Is.True);
            Assert.That(mission.Geometry.GetComponentsInChildren<MeshRenderer>().All(r => r.enabled), Is.True);
            mission.SetVisualsVisible(false);   // harmless without visuals
            Assert.That(mission.Geometry.GetComponentsInChildren<MeshRenderer>().All(r => r.enabled), Is.True);
        }

        [UnityTest]
        public IEnumerator HidingTheVisuals_ShowsTheGameplayCubes_AndShowingThemReverses()
        {
            theme = TestTheme.Create();
            mission = MissionBuilder.Build(Layout(12345), null, null, null, theme);
            yield return null;

            mission.SetVisualsVisible(false);
            Assert.That(mission.Visuals.gameObject.activeSelf, Is.False);
            Assert.That(mission.Geometry.GetComponentsInChildren<MeshRenderer>().All(r => r.enabled), Is.True);

            mission.SetVisualsVisible(true);
            Assert.That(mission.Visuals.gameObject.activeSelf, Is.True);
            Assert.That(mission.Geometry.GetComponentsInChildren<MeshRenderer>().All(r => !r.enabled), Is.True);
        }

        [UnityTest]
        public IEnumerator DestroyingTheRoot_RemovesEveryVisual()
        {
            theme = TestTheme.Create();
            mission = MissionBuilder.Build(Layout(12345), null, null, null, theme);
            yield return null;
            var visuals = mission.Visuals.gameObject;

            DestroyMission();
            yield return null;

            Assert.That(visuals == null, Is.True);
            Assert.That(GameObject.Find(GeneratedMission.RootName) == null, Is.True);
        }

        static MissionSettings Settings() => new MissionSettings();

        MissionInteractable Terminal(EnvironmentTheme withTheme, out MissionLayout layout)
        {
            MissionLayout built = null;
            ObjectivePlan plan = null;
            for (var seed = 1; seed < 40 && plan == null; seed++)
            {
                built = Layout(seed);
                ObjectivePlacer.TryPlace(built, Settings(), out plan, out _);
            }
            layout = built;
            var holder = new GameObject("Geometry");
            holder.transform.SetParent(world.Track(new GameObject("TerminalRoot")).transform, false);
            return MissionContent.AddTerminal(holder.transform, plan, built, Settings(), null, withTheme);
        }

        [Test]
        public void TheTerminal_KeepsItsBoxColliderAndNavModifier_OnAnUnscaledRoot_WithAVisualRoot()
        {
            theme = TestTheme.Create();
            var terminal = Terminal(theme, out _);

            Assert.That(terminal.transform.localScale, Is.EqualTo(Vector3.one));
            var box = terminal.GetComponent<BoxCollider>();
            Assert.That(box.size, Is.EqualTo(new Vector3(0.8f, 1.2f, 0.8f)));
            Assert.That(terminal.GetComponent<NavMeshModifier>().area, Is.EqualTo(1));
            var visualRoot = terminal.transform.Find(MissionContent.VisualRootName);
            Assert.That(visualRoot, Is.Not.Null);
            Assert.That(visualRoot.localPosition, Is.EqualTo(new Vector3(0f, -0.6f, 0f)));
            Assert.That(visualRoot.childCount, Is.EqualTo(1));
            Assert.That(visualRoot.GetComponentsInChildren<Collider>(true), Is.Empty, "the visual never collides");
            Assert.That(terminal.GetComponent<MeshRenderer>() == null, Is.True);
        }

        [Test]
        public void WithoutATheme_TheTerminalShowsAPlaceholderCube_ThatStillTintsWithItsState()
        {
            var terminal = Terminal(null, out _);
            var visualRoot = terminal.transform.Find(MissionContent.VisualRootName);

            Assert.That(visualRoot.GetComponentInChildren<MeshRenderer>(), Is.Not.Null);
            Assert.That(visualRoot.GetComponentsInChildren<Collider>(true), Is.Empty);
            var block = new MaterialPropertyBlock();
            visualRoot.GetComponentInChildren<MeshRenderer>().GetPropertyBlock(block);
            Assert.That(block.isEmpty, Is.False);
        }

        [Test]
        public void TheMarker_TintsOnlyDisplayRenderers_WhenTheVisualHasThem_AndEverythingOtherwise()
        {
            var host = world.Track(new GameObject("Host"));
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.transform.SetParent(host.transform, false);
            var display = GameObject.CreatePrimitive(PrimitiveType.Cube);
            display.name = "Display";
            display.transform.SetParent(host.transform, false);
            var marker = host.AddComponent<ObjectiveMarker>();

            marker.Bind(() => Color.red, new[] { display.GetComponent<Renderer>() });

            var block = new MaterialPropertyBlock();
            display.GetComponent<Renderer>().GetPropertyBlock(block);
            Assert.That(block.GetColor("_BaseColor").r, Is.EqualTo(1f).Within(0.01f));
            body.GetComponent<Renderer>().GetPropertyBlock(block);
            Assert.That(block.isEmpty, Is.True, "the body is not tinted");
        }

        [UnityTest]
        public IEnumerator TheDirector_BuildsWithTheTheme_AndRegeneratingLeavesNothingBehind()
        {
            theme = TestTheme.Create();
            using (var rig = new MissionRig(null, false, theme))
            {
                yield return rig.Generate(12345);
                Assert.That(rig.Director.State, Is.EqualTo(MissionState.Ready));
                Assert.That(rig.Director.Current.Visuals, Is.Not.Null);
                var firstRoot = rig.Director.Current.Root;
                var baselineRenderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;

                for (var i = 0; i < 3; i++)
                {
                    rig.Director.RegenerateSame();
                    yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);
                    yield return null;
                }

                Assert.That(firstRoot == null, Is.True, "old root destroyed");
                Assert.That(Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                    Is.EqualTo(baselineRenderers), "no stale renderers after repeated regeneration");
                Assert.That(Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.Zero);
                Assert.That(Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Count(g => g.name == GeneratedMission.RootName), Is.EqualTo(1));
            }
        }

        [UnityTest]
        public IEnumerator TheDebugToggle_SurvivesRegeneration()
        {
            theme = TestTheme.Create();
            using (var rig = new MissionRig(null, false, theme))
            {
                yield return rig.Generate(12345);
                rig.Director.ToggleVisuals();
                Assert.That(rig.Director.VisualsVisible, Is.False);
                Assert.That(rig.Director.Current.Visuals.gameObject.activeSelf, Is.False);

                rig.Director.RegenerateSame();
                yield return TestWorld.WaitUntil(() => rig.Director.State == MissionState.Ready, 20f);

                Assert.That(rig.Director.Current.Visuals.gameObject.activeSelf, Is.False, "the new mission starts in the debug view");
                rig.Director.ToggleVisuals();
                Assert.That(rig.Director.Current.Visuals.gameObject.activeSelf, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator ThemedMissions_KeepTheirLayoutHash_AndTheSameObjectiveTerminalTile()
        {
            theme = TestTheme.Create();
            ulong plainHash;
            Vector3 plainTerminal;
            using (var rig = new MissionRig())
            {
                yield return rig.Generate(31);
                plainHash = rig.Director.Report.LayoutHash;
                plainTerminal = rig.Director.Current.Terminal.Position;
            }
            using (var rig = new MissionRig(null, false, theme))
            {
                yield return rig.Generate(31);
                Assert.That(rig.Director.Report.LayoutHash, Is.EqualTo(plainHash));
                Assert.That(rig.Director.Current.Terminal.Position, Is.EqualTo(plainTerminal));
            }
        }
    }
}
