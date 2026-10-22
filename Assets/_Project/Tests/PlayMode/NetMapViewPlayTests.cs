#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    /// <summary>
    /// The client draws exactly the server's geometry (decision 052): every floor, wall, obstacle and crate cube the client builds
    /// sits where the server's real cube or crate collider is, in the same process; the client builds no collider, NavMesh or
    /// cover; a refused map draws nothing.
    /// </summary>
    public class NetMapViewPlayTests
    {
        NetLoopbackRig rig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            rig = new NetLoopbackRig();
            yield return rig.Start();
            rig.BeginNet(quiet: true);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            var root = GameObject.Find(NetMapView.RootName);
            if (root != null)
                Object.Destroy(root);
            yield return rig.Stop();
        }

        // MissionBuilder adds the floor cubes, then one cube per box, as Geometry's first children, in layout order (content such as
        // the crates comes after them); the crates are read from the mission's own list.
        static List<(string Name, Vector3 Position, Vector3 Size)> ServerShapes(GeneratedMission mission)
        {
            var result = new List<(string, Vector3, Vector3)>();
            var layout = mission.Layout;
            var cubes = layout.FloorRects.Count + layout.Boxes.Count;
            for (var i = 0; i < cubes; i++)
            {
                var cube = mission.Geometry.GetChild(i);
                result.Add((cube.name, cube.position, cube.localScale));
            }
            // The crates travel in row order (checkpoint 1 review M10), not the mission's list order: compare them sorted.
            foreach (var container in mission.LootContainers.OrderBy(c => c.transform.position.z).ThenBy(c => c.transform.position.x))
            {
                var collider = container.GetComponent<BoxCollider>();
                result.Add((container.name, container.transform.position, collider.size));
            }
            return result;
        }

        [UnityTest]
        public IEnumerator Every_client_cube_sits_where_the_servers_geometry_is([Values(false, true)] bool themed)
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var view = NetPlayKit.AddMapView(rig.Client.gameObject, rig.Client, themed);
            yield return TestWorld.WaitUntil(() => view.Built, 10f);
            var server = ServerShapes(rig.Director.Current);
            Assert.That(view.Cubes.Count, Is.EqualTo(server.Count));
            for (var i = 0; i < server.Count; i++)
            {
                Assert.That(Vector3.Distance(view.Cubes[i].position, server[i].Position), Is.LessThan(1e-4f), server[i].Name + " position");
                Assert.That(Vector3.Distance(view.Cubes[i].localScale, server[i].Size), Is.LessThan(1e-4f), server[i].Name + " size");
            }
            Assert.That(view.Layout.WorldBounds, Is.EqualTo(rig.Director.Current.Layout.WorldBounds));
            Assert.That(Vector3.Distance(view.Extraction.position, rig.Director.Current.ExtractionZone.position), Is.LessThan(1e-4f));
        }

        [UnityTest]
        public IEnumerator The_client_map_has_no_collider_navmesh_or_cover()
        {
            yield return rig.Connect(NetLoopbackRig.TokenA);
            var view = NetPlayKit.AddMapView(rig.Client.gameObject, rig.Client, true);
            yield return TestWorld.WaitUntil(() => view.Built, 10f);
            var root = GameObject.Find(NetMapView.RootName);
            Assert.That(root.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(root.GetComponentsInChildren<Unity.AI.Navigation.NavMeshSurface>(true), Is.Empty);
            Assert.That(root.GetComponentsInChildren<CoverSurface>(true), Is.Empty);
        }

        [UnityTest]
        public IEnumerator An_invalid_map_draws_nothing()
        {
            var host = new GameObject("MapViewOnly");
            var view = NetPlayKit.AddMapView(host, null, false);
            var bad = new NetMap(10, 10, new List<RectInt> { new RectInt(0, 0, 4, 4), new RectInt(2, 2, 4, 4) }, new List<NetMapBox>(), new List<Vector2Int>());
            Assert.That(view.Build(bad), Is.False);
            Assert.That(view.Built, Is.False);
            Assert.That(GameObject.Find(NetMapView.RootName), Is.Null);
            Object.Destroy(host);
            yield return null;
        }
    }
}
#endif
