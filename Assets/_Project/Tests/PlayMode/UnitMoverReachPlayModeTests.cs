using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blackglass.Tests
{
    public class UnitMoverReachPlayModeTests
    {
        TestWorld world;

        [SetUp]
        public void SetUp() => world = new TestWorld();

        [TearDown]
        public void TearDown() => world.Dispose();

        [UnityTest]
        public IEnumerator CanReach_OpenGround_True_FarOffTheMesh_False()
        {
            world.CreateEnvironment();
            var mover = world.CreateUnit(Vector3.zero).GetComponent<UnitMover>();
            yield return null;

            Assert.That(mover.CanReach(new Vector3(10f, 0f, 10f)), Is.True);
            Assert.That(mover.CanReach(new Vector3(10f, 1f, 10f)), Is.True, "A unit pivot 1 m up still snaps to the mesh");
            Assert.That(mover.CanReach(new Vector3(100f, 0f, 100f)), Is.False, "Nothing walkable within 2 m");
        }

        [UnityTest]
        public IEnumerator CanReach_APointRingedByLowWalls_IsFalse()
        {
            // Agents cannot climb 1 m walls, so the inside of the ring is a separate NavMesh island.
            world.CreateEnvironment(
                (new Vector3(0f, 0.5f, -8f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(0f, 0.5f, -2f), new Vector3(6f, 1f, 0.5f)),
                (new Vector3(-3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)),
                (new Vector3(3f, 0.5f, -5f), new Vector3(0.5f, 1f, 6f)));
            var mover = world.CreateUnit(new Vector3(0f, 0f, 4f)).GetComponent<UnitMover>();
            yield return null;

            Assert.That(mover.CanReach(new Vector3(0f, 0f, -5f)), Is.False, "The ringed point has no complete path");
            Assert.That(mover.CanReach(new Vector3(0f, 0f, -1f)), Is.True, "Just outside the ring is fine");
        }

        [UnityTest]
        public IEnumerator TrySnap_MovesAPointInsideAWallToItsEdge_AcceptsPivotHeight_AndFailsFarAway()
        {
            world.CreateEnvironment((new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 1f)));
            var mover = world.CreateUnit(new Vector3(0f, 0f, 4f)).GetComponent<UnitMover>();
            yield return null;

            Assert.That(mover.TrySnap(new Vector3(0f, 0f, 0f), out var edge), Is.True);
            Assert.That(TestWorld.HorizontalDistance(edge, Vector3.zero), Is.GreaterThan(0.6f).And.LessThan(2f),
                "The snapped point sits on the eroded NavMesh around the 1 m wall (edge near 1 m, give or take a voxel)");
            Assert.That(mover.TrySnap(new Vector3(5f, 1f, 5f), out var fromPivot), Is.True, "A pivot-height point snaps down to the mesh");
            Assert.That(fromPivot.y, Is.LessThan(0.3f));
            Assert.That(TestWorld.HorizontalDistance(fromPivot, new Vector3(5f, 0f, 5f)), Is.LessThan(0.01f));
            Assert.That(mover.TrySnap(new Vector3(100f, 0f, 0f), out _), Is.False);
        }

        [UnityTest]
        public IEnumerator PivotHeight_IsTheAgentBaseOffset()
        {
            world.CreateEnvironment();
            var mover = world.CreateUnit(Vector3.zero).GetComponent<UnitMover>();
            yield return null;
            Assert.That(mover.PivotHeight, Is.EqualTo(1f));
        }
    }
}
